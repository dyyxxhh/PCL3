using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Shared.Java;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class JavaHomeResolverTests
{
    [TestMethod]
    [DataRow("home")]
    [DataRow("parent-alias")]
    [DataRow("executable-alias")]
    public void ResolvesSystemStyleJavaAliasesToTheCompleteHome(string kind)
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux Java links."); return; }
        var root = Path.Combine(Path.GetTempPath(), "mcm-java-home-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "jvm", "java-21-openjdk");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", "java"), "fixture");
        try
        {
            var alias = Path.Combine(root, "jvm", "default-runtime");
            Directory.CreateSymbolicLink(alias, "java-21-openjdk");
            var executable = Path.Combine(root, "java");
            File.CreateSymbolicLink(executable, Path.Combine(alias, "bin", "java"));
            var input = kind == "home" ? alias : kind == "parent-alias" ? Path.Combine(alias, "bin", "java") : executable;
            Assert.AreEqual(home, JavaDiscovery.ResolveHome(input));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RejectsDirectoryWithoutAJavaExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcm-java-incomplete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Assert.ThrowsExactly<InvalidDataException>(() => JavaDiscovery.ResolveHome(root)); }
        finally { Directory.Delete(root, true); }
    }
}
