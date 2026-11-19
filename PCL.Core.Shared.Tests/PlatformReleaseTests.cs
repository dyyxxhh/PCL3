using PCL.Core.Shared;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class PlatformReleaseTests
{
    [TestMethod]
    public async Task SkipsWindowsOnlyReleaseForLinuxAndFindsEarlierBoth()
    {
        var responses = new Dictionary<string, string>
        {
            ["v0.4.0"] = "{\"version\":\"0.4.0\",\"platform\":\"windows\",\"releaseTag\":\"v0.4.0\"}",
            ["v0.3.8"] = "{\"version\":\"0.3.8\",\"platform\":\"both\",\"releaseTag\":\"v0.3.8\"}"
        };
        var tag = await ReleaseCatalog.FindAsync((url, _) =>
        {
            if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
                return Task.FromResult("""[{"tag_name":"v0.3.8","draft":false,"prerelease":false},{"tag_name":"v0.4.1","draft":false,"prerelease":true},{"tag_name":"v0.4.0","draft":false,"prerelease":false}]""");
            var releaseTag = url.Split("/download/")[1].Split('/')[0];
            return Task.FromResult(responses[releaseTag]);
        }, "https://github.com/dyyxxhh/PCL3", ReleasePlatformTarget.Linux, new Version(0, 3, 0));
        Assert.AreEqual("v0.3.8", tag);
    }

    [TestMethod]
    public void RejectsMismatchedOrMalformedManifest()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ReleaseCatalog.ParseManifest("{\"version\":\"0.4.0\",\"platform\":\"linux\",\"releaseTag\":\"v0.3.9\"}", "v0.4.0"));
        Assert.AreEqual(ReleasePlatformTarget.Both, ReleaseCatalog.ParseManifest("{\"version\":\"0.4.0\",\"platform\":\"both\",\"releaseTag\":\"v0.4.0\"}", "v0.4.0"));
    }
}
