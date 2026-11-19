using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using PCL.Core.Shared.Minecraft;

namespace PCL.Network;

internal static class TorLinkDownload
{
    internal readonly record struct Progress(long Downloaded, long? Total);

    internal static bool TryParseUrl(string input, out Uri uri)
    {
        if (Uri.TryCreate(input.Trim(), UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https" && parsed.Host.Length > 0 && parsed.UserInfo.Length == 0)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    internal static async Task SaveAsync(HttpClient torClient, Uri uri, string target, string? expectedSha256,
        IProgress<Progress>? progress, CancellationToken token)
    {
        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Missing download directory.");
        if (Path.GetFileName(target).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Invalid download file name.");
        InstanceBackupStore.RequireSafePath(target);
        if (File.Exists(target)) throw new IOException("The download destination already exists.");

        using var response = await GetResponseAsync(torClient, uri, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
            throw new HttpRequestException("The server did not return a complete file.");

        var temporary = Path.Combine(parent, $".pcl-tor-{Guid.NewGuid():N}.part");
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var created = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var hash = expectedSha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                var buffer = new byte[64 * 1024];
                var interval = Stopwatch.StartNew();
                progress?.Report(new(0, total));
                while (true)
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(45));
                    var count = await input.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (count == 0) break;
                    received += count;
                    if (received > total) throw new IOException("Download is larger than expected.");
                    hash?.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    if (interval.ElapsedMilliseconds < 200) continue;
                    progress?.Report(new(received, total));
                    interval.Restart();
                }
                if (total is not null && received != total) throw new IOException("Download length does not match.");
                if (hash is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Download SHA-256 does not match.");
                progress?.Report(new(received, total));
            }

            token.ThrowIfCancellationRequested();
            InstanceBackupStore.RequireSafePath(target);
            File.Move(temporary, target);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && idle.IsCancellationRequested)
        {
            throw new TimeoutException("The download stopped receiving data.");
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }

    // The caller disables automatic redirects so every hop receives the same validation and Tor routing.
    private static async Task<HttpResponseMessage> GetResponseAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 10; redirects++)
        {
            if (!TryParseUrl(uri.AbsoluteUri, out uri)) throw new HttpRequestException("Invalid download URL.");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;

            using (response)
            {
                if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next) ||
                    (uri.Scheme == "https" && next.Scheme != "https"))
                    throw new HttpRequestException("Unsafe or invalid download redirect.");
                uri = next;
            }
        }
        throw new HttpRequestException("Too many download redirects.");
    }
}
