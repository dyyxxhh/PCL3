using System.Text.RegularExpressions;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class PackageCommands
{
    internal static readonly TorFallbackHttpMessageHandler Network = new();
    private static readonly HttpClient Http = Network.CreateClient();
    private static readonly Regex MinecraftVersion = new(
        "^(?:latest|[0-9]+\\.[0-9]+(?:\\.[0-9]+)?(?:-(?:pre|rc)[0-9]+)?|[0-9]{2}w[0-9]{2}[a-z])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async Task<int> SearchAsync(string[] arguments)
    {
        if (!Parse(arguments, ["game-dir", "game-version", "loader", "source"], out var terms, out var options) || terms.Length == 0)
            return Usage("Usage: mcm -Ss <mod search terms> [--game-version <version>] [--loader <loader>] [--source modrinth|curseforge]");
        var source = GetSource(options.GetValueOrDefault("source"));
        if (source == ModSource.CurseForge && string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey))
            return Usage("CurseForge is unavailable in this build; use a release built with the CurseForge key.");
        var platform = new ModPlatformService(Http, LinuxSecrets.CurseForgeApiKey);
        var matches = await platform.SearchModsAsync(string.Join(' ', terms), options.GetValueOrDefault("game-version"), options.GetValueOrDefault("loader")).ConfigureAwait(false);
        foreach (var item in matches.Where(item => source is null || source == item.Source))
            Console.WriteLine($"{item.Source.ToString().ToLowerInvariant()}:{item.Id}\t{item.Name}\t{item.Description}");
        if (string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey) && source != ModSource.Modrinth)
            Console.Error.WriteLine("CurseForge results are unavailable in this build; Modrinth results are still shown.");
        return 0;
    }

    internal static async Task<int> InstallAsync(string[] arguments)
    {
        if (!Parse(arguments, ["game-dir", "instance", "loader", "loader-version", "source", "mod-version"], out var terms, out var options) || terms.Length != 1)
            return Usage("Usage: mcm -S <Minecraft version|mod name> [--instance <name>] [--loader forge|neoforge|fabric|quilt] [--game-dir <path>]");
        var target = terms[0];
        var gameDirectory = McmConfiguration.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        if (options.ContainsKey("loader-version") && !options.ContainsKey("loader"))
            return Usage("--loader-version requires --loader.");
        if (MinecraftVersion.IsMatch(target)) return await InstallGameAsync(target, gameDirectory, options).ConfigureAwait(false);
        if (options.ContainsKey("loader") || options.ContainsKey("loader-version"))
            return Usage("--loader and --loader-version require a Minecraft version target; choose a mod instance with --instance.");
        return await InstallModAsync(target, gameDirectory, options).ConfigureAwait(false);
    }

    private static async Task<int> InstallGameAsync(string version, string gameDirectory, Dictionary<string, string> options)
    {
        var name = options.GetValueOrDefault("instance") ?? version;
        var directory = ManagedInstanceService.GetInstanceDirectory(gameDirectory, name);
        var previous = ManagedInstanceService.Find(gameDirectory, name);
        if (previous is not null && (previous.GameVersion != version && version != "latest" || previous.Loader != options.GetValueOrDefault("loader")))
            return Usage($"Instance '{name}' already has a different Minecraft version or loader; use another --instance name.");
        var loader = options.GetValueOrDefault("loader");
        if (loader is not null && loader is not ("forge" or "neoforge" or "fabric" or "quilt"))
            return Usage("--loader must be forge, neoforge, fabric, or quilt.");
        Console.WriteLine($"Installing Minecraft {version} to instance '{name}'...");
        var installer = Network.CreateInstaller(Http);
        MinecraftInstallResult result;
        string launchVersion;
        string? loaderVersion = null;
        using (var progress = new ConsoleInstallProgress("Preparing Minecraft installation"))
        {
            result = await installer.InstallAsync(version, directory, progress).ConfigureAwait(false);
            launchVersion = result.VersionId;
            if (loader is not null)
            {
                progress.SetStage($"Installing {loader} loader (downloading libraries / running installer)",
                    inline: loader is not ("forge" or "neoforge"));
                var installed = await new ModLoaderInstaller(Http).InstallAsync(result.VersionId, loader,
                    options.GetValueOrDefault("loader-version"), directory).ConfigureAwait(false);
                launchVersion = installed.LaunchVersion;
                loaderVersion = installed.LoaderVersion;
            }
            progress.SetStage("Saving instance");
            await ManagedInstanceService.SaveAsync(new ManagedMinecraftInstance(name, result.VersionId, loader, loaderVersion, directory, launchVersion)).ConfigureAwait(false);
        }
        Console.WriteLine($"Installed '{name}' ({result.VersionId}{(loader is null ? "" : $" + {loader} {loaderVersion}")}).");
        return 0;
    }

    private static async Task<int> InstallModAsync(string query, string gameDirectory, Dictionary<string, string> options)
    {
        var instances = ManagedInstanceService.List(gameDirectory);
        if (!options.ContainsKey("instance") && Console.IsInputRedirected)
            return Usage("Non-interactive mod installation requires --instance <name>.");
        var instance = options.GetValueOrDefault("instance") is { } name
            ? ManagedInstanceService.Find(gameDirectory, name)
            : Select(instances, i => $"{i.Name} ({i.GameVersion} {i.Loader ?? "vanilla"})", "Select the destination instance");
        if (instance is null)
        {
            if (options.ContainsKey("instance")) return Usage("The requested instance does not exist. Install it first using mcm -S <Minecraft version> --loader <loader> --instance <name>.");
            if (Console.IsInputRedirected) return Usage("No instance selected. Provide --instance <name> (or create an instance first with mcm -S <Minecraft version> --loader <loader>).");
            Console.Write("No instance found. Minecraft version for a new instance (e.g. 1.21.1): ");
            var version = Console.ReadLine()?.Trim();
            Console.Write("Loader (fabric/forge/neoforge/quilt): ");
            var loader = Console.ReadLine()?.Trim().ToLowerInvariant();
            Console.Write("Instance name (Enter for version-loader): ");
            var chosenName = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(version) || !MinecraftVersion.IsMatch(version) || loader is not ("fabric" or "forge" or "neoforge" or "quilt"))
                return Usage("A valid Minecraft version and one of the four supported loaders are required.");
            if (string.IsNullOrWhiteSpace(chosenName)) chosenName = version + "-" + loader;
            var createOptions = new Dictionary<string, string>(StringComparer.Ordinal) { ["instance"] = chosenName, ["loader"] = loader };
            if (await InstallGameAsync(version, gameDirectory, createOptions).ConfigureAwait(false) != 0) return 2;
            instance = ManagedInstanceService.Find(gameDirectory, chosenName);
        }
        if (instance?.Loader is null) return Usage("This instance has no mod loader. Create one with mcm -S <Minecraft version> --loader <loader> --instance <name>.");

        var source = GetSource(options.GetValueOrDefault("source"));
        if (source == ModSource.CurseForge && string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey))
            return Usage("CurseForge is unavailable in this build; use a release built with the CurseForge key.");
        using var network = new TorFallbackHttpMessageHandler(instance.Name);
        using var resources = network.CreateClient();
        var platform = new ModPlatformService(resources, LinuxSecrets.CurseForgeApiKey);
        ModProject[] matches;
        using (var progress = new ConsoleInstallProgress($"Searching compatible mods for {query}"))
            matches = (await platform.SearchModsAsync(query, instance.GameVersion, instance.Loader).ConfigureAwait(false))
                .Where(item => source is null || item.Source == source).ToArray();
        var exact = matches.Where(item => item.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
            || item.Slug.Equals(query, StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = exact.Length == 1 ? exact[0] : Select(exact.Length > 0 ? exact : matches,
            item => $"{item.Source.ToString().ToLowerInvariant()}:{item.Id} {item.Name}", "Select the mod to install");
        if (selected is null) return Usage("No unambiguous compatible mod found. Search with mcm -Ss <name>, or specify --source and --instance.");
        if (!await InstallModTreeAsync(selected, options.GetValueOrDefault("mod-version"), instance, platform).ConfigureAwait(false)) return 1;
        Console.WriteLine($"Installed {selected.Name} to instance '{instance.Name}'.");
        return 0;
    }

    private static async Task<bool> InstallModTreeAsync(ModProject project, string? requestedVersion, ManagedMinecraftInstance instance,
        ModPlatformService platform)
    {
        var root = await platform.GetLatestCompatibleReleaseAsync(project, instance.GameVersion, instance.Loader!,
            requestedVersion: requestedVersion).ConfigureAwait(false);
        return await InstallRequiredAsync([root], instance, platform, release => release == root ? project.Name : release.ProjectId).ConfigureAwait(false);
    }

    private static async Task<bool> InstallRequiredAsync(IReadOnlyList<ModRelease> roots, ManagedMinecraftInstance instance,
        ModPlatformService platform, Func<ModRelease, string> name)
    {
        var installed = InstalledModService.List(instance.DirectoryPath);
        IReadOnlyList<ModRelease> plan;
        try
        {
            plan = await RequiredModPlanner.PlanAsync(roots, installed, dependency =>
                ResolveReleaseAsync(dependency.Source, dependency.ProjectId, dependency.VersionId, instance, platform)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (Console.IsInputRedirected) return false;
            Console.Write("[A]djust dependencies, [C]ancel, or install [M]ain mod(s) only? ");
            var choice = Console.ReadLine()?.Trim();
            if (string.Equals(choice, "a", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("Enable or correct the required dependency in this instance, then run the installation again.");
            if (!string.Equals(choice, "m", StringComparison.OrdinalIgnoreCase)) return false;
            plan = roots;
        }
        foreach (var release in plan)
        {
            if (installed.Any(mod => mod.Source == release.Source && mod.ProjectId == release.ProjectId && mod.ReleaseId == release.VersionId)) continue;
            using var progress = new ConsoleInstallProgress($"Downloading and verifying {name(release)}");
            await InstalledModService.InstallReleaseAsync(instance.DirectoryPath, release, name(release), platform).ConfigureAwait(false);
        }
        return true;
    }

    private static Task<ModRelease> ResolveReleaseAsync(ModSource source, string id, string? version,
        ManagedMinecraftInstance instance, ModPlatformService platform) => platform.GetLatestCompatibleReleaseAsync(
        new ModProject(source, id, id, id, "", 0, null), instance.GameVersion, instance.Loader!, requestedVersion: version);

    internal static async Task<int> ManagePackAsync(string[] arguments)
    {
        if (!Parse(arguments, ["instance", "game-dir"], out var terms, out var options)
            || terms.Length != 2 || terms[0] is not ("import" or "export"))
            return Usage("Usage: mcm pack <import|export> <file.mrpack|file.zip> --instance <name> [--game-dir <path>]");
        var gameDirectory = McmConfiguration.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var path = Path.GetFullPath(terms[1]);
        using var http = new HttpClient(new TorFallbackHttpMessageHandler()) { Timeout = TimeSpan.FromMinutes(10) };
        var service = new ModpackService(http, LinuxSecrets.CurseForgeApiKey);
        if (terms[0] == "export")
        {
            if (!options.TryGetValue("instance", out var requestedName))
                return Usage("Pack export requires --instance <name>.");
            var existing = ManagedInstanceService.Find(gameDirectory, requestedName);
            if (existing is null) return Usage("The requested instance does not exist.");
            if (path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                await service.ExportModrinthAsync(existing, path).ConfigureAwait(false);
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                await service.ExportCurseForgeAsync(existing, path).ConfigureAwait(false);
            else return Usage("Export as .mrpack (Modrinth) or .zip (CurseForge).");
            Console.WriteLine($"Exported {requestedName} to {path}");
            return 0;
        }
        var plan = service.ReadPlan(path);
        string? name = options.GetValueOrDefault("instance");
        if (name is null)
        {
            if (Console.IsInputRedirected) return Usage("Non-interactive pack import requires --instance <name>.");
            Console.Write($"Instance name for '{plan.Name}': ");
            name = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(name)) return Usage("Instance name is required.");
        }
        var directory = ManagedInstanceService.GetInstanceDirectory(gameDirectory, name);
        if (Directory.Exists(directory)) return Usage($"Instance directory '{name}' already exists; choose a new --instance name.");
        if (plan.Loader is not null && plan.Loader is not ("forge" or "neoforge" or "fabric" or "quilt"))
            return Usage($"This pack requires an unsupported loader: {plan.Loader}");
        if (plan.Files.Any(file => file.CurseForgeProject is not null) && string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey))
            return Usage("CurseForge pack import requires a release built with the CurseForge key.");
        InstanceBackupStore.RequireSafePath(directory);
        var staging = Path.Combine(Path.GetDirectoryName(directory)!, ".migration-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var network = new TorFallbackHttpMessageHandler(name);
            using var resources = network.CreateClient();
            var pack = new ModpackService(resources, LinuxSecrets.CurseForgeApiKey);
            using var progress = new ConsoleInstallProgress("Preparing modpack installation");
            var minecraft = await network.CreateInstaller(resources).InstallAsync(plan.MinecraftVersion, staging, progress).ConfigureAwait(false);
            string launchVersion = minecraft.VersionId;
            string? loaderVersion = null;
            if (plan.Loader is not null)
            {
                progress.SetStage($"Installing {plan.Loader} loader (downloading libraries / running installer)",
                    inline: plan.Loader is not ("forge" or "neoforge"));
                var result = await new ModLoaderInstaller(resources).InstallAsync(minecraft.VersionId, plan.Loader, plan.LoaderVersion, staging).ConfigureAwait(false);
                launchVersion = result.LaunchVersion;
                loaderVersion = result.LoaderVersion;
            }
            progress.SetStage("Downloading modpack files and extracting overrides");
            await pack.InstallFilesAsync(path, plan, staging).ConfigureAwait(false);
            var instance = new ManagedMinecraftInstance(name, minecraft.VersionId, plan.Loader, loaderVersion, directory, launchVersion);
            if (instance.Loader is not null)
            {
                var mods = InstalledModService.List(staging).Where(mod => !mod.Disabled && mod.ProjectId.Length > 0).ToArray();
                var platform = new ModPlatformService(resources, LinuxSecrets.CurseForgeApiKey);
                var roots = new List<ModRelease>();
                foreach (var mod in mods)
                    roots.Add(await ResolveReleaseAsync(mod.Source, mod.ProjectId, mod.ReleaseId, instance, platform).ConfigureAwait(false));
                if (!await InstallRequiredAsync(roots, instance with { DirectoryPath = staging }, platform, release => release.ProjectId).ConfigureAwait(false)) return 1;
            }
            await ManagedInstanceService.PublishStagedAsync(instance, staging).ConfigureAwait(false);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        Console.WriteLine($"Imported '{plan.Name}' as instance '{name}'.");
        return 0;
    }

    internal static async Task<int> ManageModsAsync(string[] arguments)
    {
        if (!Parse(arguments, ["instance", "game-dir"], out var terms, out var options)
            || terms.Length is < 1 or > 2 || terms[0] is not ("list" or "update" or "enable" or "disable" or "remove"))
            return Usage("Usage: mcm mod <list|update|enable|disable|remove> [file.jar] --instance <name> [--game-dir <path>]");
        if (!options.TryGetValue("instance", out var name))
            return Usage("Mod management requires --instance <name> (also when running without a terminal).");
        var gameDirectory = McmConfiguration.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var instance = ManagedInstanceService.Find(gameDirectory, name);
        if (instance is null) return Usage($"Instance '{name}' does not exist.");
        var mods = InstalledModService.List(instance.DirectoryPath);
        if (terms[0] == "list")
        {
            if (terms.Length != 1) return Usage("Usage: mcm mod list --instance <name>");
            foreach (var mod in mods) Console.WriteLine($"{mod.FileName}\t{(mod.Disabled ? "disabled" : "enabled")}\t{mod.ProjectName}");
            return 0;
        }
        if (terms.Length != 2) return Usage($"Usage: mcm mod {terms[0]} <file.jar> --instance <name>");
        var selected = mods.FirstOrDefault(item => item.FileName == terms[1]);
        if (selected is null) return Usage($"Mod '{terms[1]}' was not found in instance '{name}'.");
        switch (terms[0])
        {
            case "enable":
                await InstalledModService.SetDisabledAsync(instance.DirectoryPath, selected.FileName, false).ConfigureAwait(false);
                break;
            case "disable":
                await InstalledModService.SetDisabledAsync(instance.DirectoryPath, selected.FileName, true).ConfigureAwait(false);
                break;
            case "remove":
                await InstalledModService.RemoveAsync(instance.DirectoryPath, selected.FileName).ConfigureAwait(false);
                break;
            case "update":
            {
                if (string.IsNullOrEmpty(selected.ProjectId) || instance.Loader is null)
                    return Usage("This mod has no tracked project ID or its instance has no loader; update it by installing from a known project.");
                using var network = new TorFallbackHttpMessageHandler(instance.Name);
                using var resources = network.CreateClient();
                var platform = new ModPlatformService(resources, LinuxSecrets.CurseForgeApiKey);
                var project = new ModProject(selected.Source, selected.ProjectId, "", selected.ProjectName, "", 0, null);
                if (!await InstallModTreeAsync(project, null, instance, platform).ConfigureAwait(false)) return 1;
                break;
            }
        }
        Console.WriteLine($"{terms[0]} complete: {selected.FileName}");
        return 0;
    }

    private static T? Select<T>(IReadOnlyList<T> values, Func<T, string> describe, string prompt) where T : class
    {
        if (values.Count == 0) return null;
        if (values.Count == 1) return values[0];
        if (Console.IsInputRedirected) return null;
        Console.WriteLine(prompt + ":");
        for (var i = 0; i < values.Count; i++) Console.WriteLine($"  {i + 1}. {describe(values[i])}");
        Console.Write("Choice (number, or Enter to cancel): ");
        var input = Console.ReadLine();
        return int.TryParse(input, out var number) && number >= 1 && number <= values.Count ? values[number - 1] : null;
    }

    private static ModSource? GetSource(string? source) => source?.ToLowerInvariant() switch
    {
        null => null,
        "modrinth" => ModSource.Modrinth,
        "curseforge" => ModSource.CurseForge,
        _ => throw new ArgumentException("--source must be modrinth or curseforge.", nameof(source))
    };

    private static bool Parse(string[] args, string[] allowed, out string[] terms, out Dictionary<string, string> options)
    {
        terms = [];
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (options.Count > 0) return Fail("Positional arguments must precede options.");
                positional.Add(args[i]);
                continue;
            }
            var name = args[i][2..];
            if (!allowed.Contains(name, StringComparer.Ordinal) || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(name, args[i + 1]))
                return Fail($"Invalid or duplicate option: {args[i]}");
            i++;
        }
        terms = positional.ToArray();
        return true;

        bool Fail(string message)
        {
            Console.Error.WriteLine(message);
            return false;
        }
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
