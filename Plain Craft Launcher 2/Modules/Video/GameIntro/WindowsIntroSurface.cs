using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCL;

internal sealed class WindowsIntroSurface : Window
{
    private readonly MediaElement _video;
    private readonly TextBlock _prompt;
    private readonly nint _window;
    private nint _target;
    private uint _gamePid;
    private bool _playing;
    private bool _hasMedia;
    private bool _armed;

    internal event Action? Failed;

    internal WindowsIntroSurface(Uri media)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        Focusable = false;
        _video = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
            Volume = 1,
            Source = media
        };
        _video.MediaOpened += (_, _) => _hasMedia = true;
        _video.MediaEnded += (_, _) =>
        {
            _video.Position = TimeSpan.Zero;
            if (_playing) _video.Play();
        };
        _video.MediaFailed += (_, _) => Failed?.Invoke();
        _prompt = new TextBlock
        {
            Text = "按空格键进入游戏",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            FontSize = 26,
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 12, 12, 36),
            Opacity = 0
        };
        Content = new Grid { Background = Brushes.Black, Children = { _video, _prompt } };
        _window = new WindowInteropHelper(this).EnsureHandle();
        var source = HwndSource.FromHwnd(_window);
        source?.AddHook((nint _, int message, nint _, nint _, ref bool handled) =>
        {
            if (message == 0x84) // WM_NCHITTEST
            {
                handled = true;
                return -1; // HTTRANSPARENT
            }
            return 0;
        });
        var style = GetWindowLong(_window, -20);
        SetWindowLong(_window, -20, style | 0x08000000 | 0x00000080 | 0x00000020);
        Opacity = 0;
    }

    internal void PrepareMedia()
    {
        Show(); // Prepare media while the surface is still fully transparent.
        _video.Pause();
    }

    internal bool Bind(nint target, uint gamePid)
    {
        if (target == 0 || GetWindowThreadProcessId(target, out var ownerPid) == 0 || ownerPid != gamePid)
            return false;
        _target = target;
        _gamePid = gamePid;
        return true;
    }

    internal void Arm() => _armed = true;

    internal bool Refresh()
    {
        if (_target == 0) return true;
        if (GetWindowThreadProcessId(_target, out var ownerPid) == 0 || ownerPid != _gamePid) return false;
        var visible = _armed && _hasMedia && !IsIconic(_target) && IsWindowVisible(_target) && GetForegroundWindow() == _target &&
            GetClientRect(_target, out var rectangle) && rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top;
        var origin = new PointI();
        if (visible && ClientToScreen(_target, ref origin) && GetClientRect(_target, out var client))
        {
            if (!SetWindowPos(_window, -1, origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top, 0x0010)) return false;
            if (!_playing)
            {
                if (!IsVisible) Show();
                _playing = true;
                _video.Play();
            }
        }
        else
        {
            if (_playing)
            {
                _playing = false;
                _video.Pause();
            }
            if (IsVisible) Hide();
        }
        return true;
    }

    internal void Present(PCL.Core.Shared.Minecraft.GameIntroPresentation frame)
    {
        Opacity = _playing ? frame.OverlayOpacity : 0;
        _prompt.Opacity = frame.PromptOpacity;
    }

    internal void Stop()
    {
        _playing = false;
        _armed = false;
        _video.Volume = 0;
        _video.Close();
        if (IsVisible) Hide();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out RectI rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref PointI point);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int style);
}
