using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PCL.Core.Shared.Authentication;

public sealed record LauncherProfile(string Type, string UserName, string Uuid, string? Server = null, string? AccessToken = null, string? ClientToken = null);

public static class OfflineAuthentication
{
    public static LauncherProfile Create(string name)
    {
        if (name.Length is < 3 or > 16 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("用户名须为 3–16 位 ASCII 字母、数字或下划线。");
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new LauncherProfile("offline", name, Convert.ToHexString(bytes).ToLowerInvariant());
    }
}

public sealed class YggdrasilAuthentication(HttpClient client)
{
    public static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("验证服务器须使用 HTTPS 地址（本机测试可使用 HTTP），且不能含凭据、查询或片段。");
        var root = uri.AbsoluteUri.TrimEnd('/');
        return root.EndsWith("/authserver", StringComparison.OrdinalIgnoreCase) ? root[..^11] : root;
    }

    public async Task<LauncherProfile> AuthenticateAsync(string server, string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var root = NormalizeServer(server);
        var clientToken = Guid.NewGuid().ToString("N");
        using var response = await client.PostAsJsonAsync(root + "/authserver/authenticate", new { agent = new { name = "Minecraft", version = 1 }, username, password, clientToken, requestUser = true }, cancellationToken);
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? throw new InvalidDataException("验证服务器返回空响应。");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(data["errorMessage"]?.GetValue<string>() ?? $"验证失败：HTTP {(int)response.StatusCode}");
        var selected = data["selectedProfile"] ?? data["availableProfiles"]?.AsArray().FirstOrDefault();
        var id = selected?["id"]?.GetValue<string>() ?? throw new InvalidDataException("验证服务器未返回可用角色。");
        var name = selected?["name"]?.GetValue<string>() ?? throw new InvalidDataException("验证服务器未返回角色名。");
        var accessToken = data["accessToken"]?.GetValue<string>() ?? throw new InvalidDataException("验证服务器未返回访问令牌。");
        var returnedClientToken = data["clientToken"]?.GetValue<string>() ?? clientToken;
        if (data["selectedProfile"] is null)
        {
            using var refresh = await client.PostAsJsonAsync(root + "/authserver/refresh", new { accessToken, clientToken = returnedClientToken, selectedProfile = new { id, name } }, cancellationToken);
            var refreshed = await refresh.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? throw new InvalidDataException("角色选择响应为空。");
            if (!refresh.IsSuccessStatusCode) throw new InvalidOperationException(refreshed["errorMessage"]?.GetValue<string>() ?? "第三方角色选择失败。");
            accessToken = refreshed["accessToken"]?.GetValue<string>() ?? throw new InvalidDataException("刷新响应缺少访问令牌。");
        }
        return new LauncherProfile("yggdrasil", name, id, root, accessToken, returnedClientToken);
    }

    public async Task<LauncherProfile> RefreshAsync(LauncherProfile profile, CancellationToken cancellationToken = default)
    {
        var root = NormalizeServer(profile.Server ?? "");
        using var response = await client.PostAsJsonAsync(root + "/authserver/refresh", new { accessToken = profile.AccessToken, clientToken = profile.ClientToken, selectedProfile = new { id = profile.Uuid, name = profile.UserName } }, cancellationToken);
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? throw new InvalidDataException("刷新响应为空。");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(data["errorMessage"]?.GetValue<string>() ?? "第三方登录已过期，请重新登录。");
        return profile with { AccessToken = data["accessToken"]?.GetValue<string>() ?? throw new InvalidDataException("刷新响应缺少令牌。"), ClientToken = data["clientToken"]?.GetValue<string>() ?? profile.ClientToken };
    }
}
