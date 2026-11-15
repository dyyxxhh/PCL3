using System.IO.Compression;
using System.Text.Json;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class McmNamingCompatibilityTests
{
    [TestMethod]
    public async Task LegacyInstancesRemainReadableAndSaveMcmMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcm-instance-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = ManagedInstanceService.GetInstanceDirectory(root, "legacy");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "pcl-instance.json"),
                """{"Name":"legacy","GameVersion":"1.21.1","Loader":"fabric","LoaderVersion":"0.16.0","LaunchVersion":"old-profile"}""");
            var instance = ManagedInstanceService.Find(root, "legacy");
            Assert.IsNotNull(instance);
            Assert.AreEqual("old-profile", instance.LaunchVersion);
            await ManagedInstanceService.SaveAsync(instance with { LaunchVersion = "new-profile" });
            Assert.IsTrue(File.Exists(Path.Combine(directory, "mcm-instance.json")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "pcl-instance.json")));
            Assert.AreEqual("new-profile", ManagedInstanceService.List(root).Single().LaunchVersion);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LegacyModManifestRemainsReadableAndUpdatesMcmManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcm-mod-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mods"));
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "sample.jar"), "mock mod");
            var mod = new InstalledMod("sample.jar", ModSource.Modrinth, "project", "Sample", "v1", "1.0", false);
            await File.WriteAllTextAsync(Path.Combine(root, "pcl-mods.json"), JsonSerializer.Serialize(new[] { mod }));
            Assert.AreEqual(mod, InstalledModService.List(root).Single());
            await InstalledModService.SetDisabledAsync(root, "sample.jar", true);
            Assert.IsTrue(File.Exists(Path.Combine(root, "mcm-mods.json")));
            var disabled = InstalledModService.List(root).Single();
            Assert.IsTrue(disabled.Disabled);
            Assert.AreEqual("project", disabled.ProjectId);
            await InstalledModService.RemoveAsync(root, disabled.FileName);
            Assert.AreEqual(0, InstalledModService.List(root).Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("mcm-instance.json")]
    [DataRow("mcm-mods.json")]
    [DataRow("pcl-instance.json")]
    [DataRow("pcl-mods.json")]
    public async Task ModpacksCannotReplaceNewOrLegacyMetadata(string fileName)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcm-pack-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, "pack.zip");
        var instance = Path.Combine(root, "instance");
        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("overrides/" + fileName).Open());
                writer.Write("{}");
            }
            using var http = new HttpClient(new NoNetworkHandler());
            var service = new ModpackService(http);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.InstallFilesAsync(archive,
                new ModpackPlan("test", "1.21.1", null, null, [], "overrides/"), instance));
            var file = new ModpackFile(fileName, "https://example.invalid/metadata", new string('a', 40));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.InstallFilesAsync(archive,
                new ModpackPlan("test", "1.21.1", null, null, [file], "overrides/"), instance));
            Assert.IsFalse(File.Exists(Path.Combine(instance, fileName)));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new AssertFailedException("Metadata must be rejected before making a network request.");
    }
}
