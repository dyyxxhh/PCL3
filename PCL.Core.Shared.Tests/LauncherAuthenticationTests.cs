using System.Net;
using System.Text;
using PCL.Core.Shared.Authentication;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class LauncherAuthenticationTests
{
    [TestMethod]
    public void OfflineProfileMatchesMinecraftUuid()
    {
        var profile = OfflineAuthentication.Create("Notch");
        Assert.AreEqual("b50ad385829d3141a2167e7d7539ba7f", profile.Uuid);
        Assert.AreEqual("offline", profile.Type);
        Assert.ThrowsExactly<ArgumentException>(() => OfflineAuthentication.Create("bad name"));
    }

    [TestMethod]
    public async Task ThirdPartyAuthenticationAndRefreshUseYggdrasilEndpoints()
    {
        var handler = new FakeHandler();
        using var client = new HttpClient(handler);
        var auth = new YggdrasilAuthentication(client);
        var profile = await auth.AuthenticateAsync("https://example.org/api/yggdrasil/authserver/", "user", "password");
        Assert.AreEqual("https://example.org/api/yggdrasil/authserver/authenticate", handler.Requests[0].Uri);
        Assert.IsTrue(handler.Requests[0].Body.Contains("\"password\":\"password\""));
        Assert.AreEqual("Steve", profile.UserName);
        Assert.AreEqual("first-token", profile.AccessToken);
        var refreshed = await auth.RefreshAsync(profile);
        Assert.AreEqual("https://example.org/api/yggdrasil/authserver/refresh", handler.Requests[1].Uri);
        Assert.AreEqual("new-token", refreshed.AccessToken);
    }

    [TestMethod]
    public void ThirdPartyRejectsInsecureOrMalformedServer()
    {
        Assert.ThrowsExactly<ArgumentException>(() => YggdrasilAuthentication.NormalizeServer("http://example.org"));
        Assert.ThrowsExactly<ArgumentException>(() => YggdrasilAuthentication.NormalizeServer("https://u:p@example.org"));
        Assert.AreEqual("http://localhost:1234", YggdrasilAuthentication.NormalizeServer("http://localhost:1234/authserver"));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            var body = Requests.Count == 1
                ? "{\"accessToken\":\"first-token\",\"clientToken\":\"client\",\"selectedProfile\":{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"Steve\"}}"
                : "{\"accessToken\":\"new-token\",\"clientToken\":\"client\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
