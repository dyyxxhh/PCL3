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
        return string.IsNullOrEmpty(home)
            ? throw new InvalidOperationException("Unable to resolve the user home directory.")
            : Path.Combine(home, ".minecraft");
    }

    public static IReadOnlyList<MinecraftInstance> List(string gameDirectory)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory)) return [];

        return Directory.EnumerateDirectories(versionsDirectory)
            .Where(path => File.Exists(Path.Combine(path, Path.GetFileName(path) + ".json")))
            .Select(path => new MinecraftInstance(Path.GetFileName(path), path))
            .OrderBy(instance => instance.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
