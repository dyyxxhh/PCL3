#!/usr/bin/env python3
"""Offline/loopback-only integration regressions for a self-contained MCM release.

All game/Java/account data are independent fixtures. No real account or internet
service is contacted; invalid outbound proxies make accidental HTTP use fail.
"""
import errno
import hashlib
import http.server
import json
import os
from pathlib import Path
import pty
import select
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import zipfile


def run_with_terminal(command, env, replies):
    pid, terminal = pty.fork()
    if pid == 0:
        os.execvpe(command[0], command, env)
    output = bytearray()
    prompt = '请输入离线用户名'.encode()
    sent = 0
    deadline = time.monotonic() + 120
    try:
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([terminal], [], [], remaining)[0]:
                raise AssertionError(('installer timed out', output.decode(errors='replace')))
            try:
                chunk = os.read(terminal, 65536)
            except OSError as exc:
                if exc.errno != errno.EIO:
                    raise
                break  # Linux PTYs signal slave closure with EIO.
            if not chunk:
                break
            output.extend(chunk)
            if sent < len(replies) and output.count(prompt) > sent:
                os.write(terminal, replies[sent])
                sent += 1
        _, status = os.waitpid(pid, 0)
        pid = None
        text = output.decode(errors='replace')
        assert sent == len(replies), text
        return os.waitstatus_to_exitcode(status), text
    finally:
        os.close(terminal)
        if pid is not None:
            os.kill(pid, signal.SIGKILL)
            os.waitpid(pid, 0)


def main():
    executable = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="mcm-offline-smoke-") as temporary:
        root = Path(temporary)
        environment = os.environ.copy()
        for key in ("MCM_GAME_DIR", "PCL_GAME_DIR", "JAVA_HOME"):
            environment.pop(key, None)
        environment.update({
            "HOME": str(root / "home"), "XDG_CONFIG_HOME": str(root / "config"),
            "DOTNET_ROOT": str(root / "no-dotnet-installed"), "PATH": "/usr/bin:/bin",
            "HTTP_PROXY": "http://127.0.0.1:9", "HTTPS_PROXY": "http://127.0.0.1:9",
            "ALL_PROXY": "http://127.0.0.1:9", "NO_PROXY": "127.0.0.1,localhost",
            "http_proxy": "http://127.0.0.1:9", "https_proxy": "http://127.0.0.1:9",
            "all_proxy": "http://127.0.0.1:9", "no_proxy": "127.0.0.1,localhost",
            "MCM_TEST_ARGS": str(root / "java-arguments.txt"),
        })
        Path(environment["HOME"]).mkdir()

        # When permitted, run USB/export/launch commands with no network interfaces at all.
        # The LAN server/bootstrap intentionally stay in the host's loopback namespace.
        network_prefix = []
        if Path("/usr/bin/unshare").exists():
            probe = subprocess.run(["/usr/bin/unshare", "-Urn", "/bin/true"], capture_output=True)
            if probe.returncode == 0:
                network_prefix = ["/usr/bin/unshare", "-Urn"]

        def run(*arguments, expected=0, env=None, exe=executable):
            result = subprocess.run([*network_prefix, str(exe), *map(str, arguments)], env=env or environment,
                                    stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=90)
            assert result.returncode == expected, (arguments, result.returncode, result.stdout, result.stderr)
            return result.stdout + result.stderr

        source = root / "source game"
        instance = source / "instances" / "Portable"

        def write(relative, content):
            path = instance / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
            return hashlib.sha1(content).hexdigest()

        library_hash = write("libraries/example/test.jar", b"mock library")
        client_hash = write("versions/1.21.1/1.21.1.jar", b"mock client")
        asset_hash = hashlib.sha1(b"mock asset").hexdigest()
        write(f"assets/objects/{asset_hash[:2]}/{asset_hash}", b"mock asset")
        index = json.dumps({"objects": {"test": {"hash": asset_hash, "size": 10}}}).encode()
        index_hash = write("assets/indexes/test.json", index)
        metadata = {
            "id": "fabric-profile", "jar": "1.21.1", "mainClass": "fixture.Main",
            "javaVersion": {"majorVersion": 21},
            "assetIndex": {"id": "test", "sha1": index_hash, "url": "https://offline.invalid/index"},
            "downloads": {"client": {"sha1": client_hash}},
            "libraries": [{"name": "example:test:1", "downloads": {"artifact": {
                "path": "example/test.jar", "sha1": library_hash, "url": "https://offline.invalid/library",
            }}}],
        }
        write("versions/fabric-profile/fabric-profile.json", json.dumps(metadata).encode())
        write("mcm-instance.json", json.dumps({
            "Name": "Portable", "GameVersion": "1.21.1", "Loader": "fabric", "LoaderVersion": "fixture",
            "LaunchVersion": "fabric-profile", "Imported": True,
        }).encode())
        write("mods/test.jar", b"mock mod")
        write("config/test.json", b"config bytes")
        write("config/selected-profile.json", b"private fixture; must not be exported")
        write("accounts.json", b"private fixture; must not be exported")
        write("saves/world/private.dat", b"private fixture; must not be exported")
        write("logs/private.log", b"private fixture; must not be exported")
        java_home = root / "portable Java"
        (java_home / "bin").mkdir(parents=True)
        java = java_home / "bin" / "java"
        java.write_text('#!/bin/sh\nif [ "${1-}" = "-version" ]; then\n'
                        '  printf \'openjdk version "21.0.5"\\n\' >&2\n  exit 0\nfi\n'
                        'printf "%s\\n" "$@" > "$MCM_TEST_ARGS"\n', encoding="utf-8")
        java.chmod(0o700)
        (java_home / "LICENSE").write_text("fixture license", encoding="utf-8")
        (java_home / "lib").mkdir()
        (java_home / "lib" / "runtime.so").write_bytes(b"mock Java native library")
        (java_home / "lib" / "alias.so").symlink_to("runtime.so")
        bundle = root / "offline bundle.zip"
        assert "offline export" in run("--help")
        run("offline", "export", bundle, "--instance", "Portable", "--game-dir", source,
            "--java", root / "missing-java", expected=1)
        assert not bundle.exists(), "invalid explicit Java must not silently fall back to a host runtime"
        # Reproduce a launchable instance with no managed Java: JAVA_HOME/default-runtime is a directory link.
        java_alias = root / "default-runtime"
        java_alias.symlink_to(java_home, target_is_directory=True)
        environment["JAVA_HOME"] = str(java_alias)
        diagnostic = run("offline", "doctor", "--instance", "Portable", "--game-dir", source,
                         env=environment | {"DISPLAY": "", "WAYLAND_DISPLAY": ""}, expected=1)
        assert "[OK] Local Java 21" in diagnostic and "no bundled Java" in diagnostic
        assert "[FAIL] No runnable" not in diagnostic
        run("offline", "export", bundle, "--instance", "Portable", "--game-dir", source)
        explicit_bundle = root / "explicit Java bundle.zip"
        run("offline", "export", explicit_bundle, "--instance", "Portable", "--game-dir", source,
            "--java", java_alias / "bin" / "java")
        path_bin = root / "system bin"
        path_bin.mkdir()
        (path_bin / "java").symlink_to(java_alias / "bin" / "java")
        path_environment = environment | {"PATH": str(path_bin)}
        path_environment.pop("JAVA_HOME", None)
        path_bundle = root / "PATH system Java bundle.zip"
        run("offline", "export", path_bundle, "--instance", "Portable", "--game-dir", source,
            env=path_environment)
        with zipfile.ZipFile(path_bundle) as archive:
            assert archive.read("instance/runtime/offline-java/bin/java") == java.read_bytes()
            assert archive.read("instance/runtime/offline-java/lib/runtime.so") == b"mock Java native library"
        run("offline", "inspect", bundle)
        with zipfile.ZipFile(bundle) as archive:
            names = archive.namelist()
            assert "bootstrap/mcm" in names and "instance/runtime/offline-java/bin/java" in names
            assert "instance/runtime/offline-java/lib/runtime.so" in names
            assert not any("selected-profile" in name or "accounts.json" in name or "/saves/" in name or "/logs/" in name for name in names)
            # Simulate extracting only the bootstrap on a completely unprovisioned target.
            usb_bootstrap = root / "USB mcm"
            usb_bootstrap.write_bytes(archive.read("bootstrap/mcm"))
            usb_bootstrap.chmod(0o700)
        # Ctrl+C must reach cooperative cancellation, not leave a hidden partial instance.
        cancelled_root = root / "cancelled target"
        cancelled = subprocess.Popen([*network_prefix, str(usb_bootstrap), "offline", "import", str(bundle),
                                      "--game-dir", str(cancelled_root), "--instance", "Cancelled"],
                                     env=environment, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        deadline = time.monotonic() + 20
        while not list(cancelled_root.glob("instances/.migration-offline-*")):
            if cancelled.poll() is not None or time.monotonic() > deadline:
                stdout, stderr = cancelled.communicate(timeout=10)
                raise AssertionError(("did not observe cancellable import staging", cancelled.returncode, stdout, stderr))
            time.sleep(0.001)
        cancelled.send_signal(signal.SIGINT)
        stdout, stderr = cancelled.communicate(timeout=20)
        assert cancelled.returncode == 130, (cancelled.returncode, stdout, stderr)
        assert not list(cancelled_root.glob("instances/.migration-*"))
        assert not (cancelled_root / "instances" / "Cancelled").exists()
        target = root / "USB target"
        run("offline", "import", bundle, "--game-dir", target, "--instance", "Imported", exe=usb_bootstrap)
        imported = target / "instances" / "Imported"
        assert (imported / "mods" / "test.jar").read_bytes() == b"mock mod"
        assert (imported / "runtime" / "offline-java" / "lib" / "alias.so").read_bytes() == b"mock Java native library"
        assert not (imported / "runtime" / "offline-java" / "lib" / "alias.so").is_symlink()
        assert json.loads((imported / "mcm-instance.json").read_text())["Imported"] is False
        assert not (root / "config" / "mcm").exists(), "import must not create or transfer accounts"
        diagnostic = run("offline", "doctor", "--instance", "Imported", "--game-dir", target,
                         env=environment | {"DISPLAY": "", "WAYLAND_DISPLAY": ""}, expected=1)
        assert "[OK] Bundled Java 21" in diagnostic and "[FAIL] Desktop display" in diagnostic
        run("login", "offline", "OfflineFixture")
        run("launch", "Imported", "--game-dir", target, "--offline", exe=usb_bootstrap)
        assert "OfflineFixture" in Path(environment["MCM_TEST_ARGS"]).read_text()
        run("launch", "missing", "--game-dir", target, "--offline", expected=1)
        run("launch", "Imported", "--game-dir", target, "--offline", "--offline", expected=2)
        profile = root / "config" / "mcm" / "selected-profile.json"
        profile.write_text(json.dumps({"type": "microsoft", "userName": "MockOnly", "uuid": "mock"}), encoding="utf-8")
        message = run("launch", "Imported", "--game-dir", target, "--offline", expected=1)
        assert "never refreshes" in message
        asset = imported / "assets" / "objects" / asset_hash[:2] / asset_hash
        asset.unlink()
        run("launch", "Imported", "--game-dir", target, "--offline", expected=1)
        assert not asset.exists(), "offline launch must not repair/download missing assets"
        run("logout")

        sock = socket.socket()
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
        sock.close()
        server_log_path = root / "server-access.log"
        server_log = server_log_path.open("w", encoding="utf-8")
        server = subprocess.Popen([str(executable), "offline", "serve", str(bundle), "--bind", "127.0.0.1", "--port", str(port)],
                                  env=environment, stdin=subprocess.DEVNULL, stdout=server_log, stderr=subprocess.PIPE, text=True)
        origin = f"http://127.0.0.1:{port}"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        try:
            deadline = time.monotonic() + 45
            while True:
                if server.poll() is not None:
                    raise AssertionError(("server exited", server.communicate()))
                try:
                    with opener.open(origin, timeout=0.5) as response:
                        install_script = response.read().decode()
                        assert "sha256sum" in install_script
                    break
                except (urllib.error.URLError, TimeoutError):
                    if time.monotonic() > deadline:
                        raise AssertionError("offline server did not become ready")
                    time.sleep(0.1)

            def bootstrap_command(route):
                return ["bash", "-c", f"set -o pipefail; curl -fsS --noproxy '*' {origin}{route} | bash"]

            def bootstrap(env, expected=0, route="/direct"):
                result = subprocess.run(bootstrap_command(route), env=env, start_new_session=True,
                                        capture_output=True, text=True, timeout=120)
                assert result.returncode == expected, (result.returncode, result.stdout, result.stderr)
                return result.stdout + result.stderr

            lan_game = root / "LAN game with spaces"
            install_dir = root / "LAN bin with spaces"
            client_environment = environment | {"MCM_GAME_DIR": str(lan_game), "MCM_INSTALL_DIR": str(install_dir), "MCM_INSTANCE": "LanInstalled"}
            first_output = bootstrap(client_environment)
            assert "使用 4 个连接下载" in first_output
            assert "KiB/s | ETA" in first_output and "100%" in first_output
            assert "下载完成，正在验证 SHA-256" in first_output
            lan_instance = lan_game / "instances" / "LanInstalled"
            assert (lan_instance / "mods" / "test.jar").read_bytes() == b"mock mod"
            assert (lan_instance / "runtime" / "offline-java" / "bin" / "java").exists()
            assert (install_dir / "mcm").read_bytes() == executable.read_bytes()
            assert stat.S_IMODE((install_dir / "mcm").stat().st_mode) == 0o700
            assert not profile.exists()
            marker = lan_instance / "keep.txt"
            marker.write_text("keep", encoding="utf-8")
            launch_args = Path(environment["MCM_TEST_ARGS"])
            launch_args.unlink()
            assert not profile.exists()
            bootstrap(client_environment, expected=1, route="/")
            assert marker.read_text() == "keep"
            assert not profile.exists() and not launch_args.exists()

            no_terminal = bootstrap(client_environment | {"MCM_INSTANCE": "NoTerminal"}, expected=1, route="/")
            assert "没有可交互的终端" in no_terminal and "安装完成" in no_terminal
            assert (lan_game / "instances" / "NoTerminal" / "mcm-instance.json").exists()
            assert not profile.exists() and not launch_args.exists()

            interactive_env = client_environment | {"USER": "SystemOnly", "LOGNAME": "SystemOnly"}
            run("login", "offline", "ExistingName", env=interactive_env)
            existing_profile = profile.read_bytes()
            status, output = run_with_terminal(bootstrap_command("/"),
                                               interactive_env | {"MCM_INSTANCE": "CancelledPrompt"}, [b"\x04"])
            assert status == 1 and "未输入用户名，未登录或启动" in output, output
            assert (lan_game / "instances" / "CancelledPrompt" / "mcm-instance.json").exists()
            assert profile.read_bytes() == existing_profile and not launch_args.exists()

            status, output = run_with_terminal(bootstrap_command("/install.sh"),
                                               interactive_env | {"MCM_INSTANCE": "Interactive"},
                                               [b"\n", b"not a name\n", b"Entered_Name\n"])
            assert status == 0 and output.count("用户名无效") == 2, output
            assert output.index("安装完成") < output.index("请输入离线用户名") < output.index("已选择离线账户")
            assert json.loads(profile.read_text())["userName"] == "Entered_Name"
            args = launch_args.read_text().splitlines()
            assert "Entered_Name" in args and "ExistingName" not in args and "SystemOnly" not in args
            assert str(lan_game / "instances" / "Interactive") in args, args
            launch_args.unlink()
            installed_profile = profile.read_bytes()
            second_output = bootstrap(client_environment | {"MCM_INSTANCE": "SecondInstance", "MCM_DOWNLOAD_CONNECTIONS": "1"})
            assert "使用 1 个连接下载" in second_output
            assert (lan_game / "instances" / "SecondInstance" / "mcm-instance.json").exists()
            assert profile.read_bytes() == installed_profile and not launch_args.exists()
            with opener.open(origin + "/direct", timeout=5) as response:
                install_script = response.read().decode()
            # Independent HTTP fixture: range failures must fall back, never install partial data;
            # cancellation must stop every background curl and remove staging files.
            payloads = {"/mcm": executable.read_bytes(), "/bundle.zip": bundle.read_bytes()}
            state = {"slow": False, "active": 0, "peak": 0, "range_requests": 0}
            lock = threading.Lock()

            class DownloadFixture(http.server.BaseHTTPRequestHandler):
                def log_message(self, *_):
                    pass

                def do_GET(self):
                    if self.path not in payloads:
                        self.send_error(404)
                        return
                    data = payloads[self.path]
                    ranged = self.headers.get("Range") is not None
                    with lock:
                        state["active"] += 1
                        state["peak"] = max(state["peak"], state["active"])
                        state["range_requests"] += int(ranged)
                    try:
                        if state["slow"]:
                            # Cancellation needs sustained valid transfers. Returning the whole file
                            # makes curl --max-filesize reject each range before all workers overlap.
                            total = len(data)
                            if ranged:
                                bounds = self.headers["Range"].removeprefix("bytes=").split("-")
                                start, end = map(int, bounds)
                                assert 0 <= start <= end < total
                                data = data[start:end + 1]
                                self.send_response(206)
                                self.send_header("Content-Range", f"bytes {start}-{end}/{total}")
                            else:
                                self.send_response(200)
                            self.send_header("Content-Length", str(len(data)))
                            self.end_headers()
                            for offset in range(0, len(data), 1024):
                                self.wfile.write(data[offset:offset + 1024])
                                self.wfile.flush()
                                time.sleep(0.01)
                        else:
                            # Simulate a middlebox that does not honor Range. All four requests overlap.
                            if ranged:
                                data = data[:1024]
                                time.sleep(0.15)
                            self.send_response(200)
                            self.send_header("Content-Length", str(len(data)))
                            self.end_headers()
                            self.wfile.write(data)
                    except (BrokenPipeError, ConnectionResetError):
                        pass
                    finally:
                        with lock:
                            state["active"] -= 1

            fake = http.server.ThreadingHTTPServer(("127.0.0.1", 0), DownloadFixture)
            fake.daemon_threads = True
            fake_thread = threading.Thread(target=fake.serve_forever, daemon=True)
            fake_thread.start()
            try:
                fake_origin = f"http://127.0.0.1:{fake.server_port}"
                script = install_script.replace("base='" + origin + "'", "base='" + fake_origin + "'")
                assert script != install_script
                fallback_env = client_environment | {"MCM_INSTANCE": "FallbackInstalled"}
                fallback = subprocess.run(["bash"], input=script, env=fallback_env,
                                          capture_output=True, text=True, timeout=120)
                assert fallback.returncode == 0, (fallback.returncode, fallback.stdout, fallback.stderr)
                assert "回退为单连接" in fallback.stderr
                assert (lan_game / "instances" / "FallbackInstalled" / "mcm-instance.json").exists()
                assert state["peak"] >= 2 and state["range_requests"] >= 4, state
                invalid = subprocess.run(["bash"], input=script,
                                         env=fallback_env | {"MCM_DOWNLOAD_CONNECTIONS": "0"},
                                         capture_output=True, text=True, timeout=15)
                assert invalid.returncode != 0 and "必须是 1 到 8" in invalid.stderr
                state["slow"] = True
                cancel_tmp = root / "cancel-download-tmp"
                cancel_tmp.mkdir()
                cancel_game = root / "cancel-download-game"
                cancel_bin = root / "cancel-download-bin"
                cancel_env = client_environment | {"TMPDIR": str(cancel_tmp), "MCM_GAME_DIR": str(cancel_game),
                                                    "MCM_INSTALL_DIR": str(cancel_bin)}
                with (root / "cancel-download.log").open("w") as output:
                    downloading = subprocess.Popen(["bash"], env=cancel_env, stdin=subprocess.PIPE,
                                                   stdout=output, stderr=output, text=True, start_new_session=True)
                    downloading.stdin.write(script)
                    downloading.stdin.close()
                    deadline = time.monotonic() + 15
                    while state["active"] < 4:
                        if downloading.poll() is not None or time.monotonic() > deadline:
                            downloading.kill()
                            downloading.wait()
                            raise AssertionError("did not observe four active cancellable download workers")
                        time.sleep(0.01)
                    downloading.terminate()
                    assert downloading.wait(timeout=15) == 143
                assert not list(cancel_tmp.iterdir()), "cancelled download must clean temporary fragments"
                assert not cancel_game.exists() and not cancel_bin.exists()
            finally:
                fake.shutdown()
                fake.server_close()
                fake_thread.join(timeout=5)
            with bundle.open("ab") as stream:
                stream.write(b"corruption after server startup")
            failed_game = root / "tampered target"
            failed_bin = root / "tampered bin"
            output = bootstrap(client_environment | {"MCM_GAME_DIR": str(failed_game), "MCM_INSTALL_DIR": str(failed_bin)}, expected=1)
            assert "校验失败" in output
            assert not failed_game.exists() and not failed_bin.exists()
        finally:
            server.send_signal(signal.SIGINT)
            try:
                stdout, stderr = server.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                server.kill()
                stdout, stderr = server.communicate()
                raise AssertionError(("server did not stop on Ctrl+C", stdout, stderr))
            server_log.close()
            assert server.returncode == 0, (server.returncode, stdout, stderr)
        access = server_log_path.read_text()
        assert "127.0.0.1 GET /bundle.zip request range=yes" in access
        assert "status=206" in access and "duration_ms=" in access and "bytes=" in access
        assert not list(lan_game.glob("instances/.migration-*"))
    print("MCM offline bundle/Java/bootstrap/LAN/offline-launch safety smoke checks: PASS"
          + (" (USB/export/launch in isolated network namespaces)" if network_prefix else ""))


if __name__ == "__main__":
    main()
