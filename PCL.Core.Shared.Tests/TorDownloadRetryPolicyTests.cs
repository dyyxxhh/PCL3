using PCL.Core.Shared;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class TorDownloadRetryPolicyTests
{
    [TestMethod]
    public void FirstFiveFilesTriggerBatchFallbackOnlyAfterEachFiveFailures()
    {
        var policy = new TorDownloadRetryPolicy();
        policy.ObserveInitialFiles(["a", "b", "c", "d", "e"]);
        for (var i = 0; i < 4; i++)
            foreach (var file in new[] { "a", "b", "c", "d", "e" }) Assert.IsFalse(policy.RecordFailure(file).SwitchRemainingFiles);
        foreach (var file in new[] { "a", "b", "c", "d" }) Assert.IsFalse(policy.RecordFailure(file).SwitchRemainingFiles);
        Assert.IsTrue(policy.RecordFailure("e").SwitchRemainingFiles);
        Assert.IsTrue(policy.BatchTorEnabled);
    }

    [TestMethod]
    public void LaterFileUsesIsolatedTorAfterTwoDirectFailures()
    {
        var policy = new TorDownloadRetryPolicy();
        policy.ObserveInitialFiles(["a"]);
        Assert.IsFalse(policy.RecordFailure("later").UseTor);
        var decision = policy.RecordFailure("later");
        Assert.IsTrue(decision.UseTor);
        Assert.IsFalse(decision.SwitchRemainingFiles);
        Assert.IsTrue(decision.RetryDirect == false);
    }
}
