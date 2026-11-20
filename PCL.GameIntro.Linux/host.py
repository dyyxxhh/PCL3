#!/usr/bin/python3
"""Opt-in Linux/XWayland game-intro playback process for the Vanilla 1.21.1 bridge."""
import argparse
import ctypes
import hmac
import os
import re
import select
import socket
import subprocess
import sys
import threading
import time

import gi

gi.require_version('Gtk', '3.0')
gi.require_version('Gdk', '3.0')
gi.require_version('GdkX11', '3.0')
gi.require_version('Gst', '1.0')
gi.require_version('PangoCairo', '1.0')
gi.require_foreign('cairo')
import cairo
from gi.repository import Gdk, GdkPixbuf, GdkX11, GLib, Gst, Gtk, PangoCairo

PROMPT = '按空格键进入游戏'


class X11Focus:
    """Read the server's actual keyboard focus; EWMH's active-window hint is optional."""

    def __init__(self):
        self.xlib = ctypes.CDLL('libX11.so.6')
        self.xlib.XOpenDisplay.argtypes = (ctypes.c_char_p,)
        self.xlib.XOpenDisplay.restype = ctypes.c_void_p
        self.xlib.XGetInputFocus.argtypes = (ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_int))
        self.xlib.XGetInputFocus.restype = ctypes.c_int
        self.xlib.XCloseDisplay.argtypes = (ctypes.c_void_p,)
        self.xlib.XCloseDisplay.restype = ctypes.c_int
        self.display = self.xlib.XOpenDisplay(None)
        if not self.display:
            raise OSError('Cannot open X11 display for game focus tracking')

    def matches(self, xid):
        focused = ctypes.c_ulong()
        revert = ctypes.c_int()
        self.xlib.XGetInputFocus(self.display, ctypes.byref(focused), ctypes.byref(revert))
        return focused.value == xid

    def close(self):
        self.xlib.XCloseDisplay(self.display)


class Player:
    def __init__(self, display, screen, clip, focus):
        self.display = display
        self.screen = screen
        self.focus = focus
        self.target = None
        self.channel = None
        self.held = False
        self.started = False
        self.coverage_pending = False
        self.covered = False
        self.ready = None
        self.prompt_painted = False
        self.fade = None
        self.space_down = False
        self.stopping = False
        self.stopped_event = threading.Event()
        self.frame = None
        self.frame_changed = threading.Lock()
        self.clock = time.monotonic
        self.window = Gtk.Window(type=Gtk.WindowType.POPUP)
        self.window.set_accept_focus(False)
        self.window.set_focus_on_map(False)
        self.window.set_app_paintable(True)
        self.window.set_visual(screen.get_rgba_visual())
        self.area = Gtk.DrawingArea()
        self.area.connect('draw', self.draw)
        self.window.add(self.area)
        self.area.show()
        self.window.realize()
        self.window.input_shape_combine_region(cairo.Region())
        self.pipeline = Gst.ElementFactory.make('playbin')
        self.sink = Gst.ElementFactory.make('appsink')
        if self.pipeline is None or self.sink is None:
            raise RuntimeError('GStreamer playbin or appsink is unavailable')
        self.sink.set_property('caps', Gst.Caps.from_string('video/x-raw,format=RGBA'))
        self.sink.set_property('emit-signals', True)
        self.sink.set_property('max-buffers', 2)
        self.sink.set_property('drop', True)
        self.sink.connect('new-sample', lambda sink: self.sample(sink, 'pull-sample'))
        self.sink.connect('new-preroll', lambda sink: self.sample(sink, 'pull-preroll'))
        video_convert = Gst.ElementFactory.make('videoconvert')
        video_caps = Gst.ElementFactory.make('capsfilter')
        if video_convert is None or video_caps is None:
            raise RuntimeError('GStreamer video conversion is unavailable')
        video_caps.set_property('caps', Gst.Caps.from_string('video/x-raw,format=RGBA'))
        video_bin = Gst.Bin.new('pcl3-video-output')
        queue = Gst.ElementFactory.make('queue')
        for element in (queue, video_convert, video_caps, self.sink):
            video_bin.add(element)
        if not (queue.link(video_convert) and video_convert.link(video_caps) and video_caps.link(self.sink)):
            raise RuntimeError('GStreamer could not link the video output')
        video_bin.add_pad(Gst.GhostPad.new('sink', queue.get_static_pad('sink')))
        self.pipeline.set_property('video-sink', video_bin)
        self.pipeline.set_property('uri', Gst.filename_to_uri(clip))
        self.bus = self.pipeline.get_bus()
        self.bus.add_signal_watch()
        self.bus.connect('message', self.message)
        GLib.timeout_add(16, self.tick)
        if self.pipeline.set_state(Gst.State.PAUSED) == Gst.StateChangeReturn.FAILURE:
            raise RuntimeError('GStreamer could not prepare the intro media')

    def sample(self, sink, pull):
        sample = sink.emit(pull)
        if sample is None:
            return Gst.FlowReturn.FLUSHING
        caps = sample.get_caps().get_structure(0)
        width, height = caps.get_value('width'), caps.get_value('height')
        buffer = sample.get_buffer()
        if not (0 < width <= 4096 and 0 < height <= 4096 and buffer.get_size() == width * height * 4):
            GLib.idle_add(self.stop)
            return Gst.FlowReturn.ERROR
        frame = GdkPixbuf.Pixbuf.new_from_bytes(GLib.Bytes.new(buffer.extract_dup(0, buffer.get_size())),
                                                GdkPixbuf.Colorspace.RGB, True, 8, width, height, width * 4)
        with self.frame_changed:
            self.frame = frame
        return Gst.FlowReturn.OK

    def message(self, _, message):
        if message.type == Gst.MessageType.ERROR:
            self.stop()
        elif message.type == Gst.MessageType.EOS:
            if not self.pipeline.seek_simple(Gst.Format.TIME, Gst.SeekFlags.FLUSH | Gst.SeekFlags.KEY_UNIT, 0):
                self.stop()

    def draw(self, area, ctx):
        width, height = area.get_allocated_width(), area.get_allocated_height()
        ctx.set_source_rgb(0, 0, 0)
        ctx.paint()
        with self.frame_changed:
            video = self.frame
        if video is not None:
            scale = min(width / video.get_width(), height / video.get_height())
            ctx.save()
            ctx.translate((width - video.get_width() * scale) / 2, (height - video.get_height() * scale) / 2)
            ctx.scale(scale, scale)
            Gdk.cairo_set_source_pixbuf(ctx, video, 0, 0)
            ctx.paint()
            ctx.restore()
            if not self.covered and not self.coverage_pending:
                clock = area.get_frame_clock()
                if clock is not None:
                    self.coverage_pending = True
                    clock.connect('after-paint', self.first_frame_painted)
        if self.ready is not None:
            opacity = min(1, max(0, (self.clock() - self.ready) / .300))
            layout = area.create_pango_layout(PROMPT)
            layout.set_markup('<span font_desc="Sans Bold 18">' + PROMPT + '</span>')
            tw, th = layout.get_pixel_size()
            x, y = (width - tw) / 2, height - th - 28
            ctx.set_source_rgba(0, 0, 0, .72 * opacity)
            ctx.rectangle(x - 14, y - 7, tw + 28, th + 14)
            ctx.fill()
            ctx.set_source_rgba(1, 1, 1, opacity)
            ctx.move_to(x, y)
            PangoCairo.show_layout(ctx, layout)
            if opacity >= 1:
                self.prompt_painted = True
        return False

    def first_frame_painted(self, clock):
        if self.stopping or self.covered or not self.window.get_mapped():
            return
        self.covered = True
        self.display.sync()
        try:
            self.channel.sendall(b'COVERED\n')
        except OSError:
            self.stop()

    def tick(self):
        if self.stopping:
            return False
        if not self.held:
            return True
        if self.fade is not None:
            opacity = max(0, 1 - (self.clock() - self.fade) / .400)
            if opacity == 0:
                self.stop()
                return False
            self.window.set_opacity(opacity)
        self.display.error_trap_push()
        try:
            if self.target.is_destroyed():
                self.stop()
                return False
            _, x, y = self.target.get_origin()
            _, _, width, height = self.target.get_geometry()
            visible = width > 1 and height > 1 and self.focus.matches(self.target.get_xid())
        finally:
            xerror = self.display.error_trap_pop()
        if xerror:
            self.stop()
            return False
        if visible:
            self.window.move(x, y)
            self.window.resize(width, height)
            if not self.started:
                self.started = True
                if self.pipeline.set_state(Gst.State.PLAYING) == Gst.StateChangeReturn.FAILURE:
                    self.stop()
                    return False
            if self.frame is not None:
                self.window.show()
                self.area.queue_draw()
        else:
            self.prompt_painted = False
            self.window.hide()
        return True

    def event(self, kind, value=None):
        if self.stopping:
            return False
        if kind == 'WINDOW' and self.target is None:
            self.display.error_trap_push()
            try:
                self.target = GdkX11.X11Window.foreign_new_for_display(self.display, value)
            finally:
                xerror = self.display.error_trap_pop()
            if xerror or self.target is None:
                self.stop()
            else:
                try:
                    self.channel.sendall(b'HOLD_FRAME\n')
                except OSError:
                    self.stop()
        elif kind == 'HELD' and self.target is not None and not self.held:
            self.held = True
        elif kind == 'READY' and self.held and self.ready is None:
            self.ready = self.clock()
        elif kind == 'SPACE_DOWN':
            if (not self.space_down and self.fade is None and self.prompt_painted and
                    self.window.get_mapped() and self.focus.matches(self.target.get_xid())):
                self.fade = self.clock()
            self.space_down = True
        elif kind == 'SPACE_UP':
            self.space_down = False
        else:
            self.stop()
        return False

    def stop(self):
        if self.stopping:
            return False
        self.stopping = True
        self.window.hide()
        self.pipeline.set_state(Gst.State.NULL)
        _, state, _ = self.pipeline.get_state(2 * Gst.SECOND)
        if state != Gst.State.NULL:
            os._exit(1)  # The process owns the decoder and audio device.
        if self.channel is not None:
            try:
                self.channel.sendall(b'RELEASE\n')
                self.channel.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        self.stopped_event.set()
        Gtk.main_quit()
        return False


def pid_owns_window(xid, pid):
    try:
        result = subprocess.run(['/usr/bin/xprop', '-id', hex(xid), '_NET_WM_PID'], check=True,
                                capture_output=True, text=True, timeout=.15)
        return re.fullmatch(r'_NET_WM_PID\(CARDINAL\) = *' + str(pid) + r'\s*', result.stdout) is not None
    except (OSError, subprocess.SubprocessError):
        return False


def receive(listener, nonce, player):
    channel = None
    source = None
    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            listener.settimeout(max(.01, deadline - time.monotonic()))
            candidate, _ = listener.accept()
            candidate.settimeout(min(2, max(.01, deadline - time.monotonic())))
            candidate_source = candidate.makefile('rb')
            try:
                hello = re.fullmatch(rb'HELLO ([0-9a-f]{64}) ([1-9][0-9]{0,9})\r?\n', candidate_source.readline(100))
            except OSError:
                hello = None
            if hello is None or not hmac.compare_digest(hello[1], nonce.encode('ascii')):
                candidate_source.close()
                candidate.close()
                continue
            channel, source = candidate, candidate_source
            break
        if channel is None:
            return
        listener.close()
        pid = int(hello[2])
        channel.settimeout(None)
        player.channel = channel
        for line in iter(lambda: source.readline(100), b''):
            if line in (b'HELD\n', b'BYPASS\n', b'READY\n', b'SPACE_DOWN\n', b'SPACE_UP\n'):
                GLib.idle_add(player.event, line.strip().decode('ascii'))
            else:
                window = re.fullmatch(rb'WINDOW X11 ([1-9][0-9]{0,9})\n', line)
                if window is None or not pid_owns_window(int(window[1]), pid):
                    break
                GLib.idle_add(player.event, 'WINDOW', int(window[1]))
    except OSError:
        pass
    finally:
        GLib.idle_add(player.stop)
        if not player.stopped_event.wait(2.25):
            os._exit(1)  # Do not leave audio playing after IPC validation or disconnect failure.
        if source is not None:
            source.close()
        if channel is not None:
            channel.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('clip')
    args = parser.parse_args()
    nonce = sys.stdin.readline(66).strip() if select.select([sys.stdin], [], [], 2)[0] else ''
    if re.fullmatch('[0-9a-f]{64}', nonce) is None or not os.path.isabs(args.clip):
        return 2
    initialized, _ = Gtk.init_check()
    if not initialized:
        return 2
    Gst.init(None)
    display, screen = Gdk.Display.get_default(), Gdk.Screen.get_default()
    if not isinstance(display, GdkX11.X11Display) or not screen.is_composited() or screen.get_rgba_visual() is None:
        return 2
    if not os.access('/usr/bin/xprop', os.X_OK):
        return 2
    focus = X11Focus()
    try:
        player = Player(display, screen, args.clip, focus)
        with socket.create_server(('127.0.0.1', 0), backlog=1) as server:
            server.settimeout(20)
            threading.Thread(target=receive, args=(server, nonce, player), daemon=True).start()
            print(server.getsockname()[1], flush=True)
            Gtk.main()
    finally:
        focus.close()
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
