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
        Assert.AreEqual(checksum, LinuxSelfUpdater.ParseSha256(checksum + "  PCL3_Release_linux-x64.tar.gz\n"));
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
    public void Version022IsNewerThanPreviousReleaseAndAcceptsReleaseTag()
    {
        Assert.IsTrue(LinuxSelfUpdater.TryParseReleaseVersion("v0.2.2", out var released));
        Assert.IsTrue(LinuxSelfUpdater.TryParseReleaseVersion("0.2.1", out var previous));
        Assert.IsTrue(released > previous);
        Assert.AreEqual(new Version(0, 2, 2, 0), released);
        Assert.AreEqual("v0.2.2", LinuxSelfUpdater.ParseReleaseTag(
            "<link rel=\"canonical\" href=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.2.2\">"));
    }

    [TestMethod]
    public async Task UpdateReplacesExecutableOnlyAfterVerification()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pcl-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "pcl");
        await File.WriteAllTextAsync(target, "old executable");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var archive = CreateArchive("pcl", TarEntryType.RegularFile, "new executable");
            using var badChecksum = CreateClient(archive, new string('0', 64));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxSelfUpdater.UpdateTargetAsync(badChecksum, target, "0.1.0", new Version(0, 1, 0, 0)));
            Assert.AreEqual("old executable", await File.ReadAllTextAsync(target));

            var malicious = CreateArchive("pcl", TarEntryType.SymbolicLink, "outside");
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
            HttpContent content = url.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new StringContent("<a href=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.2.0\">release</a>")
                : url.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent(checksum)
                    : new ByteArrayContent(archive);
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
