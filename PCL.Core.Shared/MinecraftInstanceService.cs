namespace PCL.Core.Shared.Minecraft;

public sealed record MinecraftInstance(string Id, string DirectoryPath);

public static class MinecraftInstanceService
{
    public static string ResolveGameDirectory(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var configured = Environment.GetEnvironmentVariable("PCL_GAME_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ResolveDefaultGameDirectory(home, AppContext.BaseDirectory);
    }

    public static string ResolveDefaultGameDirectory(string homeDirectory, string executableDirectory)
    {
        var defaults = GetDefaultGameDirectories(homeDirectory, executableDirectory);
        return defaults.FirstOrDefault(Directory.Exists) ?? defaults[0];
    }

    public static IReadOnlyList<string> GetDefaultGameDirectories(string homeDirectory, string executableDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory))
            throw new InvalidOperationException("Unable to resolve the user home directory.");
        return new[] { Path.Combine(Path.GetFullPath(homeDirectory), ".minecraft"),
                Path.Combine(Path.GetFullPath(executableDirectory), ".minecraft") }
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static string? FindVersionMetadata(string versionDirectory)
    {
        var exact = Path.Combine(versionDirectory, Path.GetFileName(versionDirectory) + ".json");
        if (File.Exists(exact)) return exact;
        var candidates = Directory.EnumerateFiles(versionDirectory)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static bool IsReservedNonVersionDirectory(string path)
        => Path.GetFileName(path) is "PCL" or "cache" or "BLClient"
           && !File.Exists(Path.Combine(path, Path.GetFileName(path) + ".json"));

    public static IReadOnlyList<MinecraftInstance> List(string gameDirectory)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory)) return [];

        return Directory.EnumerateDirectories(versionsDirectory)
            .Where(path => !IsReservedNonVersionDirectory(path))
            .Where(path => FindVersionMetadata(path) is not null)
            .Select(path => new MinecraftInstance(Path.GetFileName(path), path))
            .OrderBy(instance => instance.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
