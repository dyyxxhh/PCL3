using System.IO.Compression;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class LegacyModpackFormatTests
{
    [TestMethod]
    public void ReadsNestedHmclPack()
    {
        var path = CreatePack(("pack/modpack.json", "{\"name\":\"HMCL Example\",\"gameVersion\":\"1.20.1\"}"));
        try
        {
            var plan = new ModpackService(new HttpClient()).ReadPlan(path);
            Assert.AreEqual("1.20.1", plan.MinecraftVersion);
            Assert.AreEqual("pack/minecraft/", plan.OverridesPrefix);
            Assert.AreEqual("pack/", plan.ArchiveBaseFolder);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ReadsMcbbsLoaderAddons()
    {
        var path = CreatePack(("mcbbs.packmeta", "{\"name\":\"MCBBS Example\",\"addons\":[{\"id\":\"game\",\"version\":\"1.21.1\"},{\"id\":\"fabric\",\"version\":\"0.16.0\"}]}"));
        try
        {
            var plan = new ModpackService(new HttpClient()).ReadPlan(path);
            Assert.AreEqual("1.21.1", plan.MinecraftVersion);
            Assert.AreEqual("fabric", plan.Loader);
            Assert.AreEqual("0.16.0", plan.LoaderVersion);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ReadsMultiMcLoaderComponents()
    {
        var path = CreatePack(("mmc-pack.json", "{\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.20.1\"},{\"uid\":\"net.minecraftforge\",\"version\":\"47.4.0\"}]}"));
        try
        {
            var plan = new ModpackService(new HttpClient()).ReadPlan(path);
            Assert.AreEqual("1.20.1", plan.MinecraftVersion);
            Assert.AreEqual("forge", plan.Loader);
            Assert.AreEqual("47.4.0", plan.LoaderVersion);
            Assert.AreEqual(".minecraft/", plan.OverridesPrefix);
        }
        finally { File.Delete(path); }
    }

    private static string CreatePack(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "pcl-legacy-pack-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return path;
    }
}
