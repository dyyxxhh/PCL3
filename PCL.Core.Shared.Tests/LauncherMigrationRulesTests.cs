using PCL.Core.Shared;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class LauncherMigrationRulesTests
{
    [TestMethod]
    [DataRow(110, 111, 3, false)]
    [DataRow(110, 111, 10, false)]
    [DataRow(520, 521, 3, true)]
    [DataRow(520, 521, 2, false)]
    [DataRow(521, 522, 3, false)]
    [DataRow(0, 521, 3, false)]
    public void HomepageMigrationOnlyRunsWhenCrossingLegacyBoundary(int previous, int current, int preset, bool expected)
        => Assert.AreEqual(expected, LauncherMigrationRules.ShouldResetLegacyHomepage(previous, current, preset));
}
