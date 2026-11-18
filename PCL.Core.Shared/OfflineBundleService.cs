using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PCL.Core.Shared.Java;

namespace PCL.Core.Shared.Minecraft;

public sealed record OfflineInstanceMetadata(string Name, string GameVersion, string? Loader, string? LoaderVersion, string LaunchVersion);
public sealed record OfflineBundleFile(string Path, long Size, string Sha256, bool Executable);
public sealed record OfflineBundleManifest(string Format, int FormatVersion, string TargetRuntime, DateTimeOffset CreatedAtUtc,
    OfflineInstanceMetadata Instance, int JavaMajorVersion, string JavaExecutable, string LauncherPath, IReadOnlyList<OfflineBundleFile> Files)
{
    public string? Layout { get; init; }
}

public sealed record OfflineBundleSource(string Path, string SourcePath, bool Executable = false);

/// <summary>
/// Versioned, self-contained instance transport, distinct from manifest-only modpacks.
/// No method in this service makes network requests. Platform adapters validate game dependencies.
/// </summary>
public static partial class OfflineBundleService
{
    public const string ManifestPath = "mcm-offline.json";
    public const string LauncherPath = "bootstrap/mcm";
    public const string WindowsLauncherPath = "bootstrap/PCL3.exe";
    public const string WindowsJavaExecutable = "runtime/offline-java/bin/java.exe";
    public const string JavaExecutable = "runtime/offline-java/bin/java";
    public const int MaxFiles = 200_000;
    public const long MaxFileSize = 8L * 1024 * 1024 * 1024;
    public const long MaxTotalSize = 64L * 1024 * 1024 * 1024;
    private const int MaxManifestSize = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 32 };
    private static readonly HashSet<string> GameDirectories = new(StringComparer.Ordinal)
        { "versions", "libraries", "assets", "mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks", "resources" };
    private static readonly HashSet<string> GameFiles = new(StringComparer.Ordinal)
        { "options.txt", "optionsof.txt", "optionsshaders.txt", "mcm-mods.json", "pcl-mods.json" };
    private static readonly HashSet<string> PrivateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.json", "launcher_accounts.json", "launcher_profiles.json", "launcher_profiles_microsoft_store.json",
        "selected-profile.json", "microsoft-account.json", "credentials.json", "tokens.json", "usercache.json",
        "jmxremote.password", "servers.dat", "servers.dat_old", "realms_persistence.json", ".git", ".ssh", ".config", "logs", "crash-reports", "saves"
    };

    public static string CurrentTargetRuntime => (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "win" : "unknown")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static async Task<OfflineBundleManifest> ExportAsync(ManagedMinecraftInstance instance, string javaHome,
        string launcher, string bundlePath, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireSupportedPlatform(CurrentTargetRuntime);
        ValidateMetadata(new OfflineInstanceMetadata(instance.Name, instance.GameVersion, instance.Loader, instance.LoaderVersion, instance.LaunchVersion));
        javaHome = JavaDiscovery.ResolveHome(javaHome);
        launcher = Path.GetFullPath(launcher);
        bundlePath = Path.GetFullPath(bundlePath);
        EnsureNoLinks(instance.DirectoryPath);
        EnsureNoLinks(javaHome);
        EnsureNoLinks(launcher);
        EnsureNoLinks(bundlePath);
        if (PathExists(bundlePath)) throw new IOException("Offline bundle already exists; choose a new output file.");

        // Enumerate before probing/reading, so links cannot smuggle host files into an archive.
        var sources = new List<BundleSource>();
        foreach (var directory in GameDirectories.Order(StringComparer.Ordinal))
        {
            var root = Path.Combine(instance.DirectoryPath, directory);
            if (!PathExists(root)) continue;
            foreach (var file in EnumerateFiles(root, allowInternalFileLinks: false))
                sources.Add(new BundleSource("instance/" + directory + "/" + file.Relative, file.Source, false));
        }
        foreach (var file in GameFiles.Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(instance.DirectoryPath, file);
            if (!PathExists(path)) continue;
            EnsureNoLinks(path);
            sources.Add(new BundleSource("instance/" + file, path, false));
        }
        foreach (var file in EnumerateFiles(javaHome, allowInternalFileLinks: true))
        {
            var executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file.Source) &
                (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            sources.Add(new BundleSource("instance/runtime/offline-java/" + file.Relative, file.Source, executable));
        }
        sources.Add(new BundleSource(LauncherPath, launcher, true));
        if (sources.Count > MaxFiles) throw new InvalidDataException("Offline bundle contains too many files.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var source in sources)
        {
            ValidatePayloadPath(source.Path);
            if (!paths.Add(source.Path)) throw new InvalidDataException("Offline bundle contains colliding file paths.");
            var length = new FileInfo(source.Source).Length;
            if (length < 0 || length > MaxFileSize || length > MaxTotalSize - total)
                throw new InvalidDataException("Offline bundle exceeds its size limits.");
            total += length;
        }

        var installer = new MinecraftInstaller();
        await installer.ValidateOfflineInstallationAsync(instance.LaunchVersion, instance.DirectoryPath, progress, cancellationToken).ConfigureAwait(false);
        var major = await installer.GetRequiredJavaMajorAsync(instance.LaunchVersion, instance.DirectoryPath, cancellationToken).ConfigureAwait(false);
        var java = JavaDiscovery.Probe(Path.Combine(javaHome, "bin", "java"));
        if (java is null || JavaDiscovery.ParseMajorVersion(java.Version) != major)
            throw new InvalidDataException($"A complete, runnable Java {major} runtime is required. Use 'mcm java install {major}' or --java <portable-java-home>.");

        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
        EnsureNoLinks(bundlePath);
        var temporary = bundlePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var files = new List<OfflineBundleFile>();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var source in sources.OrderBy(file => file.Path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new DownloadProgress(files.Count, sources.Count, source.Path) { Stage = "Packing offline bundle (including Java and MCM)" });
                    var entry = zip.CreateEntry(source.Path, CompressionLevel.Fastest);
                    entry.ExternalAttributes = RegularAttributes(source.Executable);
                    await using var input = File.OpenRead(source.Source);
                    await using var target = entry.Open();
                    var size = input.Length;
                    var hash = await CopyAndHashAsync(input, target, size, cancellationToken).ConfigureAwait(false);
                    files.Add(new OfflineBundleFile(source.Path, size, hash, source.Executable));
                }
                var manifest = new OfflineBundleManifest("mcm-offline", 1, CurrentTargetRuntime, DateTimeOffset.UtcNow,
                    new OfflineInstanceMetadata(instance.Name, instance.GameVersion, instance.Loader, instance.LoaderVersion, instance.LaunchVersion),
                    major, JavaExecutable, LauncherPath, files);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                if (bytes.Length > MaxManifestSize) throw new InvalidDataException("Offline bundle manifest is too large.");
                var manifestEntry = zip.CreateEntry(ManifestPath);
                manifestEntry.ExternalAttributes = RegularAttributes(false);
                await using var stream = manifestEntry.Open();
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            // Validate the completed archive before publishing the output filename.
            var result = await ReadManifestAsync(temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, bundlePath, overwrite: false);
            return result;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Validates structure, bounds and every SHA-256 without installing or executing anything.</summary>
    public static async Task<OfflineBundleManifest> VerifyAsync(string bundlePath, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(bundlePath);
        var contents = await ReadContentsAsync(zip, cancellationToken).ConfigureAwait(false);
        await VerifyFilesAsync(contents, null, progress, cancellationToken).ConfigureAwait(false);
        return contents.Manifest;
    }

    public static async Task<OfflineBundleManifest> ReadManifestAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(bundlePath);
        return (await ReadContentsAsync(zip, cancellationToken).ConfigureAwait(false)).Manifest;
    }

    public static async Task<ManagedMinecraftInstance> ImportAsync(string bundlePath, string gameDirectory, string? instanceName = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var zip = ZipFile.OpenRead(bundlePath);
        var contents = await ReadContentsAsync(zip, cancellationToken).ConfigureAwait(false);
        var manifest = contents.Manifest;
        RequireSupportedPlatform(manifest.TargetRuntime);
        var name = instanceName ?? manifest.Instance.Name;
        var destination = ManagedInstanceService.GetInstanceDirectory(gameDirectory, name);
        EnsureNoLinks(destination);
        if (PathExists(destination)) throw new IOException($"Instance '{name}' already exists; offline import never overwrites an instance.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        EnsureNoLinks(parent);
        var staging = Path.Combine(parent, ".migration-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await VerifyFilesAsync(contents, staging, progress, cancellationToken).ConfigureAwait(false);
            var installer = new MinecraftInstaller();
            await installer.ValidateOfflineInstallationAsync(manifest.Instance.LaunchVersion, staging, progress, cancellationToken).ConfigureAwait(false);
            var major = await installer.GetRequiredJavaMajorAsync(manifest.Instance.LaunchVersion, staging, cancellationToken).ConfigureAwait(false);
            var java = JavaDiscovery.Probe(Path.Combine(staging, manifest.JavaExecutable.Replace('/', Path.DirectorySeparatorChar)));
            if (major != manifest.JavaMajorVersion || java is null || JavaDiscovery.ParseMajorVersion(java.Version) != major)
                throw new InvalidDataException($"Bundled Java {manifest.JavaMajorVersion} cannot run here. Check the CPU architecture, glibc and system libraries; no online download was attempted.");
            var instance = new ManagedMinecraftInstance(name, manifest.Instance.GameVersion, manifest.Instance.Loader,
                manifest.Instance.LoaderVersion, destination, manifest.Instance.LaunchVersion);
            await ManagedInstanceService.WriteMetadataAsync(staging, instance, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoLinks(destination);
            Directory.Move(staging, destination);
            return instance;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    /// <summary>Extracts only the checked bootstrap executable for the LAN server.</summary>
    public static async Task ExtractLauncherAsync(string bundlePath, string destination, CancellationToken cancellationToken = default)
    {
        destination = Path.GetFullPath(destination);
        EnsureNoLinks(destination);
        using var zip = ZipFile.OpenRead(bundlePath);
        var contents = await ReadContentsAsync(zip, cancellationToken).ConfigureAwait(false);
        var file = contents.Manifest.Files.Single(file => file.Path == contents.Manifest.LauncherPath);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = contents.Entries[file.Path].Open())
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                var hash = await CopyAndHashAsync(input, output, file.Size, cancellationToken).ConfigureAwait(false);
                CheckHash(file, hash);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<BundleContents> ReadContentsAsync(ZipArchive zip, CancellationToken cancellationToken)
    {
        if (zip.Entries.Count is < 2 or > MaxFiles + 1) throw new InvalidDataException("Invalid offline bundle entry count.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ValidateRelativePath(entry.FullName);
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000) || entry.FullName.EndsWith('/') || !entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("Offline bundles cannot contain links, special files, directories or duplicate paths.");
        }
        if (!entries.TryGetValue(ManifestPath, out var manifestEntry) || manifestEntry.FullName != ManifestPath
            || manifestEntry.Length is <= 0 or > MaxManifestSize)
            throw new InvalidDataException("Offline bundle manifest is missing or too large.");
        // Limit the decompressed manifest too, not just the ZIP header's declared length.
        await using var manifestStream = manifestEntry.Open();
        using var manifestBuffer = new MemoryStream();
        await CopyAndHashAsync(manifestStream, manifestBuffer, manifestEntry.Length, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<OfflineBundleManifest>(manifestBuffer.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Offline bundle manifest is empty.");
        if (manifest.Format != "mcm-offline" || !IsSupportedLayout(manifest) || manifest.Files is null
            || manifest.Files.Count is < 1 or > MaxFiles || manifest.Instance is null
            || manifest.JavaMajorVersion is < 8 or > 100
            || manifest.TargetRuntime is not ("linux-x64" or "linux-arm64" or "win-x64" or "win-arm64"))
            throw new InvalidDataException("Unsupported or invalid offline bundle format.");
        ValidateMetadata(manifest.Instance);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Offline bundle contains a null file entry.");
            ValidatePayloadPath(file.Path);
            if (!paths.Add(file.Path) || file.Size < 0 || file.Size > MaxFileSize || file.Size > MaxTotalSize - total
                || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit)
                || !entries.TryGetValue(file.Path, out var entry) || entry.FullName != file.Path || entry.Length != file.Size)
                throw new InvalidDataException("Offline bundle file list, size or checksum is invalid.");
            total += file.Size;
        }
        if (manifest.Layout == "pcl-windows" && paths.Count != paths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Windows offline bundle contains case-insensitive path collisions.");
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                if (paths.Contains(path[..slash])) throw new InvalidDataException("Offline bundle has a file/directory path collision.");
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        var launcherFile = manifest.Files.SingleOrDefault(file => file.Path == manifest.LauncherPath);
        var javaFile = manifest.Files.SingleOrDefault(file => file.Path == "instance/" + manifest.JavaExecutable);
        if (entries.Count != paths.Count + 1 || launcherFile is null || javaFile is null
            || !manifest.Files.Any(file => file.Path == $"instance/versions/{manifest.Instance.LaunchVersion}/{manifest.Instance.LaunchVersion}.json")
            || launcherFile.Size == 0 || javaFile.Size == 0
            || manifest.Layout is null && (!launcherFile.Executable || !javaFile.Executable))
            throw new InvalidDataException("Offline bundle is missing Java, launcher or launch profile, or contains undeclared files.");
        return new BundleContents(manifest, entries);
    }

    private static async Task VerifyFilesAsync(BundleContents contents, string? staging, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var completed = 0;
        foreach (var file in contents.Manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(completed, contents.Manifest.Files.Count, file.Path) { Stage = "Verifying offline bundle SHA-256" });
            var path = staging is not null && file.Path.StartsWith("instance/", StringComparison.Ordinal)
                ? Path.Combine(staging, file.Path[9..].Replace('/', Path.DirectorySeparatorChar)) : null;
            if (path is not null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = contents.Entries[file.Path].Open())
            await using (var output = path is not null ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true) : null)
            {
                var hash = await CopyAndHashAsync(input, output, file.Size, cancellationToken).ConfigureAwait(false);
                CheckHash(file, hash);
            }
            if (path is not null && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    | (file.Executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
            completed++;
        }
        progress?.Report(new DownloadProgress(completed, contents.Manifest.Files.Count, "") { Stage = "Offline bundle verified" });
    }

    private static void CheckHash(OfflineBundleFile file, string hash)
    {
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Offline bundle SHA-256 verification failed: {file.Path}");
    }

    private static async Task<string> CopyAndHashAsync(Stream input, Stream? output, long expectedSize, CancellationToken cancellationToken)
    {
        if (expectedSize < 0 || expectedSize > MaxFileSize) throw new InvalidDataException("Offline bundle file is too large.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(131072);
        long size = 0;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (count > expectedSize - size) throw new InvalidDataException("Offline bundle file exceeds its declared size.");
                size += count;
                hash.AppendData(buffer, 0, count);
                if (output is not null) await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            if (size != expectedSize) throw new InvalidDataException("Offline bundle file is truncated.");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static IEnumerable<(string Relative, string Source)> EnumerateFiles(string root, bool allowInternalFileLinks)
    {
        EnsureNoLinks(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Offline bundle source is not a directory: {root}");
        return Walk(root, "", new HashSet<string>(StringComparer.Ordinal), 0);

        IEnumerable<(string, string)> Walk(string directory, string prefix, HashSet<string> ancestors, int depth)
        {
            if (depth > 64 || !ancestors.Add(directory)) throw new InvalidDataException("Offline bundle source has a directory cycle or is nested too deeply.");
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (PrivateNames.Contains(entry.Name) || entry.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        || entry.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var relative = prefix + entry.Name;
                    // A distro JDK may link man to every system manual, or src.zip to a separately installed source package.
                    // Neither is required by the runtime; configuration, binaries and license notices remain included.
                    if (allowInternalFileLinks && relative is "man" or "src.zip" or "lib/src.zip") continue;
                    var source = entry.FullName;
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (!allowInternalFileLinks) throw new InvalidDataException($"Offline bundles reject game symbolic links: {entry.FullName}");
                        source = JavaDiscovery.ResolvePhysicalPath(source);
                        var directoryLink = Directory.Exists(source);
                        var inside = !Path.GetRelativePath(root, source).Split(Path.DirectorySeparatorChar).Contains("..");
                        var javaConfigOrLicense = relative is "conf" or "legal" || relative.StartsWith("conf/", StringComparison.Ordinal)
                            || relative.StartsWith("legal/", StringComparison.Ordinal);
                        if (!(inside && (!directoryLink || javaConfigOrLicense)) && !IsDistributionJavaLink(relative, source))
                            throw new InvalidDataException($"Java runtime link is outside the supported configuration/license/certificate locations: {entry.FullName}. Use a portable Java home with --java.");
                        EnsureNoLinks(source);
                        attributes = File.GetAttributes(source);
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        foreach (var child in Walk(source, relative + "/", ancestors, depth + 1)) yield return child;
                    }
                    else yield return (relative, source);
                }
            }
            finally { ancestors.Remove(directory); }
        }
    }

    private static bool IsDistributionJavaLink(string relative, string target)
    {
        if (!OperatingSystem.IsLinux()) return false;
        // Only known system-owned Java configuration, public trust stores and license notices may escape JAVA_HOME.
        // Never follow an arbitrary external file or copy /usr/lib, drivers, user files, or all of /usr/share/man.
        if (relative == "lib/security/cacerts")
            return target is "/etc/ssl/certs/java/cacerts" or "/etc/ca-certificates/extracted/java-cacerts.jks"
                or "/etc/pki/java/cacerts" or "/etc/pki/ca-trust/extracted/java/cacerts";
        if (relative is "conf" || relative.StartsWith("conf/", StringComparison.Ordinal))
        {
            var segments = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 && segments[0] == "etc" && segments[1].StartsWith("java", StringComparison.Ordinal)
                && (segments[1].Contains("openjdk", StringComparison.Ordinal) || segments[1] == "java");
        }
        if (relative is "legal" || relative.StartsWith("legal/", StringComparison.Ordinal))
            return target.StartsWith("/usr/share/licenses/", StringComparison.Ordinal) || target.StartsWith("/usr/share/doc/openjdk-", StringComparison.Ordinal);
        return false;
    }

    private static void ValidatePayloadPath(string path)
    {
        ValidateRelativePath(path);
        if (path == LauncherPath || path == WindowsLauncherPath) return;
        if (!path.StartsWith("instance/", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected offline bundle payload path.");
        var relative = path[9..];
        if (relative.Split('/').Any(PrivateNames.Contains)) throw new InvalidDataException("Private files are not allowed in offline bundles.");
        var slash = relative.IndexOf('/');
        if (GameFiles.Contains(relative) || slash > 0 && GameDirectories.Contains(relative[..slash])) return;
        if (relative.StartsWith("runtime/offline-java/", StringComparison.Ordinal)) return;
        throw new InvalidDataException("Offline bundle file is outside the portable instance allowlist.");
    }

    private static void ValidateMetadata(OfflineInstanceMetadata instance)
    {
        _ = ManagedInstanceService.GetInstanceDirectory(Path.GetTempPath(), instance.Name ?? "");
        ValidateSegment(instance.Name!);
        if (instance.LoaderVersion is not null) ValidateSegment(instance.LoaderVersion);
        ValidateSegment(instance.GameVersion);
        ValidateSegment(instance.LaunchVersion);
        if (instance.Loader is not (null or "fabric" or "quilt" or "forge" or "neoforge")) throw new InvalidDataException("Invalid offline instance loader.");
    }

    internal static void ValidateSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains('/')) throw new InvalidDataException("Invalid offline bundle path segment.");
        ValidateRelativePath(segment);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\\') || path.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
            throw new InvalidDataException("Unsafe offline bundle path.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))
                throw new InvalidDataException("Unsafe offline bundle path segment.");
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')
                throw new InvalidDataException("Offline bundle contains a reserved filename.");
        }
    }

    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Offline installation does not follow symbolic links: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RequireSupportedPlatform(string target)
    {
        if (target != "linux-x64" || CurrentTargetRuntime != target)
            throw new PlatformNotSupportedException($"Offline installation currently requires linux-x64 on both computers (bundle: {target}, this computer: {CurrentTargetRuntime}). Windows and cross-architecture installation are not implemented yet.");
    }

    private static int RegularAttributes(bool executable) => unchecked((0x8000 | (executable ? 0x1ED : 0x1A4)) << 16);
    private sealed record BundleSource(string Path, string Source, bool Executable);
    private sealed record BundleContents(OfflineBundleManifest Manifest, Dictionary<string, ZipArchiveEntry> Entries);
}
