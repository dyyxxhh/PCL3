using PCL.Core.Shared.Minecraft;
using PCL.Linux.Cli;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class ConsoleInstallProgressTests
{
    [TestMethod]
    public void RedirectedOutputReportsActivityWhileNoFilesFinish()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleInstallProgress("Fetching manifest", output, false, time);
        StringAssert.Contains(output.ToString(), "Fetching manifest | 0.0s");
        var initial = output.ToString();

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(initial, output.ToString());
        time.Advance(TimeSpan.FromSeconds(1));
        StringAssert.Contains(output.ToString(), "Fetching manifest | 5.0s");
        Assert.IsFalse(output.ToString().Contains('\r'));
        Assert.IsFalse(output.ToString().Contains('\u001b'));
    }

    [TestMethod]
    public void DownloadsAreThrottledButCompletionAndNewStagesAppearImmediately()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleInstallProgress("Preparing", output, false, time);
        progress.Report(new DownloadProgress(0, 100, "/downloads/client.jar"));
        var started = output.ToString();
        for (var completed = 1; completed < 100; completed++)
            progress.Report(new DownloadProgress(completed, 100, "/downloads/client.jar"));
        Assert.AreEqual(started, output.ToString());

        time.Advance(TimeSpan.FromSeconds(5));
        StringAssert.Contains(output.ToString(), "99% (99/100 files)");
        StringAssert.Contains(output.ToString(), "client.jar");
        Assert.IsFalse(output.ToString().Contains("/downloads/"));
        progress.Report(new DownloadProgress(100, 100, "/downloads/client.jar"));
        StringAssert.Contains(output.ToString(), "100% (100/100 files)");

        progress.SetStage("Extracting libraries");
        var lastLine = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
        StringAssert.Contains(lastLine, "Extracting libraries");
        Assert.IsFalse(lastLine.Contains('%'));
        Assert.IsFalse(lastLine.Contains("client.jar"));
    }

    [TestMethod]
    public void AssetIndexCompletionDoesNotHideStartOfMainDownloads()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleInstallProgress("Preparing", output, false, time);
        progress.Report(new DownloadProgress(1, 1, "index.json") { Stage = "Downloading asset index" });
        progress.Report(new DownloadProgress(0, 200, ""));
        var lastLine = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[^1];
        StringAssert.Contains(lastLine, "Downloading files");
        StringAssert.Contains(lastLine, "0% (0/200 files)");
    }

    [TestMethod]
    public void InteractiveOutputRefreshesOneLineAndSanitizesRemoteNames()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleInstallProgress("Preparing", output, true, time);
        progress.Report(new DownloadProgress(0, 10, "bad\n\u001b[31m.jar"));
        var initial = output.ToString();
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.IsTrue(output.ToString().Length > initial.Length);
        StringAssert.Contains(output.ToString(), "\r\u001b[2K");
        Assert.IsFalse(output.ToString().Contains("\u001b[31m"));
        Assert.IsFalse(output.ToString().Contains('\n'));
    }

    [TestMethod]
    public void ExternalInstallerStagePreservesItsLogs()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        using var progress = new ConsoleInstallProgress("Downloading files", output, true, time);
        progress.SetStage("Running Forge installer", inline: false);
        output.GetStringBuilder().Clear();
        output.WriteLine("Installer log");
        time.Advance(TimeSpan.FromSeconds(5));
        StringAssert.Contains(output.ToString(), "Installer log" + Environment.NewLine);
        StringAssert.Contains(output.ToString(), "Running Forge installer | 5.0s");
        Assert.IsFalse(output.ToString().Contains('\r'));
        Assert.IsFalse(output.ToString().Contains('\u001b'));

        output.GetStringBuilder().Clear();
        progress.SetStage("Saving instance");
        StringAssert.Contains(output.ToString(), "\r\u001b[2K");
    }

    [TestMethod]
    public void DisposingAfterFailureStopsLateCallbacksAndLeavesErrorsOnANewLine()
    {
        using var output = new StringWriter();
        var time = new ManualTimeProvider();
        var progress = new ConsoleInstallProgress("Downloading files", output, true, time);
        try
        {
            using (progress)
                throw new IOException("Mock download failure");
        }
        catch (IOException)
        {
            output.WriteLine("Error: Mock download failure");
        }
        var final = output.ToString();
        StringAssert.Contains(final, Environment.NewLine + "Error: Mock download failure");
        Assert.IsTrue(time.TimerDisposed);
        time.Advance(TimeSpan.FromSeconds(10));
        progress.Report(new DownloadProgress(1, 1, "late.jar"));
        progress.SetStage("Late stage");
        progress.Dispose();
        Assert.AreEqual(final, output.ToString());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private ManualTimer? _timer;
        public bool TimerDisposed => _timer?.Disposed == true;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _timer = new ManualTimer(callback, state);

        public void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            // Also simulate a callback already queued when Dispose is called.
            _timer?.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
