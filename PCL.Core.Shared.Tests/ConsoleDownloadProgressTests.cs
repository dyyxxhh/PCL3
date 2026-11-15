using PCL.Linux.Cli;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class ConsoleDownloadProgressTests
{
    [TestMethod]
    public void KnownLengthShowsBarSpeedEtaAndCompletionWithoutTerminalEscapesInLogs()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleDownloadProgress(4 * 1024 * 1024, output, false, time);
        StringAssert.Contains(output.ToString(), "0%");
        time.Advance(TimeSpan.FromSeconds(2));
        progress.Report(2 * 1024 * 1024);
        StringAssert.Contains(output.ToString(), "[##########----------]  50%");
        StringAssert.Contains(output.ToString(), "2.00/4.00 MiB | 1.00 MiB/s | ETA 2s");
        progress.Report(4 * 1024 * 1024, completed: true);
        StringAssert.Contains(output.ToString(), "[####################] 100%");
        Assert.IsFalse(output.ToString().Contains('\r'));
        Assert.IsFalse(output.ToString().Contains('\u001b'));
    }

    [TestMethod]
    public void UnknownLengthShowsDownloadedBytesWithoutInventingPercentOrEta()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleDownloadProgress(null, output, false, time);
        time.Advance(TimeSpan.FromSeconds(2));
        progress.Report(1024 * 1024, completed: true);
        StringAssert.Contains(output.ToString(), "1.00 MiB | 0.50 MiB/s | total unknown");
        Assert.IsFalse(output.ToString().Contains('%'));
        Assert.IsFalse(output.ToString().Contains("ETA"));
    }

    [TestMethod]
    public void InteractiveProgressIsThrottledAndFailureEndsTheLineWithoutClaimingCompletion()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        var progress = new ConsoleDownloadProgress(100, output, true, time);
        var initial = output.ToString();
        progress.Report(10);
        Assert.AreEqual(initial, output.ToString());
        time.Advance(TimeSpan.FromMilliseconds(100));
        progress.Report(25);
        StringAssert.Contains(output.ToString(), "25%");
        StringAssert.Contains(output.ToString(), "\r\u001b[2K");
        Assert.IsFalse(output.ToString().Contains('\n'));
        progress.Dispose();
        StringAssert.EndsWith(output.ToString(), Environment.NewLine);
        Assert.IsFalse(output.ToString().Contains("100%"));
        var final = output.ToString();
        progress.Report(100, completed: true);
        progress.Dispose();
        Assert.AreEqual(final, output.ToString());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
