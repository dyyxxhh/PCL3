using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using PCL.Core.App.Localization;
using Backup = PCL.WindowsInstanceBackup;

namespace PCL;

public partial class PageInstanceBackup
{
    private Backup.Target? _target;
    private bool _busy;

    public PageInstanceBackup()
    {
        InitializeComponent();
        Loaded += BackupPage_Loaded;
    }

    private void BackupPage_Loaded(object sender, RoutedEventArgs e)
    {
        // The instance sidebar reuses this view, so reload settings and history on each visit.
        _target = null;
        PanBack.ScrollToHome();
        LabStatus.Text = "";
        ComboBackups.Items.Clear();
        try
        {
            var instance = PageInstanceLeft.McInstance ?? throw new InvalidOperationException("未选择实例。");
            var target = Backup.Capture(instance);
            var settings = Backup.Load(target.InstanceDirectory);
            CheckAutomatic.Checked = settings.Enabled;
            ComboScope.ItemsSource = target.IsIsolated ? new[] { "仅存档", "完整实例" } : new[] { "仅存档" };
            ComboScope.SelectedIndex = target.IsIsolated && settings.Scope == "instance" ? 1 : 0;
            LabSharedData.Visibility = target.IsIsolated ? Visibility.Collapsed : Visibility.Visible;
            TextRetention.Text = settings.Retention.ToString();
            RefreshBackups(target);
            _target = target;
        }
        catch (Exception ex) when (IsBackupError(ex))
        {
            LabStatus.Text = $"备份设置无法打开：{ex.Message}";
        }
        SyncControls();
    }

    private void RefreshBackups(Backup.Target target)
    {
        var paths = Backup.List(target);
        ComboBackups.Items.Clear();
        foreach (var path in paths)
        {
            ComboBackups.Items.Add(new MyComboBoxItem { Content = Path.GetFileName(path), Tag = path, ToolTip = path });
        }
        ComboBackups.SelectedIndex = paths.Count == 0 ? -1 : 0;
    }

    private void SyncControls()
    {
        PanActions.IsEnabled = _target is not null && !_busy;
        LabBusy.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        var hasBackups = ComboBackups.Items.Count > 0;
        ComboBackups.IsEnabled = BtnRestore.IsEnabled = BtnClone.IsEnabled = hasBackups;
        LabEmpty.Visibility = _target is not null && !hasBackups ? Visibility.Visible : Visibility.Collapsed;
    }

    private Backup.Settings ReadSettings() => int.TryParse(TextRetention.Text, out var count) && count is >= 1 and <= 100
        ? new(CheckAutomatic.Checked == true, ComboScope.SelectedIndex == 1 ? "instance" : "saves", count)
        : throw new ArgumentException("保留数量必须在 1 到 100 之间。");

    private async void Save_Click(object sender, MouseButtonEventArgs e) => await RunAsync(async target =>
    {
        var settings = ReadSettings();
        await Task.Run(() => Backup.Save(target, settings));
        return "设置已保存。";
    });

    private async void Create_Click(object sender, MouseButtonEventArgs e) => await RunAsync(async target =>
    {
        var settings = ReadSettings();
        var result = await Task.Run(() => Backup.CreateAsync(target, settings, false));
        return result.UsedOrdinaryCopy ? "备份完成。部分文件使用普通复制。" : "备份完成。";
    }, refreshBackups: true);

    private async void Restore_Click(object sender, MouseButtonEventArgs e) => await RestoreAsync(clone: false);

    private async void Clone_Click(object sender, MouseButtonEventArgs e) => await RestoreAsync(clone: true);

    private Task RestoreAsync(bool clone) => RunAsync(async target =>
    {
        if (ComboBackups.SelectedItem is not MyComboBoxItem { Tag: string path })
            throw new InvalidOperationException("请先选择备份。");
        if (!clone)
        {
            var choice = await Launcher.MyMsgBoxAsync(
                $"还原将替换实例 {target.Name} 的当前文件，并先保留保护备份。请关闭正在运行的游戏。",
                "确认还原", button2: Lang.Text("Common.Action.Cancel"), isWarn: true);
            if (choice != 1 || !IsCurrent(target)) return null;
        }
        var name = await Task.Run(() => Backup.RestoreAsync(target, path, clone));
        if (clone) Backup.RegisterClone(target, name);
        return clone ? $"已还原为实例 {name}。" : "还原完成，已保留还原前的备份。";
    }, refreshBackups: true);

    private bool IsCurrent(Backup.Target target) => IsLoaded && _target == target && Launcher.frmMain?.pageRight == this;

    private async Task RunAsync(Func<Backup.Target, Task<string?>> action, bool refreshBackups = false)
    {
        if (_busy || _target is not { } target || !IsCurrent(target)) return;
        _busy = true;
        SyncControls();
        try
        {
            var status = await action(target);
            // Do not update the reused page with a different instance's result.
            if (status is not null && IsCurrent(target))
            {
                if (refreshBackups) RefreshBackups(target);
                LabStatus.Text = status;
            }
        }
        catch (Exception ex) when (IsBackupError(ex))
        {
            if (IsCurrent(target)) LabStatus.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SyncControls();
        }
    }

    private static bool IsBackupError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException;
}
