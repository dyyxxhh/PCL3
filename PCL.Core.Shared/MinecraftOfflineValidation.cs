using System.IO.Compression;
using System.Text.Json.Nodes;

namespace PCL.Core.Shared.Minecraft;

public sealed partial class MinecraftInstaller
{
    /// <summary>Checks the local launch closure. This method never downloads or repairs files.</summary>
    public async Task ValidateOfflineInstallationAsync(string versionId, string gameDirectory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        OfflineBundleService.ValidateSegment(versionId);
        var versionDirectory = GetSafePath(Path.Combine(gameDirectory, "versions"), versionId);
        var metadataPath = Path.Combine(versionDirectory, versionId + ".json");
        OfflineBundleService.EnsureNoLinks(metadataPath);
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Minecraft metadata is invalid.");
        if (string.IsNullOrWhiteSpace(metadata["mainClass"]?.GetValue<string>()) || metadata.ContainsKey("inheritsFrom"))
            throw new InvalidDataException("Offline installation requires a complete, merged launch profile. Reinstall or migrate this instance first.");

        var files = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in ResolveClasspath(metadata, gameDirectory)) Add(path, null);
        var clientId = metadata["jar"]?.GetValue<string>() ?? metadata["id"]?.GetValue<string>() ?? versionId;
        OfflineBundleService.ValidateSegment(clientId);
        Add(Path.Combine(gameDirectory, "versions", clientId, clientId + ".jar"), metadata["downloads"]?["client"]?["sha1"]?.GetValue<string>());
        var downloads = new List<DownloadItem>();
        AddLibraryDownloads(metadata, gameDirectory, downloads);
        foreach (var file in downloads) Add(file.Path, file.Sha1);

        var indexId = metadata["assetIndex"]?["id"]?.GetValue<string>() ?? metadata["assets"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata has no asset index; cannot guarantee an offline installation.");
        OfflineBundleService.ValidateSegment(indexId);
        var indexPath = Path.Combine(gameDirectory, "assets", "indexes", indexId + ".json");
        Add(indexPath, metadata["assetIndex"]?["sha1"]?.GetValue<string>());
        OfflineBundleService.EnsureNoLinks(indexPath);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Minecraft asset index is invalid.");
        var objects = index["objects"]?.AsObject() ?? throw new InvalidDataException("Minecraft asset index has no objects.");
        foreach (var asset in objects)
        {
            var hash = asset.Value?["hash"]?.GetValue<string>();
            if (hash is null || hash.Length != 40 || !hash.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Minecraft asset index has an invalid object hash.");
            Add(Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash), hash);
            if (index["virtual"]?.GetValue<bool>() == true)
                Add(GetSafePath(Path.Combine(gameDirectory, "assets", "virtual", indexId), asset.Key), hash);
            if (index["map_to_resources"]?.GetValue<bool>() == true)
                Add(GetSafePath(Path.Combine(gameDirectory, "resources"), asset.Key), hash);
        }

        var completed = 0;
        foreach (var (path, sha1) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(completed, files.Count, path) { Stage = "Checking offline game files" });
            OfflineBundleService.EnsureNoLinks(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Offline installation is incomplete; required file is missing.", path);
            if (sha1 is not null && (sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit)
                || !await VerifySha1Async(path, sha1, cancellationToken).ConfigureAwait(false)))
                throw new InvalidDataException($"Offline game file SHA-1 verification failed: {path}");
            completed++;
        }

        // Legacy LWJGL classifiers are extracted at installation time, not by LaunchAsync.
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (!AllowedByRules(library?["rules"]?.AsArray(), "linux")) continue;
            var key = library?["natives"]?["linux"]?.GetValue<string>();
            if (key is null || library?["downloads"]?["classifiers"]?[key]?["path"]?.GetValue<string>() is not { } relative) continue;
            using var zip = ZipFile.OpenRead(GetSafePath(Path.Combine(gameDirectory, "libraries"), relative));
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith('/')) continue;
                var native = Path.Combine(gameDirectory, "versions", clientId, "natives", Path.GetFileName(entry.FullName));
                OfflineBundleService.EnsureNoLinks(native);
                if (!File.Exists(native)) throw new FileNotFoundException("Offline installation is missing an extracted native library.", native);
            }
        }
        progress?.Report(new DownloadProgress(completed, files.Count, "") { Stage = "Offline game files verified" });

        void Add(string path, string? sha1)
        {
            EnsureWithinDirectory(gameDirectory, path);
            path = Path.GetFullPath(path);
            if (!files.ContainsKey(path) || sha1 is not null) files[path] = sha1;
        }
    }
}
