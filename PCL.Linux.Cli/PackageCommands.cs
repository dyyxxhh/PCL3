using System.Text.RegularExpressions;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class PackageCommands
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Regex MinecraftVersion = new(
        "^(?:latest|[0-9]+\\.[0-9]+(?:\\.[0-9]+)?(?:-(?:pre|rc)[0-9]+)?|[0-9]{2}w[0-9]{2}[a-z])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async Task<int> SearchAsync(string[] arguments)
    {
        if (!Parse(arguments, ["game-dir", "game-version", "loader", "source"], out var terms, out var options) || terms.Length == 0)
            return Usage("Usage: pcl -Ss <mod search terms> [--game-version <version>] [--loader <loader>] [--source modrinth|curseforge]");
        var source = GetSource(options.GetValueOrDefault("source"));
        if (source == ModSource.CurseForge && string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey))
            return Usage("CurseForge requires PCL_CURSEFORGE_API_KEY configured at build time.");
        var platform = new ModPlatformService(Http, LinuxSecrets.CurseForgeApiKey);
        var matches = await platform.SearchModsAsync(string.Join(' ', terms), options.GetValueOrDefault("game-version"), options.GetValueOrDefault("loader")).ConfigureAwait(false);
        foreach (var item in matches.Where(item => source is null || source == item.Source))
            Console.WriteLine($"{item.Source.ToString().ToLowerInvariant()}:{item.Id}\t{item.Name}\t{item.Description}");
        if (string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey) && source != ModSource.Modrinth)
            Console.Error.WriteLine("CurseForge results unavailable: the release lacks PCL_CURSEFORGE_API_KEY; Modrinth results are still shown.");
        return 0;
    }

    internal static async Task<int> InstallAsync(string[] arguments)
    {
        if (!Parse(arguments, ["game-dir", "instance", "loader", "loader-version", "source", "mod-version"], out var terms, out var options) || terms.Length != 1)
            return Usage("Usage: pcl -S <Minecraft version|mod name> [--instance <name>] [--loader forge|neoforge|fabric|quilt] [--game-dir <path>]");
        var target = terms[0];
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
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
        var installer = new MinecraftInstaller();
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
                var installed = await new ModLoaderInstaller().InstallAsync(result.VersionId, loader,
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
            if (options.ContainsKey("instance")) return Usage("The requested instance does not exist. Install it first using pcl -S <Minecraft version> --loader <loader> --instance <name>.");
            if (Console.IsInputRedirected) return Usage("No instance selected. Provide --instance <name> (or create an instance first with pcl -S <Minecraft version> --loader <loader>).");
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
        if (instance?.Loader is null) return Usage("This instance has no mod loader. Create one with pcl -S <Minecraft version> --loader <loader> --instance <name>.");

        var source = GetSource(options.GetValueOrDefault("source"));
        if (source == ModSource.CurseForge && string.IsNullOrWhiteSpace(LinuxSecrets.CurseForgeApiKey))
            return Usage("CurseForge requires PCL_CURSEFORGE_API_KEY configured at build time.");
        var platform = new ModPlatformService(Http, LinuxSecrets.CurseForgeApiKey);
        ModProject[] matches;
        using (var progress = new ConsoleInstallProgress($"Searching compatible mods for {query}"))
            matches = (await platform.SearchModsAsync(query, instance.GameVersion, instance.Loader).ConfigureAwait(false))
                .Where(item => source is null || item.Source == source).ToArray();
        var exact = matches.Where(item => item.Id.Equals(query, StringComparison.OrdinalIgnoreCase)
            || item.Slug.Equals(query, StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = exact.Length == 1 ? exact[0] : Select(exact.Length > 0 ? exact : matches,
            item => $"{item.Source.ToString().ToLowerInvariant()}:{item.Id} {item.Name}", "Select the mod to install");
        if (selected is null) return Usage("No unambiguous compatible mod found. Search with pcl -Ss <name>, or specify --source and --instance.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        await InstallModTreeAsync(selected, options.GetValueOrDefault("mod-version"), instance, platform, visited, 0).ConfigureAwait(false);
        Console.WriteLine($"Installed {selected.Name} and required dependencies to instance '{instance.Name}'.");
        return 0;
    }

    private static async Task InstallModTreeAsync(ModProject project, string? requestedVersion, ManagedMinecraftInstance instance,
        ModPlatformService platform, HashSet<string> visited, int depth)
    {
        if (depth > 16) throw new InvalidDataException("Mod dependency graph is too deep.");
        var key = project.Source + ":" + project.Id;
        if (!visited.Add(key)) return;
        ModRelease release;
        using (var progress = new ConsoleInstallProgress($"Resolving compatible release for {project.Name}"))
            release = await platform.GetLatestCompatibleReleaseAsync(project, instance.GameVersion, instance.Loader!,
                requestedVersion: requestedVersion).ConfigureAwait(false);
        foreach (var dependency in release.RequiredDependencies ?? [])
        {
            var currentDependency = InstalledModService.List(instance.DirectoryPath)
                .FirstOrDefault(mod => mod.Source == dependency.Source && mod.ProjectId == dependency.ProjectId);
            if (currentDependency is not null && (dependency.VersionId is null || currentDependency.ReleaseId == dependency.VersionId))
                continue;
            var dependencyProject = new ModProject(dependency.Source, dependency.ProjectId, dependency.ProjectId,
                dependency.ProjectId, "Required dependency", 0, null);
            await InstallModTreeAsync(dependencyProject, dependency.VersionId, instance, platform, visited, depth + 1).ConfigureAwait(false);
        }
        using (var progress = new ConsoleInstallProgress($"Downloading and verifying {project.Name}"))
            await InstalledModService.InstallReleaseAsync(instance.DirectoryPath, release, project.Name, platform).ConfigureAwait(false);
    }

    internal static async Task<int> ManagePackAsync(string[] arguments)
    {
        if (!Parse(arguments, ["instance", "game-dir"], out var terms, out var options)
            || terms.Length != 2 || terms[0] is not ("import" or "export"))
            return Usage("Usage: pcl pack <import|export> <file.mrpack|file.zip> --instance <name> [--game-dir <path>]");
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var path = Path.GetFullPath(terms[1]);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
            return Usage("CurseForge pack import requires a release built with PCL_CURSEFORGE_API_KEY.");
        using (var progress = new ConsoleInstallProgress("Preparing modpack installation"))
        {
            var minecraft = await new MinecraftInstaller().InstallAsync(plan.MinecraftVersion, directory, progress).ConfigureAwait(false);
            string launchVersion = minecraft.VersionId;
            string? loaderVersion = null;
            if (plan.Loader is not null)
            {
                progress.SetStage($"Installing {plan.Loader} loader (downloading libraries / running installer)",
                    inline: plan.Loader is not ("forge" or "neoforge"));
                var result = await new ModLoaderInstaller().InstallAsync(minecraft.VersionId, plan.Loader, plan.LoaderVersion, directory).ConfigureAwait(false);
                launchVersion = result.LaunchVersion;
                loaderVersion = result.LoaderVersion;
            }
            progress.SetStage("Downloading modpack files and extracting overrides");
            await service.InstallFilesAsync(path, plan, directory).ConfigureAwait(false);
            progress.SetStage("Saving instance");
            await ManagedInstanceService.SaveAsync(new ManagedMinecraftInstance(name, minecraft.VersionId, plan.Loader, loaderVersion, directory, launchVersion)).ConfigureAwait(false);
        }
        Console.WriteLine($"Imported '{plan.Name}' as instance '{name}'.");
        return 0;
    }

    internal static async Task<int> ManageModsAsync(string[] arguments)
    {
        if (!Parse(arguments, ["instance", "game-dir"], out var terms, out var options)
            || terms.Length is < 1 or > 2 || terms[0] is not ("list" or "update" or "enable" or "disable" or "remove"))
            return Usage("Usage: pcl mod <list|update|enable|disable|remove> [file.jar] --instance <name> [--game-dir <path>]");
        if (!options.TryGetValue("instance", out var name))
            return Usage("Mod management requires --instance <name> (also when running without a terminal).");
        var gameDirectory = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        var instance = ManagedInstanceService.Find(gameDirectory, name);
        if (instance is null) return Usage($"Instance '{name}' does not exist.");
        var mods = InstalledModService.List(instance.DirectoryPath);
        if (terms[0] == "list")
        {
            if (terms.Length != 1) return Usage("Usage: pcl mod list --instance <name>");
            foreach (var mod in mods) Console.WriteLine($"{mod.FileName}\t{(mod.Disabled ? "disabled" : "enabled")}\t{mod.ProjectName}");
            return 0;
        }
        if (terms.Length != 2) return Usage($"Usage: pcl mod {terms[0]} <file.jar> --instance <name>");
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
                if (string.IsNullOrEmpty(selected.ProjectId) || instance.Loader is null)
                    return Usage("This mod has no tracked project ID or its instance has no loader; update it by installing from a known project.");
                var platform = new ModPlatformService(Http, LinuxSecrets.CurseForgeApiKey);
                var project = new ModProject(selected.Source, selected.ProjectId, "", selected.ProjectName, "", 0, null);
                await InstallModTreeAsync(project, null, instance, platform, new HashSet<string>(StringComparer.Ordinal), 0).ConfigureAwait(false);
                break;
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
