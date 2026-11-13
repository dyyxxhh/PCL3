using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL.Core.Shared.Minecraft;

public sealed record ManagedMinecraftInstance(string Name, string GameVersion, string? Loader, string? LoaderVersion, string DirectoryPath, string LaunchVersion, bool Imported = false);

public static class ManagedInstanceService
{
    private const string MetadataFileName = "pcl-instance.json";
    private static readonly Regex SafeName = new("^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant);

    public static string GetInstanceDirectory(string gameDirectory, string name)
    {
        if (name is "." or ".." || !SafeName.IsMatch(name))
            throw new ArgumentException("Instance names may only contain letters, numbers, dots, underscores, and hyphens.", nameof(name));
        return Path.Combine(Path.GetFullPath(gameDirectory), "instances", name);
    }

    public static IReadOnlyList<ManagedMinecraftInstance> List(string gameDirectory)
    {
        var root = Path.Combine(gameDirectory, "instances");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Where(path => !Path.GetFileName(path).StartsWith(".migration-", StringComparison.Ordinal))
            .Select(path => ReadFromDirectory(path))
            .OfType<ManagedMinecraftInstance>()
            .OrderBy(instance => instance.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public static ManagedMinecraftInstance? Find(string gameDirectory, string name)
        => SafeName.IsMatch(name) ? ReadFromDirectory(GetInstanceDirectory(gameDirectory, name)) : null;

    public static async Task SaveAsync(ManagedMinecraftInstance instance, CancellationToken cancellationToken = default)
    {
        var directory = GetInstanceDirectory(Path.GetDirectoryName(Path.GetDirectoryName(instance.DirectoryPath)!)!, instance.Name);
        if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(instance.DirectoryPath), StringComparison.Ordinal))
            throw new InvalidDataException("Instance directory does not match its name.");
        Directory.CreateDirectory(directory);
        await WriteMetadataAsync(directory, instance, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteMetadataAsync(string directory, ManagedMinecraftInstance instance, CancellationToken cancellationToken)
    {
        var metadata = new InstanceMetadata(instance.Name, instance.GameVersion, instance.Loader, instance.LoaderVersion, instance.LaunchVersion, instance.Imported);
        var path = Path.Combine(directory, MetadataFileName);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(metadata), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ManagedMinecraftInstance? ReadFromDirectory(string directory)
    {
        var path = Path.Combine(directory, MetadataFileName);
        if (!File.Exists(path)) return null;
        var metadata = JsonSerializer.Deserialize<InstanceMetadata>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Invalid instance metadata: {path}");
        if (metadata.Name != Path.GetFileName(directory) || string.IsNullOrWhiteSpace(metadata.GameVersion) || string.IsNullOrWhiteSpace(metadata.LaunchVersion))
            throw new InvalidDataException($"Invalid instance metadata: {path}");
        return new ManagedMinecraftInstance(metadata.Name, metadata.GameVersion, metadata.Loader, metadata.LoaderVersion, directory, metadata.LaunchVersion, metadata.Imported);
    }

    private sealed record InstanceMetadata(string Name, string GameVersion, string? Loader, string? LoaderVersion, string LaunchVersion, bool Imported = false);
}
