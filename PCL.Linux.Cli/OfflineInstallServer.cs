using System.Diagnostics;
using System.Globalization;
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
    private readonly TextWriter _accessLog;
    private long _requestId;
    private Task? _running;
    private bool _disposed;

    private OfflineInstallServer(string bundle, string temporary, string bundleHash, string launcherHash, OfflineBundleManifest manifest, TextWriter? accessLog)
    {
        _bundle = bundle;
        _temporary = temporary;
        _launcher = Path.Combine(temporary, "mcm");
        _bundleHash = bundleHash;
        _launcherHash = launcherHash;
        _manifest = manifest;
        _accessLog = TextWriter.Synchronized(accessLog ?? Console.Out);
    }

    internal static async Task<OfflineInstallServer> StartAsync(string bundle, IPAddress bind, int port,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, TextWriter? accessLog = null)
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
                await HashAsync(launcher, cancellationToken).ConfigureAwait(false), manifest, accessLog);
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
        var id = Interlocked.Increment(ref _requestId);
        var timer = Stopwatch.StartNew();
        var request = context.Request;
        var response = context.Response;
        var peer = request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        // Do not log arbitrary paths, query strings, Host/User-Agent headers or account data.
        var route = request.Url?.AbsolutePath;
        var safeRoute = route is "/" or "/install.sh" or "/direct" or "/bundle.zip" or "/mcm" ? route : "[other]";
        var method = request.HttpMethod is "GET" or "HEAD" ? request.HttpMethod : "[other]";
        var ranged = request.Headers["Range"] is not null;
        long sent = 0;
        var outcome = "completed";
        Log($"#{id} {peer} {method} {safeRoute} request{(ranged ? " range=yes" : "")}");
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (request.RemoteEndPoint is null || !IsLanAddress(request.RemoteEndPoint.Address)) { response.StatusCode = 403; return; }
            if (request.HttpMethod is not ("GET" or "HEAD")) { response.StatusCode = 405; response.Headers["Allow"] = "GET, HEAD"; return; }
            if (request.Url is null || request.Url.Query.Length != 0) { response.StatusCode = 404; return; }
            switch (route)
            {
                case "/":
                case "/install.sh":
                case "/direct":
                    // The local socket, NOT the untrusted HTTP Host header, determines the origin.
                    if (request.LocalEndPoint is null) { response.StatusCode = 400; return; }
                    var script = Encoding.UTF8.GetBytes(BuildScript(request.LocalEndPoint, _bundleHash, _launcherHash,
                        _manifest, new FileInfo(_bundle).Length, direct: route == "/direct"));
                    response.ContentType = "text/x-shellscript; charset=utf-8";
                    response.ContentLength64 = script.Length;
                    if (request.HttpMethod == "GET")
                    {
                        await response.OutputStream.WriteAsync(script, timeout.Token).ConfigureAwait(false);
                        sent = script.Length;
                    }
                    break;
                case "/bundle.zip":
                    await SendFileAsync(context, _bundle, "application/zip", count => sent += count, timeout.Token).ConfigureAwait(false);
                    break;
                case "/mcm":
                    await SendFileAsync(context, _launcher, "application/octet-stream", count => sent += count, timeout.Token).ConfigureAwait(false);
                    break;
                default: response.StatusCode = 404; break;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException)
        {
            // The wire status may already be sent. Preserve it and log a separate interrupted outcome.
            outcome = "interrupted";
        }
        finally
        {
            var status = response.StatusCode;
            try { response.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { outcome = "interrupted"; }
            Log($"#{id} {peer} {method} {safeRoute} status={status} bytes={sent} duration_ms={timer.ElapsedMilliseconds} result={outcome} range={response.Headers["Content-Range"] ?? "-"}");
            _slots.Release();
        }
    }

    private void Log(string message)
    {
        try { _accessLog.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}"); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* Logging must not stop downloads. */ }
    }

    private static async Task SendFileAsync(HttpListenerContext context, string path, string contentType, Action<int> onSent, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var response = context.Response;
        response.ContentType = contentType;
        response.Headers["Accept-Ranges"] = "bytes";
        var start = 0L;
        var end = source.Length - 1;
        var range = context.Request.HttpMethod == "GET" && context.Request.Headers["If-Range"] is null ? context.Request.Headers["Range"] : null;
        if (range is not null)
        {
            if (!TryParseRange(range, source.Length, out start, out end))
            {
                response.StatusCode = 416;
                response.Headers["Content-Range"] = $"bytes */{source.Length}";
                response.ContentLength64 = 0;
                return;
            }
            response.StatusCode = 206;
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{source.Length}";
        }
        var remaining = end - start + 1;
        response.ContentLength64 = remaining;
        if (context.Request.HttpMethod != "GET") return;
        source.Position = start;
        var buffer = new byte[131072];
        while (remaining > 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The served offline file changed during transfer.");
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            remaining -= count;
            onSent(count);
        }
    }

    internal static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        if (size <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var range = header[6..];
        var dash = range.IndexOf('-');
        if (dash < 0 || range.Contains(',')) return false;
        static bool Number(string value, out long result) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
        if (dash == 0)
        {
            if (!Number(range[1..], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
        }
        else
        {
            if (!Number(range[..dash], out start) || start >= size) return false;
            if (dash + 1 < range.Length)
            {
                if (!Number(range[(dash + 1)..], out end) || end < start) return false;
                end = Math.Min(end, size - 1);
            }
        }
        return true;
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

    internal static string BuildScript(IPEndPoint endpoint, string bundleHash, string launcherHash, OfflineBundleManifest manifest, long bundleSize, bool direct = false)
    {
        if (new[] { bundleHash, launcherHash }.Any(hash => hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Invalid bootstrap checksum.");
        var launcherSize = manifest.Files.Single(file => file.Path == OfflineBundleService.LauncherPath).Size;
        if (bundleSize <= 0 || launcherSize <= 0) throw new ArgumentException("Offline downloads require positive file sizes.");
        var origin = new UriBuilder("http", endpoint.Address.ToString(), endpoint.Port).Uri.GetLeftPart(UriPartial.Authority);
        return $$$"""
            #!/usr/bin/env bash
            # MCM offline installation. Only run scripts from a trusted LAN server.
            set -euo pipefail
            umask 077
            fail() { printf 'MCM: %s\n' "$*" >&2; exit 1; }
            for tool in curl sha256sum mktemp chmod mkdir cp ln rm uname wc sleep cat; do
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
            download_pids=()
            cleanup() {
              local pid
              for pid in "${download_pids[@]}"; do kill "$pid" 2>/dev/null || true; done
              for pid in "${download_pids[@]}"; do wait "$pid" 2>/dev/null || true; done
              rm -rf -- "$work"
              if [[ -n "$staged" ]]; then rm -f -- "$staged"; fi
            }
            trap cleanup EXIT
            trap 'exit 130' INT
            trap 'exit 143' TERM
            connections="${MCM_DOWNLOAD_CONNECTIONS:-4}"
            [[ "$connections" =~ ^[1-8]$ ]] || fail 'MCM_DOWNLOAD_CONNECTIONS 必须是 1 到 8。'
            download_file() {
              local url="$1" output="$2" label="$3" size="$4" count="${5:-$connections}"
              local i start end part status downloaded active elapsed speed eta failed=0 started=$SECONDS
              local -a parts=() statuses=() expected=() range_args=()
              (( size > 0 )) || fail '服务端未提供有效的文件长度。'
              # Avoid opening multiple connections for tiny metadata/bootstrap fixtures.
              if (( size < 4194304 )); then count=1; fi
              printf '[%s] 使用 %d 个连接下载\n' "$label" "$count" >&2
              download_pids=()
              for (( i=0; i<count; i++ )); do
                start=$(( size * i / count )); end=$(( size * (i+1) / count - 1 ))
                part="$output.part.$i"; status="$output.status.$i"
                parts+=("$part"); statuses+=("$status"); expected+=("$((end-start+1))")
                range_args=()
                if (( count > 1 )); then range_args=(--range "$start-$end"); fi
                curl --fail --silent --show-error --noproxy '*' --proto '=http' --connect-timeout 10 --max-time 1800 \
                  "${range_args[@]}" --max-filesize "$((end-start+1))" --output "$part" --write-out '%{http_code}' "$url" > "$status" &
                download_pids+=("$!")
              done
              while :; do
                downloaded=0; active=0
                for part in "${parts[@]}"; do
                  if [[ -f "$part" ]]; then downloaded=$(( downloaded + $(wc -c < "$part") )); fi
                done
                for i in "${download_pids[@]}"; do if kill -0 "$i" 2>/dev/null; then active=1; fi; done
                elapsed=$(( SECONDS-started )); if (( elapsed < 1 )); then elapsed=1; fi
                speed=$(( downloaded/elapsed )); eta=0
                if (( speed > 0 && downloaded < size )); then eta=$(( (size-downloaded)/speed )); fi
                if (( downloaded > size )); then downloaded=$size; fi
                printf '\r[%s] %3d%% | %d.%02d/%d.%02d MiB | %d KiB/s | ETA %ds' \
                  "$label" "$((downloaded*100/size))" "$((downloaded/1048576))" "$((downloaded%1048576*100/1048576))" \
                  "$((size/1048576))" "$((size%1048576*100/1048576))" "$((speed/1024))" "$eta" >&2
                if (( active == 0 )); then break; fi
                sleep 0.25
              done
              for i in "${download_pids[@]}"; do if ! wait "$i"; then failed=1; fi; done
              download_pids=()
              for (( i=0; i<count; i++ )); do
                if [[ ! -f "${parts[i]}" || ! -f "${statuses[i]}" ]]; then failed=1; continue; fi
                status="$(< "${statuses[i]}")"
                if [[ "$status" != 200 && "$count" == 1 || "$status" != 206 && "$count" != 1 ]]; then failed=1; fi
                if [[ "$(wc -c < "${parts[i]}")" != "${expected[i]}" ]]; then failed=1; fi
              done
              printf '\n' >&2
              if (( failed )); then
                rm -f -- "${parts[@]}" "${statuses[@]}"
                if (( count > 1 )); then
                  printf '[%s] 分段下载失败，回退为单连接重新下载。\n' "$label" >&2
                  download_file "$url" "$output" "$label" "$size" 1
                  return
                fi
                fail "$label 下载失败，未执行安装。"
              fi
              printf '[%s] 分片下载完成，正在合并。\n' "$label" >&2
              : > "$output"
              for part in "${parts[@]}"; do cat -- "$part" >> "$output"; rm -f -- "$part"; done
              rm -f -- "${statuses[@]}"
              printf '[%s] 下载完成，正在验证 SHA-256。\n' "$label" >&2
            }
            printf '%s\n' '从局域网下载 MCM、完整 Java 和游戏文件；不会访问公网，也不会安装系统驱动。'
            download_file "$base/mcm" "$work/mcm" 'MCM' '{{{launcherSize}}}'
            printf '%s  %s\n' '{{{launcherHash}}}' "$work/mcm" | sha256sum --check --status || fail 'MCM 校验失败，已停止。'
            download_file "$base/bundle.zip" "$work/bundle.zip" '离线包（含完整 Java）' '{{{bundleSize}}}'
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
            cleanup
            trap - EXIT
            manual_launch() {
              printf '\n安装完成。下面的命令不会联网（请自行设置玩家名）：\n'
              printf '  %q login offline <玩家名>\n' "$mcm"
              printf '  %q launch %q --game-dir %q --offline\n' "$mcm" "$instance" "$game_dir"
              printf '  %q offline doctor --instance %q --game-dir %q\n' "$mcm" "$instance" "$game_dir"
            }
            if [[ '{{{(direct ? "direct" : "interactive")}}}' == direct ]]; then
              manual_launch
              printf '%s\n' '提示：显卡驱动和 OpenGL/桌面系统库仍须由目标电脑的操作系统提供；请勿复制来源电脑的驱动。'
              exit 0
            fi
            printf '\n%s\n' '安装完成。显卡驱动和 OpenGL/桌面系统库仍须由目标电脑提供。'
            # stdin belongs to the installer when invoked with curl | bash.
            if ! { exec {terminal}<>/dev/tty; } 2>/dev/null; then
              manual_launch
              fail '没有可交互的终端，未登录或启动。无人值守安装请使用 /direct。'
            fi
            valid_name() { local LC_ALL=C; [[ "$1" =~ ^[a-zA-Z0-9_]{3,16}$ ]]; }
            while :; do
              printf '%s' '请输入离线用户名（3–16 位字母、数字或下划线）：' >&"$terminal"
              if ! IFS= read -r player <&"$terminal"; then
                manual_launch
                fail '未输入用户名，未登录或启动。'
              fi
              if valid_name "$player"; then break; fi
              printf '%s\n' '用户名无效，请重试。' >&"$terminal"
            done
            exec {terminal}>&-
            if ! "$mcm" login offline "$player"; then
              manual_launch
              fail '离线登录失败，未启动游戏。'
            fi
            "$mcm" launch "$instance" --game-dir "$game_dir" --offline
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
