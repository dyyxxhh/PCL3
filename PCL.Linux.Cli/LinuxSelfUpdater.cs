using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PCL.Linux.Cli;

internal static class LinuxSelfUpdater
{
    private const string RepositoryUrl = "https://github.com/dyyxxhh/PCL3";
    private const string AssetName = "PCL3_Release_linux-x64.tar.gz";
    private static readonly HttpClient Http = CreateHttpClient();

    public static async Task<int> UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !string.IsNullOrEmpty(AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string))
            throw new NotSupportedException("Self-update requires the published single-file Linux x64 pcl executable.");

        var localVersionText = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PclReleaseVersion")?.Value;
        if (!TryParseReleaseVersion(localVersionText, out var localVersion))
            throw new NotSupportedException("Self-update requires a versioned official release build.");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        var target = File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(processPath);
        return await UpdateTargetAsync(Http, target, localVersionText!, localVersion, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> UpdateTargetAsync(HttpClient http, string target, string localVersionText, Version localVersion, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Self-update is supported on Linux only.");
        var targetDirectory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("The executable directory is unavailable.");
        var html = await http.GetStringAsync(RepositoryUrl + "/releases/latest", cancellationToken).ConfigureAwait(false);
        var tag = ParseReleaseTag(html);
        if (!TryParseReleaseVersion(tag, out var remoteVersion))
            throw new InvalidDataException("The latest release tag is not a supported version.");
        if (remoteVersion <= localVersion)
        {
            Console.WriteLine($"Already up to date (current {localVersionText}, latest {tag}).");
            return 0;
        }
        var assetUrl = $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}";
        var expectedHash = ParseSha256(await http.GetStringAsync(assetUrl + ".sha256", cancellationToken).ConfigureAwait(false));
        var archivePath = Path.Combine(Path.GetTempPath(), "pcl-update-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        var stagedPath = Path.Combine(targetDirectory, ".pcl-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Console.WriteLine($"Downloading PCL Linux {tag}...");
            await using (var source = await http.GetStreamAsync(assetUrl, cancellationToken).ConfigureAwait(false))
            await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await source.CopyToAsync(archive, cancellationToken).ConfigureAwait(false);
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update archive SHA-256 verification failed.");

            await using (var archive = File.OpenRead(archivePath))
            await using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
            using (var reader = new TarReader(gzip))
            {
                TarEntry? entry;
                var extracted = false;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    if (entry.Name is not ("pcl" or "./pcl")) continue;
                    if (extracted || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                        throw new InvalidDataException("Invalid pcl entry in update archive.");
                    await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    extracted = true;
                }
                if (!extracted) throw new InvalidDataException("Update archive does not contain pcl.");
            }

            if ((await ComputeSha256Async(stagedPath, cancellationToken).ConfigureAwait(false)).Equals(
                    await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Already running {tag}.");
                return 0;
            }
            File.SetUnixFileMode(stagedPath, File.GetUnixFileMode(target));
            File.Move(stagedPath, target, overwrite: true);
            Console.WriteLine($"Updated to {tag}. Run pcl again to use the new version.");
            return 0;
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
        }
    }

    internal static bool TryParseReleaseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
        if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(parsed.Revision, 0));
        return true;
    }

    internal static string ParseSha256(string text)
    {
        var checksum = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (checksum is null || checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release checksum is invalid.");
        return checksum;
    }

    internal static string ParseReleaseTag(string html)
    {
        var match = Regex.Match(html, "https://github\\.com/dyyxxhh/PCL3/releases/tag/(?<tag>[^\"'< >\\s?#]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("The latest release page did not contain a release tag.");
        return Uri.UnescapeDataString(match.Groups["tag"].Value);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 PCL3-Linux-Updater/1.0");
        return client;
    }
}
