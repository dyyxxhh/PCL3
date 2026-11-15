using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PCL.Linux.Cli;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class LinuxSelfUpdaterTests
{
    [TestMethod]
    public void ParsesReleaseTagAndSha256Manifest()
    {
        var html = "<link rel=\"canonical\" href=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.1.8\">";
        Assert.AreEqual("v0.1.8", LinuxSelfUpdater.ParseReleaseTag(html));
        var checksum = new string('a', 64);
        Assert.AreEqual(checksum, LinuxSelfUpdater.ParseSha256(checksum + "  MCM_Release_linux-x64.tar.gz\n"));
    }

    [TestMethod]
    public void ReleaseVersionsAreComparedNumerically()
    {
        Assert.IsTrue(LinuxSelfUpdater.TryParseReleaseVersion("v0.1.10", out var newer));
        Assert.IsTrue(LinuxSelfUpdater.TryParseReleaseVersion("0.1.9", out var older));
        Assert.IsTrue(newer > older);
        Assert.IsFalse(LinuxSelfUpdater.TryParseReleaseVersion("v0.1.10-beta", out _));
        Assert.IsFalse(LinuxSelfUpdater.TryParseReleaseVersion(null, out _));
    }

    [TestMethod]
    public void Version030IsNewerThanPreviousReleaseAndAcceptsReleaseTag()
    {
        Assert.IsTrue(LinuxSelfUpdater.TryParseReleaseVersion("v0.3.0", out var released));
        Assert.IsTrue(LinuxSelfUpdater.TryParseReleaseVersion("0.2.3", out var previous));
        Assert.IsTrue(released > previous);
        Assert.AreEqual(new Version(0, 3, 0, 0), released);
        Assert.AreEqual("v0.3.0", LinuxSelfUpdater.ParseReleaseTag(
            "<link rel=\"canonical\" href=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.3.0\">"));
    }

    [TestMethod]
    [DataRow("mcm")]
    [DataRow("./mcm")]
    public async Task UpdateReplacesExecutableOnlyAfterVerification(string executableEntry)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "mcm-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mcm");
        await File.WriteAllTextAsync(target, "old executable");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var archive = CreateArchive(executableEntry, TarEntryType.RegularFile, "new executable");
            using var badChecksum = CreateClient(archive, new string('0', 64));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxSelfUpdater.UpdateTargetAsync(badChecksum, target, "0.1.0", new Version(0, 1, 0, 0)));
            Assert.AreEqual("old executable", await File.ReadAllTextAsync(target));

            var oldNameArchive = CreateArchive("pcl", TarEntryType.RegularFile, "wrong executable name");
            using var oldNameClient = CreateClient(oldNameArchive, Convert.ToHexString(SHA256.HashData(oldNameArchive)));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxSelfUpdater.UpdateTargetAsync(oldNameClient, target, "0.1.0", new Version(0, 1, 0, 0)));
            Assert.AreEqual("old executable", await File.ReadAllTextAsync(target));

            var malicious = CreateArchive("mcm", TarEntryType.SymbolicLink, "outside");
            using var unsafeClient = CreateClient(malicious, Convert.ToHexString(SHA256.HashData(malicious)));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxSelfUpdater.UpdateTargetAsync(unsafeClient, target, "0.1.0", new Version(0, 1, 0, 0)));
            Assert.AreEqual("old executable", await File.ReadAllTextAsync(target));

            using var valid = CreateClient(archive, Convert.ToHexString(SHA256.HashData(archive)));
            Assert.AreEqual(0, await LinuxSelfUpdater.UpdateTargetAsync(valid, target, "0.1.0", new Version(0, 1, 0, 0)));
            Assert.AreEqual("new executable", await File.ReadAllTextAsync(target));
            Assert.IsTrue(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateArchive(string name, TarEntryType type, string content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            else entry.LinkName = content;
            writer.WriteEntry(entry);
        }
        return buffer.ToArray();
    }

    private static HttpClient CreateClient(byte[] archive, string checksum)
        => new(new ReleaseHandler(archive, checksum));

    private sealed class ReleaseHandler(byte[] archive, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            const string assetUrl = "https://github.com/dyyxxhh/PCL3/releases/download/v0.2.0/MCM_Release_linux-x64.tar.gz";
            HttpContent content = url switch
            {
                "https://github.com/dyyxxhh/PCL3/releases/latest" => new StringContent("<a href=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.2.0\">release</a>"),
                assetUrl + ".sha256" => new StringContent(checksum),
                assetUrl => new ByteArrayContent(archive),
                _ => throw new AssertFailedException("Unexpected update URL: " + url)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [TestMethod]
    public void RejectsInvalidReleaseMetadata()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => LinuxSelfUpdater.ParseReleaseTag("<html></html>"));
        Assert.ThrowsExactly<InvalidDataException>(() => LinuxSelfUpdater.ParseSha256("not-a-checksum"));
    }
}
