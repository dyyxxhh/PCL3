using System.Net;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;
using PCL.Linux.Cli;

namespace PCL.Core.Shared.Tests;

[TestClass]
[DoNotParallelize]
public sealed class McmFeaturesTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcm-features-" + Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(_root);

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void ConfigMigratesAndResetsOverridesWithoutDroppingUnknownKeys()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var gameDir = Environment.GetEnvironmentVariable("PCL_GAME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
            Environment.SetEnvironmentVariable("PCL_GAME_DIR", null);
            Directory.CreateDirectory(Path.GetDirectoryName(McmConfiguration.ConfigPath)!);
            File.WriteAllText(McmConfiguration.ConfigPath, """{"future":42,"backup.retention":7}""");
            Assert.AreEqual(0, McmConfiguration.Run(["set", "backup.retention", "2", "--instance", "test"]));
            Assert.AreEqual("2", McmConfiguration.Get("backup.retention", "test"));
            Assert.AreEqual("7", McmConfiguration.Get("backup.retention"));
            Assert.AreEqual("auto", McmConfiguration.Get("memory.max_mib", "test"));
            Assert.AreEqual(0, McmConfiguration.Run(["set", "memory.max_mib", "4096"]));
            Assert.AreEqual(0, McmConfiguration.Run(["set", "memory.max_mib", "6144", "--instance", "test"]));
            Assert.AreEqual("6144", McmConfiguration.Get("memory.max_mib", "test"));
            Assert.AreEqual("4096", McmConfiguration.Get("memory.max_mib"));
            Assert.AreEqual(42, McmConfiguration.Load()["future"]!.GetValue<int>());
            Assert.AreEqual(1, McmConfiguration.Load()["schema_version"]!.GetValue<int>());
            Assert.AreEqual(0, McmConfiguration.Run(["reset", "--instance", "test"]));
            Assert.AreEqual("7", McmConfiguration.Get("backup.retention", "test"));
            Assert.AreEqual("4096", McmConfiguration.Get("memory.max_mib", "test"));
            Assert.AreEqual(0, McmConfiguration.Run(["set", "game_dir", Path.Combine(_root, "configured")]));
            Assert.AreEqual(Path.Combine(_root, "configured"), McmConfiguration.ResolveGameDirectory());
            Environment.SetEnvironmentVariable("PCL_GAME_DIR", Path.Combine(_root, "env"));
            Assert.AreEqual(Path.Combine(_root, "env"), McmConfiguration.ResolveGameDirectory());
            Assert.AreEqual(Path.Combine(_root, "explicit"), McmConfiguration.ResolveGameDirectory(Path.Combine(_root, "explicit")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable("PCL_GAME_DIR", gameDir);
        }
    }

    [TestMethod]
    [DataRow(4, 1024)]
    [DataRow(8, 2048)]
    [DataRow(16, 4096)]
    [DataRow(32, 4096)]
    public void AutomaticHeapSizeUsesAvailableMemoryWithAnUpperLimit(int gib, int expectedMiB)
    {
        Assert.AreEqual(expectedMiB, McmConfiguration.ResolveMaxHeapMiB("auto", gib * (1L << 30)));
        Assert.AreEqual(6144, McmConfiguration.ResolveMaxHeapMiB("6144", gib * (1L << 30)));
        Assert.ThrowsExactly<ArgumentException>(() => McmConfiguration.ResolveMaxHeapMiB("256", 0));
    }

    [TestMethod]
    public async Task BackupRetainsManualCopiesAndRestoresAsNewOrInPlace()
    {
        var directory = ManagedInstanceService.GetInstanceDirectory(_root, "test");
        var instance = new ManagedMinecraftInstance("test", "1.21", null, null, directory, "1.21");
        await ManagedInstanceService.SaveAsync(instance);
        var saves = Path.Combine(directory, "saves", "world");
        Directory.CreateDirectory(saves);
        var file = Path.Combine(saves, "level.dat");
        await File.WriteAllTextAsync(file, "previous world");
        var backup = await InstanceBackupService.CreateAsync(instance, false, "saves");
        await File.WriteAllTextAsync(file, "current world");
        var clone = await InstanceBackupService.RestoreAsync(instance, backup, true);
        Assert.IsNotNull(ManagedInstanceService.Find(_root, clone));
        Assert.AreEqual("previous world", await File.ReadAllTextAsync(Path.Combine(_root, "instances", clone, "saves", "world", "level.dat")));
        Assert.AreEqual("current world", await File.ReadAllTextAsync(file));
        await InstanceBackupService.RestoreAsync(instance, backup, false);
        Assert.AreEqual("previous world", await File.ReadAllTextAsync(file));
        Assert.HasCount(2, InstanceBackupService.List(instance));

        var store = new InstanceBackupStore(Path.Combine(_root, "retention"));
        var manual = await store.CreateAsync(saves, "test", "1.21", "saves", false);
        var old = await store.CreateAsync(saves, "test", "1.21", "saves", true, retention: 1);
        var latest = await store.CreateAsync(saves, "test", "1.21", "saves", true, retention: 1);
        CollectionAssert.AreEquivalent(new[] { manual.Path, latest.Path }, store.List().ToArray());
        Assert.IsFalse(Directory.Exists(old.Path));
    }

    [TestMethod]
    public async Task BadBackupPayloadAndSymlinkDoNotReplaceLiveFiles()
    {
        var directory = ManagedInstanceService.GetInstanceDirectory(_root, "test");
        var instance = new ManagedMinecraftInstance("test", "1.21", null, null, directory, "1.21");
        await ManagedInstanceService.SaveAsync(instance);
        var saves = Path.Combine(directory, "saves");
        Directory.CreateDirectory(saves);
        var file = Path.Combine(saves, "level.dat");
        await File.WriteAllTextAsync(file, "live");
        var backup = await InstanceBackupService.CreateAsync(instance, false, "saves");
        await File.WriteAllTextAsync(Path.Combine(backup, "data", "level.dat"), "modified backup");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => InstanceBackupService.RestoreAsync(instance, backup, false));
        Assert.AreEqual("live", await File.ReadAllTextAsync(file));
        if (OperatingSystem.IsLinux())
        {
            File.CreateSymbolicLink(Path.Combine(saves, "unsafe"), Path.Combine(_root, "nonexistent"));
            await Assert.ThrowsExactlyAsync<IOException>(() => InstanceBackupService.CreateAsync(instance, false, "saves"));
        }
    }

    [TestMethod]
    public async Task SkillInstallsSkipsPreservesOtherSkillsAndRejectsWrongHash()
    {
        var bytes = Encoding.UTF8.GetBytes("---\nname: mcm\ndescription: test\n---\n");
        using var client = new HttpClient(new SkillHandler(bytes));
        var targetPath = Path.Combine(_root, "codex");
        var otherSkill = Path.Combine(targetPath, "other", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(otherSkill)!);
        File.WriteAllText(otherSkill, "keep");
        (string, string, bool)[] destinations = [("test", targetPath, true), ("skipped", Path.Combine(_root, "skipped"), false)];
        using var output = new StringWriter();
        Assert.AreEqual(0, await SkillCommands.InstallAsync(client, destinations, output));
        Assert.AreEqual(0, await SkillCommands.InstallAsync(client, destinations, output));
        Assert.AreEqual("keep", File.ReadAllText(otherSkill));
        Assert.IsFalse(Directory.Exists(destinations[1].Item2));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(targetPath, "mcm", "SKILL.md")));
        using var corrupted = new HttpClient(new SkillHandler(bytes, valid: false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SkillCommands.InstallAsync(corrupted, destinations, output));
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        Assert.AreEqual(1, await SkillCommands.InstallAsync(client, [("blocked", blocked, true)], output));
    }

    private sealed class SkillHandler(byte[] content, bool valid = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256")
                ? new StringContent(valid ? Convert.ToHexString(SHA256.HashData(content)) : new string('0', 64))
                : new ByteArrayContent(content) });
    }
}
