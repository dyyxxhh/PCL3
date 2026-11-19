using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PCL.Core.Shared;
using PCL.Network;

namespace PCL;

public partial class PageToolsTorDownload
{
    private CancellationTokenSource? _download;

    public PageToolsTorDownload() => InitializeComponent();

    private async void StartDownload(object sender, MouseButtonEventArgs e)
    {
        if (_download is not null) return;
        if (!TorLinkDownload.TryParseUrl(TextUrl.Text, out var uri))
        {
            LabStatus.Text = "请输入有效的 HTTP 或 HTTPS 下载链接。";
            return;
        }
        var hash = TextSha256.Text.Trim();
        if (hash.Length > 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            LabStatus.Text = "SHA-256 应包含 64 个十六进制字符。";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        try
        {
            var fileName = Uri.UnescapeDataString(uri.AbsolutePath.Split('/')[^1]);
            var invalid = Path.GetInvalidFileNameChars();
            fileName = string.Concat(fileName.Take(160).Select(c => invalid.Contains(c) ? '_' : c)).Trim(' ', '.');
            var picker = new SaveFileDialog { Title = "保存 Tor 下载", FileName = fileName.Length > 0 ? fileName : "download",
                AddExtension = false, OverwritePrompt = false, CheckPathExists = true };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            if (File.Exists(picker.FileName))
            {
                LabStatus.Text = "已有同名文件。请选择其他文件名或位置。";
                return;
            }

            _download = cancellation;
            ToggleDownload(true);
            LabStatus.Text = "正在连接 Tor…";
            var target = picker.FileName;
            var progress = new Progress<TorLinkDownload.Progress>(value =>
            {
                if (_download != cancellation || cancellation.IsCancellationRequested) return;
                BarProgress.IsIndeterminate = value.Total is null;
                if (value.Total is > 0) BarProgress.Value = 100d * value.Downloaded / value.Total.Value;
                LabStatus.Text = value.Total is long total
                    ? $"通过 Tor 下载中 · {Common.GetString(value.Downloaded)} / {Common.GetString(total)}"
                    : $"通过 Tor 下载中 · 已接收 {Common.GetString(value.Downloaded)}";
            });

            await Task.Run(async () =>
            {
                var settings = WindowsTorSession.Load();
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                startup.CancelAfter(TimeSpan.FromMinutes(2));
                await using var service = await TorNetworkService.StartAsync(settings.Bridges, startup.Token, settings.AutoBridges);
                using var client = service.CreateHttpClient(allowAutoRedirect: false, useSystemProxy: false);
                await TorLinkDownload.SaveAsync(client, uri, target, hash.Length == 0 ? null : hash, progress, cancellation.Token);
            });
            LabStatus.Text = $"已保存到 {target}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            LabStatus.Text = "已取消下载。";
        }
        catch (HttpRequestException ex)
        {
            LabStatus.Text = ex.StatusCode is { } status
                ? $"下载失败，服务器返回 HTTP {(int)status}。"
                : "Tor 下载失败，请检查链接或到 Tor 网络设置测试连接。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or TimeoutException
                                      or OperationCanceledException or InvalidOperationException or JsonException or Win32Exception)
        {
            LabStatus.Text = "下载未完成。请检查 Tor 连接、保存位置或 SHA-256 后重试。";
        }
        finally
        {
            _download = null;
            ToggleDownload(false);
        }
    }

    private void CancelDownload(object sender, MouseButtonEventArgs e) => _download?.Cancel();

    private void ToggleDownload(bool active)
    {
        BtnDownload.IsEnabled = TextUrl.IsEnabled = TextSha256.IsEnabled = !active;
        BtnCancel.IsEnabled = active;
        BarProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        BarProgress.IsIndeterminate = active;
        BarProgress.Value = 0;
    }
}
