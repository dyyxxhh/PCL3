using System.Net;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class ModPlatformServiceTests
{
    [TestMethod]
    public async Task SearchWithoutCurseForgeKeyUsesModrinthOnly()
    {
        using var http = new HttpClient(new FakeHandler(request =>
        {
            Assert.IsTrue(request.RequestUri!.Host.Equals("api.modrinth.com", StringComparison.Ordinal));
            Assert.IsTrue(request.RequestUri.Query.Contains("project_type", StringComparison.OrdinalIgnoreCase));
            return Json("{\"hits\":[{\"project_id\":\"A\",\"slug\":\"sodium\",\"title\":\"Sodium\",\"description\":\"Performance\",\"downloads\":100}]} ");
        }));
        var results = await new ModPlatformService(http).SearchModsAsync("Sodium", "1.21.1", "fabric");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(ModSource.Modrinth, results[0].Source);
        Assert.AreEqual("A", results[0].Id);
    }

    [TestMethod]
    public async Task SearchWithCurseForgeKeyQueriesBothWithoutLeakingTheKey()
    {
        using var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.Host == "api.modrinth.com") return Json("{\"hits\":[]}");
            Assert.AreEqual("test-key", request.Headers.GetValues("x-api-key").Single());
            return Json("{\"data\":[{\"id\":123,\"slug\":\"sodium\",\"name\":\"Sodium\",\"summary\":\"Performance\",\"downloadCount\":20}]}");
        }));
        var results = await new ModPlatformService(http, "test-key").SearchModsAsync("Sodium", "1.21.1", "fabric");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(ModSource.CurseForge, results[0].Source);
        Assert.AreEqual("123", results[0].Id);
    }

    [TestMethod]
    public async Task ReadsOnlyRequiredModrinthDependencies()
    {
        using var http = new HttpClient(new FakeHandler(_ => Json("""
            [{"id":"release","version_number":"2.0","date_published":"2025-01-01T00:00:00Z","files":[{"filename":"example.jar","url":"https://cdn.modrinth.com/example.jar","primary":true,"hashes":{"sha1":"0123456789012345678901234567890123456789"}}],"dependencies":[{"project_id":"required-project","version_id":"required-version","dependency_type":"required"},{"project_id":"optional-project","dependency_type":"optional"}]}]
            """)));
        var project = new ModProject(ModSource.Modrinth, "main-project", "main", "Main", "", 0, null);
        var release = await new ModPlatformService(http).GetLatestCompatibleReleaseAsync(project, "1.21.1", "fabric");
        Assert.AreEqual("release", release.VersionId);
        Assert.AreEqual(1, release.RequiredDependencies!.Count);
        Assert.AreEqual("required-project", release.RequiredDependencies[0].ProjectId);
        Assert.AreEqual("required-version", release.RequiredDependencies[0].VersionId);
    }

    [TestMethod]
    public async Task InstallVerifiesSha1AndRejectsTraversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pcl-mod-test-" + Guid.NewGuid().ToString("N"));
        var content = Encoding.UTF8.GetBytes("mod bytes");
        var sha1 = Convert.ToHexString(SHA1.HashData(content));
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        try
        {
            var service = new ModPlatformService(http);
            var release = new ModRelease(ModSource.Modrinth, "project", "version", "1", "safe.jar", "https://cdn.modrinth.com/file", sha1, null);
            var installed = await service.InstallReleaseAsync(release, Path.Combine(directory, "mods"));
            Assert.AreEqual("safe.jar", Path.GetFileName(installed));
            CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(installed));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await service.InstallReleaseAsync(release with { FileName = "../escape.jar" }, Path.Combine(directory, "mods")));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await service.InstallReleaseAsync(release with { FileName = "bad.jar", Sha1 = new string('0', 40) }, Path.Combine(directory, "mods")));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "mods", "bad.jar")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task NamedInstancesAreIsolatedAndRejectTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-instances-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = ManagedInstanceService.GetInstanceDirectory(root, "fabric-1.21.1");
            await ManagedInstanceService.SaveAsync(new ManagedMinecraftInstance("fabric-1.21.1", "1.21.1", "fabric", "0.17.0", directory, "fabric-loader-0.17.0-1.21.1"));
            var instance = ManagedInstanceService.List(root).Single();
            Assert.AreEqual("fabric", instance.Loader);
            Assert.AreEqual(directory, instance.DirectoryPath);
            Assert.ThrowsExactly<ArgumentException>(() => ManagedInstanceService.GetInstanceDirectory(root, "../outside"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TrackedModsCanBeDisabledEnabledAndRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-mod-management-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "mods");
        Directory.CreateDirectory(mods);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(mods, "sample.jar"), [1, 2, 3]);
            await InstalledModService.TrackAsync(root, new InstalledMod("sample.jar", ModSource.Modrinth, "mod-id", "Sample", "v1", "1.0", false));
            await InstalledModService.SetDisabledAsync(root, "sample.jar", true);
            Assert.IsTrue(File.Exists(Path.Combine(mods, "sample.jar.disabled")));
            Assert.IsTrue(InstalledModService.List(root).Single().Disabled);
            await InstalledModService.SetDisabledAsync(root, "sample.jar.disabled", false);
            Assert.IsFalse(InstalledModService.List(root).Single().Disabled);
            await InstalledModService.RemoveAsync(root, "sample.jar");
            Assert.AreEqual(0, InstalledModService.List(root).Count);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await InstalledModService.RemoveAsync(root, "../outside.jar"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
