using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using PCL.Core.App;
using PCL.Core.Shared.Minecraft;

namespace PCL;

internal static class WindowsInstanceBackup
{
    private sealed record Settings(bool Enabled = false, string Scope = "saves", int Retention = 5);

    private static string InstancePath(McInstance instance) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.PathInstance));
    private static string DataPath(McInstance instance) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance.PathIndie));
    private static bool IsIsolated(McInstance instance) => string.Equals(InstancePath(instance), DataPath(instance), StringComparison.OrdinalIgnoreCase);
    private static string Root(McInstance instance)
    {
        var directory = InstancePath(instance);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory)!)!, ".pcl3-backups", id);
    }

    private static Settings Load(McInstance instance)
    {
        var path = Path.Combine(Root(instance), "settings.json");
        InstanceBackupStore.RequireSafePath(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
    }

    private static void Save(McInstance instance, Settings settings)
    {
        var path = Path.Combine(Root(instance), "settings.json");
        InstanceBackupStore.RequireSafePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, settings);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<InstanceBackupStore.Result> CreateAsync(McInstance instance, Settings settings, bool automatic)
    {
        if (settings.Scope == "instance" && !IsIsolated(instance))
            throw new InvalidOperationException("完整备份需要开启实例隔离。共享游戏目录可备份存档。");
        var source = settings.Scope == "saves" ? Path.Combine(DataPath(instance), "saves") : InstancePath(instance);
        return await new InstanceBackupStore(Root(instance)).CreateAsync(source, instance.Name, instance.Name,
            settings.Scope, automatic, settings.Retention).ConfigureAwait(false);
    }

    internal static bool BeforeLaunch(McInstance instance, Func<bool> aborted)
    {
        while (!aborted())
        {
            try
            {
                var settings = Load(instance);
                if (!settings.Enabled) return true;
                var result = CreateAsync(instance, settings, true).GetAwaiter().GetResult();
                if (result.UsedOrdinaryCopy) Common.Log("[Backup] 部分文件使用了普通复制，可能占用更多磁盘空间。");
                return !aborted();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                if (aborted()) return false;
                var choice = Launcher.MyMsgBox(ex.Message, "启动前备份失败", "重试备份", "跳过备份并启动", "取消启动", isWarn: true, forceWait: true);
                if (choice == 2) return !aborted();
                if (choice != 1) return false;
            }
        }
        return false;
    }

    private static async Task<string> RestoreAsync(McInstance instance, string backup, bool clone)
    {
        var store = new InstanceBackupStore(Root(instance));
        var manifest = await store.InspectAsync(backup, instance.Name, instance.Name).ConfigureAwait(false);
        if (manifest.Scope == "instance" && !clone && !IsIsolated(instance))
            throw new InvalidOperationException("请先开启实例隔离，再原地还原完整实例。");
        var directory = InstancePath(instance);
        var parent = Path.GetDirectoryName(directory)!;
        var name = instance.Name + "-restored-" + Guid.NewGuid().ToString("N")[..8];
        var destination = clone ? Path.Combine(parent, name) : manifest.Scope == "saves" ? Path.Combine(DataPath(instance), "saves") : directory;
        var stage = Path.Combine(Path.GetDirectoryName(destination)!, ".pcl3-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (clone && manifest.Scope == "saves")
            {
                await InstanceBackupStore.CopyTreeAsync(directory, stage).ConfigureAwait(false);
                var saves = Path.Combine(stage, "saves");
                if (Directory.Exists(saves)) Directory.Delete(saves, true);
                await InstanceBackupStore.RestorePayloadAsync(backup, manifest, saves).ConfigureAwait(false);
            }
            else await InstanceBackupStore.RestorePayloadAsync(backup, manifest, stage).ConfigureAwait(false);
            if (clone)
            {
                var oldJson = Path.Combine(stage, instance.Name + ".json");
                var json = JsonNode.Parse(await File.ReadAllTextAsync(oldJson).ConfigureAwait(false)) as JsonObject
                           ?? throw new InvalidDataException("备份内的实例 JSON 无效。");
                json["id"] = name;
                await File.WriteAllTextAsync(Path.Combine(stage, name + ".json"), json.ToJsonString()).ConfigureAwait(false);
                File.Delete(oldJson);
                var oldJar = Path.Combine(stage, instance.Name + ".jar");
                if (File.Exists(oldJar)) File.Move(oldJar, Path.Combine(stage, name + ".jar"));
                Directory.Move(stage, destination);
            }
            else
            {
                await CreateAsync(instance, new Settings(Scope: IsIsolated(instance) ? "instance" : "saves"), false).ConfigureAwait(false);
                InstanceBackupStore.ReplaceDirectory(stage, destination);
            }
            return clone ? name : instance.Name;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static void Show(McInstance instance)
    {
        var settings = Load(instance);
        var dialog = new Window { Title = "实例备份和还原", Width = 520, SizeToContent = SizeToContent.Height,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = panel;
        var auto = new CheckBox { Content = "启动前自动备份", IsChecked = settings.Enabled };
        var scope = new ComboBox { ItemsSource = IsIsolated(instance) ? new[] { "仅存档", "完整实例" } : new[] { "仅存档" },
            SelectedIndex = settings.Scope == "instance" && IsIsolated(instance) ? 1 : 0 };
        var retention = new TextBox { Text = settings.Retention.ToString() };
        var backups = new ComboBox();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var store = new InstanceBackupStore(Root(instance));
        void Refresh() { backups.ItemsSource = store.List(); backups.SelectedIndex = 0; }
        panel.Children.Add(auto);
        panel.Children.Add(scope);
        panel.Children.Add(new TextBlock { Text = "自动备份保留数量（1–100）" });
        panel.Children.Add(retention);
        Settings Read() => int.TryParse(retention.Text, out var count) && count is >= 1 and <= 100
            ? new(auto.IsChecked == true, scope.SelectedIndex == 1 ? "instance" : "saves", count)
            : throw new ArgumentException("保留数量必须在 1 到 100 之间。");
        AddButton("保存设置", () => { Save(instance, Read()); status.Text = "设置已保存。"; return Task.CompletedTask; });
        AddButton("立即备份", async () =>
        {
            var result = await CreateAsync(instance, Read(), false);
            Refresh();
            status.Text = result.UsedOrdinaryCopy ? "备份完成。部分文件使用普通复制。" : "备份完成。";
        });
        panel.Children.Add(backups);
        AddButton("原地还原", () => Restore(false));
        AddButton("还原为新实例", () => Restore(true));
        panel.Children.Add(status);
        Refresh();
        dialog.ShowDialog();

        async Task Restore(bool clone)
        {
            if (backups.SelectedItem is not string path) throw new InvalidOperationException("请先选择备份。");
            if (!clone && MessageBox.Show(dialog, "还原将替换当前文件，并先保留保护备份。请关闭正在运行的游戏。", "确认还原", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            var name = await RestoreAsync(instance, path, clone);
            if (clone)
            {
                Config.Instance.IndieV2[Path.Combine(Path.GetDirectoryName(InstancePath(instance))!, name) + Path.DirectorySeparatorChar] = true;
                Loading.LoaderFolderRun(InstanceList.mcInstanceListLoader, GameFolders.mcFolderSelected,
                    Loading.LoaderFolderRunType.ForceRun, 1, @"versions\");
            }
            Refresh();
            status.Text = clone ? $"已还原为实例 {name}。" : "还原完成，已保留还原前的备份。";
        }

        void AddButton(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(button);
            button.Click += async (_, _) =>
            {
                panel.IsEnabled = false;
                status.Text = "处理中…";
                try { await action(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException)
                { status.Text = ex.Message; }
                finally { panel.IsEnabled = true; }
            };
        }
    }
}
