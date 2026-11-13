using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

internal static class InstanceCommands
{
    internal static async Task<int> ScanAsync(string[] args)
    {
        string? sourcePath = null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var migrate = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--migrate")
            {
                if (migrate) return Usage("Duplicate --migrate option.");
                migrate = true;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                if (key is not ("game-dir" or "version" or "instance" or "data-dir") || i + 1 == args.Length
                    || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(key, args[++i]))
                    return Usage("Invalid scan option. Usage: pcl scan [path] [--migrate] [--version <id>] [--instance <name>] [--data-dir <path>] [--game-dir <destination>]");
            }
            else if (sourcePath is null) sourcePath = args[i];
            else return Usage("scan accepts only one source path.");
        }
        if (!migrate && options.Keys.Any(key => key is "version" or "instance" or "data-dir"))
            return Usage("--version, --instance and --data-dir require --migrate.");
        var destination = MinecraftInstanceService.ResolveGameDirectory(options.GetValueOrDefault("game-dir"));
        IReadOnlyList<string> roots;
        if (sourcePath is not null) roots = [MinecraftInstanceMigration.ResolveScanDirectory(sourcePath)];
        else
        {
            var defaults = MinecraftInstanceService.GetDefaultGameDirectories(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppContext.BaseDirectory);
            roots = new[] { destination }.Concat(defaults).Where(Directory.Exists)
                .Select(MinecraftInstanceMigration.ResolveScanDirectory).Distinct(StringComparer.Ordinal).ToArray();
        }
        Console.WriteLine($"PCL3 game directory: {destination}");
        var candidates = new List<ScannedMinecraftInstance>();
        foreach (var root in roots)
        {
            Console.WriteLine($"Scanning: {root}");
            var result = MinecraftInstanceMigration.Scan(root);
            foreach (var message in result.Diagnostics) Console.Error.WriteLine($"Skipped: {message}");
            foreach (var instance in result.Instances)
            {
                candidates.Add(instance);
                Console.WriteLine($"  {candidates.Count}. {instance.Id}\tMinecraft {instance.GameVersion}\t{instance.Loader ?? "vanilla"} {instance.LoaderVersion}");
                Console.WriteLine($"     Data: {instance.DataDirectory}{(instance.AmbiguousDataDirectory ? " (shared and isolated data both exist; choose during migration)" : "")}");
            }
        }
        if (candidates.Count == 0)
        {
            Console.WriteLine("No compatible versions found. Use pcl scan /path/to/.minecraft to inspect another directory.");
            return migrate ? Usage("No version is available to migrate.") : 0;
        }
        if (!migrate)
        {
            if (Console.IsInputRedirected)
            {
                Console.WriteLine("To copy a version into a PCL3 instance: pcl scan <path> --migrate --version <id> --instance <name>.");
                return 0;
            }
            Console.Write("Migrate a version to a PCL3 instance? Original files are kept. [y/N]: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        ScannedMinecraftInstance? selected;
        if (options.TryGetValue("version", out var requested))
        {
            var matches = candidates.Where(item => item.Id == requested).ToArray();
            if (matches.Length != 1) return Usage("The requested version is missing or ambiguous. Supply an exact source path and --version.");
            selected = matches[0];
        }
        else if (candidates.Count == 1) selected = candidates[0];
        else
        {
            if (Console.IsInputRedirected) return Usage("Non-interactive migration requires --version <id> when multiple versions were found.");
            Console.Write("Version number (Enter to cancel): ");
            var choice = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(choice)) return 0;
            if (!int.TryParse(choice, out var number) || number < 1 || number > candidates.Count) return Usage("Invalid selection.");
            selected = candidates[number - 1];
        }
        var name = options.GetValueOrDefault("instance");
        if (name is null)
        {
            if (Console.IsInputRedirected) return Usage("Non-interactive migration requires --instance <name>.");
            var suggested = Regex.Replace(selected.Id, "[^a-zA-Z0-9._-]", "_").TrimStart('.', '-', '_');
            if (string.IsNullOrEmpty(suggested)) suggested = "imported-instance";
            Console.Write($"PCL3 instance name [{suggested}]: ");
            name = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(name)) name = suggested;
        }
        var data = options.GetValueOrDefault("data-dir");
        if (data is null && selected.AmbiguousDataDirectory)
        {
            if (Console.IsInputRedirected) return Usage("Both data locations contain files. Use --data-dir <source-root-or-version-directory>.");
            Console.WriteLine($"1. Shared data: {selected.GameDirectory}");
            Console.WriteLine($"2. Isolated data: {Path.GetDirectoryName(selected.MetadataPath)}");
            Console.Write("Data to migrate (1/2, Enter to cancel): ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(answer)) return 0;
            if (answer is not ("1" or "2")) return Usage("Invalid data directory selection.");
            data = answer == "1" ? selected.GameDirectory : Path.GetDirectoryName(selected.MetadataPath);
        }
        Console.WriteLine($"Copying '{selected.Id}' to instance '{name}' in {destination}");
        ManagedMinecraftInstance installed;
        using (var progress = new ConsoleInstallProgress("Copying game files, mods, settings and saves"))
            installed = await MinecraftInstanceMigration.MigrateAsync(selected, destination, name, data).ConfigureAwait(false);
        Console.WriteLine($"Migrated '{installed.Name}' (Minecraft {installed.GameVersion}, {installed.Loader ?? "vanilla"}).");
        Console.WriteLine("Original files were kept. Launch with:");
        Console.WriteLine($"  pcl launch {installed.Name} --game-dir \"{destination}\"");
        return 0;
    }

    internal static bool RequiresLegacyMigration(string gameDirectory, string version)
    {
        var legacy = MinecraftInstanceService.List(gameDirectory).FirstOrDefault(instance => instance.Id == version);
        if (legacy is null) return false;
        var metadataPath = Path.Combine(legacy.DirectoryPath, version + ".json");
        if (!File.Exists(metadataPath)) return true;
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath));
        return metadata?["inheritsFrom"] is not null || metadata?["patches"] is not null;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
