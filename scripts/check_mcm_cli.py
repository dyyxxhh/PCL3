#!/usr/bin/env python3
"""Offline smoke regressions for a built MCM Linux CLI executable."""
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


def main():
    executable = Path(sys.argv[1]).resolve(strict=True)
    assert executable.name == "mcm", executable
    with tempfile.TemporaryDirectory(prefix="mcm-cli-check-") as temporary:
        root = Path(temporary)
        environment = os.environ.copy()
        for name in ("MCM_GAME_DIR", "PCL_GAME_DIR"):
            environment.pop(name, None)
        environment["XDG_CONFIG_HOME"] = str(root / "config")

        def run(*arguments, expected=0, env=None):
            result = subprocess.run(
                [str(executable), *arguments], env=env or environment,
                stdin=subprocess.DEVNULL, capture_output=True, text=True,
                timeout=30,
            )
            assert result.returncode == expected, (arguments, result.returncode, result.stdout, result.stderr)
            return result.stdout

        def instance(game, name, legacy=False):
            directory = game / "instances" / name
            (directory / "mods").mkdir(parents=True)
            metadata = {
                "Name": name, "GameVersion": "1.21.1", "Loader": "fabric",
                "LoaderVersion": "0.16.0", "LaunchVersion": "fabric-profile",
            }
            file_name = "pcl-instance.json" if legacy else "mcm-instance.json"
            (directory / file_name).write_text(json.dumps(metadata), encoding="utf-8")
            return directory

        help_text = run("--help")
        assert "MCM Linux CLI" in help_text
        assert "mcm info" in help_text
        assert "mcm -R <file.jar>" in help_text
        assert "pcl " not in help_text

        info = run("info")
        assert info.startswith("MCM Linux CLI "), info
        assert "https://github.com/dyyxxhh/PCL3" in info
        assert "PCL.Linux.Cli/README.md" in info and "/issues" in info
        assert "Minecraft Java" in info and "Linux x86_64 / glibc" in info
        assert "Apache-2.0" in info
        assert not (root / "config").exists(), "info should not create configuration"
        if len(sys.argv) > 2:
            assert info.splitlines()[0] == f"MCM Linux CLI {sys.argv[2]}", info
        run("info", "unexpected", expected=2)

        game = root / "game with spaces"
        selected = instance(game, "selected")
        untouched = instance(game, "untouched")
        (selected / "mods" / "sample.jar").write_bytes(b"mock mod")
        (selected / "mods" / "keep.jar").write_bytes(b"keep")
        (untouched / "mods" / "sample.jar").write_bytes(b"untouched")
        (selected / "pcl-mods.json").write_text(json.dumps([{
            "FileName": "sample.jar", "Source": 0, "ProjectId": "sample",
            "ProjectName": "Sample", "ReleaseId": "v1", "ReleaseName": "1.0", "Disabled": False,
        }]), encoding="utf-8")
        options = ("--instance", "selected", "--game-dir", str(game))
        run("-R", "sample.jar", "--game-dir", str(game), expected=2)
        assert (selected / "mods" / "sample.jar").exists()
        run("-R", "../outside.jar", *options, expected=2)
        run("mod", "disable", "sample.jar", *options)
        assert (selected / "mods" / "sample.jar.disabled").exists()
        manifest = json.loads((selected / "mcm-mods.json").read_text(encoding="utf-8"))
        assert manifest[0]["Disabled"] and manifest[0]["ProjectId"] == "sample"
        run("mod", "enable", "sample.jar.disabled", *options)
        run("-R", "sample.jar", *options)
        assert not (selected / "mods" / "sample.jar").exists()
        assert (selected / "mods" / "keep.jar").exists()
        assert (untouched / "mods" / "sample.jar").read_bytes() == b"untouched"
        assert json.loads((selected / "mcm-mods.json").read_text(encoding="utf-8")) == []
        run("mod", "disable", "keep.jar", *options)
        run("-R", "keep.jar.disabled", *options)
        assert not (selected / "mods" / "keep.jar.disabled").exists()
        run("-R", "missing.jar", *options, expected=2)

        legacy_game = root / "legacy-game"
        instance(legacy_game, "legacy", legacy=True)
        configured = environment | {"MCM_GAME_DIR": str(game), "PCL_GAME_DIR": str(legacy_game)}
        assert "selected\t" in run("instance", "list", env=configured)
        assert "legacy\t" in run("instance", "list", "--game-dir", str(legacy_game), env=configured)
        configured.pop("MCM_GAME_DIR")
        assert "legacy\t" in run("instance", "list", env=configured)

        run("login", "offline", "SmokeUser")
        profile = root / "config" / "mcm" / "selected-profile.json"
        assert profile.exists()
        assert stat.S_IMODE(profile.stat().st_mode) == 0o600

        imported = instance(game, "imported")
        instance_metadata = imported / "mcm-instance.json"
        metadata = json.loads(instance_metadata.read_text(encoding="utf-8"))
        metadata["Imported"] = True
        instance_metadata.write_text(json.dumps(metadata), encoding="utf-8")
        version_dir = imported / "versions" / "fabric-profile"
        version_dir.mkdir(parents=True)
        (version_dir / "fabric-profile.json").write_text(json.dumps({
            "id": "fabric-profile", "mainClass": "example.Main", "libraries": [],
            "javaVersion": {"majorVersion": 21},
            "arguments": {"jvm": ["-Xmx2G"], "game": []},
        }), encoding="utf-8")
        java = root / "java"
        java.write_text(
            '#!/bin/sh\nif [ "$1" = "-version" ]; then echo \'openjdk version "21.0.1"\' >&2; exit; fi\n'
            'printf "%s\\n" "$@" > launch-arguments.txt\n', encoding="utf-8",
        )
        java.chmod(0o700)
        launch_options = ("--game-dir", str(game), "--java", str(java))
        run("config", "set", "memory.max_mib", "3072", "--instance", "imported")
        for override, expected_heap in [((), 3072), (("--memory", "4096"), 4096)]:
            result = subprocess.run(
                [str(executable), "launch", "imported", *launch_options, *override], env=environment,
                stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30,
            )
            assert result.returncode == 0, (result.stdout, result.stderr)
            assert "Checking imported instance libraries" in result.stderr, result.stderr
            assert "Downloading files" not in result.stderr, result.stderr
            launch_args = (imported / "launch-arguments.txt").read_text(encoding="utf-8").splitlines()
            assert [arg for arg in launch_args if arg.startswith("-Xmx")] == [f"-Xmx{expected_heap}m"]

        run("logout")
        assert not profile.exists()

    print("MCM CLI info/version/naming/mod removal/legacy compatibility smoke checks: PASS")


if __name__ == "__main__":
    main()
