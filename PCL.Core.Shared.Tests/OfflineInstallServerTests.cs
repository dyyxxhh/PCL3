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
    public async Task GeneratedBashIsValidAndQuotesUntrustedMetadata()
    {
        using var fixture = await OfflineBundleFixture.CreateAsync();
        var manifest = await fixture.ExportAsync();
        var maliciousName = "bad'; $(touch /tmp/mcm-shell-injection); #";
        var script = OfflineInstallServer.BuildScript(new IPEndPoint(IPAddress.Loopback, 5078), new string('a', 64), new string('b', 64),
            manifest with { Instance = manifest.Instance with { Name = maliciousName } });
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
