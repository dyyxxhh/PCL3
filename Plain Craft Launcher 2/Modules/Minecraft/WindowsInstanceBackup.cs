using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.Shared.Minecraft;

namespace PCL;

internal static class WindowsInstanceBackup
{
    internal sealed record Settings(bool Enabled = false, string Scope = "saves", int Retention = 5);

    internal sealed record Target(string Name, string InstanceDirectory, string GameDirectory, bool IsIsolated)
    {
        public string DataDirectory => IsIsolated ? InstanceDirectory : GameDirectory;
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string GetGameDirectory(string instanceDirectory) => Path.GetDirectoryName(Path.GetDirectoryName(instanceDirectory)!)!;

    internal static Target Capture(McInstance instance)
    {
        var instanceDirectory = NormalizePath(instance.PathInstance);
        // PathIndie returns the globally selected game directory for non-isolated instances.
        // Anchor shared saves to this instance's own game directory before navigation can change it.
        var isIsolated = string.Equals(instanceDirectory, NormalizePath(instance.PathIndie), StringComparison.OrdinalIgnoreCase);
        return new Target(instance.Name, instanceDirectory, GetGameDirectory(instanceDirectory), isIsolated);
    }

    private static string Root(string instanceDirectory)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceDirectory.ToUpperInvariant())));
        return Path.Combine(GetGameDirectory(instanceDirectory), ".pcl3-backups", id);
    }

    internal static Settings Load(string instanceDirectory)
    {
        var path = Path.Combine(Root(instanceDirectory), "settings.json");
        InstanceBackupStore.RequireSafePath(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
    }

    internal static void Save(Target target, Settings settings)
    {
        var path = Path.Combine(Root(target.InstanceDirectory), "settings.json");
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

    internal static IReadOnlyList<string> List(Target target) => new InstanceBackupStore(Root(target.InstanceDirectory)).List();

    internal static Task<InstanceBackupStore.Result> CreateAsync(Target target, Settings settings, bool automatic)
    {
        if (settings.Scope == "instance" && !target.IsIsolated)
            throw new InvalidOperationException("完整备份需要开启实例隔离。共享游戏目录可备份存档。");
        var source = settings.Scope == "saves" ? Path.Combine(target.DataDirectory, "saves") : target.InstanceDirectory;
        return new InstanceBackupStore(Root(target.InstanceDirectory)).CreateAsync(source, target.Name, target.Name,
            settings.Scope, automatic, settings.Retention);
    }

    internal static bool BeforeLaunch(McInstance instance, Func<bool> aborted)
    {
        while (!aborted())
        {
            try
            {
                var settings = Load(NormalizePath(instance.PathInstance));
                if (!settings.Enabled) return true;
                var result = CreateAsync(Capture(instance), settings, true).GetAwaiter().GetResult();
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

    internal static async Task<string> RestoreAsync(Target target, string backup, bool clone)
    {
        var store = new InstanceBackupStore(Root(target.InstanceDirectory));
        var manifest = await store.InspectAsync(backup, target.Name, target.Name).ConfigureAwait(false);
        if (manifest.Scope == "instance" && !clone && !target.IsIsolated)
            throw new InvalidOperationException("请先开启实例隔离，再原地还原完整实例。");
        var directory = target.InstanceDirectory;
        var parent = Path.GetDirectoryName(directory)!;
        var name = target.Name + "-restored-" + Guid.NewGuid().ToString("N")[..8];
        var destination = clone ? Path.Combine(parent, name) : manifest.Scope == "saves" ? Path.Combine(target.DataDirectory, "saves") : directory;
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
                var oldJson = Path.Combine(stage, target.Name + ".json");
                var json = JsonNode.Parse(await File.ReadAllTextAsync(oldJson).ConfigureAwait(false)) as JsonObject
                           ?? throw new InvalidDataException("备份内的实例 JSON 无效。");
                json["id"] = name;
                await File.WriteAllTextAsync(Path.Combine(stage, name + ".json"), json.ToJsonString()).ConfigureAwait(false);
                File.Delete(oldJson);
                var oldJar = Path.Combine(stage, target.Name + ".jar");
                if (File.Exists(oldJar)) File.Move(oldJar, Path.Combine(stage, name + ".jar"));
                Directory.Move(stage, destination);
            }
            else
            {
                await CreateAsync(target, new Settings(Scope: target.IsIsolated ? "instance" : "saves"), false).ConfigureAwait(false);
                InstanceBackupStore.ReplaceDirectory(stage, destination);
            }
            return clone ? name : target.Name;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static void RegisterClone(Target target, string name)
    {
        var cloneDirectory = Path.Combine(Path.GetDirectoryName(target.InstanceDirectory)!, name);
        Config.Instance.IndieV2[cloneDirectory + Path.DirectorySeparatorChar] = true;
        // Do not switch or restart the instance list for another game directory after navigation.
        if (GameFolders.mcFolderSelected is { } current &&
            string.Equals(NormalizePath(current), target.GameDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Loading.LoaderFolderRun(InstanceList.mcInstanceListLoader, current,
                Loading.LoaderFolderRunType.ForceRun, 1, "versions" + Path.DirectorySeparatorChar);
        }
    }
}
