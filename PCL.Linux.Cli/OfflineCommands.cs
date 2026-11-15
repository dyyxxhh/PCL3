using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Reflection;
using PCL.Core.Shared.Java;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class OfflineCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        // Serving owns its long-lived cancellation handler. Other commands need
        // cooperative cancellation too, otherwise Ctrl+C bypasses staging cleanup.
        if (args.FirstOrDefault() == "serve") return await RunCoreAsync(args, CancellationToken.None).ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return await RunCoreAsync(args, cancel.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("Offline operation cancelled; temporary files were cleaned up.");
            return 130;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static async Task<int> RunCoreAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args is ["--help"] or ["-h"] or ["help"]) { Help(); return 0; }
        if (args.Length == 0) return Usage("Choose export, import, inspect, serve or doctor.");
        var allowed = args[0] switch
        {
            "export" => new[] { "instance", "game-dir", "java" },
            "import" => ["instance", "game-dir"],
            "inspect" => [],
            "serve" => ["bind", "port"],
            "doctor" => ["instance", "game-dir"],
            _ => null
        };
        if (allowed is null || !Parse(args[1..], allowed, out var terms, out var options)) return Usage("Invalid offline command/options.");
        switch (args[0])
        {
            case "export" when terms.Count == 1 && options.TryGetValue("instance", out var name):
            {
                var root = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
                var instance = ManagedInstanceService.Find(root, name) ?? throw new InvalidOperationException("Install or migrate the named instance before exporting it.");
                var installer = new MinecraftInstaller();
                var major = await installer.GetRequiredJavaMajorAsync(instance.LaunchVersion, instance.DirectoryPath, cancellationToken).ConfigureAwait(false);
                var java = options.GetValueOrDefault("java") ?? installer.FindInstalledJavaRuntime(major, instance.DirectoryPath)
                    ?? installer.FindInstalledJavaRuntime(major, root)
                    ?? throw new InvalidOperationException($"Java {major} must be bundled. First run 'mcm java install {major}' in the same MCM_GAME_DIR, or supply --java <portable-java-home>. Export will not download anything.");
                var javaHome = ResolveJavaHome(java);
                var launcher = GetSingleFileLauncher();
                OfflineBundleManifest manifest;
                using (var progress = new ConsoleInstallProgress("Preparing complete offline bundle"))
                    manifest = await OfflineBundleService.ExportAsync(instance, javaHome, launcher, terms[0], progress, cancellationToken).ConfigureAwait(false);
                PrintManifest(manifest);
                Console.WriteLine($"Offline bundle: {Path.GetFullPath(terms[0])}");
                Console.WriteLine("Includes complete Java and self-contained MCM. No accounts, tokens, saves or logs are included. Review mod configs for private data before sharing.");
                return 0;
            }
            case "import" when terms.Count == 1:
            {
                var root = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
                ManagedMinecraftInstance instance;
                using (var progress = new ConsoleInstallProgress("Verifying and installing offline bundle (no network)"))
                    instance = await OfflineBundleService.ImportAsync(terms[0], root, options.GetValueOrDefault("instance"), progress, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Installed offline instance '{instance.Name}' at {instance.DirectoryPath}");
                Console.WriteLine("Select an offline account with 'mcm login offline <name>', then use 'mcm launch <instance> --offline'.");
                return 0;
            }
            case "inspect" when terms.Count == 1:
            {
                using var progress = new ConsoleInstallProgress("Verifying offline bundle without installing");
                var manifest = await OfflineBundleService.VerifyAsync(terms[0], progress, cancellationToken).ConfigureAwait(false);
                PrintManifest(manifest);
                Console.WriteLine("All payload SHA-256 checks passed. Checksums detect corruption, not an untrusted publisher.");
                return 0;
            }
            case "serve" when terms.Count == 1:
                return await ServeAsync(terms[0], options).ConfigureAwait(false);
            case "doctor" when terms.Count == 0 && options.TryGetValue("instance", out var doctorName):
                return await DoctorAsync(MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir")), doctorName, cancellationToken).ConfigureAwait(false);
            default: return Usage("Invalid offline command arguments; export and doctor require --instance.");
        }
    }

    private static string ResolveJavaHome(string value)
    {
        var path = Path.GetFullPath(value);
        if (Directory.Exists(path)) return path;
        if (!File.Exists(path)) throw new FileNotFoundException("Portable Java home or executable was not found.", path);
        path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        var bin = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(bin) != "bin") throw new ArgumentException("--java must point to a complete Java home or its bin/java executable.");
        return Path.GetDirectoryName(bin)!;
    }

    private static string GetSingleFileLauncher()
    {
        // A framework-dependent single-file executable is NOT a complete offline bootstrap.
        var metadata = typeof(OfflineCommands).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var complete = new[] { "McmOfflineSelfContained", "McmOfflineSingleFile", "McmOfflineNativeBundle" }
            .All(key => metadata.GetValueOrDefault(key)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
        // Location being empty is exactly the single-file deployment property being checked here.
#pragma warning disable IL3000
        if (!complete || !string.IsNullOrEmpty(typeof(OfflineCommands).Assembly.Location))
#pragma warning restore IL3000
            throw new NotSupportedException("Complete offline export requires a self-contained single-file MCM release, not 'dotnet run'. Publish with --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true.");
        return Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running MCM executable.");
    }

    private static async Task<int> ServeAsync(string bundle, Dictionary<string, string> options)
    {
        if (!IPAddress.TryParse(options.GetValueOrDefault("bind", "0.0.0.0"), out var bind)) return Usage("--bind requires a numeric local IP address.");
        if (!int.TryParse(options.GetValueOrDefault("port", "5078"), out var port) || port is < 1 or > 65535) return Usage("--port must be between 1 and 65535.");
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            OfflineInstallServer server;
            using (var progress = new ConsoleInstallProgress("Verifying complete offline bundle before serving"))
                server = await OfflineInstallServer.StartAsync(bundle, bind, port, progress, cancel.Token).ConfigureAwait(false);
            await using (server)
            {
                Console.WriteLine("Offline installation server is ready. Only use it on a trusted LAN; HTTP is not authenticated or encrypted.");
                Console.WriteLine("No uploads, directory browsing, account files or internet proxy are served. Keep the source bundle unchanged while serving.");
                var addresses = bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any)
                    ? NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Select(address => address.Address)
                        .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && OfflineInstallServer.IsLanAddress(address))
                        .Distinct().ToArray()
                    : [bind];
                foreach (var address in addresses.OrderBy(address => IPAddress.IsLoopback(address)))
                {
                    var url = new UriBuilder("http", address.ToString(), port).Uri.AbsoluteUri;
                    Console.WriteLine($"  curl -fS --noproxy '*' {url} | bash");
                }
                Console.WriteLine("Client needs Linux x86_64/glibc, bash, curl and coreutils, but no Java, .NET, MCM or internet.");
                Console.WriteLine("Ctrl+C stops the server.");
                await server.RunAsync(cancel.Token).ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return 0; }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static async Task<int> DoctorAsync(string root, string name, CancellationToken cancellationToken)
    {
        var instance = ManagedInstanceService.Find(root, name) ?? throw new InvalidOperationException("Instance not found.");
        var okay = true;
        var installer = new MinecraftInstaller();
        try
        {
            await installer.ValidateOfflineInstallationAsync(instance.LaunchVersion, instance.DirectoryPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            Console.WriteLine("[OK] Local game files, libraries and assets verified (no network).");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.WriteLine($"[FAIL] {ex.Message}");
            okay = false;
        }
        var major = await installer.GetRequiredJavaMajorAsync(instance.LaunchVersion, instance.DirectoryPath, cancellationToken).ConfigureAwait(false);
        var java = installer.FindInstalledJavaRuntime(major, instance.DirectoryPath);
        Console.WriteLine(java is not null ? $"[OK] Bundled Java {major}: {java}" : $"[FAIL] No runnable bundled Java {major}; check system ABI/libraries.");
        okay &= java is not null;
        if (OperatingSystem.IsLinux())
        {
            var gl = HasLibrary("libGL.so.1");
            var x11 = HasLibrary("libX11.so.6");
            var wayland = HasLibrary("libwayland-client.so.0");
            var display = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            Console.WriteLine(gl ? "[OK] libGL loads (this does NOT prove GPU/OpenGL version compatibility)." : "[FAIL] libGL cannot load; install the correct distribution GPU/OpenGL packages offline.");
            Console.WriteLine($"[{(display && (x11 || wayland) ? "OK" : "FAIL")}] Desktop display: {(display ? "set" : "not set")}; X11: {x11}; Wayland: {wayland}. Older Minecraft/LWJGL may require XWayland.");
            Console.WriteLine(HasLibrary("libasound.so.2") ? "[OK] ALSA library loads." : "[WARN] ALSA library missing; audio may be unavailable.");
            okay &= gl && display && (x11 || wayland);
        }
        Console.WriteLine("GPU drivers must match the TARGET GPU, distribution and kernel; never copy the source computer's driver libraries. Use the target distribution's offline installation media/package cache.");
        Console.WriteLine("This check cannot create a graphics context or certify a full Minecraft launch. Some mods also require their own online services.");
        return okay ? 0 : 1;
    }

    private static bool HasLibrary(string name)
    {
        if (!NativeLibrary.TryLoad(name, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static void PrintManifest(OfflineBundleManifest manifest)
    {
        Console.WriteLine($"Instance: {manifest.Instance.Name}; Minecraft {manifest.Instance.GameVersion}; {manifest.Instance.Loader ?? "vanilla"} {manifest.Instance.LoaderVersion}");
        Console.WriteLine($"Target: {manifest.TargetRuntime}; Java {manifest.JavaMajorVersion}; includes self-contained MCM");
        Console.WriteLine($"Files: {manifest.Files.Count}; uncompressed bytes: {manifest.Files.Sum(file => file.Size)}");
    }

    private static bool Parse(string[] args, string[] allowed, out List<string> terms, out Dictionary<string, string> options)
    {
        terms = [];
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-')) { terms.Add(args[i]); continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..], StringComparer.Ordinal)
                || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i][2..], args[++i])) return false;
        }
        return true;
    }

    private static int Usage(string error) { Console.Error.WriteLine(error); Help(); return 2; }
    private static void Help()
    {
        Console.WriteLine("mcm offline export <bundle.zip> --instance <name> [--game-dir <path>] [--java <portable-java-home>]");
        Console.WriteLine("mcm offline import <bundle.zip> [--instance <new-name>] [--game-dir <path>]");
        Console.WriteLine("mcm offline inspect <bundle.zip>");
        Console.WriteLine("mcm offline serve <bundle.zip> [--bind <local-IP>] [--port <5078>]");
        Console.WriteLine("mcm offline doctor --instance <name> [--game-dir <path>]");
    }
}
