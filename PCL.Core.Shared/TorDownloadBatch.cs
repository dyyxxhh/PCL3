namespace PCL.Core.Shared;

/// <summary>Retries complete transfers, including consumption and verification of the response body.</summary>
public sealed class TorDownloadBatch(TorDownloadRetryPolicy policy, Func<CancellationToken, Task<HttpClient>> getTorClient)
{
    public void RecordCachedFile(string key) => policy.RecordSuccess(key);

    public async Task DownloadAsync(string key, Func<HttpClient?, CancellationToken, Task> transfer, CancellationToken cancellationToken)
    {
        Exception? error = null;
        while (!policy.BatchTorEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await transfer(null, cancellationToken).ConfigureAwait(false);
                policy.RecordSuccess(key);
                return;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                error = ex;
                if (policy.RecordFailure(key).UseTor) break;
            }
        }

        HttpClient tor;
        try { tor = await getTorClient(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or HttpRequestException)
        {
            throw new IOException($"Tor connection failed for {Path.GetFileName(key)}. Check or change bridges before retrying.", ex);
        }
        for (var attempt = 0; attempt < policy.TorAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await transfer(tor, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken)) { error = ex; }
        }
        throw new IOException($"Download failed for {Path.GetFileName(key)} after {policy.TorAttempts} Tor attempts. Retry, change bridges, or skip this file.", error);
    }

    private static bool IsRetryable(Exception error, CancellationToken token) => !token.IsCancellationRequested &&
        error is HttpRequestException or IOException or InvalidDataException or TimeoutException or OperationCanceledException;
}
