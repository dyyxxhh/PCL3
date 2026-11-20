#!/usr/bin/python3
"""Exercise MCM launch against an isolated Xvfb/Openbox session, using a mock Java executable."""
import argparse
import json
import os
from pathlib import Path
import select
import shlex
import subprocess
import sys
import tempfile
import time


def wait_for(check, description):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if check():
            return
        time.sleep(.025)
    raise AssertionError(description)


def fixture(mode):
    import gi
    gi.require_version('Gtk', '3.0')
    gi.require_version('Gdk', '3.0')
    from gi.repository import Gdk, Gtk

    def pump():
        while Gtk.events_pending():
            Gtk.main_iteration()

    def for_seconds(seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            pump()
            time.sleep(.025)

    def show(title, hint=Gdk.WindowTypeHint.NORMAL):
        window = Gtk.Window(title=title)
        window.set_default_size(300, 200)
        window.set_type_hint(hint)
        window.show()
        pump()
        return window

    def maximized(window):
        pump()
        return bool(window.get_window().get_state() & Gdk.WindowState.MAXIMIZED)

    if mode == 'foreign':
        window = show('Unrelated app in the test display')
        print('WINDOW', flush=True)
        for_seconds(5)
        assert not maximized(window), 'an unrelated process was maximized'
        return 0

    assert 'fullscreen:false\n' in Path('options.txt').read_text(), 'launch must start windowed'
    splash = show('Test splash', Gdk.WindowTypeHint.SPLASHSCREEN)
    dialog = Gtk.Window(title='Transient test dialog', transient_for=splash)
    dialog.show()
    for_seconds(.6)
    assert not maximized(splash), 'a declared splash was maximized'
    assert not maximized(dialog), 'a transient dialog was maximized'
    dialog.destroy()
    splash.destroy()
    window = show('Original game window')
    if mode == 'no-wm':
        for_seconds(.3)
        assert not maximized(window)
        return 23
    wait_for(lambda: maximized(window), 'the first game window was never maximized')
    window.unmaximize()
    wait_for(lambda: not maximized(window), 'the window manager did not restore the game window')
    for_seconds(.45)
    assert not maximized(window), 'MCM maximized the same window again after manual restore'
    window.destroy()
    replacement = show('Replacement game window')
    wait_for(lambda: maximized(replacement), 'the replacement game window was never maximized')
    return 0


def cleanup(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(3)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def run(args):
    env = dict(os.environ)
    env.update(GDK_BACKEND='x11', PCL3_GAME_INTRO_PREVIEW='0')
    env.pop('XAUTHORITY', None)
    with tempfile.TemporaryDirectory(prefix='mcm-maximize-test-') as tmp:
        root = Path(tmp)
        game = root / 'game'
        version = game / 'versions' / 'test'
        version.mkdir(parents=True)
        (version / 'test.json').write_text(json.dumps({
            'id': 'test', 'mainClass': 'TestClient', 'javaVersion': {'majorVersion': 21}
        }))
        env['XDG_CONFIG_HOME'] = str(root / 'config')
        java = root / 'java'
        fixture_command = shlex.join((sys.executable, str(Path(__file__).resolve()), '--fixture'))
        java.write_text('#!/bin/sh\nif [ "$1" = -version ]; then\n  echo \'openjdk version "21.0.7"\'\n  exit 0\nfi\nexec ' + fixture_command + ' "$MCM_MAXIMIZE_FIXTURE_MODE"\n')
        java.chmod(0o700)
        mcm = ['dotnet', str(args.mcm)] if args.mcm.suffix == '.dll' else [str(args.mcm)]
        subprocess.run([*mcm, 'login', 'offline', 'WindowTest'], check=True, env=env, capture_output=True)
        subprocess.run([*mcm, 'config', 'set', 'game.window.mode', 'maximized'], check=True, env=env, capture_output=True)
        launch = [*mcm, 'launch', 'test', '--game-dir', str(game), '--java', str(java)]
        xserver = subprocess.Popen([str(args.xvfb), '-displayfd', '1', '-screen', '0', '1024x768x24', '-nolisten', 'tcp', '-noreset'],
                                   env=env, stdout=subprocess.PIPE, text=True)
        wm = foreign = None
        try:
            assert select.select([xserver.stdout], [], [], 5)[0], 'Xvfb failed to announce a display'
            display = xserver.stdout.readline().strip()
            assert display.isdecimal(), f'invalid Xvfb display {display!r}'
            env['DISPLAY'] = ':' + display
            (game / 'options.txt').write_text('fullscreen:true\n')
            env['MCM_MAXIMIZE_FIXTURE_MODE'] = 'no-wm'
            result = subprocess.run(launch, env=env, text=True, capture_output=True, timeout=10)
            assert result.returncode == 23, result.stderr
            assert 'no EWMH-compatible X11 window manager' in result.stderr, result.stderr
            wm = subprocess.Popen([str(args.openbox), '--sm-disable'], env=env)
            wait_for(lambda: 'window id #' in subprocess.check_output(
                ['xprop', '-root', '_NET_SUPPORTING_WM_CHECK'], text=True, env=env), 'Openbox failed to start')
            foreign = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--fixture', 'foreign'],
                                       env=env, stdout=subprocess.PIPE, text=True)
            assert select.select([foreign.stdout], [], [], 5)[0] and foreign.stdout.readline() == 'WINDOW\n'
            env['MCM_MAXIMIZE_FIXTURE_MODE'] = 'wm'
            result = subprocess.run(launch, env=env, text=True, capture_output=True, timeout=15)
            assert result.returncode == 0, result.stdout + result.stderr
            assert foreign.wait(6) == 0
            print('MCM maximize with Xvfb/Openbox, replacement window, and no-WM fallback passed')
        finally:
            cleanup(foreign)
            cleanup(wm)
            cleanup(xserver)


if __name__ == '__main__':
    if sys.argv[1:2] == ['--fixture']:
        raise SystemExit(fixture(sys.argv[2]))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mcm', type=lambda path: Path(path).resolve(strict=True))
    parser.add_argument('--xvfb', default='Xvfb')
    parser.add_argument('--openbox', default='openbox')
    run(parser.parse_args())
