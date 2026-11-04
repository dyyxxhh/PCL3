using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class ModLoaderInstallerTests
{
    [TestMethod]
    public async Task FabricProfileInstallsVerifiedLibrariesBeforeVanillaLibraries()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-fabric-test-" + Guid.NewGuid().ToString("N"));
        var gameVersion = "1.21.1";
        var libraryBytes = Encoding.UTF8.GetBytes("fabric loader jar");
        var sha1 = Convert.ToHexString(SHA1.HashData(libraryBytes)).ToLowerInvariant();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/versions/loader/1.21.1")
                return Json("[{\"loader\":{\"version\":\"0.16.0\",\"stable\":true}}]");
            if (path.EndsWith("/profile/json", StringComparison.Ordinal))
                return Json("{\"id\":\"fabric-loader-0.16.0-1.21.1\",\"inheritsFrom\":\"1.21.1\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.16.0\",\"url\":\"https://maven.fabricmc.net/\"}],\"arguments\":{\"game\":[\"--demo\"]}}");
            if (path.EndsWith(".sha1", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha1) };
            if (path.EndsWith("fabric-loader-0.16.0.jar", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(libraryBytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        try
        {
            var baseVersion = Path.Combine(root, "versions", gameVersion);
            Directory.CreateDirectory(baseVersion);
            var metadata = new JsonObject
            {
                ["id"] = gameVersion,
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["libraries"] = new JsonArray(
                    new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.16.0", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = "vanilla/old.jar" } } },
                    new JsonObject { ["name"] = "com.example:base:1.0", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = "com/example/base/1.0/base-1.0.jar" } } }),
                ["arguments"] = new JsonObject { ["jvm"] = new JsonArray(), ["game"] = new JsonArray() }
            };
            await File.WriteAllTextAsync(Path.Combine(baseVersion, gameVersion + ".json"), metadata.ToJsonString());
            var result = await new ModLoaderInstaller(http).InstallAsync(gameVersion, "fabric", null, root);
            Assert.AreEqual("fabric-loader-0.16.0-1.21.1", result.LaunchVersion);
            Assert.AreEqual("0.16.0", result.LoaderVersion);
            var installed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "versions", result.LaunchVersion, result.LaunchVersion + ".json")))!.AsObject();
            Assert.AreEqual(gameVersion, installed["jar"]!.GetValue<string>());
            Assert.AreEqual(result.LaunchVersion, installed["id"]!.GetValue<string>());
            Assert.AreEqual("net.fabricmc:fabric-loader:0.16.0", installed["libraries"]![0]!["name"]!.GetValue<string>());
            Assert.AreEqual("com.example:base:1.0", installed["libraries"]![1]!["name"]!.GetValue<string>());
            Assert.AreEqual("--demo", installed["arguments"]!["game"]![0]!.GetValue<string>());
            CollectionAssert.AreEqual(libraryBytes, await File.ReadAllBytesAsync(Path.Combine(root, "libraries", "net", "fabricmc", "fabric-loader", "0.16.0", "fabric-loader-0.16.0.jar")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
