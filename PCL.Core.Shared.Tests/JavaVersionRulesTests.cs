using PCL.Core.Shared.Java;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class JavaVersionRulesTests
{
    [TestMethod]
    [DataRow("1.8.0", "8.0.0")]
    [DataRow("1.8.0.202", "8.0.202")]
    [DataRow("17.0.15", "17.0.15")]
    public void NormalizeVersionPreservesJavaCompatibilitySemantics(string input, string expected)
    {
        Assert.AreEqual(Version.Parse(expected), JavaDiscovery.NormalizeVersion(Version.Parse(input)));
    }
}
