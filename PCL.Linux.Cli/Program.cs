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
                "login" when args.Length == 1 || args is ["login", "microsoft"] => await LoginAsync().ConfigureAwait(false),
                "login" when args is ["login", "offline", _] => await LoginOfflineAsync(args[2]).ConfigureAwait(false),
                "login" when args is ["login", "authlib", _, _] => await LoginAuthlibAsync(args[2], args[3]).ConfigureAwait(false),
                "logout" when args.Length == 1 => await LogoutAsync().ConfigureAwait(false),
                "update" when args is ["update"] => await LinuxSelfUpdater.UpdateAsync().ConfigureAwait(false),
                "instance" when args.Length >= 2 && args[1] == "list" => ListInstances(args.Skip(2).ToArray()),
                "install" when args.Length >= 2 => await InstallAsync(args[1], args.Skip(2).ToArray()).ConfigureAwait(false),
                "launch" when args.Length >= 2 => await LaunchAsync(args[1], args.Skip(2).ToArray()).ConfigureAwait(false),
                _ => InvalidCommand()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or ArgumentException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException or TimeoutException or NotSupportedException or System.Security.Cryptography.CryptographicException)
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
        Console.WriteLine("  pcl login [microsoft]");
        Console.WriteLine("  pcl login offline <玩家名>");
        Console.WriteLine("  pcl login authlib <验证服务器URL> <用户名>");
        Console.WriteLine("  pcl logout");
        Console.WriteLine("  pcl update");
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
        var existing = installer.FindInstalledJavaRuntime(majorVersion, gameDirectory);
        if (existing is not null)
        {
            Console.WriteLine($"Using installed Java {majorVersion} runtime: {existing}");
            return 0;
        }
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
        await LauncherProfileStore.SaveAsync(new LauncherProfile("microsoft", account.UserName, account.Uuid)).ConfigureAwait(false);
        Console.WriteLine($"Logged in as {account.UserName}.");
        return 0;
    }

    private static async Task<int> LoginOfflineAsync(string name)
    {
        var profile = OfflineAuthentication.Create(name);
        await LauncherProfileStore.SaveAsync(profile).ConfigureAwait(false);
        Console.WriteLine($"已选择离线账户：{profile.UserName} ({profile.Uuid})");
        return 0;
    }

    private static async Task<int> LoginAuthlibAsync(string server, string username)
    {
        YggdrasilAuthentication.NormalizeServer(server);
        if (Console.IsInputRedirected) throw new InvalidOperationException("第三方登录需要交互式终端输入密码。");
        Console.Write("密码（不回显）：");
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
        Console.WriteLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var profile = await new YggdrasilAuthentication(client).AuthenticateAsync(server, username, password.ToString()).ConfigureAwait(false);
        password.Clear();
        await LauncherProfileStore.SaveAsync(profile).ConfigureAwait(false);
        Console.WriteLine($"第三方登录成功：{profile.UserName}；令牌保存在仅当前用户可读的配置文件中。");
        return 0;
    }

    private static async Task<int> LogoutAsync()
    {
        LauncherProfileStore.Delete();
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
            ? JavaDiscovery.Probe(installer.FindInstalledJavaRuntime(requiredMajor, gameDirectory) ?? string.Empty)
              ?? JavaDiscovery.Scan().FirstOrDefault(runtime => JavaDiscovery.ParseMajorVersion(runtime.Version) == requiredMajor)
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

        var profile = await LauncherProfileStore.LoadAsync().ConfigureAwait(false);
        MinecraftLaunchOptions launchOptions;
        if (profile?.Type == "offline")
            launchOptions = new MinecraftLaunchOptions(gameDirectory, javaPath, profile.UserName, profile.Uuid);
        else if (profile?.Type == "yggdrasil")
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            profile = await new YggdrasilAuthentication(client).RefreshAsync(profile).ConfigureAwait(false);
            await LauncherProfileStore.SaveAsync(profile).ConfigureAwait(false);
            var agent = await AuthlibInjector.EnsureAsync(gameDirectory).ConfigureAwait(false);
            launchOptions = new MinecraftLaunchOptions(gameDirectory, javaPath, profile.UserName, profile.Uuid, profile.AccessToken!, "legacy", agent, profile.Server);
        }
        else if (profile is null || profile.Type == "microsoft")
        {
            var storedAccount = await MinecraftAccountStore.LoadAsync().ConfigureAwait(false);
            if (storedAccount is null)
            {
                Console.Error.WriteLine("请先运行 pcl login、pcl login offline 或 pcl login authlib。");
                return 1;
            }
            var account = await new MicrosoftMinecraftAuth().RefreshAsync(new MinecraftAccount(storedAccount.UserName, storedAccount.Uuid, string.Empty, storedAccount.RefreshToken, DateTimeOffset.MinValue)).ConfigureAwait(false);
            await MinecraftAccountStore.SaveAsync(new StoredMinecraftAccount(account.UserName, account.Uuid, account.RefreshToken)).ConfigureAwait(false);
            launchOptions = new MinecraftLaunchOptions(gameDirectory, javaPath, account.UserName, account.Uuid, account.AccessToken, "msa");
        }
        else throw new InvalidDataException("未知账户类型，请重新登录。");
        var process = await installer.LaunchAsync(version, launchOptions).ConfigureAwait(false);
        Console.WriteLine($"Started Minecraft {version} as {launchOptions.PlayerName} with Java at {javaPath}.");
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
