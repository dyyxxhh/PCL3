using System.Diagnostics;
using PCL.Core.Shared.Minecraft;
using System.Windows.Input;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.App.IoC;
using PCL.Core.Utils.OS;

namespace PCL;

internal static class Program
{
    private static void ImportOffline(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows offline import requires Windows.");
        string? bundle = null;
        string? gameDirectory = null;
        string? instance = null;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i].ToLowerInvariant();
            if (i + 1 == args.Length)
                throw new ArgumentException($"Missing value for offline-import option: {option}");
            switch (option)
            {
                case "--bundle": bundle = args[++i]; break;
                case "--game-dir": gameDirectory = args[++i]; break;
                case "--instance": instance = args[++i]; break;
                default: throw new ArgumentException($"Unknown offline-import option: {option}");
            }
        }
        if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(gameDirectory))
            throw new ArgumentException("Usage: offline-import --bundle <zip> --game-dir <directory> [--instance <name>]");
        Console.WriteLine("Verifying and importing the offline bundle...");
        var imported = OfflineBundleService.ImportWindowsAsync(bundle, gameDirectory, instance).GetAwaiter().GetResult();
        Console.WriteLine($"Offline instance imported: {imported.Instance.Name}");
    }

    /// <summary>
    /// Program startup point
    /// </summary>
    [STAThread]
    public static void Main()
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (commandLine.Length > 0 && commandLine[0].Equals("offline-import", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ImportOffline(commandLine[1..]);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Offline import failed: {ex.Message}");
                Environment.ExitCode = 1;
            }
            return;
        }
        if (Basics.CommandLineArguments.Contains("--console")) KernelInterop.AllocateConsole();
#if DEBUG
        if (Basics.CommandLineArguments.Contains("--debug"))
        {
            Console.WriteLine("Waiting for debugger...");
            while (!Debugger.IsAttached) Thread.Sleep(50);
        }
#endif
        Console.WriteLine("Welcome to PCL₃!");
        // Preloading tasks
        ApplicationService.Loading = static () =>
        {
            var app = new Application();
            app.InitializeComponent();
            return app;
        };
        MainWindowService.Loading = static () =>
        {
            var form = new FormMain();
            return form;
        };
        // From dotnet/wpf #2393: fix tablet devices broken on .NET Core 3.0+
        _ = Tablet.TabletDevices;
        // Start lifecycle
        Lifecycle.OnInitialize();
    }
}