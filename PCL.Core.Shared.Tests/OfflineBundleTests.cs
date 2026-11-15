using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class OfflineBundleTests
{
    [TestMethod]
    public async Task RoundTripIncludesFullJavaAndLauncherAndPreservesLoaderProfile()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.JavaHome, "lib"));
        await File.WriteAllTextAsync(Path.Combine(fixture.JavaHome, "lib", "libjava.so"), "runtime library");
        File.CreateSymbolicLink(Path.Combine(fixture.JavaHome, "lib", "alias.so"), "libjava.so");
        var original = await fixture.ExportAsync();
        Assert.AreEqual("linux-x64", original.TargetRuntime);
        Assert.AreEqual(21, original.JavaMajorVersion);
        Assert.IsTrue(original.Files.Any(file => file.Path == OfflineBundleService.LauncherPath));
        Assert.IsTrue(original.Files.Any(file => file.Path == "instance/runtime/offline-java/lib/libjava.so"));
        Assert.IsFalse(original.Files.Any(file => file.Path.Contains("selected-profile", StringComparison.Ordinal) || file.Path.Contains("saves/", StringComparison.Ordinal) || file.Path.Contains("logs/", StringComparison.Ordinal)));
        var verified = await OfflineBundleService.VerifyAsync(fixture.Bundle);
        Assert.AreEqual(original.Files.Count, verified.Files.Count);
        var imported = await OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target, "Copied");
        Assert.AreEqual("Copied", imported.Name);
        Assert.AreEqual("fabric", imported.Loader);
        Assert.AreEqual("fabric-profile", imported.LaunchVersion);
        Assert.IsFalse(imported.Imported); // no online legacy-repair path on normal launch
        Assert.AreEqual("Copied", ManagedInstanceService.Find(fixture.Target, "Copied")?.Name);
        Assert.AreEqual("mod bytes", await File.ReadAllTextAsync(Path.Combine(imported.DirectoryPath, "mods", "example.jar")));
        Assert.AreEqual("config", await File.ReadAllTextAsync(Path.Combine(imported.DirectoryPath, "config", "example.json")));
        Assert.IsNull(new FileInfo(Path.Combine(imported.DirectoryPath, "runtime", "offline-java", "lib", "alias.so")).LinkTarget);
        Assert.AreEqual("runtime library", await File.ReadAllTextAsync(Path.Combine(imported.DirectoryPath, "runtime", "offline-java", "lib", "alias.so")));
        var java = Path.Combine(imported.DirectoryPath, "runtime", "offline-java", "bin", "java");
        if (OperatingSystem.IsLinux())
        {
            Assert.IsTrue((File.GetUnixFileMode(java) & UnixFileMode.UserExecute) != 0);
            Assert.AreEqual((UnixFileMode)0, File.GetUnixFileMode(java) & (UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.OtherWrite));
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(imported.DirectoryPath, "bootstrap")));
        AssertNoStaging(fixture.Target);
        var launcher = Path.Combine(fixture.Root, "extracted-mcm");
        await OfflineBundleService.ExtractLauncherAsync(fixture.Bundle, launcher);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(fixture.Launcher), await File.ReadAllBytesAsync(launcher));
        await Assert.ThrowsExactlyAsync<IOException>(() => OfflineBundleService.ExtractLauncherAsync(fixture.Bundle, launcher));
        Assert.IsTrue(File.Exists(launcher));
    }

    [TestMethod]
    public async Task ValidationNeverUsesHttpEvenWhenAllUrlsAreUnreachable()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var handler = new RejectNetworkHandler();
        using var client = new HttpClient(handler);
        await new MinecraftInstaller(client).ValidateOfflineInstallationAsync(fixture.Instance.LaunchVersion, fixture.Instance.DirectoryPath);
        Assert.AreEqual(0, handler.Requests);
    }

    [TestMethod]
    public async Task ExistingInstanceAndExistingOutputAreNotOverwritten()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        var destination = ManagedInstanceService.GetInstanceDirectory(fixture.Target, fixture.Instance.Name);
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(marker, "original");
        await Assert.ThrowsExactlyAsync<IOException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.AreEqual("original", await File.ReadAllTextAsync(marker));
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(fixture.Bundle));
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.ExportAsync());
        CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.Bundle)));
        AssertNoStaging(fixture.Target);
    }

    [TestMethod]
    public async Task CorruptPayloadRollsBackAndInspectAlsoRejectsIt()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        OfflineBundleFixture.ReplaceEntry(fixture.Bundle, "instance/mods/example.jar", Encoding.UTF8.GetBytes("bad bytes"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.VerifyAsync(fixture.Bundle));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.IsNull(ManagedInstanceService.Find(fixture.Target, fixture.Instance.Name));
        AssertNoStaging(fixture.Target);
    }

    [TestMethod]
    [DataRow("instance/mods/../../outside.txt")]
    [DataRow("/outside.txt")]
    [DataRow("instance\\mods\\outside.jar")]
    [DataRow("instance/mods/NUL.jar")]
    [DataRow("instance/mods/trailing.")]
    [DataRow("instance/mods/empty//file")]
    [DataRow("instance/config/selected-profile.json")]
    [DataRow("instance/mcm-instance.json")]
    [DataRow("instance/servers.dat")]
    [DataRow("instance/saves/world.dat")]
    public async Task RejectsUnsafeAndPrivatePayloadPaths(string path)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        OfflineBundleFixture.RenameEntry(fixture.Bundle, "instance/mods/example.jar", path);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.IsFalse(Directory.Exists(fixture.Target));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [TestMethod]
    [DataRow(0xA000)]
    [DataRow(0x2000)]
    [DataRow(0x4000)]
    public async Task RejectsArchiveLinksAndSpecialFiles(int type)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        using (var zip = ZipFile.Open(fixture.Bundle, ZipArchiveMode.Update))
            zip.GetEntry("instance/mods/example.jar")!.ExternalAttributes = unchecked(type << 16);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.IsFalse(Directory.Exists(fixture.Target));
    }

    [TestMethod]
    [DataRow("instance/mods/example.jar")]
    [DataRow("instance/MODS/EXAMPLE.jar")]
    [DataRow("undeclared.txt")]
    public async Task RejectsDuplicateCaseCollidingAndUndeclaredZipEntries(string path)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        using (var zip = ZipFile.Open(fixture.Bundle, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry(path).Open())) writer.Write("unexpected");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.VerifyAsync(fixture.Bundle));
    }

    [TestMethod]
    [DataRow("missing-java")]
    [DataRow("missing-launcher")]
    [DataRow("oversize")]
    [DataRow("bad-format")]
    [DataRow("bad-hash")]
    [DataRow("wrong-java-mode")]
    [DataRow("case-java")]
    [DataRow("path-collision")]
    public async Task RejectsIncompleteOrInvalidManifests(string mutation)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        if (mutation == "case-java")
            OfflineBundleFixture.RenameEntry(fixture.Bundle, "instance/" + OfflineBundleService.JavaExecutable, "instance/runtime/offline-java/bin/Java");
        else if (mutation == "path-collision")
        {
            OfflineBundleFixture.AddTrackedFile(fixture.Bundle, "instance/mods/example.jar/child", "child");
        }
        else
        {
            OfflineBundleFixture.RewriteManifest(fixture.Bundle, manifest =>
            {
                var files = manifest["files"]!.AsArray();
                var java = files.OfType<JsonObject>().Single(file => file["path"]!.GetValue<string>() == "instance/" + OfflineBundleService.JavaExecutable);
                switch (mutation)
                {
                    case "missing-java": files.Remove(java); break;
                    case "missing-launcher": files.Remove(files.Single(file => file!["path"]!.GetValue<string>() == OfflineBundleService.LauncherPath)); break;
                    case "oversize": java["size"] = OfflineBundleService.MaxFileSize + 1; break;
                    case "bad-format": manifest["formatVersion"] = 99; break;
                    case "bad-hash": java["sha256"] = "not a hash"; break;
                    case "wrong-java-mode": java["executable"] = false; break;
                }
            });
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.IsFalse(Directory.Exists(fixture.Target));
    }

    [TestMethod]
    public async Task RejectsMismatchedTargetWithoutExtracting()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        OfflineBundleFixture.RewriteManifest(fixture.Bundle, manifest => manifest["targetRuntime"] = "win-x64");
        await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.IsFalse(Directory.Exists(fixture.Target));
    }

    [TestMethod]
    [DataRow("library")]
    [DataRow("asset")]
    [DataRow("client")]
    public async Task IncompleteSourceFailsBeforeCreatingArchive(string file)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        File.Delete(file switch { "library" => fixture.Library, "asset" => fixture.Asset, _ => fixture.Client });
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fixture.ExportAsync());
        Assert.IsFalse(File.Exists(fixture.Bundle));
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Root, "*.tmp-*").Any());
    }

    [TestMethod]
    public async Task RehashedBundleStillChecksOfficialGameHashes()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        var bytes = Encoding.UTF8.GetBytes("corrupt game library");
        OfflineBundleFixture.ReplaceEntry(fixture.Bundle, "instance/libraries/org/example/sample.jar", bytes);
        OfflineBundleFixture.RewriteManifest(fixture.Bundle, manifest =>
        {
            var file = manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == "instance/libraries/org/example/sample.jar")!;
            file["size"] = bytes.Length;
            file["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        });
        await OfflineBundleService.VerifyAsync(fixture.Bundle); // transport integrity, not authenticity
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.IsNull(ManagedInstanceService.Find(fixture.Target, fixture.Instance.Name));
        AssertNoStaging(fixture.Target);
    }

    [TestMethod]
    [DataRow("game-link")]
    [DataRow("external-java-link")]
    [DataRow("java-directory-link")]
    public async Task RejectsSourceLinksThatCouldLeakOrEscape(string kind)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var secret = Path.Combine(fixture.Root, "private.txt");
        await File.WriteAllTextAsync(secret, "do not package");
        if (kind == "game-link") File.CreateSymbolicLink(Path.Combine(fixture.Instance.DirectoryPath, "mods", "leak.jar"), secret);
        if (kind == "external-java-link") File.CreateSymbolicLink(Path.Combine(fixture.JavaHome, "leak"), secret);
        if (kind == "java-directory-link") Directory.CreateSymbolicLink(Path.Combine(fixture.JavaHome, "leak"), fixture.Root);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.ExportAsync());
        Assert.IsFalse(File.Exists(fixture.Bundle));
    }

    [TestMethod]
    public async Task ExportAcceptsJavaHomeAliasAndFlattensRuntimeConfigurationLinks()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var settings = Path.Combine(fixture.JavaHome, "settings");
        Directory.CreateDirectory(settings);
        await File.WriteAllTextAsync(Path.Combine(settings, "java.security"), "security fixture");
        await File.WriteAllTextAsync(Path.Combine(settings, "jmxremote.password"), "private fixture");
        Directory.CreateSymbolicLink(Path.Combine(fixture.JavaHome, "conf"), "settings");
        var alias = Path.Combine(fixture.Root, "default-runtime");
        Directory.CreateSymbolicLink(alias, fixture.JavaHome);
        var manifest = await OfflineBundleService.ExportAsync(fixture.Instance, alias, fixture.Launcher, fixture.Bundle);
        Assert.IsTrue(manifest.Files.Any(file => file.Path == "instance/runtime/offline-java/conf/java.security"));
        Assert.IsFalse(manifest.Files.Any(file => file.Path.EndsWith("jmxremote.password", StringComparison.Ordinal)));
        var imported = await OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target);
        var config = Path.Combine(imported.DirectoryPath, "runtime", "offline-java", "conf", "java.security");
        Assert.AreEqual("security fixture", await File.ReadAllTextAsync(config));
        Assert.IsNull(new DirectoryInfo(Path.GetDirectoryName(config)!).LinkTarget);
    }

    [TestMethod]
    [DataRow("conf")]
    [DataRow("legal")]
    public async Task RejectsArbitraryExternalConfigurationAndLicenseDirectories(string name)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var outside = Path.Combine(fixture.Root, "private");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret"), "do not copy");
        Directory.CreateSymbolicLink(Path.Combine(fixture.JavaHome, name), outside);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.ExportAsync());
        Assert.IsFalse(File.Exists(fixture.Bundle));
    }

    [TestMethod]
    public async Task RejectsJavaConfigurationDirectoryCycle()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        Directory.CreateSymbolicLink(Path.Combine(fixture.JavaHome, "conf"), fixture.JavaHome);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.ExportAsync());
        Assert.IsFalse(File.Exists(fixture.Bundle));
    }

    [TestMethod]
    public async Task RejectsSymlinkDestinationWithoutWritingThroughIt()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(fixture.Target);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Target, "instances"), outside);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target));
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(outside).Count());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationLeavesNeitherPartialInstanceNorPartialArchive(bool exporting)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        if (!exporting) await fixture.ExportAsync();
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.Stage.StartsWith(exporting ? "Packing" : "Verifying", StringComparison.Ordinal) && value.Completed >= 1) cancel.Cancel();
        });
        try
        {
            if (exporting) await OfflineBundleService.ExportAsync(fixture.Instance, fixture.JavaHome, fixture.Launcher, fixture.Bundle, progress, cancel.Token);
            else await OfflineBundleService.ImportAsync(fixture.Bundle, fixture.Target, progress: progress, cancellationToken: cancel.Token);
            Assert.Fail("Cancellation must be observed.");
        }
        catch (OperationCanceledException) { }
        if (exporting) Assert.IsFalse(File.Exists(fixture.Bundle));
        else Assert.IsNull(ManagedInstanceService.Find(fixture.Target, fixture.Instance.Name));
        AssertNoStaging(fixture.Target);
        Assert.IsFalse(Directory.EnumerateFiles(fixture.Root, "*.tmp-*").Any());
    }

    [TestMethod]
    [DataRow("virtual")]
    [DataRow("map_to_resources")]
    public async Task MissingLegacyMappedAssetsAreNotConsideredOfflineReady(string flag)
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var indexPath = Path.Combine(fixture.Instance.DirectoryPath, "assets", "indexes", "test.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!;
        index[flag] = true;
        var bytes = Encoding.UTF8.GetBytes(index.ToJsonString());
        await File.WriteAllBytesAsync(indexPath, bytes);
        var metadataPath = Path.Combine(fixture.Instance.DirectoryPath, "versions", fixture.Instance.LaunchVersion, fixture.Instance.LaunchVersion + ".json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;
        metadata["assetIndex"]!["sha1"] = Convert.ToHexString(SHA1.HashData(bytes));
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fixture.ExportAsync());
        var mapped = flag == "virtual" ? Path.Combine(fixture.Instance.DirectoryPath, "assets", "virtual", "test", "test")
            : Path.Combine(fixture.Instance.DirectoryPath, "resources", "test");
        Directory.CreateDirectory(Path.GetDirectoryName(mapped)!);
        File.Copy(fixture.Asset, mapped);
        await fixture.ExportAsync();
    }

    [TestMethod]
    public async Task MissingExtractedNativeIsNotSilentlyDownloaded()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var native = Path.Combine(fixture.Instance.DirectoryPath, "libraries", "natives.jar");
        using (var zip = ZipFile.Open(native, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("libexample.so").Open())) writer.Write("native");
        var metadataPath = Path.Combine(fixture.Instance.DirectoryPath, "versions", fixture.Instance.LaunchVersion, fixture.Instance.LaunchVersion + ".json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!;
        metadata["libraries"]!.AsArray().Add(JsonNode.Parse("""{"name":"test:native:1","natives":{"linux":"natives-linux"},"downloads":{"classifiers":{"natives-linux":{"path":"natives.jar","url":"https://unreachable.invalid/native.jar"}}}}"""));
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => fixture.ExportAsync());
        Assert.IsFalse(File.Exists(fixture.Bundle));
    }

    private static void AssertNoStaging(string root)
    {
        var parent = Path.Combine(root, "instances");
        Assert.IsFalse(Directory.Exists(parent) && Directory.EnumerateDirectories(parent, ".migration-*").Any());
    }
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress> { public void Report(DownloadProgress value) => report(value); }
    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new HttpRequestException("No network is allowed in this test.");
        }
    }
}

internal sealed class OfflineBundleFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcm-offline-tests-" + Guid.NewGuid().ToString("N"));
    internal string JavaHome => Path.Combine(Root, "portable java");
    internal string Launcher => Path.Combine(Root, "mcm");
    internal string Bundle => Path.Combine(Root, "bundle.zip");
    internal string Target => Path.Combine(Root, "target with spaces");
    internal string Library => Path.Combine(Instance.DirectoryPath, "libraries", "org", "example", "sample.jar");
    internal string Client => Path.Combine(Instance.DirectoryPath, "versions", "1.21.1", "1.21.1.jar");
    internal string Asset { get; private set; } = "";
    internal ManagedMinecraftInstance Instance { get; }

    private OfflineBundleFixture()
    {
        Instance = new ManagedMinecraftInstance("Prepared", "1.21.1", "fabric", "0.16.0",
            ManagedInstanceService.GetInstanceDirectory(Path.Combine(Root, "source"), "Prepared"), "fabric-profile");
    }

    internal static async Task<OfflineBundleFixture> CreateAsync()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Linux runtime fixture."); return null!; }
        var fixture = new OfflineBundleFixture();
        try
        {
            var root = fixture.Instance.DirectoryPath;
            foreach (var directory in new[] { "versions/fabric-profile", "versions/1.21.1", "libraries/org/example", "assets/indexes", "mods", "config", "saves/world", "logs" })
                Directory.CreateDirectory(Path.Combine(root, directory));
            await File.WriteAllTextAsync(fixture.Library, "library");
            await File.WriteAllTextAsync(fixture.Client, "client");
            var assetBytes = Encoding.UTF8.GetBytes("asset");
            var assetHash = Convert.ToHexString(SHA1.HashData(assetBytes)).ToLowerInvariant();
            fixture.Asset = Path.Combine(root, "assets", "objects", assetHash[..2], assetHash);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Asset)!);
            await File.WriteAllBytesAsync(fixture.Asset, assetBytes);
            var index = new JsonObject { ["objects"] = new JsonObject { ["test"] = new JsonObject { ["hash"] = assetHash, ["size"] = assetBytes.Length } } };
            var indexBytes = Encoding.UTF8.GetBytes(index.ToJsonString());
            await File.WriteAllBytesAsync(Path.Combine(root, "assets", "indexes", "test.json"), indexBytes);
            var metadata = new JsonObject
            {
                ["id"] = "fabric-profile", ["jar"] = "1.21.1", ["mainClass"] = "test.Main",
                ["javaVersion"] = new JsonObject { ["majorVersion"] = 21 },
                ["assetIndex"] = new JsonObject { ["id"] = "test", ["sha1"] = Convert.ToHexString(SHA1.HashData(indexBytes)), ["url"] = "https://unreachable.invalid/assets" },
                ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["sha1"] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("client"))) } },
                ["libraries"] = new JsonArray(new JsonObject { ["name"] = "org.example:sample:1", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject
                { ["path"] = "org/example/sample.jar", ["sha1"] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("library"))), ["url"] = "https://unreachable.invalid/library" } } })
            };
            await File.WriteAllTextAsync(Path.Combine(root, "versions", "fabric-profile", "fabric-profile.json"), metadata.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(root, "mods", "example.jar"), "mod bytes");
            await File.WriteAllTextAsync(Path.Combine(root, "config", "example.json"), "config");
            foreach (var file in new[] { "accounts.json", "config/selected-profile.json", "saves/world/private.txt", "logs/private.log" })
                await File.WriteAllTextAsync(Path.Combine(root, file), "private fixture data");
            Directory.CreateDirectory(Path.Combine(fixture.JavaHome, "bin"));
            var java = Path.Combine(fixture.JavaHome, "bin", "java");
            await File.WriteAllTextAsync(java, "#!/bin/sh\nprintf 'openjdk version \"21.0.5\"\\n' >&2\n");
            File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.WriteAllTextAsync(Path.Combine(fixture.JavaHome, "LICENSE"), "Java fixture license");
            await File.WriteAllTextAsync(fixture.Launcher, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(fixture.Launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await ManagedInstanceService.SaveAsync(fixture.Instance);
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    internal Task<OfflineBundleManifest> ExportAsync() => OfflineBundleService.ExportAsync(Instance, JavaHome, Launcher, Bundle);

    internal static void ReplaceEntry(string bundle, string path, byte[] bytes)
    {
        using var zip = ZipFile.Open(bundle, ZipArchiveMode.Update);
        zip.GetEntry(path)?.Delete();
        using var output = zip.CreateEntry(path).Open();
        output.Write(bytes);
    }

    internal static void RewriteManifest(string bundle, Action<JsonObject> change)
    {
        using var zip = ZipFile.Open(bundle, ZipArchiveMode.Update);
        var entry = zip.GetEntry(OfflineBundleService.ManifestPath)!;
        JsonObject manifest;
        using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        change(manifest);
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(OfflineBundleService.ManifestPath).Open());
        writer.Write(manifest.ToJsonString());
    }

    internal static void RenameEntry(string bundle, string oldPath, string newPath)
    {
        byte[] bytes;
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry(oldPath)!;
            using (var input = entry.Open())
            using (var output = new MemoryStream()) { input.CopyTo(output); bytes = output.ToArray(); }
            entry.Delete();
            using var target = zip.CreateEntry(newPath).Open();
            target.Write(bytes);
        }
        RewriteManifest(bundle, manifest => manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == oldPath)!["path"] = newPath);
    }

    internal static void AddTrackedFile(string bundle, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        ReplaceEntry(bundle, path, bytes);
        RewriteManifest(bundle, manifest => manifest["files"]!.AsArray().Add(new JsonObject
            { ["path"] = path, ["size"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)), ["executable"] = false }));
    }

    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
