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
    public void FindRuntimeReturnsMatchingExecutableFromGameDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), "pcl-java-test-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "runtime", "17", "bin", "java");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "#!/bin/sh\necho 'openjdk version \"17.0.15\"' >&2\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runtime = JavaDiscovery.FindRuntime(root, 17);
            Assert.IsNotNull(runtime);
            Assert.AreEqual(Path.GetFullPath(executable), runtime.ExecutablePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InstalledJavaIsReusedWithoutNetwork()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pcl-java-offline-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(root, "runtime", "component", "17.0.15", "bin", "java");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "#!/bin/sh\necho 'openjdk version \"17.0.15\"' >&2\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var installer = new MinecraftInstaller();
            Assert.AreEqual(executable, await installer.InstallJavaRuntimeAsync(17, root, cancellationToken: new CancellationToken(true)));
            Assert.IsNull(installer.FindInstalledJavaRuntime(21, root));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.IsNull(installer.FindInstalledJavaRuntime(17, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void JavaProbeTimesOutForHungExecutable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pcl-java-hung-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "java");
        File.WriteAllText(executable, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsNull(JavaDiscovery.Probe(executable));
            Assert.IsLessThan(TimeSpan.FromSeconds(12), timer.Elapsed);
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
