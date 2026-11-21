#!/usr/bin/env python3
"""Native Wayland fullscreen smoke test; run on a dedicated Hyprland test instance.

Needs the GTK/GStreamer host dependencies, gtk-layer-shell, hyprctl and ffmpeg.
Pass the test instance's WAYLAND_DISPLAY and HYPRLAND_INSTANCE_SIGNATURE.
Both the synthetic game and the player run without DISPLAY, even if it is set.
"""
import contextlib
import json
import os
from pathlib import Path
import secrets
import select
import socket
import subprocess
import tempfile
import time

from test_x11_focus import HOST, eventually, read_line, start


GAME = '''
import gi, sys
gi.require_version('Gtk', '3.0')
from gi.repository import GLib, Gtk
window = Gtk.Window(title=sys.argv[1])
window.fullscreen()
window.show()
Gtk.main()
'''


def test():
    env = os.environ.copy()
    env.pop('DISPLAY', None)
    env['GDK_BACKEND'] = 'wayland'
    if not env.get('WAYLAND_DISPLAY') or not env.get('HYPRLAND_INSTANCE_SIGNATURE'):
        raise RuntimeError('A dedicated Hyprland test instance is required')

    def hypr(command):
        return json.loads(subprocess.check_output(['hyprctl', '-j', command], env=env, timeout=3))

    def active(pid):
        window = hypr('activewindow')
        return window.get('pid') == pid and not window['xwayland'] and window['fullscreen'] in (2, 3)

    def overlay():
        return next((surface for monitor in hypr('layers').values() for surfaces in monitor['levels'].values()
                     for surface in surfaces if surface['namespace'] == 'pcl3-game-intro'), None)

    with contextlib.ExitStack() as stack:
        clip = Path(stack.enter_context(tempfile.TemporaryDirectory())) / 'clip.ogv'
        subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
                        'testsrc2=size=320x180:rate=20', '-t', '1', '-c:v', 'libtheora', str(clip)], check=True)
        game = start(stack, ['/usr/bin/python3', '-I', '-c', GAME, 'PCL3 Wayland test game'], env)
        eventually(lambda: active(game.pid), 'the native Wayland game did not become fullscreen')

        def connect():
            nonce = secrets.token_hex(32)
            player = start(stack, ['/usr/bin/python3', '-I', HOST, clip], env,
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE)
            player.stdin.write(f'{nonce}\n'.encode('ascii'))
            player.stdin.close()
            port = int(read_line(player, 5))
            channel = stack.enter_context(socket.create_connection(('127.0.0.1', port), timeout=5))
            replies = stack.enter_context(channel.makefile('rb'))
            channel.sendall(f'HELLO {nonce} {game.pid}\nWINDOW WAYLAND\n'.encode('ascii'))
            assert replies.readline() == b'HOLD_FOCUS\n'
            return player, channel, replies

        player, channel, replies = connect()
        channel.sendall(b'HELD\nUNFOCUSED\n')
        assert not select.select([channel], [], [], .35)[0] and overlay() is None
        channel.sendall(b'FOCUSED\n')
        eventually(overlay, 'the compositor overlay did not map above the fullscreen game')
        surface = overlay()
        monitors = hypr('monitors')
        assert any(abs(surface['w'] - m['width'] / m['scale']) <= 1 and
                   abs(surface['h'] - m['height'] / m['scale']) <= 1 for m in monitors), surface
        assert active(game.pid), 'the overlay took focus or changed fullscreen mode'

        # Losing focus must hide the overlay, and a Space held while loading must not dismiss it.
        channel.sendall(b'UNFOCUSED\nSPACE_DOWN\nREADY\n')
        eventually(lambda: overlay() is None, 'the overlay did not hide when the game lost focus')
        channel.sendall(b'FOCUSED\n')
        eventually(overlay, 'the overlay did not return')
        assert not select.select([channel], [], [], .5)[0]
        channel.sendall(b'SPACE_DOWN\n')
        assert not select.select([channel], [], [], .2)[0]
        started = time.monotonic()
        channel.sendall(b'SPACE_UP\nSPACE_DOWN\n')
        assert replies.readline() == b'RELEASE\n'
        assert time.monotonic() - started >= .38, 'handoff did not wait for the fade'
        assert player.wait(timeout=5) == 0 and active(game.pid)
        eventually(lambda: overlay() is None, 'the overlay did not disappear after handoff')

        player, channel, replies = connect()
        channel.sendall(b'HELD\nFOCUSED\n')
        eventually(overlay, 'the second overlay did not map')
        channel.sendall(b'NOT_A_PROTOCOL_EVENT\n')
        assert replies.readline() == b'RELEASE\n'
        assert player.wait(timeout=5) == 0
    print('Native Wayland fullscreen, game focus, fresh Space, fade, and IPC shutdown passed.')


if __name__ == '__main__':
    test()
