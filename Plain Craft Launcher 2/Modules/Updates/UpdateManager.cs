using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Core.Utils.OS;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

public static class UpdateManager
{
    public static bool isUpdateWaitingRestart;

    public static readonly UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>
    {
        // 自带运行时的发布版必须更新到同名变体，否则替换后会因缺少系统 .NET 运行时无法启动
        new UpdatesGithubReleaseModel(BuildVariant.IsSelfContainedSingleFile)
    });

    public static UpdateEnums.VersionStatus GetVersionStatus()
    {
        try
        {
            return remoteServer.IsLatest(
                UpdateChannel.stable,
                SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64,
                SemVer.Parse(Common.VersionBaseName),
                Common.VersionCode)
                ? UpdateEnums.VersionStatus.Latest
                : UpdateEnums.VersionStatus.NotLatest;
        }
        catch (Exception ex)
        {
            Common.Log(
                ex,
                Lang.Text("Update.Check.Failed"),
                Common.LogLevel.Hint,
                userSummary: Lang.Text("Update.Check.Failed"));
            return UpdateEnums.VersionStatus.Unknown;
        }
    }
    
    internal static Loading.LoaderCombo<JsonObject>? UpdateLoader { get; private set; }
    internal static LoaderDownload? UpdateDownload { get; private set; }

    public static void UpdateStart(UpdateEnums.UpdateType type)
    {
        Common.RunInNewThread(() =>
        {
            try
            {
                var arch = SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64;
                var current = SemVer.Parse(Common.VersionBaseName);
                if (remoteServer.IsLatest(UpdateChannel.stable, arch, current, Common.VersionCode))
                {
                    Common.Log("[Update] 已是最新版本，无需请求更新校验文件");
                    return;
                }
                var version = remoteServer.GetLatestVersion(UpdateChannel.stable, arch);
                if (SemVer.Parse(version.VersionName) <= current)
                    return;

                Common.RunInUi(() =>
                {
                    if (type == UpdateEnums.UpdateType.PromptOnly)
                    {
                        if (Launcher.MyMsgBox(
                                Lang.Text("Update.Available", Common.VersionBaseName, version.VersionName),
                                Lang.Text("Update.Title"), Lang.Text("Update.Action"), Lang.Text("Common.Action.Cancel")) == 1)
                            Launcher.frmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUpdate);
                        return;
                    }

                    StartDownload(version, type);
                });
            }
            catch (Exception ex)
            {
                ReportUpdateFailure(ex, type);
            }
        });
    }

    // This takes the already checked release and registers the job on the UI thread. No network work here.
    internal static void StartDownload(VersionDataModel version, UpdateEnums.UpdateType type)
    {
        if (isUpdateWaitingRestart || UpdateLoader?.State == Common.LoadState.Loading)
            return;

        try
        {
            var dlTargetPath = Path.Combine(Common.ExePath, "PCL", "PCL₃.exe");
            var download = new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"),
                [new DownloadFile([version.DownloadUrl], dlTargetPath)]) { ProgressWeight = 95 };
            var loaders = new List<Loading.LoaderBase>
            {
                download,
                new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Check"), _ =>
                {
                    var curHash = Common.GetFileSha256(dlTargetPath);
                    if (!string.Equals(curHash, version.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new Exception(Lang.Text("Update.Error.Sha256Mismatch", version.Sha256, curHash));
                }) { ProgressWeight = 4 },
                new Loading.LoaderTask<int, int>(Lang.Text("Update.Task.Prepare"), _ =>
                    Common.WriteFile(Path.Combine(Common.pathTemp, "PCL₃UpdateLog.md"), version.Changelog)) { show = false }
            };
            var loader = new Loading.LoaderCombo<JsonObject>(Lang.Text("Update.Title"), loaders);
            loader.OnStateChangedUi += (_, state, _) =>
            {
                if (state == Common.LoadState.Failed)
                    ReportUpdateFailure(loader.Error, type);
                if (state != Common.LoadState.Finished || !ReferenceEquals(UpdateLoader, loader))
                    return;

                isUpdateWaitingRestart = true;
                if (type == UpdateEnums.UpdateType.UpdateNow)
                    UpdateRestart(true);
                else if (type == UpdateEnums.UpdateType.DownloadAndPrompt)
                {
                    LocalizedText.TryBind(Launcher.frmMain.BtnExtraUpdateRestart, "ToolTip", () => Lang.Text(
                        "Main.Extra.UpdateRestart.ToolTipWithVersion", Common.VersionBaseName, version.VersionName));
                    Launcher.frmMain.BtnExtraUpdateRestart.ShowRefresh();
                    Launcher.frmMain.BtnExtraUpdateRestart.Ribble();
                }
            };
            UpdateDownload = download;
            UpdateLoader = loader;
            loader.Start();
            if (type == UpdateEnums.UpdateType.UpdateNow)
            {
                Loading.LoaderTaskbarAdd(loader);
                Launcher.frmMain.BtnExtraDownload.ShowRefresh();
                Launcher.frmMain.BtnExtraDownload.Ribble();
            }
        }
        catch (Exception ex)
        {
            ReportUpdateFailure(ex, type);
        }
    }

    private static void ReportUpdateFailure(Exception ex, UpdateEnums.UpdateType type)
    {
        Common.Log(ex, "[Update] 获取启动器更新失败");
        if (type != UpdateEnums.UpdateType.Silent)
            HintService.Hint(Lang.Text("Update.Error.FetchFailed"), HintType.Error);
    }

    public static void UpdateRestart(bool triggerRestartAndByEnd, bool triggerRestart = true)
    {
        try
        {
            var fileName = Path.Combine(Common.ExePath, "PCL", "PCL₃.exe");
            if (!File.Exists(fileName))
            {
                Common.Log("[System] 更新失败：未找到更新文件");
                isUpdateWaitingRestart = false;
                HintService.Hint(Lang.Text("Update.Error.FetchFailed"), HintType.Error);
                return;
            }

            // id old new restart
            var text =
                $"update {Process.GetCurrentProcess().Id} \"{Basics.ExecutablePath}\" \"{fileName}\" {(triggerRestart ? "true" : "false")}";
            Common.Log("[System] 更新程序启动，参数：" + text);
            Process.Start(new ProcessStartInfo(fileName)
                { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, Arguments = text });
            if (triggerRestartAndByEnd)
            {
                Launcher.frmMain.EndProgram(false, true);
                Common.Log("[System] 已由于更新强制结束程序");
            }
        }
        catch (Win32Exception ex)
        {
            Common.Log(ex, "自动更新时触发 Win32 错误，疑似被拦截");
            Launcher.MyMsgBox(
                Lang.Text("Update.Error.UpdateBlockedMessage", Common.ExePath),
                Lang.Text("Update.Error.UpdateBlocked"),
                Lang.Text("Common.Action.Confirm"),
                "",
                "",
                true);
        }
    }

    /// <summary>
    ///     确保 PathTemp 下的 Latest.exe 是最新正式版的 PCL，它会被用于整合包打包。
    ///     如果不是，则下载一个。
    /// </summary>
    internal static void DownloadLatestPCL(Loading.LoaderBase loaderToSyncProgress = null)
    {
        // 注意：如果要自行实现这个功能，请换用另一个文件路径，以免与官方版本冲突
        var latestPCLPath = Path.Combine(Common.pathTemp, "PCL₃-Latest.exe");
        var target = remoteServer.GetLatestVersion(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64);
        if (target is null)
            throw new Exception(Lang.Text("Update.Error.UnableToGetUpdate"));
        if (File.Exists(latestPCLPath) &&
            (Common.GetFileSha256(latestPCLPath) ?? "") == (target.Sha256 ?? ""))
        {
            Common.Log("[System] 最新版 PCL 已存在，跳过下载");
            return;
        }

        if ((Common.GetFileSha256(Basics.ExecutablePath) ?? "") ==
            (target.Sha256 ?? "")) // 正在使用的版本符合要求，直接拿来用
        {
            Common.CopyFile(Basics.ExecutablePath, latestPCLPath);
            return;
        }

        var loaders = remoteServer.GetDownloadLoader(UpdateChannel.stable,
            SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64, latestPCLPath);
        var loader =
            new Loading.LoaderCombo<int>(Lang.Text("Update.Task.DownloadLatestStable"), loaders);
        loader.Start();
        loader.WaitForExit();
    }

    public static Loading.LoaderTask<int, int> serverLoader =
        new(Lang.Text("Update.Service.Github"),
            _ => LoadOnlineInfo(),
            priority: ThreadPriority.BelowNormal);

    private static void LoadOnlineInfo()
    {
        ScheduleBasedOnConfig();
        // Do not load announcements from the PCL CE update service.
    }

    private static void ScheduleBasedOnConfig()
    {
        switch (Config.Update.UpdateMode)
        {
            case LauncherAutoUpdateBehavior.DownloadAndInstall:
                Common.Log("[Update] 更新设置: 自动下载并安装更新");
                if (GetVersionStatus() != UpdateEnums.VersionStatus.Latest)
                    UpdateStart(UpdateEnums.UpdateType.Silent);
                break;
            case LauncherAutoUpdateBehavior.DownloadAndAnnounce:
                Common.Log("[Update] 更新设置: 自动下载并提示更新");
                UpdateStart(UpdateEnums.UpdateType.DownloadAndPrompt);
                break;
            case LauncherAutoUpdateBehavior.AnnounceOnly:
                Common.Log("[Update] 更新设置: 提示更新");
                UpdateStart(UpdateEnums.UpdateType.PromptOnly);
                break;
            default:
                Common.Log("[Update] 更新设置: 不自动检查更新");
                return;
        }
    }

}
