using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class MinecraftInstanceMigrationTests
{
    private string _root = null!;
    private string Source => Path.Combine(_root, "PCL", ".minecraft");
    private string Destination => Path.Combine(_root, "pcl3");

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "pcl-migration-test-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public void DefaultsPreferHomeThenExecutableAndNeverCurrentDirectory()
    {
        var home = Path.Combine(_root, "home");
        var executable = Path.Combine(_root, "executable");
        var homeGame = Path.Combine(home, ".minecraft");
        var portableGame = Path.Combine(executable, ".minecraft");
        Assert.AreEqual(homeGame, MinecraftInstanceService.ResolveDefaultGameDirectory(home, executable));
        Directory.CreateDirectory(portableGame);
        Assert.AreEqual(portableGame, MinecraftInstanceService.ResolveDefaultGameDirectory(home, executable));
        Directory.CreateDirectory(homeGame);
        Assert.AreEqual(homeGame, MinecraftInstanceService.ResolveDefaultGameDirectory(home, executable));
        CollectionAssert.AreEqual(new[] { homeGame, portableGame }, MinecraftInstanceService.GetDefaultGameDirectories(home, executable).ToArray());
    }

    [TestMethod]
    public async Task MigratesNonIsolatedPclDataAndFlattensInheritedLoader()
    {
        CreateVersion("1.20.1", Vanilla("1.20.1"), jar: true);
        CreateVersion("我的 Forge 整合包", """
            {"id":"forge-profile","inheritsFrom":"1.20.1","mainClass":"cpw.mods.bootstraplauncher.BootstrapLauncher",
             "libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.4.0:client"}],
             "arguments":{"game":["--fml.mcVersion","1.20.1"],"jvm":["-Dloader=true"]}}
            """, jsonName: "forge.json");
        Write(Source, "mods/example.jar", "mod");
        Write(Source, "config/mod.toml", "config");
        Write(Source, "saves/world/level.dat", "world");
        Write(Source, "options.txt", "options");
        Write(Source, "libraries/example/base.jar", "library");
        Write(Source, "assets/objects/example", "asset");
        Write(Source, "launcher_accounts.json", "private account fixture");
        Write(Source, "PCL/Setup.ini", "private launcher settings fixture");
        var scan = MinecraftInstanceMigration.Scan(Path.GetDirectoryName(Source)!);
        Assert.AreEqual(0, scan.Diagnostics.Count);
        var selected = scan.Instances.Single(item => item.Id == "我的 Forge 整合包");
        Assert.AreEqual(Source, selected.DataDirectory);
        Assert.AreEqual("forge", selected.Loader);
        Assert.AreEqual("47.4.0", selected.LoaderVersion);
        Assert.AreEqual("1.20.1", selected.GameVersion);

        var result = await MinecraftInstanceMigration.MigrateAsync(selected, Destination, "forge-import");

        Assert.AreEqual(result, ManagedInstanceService.Find(Destination, "forge-import"));
        foreach (var file in new[] { "mods/example.jar", "config/mod.toml", "saves/world/level.dat", "options.txt", "libraries/example/base.jar", "assets/objects/example" })
            Assert.AreEqual(File.ReadAllText(Path.Combine(Source, file)), File.ReadAllText(Path.Combine(result.DirectoryPath, file)));
        Assert.IsFalse(File.Exists(Path.Combine(result.DirectoryPath, "launcher_accounts.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(result.DirectoryPath, "PCL")));
        var merged = JsonNode.Parse(File.ReadAllText(Path.Combine(result.DirectoryPath, "versions", result.LaunchVersion, result.LaunchVersion + ".json")))!;
        Assert.IsNull(merged["inheritsFrom"]);
        Assert.AreEqual("1.20.1", merged["jar"]!.GetValue<string>());
        Assert.AreEqual("cpw.mods.bootstraplauncher.BootstrapLauncher", merged["mainClass"]!.GetValue<string>());
        Assert.AreEqual(2, merged["libraries"]!.AsArray().Count);
        Assert.AreEqual(3, merged["arguments"]!["game"]!.AsArray().Count);
        Assert.AreEqual("client jar", File.ReadAllText(Path.Combine(result.DirectoryPath, "versions", "1.20.1", "1.20.1.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(Source, "versions", "我的 Forge 整合包", "forge.json")));
        Assert.IsFalse(Directory.EnumerateDirectories(Path.Combine(Destination, "instances")).Any(path => Path.GetFileName(path).StartsWith(".migration-")));
    }

    [TestMethod]
    [DataRow("Setup.ini", "VersionArgumentIndie:2\n", false)]
    [DataRow("Setup.ini", "VersionArgumentIndie:1\n", true)]
    [DataRow("Setup.ini", "VersionArgumentIndieV2:False\n", false)]
    [DataRow("config.v1.yml", "VersionArgumentIndieV2: true\n", true)]
    public async Task HonorsExplicitPclIsolationSetting(string config, string setting, bool isolated)
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        var version = Path.Combine(Source, "versions", "1.21.1");
        Write(version, "PCL/" + config, setting);
        Write(Source, "mods/shared.jar", "shared");
        Write(version, "mods/local.jar", "local");
        var selected = MinecraftInstanceMigration.Scan(Source).Instances.Single();
        Assert.IsFalse(selected.AmbiguousDataDirectory);
        Assert.AreEqual(isolated ? version : Source, selected.DataDirectory);
        var migrated = await MinecraftInstanceMigration.MigrateAsync(selected, Destination, "isolation-test");
        Assert.IsTrue(File.Exists(Path.Combine(migrated.DirectoryPath, "mods", isolated ? "local.jar" : "shared.jar")));
        Assert.IsFalse(File.Exists(Path.Combine(migrated.DirectoryPath, "mods", isolated ? "shared.jar" : "local.jar")));
    }

    [TestMethod]
    public async Task RequiresChoiceWhenBothDataLocationsExist()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        Write(Source, "mods/shared.jar", "shared");
        Write(Path.Combine(Source, "versions", "1.21.1"), "mods/local.jar", "local");
        var selected = MinecraftInstanceMigration.Scan(Source).Instances.Single();
        Assert.IsTrue(selected.AmbiguousDataDirectory);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MinecraftInstanceMigration.MigrateAsync(selected, Destination, "ambiguous"));
        Assert.IsFalse(Directory.Exists(Path.Combine(Destination, "instances", "ambiguous")));
        var migrated = await MinecraftInstanceMigration.MigrateAsync(selected, Destination, "chosen", Source);
        Assert.IsTrue(File.Exists(Path.Combine(migrated.DirectoryPath, "mods", "shared.jar")));
    }

    [TestMethod]
    public void ReportsBrokenJsonMissingParentAndInheritanceCyclesWithoutHidingValidVersions()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        CreateVersion("broken", "not json");
        CreateVersion("missing", "{\"inheritsFrom\":\"absent\"}");
        CreateVersion("cycle-a", "{\"inheritsFrom\":\"cycle-b\"}");
        CreateVersion("cycle-b", "{\"inheritsFrom\":\"cycle-a\"}");
        CreateVersion("unsafe", "{\"inheritsFrom\":\"../../outside\"}");
        var scanned = MinecraftInstanceMigration.Scan(Source);
        Assert.AreEqual(1, scanned.Instances.Count);
        Assert.AreEqual(5, scanned.Diagnostics.Count);
        Assert.IsTrue(scanned.Diagnostics.Any(message => message.Contains("Cyclic")));
        Assert.IsTrue(scanned.Diagnostics.Any(message => message.Contains("unsafe path")));
    }

    [TestMethod]
    [DataRow("config")]
    [DataRow("libraries")]
    [DataRow("assets")]
    [DataRow("mods")]
    public async Task RejectsDestinationInsideCopiedSourceTreeBeforeCreatingDirectories(string folder)
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        var source = MinecraftInstanceMigration.Scan(Source).Instances.Single();
        var destination = Path.Combine(Source, folder);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MinecraftInstanceMigration.MigrateAsync(source, destination, "recursive"));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    public async Task FailedCopyLeavesNoPublishedInstanceAndDoesNotOverwriteExistingInstances()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        var source = MinecraftInstanceMigration.Scan(Source).Instances.Single();
        Write(Destination, "instances/existing/keep.txt", "keep");
        await Assert.ThrowsExactlyAsync<IOException>(() => MinecraftInstanceMigration.MigrateAsync(source, Destination, "existing"));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(Destination, "instances", "existing", "keep.txt")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => MinecraftInstanceMigration.MigrateAsync(source, Destination, "cancelled", cancellationToken: cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(Destination, "instances", "cancelled")));
        Assert.AreEqual(1, Directory.GetDirectories(Path.Combine(Destination, "instances")).Length);
    }

    [TestMethod]
    public async Task RejectsLinkedGameDataWithoutPublishingInstance()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Symbolic link fixture is for Linux."); return; }
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        Write(Source, "config/keep.txt", "keep");
        Write(_root, "outside/private.txt", "must not copy");
        Directory.CreateSymbolicLink(Path.Combine(Source, "config", "linked"), Path.Combine(_root, "outside"));
        var source = MinecraftInstanceMigration.Scan(Source).Instances.Single();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MinecraftInstanceMigration.MigrateAsync(source, Destination, "linked"));
        Assert.IsFalse(Directory.Exists(Path.Combine(Destination, "instances", "linked")));
    }

    [TestMethod]
    public async Task RenamedFlattenedPclProfileKeepsOfficialVersionAndLaunchesFromNewInstance()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Process fixture requires Linux."); return; }
        var json = JsonNode.Parse(Vanilla("PackName"))!.AsObject();
        json["clientVersion"] = "1.21.1";
        CreateVersion("中文整合包", json.ToJsonString(), jar: true, jsonName: "old-name.json");
        var source = MinecraftInstanceMigration.Scan(Source).Instances.Single();
        Assert.AreEqual("1.21.1", source.GameVersion);
        var instance = await MinecraftInstanceMigration.MigrateAsync(source, Destination, "renamed-pack");
        var java = Path.Combine(_root, "capture-java");
        await File.WriteAllTextAsync(java, "#!/bin/sh\nprintf '%s\\n' \"$PWD\" \"$@\" > capture.txt\n");
        File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var process = await new MinecraftInstaller().LaunchAsync(instance.LaunchVersion, new MinecraftLaunchOptions(instance.DirectoryPath, java));
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode);
        var captured = await File.ReadAllLinesAsync(Path.Combine(instance.DirectoryPath, "capture.txt"));
        Assert.AreEqual(instance.DirectoryPath, captured[0]);
        Assert.IsTrue(captured.Any(line => line.Contains(Path.Combine(instance.DirectoryPath, "versions", "中文整合包", "中文整合包.jar"))));
        Assert.IsFalse(captured.Any(line => line.Contains(Source)));
    }

    [TestMethod]
    public void ReadsGameVersionFromRenamedClientJar()
    {
        CreateVersion("custom-name", Vanilla("custom-name"));
        var jar = Path.Combine(Source, "versions", "custom-name", "custom-name.jar");
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("version.json").Open()))
            writer.Write("{\"id\":\"1.21.1\"}");
        Assert.AreEqual("1.21.1", MinecraftInstanceMigration.Scan(Source).Instances.Single().GameVersion);
    }

    [TestMethod]
    public void NewConfigFileOverridesLegacyIsolationKeysAndAcceptsNumericBoolean()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        var version = Path.Combine(Source, "versions", "1.21.1");
        Write(version, "PCL/Setup.ini", "VersionArgumentIndie:1\n");
        Write(version, "PCL/config.v1.json", "{\"VersionArgumentIndieV2\":0}");
        Assert.AreEqual(Source, MinecraftInstanceMigration.Scan(Source).Instances.Single().DataDirectory);
        Write(version, "PCL/config.v1.yml", "VersionArgumentIndieV2: 1\n");
        Assert.AreEqual(version, MinecraftInstanceMigration.Scan(Source).Instances.Single().DataDirectory);
    }

    [TestMethod]
    public async Task ImportedWindowsProfileDownloadsOnlyLinuxLibrariesAndExtractsNatives()
    {
        var nativeBytes = CreateNativeArchive();
        var hash = Convert.ToHexString(SHA1.HashData(nativeBytes));
        var metadata = JsonNode.Parse(Vanilla("1.21.1"))!.AsObject();
        metadata["libraries"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "example:native:1.0",
            ["natives"] = new JsonObject { ["linux"] = "natives-linux" },
            ["downloads"] = new JsonObject { ["classifiers"] = new JsonObject
            {
                ["natives-linux"] = new JsonObject { ["url"] = "https://example.invalid/linux.jar", ["path"] = "example/native/linux.jar", ["sha1"] = hash },
                ["natives-windows"] = new JsonObject { ["url"] = "https://example.invalid/windows.jar", ["path"] = "example/native/windows.jar", ["sha1"] = hash }
            } }
        });
        CreateVersion("1.21.1", metadata.ToJsonString(), jar: true);
        var instance = await MinecraftInstanceMigration.MigrateAsync(MinecraftInstanceMigration.Scan(Source).Instances.Single(), Destination, "linux-ready");
        var requests = new List<string>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(nativeBytes) };
        }));
        var installer = new MinecraftInstaller(http);
        await installer.PrepareForLaunchAsync(instance.LaunchVersion, instance.DirectoryPath);
        await installer.PrepareForLaunchAsync(instance.LaunchVersion, instance.DirectoryPath);
        CollectionAssert.AreEqual(new[] { "/linux.jar" }, requests);
        Assert.AreEqual("linux native", File.ReadAllText(Path.Combine(instance.DirectoryPath, "versions", "1.21.1", "natives", "libexample.so")));
    }

    [TestMethod]
    public async Task ReusesCopiedLegacyLibrariesWithoutRequiringNewDownloadMetadata()
    {
        var metadata = JsonNode.Parse(Vanilla("1.21.1"))!.AsObject();
        metadata["libraries"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "example:legacy:1.0", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject
            { ["url"] = "http://example.invalid/legacy.jar", ["path"] = "example/legacy.jar" } }
        });
        CreateVersion("1.21.1", metadata.ToJsonString(), jar: true);
        Write(Source, "libraries/example/legacy.jar", "existing library");
        using var http = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("A complete copied library must not be downloaded again.")));
        await new MinecraftInstaller(http).PrepareForLaunchAsync("1.21.1", Source);
        Assert.AreEqual("existing library", File.ReadAllText(Path.Combine(Source, "libraries", "example", "legacy.jar")));
    }

    [TestMethod]
    public void OlderManagedMetadataDefaultsToNotImported()
    {
        Write(Destination, "instances/previous/pcl-instance.json", "{\"Name\":\"previous\",\"GameVersion\":\"1.21.1\",\"Loader\":null,\"LoaderVersion\":null,\"LaunchVersion\":\"1.21.1\"}");
        Assert.IsFalse(ManagedInstanceService.Find(Destination, "previous")!.Imported);
    }

    [TestMethod]
    public async Task RejectsUnsafeImportedLibraryPathBeforeDownloading()
    {
        var metadata = JsonNode.Parse(Vanilla("1.21.1"))!.AsObject();
        metadata["libraries"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "example:unsafe:1.0", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject
            { ["url"] = "https://example.invalid/bad.jar", ["path"] = "../../outside.jar", ["sha1"] = new string('0', 40) } }
        });
        CreateVersion("1.21.1", metadata.ToJsonString(), jar: true);
        using var http = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("Must not access network for unsafe metadata.")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new MinecraftInstaller(http).PrepareForLaunchAsync("1.21.1", Source));
    }

    private static byte[] CreateNativeArchive()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("libexample.so").Open())) writer.Write("linux native");
        return buffer.ToArray();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    [TestMethod]
    public async Task ScanCommandCanInspectAndThenMigrateSelectedVersion()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        CreateVersion("1.20.1", Vanilla("1.20.1"), jar: true);
        Write(Source, "mods/shared.jar", "shared");
        var inspected = await PCL.Linux.Cli.InstanceCommands.ScanAsync([Source, "--game-dir", Destination]);
        Assert.AreEqual(0, inspected);
        Assert.IsFalse(Directory.Exists(Destination), "Inspecting must not create or modify the destination.");
        var migrated = await PCL.Linux.Cli.InstanceCommands.ScanAsync([Source, "--migrate", "--version", "1.21.1", "--instance", "chosen", "--game-dir", Destination]);
        Assert.AreEqual(0, migrated);
        Assert.AreEqual("1.21.1", ManagedInstanceService.List(Destination).Single().LaunchVersion);
        Assert.IsTrue(File.Exists(Path.Combine(Destination, "instances", "chosen", "mods", "shared.jar")));
    }

    [TestMethod]
    public async Task ScanCommandRejectsAmbiguousAndInvalidSelections()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        Assert.AreEqual(2, await PCL.Linux.Cli.InstanceCommands.ScanAsync([Source, "--migrate", "--version", "missing", "--instance", "none", "--game-dir", Destination]));
        Assert.AreEqual(2, await PCL.Linux.Cli.InstanceCommands.ScanAsync([Source, "--version", "1.21.1", "--game-dir", Destination]));
        Assert.AreEqual(2, await PCL.Linux.Cli.InstanceCommands.ScanAsync([Path.Combine(_root, "absent"), "--migrate", "--version", "missing", "--instance", "none", "--game-dir", Destination]));
        Assert.IsFalse(Directory.Exists(Destination));
    }

    [TestMethod]
    public void LegacyLaunchRecognizesRenamedChineseAndInheritedProfilesAsRequiringMigration()
    {
        CreateVersion("1.21.1", Vanilla("1.21.1"), jar: true);
        CreateVersion("中文整合包", Vanilla("1.21.1"), jar: true, jsonName: "original.json");
        CreateVersion("fabric-profile", "{\"id\":\"fabric-profile\",\"inheritsFrom\":\"1.21.1\"}");
        Assert.IsNull(ManagedInstanceService.Find(Source, "中文整合包"));
        Assert.IsTrue(PCL.Linux.Cli.InstanceCommands.RequiresLegacyMigration(Source, "中文整合包"));
        Assert.IsTrue(PCL.Linux.Cli.InstanceCommands.RequiresLegacyMigration(Source, "fabric-profile"));
        Assert.IsFalse(PCL.Linux.Cli.InstanceCommands.RequiresLegacyMigration(Source, "1.21.1"));
        Assert.IsFalse(PCL.Linux.Cli.InstanceCommands.RequiresLegacyMigration(Source, "not-installed"));
    }

    private void CreateVersion(string id, string json, bool jar = false, string? jsonName = null)
    {
        var directory = Path.Combine(Source, "versions", id);
        Write(directory, jsonName ?? id + ".json", json);
        if (jar) Write(directory, id + ".jar", "client jar");
    }

    private static string Vanilla(string version) => $$$"""
        {"id":"{{{version}}}","mainClass":"net.minecraft.client.main.Main","javaVersion":{"majorVersion":17},
         "libraries":[{"name":"example:base:1.0"}],"arguments":{"game":["--demo"],"jvm":["-cp","${classpath}"]}}
        """;

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
