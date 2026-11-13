using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Shared.Minecraft;

public sealed record ScannedMinecraftInstance(string Id, string GameDirectory, string MetadataPath,
    string GameVersion, string? Loader, string? LoaderVersion, string DataDirectory, bool AmbiguousDataDirectory);

public sealed record MinecraftScanResult(IReadOnlyList<ScannedMinecraftInstance> Instances, IReadOnlyList<string> Diagnostics);

/// <summary>Reads PCL/official version folders and copies selected versions into independent PCL3 instances.</summary>
public static class MinecraftInstanceMigration
{
    private static readonly string[] DataDirectories = ["mods", "config", "defaultconfigs", "saves", "resourcepacks",
        "shaderpacks", "kubejs", "scripts", "screenshots", "schematics", "journeymap", "XaeroWaypoints", "XaeroWorldMap"];
    private static readonly string[] DataFiles = ["options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "servers.dat_old", "hotbar.nbt"];

    public static string ResolveScanDirectory(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetFileName(root) == "versions") return Path.GetDirectoryName(root)!;
        if (Directory.Exists(Path.Combine(root, "versions"))) return root;
        if (Directory.Exists(Path.Combine(root, ".minecraft", "versions"))) return Path.Combine(root, ".minecraft");
        if (Path.GetFileName(Path.GetDirectoryName(root)) == "versions") return Path.GetDirectoryName(Path.GetDirectoryName(root))!;
        return root;
    }

    public static MinecraftScanResult Scan(string path)
    {
        var root = ResolveScanDirectory(path);
        var versions = Path.Combine(root, "versions");
        var found = new List<ScannedMinecraftInstance>();
        var diagnostics = new List<string>();
        if (!Directory.Exists(versions))
            return new(found, [$"{root}: no versions directory; select the .minecraft root containing versions."]);
        foreach (var directory in Directory.EnumerateDirectories(versions).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(directory);
            if (MinecraftInstanceService.IsReservedNonVersionDirectory(directory)) continue;
            try
            {
                CheckPathNotLinked(directory);
                var profile = ReadProfile(root, id, []);
                var jarDirectory = Path.Combine(root, "versions", profile.JarId);
                if (FindJar(jarDirectory, profile.JarId) is null)
                    throw new FileNotFoundException($"The Minecraft client JAR for '{profile.JarId}' is missing.");
                var (loader, loaderVersion) = GetLoader(profile.Metadata);
                var (data, ambiguous) = FindDataDirectory(root, directory);
                found.Add(new(id, root, MinecraftInstanceService.FindVersionMetadata(directory)!,
                    GetGameVersion(profile.Metadata, profile.BaseId, root, profile.JarId), loader, loaderVersion, data, ambiguous));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            {
                diagnostics.Add($"{directory}: {ex.Message}");
            }
        }
        return new(found, diagnostics);
    }

    public static async Task<ManagedMinecraftInstance> MigrateAsync(ScannedMinecraftInstance source,
        string destinationGameDirectory, string instanceName, string? dataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var destination = ManagedInstanceService.GetInstanceDirectory(destinationGameDirectory, instanceName);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"Instance '{instanceName}' already exists; choose a different name.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.GameDirectory));
        CheckPathNotLinked(root);
        var versionDirectory = Path.Combine(root, "versions", SafeId(source.Id));
        var profile = ReadProfile(root, source.Id, []);
        var (defaultData, ambiguous) = FindDataDirectory(root, versionDirectory);
        if (dataDirectory is null && ambiguous)
            throw new InvalidDataException("Both shared and isolated game data exist. Select a data directory with --data-dir.");
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory ?? defaultData));
        if (data != root && data != versionDirectory)
            throw new ArgumentException("--data-dir must be the source .minecraft root or the selected version directory.");
        var jarId = SafeId(profile.Metadata["jar"]?.GetValue<string>() ?? profile.JarId);
        var jarDirectory = Path.Combine(root, "versions", jarId);
        CheckNotLink(jarDirectory);
        var jar = FindJar(jarDirectory, jarId)
            ?? throw new FileNotFoundException($"The Minecraft client JAR for '{jarId}' is missing. Complete the original installation first.");
        var (loader, loaderVersion) = GetLoader(profile.Metadata);
        var gameVersion = GetGameVersion(profile.Metadata, profile.BaseId, root, profile.JarId);
        var parent = Path.GetDirectoryName(destination)!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var copyRoots = new[] { Path.Combine(root, "libraries"), Path.Combine(root, "assets") }
            .Concat(DataDirectories.Select(name => Path.Combine(data, name)));
        foreach (var copyRoot in copyRoots)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copyRoot));
            if (destination.Equals(normalized, comparison) || destination.StartsWith(normalized + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("The migration destination must not be inside a source directory being copied.");
        }
        CheckPathNotLinked(parent);
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var folder in new[] { "libraries", "assets" })
                await CopyDirectoryAsync(Path.Combine(root, folder), Path.Combine(staging, folder), cancellationToken).ConfigureAwait(false);
            foreach (var folder in DataDirectories)
                await CopyDirectoryAsync(Path.Combine(data, folder), Path.Combine(staging, folder), cancellationToken).ConfigureAwait(false);
            foreach (var file in DataFiles)
            {
                var path = Path.Combine(data, file);
                if (File.Exists(path)) await CopyFileAsync(path, Path.Combine(staging, file), cancellationToken).ConfigureAwait(false);
            }
            var targetJar = Path.Combine(staging, "versions", jarId, jarId + ".jar");
            await CopyFileAsync(jar, targetJar, cancellationToken).ConfigureAwait(false);
            var metadata = profile.Metadata;
            metadata["id"] = source.Id;
            metadata["jar"] = jarId;
            metadata.Remove("inheritsFrom");
            var targetJson = Path.Combine(staging, "versions", source.Id, source.Id + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(targetJson)!);
            await File.WriteAllTextAsync(targetJson, metadata.ToJsonString(), cancellationToken).ConfigureAwait(false);
            // Publish only after all copies and metadata have completed. Never modify the source.
            var staged = new ManagedMinecraftInstance(instanceName, gameVersion, loader, loaderVersion, staging, source.Id, Imported: true);
            await ManagedInstanceService.WriteMetadataAsync(staging, staged, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        return new ManagedMinecraftInstance(instanceName, gameVersion, loader, loaderVersion, destination, source.Id, Imported: true);
    }

    private sealed record Profile(JsonObject Metadata, string BaseId, string JarId);

    private static Profile ReadProfile(string root, string id, HashSet<string> chain)
    {
        SafeId(id);
        if (chain.Count >= 32 || !chain.Add(id)) throw new InvalidDataException($"Cyclic or excessively deep version inheritance at '{id}'.");
        var directory = Path.Combine(root, "versions", id);
        CheckNotLink(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Inherited version '{id}' was not found.");
        var path = MinecraftInstanceService.FindVersionMetadata(directory)
            ?? throw new InvalidDataException("No matching version JSON, or multiple differently named JSON files.");
        CheckNotLink(path);
        var metadata = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("Version JSON is empty.");
        if (metadata["patches"] is JsonArray patches && metadata["time"] is null)
        {
            foreach (var patch in patches.OfType<JsonObject>().OrderBy(patch => patch["priority"]?.GetValue<int>() ?? 0))
                metadata = Merge(metadata, patch);
            metadata.Remove("inheritsFrom");
        }
        var baseId = id;
        var jarId = id;
        var explicitJar = metadata["jar"]?.GetValue<string>();
        if (metadata["inheritsFrom"]?.GetValue<string>() is { Length: > 0 } parentId)
        {
            var parent = ReadProfile(root, parentId, chain);
            metadata = Merge(parent.Metadata, metadata);
            baseId = parent.BaseId;
            jarId = parent.JarId;
        }
        if (!string.IsNullOrEmpty(explicitJar)) jarId = SafeId(explicitJar);
        metadata.Remove("jar");
        if (metadata["mainClass"]?.GetValue<string>() is not { Length: > 0 })
            throw new InvalidDataException("Version JSON has no mainClass.");
        if (metadata["libraries"] is not JsonArray)
            throw new InvalidDataException("Version JSON has no libraries array.");
        metadata["jar"] = jarId;
        chain.Remove(id);
        return new(metadata, baseId, jarId);
    }

    private static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var (key, value) in child)
        {
            if (key == "libraries" && value is JsonArray libraries)
            {
                var merged = new JsonArray();
                var names = libraries.Select(LibraryKey).ToHashSet(StringComparer.Ordinal);
                foreach (var entry in libraries) merged.Add(entry?.DeepClone());
                foreach (var entry in parent["libraries"]?.AsArray() ?? [])
                    if (!names.Contains(LibraryKey(entry))) merged.Add(entry?.DeepClone());
                result[key] = merged;
            }
            else if (key == "arguments" && value is JsonObject arguments)
            {
                var merged = parent[key]?.DeepClone().AsObject() ?? new JsonObject();
                foreach (var (section, entries) in arguments)
                {
                    var combined = merged[section]?.DeepClone().AsArray() ?? new JsonArray();
                    foreach (var entry in entries?.AsArray() ?? []) combined.Add(entry?.DeepClone());
                    merged[section] = combined;
                }
                result[key] = merged;
            }
            else result[key] = value?.DeepClone();
        }
        return result;
    }

    private static string LibraryKey(JsonNode? entry)
    {
        var name = entry?["name"]?.GetValue<string>() ?? "";
        var parts = name.Split(':');
        return parts.Length >= 3 ? parts[0] + ":" + parts[1] + (parts.Length > 3 ? ":" + parts[3] : "") : name;
    }

    private static string GetGameVersion(JsonObject metadata, string baseId, string root, string jarId)
    {
        foreach (var key in new[] { "clientVersion", "_minecraftVersion", "minecraftVersion" })
            if (metadata[key] is JsonValue value && value.TryGetValue<string>(out var version) && IsGameVersion(version)) return version;
        if (metadata["patches"] is JsonArray patches)
            foreach (var patch in patches)
                if (patch?["id"]?.GetValue<string>() == "game" && patch["version"]?.GetValue<string>() is { } version && IsGameVersion(version)) return version;
        if (metadata["arguments"]?["game"] is JsonArray gameArguments)
            for (var i = 0; i + 1 < gameArguments.Count; i++)
                if (gameArguments[i] is JsonValue option && option.TryGetValue<string>(out var name)
                    && name == "--fml.mcVersion" && gameArguments[i + 1] is JsonValue argument
                    && argument.TryGetValue<string>(out var version) && IsGameVersion(version)) return version;
        if (metadata["jar"]?.GetValue<string>() is { } jar && IsGameVersion(jar)) return jar;
        if (IsGameVersion(baseId)) return baseId;
        if (metadata["id"]?.GetValue<string>() is { } id && IsGameVersion(id)) return id;
        var clientJar = FindJar(Path.Combine(root, "versions", jarId), jarId);
        if (clientJar is not null)
        {
            CheckNotLink(clientJar);
            try
            {
                using var archive = ZipFile.OpenRead(clientJar);
                if (archive.GetEntry("version.json") is { } entry)
                {
                    using var input = entry.Open();
                    var version = JsonNode.Parse(input)?["id"]?.GetValue<string>();
                    if (version is not null && IsGameVersion(version)) return version;
                }
            }
            catch (InvalidDataException) { /* Older client jars may not contain version.json. */ }
        }
        var match = Regex.Match(metadata["id"]?.GetValue<string>() ?? baseId, @"(?<!\d)(1\.\d+(?:\.\d+)?)(?!\d)", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : throw new InvalidDataException("Cannot determine Minecraft version from metadata.");
    }

    private static bool IsGameVersion(string value)
        => Regex.IsMatch(value, @"^(?:[a-z]?\d+\.\d+(?:\.\d+)?(?:[-_](?:pre|rc)\d+)?|\d{2}w\d{2}[a-z])$", RegexOptions.CultureInvariant);

    private static (string? Loader, string? Version) GetLoader(JsonObject metadata)
    {
        var coordinates = (metadata["libraries"]?.AsArray() ?? []).Select(entry => entry?["name"]?.GetValue<string>() ?? "").ToArray();
        foreach (var (prefix, loader) in new[] { ("net.neoforged:neoforge:", "neoforge"), ("net.neoforged:forge:", "neoforge"),
                     ("net.minecraftforge:forge:", "forge"), ("net.fabricmc:fabric-loader:", "fabric"), ("org.quiltmc:quilt-loader:", "quilt") })
        {
            var coordinate = coordinates.FirstOrDefault(name => name.StartsWith(prefix, StringComparison.Ordinal));
            if (coordinate is null) continue;
            var version = coordinate[prefix.Length..].Split(':')[0];
            if (prefix.EndsWith(":forge:", StringComparison.Ordinal) && version.Contains('-')) version = version[(version.IndexOf('-') + 1)..];
            return (loader, version);
        }
        return (null, null);
    }

    private static (string Directory, bool Ambiguous) FindDataDirectory(string root, string versionDirectory)
    {
        // PCL selects the new configuration file as a whole before falling back to Setup.ini.
        var settings = new[] { "config.v1.yml", "config.v1.json", "Setup.ini" }
            .Select(name => Path.Combine(versionDirectory, "PCL", name)).FirstOrDefault(File.Exists);
        if (settings is not null)
        {
            var v2 = ReadSetting(settings, "VersionArgumentIndieV2");
            if (bool.TryParse(v2, out var isolated)) return (isolated ? versionDirectory : root, false);
            if (v2 is "1" or "0") return (v2 == "1" ? versionDirectory : root, false);
            var v1 = ReadSetting(settings, "VersionArgumentIndie");
            if (v1 is "1" or "2") return (v1 == "1" ? versionDirectory : root, false);
        }
        var local = HasGameData(versionDirectory);
        var shared = HasGameData(root);
        return (local ? versionDirectory : root, local && shared);
    }

    private static bool HasGameData(string directory)
        => DataDirectories.Any(name => HasDirectoryData(Path.Combine(directory, name)))
           || DataFiles.Any(name => File.Exists(Path.Combine(directory, name)));

    private static bool HasDirectoryData(string path)
    {
        if (!Directory.Exists(path)) return false;
        CheckNotLink(path);
        return Directory.EnumerateFileSystemEntries(path).Any();
    }

    private static string? ReadSetting(string path, string key)
    {
        if (!File.Exists(path)) return null;
        CheckPathNotLinked(path);
        if (Path.GetExtension(path) == ".json")
        {
            var value = JsonNode.Parse(File.ReadAllText(path))?[key];
            return value?.ToString();
        }
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            var match = Regex.Match(trimmed, "^" + Regex.Escape(key) + @"\s*[:=]\s*([^#\r\n]*)", RegexOptions.CultureInvariant);
            if (match.Success) return match.Groups[1].Value.Trim().Trim('"', '\'');
        }
        return null;
    }

    private static string? FindJar(string directory, string id)
    {
        if (!Directory.Exists(directory)) return null;
        var exact = Path.Combine(directory, id + ".jar");
        if (File.Exists(exact)) return exact;
        var jars = Directory.EnumerateFiles(directory).Where(path => Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return jars.Length == 1 ? jars[0] : null;
    }

    private static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || Path.IsPathRooted(id))
            throw new InvalidDataException("Version metadata contains an unsafe path identifier.");
        return id;
    }

    private static void CheckNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Symbolic links are not supported during migration: {path}");
    }

    private static void CheckPathNotLinked(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists) CheckNotLink(directory.FullName);
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source)) return;
        CheckNotLink(source);
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckNotLink(entry);
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) await CopyDirectoryAsync(entry, target, cancellationToken).ConfigureAwait(false);
            else await CopyFileAsync(entry, target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        CheckNotLink(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
