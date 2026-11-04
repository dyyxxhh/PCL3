using System.IO.Compression;
using System.Net;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class ModLoaderAndPackTests
{
    [TestMethod]
    public void ForgeVersionsAreBoundToTheSelectedMinecraftVersion()
    {
        var available = new[] { "1.21.1-52.0.0", "1.21.1-52.1.9", "1.21.1-52.1.21", "1.20.1-47.4.0" };
        Assert.AreEqual("1.21.1-52.1.21", ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21.1", "forge"));
        Assert.AreEqual("1.21.1-52.1.9", ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21.1", "forge", "52.1.9"));
        Assert.AreEqual("1.21.1-52.1.9", ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21.1", "forge", "1.21.1-52.1.9"));
        Assert.ThrowsExactly<InvalidOperationException>(() => ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21.1", "forge", "51.0.0"));
    }

    [TestMethod]
    public void SelectsForgeProfileByLoaderVersionSuffix()
    {
        var profiles = new[] { "1.21.1-forge-52.0.0", "1.21.1-forge-52.1.9", "1.21.1-forge-52.1.21" };
        Assert.AreEqual("1.21.1-forge-52.1.21", ForgeLikeLoaderInstaller.SelectProfileId(profiles, "52.1.21"));
        Assert.AreEqual("1.21.1-forge-52.1.9", ForgeLikeLoaderInstaller.SelectProfileId(profiles, "52.1.9"));
    }

    [TestMethod]
    public void NeoForgeVersionsUseTheOfficialMinecraftVersionPrefix()
    {
        var available = new[] { "21.1.200", "21.1.216", "21.2.1", "20.4.250" };
        Assert.AreEqual("21.1.216", ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21.1", "neoforge"));
        Assert.AreEqual("21.1.200", ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21.1", "neoforge", "200"));
    }

    [TestMethod]
    public void NeoForgeMinorReleaseDoesNotSelectAPatchVersionBuild()
    {
        var available = new[] { "21.0.100", "21.1.216", "21.2.1" };
        Assert.AreEqual("21.0.100", ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21", "neoforge"));
        Assert.ThrowsExactly<InvalidOperationException>(() => ForgeLikeLoaderInstaller.SelectMavenVersion(available, "1.21", "neoforge", "21.1.216"));
    }

    [TestMethod]
    public void ReadsModrinthPackDependenciesAndFiles()
    {
        var path = CreatePack("modrinth.index.json", """
            {"formatVersion":1,"game":"minecraft","name":"Example","versionId":"1","files":[{"path":"mods/example.jar","downloads":["https://cdn.example/mod.jar"],"hashes":{"sha1":"0123456789012345678901234567890123456789"}}],"dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.0"}}
            """);
        try
        {
            var plan = new ModpackService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))).ReadPlan(path);
            Assert.AreEqual("1.21.1", plan.MinecraftVersion);
            Assert.AreEqual("fabric", plan.Loader);
            Assert.AreEqual("0.16.0", plan.LoaderVersion);
            Assert.AreEqual("mods/example.jar", plan.Files.Single().Path);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task RejectsTraversalInModpackOverrides()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcl-pack-test-" + Guid.NewGuid().ToString("N") + ".zip");
        var root = Path.Combine(Path.GetTempPath(), "pcl-pack-target-" + Guid.NewGuid().ToString("N"));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("overrides/../outside.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("no");
        }
        try
        {
            var service = new ModpackService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await service.InstallFilesAsync(path, new ModpackPlan("bad", "1.21.1", null, null, [], "overrides/"), root));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside.txt")));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ImportedRemoteModsRemainTrackedAndExportable(bool curseForge)
    {
        var payload = Encoding.UTF8.GetBytes("test jar bytes");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload)).ToLowerInvariant();
        var archive = CreatePack("empty.json", "{}");
        var root = Path.Combine(Path.GetTempPath(), "pcl-pack-tracking-" + Guid.NewGuid().ToString("N"));
        var export = root + (curseForge ? ".zip" : ".mrpack");
        var service = new ModpackService(new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.Host == "api.curseforge.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { data = new { fileName = "example.jar", downloadUrl = "https://cdn.example/example.jar", hashes = new[] { new { algo = 1, value = hash } } } })) };
            if (request.RequestUri.Host == "api.modrinth.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"project_id":"project-one","id":"version-one"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        })), "mock-application-key");
        try
        {
            var file = curseForge ? new ModpackFile("mods/", null, null, "123", "456")
                : new ModpackFile("mods/example.jar", "https://cdn.example/example.jar", hash);
            await service.InstallFilesAsync(archive, new ModpackPlan("test", "1.21.1", "fabric", "0.16.0", [file], "overrides/"), root);
            var installed = InstalledModService.List(root).Single();
            Assert.AreEqual(curseForge ? "123" : "project-one", installed.ProjectId);
            Assert.AreEqual(curseForge ? "456" : "version-one", installed.ReleaseId);
            Assert.AreEqual(hash, installed.Sha1);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            Directory.CreateDirectory(Path.Combine(root, "kubejs"));
            Directory.CreateDirectory(Path.Combine(root, "saves"));
            await File.WriteAllTextAsync(Path.Combine(root, "config", "example.json"), "{\"enabled\":true}");
            await File.WriteAllTextAsync(Path.Combine(root, "kubejs", "startup.js"), "// pack script");
            await File.WriteAllTextAsync(Path.Combine(root, "saves", "private.txt"), "not exported");
            await File.WriteAllTextAsync(Path.Combine(root, "accounts.json"), "not exported");
            var instance = new ManagedMinecraftInstance("test", "1.21.1", "fabric", "0.16.0", root, "test");
            if (curseForge) await service.ExportCurseForgeAsync(instance, export);
            else await service.ExportModrinthAsync(instance, export);
            var plan = service.ReadPlan(export);
            using (var zip = ZipFile.OpenRead(export))
            {
                Assert.IsNotNull(zip.GetEntry("overrides/config/example.json"));
                Assert.IsNotNull(zip.GetEntry("overrides/kubejs/startup.js"));
                Assert.IsNull(zip.GetEntry("overrides/accounts.json"));
                Assert.IsNull(zip.GetEntry("overrides/saves/private.txt"));
                Assert.IsFalse(zip.Entries.Any(entry => entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)));
            }
            Assert.AreEqual(1, plan.Files.Count);
            if (curseForge) Assert.AreEqual("123", plan.Files[0].CurseForgeProject);
            else Assert.AreEqual(hash, plan.Files[0].Sha1);
        }
        finally
        {
            File.Delete(archive);
            File.Delete(export);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow("example.jar")]
    [DataRow("example-new.jar")]
    public async Task UpdatingDisabledModPreservesDisabledState(string newFileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-mod-disabled-" + Guid.NewGuid().ToString("N"));
        var payload = Encoding.UTF8.GetBytes("updated mod");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        await File.WriteAllTextAsync(Path.Combine(root, "mods", "example.jar.disabled"), "old mod");
        await InstalledModService.TrackAsync(root, new InstalledMod("example.jar.disabled", ModSource.Modrinth, "project", "Example", "old", "old", true));
        try
        {
            var platform = new ModPlatformService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })));
            await InstalledModService.InstallReleaseAsync(root, new ModRelease(ModSource.Modrinth, "project", "new", "new", newFileName, "https://cdn.example/mod.jar", hash, null), "Example", platform);
            var installed = InstalledModService.List(root).Single();
            Assert.IsTrue(installed.Disabled);
            Assert.AreEqual(newFileName + ".disabled", installed.FileName);
            Assert.AreEqual("new", installed.ReleaseId);
            Assert.IsFalse(File.Exists(Path.Combine(root, "mods", newFileName)));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(root, "mods", installed.FileName)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreatePack(string entryName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "pcl-pack-plan-" + Guid.NewGuid().ToString("N") + ".mrpack");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), Encoding.UTF8);
        writer.Write(content);
        return path;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
