using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCL.Core.Shared;
using PCL.Network;

namespace PCL;

public partial class PageSetupTor
{
    private readonly List<string> _bridges = [];
    private CancellationTokenSource? _testCancellation;
    private bool _loaded;

    public PageSetupTor()
    {
        InitializeComponent();
        Loaded += LoadSettings;
        Unloaded += (_, _) =>
        {
            _testCancellation?.Cancel();
            TextBridge.Clear();
        };
    }

    private void LoadSettings(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        try
        {
            var options = WindowsTorSession.Load();
            CheckFallback.Checked = options.Enabled;
            CheckDiscovery.Checked = options.AutoBridges;
            _bridges.AddRange(options.Bridges ?? []);
            RefreshBridges();
            LabStatus.Text = "Tor 按需连接。";
            _loaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LabStatus.Text = "无法读取 Tor 设置。请检查本地配置文件。";
        }
    }

    private void RefreshBridges(int selection = -1)
    {
        ComboBridges.ItemsSource = new[] { "使用连接设置中的全部网桥" }
            .Concat(Enumerable.Range(1, _bridges.Count).Select(index => $"网桥 {index}"));
        ComboBridges.SelectedIndex = selection + 1;
        BtnClear.IsEnabled = _bridges.Count > 0;
    }

    private void BridgeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BtnRemove is not null) BtnRemove.IsEnabled = ComboBridges.SelectedIndex > 0;
    }

    private void AddBridge(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var bridge = TorNetworkService.ValidateBridge(TextBridge.Password);
            var index = _bridges.IndexOf(bridge);
            if (index < 0)
            {
                index = _bridges.Count;
                _bridges.Add(bridge);
            }
            TextBridge.Clear();
            RefreshBridges(index);
            LabStatus.Text = "请保存网桥更改。";
        }
        catch (ArgumentException)
        {
            LabStatus.Text = "网桥格式不正确，请粘贴完整的一行网桥配置。";
        }
    }

    private void RemoveBridge(object sender, MouseButtonEventArgs e)
    {
        if (ComboBridges.SelectedIndex <= 0) return;
        _bridges.RemoveAt(ComboBridges.SelectedIndex - 1);
        RefreshBridges();
        LabStatus.Text = "请保存网桥更改。";
    }

    private void ClearBridges(object sender, MouseButtonEventArgs e)
    {
        _bridges.Clear();
        RefreshBridges();
        LabStatus.Text = "请保存网桥更改。";
    }

    private void SaveSettings(object sender, MouseButtonEventArgs e)
    {
        try
        {
            WindowsTorSession.Save(new(CheckFallback.Checked == true, CheckDiscovery.Checked == true, _bridges.ToArray()));
            _loaded = true;
            LabStatus.Text = "已保存，将用于新的下载任务。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LabStatus.Text = "无法保存 Tor 设置。请检查本地配置目录的权限。";
        }
    }

    private async void TestConnection(object sender, MouseButtonEventArgs e)
    {
        if (_testCancellation is not null) return;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _testCancellation = cancellation;
        var index = ComboBridges.SelectedIndex - 1;
        var bridges = index >= 0 ? [_bridges[index]] : _bridges.ToArray();
        var discover = index < 0 && CheckDiscovery.Checked == true;
        PanSettings.IsEnabled = false;
        LabStatus.Text = "正在连接 Tor…";
        try
        {
            await Task.Run(async () =>
            {
                await using var service = await TorNetworkService.StartAsync(bridges, cancellation.Token, discover);
            });
            LabStatus.Text = "Tor 连接测试成功。";
        }
        catch (OperationCanceledException)
        {
            LabStatus.Text = "Tor 连接测试已停止。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or TimeoutException
                                      or UnauthorizedAccessException or JsonException or Win32Exception)
        {
            LabStatus.Text = "Tor 连接失败。请检查网桥设置后重试。";
        }
        finally
        {
            _testCancellation = null;
            PanSettings.IsEnabled = true;
        }
    }
}
