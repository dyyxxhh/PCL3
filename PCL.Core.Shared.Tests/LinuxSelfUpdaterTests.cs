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

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task StreamingUpdateReportsProgressWithOrWithoutContentLength(bool knownLength)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "mcm-update-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mcm");
        await File.WriteAllTextAsync(target, "old executable");
        try
        {
            var archive = CreateArchive("mcm", TarEntryType.RegularFile, "new executable");
            using var client = new HttpClient(new ReleaseHandler(archive, Convert.ToHexString(SHA256.HashData(archive)), knownLength));
            using var output = new StringWriter();
            Assert.AreEqual(0, await LinuxSelfUpdater.UpdateTargetAsync(client, target, "0.1.0", new Version(0, 1, 0, 0), progressOutput: output));
            Assert.AreEqual("new executable", await File.ReadAllTextAsync(target));
            StringAssert.Contains(output.ToString(), "MiB/s");
            StringAssert.Contains(output.ToString(), knownLength ? "100%" : "total unknown");
            Assert.IsFalse(output.ToString().Contains('\u001b'));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(-1)]
    public async Task IncorrectContentLengthPreservesExecutableAndCleansStaging(int extraLength)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "mcm-update-truncated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mcm");
        await File.WriteAllTextAsync(target, "old executable");
        try
        {
            var archive = CreateArchive("mcm", TarEntryType.RegularFile, "new executable");
            using var client = new HttpClient(new ReleaseHandler(archive, Convert.ToHexString(SHA256.HashData(archive)), extraLength: extraLength));
            using var output = new StringWriter();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LinuxSelfUpdater.UpdateTargetAsync(client, target, "0.1.0", new Version(0, 1, 0, 0), progressOutput: output));
            Assert.AreEqual("old executable", await File.ReadAllTextAsync(target));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
            Assert.IsFalse(output.ToString().Contains("100%"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CancellationDuringStreamedBodyPreservesExecutableAndCleansStaging()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "mcm-update-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "mcm");
        await File.WriteAllTextAsync(target, "old executable");
        try
        {
            var archive = CreateArchive("mcm", TarEntryType.RegularFile, "new executable");
            using var cancel = new CancellationTokenSource();
            using var client = new HttpClient(new ReleaseHandler(archive, Convert.ToHexString(SHA256.HashData(archive)), bodyCancellation: cancel));
            using var output = new StringWriter();
            await Assert.ThrowsAsync<OperationCanceledException>(() => LinuxSelfUpdater.UpdateTargetAsync(client, target, "0.1.0", new Version(0, 1, 0, 0), cancel.Token, output));
            Assert.AreEqual("old executable", await File.ReadAllTextAsync(target));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
            Assert.IsFalse(output.ToString().Contains("100%"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Non-seekable input ensures that ResponseHeadersRead really streams the body. Buffering it would
    // make HttpContent attempt the unsupported synchronous Read, or lose the no-length test case.
    private sealed class StreamingBody(byte[] bytes, CancellationTokenSource? cancellation) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellation is not null)
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var count = Math.Min(Math.Min(buffer.Length, 31), bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Update body must be streamed asynchronously.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed class ReleaseHandler(byte[] archive, string checksum, bool knownLength = true, int extraLength = 0, CancellationTokenSource? bodyCancellation = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            const string assetUrl = "https://github.com/dyyxxhh/PCL3/releases/download/v0.2.0/MCM_Release_linux-x64.tar.gz";
            HttpContent content = url switch
            {
                "https://github.com/dyyxxhh/PCL3/releases/latest" => new StringContent("<a href=\"https://github.com/dyyxxhh/PCL3/releases/tag/v0.2.0\">release</a>"),
                assetUrl + ".sha256" => new StringContent(checksum),
                assetUrl => ArchiveContent(),
                _ => throw new AssertFailedException("Unexpected update URL: " + url)
            };
            HttpContent ArchiveContent()
            {
                var body = new StreamContent(new StreamingBody(archive, bodyCancellation));
                if (knownLength) body.Headers.ContentLength = archive.Length + extraLength;
                return body;
            }
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
