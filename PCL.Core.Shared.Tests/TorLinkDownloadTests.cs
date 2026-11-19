using System.Net;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Network;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class TorLinkDownloadTests
{
    private static readonly Uri Source = new("https://example.test/download");
    private static readonly byte[] Payload = [2, 4, 8, 16];
    private string _folder = null!;
    private string Target => Path.Combine(_folder, "download.bin");

    [TestInitialize]
    public void CreateDirectory() => _folder = Directory.CreateTempSubdirectory().FullName;

    [TestCleanup]
    public void RemoveDirectory() => Directory.Delete(_folder, true);

    [TestMethod]
    public async Task FollowsRedirectUsingSameClientAndValidatesFile()
    {
        var requests = new List<Uri>();
        using var client = new HttpClient(new FakeHandler(uri =>
        {
            requests.Add(uri);
            return uri == Source ? Redirect("https://files.example.test/payload") : Content();
        }));
        await SaveAsync(client, Convert.ToHexString(SHA256.HashData(Payload)));
        CollectionAssert.AreEqual(new[] { Source, new Uri("https://files.example.test/payload") }, requests);
        CollectionAssert.AreEqual(Payload, await File.ReadAllBytesAsync(Target));
        CollectionAssert.AreEqual(new[] { Target }, Directory.GetFiles(_folder));
    }

    [TestMethod]
    [DataRow("file:///tmp/file")]
    [DataRow("http://example.test/cleartext")]
    [DataRow("https://mock-user@example.test/file")]
    public async Task RejectsUnsafeRedirect(string destination)
    {
        var count = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        {
            count++;
            return Redirect(destination);
        }));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => SaveAsync(client));
        Assert.AreEqual(1, count);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_folder));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemovesPartialFileAfterVerificationFailure(bool wrongLength)
    {
        using var client = new HttpClient(new FakeHandler(_ => Content(wrongLength ? Payload.Length + 1 : null)));
        await Assert.ThrowsExactlyAsync<IOException>(() => SaveAsync(client, wrongLength ? null : new string('0', 64)));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_folder));
    }

    [TestMethod]
    public async Task CancelsDownloadAndRemovesPartialFile()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new FakeHandler(_ => Content()));
        var progress = new InlineProgress(() => cancellation.Cancel());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            TorLinkDownload.SaveAsync(client, Source, Target, null, progress, cancellation.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_folder));
    }

    [TestMethod]
    public async Task PreservesExistingFile()
    {
        await File.WriteAllTextAsync(Target, "existing");
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("Should not request the URL")));
        await Assert.ThrowsExactlyAsync<IOException>(() => SaveAsync(client));
        Assert.AreEqual("existing", await File.ReadAllTextAsync(Target));
    }

    [TestMethod]
    public async Task RejectsLinkedDestinationParent()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("This test creates a Linux symbolic link.");
        var real = Directory.CreateDirectory(Path.Combine(_folder, "real"));
        var link = Path.Combine(_folder, "link");
        Directory.CreateSymbolicLink(link, real.FullName);
        using var client = new HttpClient(new FakeHandler(_ => throw new AssertFailedException("Should not request the URL")));
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            TorLinkDownload.SaveAsync(client, Source, Path.Combine(link, "download.bin"), null, null, default));
        Assert.IsEmpty(Directory.GetFileSystemEntries(real.FullName));
    }

    private Task SaveAsync(HttpClient client, string? hash = null) =>
        TorLinkDownload.SaveAsync(client, Source, Target, hash, null, default);

    private static HttpResponseMessage Redirect(string destination) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(destination) }
    };

    private static HttpResponseMessage Content(long? length = null)
    {
        var content = new ByteArrayContent(Payload);
        if (length is not null) content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private sealed class InlineProgress(Action report) : IProgress<TorLinkDownload.Progress>
    {
        public void Report(TorLinkDownload.Progress value) => report();
    }
}
