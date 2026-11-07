using System.Globalization;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

/// <summary>Renders installation activity even while a download or external installer is waiting.</summary>
internal sealed class ConsoleInstallProgress : IProgress<DownloadProgress>, IDisposable
{
    private readonly object _sync = new();
    private readonly TextWriter _output;
    private readonly bool _interactive;
    private readonly TimeProvider _timeProvider;
    private readonly long _started;
    private readonly ITimer _timer;
    private string _stage;
    private DownloadProgress? _download;
    private TimeSpan _lastOutput;
    private int _frame;
    private bool _disposed;
    private bool _inline;

    public ConsoleInstallProgress(string stage)
        : this(stage, Console.Error, !Console.IsErrorRedirected
            && Environment.GetEnvironmentVariable("TERM") != "dumb") { }

    internal ConsoleInstallProgress(string stage, TextWriter output, bool interactive, TimeProvider? timeProvider = null)
    {
        _output = output;
        _interactive = interactive;
        _inline = interactive;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _started = _timeProvider.GetTimestamp();
        _stage = stage;
        Render();
        _timer = _timeProvider.CreateTimer(_ => Refresh(), null,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public void SetStage(string stage, bool inline = true)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var nextInline = _interactive && inline;
            if (_inline && !nextInline) _output.WriteLine();
            _inline = nextInline;
            _stage = stage;
            _download = null;
            Render();
        }
    }

    public void Report(DownloadProgress value)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var changedStage = _stage != value.Stage;
            var finished = value.Total > 0 && value.Completed == value.Total
                && (_download?.Completed != value.Completed || _download?.Total != value.Total);
            _stage = value.Stage;
            _download = value;
            if (changedStage || finished || Elapsed - _lastOutput >= RefreshInterval)
                Render();
        }
    }

    private TimeSpan Elapsed => _timeProvider.GetElapsedTime(_started);
    private TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(_inline ? 100 : 5000);

    private void Refresh()
    {
        lock (_sync)
        {
            if (!_disposed && Elapsed - _lastOutput >= RefreshInterval)
                Render();
        }
    }

    private void Render()
    {
        var elapsed = Elapsed;
        var indicator = _inline ? "|/-\\"[_frame++ % 4] + " " : "";
        var status = _stage;
        if (_download is { Total: > 0 } download)
        {
            var percent = Math.Clamp(download.Completed * 100L / download.Total, 0, 100);
            const int width = 20;
            var filled = (int)(percent * width / 100);
            status += $" [{new string('#', filled)}{new string('-', width - filled)}] {percent,3}% ({download.Completed}/{download.Total} files)";
        }
        status += " | " + elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        if (_download is { CurrentPath.Length: > 0 } current)
            status += " | " + Path.GetFileName(current.CurrentPath);
        // Metadata and filenames may come from remote services. Keep them on one terminal line.
        var line = indicator + new string(status.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        if (_inline)
        {
            var width = Console.WindowWidth;
            if (width > 1 && line.Length >= width) line = line[..(width - 1)];
            _output.Write("\r\u001b[2K" + line);
            _output.Flush();
        }
        else
            _output.WriteLine(line);
        _lastOutput = elapsed;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_inline) _output.WriteLine();
        }
        _timer.Dispose();
    }
}
