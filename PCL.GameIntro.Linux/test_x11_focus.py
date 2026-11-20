#!/usr/bin/env python3
"""Linux focus/playback regression; uses a synthetic clip and an isolated X server.

Run with Xvfb, xcompmgr, xauth, xdotool, xprop, xmessage and ffmpeg on PATH,
plus the Python/GTK/GStreamer requirements documented for the host.
"""
import contextlib
import os
from pathlib import Path
import secrets
import select
import shutil
import socket
import subprocess
import tempfile
import time


HOST = Path(__file__).resolve().with_name('host.py')


def start(stack, argv, env, **kwargs):
    process = subprocess.Popen(argv, env=env, **kwargs)

    def close():
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=3)

    stack.callback(close)
    return process


def read_line(process, timeout):
    readable, _, _ = select.select([process.stdout], [], [], timeout)
    if not readable:
        raise AssertionError(f'{process.args[0]} did not announce readiness')
    line = process.stdout.readline().strip()
    if not line:
        raise AssertionError(f'{process.args[0]} exited without announcing readiness')
    return line


def eventually(predicate, message, timeout=5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.05)
    raise AssertionError(message)


def test():
    deps = ('Xvfb', 'xcompmgr', 'xauth', 'xdotool', 'xprop', 'xmessage', 'ffmpeg')
    missing = [tool for tool in deps if shutil.which(tool) is None]
    if missing:
        raise SystemExit('Missing test commands: ' + ', '.join(missing))
    with tempfile.TemporaryDirectory(prefix='pcl3-intro-focus-') as tmp, contextlib.ExitStack() as stack:
        tmp = Path(tmp)
        env = dict(os.environ, GDK_BACKEND='x11', XAUTHORITY=str(tmp / 'xauthority'), LC_ALL='C')
        Path(env['XAUTHORITY']).touch(mode=0o600)
        cookie = secrets.token_hex(16)
        # Xvfb loads the authorization record regardless of this placeholder display number.
        def authorize(display):
            subprocess.run(['xauth', '-q', '-f', env['XAUTHORITY']], env=env, check=True, timeout=3,
                           input=f'add {display} MIT-MAGIC-COOKIE-1 {cookie}\n', text=True)

        authorize(':1023')
        xvfb = start(stack, ['Xvfb', '-displayfd', '1', '-nolisten', 'tcp', '-auth', env['XAUTHORITY'],
                             '-screen', '0', '800x600x24'], env, stdout=subprocess.PIPE)
        env['DISPLAY'] = ':' + read_line(xvfb, 5).decode('ascii')
        authorize(env['DISPLAY'])
        start(stack, ['xcompmgr', '-n'], env)
        compositor_probe = [
            '/usr/bin/python3', '-I', '-c',
            'import gi; gi.require_version("Gdk", "3.0"); gi.require_version("Gtk", "3.0"); '
            'from gi.repository import Gdk, Gtk; '
            'raise SystemExit(0 if Gtk.init_check()[0] and Gdk.Screen.get_default().is_composited() else 1)',
        ]
        eventually(lambda: subprocess.run(compositor_probe, env=env, timeout=2).returncode == 0,
                   'X11 test compositor did not start')
        clip = tmp / 'fixture.mp4'
        subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-nostdin', '-f', 'lavfi',
                        '-i', 'testsrc2=size=160x90:rate=15', '-t', '1', '-c:v', 'libx264', str(clip)],
                       env=env, check=True, timeout=10)
        app = start(stack, ['xmessage', '-name', 'PclIntroFocusTest', '-geometry', '480x270+12+12', 'Test window'], env)
        xid = subprocess.check_output(['xdotool', 'search', '--sync', '--limit', '1', '--name', 'PclIntroFocusTest'],
                                      env=env, timeout=5).strip().decode('ascii')
        subprocess.run(['xprop', '-id', xid, '-f', '_NET_WM_PID', '32c', '-set', '_NET_WM_PID', str(app.pid)],
                       check=True, env=env, timeout=3)
        subprocess.run(['xdotool', 'windowfocus', '--sync', xid], check=True, env=env, timeout=3)
        focus = subprocess.check_output(['xdotool', 'getwindowfocus', '-f'], env=env, timeout=3).strip().decode('ascii')
        assert focus == xid, 'the test window does not have X input focus'
        active = subprocess.check_output(['xprop', '-root', '_NET_ACTIVE_WINDOW'], env=env, timeout=3)
        assert b'not found' in active or b'no such atom' in active, active
        nonce = secrets.token_hex(32)
        # Match single-file MCM: source is a bounded -c argument and the nonce remains on stdin.
        source = HOST.read_bytes().decode('utf-8')
        assert 0 < len(source.encode('utf-8')) <= 32 * 1024 and '\0' not in source
        host = start(stack, ['/usr/bin/python3', '-I', '-c', source, clip], env,
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        host.stdin.write(f'{nonce}\n'.encode('ascii'))
        host.stdin.close()
        port = int(read_line(host, 5))
        with socket.create_connection(('127.0.0.1', port), timeout=5) as attacker:
            attacker.sendall(b'HELLO wrong\n')
        with socket.create_connection(('127.0.0.1', port), timeout=5) as channel, channel.makefile('rb') as replies:
            channel.sendall(f'HELLO {nonce} {app.pid}\nWINDOW X11 {xid}\n'.encode('ascii'))
            assert replies.readline() == b'HOLD_FRAME\n'
            channel.sendall(b'HELD\n')
            assert replies.readline() == b'COVERED\n'

            def overlay_mapped():
                response = subprocess.run(['xdotool', 'search', '--onlyvisible', '--pid', str(host.pid)],
                                          env=env, timeout=3, capture_output=True)
                return response.returncode == 0 and bool(response.stdout.strip())

            eventually(overlay_mapped, 'decoded video overlay never appeared on focused X11 window')
            actual_focus = subprocess.check_output(['xdotool', 'getwindowfocus', '-f'], env=env, timeout=3)
            assert actual_focus.strip().decode('ascii') == xid, 'the intro stole keyboard focus from the game'

            other = start(stack, ['xmessage', '-name', 'PclIntroOtherWindow', 'Other window'], env)
            other_xid = subprocess.check_output(['xdotool', 'search', '--sync', '--limit', '1', '--pid', str(other.pid),
                                                 '--name', 'PclIntroOtherWindow'], env=env, timeout=5).strip()
            subprocess.run(['xdotool', 'windowfocus', '--sync', other_xid], check=True, env=env, timeout=3)
            eventually(lambda: not overlay_mapped(), 'the overlay stayed visible after the game lost focus')
            channel.sendall(b'READY\n')
            assert not select.select([channel], [], [], .35)[0]
            channel.sendall(b'SPACE_DOWN\n')
            assert not select.select([channel], [], [], .5)[0], 'a Space before the prompt was displayed dismissed the intro'
            subprocess.run(['xdotool', 'windowfocus', '--sync', xid], check=True, env=env, timeout=3)
            eventually(overlay_mapped, 'the overlay did not return when the game regained focus')
            channel.sendall(b'SPACE_DOWN\n')
            assert not select.select([channel], [], [], .2)[0], 'a repeated key-down dismissed the intro'
            started_fade = time.monotonic()
            channel.sendall(b'SPACE_UP\nSPACE_DOWN\n')
            assert replies.readline() == b'RELEASE\n'
            assert time.monotonic() - started_fade >= .38, 'the game was released before the 400 ms fade'
        assert host.wait(timeout=5) == 0

        # A malformed command must stop the decoder before the helper closes IPC.
        nonce = secrets.token_hex(32)
        host = start(stack, ['/usr/bin/python3', '-I', HOST, clip], env, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        host.stdin.write(f'{nonce}\n'.encode('ascii'))
        host.stdin.close()
        port = int(read_line(host, 5))
        with socket.create_connection(('127.0.0.1', port), timeout=5) as channel, channel.makefile('rb') as replies:
            channel.sendall(f'HELLO {nonce} {app.pid}\nWINDOW X11 {xid}\n'.encode('ascii'))
            assert replies.readline() == b'HOLD_FRAME\n'
            channel.sendall(b'HELD\n')
            assert replies.readline() == b'COVERED\n'
            time.sleep(.3)
            channel.sendall(b'NOT_A_PROTOCOL_EVENT\n')
            assert replies.readline() == b'RELEASE\n', 'invalid IPC closed before media shutdown completed'
        assert host.wait(timeout=5) == 0
    print('X11 focus, fresh Space, and stop-before-release contracts passed.')


if __name__ == '__main__':
    test()
