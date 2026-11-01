using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCL.Core.Shared.Authentication;

public sealed record MinecraftAccount(string UserName, string Uuid, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, int ExpiresIn, int PollInterval);

public sealed class MicrosoftMinecraftAuth
{
    private const string DeviceEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string ClientIdEnvironmentVariable = "PCL_MS_CLIENT_ID";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly string _clientId;

    public MicrosoftMinecraftAuth(string? clientId = null)
    {
        _clientId = clientId ?? Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException($"Microsoft login requires {ClientIdEnvironmentVariable}.");
    }

    public async Task<(DeviceCodePrompt Prompt, Func<CancellationToken, Task<MinecraftAccount>> Complete)> BeginDeviceLoginAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostAsync(DeviceEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = "XboxLive.signin offline_access"
        }), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<DeviceResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Microsoft device code response was empty.");
        if (data.Error is not null) throw new InvalidOperationException($"Microsoft device login failed: {data.ErrorDescription ?? data.Error}");
        var prompt = new DeviceCodePrompt(data.UserCode ?? throw new InvalidDataException("Microsoft response has no user code."), data.VerificationUri ?? data.VerificationUriComplete ?? throw new InvalidDataException("Microsoft response has no verification URI."), data.ExpiresIn, data.Interval);
        return (prompt, token => CompleteDeviceLoginAsync(data, token));
    }

    private async Task<MinecraftAccount> CompleteDeviceLoginAsync(DeviceResponse device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, device.Interval));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = device.DeviceCode ?? string.Empty
            }), cancellationToken).ConfigureAwait(false);
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Microsoft token response was empty.");
            if (token.AccessToken is not null)
                return await CompleteXboxAsync(token.AccessToken, token.RefreshToken ?? string.Empty, token.ExpiresIn, cancellationToken).ConfigureAwait(false);
            if (token.Error is not ("authorization_pending" or "slow_down"))
                throw new InvalidOperationException($"Microsoft device login failed: {token.ErrorDescription ?? token.Error}");
            if (token.Error == "slow_down") interval += TimeSpan.FromSeconds(5);
        }
        throw new TimeoutException("Microsoft device login expired.");
    }

    public async Task<MinecraftAccount> RefreshAsync(MinecraftAccount account, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken)) throw new InvalidOperationException("Account has no refresh token.");
        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken,
            ["scope"] = "XboxLive.signin offline_access"
        }), cancellationToken).ConfigureAwait(false);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Microsoft refresh response was empty.");
        if (token.AccessToken is null) throw new InvalidOperationException($"Microsoft refresh failed: {token.ErrorDescription ?? token.Error}");
        return await CompleteXboxAsync(token.AccessToken, token.RefreshToken ?? account.RefreshToken, token.ExpiresIn, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MinecraftAccount> CompleteXboxAsync(string microsoftToken, string refreshToken, int expiresIn, CancellationToken cancellationToken)
    {
        var xbl = await PostJsonAsync<XboxResponse>("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + microsoftToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, cancellationToken).ConfigureAwait(false);
        var xsts = await PostJsonAsync<XboxResponse>("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xbl.Token } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, cancellationToken).ConfigureAwait(false);
        var userHash = xsts.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash;
        if (string.IsNullOrWhiteSpace(userHash) || string.IsNullOrWhiteSpace(xsts.Token)) throw new InvalidOperationException("Xbox Live did not return a usable account token.");
        var minecraft = await PostJsonAsync<MinecraftTokenResponse>("https://api.minecraftservices.com/authentication/login_with_xbox", new { identityToken = $"XBL3.0 x={userHash};{xsts.Token}" }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(minecraft.AccessToken)) throw new InvalidOperationException("Minecraft authentication returned no token.");
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraft.AccessToken);
        using var profileResponse = await Http.SendAsync(profileRequest, cancellationToken).ConfigureAwait(false);
        profileResponse.EnsureSuccessStatusCode();
        var profile = await profileResponse.Content.ReadFromJsonAsync<ProfileResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Minecraft profile response was empty.");
        return new MinecraftAccount(profile.Name ?? "Player", profile.Id ?? throw new InvalidDataException("Minecraft profile has no id."), minecraft.AccessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(minecraft.ExpiresIn > 0 ? minecraft.ExpiresIn : expiresIn));
    }

    private static async Task<T> PostJsonAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsJsonAsync(url, body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"Empty response from {url}.");
    }

    private sealed record DeviceResponse(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval = 5,
        [property: JsonPropertyName("error")] string? Error = null,
        [property: JsonPropertyName("error_description")] string? ErrorDescription = null);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record XboxResponse(string Token, XboxClaims? DisplayClaims);
    private sealed record XboxClaims(List<XboxUser>? Xui);
    private sealed record XboxUser([property: JsonPropertyName("uh")] string UserHash);
    private sealed record MinecraftTokenResponse([property: JsonPropertyName("access_token")] string? AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);
    private sealed record ProfileResponse(string? Id, string? Name);
}
