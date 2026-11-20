using System.Diagnostics;
using PCL.Linux.Cli;

await MaximizerContracts.RunAsync();

internal static class MaximizerContracts
{
    public static async Task RunAsync()
    {
        await MaximizesOnlyMappedManagedNormalWindowsForTheGameAndHandlesHandoffOnce();
        await ExitsCleanlyWhenTheGameExitsBeforeShowingAWindow();
        Console.WriteLine("Linux maximize window-selection and fail-open contracts: PASS");
    }

    private static async Task MaximizesOnlyMappedManagedNormalWindowsForTheGameAndHandlesHandoffOnce()
    {
        using var game = StartSleep();
        using var cancel = new CancellationTokenSource();
        using var manager = new FakeWindowManager();
        manager.SetWindows(
            new(100, game.Id + 42, true, true, false, false, true, false), // another game's window
            new(101, game.Id, true, true, true, false, true, false), // transient warning dialog
            new(102, game.Id, false, true, false, false, true, false), // hidden while minimized
            new(103, game.Id, true, false, false, false, true, false), // not managed by a WM
            new(104, game.Id, true, true, false, true, true, false), // override-redirect surface
            new(105, game.Id, true, true, false, false, false, false)); // splash, not a normal client

        var monitor = LinuxGameWindowMaximizer.MonitorProcessAsync(game, manager, TimeSpan.FromMilliseconds(10), cancel.Token);
        await Task.Delay(50);
        Check(manager.Requests.Count == 0, "splash and ineligible windows must not be maximized");

        manager.SetWindows(new LinuxGameWindow(200, game.Id, true, true, false, false, true, false)); // handoff main GLFW window
        await manager.WaitForRequestAsync(200);
        Check(manager.Requests.SequenceEqual(new ulong[] { 200 }), "only the matching game's main normal window should be requested");
        Check(manager.Windows.Single().IsMaximized, "a successful WM should confirm both maximize atoms");

        manager.SetWindows(new LinuxGameWindow(200, game.Id, true, true, false, false, true, false)); // user manually restores it
        await Task.Delay(50);
        Check(manager.Requests.SequenceEqual(new ulong[] { 200 }), "restoring a window must not trigger another maximize request");
        manager.SetWindows(new LinuxGameWindow(200, game.Id, true, true, false, false, true, false),
                           new LinuxGameWindow(201, game.Id, true, true, true, false, true, false), // later transient still ignored
                           new LinuxGameWindow(202, game.Id, true, true, false, false, false, false), // another splash still ignored
                           new LinuxGameWindow(203, game.Id, true, true, false, false, true, false)); // replacement main after Forge handoff
        await manager.WaitForRequestAsync(203);
        await Task.Delay(50);
        Check(manager.Requests.SequenceEqual(new ulong[] { 200, 203 }), "only the replacement main window should be maximized after manual restore");

        game.Kill();
        await game.WaitForExitAsync();
        await monitor.WaitAsync(TimeSpan.FromSeconds(1));
        cancel.Cancel();
    }

    private static async Task ExitsCleanlyWhenTheGameExitsBeforeShowingAWindow()
    {
        using var game = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start the short-lived process fixture.");
        await game.WaitForExitAsync();
        using var manager = new FakeWindowManager();
        await LinuxGameWindowMaximizer.MonitorProcessAsync(game, manager, TimeSpan.FromMilliseconds(5), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Check(manager.Requests.Count == 0, "an exited game must be left alone");
    }

    private static Process StartSleep() => Process.Start(new ProcessStartInfo("/usr/bin/sleep")
    {
        UseShellExecute = false,
        ArgumentList = { "30" }
    }) ?? throw new InvalidOperationException("Could not start the sleep process fixture.");

    private static void Check(bool result, string description)
    {
        if (!result) throw new InvalidOperationException(description);
    }

    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly object _gate = new();
        private readonly List<LinuxGameWindow> _windows = [];
        private readonly List<ulong> _requests = [];
        private readonly SemaphoreSlim _changed = new(0);
        public IReadOnlyList<ulong> Requests { get { lock (_gate) return _requests.ToArray(); } }
        public IReadOnlyList<LinuxGameWindow> Windows { get { lock (_gate) return _windows.ToArray(); } }

        public IReadOnlyList<LinuxGameWindow> GetWindows(int processId)
        {
            lock (_gate) return _windows.ToArray();
        }

        public void RequestMaximize(ulong windowId, int processId)
        {
            lock (_gate)
            {
                _requests.Add(windowId);
                var index = _windows.FindIndex(window => window.Id == windowId);
                if (index >= 0) _windows[index] = _windows[index] with { IsMaximized = true };
            }
            _changed.Release();
        }

        public void SetWindows(params LinuxGameWindow[] windows)
        {
            lock (_gate)
            {
                _windows.Clear();
                _windows.AddRange(windows);
            }
        }

        public async Task WaitForRequestAsync(ulong windowId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (!Requests.Contains(windowId)) await _changed.WaitAsync(timeout.Token);
        }

        public void Dispose() => _changed.Dispose();
    }
}
