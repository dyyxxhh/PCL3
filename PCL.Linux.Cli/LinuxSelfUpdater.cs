using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PCL.Core.Shared;

namespace PCL.Linux.Cli;

internal static class LinuxSelfUpdater
{
    private const string RepositoryUrl = McmInfo.RepositoryUrl;
    private const string ExecutableName = "mcm";
    private const string AssetName = "MCM_Release_linux-x64.tar.gz";

    public static async Task<int> UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !string.IsNullOrEmpty(AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string))
            throw new NotSupportedException("Self-update requires the published single-file Linux x64 mcm executable.");

        var localVersionText = McmInfo.ReleaseVersion;
        if (!TryParseReleaseVersion(localVersionText, out var localVersion))
            throw new NotSupportedException("Self-update requires a versioned official release build.");
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unavailable.");
        var target = File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(processPath);
        try
        {
            using var http = TorFallbackHttpMessageHandler.CreateResourceClient();
            return await UpdateTargetAsync(http, target, localVersionText, localVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("MCM update timed out; the original executable was preserved. Please retry.");
        }
    }

    internal static async Task<int> UpdateTargetAsync(HttpClient http, string target, string localVersionText, Version localVersion, CancellationToken cancellationToken = default, TextWriter? progressOutput = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Self-update is supported on Linux only.");
        var targetDirectory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("The executable directory is unavailable.");
        Console.WriteLine("Checking MCM updates...");
        var tag = await FindLatestApplicableTagAsync(http, localVersion, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            Console.WriteLine($"Already up to date for Linux (current {localVersionText}).");
            return 0;
        }
        if (!TryParseReleaseVersion(tag, out var remoteVersion) || remoteVersion <= localVersion)
            throw new InvalidDataException("The applicable release tag is not a supported version.");
        var assetUrl = $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}";
        Console.WriteLine("Fetching release SHA-256...");
        var expectedHash = ParseSha256(await http.GetStringAsync(assetUrl + ".sha256", cancellationToken).ConfigureAwait(false));
        var archivePath = Path.Combine(Path.GetTempPath(), "mcm-update-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        var stagedPath = Path.Combine(targetDirectory, ".mcm-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Console.WriteLine($"Downloading MCM Linux {tag}...");
            await DownloadArchiveAsync(http, assetUrl, archivePath, progressOutput, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("Verifying update SHA-256...");
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update archive SHA-256 verification failed.");

            Console.WriteLine("Extracting verified MCM update...");
            await using (var archive = File.OpenRead(archivePath))
            await using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
            using (var reader = new TarReader(gzip))
            {
                TarEntry? entry;
                var extracted = false;
                while ((entry = reader.GetNextEntry()) is not null)
                {
                    if (entry.Name is not (ExecutableName or "./mcm")) continue;
                    if (extracted || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                        throw new InvalidDataException("Invalid mcm entry in update archive.");
                    await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
                    await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    extracted = true;
                }
                if (!extracted) throw new InvalidDataException("Update archive does not contain mcm.");
            }

            if ((await ComputeSha256Async(stagedPath, cancellationToken).ConfigureAwait(false)).Equals(
                    await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Already running {tag}.");
                return 0;
            }
            File.SetUnixFileMode(stagedPath, File.GetUnixFileMode(target));
            File.Move(stagedPath, target, overwrite: true);
            Console.WriteLine($"Updated to {tag}. Run mcm again to use the new version.");
            return 0;
        }
        finally
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
        }
    }

    private static Task<string?> FindLatestApplicableTagAsync(HttpClient http, Version localVersion, CancellationToken cancellationToken)
        => ReleaseCatalog.FindAsync((url, ct) => http.GetStringAsync(url, ct), RepositoryUrl,
            ReleasePlatformTarget.Linux, new Version(localVersion.Major, localVersion.Minor, localVersion.Build), cancellationToken);

    internal static async Task DownloadArchiveAsync(HttpClient http, string url, string path,
        TextWriter? progressOutput, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead stops HttpClient's timeout after the headers. Keep its deadline for the body too.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (http.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(http.Timeout);
        var token = timeout.Token;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        using var progress = new ConsoleDownloadProgress(total, progressOutput ?? Console.Error,
            progressOutput is null && !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("TERM") != "dumb");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var archive = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        long downloaded = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            await archive.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            downloaded += count;
            if (total is { } length && downloaded > length)
                throw new InvalidDataException("Update download exceeded its declared Content-Length.");
            progress.Report(downloaded);
        }
        if (total is { } expected && downloaded != expected)
            throw new InvalidDataException("Update download ended before its declared Content-Length.");
        progress.Report(downloaded, completed: true);
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

}
