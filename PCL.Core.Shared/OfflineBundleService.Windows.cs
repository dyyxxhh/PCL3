using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace PCL.Core.Shared.Minecraft;

public static partial class OfflineBundleService
{
    public static IEnumerable<OfflineBundleSource> EnumeratePortableData(string root) =>
        EnumerateFiles(root, allowInternalFileLinks: false)
            .Select(file => new OfflineBundleSource(file.Relative, file.Source));

    private static void ValidateWindowsExecutable(string path, string runtime)
    {
        EnsureNoLinks(path);
        using var stream = File.OpenRead(path);
        using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
        var expected = runtime == "win-arm64"
            ? System.Reflection.PortableExecutable.Machine.Arm64
            : System.Reflection.PortableExecutable.Machine.Amd64;
        if (reader.PEHeaders.PEHeader is null || reader.PEHeaders.CoffHeader.Machine != expected)
            throw new InvalidDataException($"The executable does not match {runtime}: {Path.GetFileName(path)}");
    }

    private static readonly JsonSerializerOptions WindowsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32
    };

    public static async Task<OfflineBundleManifest> ExportWindowsAsync(
        OfflineInstanceMetadata instance, int javaMajorVersion, string targetRuntime,
        string javaHome, string launcher, string bundlePath,
        IEnumerable<OfflineBundleSource> files,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows bundles can only be exported on Windows.");
        if (targetRuntime != CurrentTargetRuntime || targetRuntime is not ("win-x64" or "win-arm64"))
            throw new PlatformNotSupportedException("Windows offline bundles require a matching x64 or ARM64 Windows host.");
        ValidateMetadata(instance);
        ValidateSegment(javaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        javaHome = Path.GetFullPath(javaHome);
        launcher = Path.GetFullPath(launcher);
        bundlePath = Path.GetFullPath(bundlePath);
        var javaExe = Path.Combine(javaHome, "bin", "java.exe");
        if (!File.Exists(javaExe)) throw new FileNotFoundException("A complete Windows Java runtime is required.", javaExe);
        ValidateWindowsExecutable(javaExe, targetRuntime);
        ValidateWindowsExecutable(launcher, targetRuntime);
        EnsureNoLinks(bundlePath);
        if (PathExists(bundlePath)) throw new IOException("Offline bundle already exists; choose a new output file.");

        var sources = new List<OfflineBundleSource>();
        foreach (var source in files) sources.Add(source);
        sources.AddRange(EnumerateFiles(javaHome, allowInternalFileLinks: false)
            .Select(file => new OfflineBundleSource("instance/runtime/offline-java/" + file.Relative, file.Source)));
        sources.Add(new OfflineBundleSource(WindowsLauncherPath, launcher, true));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var source in sources)
        {
            ValidatePayloadPath(source.Path);
            EnsureNoLinks(source.SourcePath);
            if (!File.Exists(source.SourcePath)) throw new FileNotFoundException("Offline bundle source file is missing.", source.SourcePath);
            if (!paths.Add(source.Path)) throw new InvalidDataException("Offline bundle contains colliding file paths.");
            var size = new FileInfo(source.SourcePath).Length;
            if (size < 0 || size > MaxFileSize || size > MaxTotalSize - total) throw new InvalidDataException("Offline bundle exceeds its size limits.");
            total += size;
        }
        if (sources.Count > MaxFiles) throw new InvalidDataException("Offline bundle contains too many files.");
        var temp = bundlePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var manifestFiles = new List<OfflineBundleFile>(sources.Count);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var source in sources.OrderBy(file => file.Path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new DownloadProgress(manifestFiles.Count, sources.Count, source.Path) { Stage = "Packing Windows offline bundle" });
                    var entry = zip.CreateEntry(source.Path, CompressionLevel.Fastest);
                    await using var input = File.OpenRead(source.SourcePath);
                    await using var target = entry.Open();
                    var size = input.Length;
                    var sha = await CopyAndHashAsync(input, target, size, cancellationToken).ConfigureAwait(false);
                    manifestFiles.Add(new OfflineBundleFile(source.Path, size, sha, source.Executable));
                }
                var manifest = new OfflineBundleManifest("mcm-offline", 2, targetRuntime, DateTimeOffset.UtcNow,
                    instance, javaMajorVersion, WindowsJavaExecutable, WindowsLauncherPath, manifestFiles) { Layout = "pcl-windows" };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, WindowsJsonOptions);
                var manifestEntry = zip.CreateEntry(ManifestPath);
                await using var stream = manifestEntry.Open();
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            var result = await ReadManifestAsync(temp, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, bundlePath, overwrite: false);
            return result;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<OfflineBundleManifest> ImportWindowsAsync(
        string bundlePath, string gameDirectory, string? instanceName = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows offline bundles can only be imported on Windows.");
        using var zip = ZipFile.OpenRead(bundlePath);
        var contents = await ReadContentsAsync(zip, cancellationToken).ConfigureAwait(false);
        var manifest = contents.Manifest;
        if (manifest.Layout != "pcl-windows" || manifest.FormatVersion != 2 || manifest.TargetRuntime != CurrentTargetRuntime)
            throw new PlatformNotSupportedException($"This bundle targets {manifest.TargetRuntime}; this computer is {CurrentTargetRuntime}.");
        var name = instanceName ?? manifest.Instance.Name;
        ValidateSegment(name);
        gameDirectory = Path.GetFullPath(gameDirectory);
        var versions = Path.Combine(gameDirectory, "versions");
        var destination = Path.Combine(versions, name);
        EnsureNoLinks(destination);
        if (PathExists(destination)) throw new IOException($"Version '{name}' already exists; offline import never overwrites an instance.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var instanceRoot = Path.Combine(staging, "instance");
            Directory.CreateDirectory(instanceRoot);
            await VerifyFilesAsync(contents, instanceRoot, progress, cancellationToken).ConfigureAwait(false);
            var java = Path.Combine(instanceRoot, manifest.JavaExecutable.Replace('/', Path.DirectorySeparatorChar));
            ValidateWindowsExecutable(java, manifest.TargetRuntime);
            if (!File.Exists(Path.Combine(instanceRoot, "versions", manifest.Instance.LaunchVersion, manifest.Instance.LaunchVersion + ".jar")))
                throw new InvalidDataException("Offline package does not contain its Minecraft client JAR.");
            var versionJson = Path.Combine(instanceRoot, "versions", manifest.Instance.LaunchVersion, manifest.Instance.LaunchVersion + ".json");
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(versionJson, cancellationToken).ConfigureAwait(false));
            if (!json.RootElement.TryGetProperty("mainClass", out var mainClass) || string.IsNullOrWhiteSpace(mainClass.GetString())
                || json.RootElement.TryGetProperty("inheritsFrom", out _))
                throw new InvalidDataException("Windows offline version metadata must be a complete merged launch profile.");

            await MergeSharedTreesAsync([
                (Path.Combine(instanceRoot, "libraries"), Path.Combine(gameDirectory, "libraries")),
                (Path.Combine(instanceRoot, "assets"), Path.Combine(gameDirectory, "assets"))
            ], cancellationToken).ConfigureAwait(false);
            var versionSource = Path.Combine(instanceRoot, "versions", manifest.Instance.LaunchVersion);
            var versionTarget = Path.Combine(staging, "version");
            Directory.Move(versionSource, versionTarget);
            foreach (var dir in new[] { "mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks", "resources" })
                CopyTreeIfExists(Path.Combine(instanceRoot, dir), Path.Combine(versionTarget, dir));
            foreach (var file in new[] { "options.txt", "optionsof.txt", "optionsshaders.txt", "mcm-mods.json", "pcl-mods.json" })
            {
                var source = Path.Combine(instanceRoot, file);
                if (File.Exists(source)) File.Copy(source, Path.Combine(versionTarget, file));
            }
            var targetJson = Path.Combine(versionTarget, name + ".json");
            var sourceJson = Path.Combine(versionTarget, manifest.Instance.LaunchVersion + ".json");
            if (!sourceJson.Equals(targetJson, StringComparison.OrdinalIgnoreCase))
            {
                var versionObject = JsonNode.Parse(await File.ReadAllTextAsync(sourceJson, cancellationToken).ConfigureAwait(false))?.AsObject()
                    ?? throw new InvalidDataException("Windows offline version metadata is invalid.");
                versionObject["id"] = name;
                if (versionObject["jar"]?.GetValue<string>() == manifest.Instance.LaunchVersion) versionObject["jar"] = name;
                var oldJar = Path.Combine(versionTarget, manifest.Instance.LaunchVersion + ".jar");
                if (File.Exists(oldJar)) File.Move(oldJar, Path.Combine(versionTarget, name + ".jar"), false);
                await File.WriteAllTextAsync(targetJson, versionObject.ToJsonString(), cancellationToken).ConfigureAwait(false);
                File.Delete(sourceJson);
            }
            var runtimeSource = Path.Combine(instanceRoot, "runtime", "offline-java");
            var runtimeTarget = Path.Combine(versionTarget, "runtime", "offline-java");
            Directory.CreateDirectory(Path.GetDirectoryName(runtimeTarget)!);
            Directory.Move(runtimeSource, runtimeTarget);
            var local = JsonSerializer.Serialize(new { offlineBundle = true, java = "runtime/offline-java/bin/java.exe", name, gameVersion = manifest.Instance.GameVersion, loader = manifest.Instance.Loader });
            await File.WriteAllTextAsync(Path.Combine(versionTarget, "pcl-offline.json"), local, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(destination);
            Directory.Move(versionTarget, destination);
            return manifest with { Instance = manifest.Instance with { Name = name } };
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static async Task MergeSharedTreesAsync(
        IReadOnlyList<(string Source, string Target)> trees, CancellationToken cancellationToken)
    {
        var pending = new List<(string Source, string Destination)>();
        foreach (var (source, target) in trees)
        {
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(file);
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                EnsureNoLinks(destination);
                if (File.Exists(destination))
                {
                    if (new FileInfo(destination).Length != new FileInfo(file).Length
                        || !string.Equals(await HashFileAsync(destination, cancellationToken).ConfigureAwait(false), await HashFileAsync(file, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Shared Minecraft file conflicts with existing content: {relative}");
                    continue;
                }
                if (Directory.Exists(destination)) throw new IOException($"Shared Minecraft file collides with an existing directory: {relative}");
                pending.Add((file, destination));
            }
        }
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            var temp = item.Destination + ".offline-" + Guid.NewGuid().ToString("N");
            try { File.Copy(item.Source, temp); File.Move(temp, item.Destination, false); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static void CopyTreeIfExists(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            EnsureNoLinks(file);
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static bool IsSupportedLayout(OfflineBundleManifest manifest) =>
        manifest.Layout is null && manifest.FormatVersion == 1 && manifest.JavaExecutable == JavaExecutable && manifest.LauncherPath == LauncherPath
            || manifest.Layout == "pcl-windows" && manifest.FormatVersion == 2 && manifest.JavaExecutable == WindowsJavaExecutable && manifest.LauncherPath == WindowsLauncherPath;
}
