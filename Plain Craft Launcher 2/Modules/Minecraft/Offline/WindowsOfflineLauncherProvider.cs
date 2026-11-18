using System.IO;
using PCL.Core.App;
using PCL.Core.Utils.OS;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>
///     解析完整 Windows 离线包与局域网安装所需的启动器入口。
///     主发布是框架依赖版，自身不能作为离线包入口，因此按当前版本与架构从 GitHub Release
///     下载自带运行时的压缩单文件版并缓存；自带运行时版直接使用自身。
/// </summary>
internal static class WindowsOfflineLauncherProvider
{
    private const string CacheFolderName = "Offline";
    private const string DownloadTaskName = "下载自带运行时的 PCL₃";

    /// <summary>
    ///     当前进程是否可以直接作为离线包与局域网安装的启动器。
    /// </summary>
    public static bool IsCurrentBuildUsable => BuildVariant.IsSelfContainedSingleFile;

    /// <summary>
    ///     获取可嵌入离线包、并可由局域网安装脚本分发的启动器路径。
    ///     框架依赖版会联网获取同版本的自带运行时压缩版（校验发布页 SHA-256，缓存后复用）。
    /// </summary>
    public static async Task<string> ResolveAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("完整 Windows 离线包只能在 Windows 上导出。");
        if (IsCurrentBuildUsable)
            return Environment.ProcessPath
                   ?? throw new InvalidOperationException("找不到当前启动器的可执行文件。");
        return await DownloadSelfContainedLauncherAsync().ConfigureAwait(true);
    }

    private static async Task<string> DownloadSelfContainedLauncherAsync()
    {
        var architecture = SystemInfo.IsArm64System ? UpdateArch.arm64 : UpdateArch.x64;
        var version = Basics.Metadata.Version.BaseVersion;
        var configuration = ReleaseAssets.ConfigurationName;
        var tag = "v" + version;
        var assetName = ReleaseAssets.GetAssetName(architecture, configuration, true, ".exe");
        var assetUrl = ReleaseAssets.GetDownloadUrl(tag, assetName);
        var shaUrl = ReleaseAssets.GetDownloadUrl(tag, assetName + ".sha256");
        var cacheDirectory = Path.Combine(Common.ExePath, "PCL", CacheFolderName);
        var cachePath = Path.Combine(cacheDirectory, assetName);

        string expected;
        try
        {
            expected = ParseSha256(await Requester.FetchStringAsync(shaUrl, RequestParam.WithRetry).ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"当前是框架依赖版，完整离线包需要自带运行时的 PCL₃（v{version} {configuration}）。" +
                $"自动获取它的校验信息失败，请检查网络后重试，或直接下载发布页的 {assetName} 并用它导出。", ex);
        }

        if (File.Exists(cachePath) &&
            string.Equals(await Task.Run(() => Common.GetFileSha256(cachePath)).ConfigureAwait(true), expected,
                StringComparison.OrdinalIgnoreCase))
        {
            CleanupCache(cacheDirectory, cachePath);
            return cachePath;
        }

        Directory.CreateDirectory(cacheDirectory);
        var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await DownloadAsync(assetUrl, temporary, expected).ConfigureAwait(true);
            // 下载器会按 FileChecker 校验一次，这里再独立确认落盘内容，避免把未校验的产物写进离线包
            if (!string.Equals(await Task.Run(() => Common.GetFileSha256(temporary)).ConfigureAwait(true), expected,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"自带运行时的 PCL₃ 校验失败：{assetName}");
            if (File.Exists(cachePath))
                File.Delete(cachePath);
            File.Move(temporary, cachePath);
            CleanupCache(cacheDirectory, cachePath);
            return cachePath;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // 清理失败不影响导出错误本身
            }

            throw;
        }
    }

    private static async Task DownloadAsync(string url, string target, string sha256)
    {
        var file = new DownloadFile(new[] { url }, target, new Common.FileChecker(hash: sha256));
        var combo = new Loading.LoaderCombo<int>(DownloadTaskName, new List<Loading.LoaderBase>
        {
            new LoaderDownload(DownloadTaskName, new List<DownloadFile> { file })
        })
        {
            OnStateChanged = DownloadUi.LoaderStateChangedHintOnly
        };
        combo.Start();
        Loading.LoaderTaskbarAdd(combo);
        while (combo.State is Common.LoadState.Waiting or Common.LoadState.Loading)
            await Task.Delay(150).ConfigureAwait(true);
        switch (combo.State)
        {
            case Common.LoadState.Finished:
                return;
            case Common.LoadState.Aborted:
                throw new OperationCanceledException("已取消下载自带运行时的 PCL₃。");
            default:
                throw combo.Error ?? new Exception("下载自带运行时的 PCL₃ 失败。");
        }
    }

    private static void CleanupCache(string cacheDirectory, string keepPath)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(cacheDirectory))
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
        }
        catch
        {
            // 缓存清理是尽力而为，失败不影响导出
        }
    }

    private static string ParseSha256(string content)
    {
        var token = content
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (token is null || token.Length != 64 || !token.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("GitHub Release 返回了无效的 SHA-256 校验和。");
        return token.ToLowerInvariant();
    }
}
