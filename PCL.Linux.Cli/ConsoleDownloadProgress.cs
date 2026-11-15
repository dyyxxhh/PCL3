using System.Globalization;

namespace PCL.Linux.Cli;

/// <summary>Byte-based update progress; redirected output is throttled and contains no terminal escapes.</summary>
internal sealed class ConsoleDownloadProgress : IDisposable
{
    private readonly TextWriter _output;
    private readonly bool _interactive;
    private readonly long? _total;
    private readonly TimeProvider _time;
    private readonly long _started;
    private TimeSpan _lastOutput;
    private bool _disposed;

    internal ConsoleDownloadProgress(long? total, TextWriter output, bool interactive, TimeProvider? timeProvider = null)
    {
        _total = total is > 0 ? total : null;
        _output = output;
        _interactive = interactive;
        _time = timeProvider ?? TimeProvider.System;
        _started = _time.GetTimestamp();
        Render(0);
    }

    internal void Report(long downloaded, bool completed = false)
    {
        if (_disposed) return;
        var elapsed = _time.GetElapsedTime(_started);
        if (completed || elapsed - _lastOutput >= TimeSpan.FromMilliseconds(_interactive ? 100 : 2000))
            Render(downloaded);
    }

    private void Render(long downloaded)
    {
        var elapsed = _time.GetElapsedTime(_started);
        var speed = elapsed.TotalSeconds > 0 ? downloaded / elapsed.TotalSeconds : 0;
        const double mib = 1024 * 1024;
        var amount = (downloaded / mib).ToString("0.00", CultureInfo.InvariantCulture);
        var rate = (speed / mib).ToString("0.00", CultureInfo.InvariantCulture);
        string line;
        if (_total is { } total)
        {
            var fraction = Math.Clamp((double)downloaded / total, 0, 1);
            var percent = (int)(fraction * 100);
            var filled = percent / 5;
            var bar = new string('#', filled) + new string('-', 20 - filled);
            var eta = downloaded >= total ? "0s" : speed > 0
                ? Math.Ceiling((total - downloaded) / speed).ToString("0", CultureInfo.InvariantCulture) + "s" : "--";
            line = FormattableString.Invariant($"MCM [{bar}] {percent,3}% | {amount}/{total / mib:0.00} MiB | {rate} MiB/s | ETA {eta}");
        }
        else
            line = $"MCM [downloading] {amount} MiB | {rate} MiB/s | total unknown";
        if (_interactive)
            _output.Write("\r\u001b[2K" + line);
        else
            _output.WriteLine(line);
        _output.Flush();
        _lastOutput = elapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_interactive) _output.WriteLine();
    }
}
