using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.Shared;
using PCL.Network;

namespace PCL;

internal static class TorSettingsDialog
{
    internal static void Show(Window? owner)
    {
        var settings = WindowsTorSession.Load();
        var dialog = new Window { Owner = owner, Title = "Tor 下载设置", Width = 480,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = panel;
        var enabled = new CheckBox { Content = "公开资源下载失败时使用 Tor", IsChecked = settings.Enabled };
        var discover = new CheckBox { Content = "允许自动发现网桥", IsChecked = settings.AutoBridges };
        var savedBridges = (settings.Bridges ?? []).ToList();
        var list = new ComboBox();
        var input = new PasswordBox();
        var status = new TextBlock { Text = "Tor 按需连接。", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(enabled);
        panel.Children.Add(discover);
        panel.Children.Add(list);
        panel.Children.Add(input);
        Add("添加网桥", () =>
        {
            var bridge = TorNetworkService.ValidateBridge(input.Password);
            if (!savedBridges.Contains(bridge, StringComparer.Ordinal)) savedBridges.Add(bridge);
            input.Clear();
            Refresh();
            return Task.CompletedTask;
        });
        Add("删除选中的网桥", () =>
        {
            if (list.SelectedIndex >= 0) savedBridges.RemoveAt(list.SelectedIndex);
            Refresh();
            return Task.CompletedTask;
        });
        Add("清除网桥", () => { savedBridges.Clear(); Refresh(); return Task.CompletedTask; });
        Add("测试连接", async () =>
        {
            status.Text = "正在连接 Tor…";
            var bridges = list.SelectedIndex >= 0 ? new[] { savedBridges[list.SelectedIndex] } : savedBridges.ToArray();
            await using var service = await TorNetworkService.StartAsync(bridges,
                autoBridges: list.SelectedIndex < 0 && discover.IsChecked == true);
            status.Text = "Tor 连接测试成功。";
        });
        Add("保存设置", () =>
        {
            WindowsTorSession.Save(new(enabled.IsChecked == true, discover.IsChecked == true, savedBridges.ToArray()));
            status.Text = "设置已保存，将用于新的下载任务。";
            return Task.CompletedTask;
        });
        panel.Children.Add(status);
        Refresh();
        dialog.ShowDialog();

        void Refresh() => list.ItemsSource = Enumerable.Range(1, savedBridges.Count).Select(index => $"网桥 {index}").ToArray();

        void Add(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(button);
            button.Click += async (_, _) =>
            {
                panel.IsEnabled = false;
                try { await action(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException or JsonException)
                { status.Text = ex.Message; }
                finally { panel.IsEnabled = true; }
            };
        }
    }
}
