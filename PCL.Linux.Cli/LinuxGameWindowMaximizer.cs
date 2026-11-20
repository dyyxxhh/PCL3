using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCL.Linux.Cli;

/// <summary>
/// Requests maximization once for each managed, normal X11 window owned by the game process.
/// The monitor ignores declared splash screens, transients and unmanaged windows.
/// </summary>
internal sealed class LinuxGameWindowMaximizer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;
    private int _disposed;

    private LinuxGameWindowMaximizer(Process game)
    {
        var cancellationToken = _stop.Token;
        _monitor = Task.Run(() => MonitorProcessAsync(game, cancellationToken));
    }

    internal static LinuxGameWindowMaximizer? TryStart(Process game, bool enabled)
    {
        if (!enabled) return null;
        if (!OperatingSystem.IsLinux()) return null;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine("Warning: maximizing Minecraft requires X11/XWayland (DISPLAY); native Wayland cannot be controlled by MCM.");
            return null;
        }
        return new LinuxGameWindowMaximizer(game);
    }

    internal static async Task MonitorProcessAsync(Process game, IWindowManager manager,
        TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var requested = new HashSet<ulong>();
        var pending = new Dictionary<ulong, long>();
        var warningShown = false;
        var sawEligibleWindow = false;
        var noWindowWarningShown = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested && !game.HasExited)
            {
                var windows = manager.GetWindows(game.Id);
                if (cancellationToken.IsCancellationRequested || game.HasExited) return;
                foreach (var window in windows)
                {
                    if (window.ProcessId != game.Id || !window.IsManaged || !window.IsMapped || window.IsTransient ||
                        window.OverrideRedirect || !window.IsNormal || window.Id == 0)
                        continue;

                    sawEligibleWindow = true;
                    if (window.IsMaximized)
                    {
                        pending.Remove(window.Id);
                        requested.Add(window.Id);
                        continue;
                    }

                    if (pending.TryGetValue(window.Id, out var requestedAt))
                    {
                        if (stopwatch.ElapsedMilliseconds - requestedAt >= 1000)
                        {
                            pending.Remove(window.Id);
                            if (!warningShown)
                            {
                                Console.Error.WriteLine("Warning: the active X11 window manager did not confirm Minecraft maximization.");
                                warningShown = true;
                            }
                        }
                        continue;
                    }

                    if (requested.Add(window.Id))
                    {
                        manager.RequestMaximize(window.Id, game.Id);
                        pending[window.Id] = stopwatch.ElapsedMilliseconds;
                    }
                }
                if (!sawEligibleWindow && !noWindowWarningShown && stopwatch.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    Console.Error.WriteLine("Warning: MCM has not found a manageable X11 Minecraft window; native Wayland is unsupported.");
                    noWindowWarningShown = true;
                }
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            if (!warningShown)
                Console.Error.WriteLine("Warning: MCM could not maximize the Minecraft window through X11: " + ex.Message);
        }
    }

    private static async Task MonitorProcessAsync(Process game, CancellationToken cancellationToken)
    {
        try
        {
            using var manager = X11WindowManager.TryOpen();
            if (manager is null)
            {
                Console.Error.WriteLine("Warning: no EWMH-compatible X11 window manager is available; starting Minecraft without maximization.");
                return;
            }
            await MonitorProcessAsync(game, manager, TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine("Warning: MCM could not access the X11 window manager; starting Minecraft without maximization: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = _monitor.ContinueWith(task =>
        {
            _ = task.Exception;
            _stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

internal sealed record LinuxGameWindow(ulong Id, int ProcessId, bool IsMapped, bool IsManaged,
    bool IsTransient, bool OverrideRedirect, bool IsNormal, bool IsMaximized);

internal interface IWindowManager : IDisposable
{
    IReadOnlyList<LinuxGameWindow> GetWindows(int processId);
    void RequestMaximize(ulong windowId, int processId);
}

// XCB reports stale windows and disconnected displays as errors without installing a
// process-wide Xlib error handler. X11 wire values are 32-bit on all supported CPUs.
internal sealed class X11WindowManager : IWindowManager
{
    private const string Xcb = "libxcb.so.1";
    private const uint AtomType = 4, CardinalType = 6, WindowType = 33;
    private const uint WmEventMask = (1u << 19) | (1u << 20);
    private readonly nint _connection;
    private readonly uint _root;
    private readonly Dictionary<string, uint> _atoms = new(StringComparer.Ordinal);

    private X11WindowManager(nint connection, uint root) => (_connection, _root) = (connection, root);

    internal static X11WindowManager? TryOpen()
    {
        var connection = xcb_connect(0, out var screen);
        if (connection == 0) return null;
        var keepConnection = false;
        try
        {
            if (xcb_connection_has_error(connection) != 0) return null;
            var roots = xcb_setup_roots_iterator(xcb_get_setup(connection));
            for (var i = 0; i < screen && roots.Remaining > 0; i++) xcb_screen_next(ref roots);
            if (roots.Remaining == 0 || roots.Data == 0) return null;
            var manager = new X11WindowManager(connection, (uint)Marshal.ReadInt32(roots.Data));
            if (!manager.SupportsMaximize()) return null;
            keepConnection = true;
            return manager;
        }
        finally { if (!keepConnection) xcb_disconnect(connection); }
    }

    private bool SupportsMaximize()
    {
        var check = Atom("_NET_SUPPORTING_WM_CHECK");
        var wm = Property(_root, check, WindowType, 1).FirstOrDefault();
        if (wm == 0 || Property(wm, check, WindowType, 1).FirstOrDefault() != wm) return false;
        var supported = Property(_root, Atom("_NET_SUPPORTED"), AtomType);
        return supported.Contains(Atom("_NET_CLIENT_LIST")) && supported.Contains(Atom("_NET_WM_STATE")) &&
            supported.Contains(Atom("_NET_WM_STATE_MAXIMIZED_HORZ")) && supported.Contains(Atom("_NET_WM_STATE_MAXIMIZED_VERT"));
    }

    public IReadOnlyList<LinuxGameWindow> GetWindows(int processId)
    {
        if (xcb_connection_has_error(_connection) != 0) throw new InvalidOperationException("The X11 connection closed.");
        List<LinuxGameWindow> windows = [];
        foreach (var window in Property(_root, Atom("_NET_CLIENT_LIST"), WindowType))
        {
            if (window == 0 || !OwnsWindow(window, processId)) continue;
            var attrs = Reply(xcb_get_window_attributes(_connection, window));
            if (attrs == 0) continue;
            bool mapped, overrideRedirect;
            try
            {
                mapped = Marshal.ReadByte(attrs, 26) == 2; // xcb_get_window_attributes_reply_t.map_state (VIEWABLE)
                overrideRedirect = Marshal.ReadByte(attrs, 27) != 0;
            }
            finally { Free(attrs); }
            var transient = Property(window, Atom("WM_TRANSIENT_FOR"), WindowType, 1).Length != 0;
            var types = Property(window, Atom("_NET_WM_WINDOW_TYPE"), AtomType);
            var normal = types.Length == 0 || types[0] == Atom("_NET_WM_WINDOW_TYPE_NORMAL");
            var states = Property(window, Atom("_NET_WM_STATE"), AtomType);
            normal &= !states.Contains(Atom("_NET_WM_STATE_FULLSCREEN"));
            var maximized = states.Contains(Atom("_NET_WM_STATE_MAXIMIZED_HORZ")) && states.Contains(Atom("_NET_WM_STATE_MAXIMIZED_VERT"));
            windows.Add(new LinuxGameWindow(window, processId, mapped, true, transient, overrideRedirect, normal, maximized));
        }
        return windows;
    }

    public void RequestMaximize(ulong windowId, int processId)
    {
        var window = checked((uint)windowId);
        if (!OwnsWindow(window, processId)) return;
        var message = new XcbClientMessage
        {
            ResponseType = 33, // ClientMessage
            Format = 32,
            Window = window,
            MessageType = Atom("_NET_WM_STATE"),
            Action = 1, // _NET_WM_STATE_ADD
            FirstAtom = Atom("_NET_WM_STATE_MAXIMIZED_VERT"),
            SecondAtom = Atom("_NET_WM_STATE_MAXIMIZED_HORZ"),
            Source = 1 // An application request, without activation.
        };
        xcb_send_event(_connection, 0, _root, WmEventMask, ref message);
        xcb_flush(_connection);
    }

    private bool OwnsWindow(uint window, int processId) =>
        Property(window, Atom("_NET_WM_PID"), CardinalType, 1).FirstOrDefault() == (uint)processId;

    private uint Atom(string name)
    {
        if (_atoms.TryGetValue(name, out var atom)) return atom;
        var reply = Reply(xcb_intern_atom(_connection, 0, (ushort)name.Length, name));
        if (reply == 0) throw new InvalidOperationException("Unable to resolve X11 atoms.");
        try { return _atoms[name] = (uint)Marshal.ReadInt32(reply, 8); }
        finally { Free(reply); }
    }

    private uint[] Property(uint window, uint atom, uint type, uint maximum = 4096)
    {
        var reply = Reply(xcb_get_property(_connection, 0, window, atom, type, 0, maximum));
        if (reply == 0) return [];
        try
        {
            // xcb_get_property_reply_t contains a 32-byte header followed by value data.
            if (Marshal.ReadByte(reply, 1) != 32 || (uint)Marshal.ReadInt32(reply, 8) != type) return [];
            var count = Marshal.ReadInt32(reply, 16);
            if (count <= 0 || count > maximum || count > Marshal.ReadInt32(reply, 4)) return [];
            var values = new uint[count];
            for (var i = 0; i < count; i++) values[i] = (uint)Marshal.ReadInt32(reply, 32 + sizeof(uint) * i);
            return values;
        }
        finally { Free(reply); }
    }

    private nint Reply(uint sequence)
    {
        var reply = xcb_wait_for_reply(_connection, sequence, out var error);
        if (error != 0) Free(error);
        return reply;
    }

    public void Dispose() => xcb_disconnect(_connection);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator { public nint Data; public int Remaining, Index; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XcbClientMessage
    {
        public byte ResponseType, Format;
        public ushort Sequence;
        public uint Window, MessageType, Action, FirstAtom, SecondAtom, Source, Reserved;
    }

    [DllImport(Xcb)] private static extern nint xcb_connect(nint displayName, out int screen);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(nint connection);
    [DllImport(Xcb)] private static extern void xcb_disconnect(nint connection);
    [DllImport(Xcb)] private static extern nint xcb_get_setup(nint connection);
    [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(nint setup);
    [DllImport(Xcb)] private static extern void xcb_screen_next(ref ScreenIterator iterator);
    [DllImport(Xcb, CharSet = CharSet.Ansi)] private static extern uint xcb_intern_atom(nint connection, byte onlyIfExists, ushort length, string name);
    [DllImport(Xcb)] private static extern uint xcb_get_property(nint connection, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [DllImport(Xcb)] private static extern uint xcb_get_window_attributes(nint connection, uint window);
    [DllImport(Xcb)] private static extern nint xcb_wait_for_reply(nint connection, uint sequence, out nint error);
    [DllImport(Xcb)] private static extern uint xcb_send_event(nint connection, byte propagate, uint destination, uint mask, ref XcbClientMessage message);
    [DllImport(Xcb)] private static extern int xcb_flush(nint connection);
    [DllImport("libc", EntryPoint = "free")] private static extern void Free(nint pointer);
}
