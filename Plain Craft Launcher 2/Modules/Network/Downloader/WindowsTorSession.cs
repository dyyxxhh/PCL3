using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PCL.Core.Shared;
using PCL.Core.Shared.Minecraft;

namespace PCL.Network;

internal sealed class WindowsTorSession : IDisposable
{
    internal sealed record Options(bool Enabled = true, bool AutoBridges = true, string[]? Bridges = null);
    private readonly SemaphoreSlim _gate = new(1);
    private TorNetworkService? _service;
    private HttpClient? _client;
    internal Options Settings { get; } = Load();
    internal bool Connected => _client is not null;

    private static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL3", "tor.json");

    internal static Options Load()
    {
        InstanceBackupStore.RequireSafePath(ConfigPath);
        return File.Exists(ConfigPath) ? JsonSerializer.Deserialize<Options>(File.ReadAllText(ConfigPath)) ?? new() : new();
    }

    internal static void Save(Options settings)
    {
        var path = ConfigPath;
        InstanceBackupStore.RequireSafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, settings);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    // Narrow opt-in using known public download origins. Account and LAN installation traffic stay on the original clients.
    internal static bool IsPublic(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) return false;
        var host = uri.IdnHost;
        return host is "github.com" or "api.github.com" or "api.modrinth.com" or "cdn.modrinth.com" or "bmclapi2.bangbang93.com" or
            "files.minecraftforge.net" or "piston-meta.mojang.com" or "resources.download.minecraft.net" or "meta.fabricmc.net" or
            "meta.quiltmc.org" or "maven.neoforged.net" or "meta.prismlauncher.org" or "api.adoptium.net" ||
            new[] { ".mojang.com", ".githubusercontent.com", ".minecraft.net", ".forgecdn.net", ".minecraftforge.net", ".fabricmc.net", ".azul.com" }
                .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal TorDownloadBatch CreateBatch(IEnumerable<string> paths)
    {
        var policy = new TorDownloadRetryPolicy();
        policy.ObserveInitialFiles(paths);
        return new TorDownloadBatch(policy, GetClientAsync);
    }

    internal async Task<HttpClient> GetClientAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_client is not null) return _client;
            _service = await TorNetworkService.StartAsync(Settings.Bridges, token, Settings.AutoBridges).ConfigureAwait(false);
            return _client = _service.CreateHttpClient();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _service?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _gate.Dispose();
    }
}
