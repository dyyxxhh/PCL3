using System.Net;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared;

public sealed class TorNetworkService : IAsyncDisposable
{
    private readonly Process _process;
    private readonly int _port;
    private readonly string _directory;
    private string? _bundleDirectory;

    private TorNetworkService(Process process, int port, string directory)
        => (_process, _port, _directory) = (process, port, directory);

    public static async Task<TorNetworkService> StartAsync(IEnumerable<string>? bridges = null,
        CancellationToken cancellationToken = default, bool autoBridges = true)
    {
        var custom = (bridges ?? []).Select(ValidateBridge).Distinct(StringComparer.Ordinal).ToArray();
        string? bundleRoot = null;
        try
        {
            using var bundle = TorExpertBundle.Open();
            if (bundle is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MCM_TOR_EXECUTABLE")))
            {
                bundleRoot = NewPrivateDirectory();
                TorExpertBundle.Extract(bundle, bundleRoot);
            }

            try { return await ConnectAsync(custom).ConfigureAwait(false); }
            catch (Exception ex) when (autoBridges && ex is TimeoutException or IOException) { }

            string[][] discovered = [];
            try { discovered = await DiscoverBridgesAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or IOException or JsonException or OperationCanceledException) { }
            var builtins = bundleRoot is not null ? TorExpertBundle.GetBuiltinBridges(bundleRoot) : [];
            foreach (var candidate in discovered.Concat(builtins).DistinctBy(lines => string.Join('\n', lines)))
            {
                try { return await ConnectAsync(candidate).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or IOException) { }
            }
            throw new IOException("Tor could not connect using the available bridge options.");
        }
        finally
        {
            if (bundleRoot is not null) Directory.Delete(bundleRoot, recursive: true);
        }

        async Task<TorNetworkService> ConnectAsync(string[] candidate)
        {
            var service = await StartCandidateAsync(candidate, bundleRoot, cancellationToken).ConfigureAwait(false);
            service._bundleDirectory = bundleRoot;
            bundleRoot = null;
            return service;
        }
    }

    private static string NewPrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcm-tor-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static async Task<TorNetworkService> StartCandidateAsync(string[] bridges, string? bundleRoot, CancellationToken token)
    {
        var executable = Environment.GetEnvironmentVariable("MCM_TOR_EXECUTABLE");
        var local = Path.Combine(bundleRoot ?? AppContext.BaseDirectory, "tor", OperatingSystem.IsWindows() ? "tor.exe" : "tor");
        executable ??= File.Exists(local) ? local : "tor";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var directory = NewPrivateDirectory();
        Process? process = null;
        try
        {
            var torrc = Path.Combine(directory, "torrc");
            var lines = new List<string>
            {
                "ClientOnly 1", $"SocksPort 127.0.0.1:{port}", $"DataDirectory {Quote(directory)}", "Log notice stdout", "SafeLogging 1"
            };
            if (bundleRoot is not null)
            {
                lines.Add("GeoIPFile " + Quote(Path.Combine(bundleRoot, "data", "geoip")));
                lines.Add("GeoIPv6File " + Quote(Path.Combine(bundleRoot, "data", "geoip6")));
            }
            if (bridges.Length > 0)
            {
                lines.Add("UseBridges 1");
                foreach (var transport in bridges.Select(Transport).OfType<string>().Distinct())
                {
                    var defaultTransport = OperatingSystem.IsWindows() ? "lyrebird.exe" : "lyrebird";
                    var sibling = Path.Combine(Path.GetDirectoryName(executable) ?? "", defaultTransport);
                    var embedded = Path.Combine(Path.GetDirectoryName(executable) ?? "", "pluggable_transports", defaultTransport);
                    var program = Environment.GetEnvironmentVariable("MCM_TOR_TRANSPORT") ??
                        (File.Exists(embedded) ? embedded : File.Exists(sibling) ? Path.GetFullPath(sibling) : defaultTransport);
                    lines.Add($"ClientTransportPlugin {transport} exec {Quote(program)}");
                }
                lines.AddRange(bridges.Select(bridge => "Bridge " + bridge));
            }
            await File.WriteAllLinesAsync(torrc, lines, token).ConfigureAwait(false);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process = new Process { StartInfo = new ProcessStartInfo(executable)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
            if (bundleRoot is not null)
            {
                process.StartInfo.WorkingDirectory = Path.Combine(bundleRoot, "tor");
                if (OperatingSystem.IsLinux()) process.StartInfo.Environment["LD_LIBRARY_PATH"] = process.StartInfo.WorkingDirectory;
            }
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(torrc);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => ready.TrySetException(new IOException("Tor exited before connecting. Check the Tor and transport installation."));
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data?.Contains("Bootstrapped 100%", StringComparison.Ordinal) == true) ready.TrySetResult();
            };
            process.ErrorDataReceived += (_, _) => { }; // Never forward Tor's output, which can include bridge details.
            try { process.Start(); }
            catch (Win32Exception) { throw new InvalidOperationException("Tor executable unavailable. Install Tor or set MCM_TOR_EXECUTABLE."); }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(35), token).ConfigureAwait(false);
            return new TorNetworkService(process, port, directory);
        }
        catch
        {
            await CleanupAsync(process, directory).ConfigureAwait(false);
            throw;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Transport(string bridge) => bridge.Split(' ', 2)[0] is var word &&
        word is "obfs4" or "snowflake" or "webtunnel" or "meek_lite" ? word : null;

    public static string ValidateBridge(string bridge)
    {
        bridge = bridge.Trim();
        if (bridge.StartsWith("Bridge ", StringComparison.OrdinalIgnoreCase)) bridge = bridge[7..].TrimStart();
        if (bridge.StartsWith("vanilla ", StringComparison.OrdinalIgnoreCase)) bridge = bridge[8..].TrimStart();
        if (bridge.Length is 0 or > 1024 || bridge.Any(c => char.IsControl(c) || c is '#' or '\\' or '"') ||
            !Regex.IsMatch(bridge, @"^(?:(?:obfs4|snowflake|webtunnel|meek_lite) +)?(?:[0-9.]+|\[[a-fA-F0-9:]+\]):[0-9]{1,5}(?: +[^ ]+)*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid Tor bridge line.");
        return bridge;
    }

    private static async Task<string[][]> DiscoverBridgesAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 256 * 1024 };
        var json = await client.GetStringAsync("https://bridges.torproject.org/moat/circumvention/defaults", token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Tor bridge service returned an invalid response.");
        return settings.EnumerateArray().Where(setting => setting.TryGetProperty("bridges", out _)).Select(setting => setting.GetProperty("bridges"))
            .Where(bridge => bridge.TryGetProperty("bridge_strings", out var lines) && lines.ValueKind == JsonValueKind.Array)
            .Select(bridge => ParseBridges(bridge.GetProperty("bridge_strings")))
            .Where(candidate => candidate.Length > 0).Take(4).ToArray();
    }

    internal static string[] ParseBridges(JsonElement lines)
    {
        var bridges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.String) continue;
            try { bridges.Add(ValidateBridge(line.GetString()!)); }
            catch (ArgumentException) { }
            if (bridges.Count == 8) break;
        }
        return bridges.ToArray();
    }

    public HttpClient CreateHttpClient(TimeSpan? timeout = null, bool allowAutoRedirect = true,
        bool useSystemProxy = true) => new(new SocketsHttpHandler
    {
        UseCookies = false,
        UseProxy = useSystemProxy,
        AllowAutoRedirect = allowAutoRedirect,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, cancellationToken) =>
            await ConnectThroughSocks5Async(context.DnsEndPoint, _port, cancellationToken).ConfigureAwait(false)
    }) { Timeout = timeout ?? TimeSpan.FromSeconds(90) };

    private static async ValueTask<Stream> ConnectThroughSocks5Async(DnsEndPoint destination, int proxyPort,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, proxyPort, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellationToken).ConfigureAwait(false);
            var greeting = new byte[2];
            await ReadExactlyAsync(stream, greeting, cancellationToken).ConfigureAwait(false);
            if (greeting[0] != 5 || greeting[1] != 0)
                throw new IOException("The Tor SOCKS proxy rejected the connection method.");

            var host = Encoding.ASCII.GetBytes(destination.Host);
            if (host.Length is 0 or > 255 || destination.Port is < 1 or > 65535)
                throw new IOException("The requested Tor destination is invalid.");
            var request = new byte[7 + host.Length];
            request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3; request[4] = checked((byte)host.Length);
            host.CopyTo(request, 5);
            request[^2] = (byte)(destination.Port >> 8); request[^1] = (byte)destination.Port;
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

            var response = new byte[4];
            await ReadExactlyAsync(stream, response, cancellationToken).ConfigureAwait(false);
            if (response[0] != 5 || response[1] != 0)
                throw new IOException($"Tor could not connect to the requested resource (SOCKS error {response[1]}).");
            var addressLength = response[3] switch
            {
                1 => 4,
                4 => 16,
                3 => await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false),
                _ => throw new IOException("Tor returned an invalid SOCKS address type.")
            };
            var trailing = new byte[addressLength + 2];
            await ReadExactlyAsync(stream, trailing, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Tor closed the SOCKS connection during negotiation.");
            buffer = buffer[count..];
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        await ReadExactlyAsync(stream, buffer, token).ConfigureAwait(false);
        return buffer[0];
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync(_process, _directory).ConfigureAwait(false);
        if (_bundleDirectory is not null) Directory.Delete(_bundleDirectory, recursive: true);
    }

    private static async Task CleanupAsync(Process? process, string directory)
    {
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException) { }
            finally { process.Dispose(); }
        }
        InstanceBackupStore.RequireSafePath(directory);
        Directory.Delete(directory, true);
    }
}
