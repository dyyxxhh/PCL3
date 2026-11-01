using PCL.Core.Shared.Java;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class MinecraftInstanceServiceTests
{
    [TestMethod]
    public void ListReturnsOnlyDirectoriesWithMatchingMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcl-shared-test-" + Guid.NewGuid().ToString("N"));
        var valid = Path.Combine(root, "versions", "1.20.1");
        var invalid = Path.Combine(root, "versions", "incomplete");
        Directory.CreateDirectory(valid);
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(valid, "1.20.1.json"), "{}");

        try
        {
            var instances = MinecraftInstanceService.List(root);
            Assert.AreEqual(1, instances.Count);
            Assert.AreEqual("1.20.1", instances[0].Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("17.0.15", 17)]
    [DataRow("1.8.0_202", 8)]
    [DataRow("8u202", 8)]
    public void JavaVersionParserHandlesRuntimeFormats(string version, int expectedMajor)
    {
        Assert.AreEqual(expectedMajor, JavaDiscovery.ParseMajorVersion(version));
    }
}
