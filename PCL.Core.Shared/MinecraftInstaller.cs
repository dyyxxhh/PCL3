using PCL.Core.Shared;
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
    string AccessToken = "0",
    string UserType = "legacy",
    string? AuthlibInjectorPath = null,
    string? AuthServer = null,
    string LauncherName = "PCL",
    int? MaxHeapMiB = null)
{
    public GameStartupOptions? StartupOptions { get; init; }
    public Action<string>? StartupOptionsWarning { get; init; }
    public GameIntroLaunch? Intro { get; init; }
}

public sealed partial class MinecraftInstaller(HttpClient? httpClient = null,
    Func<CancellationToken, Task<HttpClient>>? getTorClient = null, HttpClient? directFileClient = null)
{
    private readonly HttpClient _http = httpClient ?? Http;
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
        progress?.Report(new DownloadProgress(0, 0, "") { Stage = "Fetching Minecraft version manifest" });
        var manifest = await GetJsonAsync(VersionManifestUrl, cancellationToken).ConfigureAwait(false);
        var latest = manifest["latest"]?["release"]?.GetValue<string>();
        if (versionId.Equals("latest", StringComparison.OrdinalIgnoreCase))
            versionId = latest ?? throw new InvalidDataException("Version manifest has no latest release.");
        var version = manifest["versions"]?.AsArray()
            .FirstOrDefault(item => string.Equals(item?["id"]?.GetValue<string>(), versionId, StringComparison.Ordinal));
        if (version?["url"]?.GetValue<string>() is not { } versionUrl)
            throw new InvalidOperationException($"Minecraft version '{versionId}' was not found.");

        progress?.Report(new DownloadProgress(0, 0, "") { Stage = $"Fetching Minecraft {versionId} metadata" });
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
        progress?.Report(new DownloadProgress(0, 0, "") { Stage = "Extracting native libraries" });
        await ExtractNativesAsync(metadata, gameDirectory, nativesDirectory, cancellationToken).ConfigureAwait(false);
        var mainClass = metadata["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata does not contain mainClass.");
        return new MinecraftInstallResult(versionId, versionDirectory, mainClass);
    }

    /// <summary>Completes Linux libraries after importing a Windows installation, without reinstalling its version JSON.</summary>
    public async Task PrepareForLaunchAsync(string versionId, string gameDirectory,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var versionDirectory = GetSafePath(Path.Combine(gameDirectory, "versions"), versionId);
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(versionDirectory, versionId + ".json"), cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Minecraft metadata is invalid.");
        var downloads = new List<DownloadItem>();
        AddLibraryDownloads(metadata, gameDirectory, downloads);
        var librariesRoot = Path.Combine(gameDirectory, "libraries");
        var requiredDownloads = new List<DownloadItem>();
        foreach (var item in downloads)
        {
            EnsureWithinDirectory(librariesRoot, item.Path);
            InstanceBackupStore.RequireSafePath(item.Path);
            if (File.Exists(item.Path) && (item.Sha1 is null
                || await VerifySha1Async(item.Path, item.Sha1, cancellationToken).ConfigureAwait(false))) continue;
            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Missing imported libraries must have an HTTPS download URL.");
            if (item.Sha1 is null || !Regex.IsMatch(item.Sha1, "^[a-fA-F0-9]{40}$"))
                throw new InvalidDataException("Missing imported libraries must have a valid SHA-1 checksum.");
            requiredDownloads.Add(item);
        }
        if (requiredDownloads.Count > 0)
            await DownloadAllAsync(requiredDownloads, progress, cancellationToken).ConfigureAwait(false);
        var nativeId = metadata["jar"]?.GetValue<string>() ?? versionId;
        var nativesDirectory = GetSafePath(Path.Combine(gameDirectory, "versions"), nativeId);
        await ExtractNativesAsync(metadata, gameDirectory, Path.Combine(nativesDirectory, "natives"), cancellationToken).ConfigureAwait(false);
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
        var nativeVersion = metadata["jar"]?.GetValue<string>() ?? versionId;
        var nativesDirectory = Path.Combine(options.GameDirectory, "versions", nativeVersion, "natives");
        var assetsDirectory = Path.Combine(options.GameDirectory, "assets");
        var assetIndexName = metadata["assetIndex"]?["id"]?.GetValue<string>() ?? metadata["assets"]?.GetValue<string>() ?? "legacy";
        var mainClass = metadata["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata does not contain mainClass.");

        var arguments = BuildArguments(metadata, versionId, nativeVersion, options, nativesDirectory, libraries, assetsDirectory, assetIndexName);
        var startInfo = new ProcessStartInfo { FileName = options.JavaPath, WorkingDirectory = options.GameDirectory, UseShellExecute = false };
        if (options.AuthServer is not null)
        {
            if (string.IsNullOrEmpty(options.AuthlibInjectorPath) || !File.Exists(options.AuthlibInjectorPath))
                throw new FileNotFoundException("authlib-injector 不存在。", options.AuthlibInjectorPath);
            startInfo.ArgumentList.Add($"-javaagent:{options.AuthlibInjectorPath}={options.AuthServer}");
            startInfo.ArgumentList.Add("-Dauthlibinjector.side=client");
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (options.StartupOptions is { } startup)
        {
            try
            {
                var releaseDate = startup.AutoLanguage
                    ? await ReadStartupReleaseDateAsync(metadata, options.GameDirectory, versionId, nativeVersion, cancellationToken).ConfigureAwait(false)
                    : null;
                GameStartupOptionsFile.Apply(options.GameDirectory, startup, releaseDate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                options.StartupOptionsWarning?.Invoke("Could not apply Minecraft startup settings; continuing with existing options: " + ex.Message);
            }
        }
        var clientId = metadata["id"]?.GetValue<string>() ?? versionId;
        var supportedIntroProfile = options.Intro is { } selectedIntro && GameIntroLaunch.MatchesEntryPoint(selectedIntro.Profile, mainClass) &&
            (selectedIntro.Profile == "vanilla-1.21.1" ? clientId == "1.21.1" : nativeVersion == "1.21.1");
        if (options.Intro is { } intro && supportedIntroProfile &&
            options.StartupOptions?.WindowMode != GameStartupWindowMode.Fullscreen &&
            !GameStartupOptionsFile.IsFullscreen(options.GameDirectory) && !arguments.Contains("--fullscreen"))
        {
            startInfo.ArgumentList.Insert(0, $"-javaagent:{intro.AgentPath}={intro.Profile}");
            startInfo.Environment["PCL_GAME_INTRO_ENDPOINT"] = intro.Endpoint;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Java.");
    }

    private static async Task<DateTime?> ReadStartupReleaseDateAsync(JsonObject metadata, string gameDirectory, string versionId,
        string nativeVersion, CancellationToken cancellationToken)
    {
        static DateTime? ParseDate(JsonNode? value) => value is JsonValue json && json.TryGetValue<string>(out var text) &&
            DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var timestamp)
                ? timestamp.Date : null;

        if (nativeVersion != versionId && IsSafeMetadataId(nativeVersion))
        {
            var baseMetadata = Path.Combine(gameDirectory, "versions", nativeVersion, nativeVersion + ".json");
            InstanceBackupStore.RequireSafePath(baseMetadata);
            if (File.Exists(baseMetadata))
            {
                var body = JsonNode.Parse(await File.ReadAllTextAsync(baseMetadata, cancellationToken).ConfigureAwait(false));
                if (ParseDate((body as JsonObject)?["releaseTime"]) is { } date) return date;
            }
        }

        if (ParseDate(metadata["releaseTime"]) is { } releaseDate) return releaseDate;
        if (Version.TryParse(nativeVersion, out var legacy) && legacy.Major == 1)
            return legacy.Minor == 0 ? new DateTime(2011, 11, 18)
                : legacy.Minor <= 10 ? new DateTime(2016, 6, 8) : null;
        return null;
    }

    private static bool IsSafeMetadataId(string id) =>
        id is not ("." or "..") && Regex.IsMatch(id, @"\A[A-Za-z0-9._+\-]+\z", RegexOptions.CultureInvariant);

    public async Task<int> GetRequiredJavaMajorAsync(string versionId, string gameDirectory, CancellationToken cancellationToken = default)
    {
        var metadataPath = Path.Combine(gameDirectory, "versions", versionId, versionId + ".json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("Minecraft version is not installed.", metadataPath);
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
        return metadata?["javaVersion"]?["majorVersion"]?.GetValue<int>() ?? 8;
    }

    /// <summary>
    /// Finds an already installed Mojang runtime without accessing the network.
    /// </summary>
    public string? FindInstalledJavaRuntime(int majorVersion, string gameDirectory)
        => JavaDiscovery.FindRuntime(Path.Combine(gameDirectory, "runtime"), majorVersion)?.ExecutablePath;

    public async Task<string> InstallJavaRuntimeAsync(
        int majorVersion,
        string gameDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Managed Java runtimes are currently supported on Linux only.");
        var installedPath = FindInstalledJavaRuntime(majorVersion, gameDirectory);
        if (installedPath is not null) return installedPath;

        progress?.Report(new DownloadProgress(0, 0, "") { Stage = $"Fetching Java {majorVersion} runtime manifest" });
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
        progress?.Report(new DownloadProgress(0, 0, "") { Stage = $"Configuring and verifying Java {majorVersion}" });
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
        var installed = JavaDiscovery.Probe(javaPath);
        if (installed is null || ParseMajor(installed.Version) != majorVersion)
            throw new InvalidDataException($"Installed Java runtime is missing, unusable, or not Java {majorVersion}: {javaPath}.");
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

    private async Task<JsonObject?> DownloadAssetIndexAsync(JsonObject metadata, string gameDirectory, ICollection<DownloadItem> downloads, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var index = metadata["assetIndex"]?.AsObject();
        if (index?["url"]?.GetValue<string>() is not { } url || index["id"]?.GetValue<string>() is not { } id) return null;
        var path = Path.Combine(gameDirectory, "assets", "indexes", id + ".json");
        var item = new DownloadItem(url, path, index["sha1"]?.GetValue<string>());
        progress?.Report(new DownloadProgress(0, 1, item.Path) { Stage = "Downloading asset index" });
        await DownloadAsync(item, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new DownloadProgress(1, 1, item.Path) { Stage = "Downloading asset index" });
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

    private async Task DownloadAllAsync(IEnumerable<DownloadItem> downloads, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var items = downloads.DistinctBy(item => item.Path, pathComparer).ToArray();
        TorDownloadBatch? batch = null;
        if (getTorClient is not null)
        {
            var policy = new TorDownloadRetryPolicy();
            policy.ObserveInitialFiles(items.Select(item => item.Path));
            batch = new TorDownloadBatch(policy, getTorClient);
        }
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var completed = 0;
        var progressLock = new object();
        progress?.Report(new DownloadProgress(0, items.Length, ""));
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (item, token) =>
        {
            lock (progressLock)
                progress?.Report(new DownloadProgress(completed, items.Length, item.Path));
            try { await DownloadAsync(item, progress, token, batch).ConfigureAwait(false); }
            catch (Exception ex) when (batch is not null && ex is IOException && !token.IsCancellationRequested)
            {
                errors.Enqueue(ex);
                return;
            }
            lock (progressLock)
            {
                completed++;
                progress?.Report(new DownloadProgress(completed, items.Length, item.Path));
            }
        }).ConfigureAwait(false);
        if (errors.TryPeek(out var first))
            throw new IOException(first.Message + (errors.Count > 1 ? $" ({errors.Count} failed files.)" : ""), first);
    }

    private async Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, TorDownloadBatch? batch = null)
    {
        InstanceBackupStore.RequireSafePath(item.Path);
        if (File.Exists(item.Path) && (item.Sha1 is null || await VerifySha1Async(item.Path, item.Sha1, cancellationToken).ConfigureAwait(false)))
        {
            batch?.RecordCachedFile(item.Path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
        if (batch is not null)
        {
            await batch.DownloadAsync(item.Path, TransferAsync, cancellationToken).ConfigureAwait(false);
            return;
        }
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { await TransferAsync(null, cancellationToken).ConfigureAwait(false); return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TimeoutException)
            {
                lastError = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException($"Download failed after retries: {item.Path}", lastError);

        async Task TransferAsync(HttpClient? tor, CancellationToken token)
        {
            var temporary = item.Path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await (tor ?? directFileClient ?? _http).GetAsync(item.Url,
                    HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
                await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    var buffer = new byte[65536];
                    while (true)
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(30));
                        var count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                    }
                    if (response.Content.Headers.ContentLength is long length && target.Length != length)
                        throw new InvalidDataException($"Download length verification failed for {item.Path}.");
                }
                if (item.Sha1 is not null && !await VerifySha1Async(temporary, item.Sha1, token).ConfigureAwait(false))
                    throw new InvalidDataException($"SHA-1 verification failed for {item.Path}.");
                File.Move(temporary, item.Path, true);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException($"Download stalled: {item.Path}"); }
            finally { File.Delete(temporary); }
        }
    }

    private static async Task<bool> VerifySha1Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExtractNativesAsync(JsonObject metadata, string gameDirectory, string nativesDirectory, CancellationToken cancellationToken)
    {
        InstanceBackupStore.RequireSafePath(nativesDirectory);
        Directory.CreateDirectory(nativesDirectory);
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (!AllowedByRules(library?["rules"]?.AsArray(), "linux")) continue;
            var nativesKey = library?["natives"]?["linux"]?.GetValue<string>();
            if (nativesKey is null) continue;
            var classifier = library?["downloads"]?["classifiers"]?[nativesKey]?.AsObject();
            if (classifier?["path"]?.GetValue<string>() is not { } path) continue;
            var archive = GetSafePath(Path.Combine(gameDirectory, "libraries"), path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(archive)) continue;
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith('/')) continue;
                var name = Path.GetFileName(entry.FullName);
                if (name is "" or "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Invalid native library filename.");
                var destination = Path.Combine(nativesDirectory, name);
                InstanceBackupStore.RequireSafePath(destination);
                if (File.Exists(destination) && new FileInfo(destination).Length == entry.Length)
                {
                    await using var cached = File.OpenRead(destination);
                    await using var archived = entry.Open();
                    var cachedHash = await SHA256.HashDataAsync(cached, cancellationToken).ConfigureAwait(false);
                    var archiveHash = await SHA256.HashDataAsync(archived, cancellationToken).ConfigureAwait(false);
                    if (cachedHash.AsSpan().SequenceEqual(archiveHash)) continue;
                }

                // Replace changed natives atomically. A running game may still have the old file mapped.
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally { File.Delete(temporary); }
            }
        }
    }

    private static IReadOnlyList<string> ResolveClasspath(JsonObject metadata, string gameDirectory)
    {
        var paths = new List<string>();
        foreach (var library in metadata["libraries"]?.AsArray() ?? [])
        {
            if (!AllowedByRules(library?["rules"]?.AsArray(), "linux")) continue;
            var libraryPath = library?["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (libraryPath is null && library?["name"]?.GetValue<string>() is { } coordinate)
            {
                var parts = coordinate.Split(':');
                if (parts.Length is 3 or 4)
                    libraryPath = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{(parts.Length == 4 ? "-" + parts[3] : "")}.jar";
            }
            if (libraryPath is not null)
                paths.Add(Path.Combine(gameDirectory, "libraries", libraryPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        var id = metadata["jar"]?.GetValue<string>() ?? metadata["id"]?.GetValue<string>() ?? throw new InvalidDataException("Minecraft metadata does not contain id.");
        paths.Add(Path.Combine(gameDirectory, "versions", id, id + ".jar"));
        return paths;
    }

    private static IEnumerable<string> BuildArguments(JsonObject metadata, string versionId, string clientVersion, MinecraftLaunchOptions options, string nativesDirectory, IReadOnlyList<string> libraries, string assetsDirectory, string assetIndexName)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${auth_player_name}"] = options.PlayerName,
            ["${auth_uuid}"] = options.PlayerUuid,
            ["${auth_access_token}"] = options.AccessToken,
            ["${user_type}"] = options.UserType,
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
            ["${launcher_name}"] = options.LauncherName,
            ["${launcher_version}"] = "Linux CLI"
        };
        var result = new List<string>();
        var jvmArguments = metadata["arguments"]?["jvm"]?.AsArray();
        if (jvmArguments is null)
        {
            result.Add("-Djava.library.path=" + nativesDirectory);
            result.AddRange(["-cp", replacements["${classpath}"]]);
        }
        else
        {
            foreach (var argument in jvmArguments)
                AppendArgument(argument, result, replacements, clientVersion);
        }

        if (options.MaxHeapMiB is { } maxHeap)
        {
            result.RemoveAll(arg => arg.StartsWith("-Xmx", StringComparison.Ordinal));
            result.Insert(0, FormattableString.Invariant($"-Xmx{maxHeap}m"));
        }
        else if (!result.Any(arg => arg.StartsWith("-Xmx", StringComparison.Ordinal)))
            result.Insert(0, "-Xmx2G");

        var mainClass = metadata["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Minecraft metadata does not contain mainClass.");
        result.Add(mainClass);
        var gameArguments = metadata["arguments"]?["game"]?.AsArray();
        if (gameArguments is not null)
        {
            foreach (var argument in gameArguments)
                AppendArgument(argument, result, replacements, clientVersion);
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
        if (options.StartupOptions is { WindowMode: GameStartupWindowMode.Custom } startup &&
            (gameArguments is not null || Version.TryParse(clientVersion, out var version) && version >= new Version(1, 6)))
        {
            if (!result.Contains("--width")) result.AddRange(["--width", FormattableString.Invariant($"{Math.Max(100, startup.Width)}")]);
            if (!result.Contains("--height")) result.AddRange(["--height", FormattableString.Invariant($"{Math.Max(100, startup.Height)}")]);
        }
        return result;
    }

    private static void AppendArgument(JsonNode? node, ICollection<string> target, IReadOnlyDictionary<string, string> replacements, string clientVersion)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            target.Add(Replace(ReplaceClientJarIgnoreList(text, clientVersion), replacements));
            return;
        }

        if (node is not JsonObject conditional || !AllowedByRules(conditional["rules"]?.AsArray(), "linux")) return;
        if (conditional["value"] is JsonValue conditionalValue && conditionalValue.TryGetValue<string>(out var conditionalText))
            target.Add(Replace(ReplaceClientJarIgnoreList(conditionalText, clientVersion), replacements));
        else if (conditional["value"] is JsonArray conditionalValues)
            foreach (var item in conditionalValues)
                if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText))
                    target.Add(Replace(ReplaceClientJarIgnoreList(itemText, clientVersion), replacements));
    }

    // NeoForge's ignoreList placeholder otherwise names the loader profile JAR, while classpath uses metadata.jar.
    private static string ReplaceClientJarIgnoreList(string argument, string clientVersion)
        => argument.StartsWith("-DignoreList=", StringComparison.Ordinal)
            ? argument.Replace("${version_name}.jar", clientVersion + ".jar", StringComparison.Ordinal)
            : argument;

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

public sealed record DownloadProgress(int Completed, int Total, string CurrentPath)
{
    public string Stage { get; init; } = "Downloading files";
}
