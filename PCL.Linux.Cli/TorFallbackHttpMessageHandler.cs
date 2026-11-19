using System.Net;
using PCL.Core.Shared;

namespace PCL.Linux.Cli;

// Use only for clients constructed explicitly for public resources. Authentication has its own HTTP clients.
internal sealed class TorFallbackHttpMessageHandler(string? instance = null) : HttpMessageHandler
{
    private readonly HttpClient _direct = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), UseCookies = false });
    private readonly SemaphoreSlim _startup = new(1);
    private TorNetworkService? _tor;
    private HttpClient? _torClient;

    internal HttpClient CreateClient() => new(this, disposeHandler: false)
    {
        Timeout = TimeSpan.FromMinutes(8),
        DefaultRequestHeaders = { UserAgent = { new("MCM", McmInfo.ReleaseVersion) } }
    };

    internal static HttpClient CreateResourceClient(string? instance = null) => new(new TorFallbackHttpMessageHandler(instance))
    {
        Timeout = TimeSpan.FromMinutes(8),
        DefaultRequestHeaders = { UserAgent = { new("MCM", McmInfo.ReleaseVersion) } }
    };

    internal PCL.Core.Shared.Minecraft.MinecraftInstaller CreateInstaller(HttpClient resources) =>
        new(resources, Enabled ? GetTorClientAsync : null, Enabled ? _direct : null);

    internal bool Enabled => !bool.TryParse(McmConfiguration.Get("tor.enabled", instance), out var enabled) || enabled;

    internal async Task<HttpClient> GetTorClientAsync(CancellationToken cancellationToken)
    {
        await _startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_torClient is not null) return _torClient;
            _tor = await TorNetworkService.StartAsync(McmConfiguration.Bridges(instance), cancellationToken,
                autoBridges: !string.Equals(McmConfiguration.Get("tor.auto_bridges", instance), "false", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            _torClient = _tor.CreateHttpClient();
            _torClient.DefaultRequestHeaders.UserAgent.ParseAdd($"MCM/{McmInfo.ReleaseVersion}");
            return _torClient;
        }
        finally { _startup.Release(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (!Enabled || request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            return await _direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var label = request.RequestUri?.AbsolutePath ?? "resource";
        var policy = new TorDownloadRetryPolicy();
        policy.ObserveInitialFiles([label]);
        HttpResponseMessage? result = null;
        await new TorDownloadBatch(policy, GetTorClientAsync).DownloadAsync(label, async (tor, ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await (tor ?? _direct).SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Forbidden or (HttpStatusCode)429 or >= HttpStatusCode.InternalServerError)
            {
                response.Dispose();
                throw new HttpRequestException("Public resource request failed.", null, response.StatusCode);
            }
            result = response;
        }, token).ConfigureAwait(false);
        return result!;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _torClient?.Dispose();
            _direct.Dispose();
            _tor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _startup.Dispose();
        }
        base.Dispose(disposing);
    }
}
