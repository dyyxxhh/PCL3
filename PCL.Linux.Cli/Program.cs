using PCL.Core.Shared.Authentication;
using PCL.Core.Shared.Java;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var packageNetwork = PackageCommands.Network;
        ApplyEnvironmentAliases();
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
                "info" when args is ["info"] => McmInfo.Write(Console.Out),
                "update" when args is ["update"] => await LinuxSelfUpdater.UpdateAsync().ConfigureAwait(false),
                "skill" when args.Length >= 2 && args[1] == "install" => await SkillCommands.InstallAsync(args.Skip(2).ToArray()).ConfigureAwait(false),
                "config" => McmConfiguration.Run(args.Skip(1).ToArray()),
                "backup" => await RunBackupCommandAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "tor" => await TorCommands.RunAsync(args[1..]).ConfigureAwait(false),
                "scan" => await InstanceCommands.ScanAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "instance" when args.Length >= 2 && args[1] == "list" => ListInstances(args.Skip(2).ToArray()),
                "-Ss" => await PackageCommands.SearchAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "-S" => await PackageCommands.InstallAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "-R" => await PackageCommands.ManageModsAsync(["remove", .. args.Skip(1)]).ConfigureAwait(false),
                "mod" => await PackageCommands.ManageModsAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "pack" => await PackageCommands.ManagePackAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "offline" => await OfflineCommands.RunAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "launch" => await LaunchConfiguredAsync(args[1..]).ConfigureAwait(false),
                _ => InvalidCommand()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or ArgumentException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException or TimeoutException or NotSupportedException or System.Security.Cryptography.CryptographicException or System.Net.HttpListenerException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("MCM Linux CLI (early development)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  mcm java scan");
        Console.WriteLine("  mcm java install <major-version>");
        Console.WriteLine("  mcm login [microsoft]");
        Console.WriteLine("  mcm login offline <玩家名>");
        Console.WriteLine("  mcm login authlib <验证服务器URL> <用户名>");
        Console.WriteLine("  mcm logout");
        Console.WriteLine("  mcm info");
        Console.WriteLine("  mcm update");
        Console.WriteLine("  mcm skill install");
        Console.WriteLine("  mcm config [list|get <key>|set <key> <value>|reset [key]]");
        Console.WriteLine("  mcm backup create|list|restore <backup> [--instance <name>] [--clone]");
        Console.WriteLine("  mcm tor status|bridge list|bridge add <bridge-line>|bridge clear");
        Console.WriteLine("  mcm scan [path] [--migrate] [--version <id>] [--instance <name>] [--data-dir <path>] [--game-dir <destination>]");
        Console.WriteLine("  mcm instance list [--game-dir <path>]");
        Console.WriteLine("  mcm -Ss <mod query> [--game-version <version>] [--loader <loader>] [--source modrinth|curseforge]");
        Console.WriteLine("  mcm -S <Minecraft version|mod name> [--instance <name>] [--loader fabric|quilt|forge|neoforge] [--loader-version <version>] [--mod-version <version>] [--game-dir <path>]");
        Console.WriteLine("  mcm -R <file.jar> --instance <name> [--game-dir <path>]");
        Console.WriteLine("  mcm mod <list|update|enable|disable|remove> [file.jar] --instance <name> [--game-dir <path>]");
        Console.WriteLine("  mcm pack <import|export> <file.mrpack|file.zip> --instance <name> [--game-dir <path>]");
        Console.WriteLine("  mcm offline export <bundle.zip> --instance <name> [--game-dir <path>] [--java <portable-java-home>]");
        Console.WriteLine("  mcm offline import <bundle.zip> [--instance <name>] [--game-dir <path>]");
        Console.WriteLine("  mcm offline inspect <bundle.zip>");
        Console.WriteLine("  mcm offline serve <bundle.zip> [--bind <local-IP>] [--port <5078>]");
        Console.WriteLine("  mcm offline doctor --instance <name> [--game-dir <path>]");
        Console.WriteLine("  mcm launch <instance|version> [--game-dir <path>] [--java <path>] [--memory <MiB|auto>] [--offline]");
        Console.WriteLine("  mcm --help");
        Console.WriteLine();
        Console.WriteLine("Microsoft login requires MCM_MS_CLIENT_ID and stores its refresh token with owner-only file permissions.");
    }

    private static void ApplyEnvironmentAliases()
    {
        // Shared services still consume legacy PCL_* keys; prefer the MCM_* CLI names.
        CopyEnvironmentVariable("MCM_GAME_DIR", "PCL_GAME_DIR");
        CopyEnvironmentVariable("MCM_MS_CLIENT_ID", "PCL_MS_CLIENT_ID");

        static void CopyEnvironmentVariable(string source, string destination)
        {
            var value = Environment.GetEnvironmentVariable(source);
            if (!string.IsNullOrWhiteSpace(value)) Environment.SetEnvironmentVariable(destination, value);
        }
    }

    private static int InvalidCommand()
    {
        Console.Error.WriteLine("Unknown or invalid command. Run 'mcm --help' for usage.");
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
        var gameDirectory = McmConfiguration.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var instances = ManagedInstanceService.List(gameDirectory);
        var legacyInstances = MinecraftInstanceService.List(gameDirectory);
        if (instances.Count == 0 && legacyInstances.Count == 0)
        {
            Console.WriteLine($"No installed instances found in {gameDirectory}");
            return 0;
        }
        foreach (var instance in instances)
            Console.WriteLine($"{instance.Name}\t{instance.GameVersion}\t{instance.Loader ?? "vanilla"}");
        foreach (var instance in legacyInstances)
            Console.WriteLine($"{instance.Id}\tlegacy");
        return 0;
    }

    private static async Task<int> InstallJavaAsync(int majorVersion)
    {
        var gameDirectory = McmConfiguration.ResolveGameDirectory();
        using var network = new TorFallbackHttpMessageHandler();
        using var resources = network.CreateClient();
        var installer = network.CreateInstaller(resources);
        var existing = installer.FindInstalledJavaRuntime(majorVersion, gameDirectory);
        if (existing is not null)
        {
            Console.WriteLine($"Using installed Java {majorVersion} runtime: {existing}");
            return 0;
        }
        Console.WriteLine($"Installing Mojang Java {majorVersion} runtime to {gameDirectory}");
        string path;
        using (var progress = new ConsoleInstallProgress($"Preparing Java {majorVersion} installation"))
            path = await installer.InstallJavaRuntimeAsync(majorVersion, gameDirectory, progress).ConfigureAwait(false);
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

    private static Task<int> LaunchConfiguredAsync(string[] args)
    {
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)) return LaunchAsync(args[0], args[1..]);
        var configured = McmConfiguration.Get("default_instance");
        if (string.IsNullOrWhiteSpace(configured)) throw new ArgumentException("Specify a version or set default_instance first.");
        return LaunchAsync(configured, args);
    }

    private static async Task<int> LaunchAsync(string version, string[] args)
    {
        if (!TryParseOptions(args, out var options, allowOffline: true)) return 2;
        var offline = options.ContainsKey("offline");
        var gameDirectory = McmConfiguration.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var managed = ManagedInstanceService.Find(gameDirectory, version);
        var maxHeapMiB = McmConfiguration.ResolveMaxHeapMiB(
            options.GetValueOrDefault("memory") ?? McmConfiguration.Get("memory.max_mib", managed?.Name),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        if (managed is not null)
        {
            gameDirectory = managed.DirectoryPath;
            version = managed.LaunchVersion;
            await CreateAutomaticBackupIfEnabledAsync(managed).ConfigureAwait(false);
        }
        using var network = new TorFallbackHttpMessageHandler(managed?.Name);
        using var resources = network.CreateClient();
        var installer = network.CreateInstaller(resources);
        var installedPath = Path.Combine(gameDirectory, "versions", version, version + ".json");
        if (managed is null && InstanceCommands.RequiresLegacyMigration(gameDirectory, version))
        {
            Console.Error.WriteLine("This legacy PCL version was found but requires migration before MCM can launch it.");
            Console.Error.WriteLine($"Run: mcm scan \"{gameDirectory}\" --migrate --version \"{version}\" --instance ImportedGame --game-dir \"{gameDirectory}\"");
            return 2;
        }
        if (managed is not null && !File.Exists(installedPath))
            throw new InvalidDataException($"Instance '{managed.Name}' is incomplete. Reinstall it with mcm -S {managed.GameVersion} --instance {managed.Name}.");
        if (version.Equals("latest", StringComparison.OrdinalIgnoreCase) || !File.Exists(installedPath))
        {
            if (offline)
            {
                Console.Error.WriteLine("Offline launch requires a locally installed, explicit version; no download was attempted.");
                return 1;
            }
            Console.WriteLine($"Minecraft {version} is not installed. Installing it now...");
            using var progress = new ConsoleInstallProgress("Preparing Minecraft installation");
            var installed = await installer.InstallAsync(version, gameDirectory, progress).ConfigureAwait(false);
            version = installed.VersionId;
        }
        if (offline)
        {
            using var progress = new ConsoleInstallProgress("Checking local files for offline launch");
            await installer.ValidateOfflineInstallationAsync(version, gameDirectory, progress).ConfigureAwait(false);
        }
        else if (managed?.Imported == true)
        {
            using var progress = new ConsoleInstallProgress("Checking imported instance libraries for Linux");
            await installer.PrepareForLaunchAsync(version, gameDirectory, progress).ConfigureAwait(false);
        }
        var requiredMajor = await installer.GetRequiredJavaMajorAsync(version, gameDirectory).ConfigureAwait(false);
        var requestedJavaPath = options.GetValueOrDefault("java") ?? McmConfiguration.Get("java", managed?.Name);
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
            if (offline)
            {
                Console.Error.WriteLine($"Offline launch requires a local Java {requiredMajor} runtime; no download was attempted. Reimport a complete offline bundle or select --java <path>.");
                return 1;
            }
            Console.WriteLine($"No Java {requiredMajor} runtime found. Installing Mojang's Linux runtime...");
            using var progress = new ConsoleInstallProgress($"Preparing Java {requiredMajor} installation");
            javaPath = await installer.InstallJavaRuntimeAsync(requiredMajor, gameDirectory, progress).ConfigureAwait(false);
        }

        var profile = await LauncherProfileStore.LoadAsync().ConfigureAwait(false);
        if (offline && profile?.Type != "offline")
        {
            Console.Error.WriteLine("Offline launch never refreshes online accounts. First select an offline account with 'mcm login offline <name>'.");
            return 1;
        }
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
                Console.Error.WriteLine("请先运行 mcm login、mcm login offline 或 mcm login authlib。");
                return 1;
            }
            var account = await new MicrosoftMinecraftAuth().RefreshAsync(new MinecraftAccount(storedAccount.UserName, storedAccount.Uuid, string.Empty, storedAccount.RefreshToken, DateTimeOffset.MinValue)).ConfigureAwait(false);
            await MinecraftAccountStore.SaveAsync(new StoredMinecraftAccount(account.UserName, account.Uuid, account.RefreshToken)).ConfigureAwait(false);
            launchOptions = new MinecraftLaunchOptions(gameDirectory, javaPath, account.UserName, account.Uuid, account.AccessToken, "msa");
        }
        else throw new InvalidDataException("未知账户类型，请重新登录。");
        launchOptions = launchOptions with { LauncherName = "MCM", MaxHeapMiB = maxHeapMiB };
        using var process = await installer.LaunchAsync(version, launchOptions).ConfigureAwait(false);
        Console.WriteLine($"Started Minecraft {version} as {launchOptions.PlayerName} with Java at {javaPath} (max heap: {maxHeapMiB} MiB).");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task<int> RunBackupCommandAsync(string[] args)
    {
        if (args.Length == 0) return BackupUsage();
        if (args[0] == "list" && args.Length == 2)
        {
            var instance = ManagedInstanceService.Find(McmConfiguration.ResolveGameDirectory(), args[1]);
            if (instance is null) return BackupUsage($"Instance '{args[1]}' does not exist.");
            foreach (var backup in InstanceBackupService.List(instance)) Console.WriteLine(backup);
            return 0;
        }
        if (args[0] == "create" && args.Length is 2 or 4)
        {
            var gameDirectory = McmConfiguration.ResolveGameDirectory();
            var instance = ManagedInstanceService.Find(gameDirectory, args[1]);
            if (instance is null) return BackupUsage($"Instance '{args[1]}' does not exist.");
            var scope = args.Length == 4 && args[2] == "--scope" ? args[3] : null;
            if (args.Length == 4 && scope is null) return BackupUsage("Usage: mcm backup create <instance> [--scope saves|instance]");
            Console.WriteLine($"Creating backup for '{instance.Name}'...");
            Console.WriteLine(await InstanceBackupService.CreateAsync(instance, false, scope).ConfigureAwait(false));
            return 0;
        }
        if (args[0] == "restore" && args.Length >= 4)
        {
            var instanceIndex = Array.IndexOf(args, "--instance");
            if (instanceIndex != 2 || instanceIndex + 1 >= args.Length) return BackupUsage("Usage: mcm backup restore <backup> --instance <name> [--clone]");
            var clone = args.Skip(4).Contains("--clone", StringComparer.Ordinal);
            if (args.Skip(4).Any(item => item != "--clone")) return BackupUsage();
            var instanceName = args[3];
            var gameDirectory = McmConfiguration.ResolveGameDirectory();
            var instance = ManagedInstanceService.Find(gameDirectory, instanceName);
            if (instance is null) return BackupUsage($"Instance '{instanceName}' does not exist.");
            var restored = await InstanceBackupService.RestoreAsync(instance, args[1], clone).ConfigureAwait(false);
            Console.WriteLine(clone ? $"Backup restored as new instance '{restored}'." : "Backup restored in place; the previous state was preserved.");
            return 0;
        }
        return BackupUsage();
    }

    private static int BackupUsage(string? message = null)
    {
        if (message is not null) Console.Error.WriteLine(message);
        else Console.Error.WriteLine("Usage: mcm backup create <instance> [--scope saves|instance] | list <instance> | restore <backup> --instance <name> [--clone]");
        return 2;
    }

    private static async Task CreateAutomaticBackupIfEnabledAsync(ManagedMinecraftInstance instance)
    {
        if (!bool.TryParse(McmConfiguration.Get("backup.enabled", instance.Name), out var enabled) || !enabled) return;
        while (true)
        {
            try
            {
                var path = await InstanceBackupService.CreateAsync(instance, true).ConfigureAwait(false);
                Console.WriteLine($"Created automatic backup: {path}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (Console.IsInputRedirected) throw new IOException($"Automatic backup failed; launch was blocked: {ex.Message}", ex);
                Console.Error.WriteLine($"Automatic backup failed: {ex.Message}");
                Console.Write("[R]etry, [C]ontinue without backup, or [Q]uit? ");
                switch (Console.ReadLine()?.Trim().ToLowerInvariant())
                {
                    case "r": continue;
                    case "c": return;
                    default: throw new IOException("Launch cancelled because the automatic backup did not complete.");
                }
            }
        }
    }

    private static bool TryParseOptions(string[] args, out Dictionary<string, string> options, bool allowOffline = false)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (allowOffline && args[i] == "--offline")
            {
                if (options.TryAdd("offline", "true")) continue;
                Console.Error.WriteLine("Duplicate option: --offline");
                return false;
            }
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Options must be name/value pairs (except launch --offline).");
                return false;
            }
            var key = args[i][2..];
            if ((key is not ("game-dir" or "java" or "username") && !(allowOffline && key == "memory"))
                || !options.TryAdd(key, args[i + 1]))
            {
                Console.Error.WriteLine($"Unknown or duplicate option: {args[i]}");
                return false;
            }
            i++;
        }
        return true;
    }
}
