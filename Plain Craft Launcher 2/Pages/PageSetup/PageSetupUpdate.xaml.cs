using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCL.Core.App;
using PCL.Core.Utils;
using PCL.Core.App.Localization;
using PCL.Core.Utils.OS;

namespace PCL;

public partial class PageSetupUpdate : IPageView
{
    private readonly PageSetupUpdateViewModel _vm = new();
    private bool _refreshed;
    private bool _checking;
    private bool _restarting;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private VersionDataModel? _updateInfo;

    public PageSetupUpdate()
    {
        InitializeComponent();
        DataContext = _vm;
        _updateTimer.Tick += (_, _) => RefreshUpdateButton();
        Loaded += (_, _) =>
        {
            Refresh();
            RefreshUpdateButton();
            _updateTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _refreshed = false;
            _updateTimer.Stop();
        };
    }

    public PageSetupUpdateViewModel ViewModel => _vm;

    public void OnNavigatedTo(object? parameter) => Refresh();

    public void OnNavigatedFrom()
    {
    }

    private void Refresh()
    {
        if (_refreshed)
            return;
        _refreshed = true;
        Init();
    }

    private void Init()
    {
        Animations.AniBeginControl();
        ComboSystemUpdateMode.SelectedIndex = (int)Config.Update.UpdateMode;

        _vm.Reload();
        Animations.AniEndControl();
        CheckUpdate();
    }

    private async void CheckUpdate()
    {
        if (_checking || (_updateInfo is not null &&
            (UpdateManager.UpdateLoader?.State == Common.LoadState.Loading || UpdateManager.isUpdateWaitingRestart)))
            return;

        _checking = true;
        Common.Log("[Update] 开始检查更新");
        _vm.ShowUpdateCard = false;
        _vm.ShowCheckCard = true;
        _vm.CheckAgainEnabled = false;
        _vm.CurrentDescText = Lang.Text("Setup.Update.Checking");
        try
        {
            var version = await Task.Run(() =>
            {
                var arch = SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64;
                var current = SemVer.Parse(Common.VersionBaseName);
                if (UpdateManager.remoteServer.IsLatest(UpdateChannel.stable, arch, current, Common.VersionCode))
                    return null;
                var latest = UpdateManager.remoteServer.GetLatestVersion(UpdateChannel.stable, arch);
                return SemVer.Parse(latest.VersionName) > current ? latest : null;
            });
            _updateInfo = version;
            if (version is null)
            {
                _vm.CurrentDescText = Lang.Text("Setup.Update.Latest");
                return;
            }

            _vm.UpdateNameText = "PCL₃ " + VersionNameFormat(version.VersionName);
            var summary = version.Changelog.Between("<summary>", "</summary>");
            if (!version.Changelog.Contains("<summary>") || string.IsNullOrWhiteSpace(summary))
                TextChangelog.TrySetLocalizedValue("Text", "Setup.Update.Changelog.Empty");
            else
                TextChangelog.Text = summary;

            RefreshUpdateButton();
            _vm.ShowUpdateCard = true;
            _vm.ShowCheckCard = false;
        }
        catch (Exception ex)
        {
            _updateInfo = null;
            _vm.CurrentDescText = Lang.Text("Setup.Update.CheckFailed");
            Common.Log(ex, "[Update] 检查更新失败", Common.LogLevel.Hint,
                userSummary: Lang.Text("Setup.Update.Error.NetworkFailed"));
        }
        finally
        {
            _checking = false;
            _vm.CheckAgainEnabled = true;
        }
    }

    private void RefreshUpdateButton()
    {
        if (_restarting)
            return;

        if (UpdateManager.UpdateLoader?.State == Common.LoadState.Loading && UpdateManager.UpdateDownload is { } download)
        {
            BtnUpdate.IsEnabled = false;
            var file = download.files.FirstOrDefault();
            var text = download.State == Common.LoadState.Finished
                ? Lang.Text("Update.Task.Check")
                : file?.TotalSize > 0 && file.DownloadedBytes > 0
                    ? Lang.Number(Math.Clamp((double)file.DownloadedBytes / file.TotalSize, 0, 1), "P1")
                    : file?.DownloadedBytes > 0
                        ? Lang.Text("Update.Task.DownloadUpdateFile")
                        : Lang.Text("Setup.Update.Preparing");
            if (BtnUpdate.Text != text)
                BtnUpdate.Text = text;
            return;
        }

        var label = Lang.Text(UpdateManager.isUpdateWaitingRestart ? "Setup.Update.RestartInstall" : "Setup.Update.Install");
        if (BtnUpdate.Text != label)
            BtnUpdate.Text = label;
        BtnUpdate.IsEnabled = _updateInfo is not null;
    }

    private async void BtnUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        if (!BtnUpdate.IsEnabled || _updateInfo is not { } version)
            return;

        BtnUpdate.IsEnabled = false;
        if (UpdateManager.isUpdateWaitingRestart)
        {
            BtnUpdate.Text = Lang.Text("Update.Task.Install");
            _restarting = true;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                UpdateManager.UpdateRestart(true);
            }
            finally
            {
                _restarting = false;
                RefreshUpdateButton();
            }
            return;
        }

        BtnUpdate.Text = Lang.Text("Setup.Update.Preparing");
        UpdateManager.StartDownload(version, UpdateEnums.UpdateType.UpdateNow);
    }

    private async void BtnChangelogDetail_Click(object sender, EventArgs e)
    {
        var ctl = sender as Control;
        if (ctl is not null) ctl.IsEnabled = false;
        try
        {
            if (_updateInfo is null)
                await Launcher.MyMsgBoxAsync(Lang.Text("Setup.Update.Changelog.Unavailable"), Lang.Text("Setup.Update.Changelog.Title"));
            else
                Launcher.MyMsgBoxMarkdown(_updateInfo.Changelog, Lang.Text("Setup.Update.Changelog.Title"));
        }
        catch (Exception ex)
        {
            Common.Log(ex, "查看更新日志失败", Common.LogLevel.Feedback,
                userSummary: Lang.Text("Setup.Error.OperationFailed"));
        }
        finally
        {
            if (ctl is not null) ctl.IsEnabled = true;
        }
    }

    private void ComboSystemUpdateMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!Animations.AniExitsAnyControl())
            Config.Update.UpdateMode = (LauncherAutoUpdateBehavior)ComboSystemUpdateMode.SelectedIndex;
    }

    private void BtnChangelog_Click(object sender, MouseButtonEventArgs e)
    {
        Common.OpenWebsite("https://github.com/dyyxxhh/PCL3/releases/tag/v" + Common.VersionBaseName);
    }

    public string VersionNameFormat(string str) => PageSetupUpdateViewModel.VersionNameFormat(str);

    private void BtnCheckAgain_OnClick(object sender, MouseButtonEventArgs e)
    {
        CheckUpdate();
    }

}
