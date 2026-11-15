using System.Net;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Linux.Cli;

/// <summary>Read-only, allowlisted HTTP transport for trusted LANs; never proxies internet downloads.</summary>
internal sealed class OfflineInstallServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly string _bundle;
    private readonly string _temporary;
    private readonly string _launcher;
    private readonly string _bundleHash;
    private readonly string _launcherHash;
    private readonly OfflineBundleManifest _manifest;
    private Task? _running;
    private bool _disposed;

    private OfflineInstallServer(string bundle, string temporary, string bundleHash, string launcherHash, OfflineBundleManifest manifest)
    {
        _bundle = bundle;
        _temporary = temporary;
        _launcher = Path.Combine(temporary, "mcm");
        _bundleHash = bundleHash;
        _launcherHash = launcherHash;
        _manifest = manifest;
    }

    internal static async Task<OfflineInstallServer> StartAsync(string bundle, IPAddress bind, int port,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (!bind.Equals(IPAddress.Any) && !bind.Equals(IPAddress.IPv6Any) && !IsLanAddress(bind))
            throw new ArgumentException("Offline serving must bind to a LAN or loopback address.");
        bundle = Path.GetFullPath(bundle);
        var manifest = await OfflineBundleService.VerifyAsync(bundle, progress, cancellationToken).ConfigureAwait(false);
        if (manifest.TargetRuntime != "linux-x64" || manifest.TargetRuntime != OfflineBundleService.CurrentTargetRuntime)
            throw new PlatformNotSupportedException("The bundled MCM bootstrap currently requires a linux-x64 server and client.");
        var temporary = Path.Combine(Path.GetTempPath(), "mcm-offline-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        OfflineInstallServer? server = null;
        try
        {
            var launcher = Path.Combine(temporary, "mcm");
            await OfflineBundleService.ExtractLauncherAsync(bundle, launcher, cancellationToken).ConfigureAwait(false);
            server = new OfflineInstallServer(bundle, temporary, await HashAsync(bundle, cancellationToken).ConfigureAwait(false),
                await HashAsync(launcher, cancellationToken).ConfigureAwait(false), manifest);
            var host = bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any) ? "*"
                : bind.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{bind}]" : bind.ToString();
            server._listener.Prefixes.Add($"http://{host}:{port}/");
            server._listener.Start();
            return server;
        }
        catch
        {
            if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
            else if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            throw;
        }
    }

    internal Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running is not null) throw new InvalidOperationException("The offline server is already running.");
        return _running = RunCoreAsync(cancellationToken);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        using var registration = token.Register(() => _listener.Stop());
        var active = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _slots.WaitAsync(token).ConfigureAwait(false);
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                active.RemoveAll(task => task.IsCompleted);
                active.Add(HandleLimitedAsync(context, token));
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        finally { await Task.WhenAll(active).ConfigureAwait(false); }
    }

    private async Task HandleLimitedAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (request.RemoteEndPoint is null || !IsLanAddress(request.RemoteEndPoint.Address)) { response.StatusCode = 403; return; }
            if (request.HttpMethod is not ("GET" or "HEAD")) { response.StatusCode = 405; response.Headers["Allow"] = "GET, HEAD"; return; }
            if (request.Url is null || request.Url.Query.Length != 0) { response.StatusCode = 404; return; }
            switch (request.Url.AbsolutePath)
            {
                case "/":
                case "/install.sh":
                    // The local socket, NOT the untrusted HTTP Host header, determines the origin.
                    if (request.LocalEndPoint is null) { response.StatusCode = 400; return; }
                    var script = Encoding.UTF8.GetBytes(BuildScript(request.LocalEndPoint, _bundleHash, _launcherHash, _manifest));
                    response.ContentType = "text/x-shellscript; charset=utf-8";
                    response.ContentLength64 = script.Length;
                    if (request.HttpMethod == "GET") await response.OutputStream.WriteAsync(script, timeout.Token).ConfigureAwait(false);
                    break;
                case "/bundle.zip":
                    await SendFileAsync(context, _bundle, "application/zip", timeout.Token).ConfigureAwait(false);
                    break;
                case "/mcm":
                    await SendFileAsync(context, _launcher, "application/octet-stream", timeout.Token).ConfigureAwait(false);
                    break;
                default: response.StatusCode = 404; break;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException)
        {
            // Disconnects and cancelled downloads must not stop serving other clients.
            try { context.Response.StatusCode = 500; } catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
        }
        finally
        {
            try { context.Response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            _slots.Release();
        }
    }

    private static async Task SendFileAsync(HttpListenerContext context, string path, string contentType, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = source.Length;
        if (context.Request.HttpMethod == "GET") await source.CopyToAsync(context.Response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254
            : address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }

    internal static string BuildScript(IPEndPoint endpoint, string bundleHash, string launcherHash, OfflineBundleManifest manifest)
    {
        if (new[] { bundleHash, launcherHash }.Any(hash => hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Invalid bootstrap checksum.");
        var origin = new UriBuilder("http", endpoint.Address.ToString(), endpoint.Port).Uri.GetLeftPart(UriPartial.Authority);
        return $$$"""
            #!/usr/bin/env bash
            # MCM offline installation. Only run scripts from a trusted LAN server.
            set -euo pipefail
            umask 077
            fail() { printf 'MCM: %s\n' "$*" >&2; exit 1; }
            for tool in curl sha256sum mktemp chmod mkdir cp ln rm uname; do
              command -v "$tool" >/dev/null 2>&1 || fail "缺少系统工具 $tool；请先通过离线系统介质安装。"
            done
            [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]] || fail '此离线包仅支持 Linux x86_64（glibc），不支持 Windows、ARM 或 musl/Alpine。'
            : "${HOME:?HOME is required}"
            base={{{ShellQuote(origin)}}}
            default_instance={{{ShellQuote(manifest.Instance.Name)}}}
            instance="${MCM_INSTANCE:-$default_instance}"
            game_dir="${MCM_GAME_DIR:-${PCL_GAME_DIR:-$HOME/.minecraft}}"
            install_dir="${MCM_INSTALL_DIR:-$HOME/.local/bin}"
            work="$(mktemp -d "${TMPDIR:-/tmp}/mcm-offline.XXXXXXXX")"
            staged=''
            trap 'rm -rf -- "$work"; if [[ -n "$staged" ]]; then rm -f -- "$staged"; fi' EXIT
            printf '%s\n' '从局域网下载 MCM、完整 Java 和游戏文件；不会访问公网，也不会安装系统驱动。'
            curl --fail --silent --show-error --noproxy '*' --proto '=http' --connect-timeout 10 --max-time 1800 --output "$work/mcm" "$base/mcm"
            printf '%s  %s\n' '{{{launcherHash}}}' "$work/mcm" | sha256sum --check --status || fail 'MCM 校验失败，已停止。'
            curl --fail --silent --show-error --noproxy '*' --proto '=http' --connect-timeout 10 --max-time 1800 --output "$work/bundle.zip" "$base/bundle.zip"
            printf '%s  %s\n' '{{{bundleHash}}}' "$work/bundle.zip" | sha256sum --check --status || fail '离线包校验失败，已停止。'
            mkdir -p -- "$install_dir"
            mcm="$install_dir/mcm"
            if [[ -e "$mcm" || -L "$mcm" ]]; then
              [[ -f "$mcm" && ! -L "$mcm" ]] || fail 'MCM 目标路径不是普通文件，请设置其他 MCM_INSTALL_DIR。'
              printf '%s  %s\n' '{{{launcherHash}}}' "$mcm" | sha256sum --check --status || fail '已有不同版本的 mcm；不会覆盖。请设置其他 MCM_INSTALL_DIR，或使用现有 MCM 手动导入。'
            else
              staged="$(mktemp "$install_dir/.mcm-offline.XXXXXXXX")"
              cp -- "$work/mcm" "$staged"
              chmod 700 "$staged"
              ln -- "$staged" "$mcm" || fail 'MCM 目标已存在或不可写，未覆盖任何文件。'
              rm -f -- "$staged"
              staged=''
            fi
            if ! "$mcm" offline import "$work/bundle.zip" --instance "$instance" --game-dir "$game_dir"; then
              fail '离线导入失败。若 MCM/Java 无法运行，请检查 glibc 和发行版系统库；不会自动联网补装。MCM 可执行文件已保留。'
            fi
            printf '\n安装完成。下面的命令不会联网（请自行设置玩家名）：\n'
            printf '  %q login offline <玩家名>\n' "$mcm"
            printf '  %q launch %q --game-dir %q --offline\n' "$mcm" "$instance" "$game_dir"
            printf '  %q offline doctor --instance %q --game-dir %q\n' "$mcm" "$instance" "$game_dir"
            printf '%s\n' '提示：显卡驱动和 OpenGL/桌面系统库仍须由目标电脑的操作系统提供；请勿复制来源电脑的驱动。'
            """ + "\n";
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener.Close();
        try { if (_running is not null) await _running.ConfigureAwait(false); }
        finally
        {
            _stop.Dispose();
            _slots.Dispose();
            if (Directory.Exists(_temporary)) Directory.Delete(_temporary, true);
        }
    }
}
