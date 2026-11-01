using PCL.Core.Shared.Authentication;
using PCL.Core.Shared.Java;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"])
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "java" when args.Length == 2 && args[1] == "scan" => ScanJava(),
                "java" when args.Length == 3 && args[1] == "install" && int.TryParse(args[2], out var major) => await InstallJavaAsync(major).ConfigureAwait(false),
                "login" when args.Length == 1 => await LoginAsync().ConfigureAwait(false),
                "logout" when args.Length == 1 => await LogoutAsync().ConfigureAwait(false),
                "instance" when args.Length >= 2 && args[1] == "list" => ListInstances(args.Skip(2).ToArray()),
                "install" when args.Length >= 2 => await InstallAsync(args[1], args.Skip(2).ToArray()).ConfigureAwait(false),
                "launch" when args.Length >= 2 => await LaunchAsync(args[1], args.Skip(2).ToArray()).ConfigureAwait(false),
                _ => InvalidCommand()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException or TimeoutException or NotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("PCL Linux CLI (early development)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  pcl java scan");
        Console.WriteLine("  pcl java install <major-version>");
        Console.WriteLine("  pcl login");
        Console.WriteLine("  pcl logout");
        Console.WriteLine("  pcl instance list [--game-dir <path>]");
        Console.WriteLine("  pcl install <version|latest> [--game-dir <path>]");
        Console.WriteLine("  pcl launch <version> [--game-dir <path>] [--java <path>]");
        Console.WriteLine("  pcl --help");
        Console.WriteLine();
        Console.WriteLine("Microsoft login requires PCL_MS_CLIENT_ID and stores its refresh token with owner-only file permissions.");
    }

    private static int InvalidCommand()
    {
        Console.Error.WriteLine("Unknown or invalid command. Run 'pcl --help' for usage.");
        return 2;
    }

    private static int ScanJava()
    {
        var found = JavaDiscovery.Scan();
        if (found.Count == 0)
        {
            Console.WriteLine("No usable Java executable found. Install Java or add it to PATH.");
            return 1;
        }

        foreach (var runtime in found)
            Console.WriteLine($"Java {runtime.Version}\t{runtime.ExecutablePath}");
        return 0;
    }

    private static int ListInstances(string[] args)
    {
        if (!TryParseOptions(args, out var options)) return 2;
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var instances = MinecraftInstanceService.List(gameDirectory);
        if (instances.Count == 0)
        {
            Console.WriteLine($"No installed versions found in {Path.Combine(gameDirectory, "versions")}");
            return 0;
        }
        foreach (var instance in instances) Console.WriteLine(instance.Id);
        return 0;
    }

    private static async Task<int> InstallJavaAsync(int majorVersion)
    {
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory();
        var installer = new MinecraftInstaller();
        Console.WriteLine($"Installing Mojang Java {majorVersion} runtime to {gameDirectory}");
        var path = await installer.InstallJavaRuntimeAsync(majorVersion, gameDirectory, new ConsoleDownloadProgress()).ConfigureAwait(false);
        Console.WriteLine($"Installed Java runtime: {path}");
        return 0;
    }

    private static async Task<int> LoginAsync()
    {
        var auth = new MicrosoftMinecraftAuth();
        var (prompt, complete) = await auth.BeginDeviceLoginAsync().ConfigureAwait(false);
        Console.WriteLine($"Open {prompt.VerificationUri}");
        Console.WriteLine($"Enter code: {prompt.UserCode}");
        Console.WriteLine("Waiting for Microsoft authorization...");
        var account = await complete(CancellationToken.None).ConfigureAwait(false);
        await MinecraftAccountStore.SaveAsync(new StoredMinecraftAccount(account.UserName, account.Uuid, account.RefreshToken)).ConfigureAwait(false);
        Console.WriteLine($"Logged in as {account.UserName}.");
        return 0;
    }

    private static async Task<int> LogoutAsync()
    {
        await MinecraftAccountStore.DeleteAsync().ConfigureAwait(false);
        Console.WriteLine("Logged out.");
        return 0;
    }

    private static async Task<int> InstallAsync(string version, string[] args)
    {
        if (!TryParseOptions(args, out var options)) return 2;
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var installer = new MinecraftInstaller();
        Console.WriteLine($"Installing Minecraft {version} to {gameDirectory}");
        var result = await installer.InstallAsync(version, gameDirectory, new ConsoleDownloadProgress()).ConfigureAwait(false);
        Console.WriteLine($"Installed {result.VersionId}.");
        return 0;
    }

    private static async Task<int> LaunchAsync(string version, string[] args)
    {
        if (!TryParseOptions(args, out var options)) return 2;
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var installer = new MinecraftInstaller();
        var installedPath = Path.Combine(gameDirectory, "versions", version, version + ".json");
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase) || !File.Exists(installedPath))
        {
            Console.WriteLine($"Minecraft {version} is not installed. Installing it now...");
            var installed = await installer.InstallAsync(version, gameDirectory, new ConsoleDownloadProgress()).ConfigureAwait(false);
            version = installed.VersionId;
        }
        var requiredMajor = await installer.GetRequiredJavaMajorAsync(version, gameDirectory).ConfigureAwait(false);
        var requestedJavaPath = options.GetValueOrDefault("java");
        var matchingJava = requestedJavaPath is null
            ? JavaDiscovery.Scan().FirstOrDefault(runtime => JavaDiscovery.ParseMajorVersion(runtime.Version) == requiredMajor)
            : JavaDiscovery.Probe(requestedJavaPath);
        if (requestedJavaPath is not null && matchingJava is null)
        {
            Console.Error.WriteLine($"Java executable was not found or is not runnable: {requestedJavaPath}");
            return 2;
        }
        if (matchingJava is not null && JavaDiscovery.ParseMajorVersion(matchingJava.Version) != requiredMajor)
        {
            Console.Error.WriteLine($"Minecraft {version} requires Java {requiredMajor}; selected Java is {matchingJava.Version}.");
            return 2;
        }

        var javaPath = matchingJava?.ExecutablePath;
        if (javaPath is null)
        {
            Console.WriteLine($"No Java {requiredMajor} runtime found. Installing Mojang's Linux runtime...");
            javaPath = await installer.InstallJavaRuntimeAsync(requiredMajor, gameDirectory, new ConsoleDownloadProgress()).ConfigureAwait(false);
        }

        var storedAccount = await MinecraftAccountStore.LoadAsync().ConfigureAwait(false);
        if (storedAccount is null)
        {
            Console.Error.WriteLine("No Microsoft account configured. Run 'pcl login' first.");
            return 1;
        }
        var auth = new MicrosoftMinecraftAuth();
        var account = await auth.RefreshAsync(new MinecraftAccount(storedAccount.UserName, storedAccount.Uuid, string.Empty, storedAccount.RefreshToken, DateTimeOffset.MinValue)).ConfigureAwait(false);
        await MinecraftAccountStore.SaveAsync(new StoredMinecraftAccount(account.UserName, account.Uuid, account.RefreshToken)).ConfigureAwait(false);
        var process = await installer.LaunchAsync(version, new MinecraftLaunchOptions(gameDirectory, javaPath, account.UserName, account.Uuid, account.AccessToken)).ConfigureAwait(false);
        Console.WriteLine($"Started Minecraft {version} as {account.UserName} with Java at {javaPath}.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Options must be name/value pairs.");
                return false;
            }
            var key = args[i][2..];
            if (key is not ("game-dir" or "java" or "username") || !options.TryAdd(key, args[i + 1]))
            {
                Console.Error.WriteLine($"Unknown or duplicate option: {args[i]}");
                return false;
            }
        }
        return true;
    }

    private static bool IsValidPlayerName(string name)
        => name.Length is >= 3 and <= 16 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string OfflineUuid(string playerName)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    private sealed class ConsoleDownloadProgress : IProgress<DownloadProgress>
    {
        private int _lastPercent = -1;
        public void Report(DownloadProgress value)
        {
            if (value.Total == 0) return;
            var percent = value.Completed * 100 / value.Total;
            if (percent / 5 <= _lastPercent / 5 && percent != 100) return;
            _lastPercent = percent;
            Console.WriteLine($"Download {percent,3}% ({value.Completed}/{value.Total})");
        }
    }
}
