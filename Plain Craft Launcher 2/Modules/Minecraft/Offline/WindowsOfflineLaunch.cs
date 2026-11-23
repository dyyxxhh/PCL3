using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Essentials;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Core.Shared.Minecraft;

namespace PCL;

/// <summary>Handles the interactive Windows offline LAN installation launcher entry.</summary>
internal static class WindowsOfflineLaunch
{
    private const string UsernamePattern = @"\A[A-Za-z0-9_]{3,16}\z";
    private static bool _started;

    // Register only after the main window and first-run consent are ready.
    internal static void TryStart()
    {
        StartupService.TryHandleCommand("offline-launch", (model, forwarded) =>
        {
            var gameDirectory = model.Arguments.TryGetValue("game-dir", out var gameArg) ? gameArg.ValueText : null;
            var instanceName = model.Arguments.TryGetValue("instance", out var instanceArg) ? instanceArg.ValueText : null;
            var requireConfirmation = forwarded || !ReferenceEquals(model, StartupService.CommandLine.Subcommand);
            Common.RunInUi(() => { _ = RunAsync(gameDirectory, instanceName, requireConfirmation); });
        }, registerCallback: true);
    }

    private static void RejectReparse(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("离线启动目录不能包含重解析点。");
            if (current.Parent is null) break;
        }
    }

    private static void ValidateImportedInstance(string instancePath, string instanceName)
    {
        RejectReparse(instancePath);
        var markerPath = Path.Combine(instancePath, "pcl-offline.json");
        if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("目标实例不是有效的离线导入实例。");
        using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
        var metadata = marker.RootElement;
        if (!metadata.TryGetProperty("offlineBundle", out var flag) || flag.ValueKind != JsonValueKind.True
            || !metadata.TryGetProperty("name", out var name) || !name.ValueEquals(instanceName)
            || !metadata.TryGetProperty("java", out var java) || !java.ValueEquals(OfflineBundleService.WindowsJavaExecutable))
            throw new InvalidDataException("离线包标记与目标实例不匹配。");
        var javaPath = Path.Combine(instancePath, OfflineBundleService.WindowsJavaExecutable.Replace('/', Path.DirectorySeparatorChar));
        RejectReparse(Path.GetDirectoryName(javaPath)!);
        if (!File.Exists(javaPath) || (File.GetAttributes(javaPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("离线实例缺少随包提供的 Java。");
    }

    private static async Task RunAsync(string? gameDirectory, string? instanceName, bool forwarded)
    {
        if (_started || GameLaunch.isLaunching)
        {
            HintService.Hint("当前已有启动任务，请稍后重试。", HintType.Error);
            return;
        }
        _started = true;
        try
        {
            if (string.IsNullOrWhiteSpace(gameDirectory) || string.IsNullOrWhiteSpace(instanceName)
                || instanceName is "." or ".." || instanceName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || instanceName.Contains('/') || instanceName.Contains('\\'))
                throw new ArgumentException("offline-launch 需要安全的 --game-dir 和 --instance。");

            if (!Path.IsPathFullyQualified(gameDirectory) || !Regex.IsMatch(gameDirectory, "^[A-Za-z]:[\\\\/]")
                || gameDirectory.StartsWith("\\\\", StringComparison.Ordinal))
                throw new ArgumentException("离线启动目录必须是本地绝对路径。");
            var root = Path.GetFullPath(gameDirectory);
            var versionsPath = Path.Combine(root, "versions");
            var instancePath = Path.GetFullPath(Path.Combine(versionsPath, instanceName));
            if (!string.Equals(Path.GetDirectoryName(instancePath), versionsPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("实例目录不在目标游戏目录内。");
            // The generic CLI pipe does not authenticate the sender. Ask before touching the supplied path.
            if (forwarded && await Launcher.MyMsgBoxAsync($"收到离线启动请求：{instancePath}\n是否继续？",
                    "确认离线启动", "继续", "取消", isWarn: true) != 1) return;
            if (new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network)
                throw new IOException("离线启动目录不能位于网络驱动器。");
            ValidateImportedInstance(instancePath, instanceName);

            var username = await Launcher.MyMsgBoxInputAsync(
                "请输入离线登录用户名（3–16 位 ASCII 字母、数字或下划线）",
                "局域网安装已完成，现在登录并启动",
                validateRules: [new PCL.Core.Utils.Validate.RegexValidator(UsernamePattern)],
                hintText: "仅限 ASCII 字母、数字或下划线").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(username)) return;
            if (!Regex.IsMatch(username, UsernamePattern, RegexOptions.CultureInvariant))
                throw new ArgumentException("离线用户名格式无效。");

            if (GameLaunch.isLaunching)
            {
                HintService.Hint("当前已有启动任务，请稍后重试。", HintType.Error);
                return;
            }
            ValidateImportedInstance(instancePath, instanceName);
            var instance = new McInstance(instancePath).Load();
            if (instance.state == McInstanceState.Error)
                throw new InvalidDataException(instance.Desc);
            var standardUuid = ProfileUi.GetOfflineUuid(username);
            var profile = ProfileService.Profiles.FirstOrDefault(p => p.ProfileType == ProfileType.Offline
                && p.UserName.Equals(username, StringComparison.Ordinal)
                && p.Uuid.Replace("-", "", StringComparison.Ordinal).Equals(standardUuid, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                profile = new McProfile
                {
                    ProfileType = ProfileType.Offline,
                    UserName = username,
                    Uuid = standardUuid,
                    AccessToken = standardUuid,
                    ClientToken = standardUuid,
                    Description = "",
                    ProfileId = Guid.NewGuid().ToString("N")
                };
                ProfileService.Add(profile);
            }
            else ProfileService.Select(profile);

            GameFolders.mcFolderSelected = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            States.Game.SelectedFolder = root.Replace(Common.ExePath, "$") + (root.EndsWith(Path.DirectorySeparatorChar) ? "" : "\\");
            GameLaunch.McLaunchStart(new GameLaunch.McLaunchOptions { Offline = true, instance = instance });
        }
        catch (Exception ex)
        {
            Common.Log(ex, "局域网离线启动失败", Common.LogLevel.Feedback);
            Launcher.MyMsgBox($"离线启动失败：{ex.Message}", "离线启动失败", isWarn: true);
        }
        finally { _started = false; }
    }
}
