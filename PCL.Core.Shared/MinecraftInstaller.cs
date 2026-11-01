using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Java;

namespace PCL.Core.Shared.Minecraft;

public sealed record MinecraftInstallResult(string VersionId, string VersionDirectory, string MainClass);

public sealed record MinecraftLaunchOptions(
    string GameDirectory,
    string JavaPath,
    string PlayerName = "Player",
    string PlayerUuid = "00000000-0000-0000-0000-000000000000",
    string AccessToken = "0");

public sealed class MinecraftInstaller
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<MinecraftInstallResult> InstallAsync(
        string versionId,
        string gameDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentException("Version is required.", nameof(versionId));
        Directory.CreateDirectory(gameDirectory);
        var manifest = await GetJsonAsync(VersionManifestUrl, cancellationToken).ConfigureAwait(false);
        var latest = manifest["latest"]?["release"]?.GetValue<string>();
        if (versionId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            versionId = latest ?? throw new InvalidDataException("Version manifest has no latest release.");
        var version = manifest["versions"]?.AsArray()
            .FirstOrDefault(item => string.Equals(item?["id"]?.GetValue<string>(), versionId, StringComparison.Ordinal));
        if (version?["url"]?.GetValue<string>() is not { } versionUrl)
            throw new InvalidOperationException($"Minecraft version '{versionId}' was not found.");

        var metadata = await GetJsonAsync(versionUrl, cancellationToken).ConfigureAwait(false);
        var versionDirectory = Path.Combine(gameDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);
        var metadataPath = Path.Combine(versionDirectory, versionId + ".json");
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), cancellationToken).ConfigureAwait(false);

        var downloads = new List<DownloadItem>();
        AddClientDownload(metadata, versionDirectory, versionId, downloads);
        AddLibraryDownloads(metadata, gameDirectory, downloads);
        var assetIndex = await DownloadAssetIndexAsync(metadata, gameDirectory, downloads, progress, cancellationToken).ConfigureAwait(false);
        AddAssetObjects(assetIndex, gameDirectory, downloads);
        await DownloadAllAsync(downloads, progress, cancellationToken).ConfigureAwait(false);

        var nativesDirectory = Path.Combine(versionDirectory, "natives");
        await ExtractNativesAsync(metadata, gameDirectory, nativesDirectory, cancellationToken).ConfigureAwait(false);
        var mainClass = metadata["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata does not contain mainClass.");
        return new MinecraftInstallResult(versionId, versionDirectory, mainClass);
    }

    public async Task<Process> LaunchAsync(
        string versionId,
        MinecraftLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        var versionDirectory = Path.Combine(options.GameDirectory, "versions", versionId);
        var metadataPath = Path.Combine(versionDirectory, versionId + ".json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("Minecraft version is not installed.", metadataPath);
        if (!File.Exists(options.JavaPath)) throw new FileNotFoundException("Java executable was not found.", options.JavaPath);

        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Minecraft metadata is invalid.");
        var libraries = ResolveClasspath(metadata, options.GameDirectory);
        var nativesDirectory = Path.Combine(versionDirectory, "natives");
        var assetsDirectory = Path.Combine(options.GameDirectory, "assets");
        var assetIndexName = metadata["assetIndex"]?["id"]?.GetValue<string>() ?? metadata["assets"]?.GetValue<string>() ?? "legacy";
        var mainClass = metadata["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata does not contain mainClass.");

        var arguments = BuildArguments(metadata, versionId, options, nativesDirectory, libraries, assetsDirectory, assetIndexName);
        var startInfo = new ProcessStartInfo { FileName = options.JavaPath, WorkingDirectory = options.GameDirectory, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Java.");
    }

    public async Task<int> GetRequiredJavaMajorAsync(string versionId, string gameDirectory, CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(gameDirectory, "versions", versionId, versionId + ".json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("Minecraft version is not installed.", metadataPath);
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
        return metadata?["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8;
    }

    public async Task<string> InstallJavaRuntimeAsync(
        int majorVersion,
        string gameDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Managed Java runtimes are currently supported on Linux only.");
        var index = await GetJsonAsync("https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json", cancellationToken).ConfigureAwait(false);
        var components = index["linux"]?.AsObject() ?? throw new InvalidDataException("Mojang runtime index has no Linux profile.");
        var selected = components
            .SelectMany(component => component.Value?.AsArray() ?? [], (component, entries) => (component.Key, Entry: entries))
            .Where(pair => pair.Entry?["version"]?["name"]?.GetValue<string>() is { } name && ParseMajor(name) == majorVersion)
            .OrderByDescending(pair => pair.Entry?["version"]?["released"]?.GetValue<string>(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected.Entry is null || selected.Entry["manifest"]?["url"]?.GetValue<string>() is not { } manifestUrl)
            throw new InvalidOperationException($"Mojang does not provide a Linux Java runtime for Java {majorVersion}.");

        var runtimeVersion = selected.Entry["version"]?["name"]?.GetValue<string>() ?? majorVersion.ToString();
        var runtimeDirectory = Path.Combine(gameDirectory, "runtime", selected.Key, runtimeVersion);
        var runtimeManifest = await GetJsonAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        var fileManifest = runtimeManifest["files"]?.AsObject() ?? throw new InvalidDataException("Java runtime manifest has no files.");
        var directories = new List<string>();
        var links = new List<(string Path, string Target)>();
        var downloads = new List<(DownloadItem Item, bool Executable)>();
        foreach (var (relativePath, fileNode) in fileManifest)
        {
            var destination = GetSafePath(runtimeDirectory, relativePath);
            var file = fileNode?.AsObject() ?? throw new InvalidDataException($"Invalid Java runtime entry '{relativePath}'.");
            var type = file["type"]?.GetValue<string>();
            if (type == "directory")
            {
                directories.Add(destination);
            }
            else if (type == "link")
            {
                if (file["target"]?.GetValue<string>() is { } target) links.Add((destination, target));
            }
            else if (file["downloads"]?["raw"]?.AsObject() is { } raw)
            {
                var url = raw["url"]?.GetValue<string>() ?? throw new InvalidDataException($"Runtime file '{relativePath}' has no URL.");
                var sha1 = raw["sha1"]?.GetValue<string>() ?? throw new InvalidDataException($"Runtime file '{relativePath}' has no SHA-1.");
                downloads.Add((new DownloadItem(url, destination, sha1), file["executable"]?.GetValue<bool>() == true));
            }
        }

        foreach (var directory in directories) Directory.CreateDirectory(directory);
        await DownloadAllAsync(downloads.Select(item => item.Item), progress, cancellationToken).ConfigureAwait(false);
        foreach (var (item, executable) in downloads)
            if (executable && File.Exists(item.Path)) File.SetUnixFileMode(item.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        foreach (var (linkPath, target) in links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var resolvedTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
            EnsureWithinDirectory(runtimeDirectory, resolvedTarget);
            if (File.Exists(linkPath) || Directory.Exists(linkPath)) File.Delete(linkPath);
            File.CreateSymbolicLink(linkPath, target);
        }

        var javaPath = Path.Combine(runtimeDirectory, "bin", "java");
        if (!File.Exists(javaPath)) throw new InvalidDataException($"Installed Java runtime does not contain {javaPath}.");
        return javaPath;
    }

    private static int ParseMajor(string version) => JavaDiscovery.ParseMajorVersion(version);

    private static string GetSafePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Runtime manifest contains a rooted path.");
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithinDirectory(root, fullPath);
        return fullPath;
    }

    private static void EnsureWithinDirectory(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime manifest path escapes its installation directory.");
    }

    private static async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
        => (await Http.GetFromJsonAsync<JsonObject>(url, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException($"Empty JSON response from {url}.");

    private static void AddClientDownload(JsonObject metadata, string versionDirectory, string versionId, ICollection<DownloadItem> downloads)
    {
        var client = metadata["downloads"]?["client"]?.AsObject();
        if (client?["url"]?.GetValue<string>() is not { } url)
            throw new InvalidDataException("Minecraft metadata does not contain a client download.");
        downloads.Add(new DownloadItem(url, Path.Combine(versionDirectory, versionId + ".jar"), client["sha1"]?.GetValue<string>()));
    }

    private static void AddLibraryDownloads(JsonObject metadata, string gameDirectory, ICollection<DownloadItem> downloads)
    {
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (!AllowedByRules(library?["rules"]?.AsArray(), "linux")) continue;
            var libraryDownloads = library?["downloads"]?.AsObject();
            var artifact = libraryDownloads?["artifact"]?.AsObject();
            if (artifact?["url"]?.GetValue<string>() is { } url && artifact["path"]?.GetValue<string>() is { } path)
                downloads.Add(new DownloadItem(url, Path.Combine(gameDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)), artifact["sha1"]?.GetValue<string>()));

            var classifierName = library?["natives"]?["linux"]?.GetValue<string>();
            if (classifierName is null) continue;
            var classifier = libraryDownloads?["classifiers"]?[classifierName]?.AsObject();
            if (classifier?["url"]?.GetValue<string>() is { } nativeUrl && classifier["path"]?.GetValue<string>() is { } nativePath)
                downloads.Add(new DownloadItem(nativeUrl, Path.Combine(gameDirectory, "libraries", nativePath.Replace('/', Path.DirectorySeparatorChar)), classifier["sha1"]?.GetValue<string>()));
        }
    }

    private static async Task<JsonObject?> DownloadAssetIndexAsync(JsonObject metadata, string gameDirectory, ICollection<DownloadItem> downloads, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var index = metadata["assetIndex"]?.AsObject();
        if (index?["url"]?.GetValue<string>() is not { } url || index["id"]?.GetValue<string>() is not { } id) return null;
        var path = Path.Combine(gameDirectory, "assets", "indexes", id + ".json");
        var item = new DownloadItem(url, path, index["sha1"]?.GetValue<string>());
        await DownloadAsync(item, progress, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))?.AsObject();
    }

    private static void AddAssetObjects(JsonObject? assetIndex, string gameDirectory, ICollection<DownloadItem> downloads)
    {
        foreach (var property in assetIndex?["objects"]?.AsObject() ?? [])
        {
            var hash = property.Value?["hash"]?.GetValue<string>();
            if (hash is null || hash.Length < 2) continue;
            var path = Path.Combine(gameDirectory, "assets", "objects", hash[..2], hash);
            downloads.Add(new DownloadItem("https://resources.download.minecraft.net/" + hash[..2] + "/" + hash, path, hash));
        }
    }

    private static async Task DownloadAllAsync(IEnumerable<DownloadItem> downloads, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var items = downloads.DistinctBy(item => item.Path, pathComparer).ToArray();
        var completed = 0;
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (item, token) =>
        {
            await DownloadAsync(item, progress, token).ConfigureAwait(false);
            Interlocked.Increment(ref completed);
            progress?.Report(new DownloadProgress(completed, items.Length, item.Path));
        }).ConfigureAwait(false);
    }

    private static async Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(item.Path) && (item.Sha1 is null || await VerifySha1Async(item.Path, item.Sha1, cancellationToken).ConfigureAwait(false))) return;
        Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
        var temporary = item.Path + ".part";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await using (var source = await Http.GetStreamAsync(item.Url, cancellationToken).ConfigureAwait(false))
                await using (var target = File.Create(temporary))
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                if (item.Sha1 is not null && !await VerifySha1Async(temporary, item.Sha1, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException($"SHA-1 verification failed for {item.Url}.");
                File.Move(temporary, item.Path, true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = ex;
                if (File.Exists(temporary)) File.Delete(temporary);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException($"Download failed after retries: {item.Url}", lastError);
    }

    private static async Task<bool> VerifySha1Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExtractNativesAsync(JsonObject metadata, string gameDirectory, string nativesDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(nativesDirectory);
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (!AllowedByRules(library?["rules"]?.AsArray(), "linux")) continue;
            var nativesKey = library?["natives"]?["linux"]?.GetValue<string>();
            if (nativesKey is null) continue;
            var classifier = library?["downloads"]?["classifiers"]?[nativesKey]?.AsObject();
            if (classifier?["path"]?.GetValue<string>() is not { } path) continue;
            var archive = Path.Combine(gameDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(archive)) continue;
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith('/')) continue;
                var destination = Path.Combine(nativesDirectory, Path.GetFileName(entry.FullName));
                await using var input = entry.Open();
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<string> ResolveClasspath(JsonObject metadata, string gameDirectory)
    {
        var paths = new List<string>();
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (!AllowedByRules(library?["rules"]?.AsArray(), "linux")) continue;
            if (library?["downloads"]?["artifact"]?["path"]?.GetValue<string>() is { } path)
                paths.Add(Path.Combine(gameDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)));
        }
        var id = metadata["id"]?.GetValue<string>() ?? throw new InvalidDataException("Minecraft metadata does not contain id.");
        paths.Add(Path.Combine(gameDirectory, "versions", id, id + ".jar"));
        return paths;
    }

    private static IEnumerable<string> BuildArguments(JsonObject metadata, string versionId, MinecraftLaunchOptions options, string nativesDirectory, IReadOnlyList<string> libraries, string assetsDirectory, string assetIndexName)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = options.PlayerName,
            ["${auth_uuid}"] = options.PlayerUuid,
            ["${auth_access_token}"] = options.AccessToken,
            ["${user_type}"] = "legacy",
            ["${version_name}"] = versionId,
            ["${game_directory}"] = options.GameDirectory,
            ["${assets_root}"] = assetsDirectory,
            ["${game_assets}"] = Path.Combine(assetsDirectory, "virtual", assetIndexName),
            ["${assets_index_name}"] = assetIndexName,
            ["${natives_directory}"] = nativesDirectory,
            ["${library_directory}"] = Path.Combine(options.GameDirectory, "libraries"),
            ["${classpath}"] = string.Join(Path.PathSeparator, libraries),
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${clientid}"] = "0",
            ["${auth_xuid}"] = "0",
            ["${version_type}"] = "release",
            ["${launcher_name}"] = "PCL",
            ["${launcher_version}"] = "Linux CLI"
        };
        var result = new List<string> { "-Xmx2G" };
        var jvmArguments = metadata["arguments"]?["jvm"]?.AsArray();
        if (jvmArguments is null)
        {
            result.Add("-Djava.library.path=" + nativesDirectory);
            result.AddRange(["-cp", replacements["${classpath}"]]);
        }
        else
        {
            foreach (var argument in jvmArguments)
                AppendArgument(argument, result, replacements);
        }

        var mainClass = metadata["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata does not contain mainClass.");
        result.Add(mainClass);
        var gameArguments = metadata["arguments"]?["game"]?.AsArray();
        if (gameArguments is not null)
        {
            foreach (var argument in gameArguments)
                AppendArgument(argument, result, replacements);
        }
        else if (metadata["minecraftArguments"]?.GetValue<string>() is { } legacyArguments)
        {
            foreach (Match match in Regex.Matches(legacyArguments, "\\\"([^\\\"]*)\\\"|([^\\s]+)"))
                result.Add(Replace(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, replacements));
        }
        else
        {
            result.AddRange(["--username", options.PlayerName, "--version", versionId, "--gameDir", options.GameDirectory, "--assetsDir", assetsDirectory, "--assetIndex", assetIndexName, "--uuid", options.PlayerUuid, "--accessToken", options.AccessToken, "--userType", "legacy"]);
        }
        return result;
    }

    private static void AppendArgument(JsonNode? node, ICollection<string> target, IReadOnlyDictionary<string, string> replacements)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            target.Add(Replace(text, replacements));
            return;
        }

        if (node is not JsonObject conditional || !AllowedByRules(conditional["rules"]?.AsArray(), "linux")) return;
        if (conditional["value"] is JsonValue conditionalValue && conditionalValue.TryGetValue<string>(out var conditionalText))
            target.Add(Replace(conditionalText, replacements));
        else if (conditional["value"] is JsonArray conditionalValues)
            foreach (var item in conditionalValues)
                if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText)) target.Add(Replace(itemText, replacements));
    }

    private static string Replace(string value, IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var replacement in replacements) value = value.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        return value;
    }

    private static bool AllowedByRules(JsonArray? rules, string osName)
    {
        if (rules is null) return true;
        var allowed = false;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var os = rule["os"]?["name"]?.GetValue<string>();
            if (os is not null && !os.Equals(osName, StringComparison.OrdinalIgnoreCase)) continue;
            var features = rule["features"]?.AsObject();
            if (features is not null && features.Any(feature => feature.Value?.GetValue<bool>() == true)) continue;
            allowed = rule["action"]?.GetValue<string>() == "allow";
        }
        return allowed;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(10) };

    private sealed record DownloadItem(string Url, string Path, string? Sha1);
}

public sealed record DownloadProgress(int Completed, int Total, string CurrentPath);
