using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PCL.Core.Shared.Authentication;

public static class AuthlibInjector
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    public static async Task<string> EnsureAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(gameDirectory, "pcl", "authlib-injector.jar");
        JsonObject? metadata;
        try { metadata = await Http.GetFromJsonAsync<JsonObject>("https://authlib-injector.yushi.moe/artifact/latest.json", cancellationToken); }
        catch (HttpRequestException) { metadata = await Http.GetFromJsonAsync<JsonObject>("https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json", cancellationToken); }
        if (metadata is null) throw new InvalidDataException("authlib-injector 元数据为空。");
        var url = metadata["download_url"]?.GetValue<string>() ?? throw new InvalidDataException("注入器元数据缺少下载地址。");
        var hash = metadata["checksums"]?["sha256"]?.GetValue<string>() ?? throw new InvalidDataException("注入器元数据缺少 SHA-256。");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("注入器下载地址必须使用 HTTPS。");
        if (File.Exists(path) && await VerifyAsync(path, hash, cancellationToken)) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Stream input;
            try { input = await Http.GetStreamAsync(uri, cancellationToken); }
            catch (HttpRequestException) when (uri.Host.Equals("authlib-injector.yushi.moe", StringComparison.OrdinalIgnoreCase))
            {
                input = await Http.GetStreamAsync("https://bmclapi2.bangbang93.com/mirrors/authlib-injector" + uri.PathAndQuery, cancellationToken);
            }
            await using (input)
            await using (var output = File.Create(tmp)) await input.CopyToAsync(output, cancellationToken);
            if (!await VerifyAsync(tmp, hash, cancellationToken)) throw new InvalidDataException("authlib-injector SHA-256 校验失败。");
            File.Move(tmp, path, true);
            return path;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static async Task<bool> VerifyAsync(string path, string expected, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
