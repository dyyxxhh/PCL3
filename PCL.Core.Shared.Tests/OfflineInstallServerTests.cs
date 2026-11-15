using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PCL.Linux.Cli;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class OfflineInstallServerTests
{
    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("192.168.2.3", true)]
    [DataRow("10.0.0.2", true)]
    [DataRow("172.16.0.1", true)]
    [DataRow("172.31.255.255", true)]
    [DataRow("169.254.1.2", true)]
    [DataRow("172.32.0.1", false)]
    [DataRow("8.8.8.8", false)]
    [DataRow("::1", true)]
    [DataRow("::ffff:192.168.1.2", true)]
    [DataRow("fd00::1", true)]
    [DataRow("2001:4860:4860::8888", false)]
    public void OnlyAcceptsLanAndLoopbackPeers(string address, bool allowed)
        => Assert.AreEqual(allowed, OfflineInstallServer.IsLanAddress(IPAddress.Parse(address)));

    [TestMethod]
    public async Task ServesOnlyVerifiedAllowlistedFilesAndIgnoresHostHeader()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        var port = UnusedPort();
        await using var server = await OfflineInstallServer.StartAsync(fixture.Bundle, IPAddress.Any, port);
        using var cancel = new CancellationTokenSource();
        var running = server.RunAsync(cancel.Token);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Host = "untrusted.example";
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var script = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(script, $"http://127.0.0.1:{port}");
            Assert.IsFalse(script.Contains("untrusted.example", StringComparison.Ordinal));
            StringAssert.Contains(script, "sha256sum --check --status");
            StringAssert.Contains(script, "--noproxy '*'");
            StringAssert.Contains(script, "--offline");
            StringAssert.Contains(script, "MCM_DOWNLOAD_CONNECTIONS:-4");
            StringAssert.Contains(script, "--range");
            StringAssert.Contains(script, "KiB/s | ETA");
            Assert.IsFalse(script.Contains("https://", StringComparison.Ordinal));
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(fixture.Launcher), await client.GetByteArrayAsync("/mcm"));
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(fixture.Bundle), await client.GetByteArrayAsync("/bundle.zip"));
            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/bundle.zip"));
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual(new FileInfo(fixture.Bundle).Length, head.Content.Headers.ContentLength);
            Assert.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length);
            foreach (var path in new[] { "/etc/passwd", "/mcm-offline.json", "/bundle.zip?path=private", "/bootstrap/mcm", "/selected-profile.json" })
            {
                using var missing = await client.GetAsync(path);
                Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode, path);
            }
            using var post = await client.PostAsync("/", new StringContent("no uploads"));
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            var requests = Enumerable.Range(0, 16).Select(_ => client.GetStringAsync("/install.sh"));
            Assert.AreEqual(16, (await Task.WhenAll(requests)).Length);
        }
        finally
        {
            cancel.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow("bytes=0-9", true, 0L, 9L)]
    [DataRow("bytes=90-", true, 90L, 99L)]
    [DataRow("bytes=-10", true, 90L, 99L)]
    [DataRow("bytes=-200", true, 0L, 99L)]
    [DataRow("bytes=90-200", true, 90L, 99L)]
    [DataRow("bytes=100-", false, 0L, 0L)]
    [DataRow("bytes=9-0", false, 0L, 0L)]
    [DataRow("bytes=-0", false, 0L, 0L)]
    [DataRow("bytes=0-1,5-6", false, 0L, 0L)]
    [DataRow("bytes=0-999999999999999999999", false, 0L, 0L)]
    [DataRow("bytes=+1-2", false, 0L, 0L)]
    [DataRow("bits=0-1", false, 0L, 0L)]
    public void ValidatesSingleByteRanges(string header, bool valid, long expectedStart, long expectedEnd)
    {
        Assert.AreEqual(valid, OfflineInstallServer.TryParseRange(header, 100, out var start, out var end));
        if (!valid) return;
        Assert.AreEqual(expectedStart, start);
        Assert.AreEqual(expectedEnd, end);
    }

    [TestMethod]
    public async Task ServesParallelByteRangesAndLogsPeersWithoutLeakingRequestParameters()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        var port = UnusedPort();
        using var log = new StringWriter();
        await using var server = await OfflineInstallServer.StartAsync(fixture.Bundle, IPAddress.Loopback, port, accessLog: log);
        using var cancel = new CancellationTokenSource();
        var running = server.RunAsync(cancel.Token);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var bytes = await File.ReadAllBytesAsync(fixture.Bundle);
            var downloads = Enumerable.Range(0, 4).Select(async index =>
            {
                var start = bytes.Length * index / 4;
                var end = bytes.Length * (index + 1) / 4 - 1;
                using var request = new HttpRequestMessage(HttpMethod.Get, "/bundle.zip");
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                using var response = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
                Assert.AreEqual($"bytes {start}-{end}/{bytes.Length}", response.Content.Headers.ContentRange?.ToString());
                CollectionAssert.AreEqual(bytes[start..(end + 1)], await response.Content.ReadAsByteArrayAsync());
            });
            await Task.WhenAll(downloads);
            using var invalid = new HttpRequestMessage(HttpMethod.Get, "/mcm");
            invalid.Headers.TryAddWithoutValidation("Range", "bytes=999999999-");
            using var rejected = await client.SendAsync(invalid);
            Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, rejected.StatusCode);
            Assert.AreEqual(0, (await rejected.Content.ReadAsByteArrayAsync()).Length);
            using var query = await client.GetAsync("/bundle.zip?token=must-not-log-this");
            Assert.AreEqual(HttpStatusCode.NotFound, query.StatusCode);
            using var other = await client.GetAsync("/private-account-must-not-log-this");
            Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/bundle.zip");
            headRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
            using var head = await client.SendAsync(headRequest);
            Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
            Assert.AreEqual(bytes.Length, head.Content.Headers.ContentLength);
        }
        finally
        {
            cancel.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var access = log.ToString();
        StringAssert.Contains(access, "127.0.0.1 GET /bundle.zip request range=yes");
        StringAssert.Contains(access, "status=206");
        StringAssert.Contains(access, "status=416");
        StringAssert.Contains(access, "status=404");
        StringAssert.Contains(access, "duration_ms=");
        StringAssert.Contains(access, "bytes=");
        Assert.IsFalse(access.Contains("must-not-log-this", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GeneratedBashIsValidAndQuotesUntrustedMetadata()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var manifest = await fixture.ExportAsync();
        var maliciousName = "bad'; $(touch /tmp/mcm-shell-injection); #";
        var script = OfflineInstallServer.BuildScript(new IPEndPoint(IPAddress.Loopback, 5078), new string('a', 64), new string('b', 64),
            manifest with { Instance = manifest.Instance with { Name = maliciousName } }, new FileInfo(fixture.Bundle).Length);
        StringAssert.Contains(script, "'\"'\"'");
        var path = Path.Combine(fixture.Root, "install.sh");
        await File.WriteAllTextAsync(path, script);
        using var bash = Process.Start(new ProcessStartInfo("bash") { ArgumentList = { "-n", path }, UseShellExecute = false });
        Assert.IsNotNull(bash);
        await bash.WaitForExitAsync();
        Assert.AreEqual(0, bash.ExitCode);
    }

    [TestMethod]
    public async Task RejectsInvalidBundleBeforeOpeningPort()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        await fixture.ExportAsync();
        OfflineBundleFixture.ReplaceEntry(fixture.Bundle, "instance/mods/example.jar", "corrupt!!"u8.ToArray());
        var port = UnusedPort();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => OfflineInstallServer.StartAsync(fixture.Bundle, IPAddress.Loopback, port));
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
