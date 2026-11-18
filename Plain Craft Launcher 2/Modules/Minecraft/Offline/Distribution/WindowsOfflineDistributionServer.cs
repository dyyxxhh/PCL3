using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

namespace PCL.Offline.Distribution;

public sealed record WindowsOfflineDistributionPackage(string BundlePath, string LauncherPath, string TargetRuntime, string InstanceName);

public sealed class WindowsOfflineDistributionServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SemaphoreSlim _slots = new(8);
    private readonly WindowsOfflineDistributionPackage _package;
    private readonly string _bundleHash;
    private readonly string _launcherHash;
    private readonly long _bundleSize;
    private readonly long _launcherSize;
    private readonly Action<string>? _log;

    private WindowsOfflineDistributionServer(WebApplication app, WindowsOfflineDistributionPackage package,
        string bundleHash, string launcherHash, long bundleSize, long launcherSize, IPAddress address, int port, Action<string>? log)
    {
        _app = app;
        _package = package;
        _bundleHash = bundleHash;
        _launcherHash = launcherHash;
        _bundleSize = bundleSize;
        _launcherSize = launcherSize;
        _log = log;
        SetOrigin(address, port);
    }

    private void SetOrigin(IPAddress address, int port)
    {
        Origin = new UriBuilder("http", address.ToString(), port).Uri.GetLeftPart(UriPartial.Authority);
        PowerShellCommand = "$request=[Net.HttpWebRequest]::Create('" + Origin + "/install.ps1');$request.Proxy=$null;$request.AllowAutoRedirect=$false;$request.Timeout=15000;$request.ReadWriteTimeout=15000;$response=$request.GetResponse();try{if([int]$response.StatusCode -ne 200 -or $response.ContentLength -lt 1 -or $response.ContentLength -gt 65536){throw 'Invalid installer response'};$reader=[IO.StreamReader]::new($response.GetResponseStream());try{$installer=[scriptblock]::Create($reader.ReadToEnd())}finally{$reader.Dispose()}}finally{$response.Dispose()};& $installer";
        CmdCommand = $"powershell.exe -NoLogo -NoProfile -Command \"try {{ {PowerShellCommand} }} catch {{ [Console]::Error.WriteLine($_); exit 1 }}\"";
    }

    public string Origin { get; private set; } = "";
    public string PowerShellCommand { get; private set; } = "";
    public string CmdCommand { get; private set; } = "";

    public static async Task<WindowsOfflineDistributionServer> StartAsync(WindowsOfflineDistributionPackage package,
        IPAddress bind, int port = 5078, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (port is < 0 or > 65535 || port == 0 && !IPAddress.IsLoopback(bind)) throw new ArgumentOutOfRangeException(nameof(port));
        if (!IsLanAddress(bind)) throw new ArgumentException("Bind address must be loopback or a private LAN address.", nameof(bind));
        ValidateRegularFile(package.BundlePath);
        ValidateRegularFile(package.LauncherPath);
        if (package.TargetRuntime is not ("win-x64" or "win-arm64")) throw new PlatformNotSupportedException("Windows offline package target is unsupported.");
        if (package.TargetRuntime != $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}")
            throw new PlatformNotSupportedException("The package architecture must match the server architecture.");
        if (string.IsNullOrWhiteSpace(package.InstanceName) || package.InstanceName is "." or ".."
            || package.InstanceName.Any(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\'))
            throw new ArgumentException("Invalid offline instance name.", nameof(package));
        var bundle = Path.GetFullPath(package.BundlePath);
        var launcher = Path.GetFullPath(package.LauncherPath);
        var bundleInfo = new FileInfo(bundle);
        var launcherInfo = new FileInfo(launcher);
        var bundleHash = await HashAsync(bundle, cancellationToken).ConfigureAwait(false);
        var launcherHash = await HashAsync(launcher, cancellationToken).ConfigureAwait(false);
        var manifest = await OfflineBundleService.VerifyAsync(bundle, cancellationToken: cancellationToken).ConfigureAwait(false);
        var bundledLauncher = manifest.Files.SingleOrDefault(file => file.Path == OfflineBundleService.WindowsLauncherPath);
        if (manifest.Layout != "pcl-windows" || manifest.TargetRuntime != package.TargetRuntime
            || manifest.Instance.Name != package.InstanceName || bundledLauncher is null
            || bundledLauncher.Size != launcherInfo.Length || !bundledLauncher.Sha256.Equals(launcherHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Windows launcher and verified bundle manifest do not match.");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(bind, port, listen => listen.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        var server = new WindowsOfflineDistributionServer(app, package with { BundlePath = bundle, LauncherPath = launcher },
            bundleHash, launcherHash, bundleInfo.Length, launcherInfo.Length, bind, port, log);
        app.Run(async context => await server.HandleAsync(context).ConfigureAwait(false));
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            if (port == 0) server.SetOrigin(bind, new Uri(app.Urls.Single()).Port);
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var timer = Stopwatch.StartNew();
        var route = context.Request.Path.Value switch
        {
            "/" => "/", "/install.ps1" => "/install.ps1", "/PCL3.exe" => "/PCL3.exe", "/bundle.zip" => "/bundle.zip", _ => "[other]"
        };
        var method = context.Request.Method is "GET" or "HEAD" ? context.Request.Method : "[other]";
        var peer = context.Connection.RemoteIpAddress;
        var status = StatusCodes.Status200OK;
        long sent = 0;
        await _slots.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            if (peer is null || !IsLanAddress(peer)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (method == "[other]") { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; context.Response.Headers.Allow = "GET, HEAD"; return; }
            if (context.Request.QueryString.HasValue) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (route is "/" or "/install.ps1")
            {
                var bytes = Encoding.UTF8.GetBytes(BuildInstallScript(_package, Origin, _bundleHash, _launcherHash, _bundleSize, _launcherSize));
                context.Response.ContentType = route == "/" ? "text/plain; charset=utf-8" : "text/plain; charset=utf-8";
                context.Response.ContentLength = bytes.Length;
                if (method == "GET") { await context.Response.Body.WriteAsync(bytes, timeout.Token).ConfigureAwait(false); sent = bytes.Length; }
                return;
            }
            var path = route switch { "/PCL3.exe" => _package.LauncherPath, "/bundle.zip" => _package.BundlePath, _ => null };
            if (path is null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var rangeHeader = context.Request.Headers.Range.ToString();
            await SendFileAsync(context, path, route == "/PCL3.exe" ? "application/octet-stream" : "application/zip", rangeHeader,
                method == "HEAD", timeout.Token, count => sent += count).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { status = StatusCodes.Status408RequestTimeout; }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { status = context.Response.StatusCode; }
        finally
        {
            status = context.Response.StatusCode;
            _log?.Invoke($"{DateTimeOffset.Now:O} {peer} {method} {route} status={status} bytes={sent} duration_ms={timer.ElapsedMilliseconds}");
            _slots.Release();
        }
    }

    private static async Task SendFileAsync(HttpContext context, string path, string contentType, string rangeHeader,
        bool head, CancellationToken token, Action<int> written)
    {
        var length = new FileInfo(path).Length;
        var start = 0L;
        var end = length - 1;
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            if (!TryParseRange(rangeHeader, length, out start, out end))
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers.ContentRange = $"bytes */{length}";
                return;
            }
            context.Response.StatusCode = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        }
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentType = contentType;
        context.Response.ContentLength = end - start + 1;
        if (head) return;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        input.Position = start;
        var remaining = end - start + 1;
        var buffer = new byte[131072];
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            remaining -= count;
            written(count);
        }
    }

    public static bool TryParseRange(string value, long size, out long start, out long end)
    {
        start = 0; end = size - 1;
        if (size <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var range = value[6..];
        if (range.Contains(',')) return false;
        var dash = range.IndexOf('-');
        if (dash < 0) return false;
        if (dash == 0)
        {
            if (!long.TryParse(range[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
        }
        else
        {
            if (!long.TryParse(range[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= size) return false;
            if (dash < range.Length - 1 && (!long.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)) return false;
            end = Math.Min(end, size - 1);
        }
        return true;
    }

    public static bool IsLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4
            ? bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254
            : address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
    }

    private static string BuildInstallScript(WindowsOfflineDistributionPackage package, string origin, string bundleHash, string launcherHash, long bundleSize, long launcherSize)
    {
        return $$"""
            param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'PCL3'), [string]$GameDirectory = '', [string]$InstanceName = '{{EscapePs(package.InstanceName)}}', [ValidateRange(1,8)][int]$Connections = 4)
            $ErrorActionPreference = 'Stop'
            if (-not $GameDirectory) { $GameDirectory = Join-Path $InstallDirectory '.minecraft' }
            if (-not [Environment]::Is64BitOperatingSystem -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant() -ne '{{package.TargetRuntime[4..]}}') { throw 'Windows 架构与离线包不匹配。' }
            $origin = '{{origin}}'; $bundleHash = '{{bundleHash}}'; $launcherHash = '{{launcherHash}}'
            $temp = Join-Path ([IO.Path]::GetTempPath()) ('pcl3-offline-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $temp | Out-Null; $activeJobs = [Collections.Generic.List[object]]::new()
            function DownloadSingle([string]$url,[string]$path,[string]$label,[long]$size) {
              $request=[Net.HttpWebRequest]::Create($url); $request.Proxy=$null; $request.AllowAutoRedirect=$false; $request.Timeout=1800000; $response=$request.GetResponse(); if ([long]$response.ContentLength -ne $size) { $response.Dispose(); throw "$label 长度不匹配。" }
              $input=$response.GetResponseStream(); $output=[IO.File]::Create($path); $buffer=New-Object byte[] 131072; [long]$done=0; $timer=[Diagnostics.Stopwatch]::StartNew()
              try { while (($read=$input.Read($buffer,0,$buffer.Length)) -gt 0) { $output.Write($buffer,0,$read); $done += $read; $pct=[Math]::Min(100,[int](100*$done/$size)); $rate=[Math]::Max(1,[int]($done/[Math]::Max(1,$timer.Elapsed.TotalSeconds))); Write-Progress -Activity $label -Status ("{0}% | {1:N1}/{2:N1} MiB | {3:N0} KiB/s | ETA {4:N0}s" -f $pct,($done/1MB),($size/1MB),($rate/1KB),(($size-$done)/$rate)) -PercentComplete $pct } } finally { $output.Dispose(); $input.Dispose(); $response.Dispose() }
              if ($done -ne $size) { throw "$label length mismatch" }
            }
            function Download([string]$url,[string]$path,[string]$label,[long]$size) {
              $count = if ($size -lt 4194304) { 1 } else { $Connections }; $parts=@(); $jobs=@()
              if ($count -eq 1) { DownloadSingle $url $path $label $size; return }
              try {
                for ($i=0; $i -lt $count; $i++) {
                  $start=[long][Math]::Floor($size*$i/$count); $end=[long]([Math]::Floor($size*($i+1)/$count)-1); $part="$path.part.$i"; $parts += $part
                  $jobs += Start-Job -ArgumentList $url,$part,$start,$end,$size -ScriptBlock {
                    param($u,$p,[long]$s,[long]$e,[long]$total)
                    $r=[Net.HttpWebRequest]::Create($u); $r.Proxy=$null; $r.AllowAutoRedirect=$false; $r.Timeout=1800000; $r.AddRange($s,$e)
                    $v=$r.GetResponse(); try { $expected=$e-$s+1; if ([int]$v.StatusCode -ne 206 -or $v.Headers['Content-Range'] -ne "bytes $s-$e/$total" -or [long]$v.ContentLength -ne $expected) { throw 'Invalid ranged response' }; $source=$v.GetResponseStream(); $target=[IO.File]::Create($p); try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }; if ((Get-Item -LiteralPath $p).Length -ne $expected) { throw 'Invalid range length' } } finally { $v.Dispose() }
                  }
                  $activeJobs.Add($jobs[-1])
                }
                $timer=[Diagnostics.Stopwatch]::StartNew()
                while (($jobs | Where-Object State -eq 'Running').Count -gt 0) { $done=0L; foreach ($part in $parts) { if (Test-Path -LiteralPath $part) { $done += (Get-Item -LiteralPath $part).Length } }; $rate=[Math]::Max(1,[int]($done/[Math]::Max(1,$timer.Elapsed.TotalSeconds))); $pct=[Math]::Min(99,[int](100*$done/$size)); Write-Progress -Activity $label -Status ("{0}% | {1:N1}/{2:N1} MiB | {3:N0} KiB/s | ETA {4:N0}s" -f $pct,($done/1MB),($size/1MB),($rate/1KB),(($size-$done)/$rate)) -PercentComplete $pct; Start-Sleep -Milliseconds 250 }
                foreach ($job in $jobs) { Wait-Job $job | Out-Null; if ($job.State -ne 'Completed') { throw "$label range download failed" } }
                $output=[IO.File]::Create($path); try { foreach ($part in $parts) { $input=[IO.File]::OpenRead($part); try { $input.CopyTo($output) } finally { $input.Dispose() } } } finally { $output.Dispose() }
                if ((Get-Item -LiteralPath $path).Length -ne $size) { throw "$label merged length mismatch" }
              } catch {
                foreach ($job in $jobs) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }; foreach ($part in $parts) { Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue }; Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
                Write-Host "$label 分段下载失败，清理后回退单连接重试。"; DownloadSingle $url $path $label $size; return
              } finally { foreach ($job in $jobs) { Remove-Job $job -Force -ErrorAction SilentlyContinue }; foreach ($part in $parts) { Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue } }
            }
            try {
              $exe=Join-Path $temp 'PCL3.exe'; $zip=Join-Path $temp 'bundle.zip'; Download "$origin/PCL3.exe" $exe 'PCL₃' {{launcherSize}}; Download "$origin/bundle.zip" $zip '离线包' {{bundleSize}}
              if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $launcherHash) { throw 'PCL₃ SHA-256 校验失败。' }; if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $bundleHash) { throw '离线包 SHA-256 校验失败。' }
              New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null; $target=Join-Path $InstallDirectory 'PCL3.exe'
              if (Test-Path -LiteralPath $target) { if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $launcherHash) { throw "已有不同版本的 PCL3.exe：$target；不会覆盖。" } } else { [IO.File]::Move($exe,$target) }
              New-Item -ItemType Directory -Force -Path $GameDirectory | Out-Null
              $quoted = 'offline-import --bundle "' + $zip + '" --game-dir "' + $GameDirectory + '" --instance "' + $InstanceName + '"'; $process=Start-Process -FilePath $target -ArgumentList $quoted -Wait -PassThru; if ($process.ExitCode -ne 0) { throw "离线导入失败，退出代码 $($process.ExitCode)" }; Write-Host "安装完成：$GameDirectory\versions\$InstanceName"
            } finally { foreach ($job in $activeJobs) { Stop-Job $job -ErrorAction SilentlyContinue; Wait-Job $job -ErrorAction SilentlyContinue | Out-Null; Remove-Job $job -Force -ErrorAction SilentlyContinue }; Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue; Write-Progress -Activity 'Offline install' -Completed }
            """;
    }

    private static string EscapePs(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void ValidateRegularFile(string path)
    {
        path = Path.GetFullPath(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
            throw new IOException("Distribution files must be regular files without reparse points.");
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            try { if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Distribution file path contains a reparse point."); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _slots.Dispose();
    }
}
