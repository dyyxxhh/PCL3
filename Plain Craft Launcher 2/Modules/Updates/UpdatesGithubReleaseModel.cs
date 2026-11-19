using PCL.Core.Shared;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>GitHub Releases 更新源，供本分支使用，按平台和资源变体选择正式发布。</summary>
/// <param name="selfContained">当前是否为自带运行时的发布版；为 True 时下载对应的压缩单文件资产。</param>
/// <param name="name">更新源显示名称。</param>
public sealed class UpdatesGithubReleaseModel(bool selfContained = false, string name = "PCL₃ GitHub Releases")
    : IUpdateSource
{
    private string? _latestTag;
    private UpdateArch? _cachedArchitecture;

    public string SourceName { get; set; } = name;

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        _cachedArchitecture = null;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var tag = GetCachedTag(arch);
        if (tag is null) throw new InvalidOperationException("没有适用于当前 Windows 构建的发布资源。");
        var version = NormalizeVersion(tag);
        var shaUrl = ReleaseAssets.GetDownloadUrl(tag, GetAssetName(arch, ".sha256"));
        var sha256 = Requester.FetchString(shaUrl, RequestParam.WithRetry)
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new Exception("GitHub Release 返回了无效的 SHA-256 校验和");

        return new VersionDataModel
        {
            Source = SourceName,
            VersionName = version,
            VersionCode = 0,
            Sha256 = sha256,
            Changelog = string.Empty
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var tag = GetLatestTag(arch);
        return tag is null || currentVersion >= SemVer.Parse(NormalizeVersion(tag));
    }

    public VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = new() };

    public List<Loading.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var tag = GetCachedTag(arch);
        if (tag is null) throw new InvalidOperationException("没有适用于当前 Windows 构建的发布资源。");
        var url = ReleaseAssets.GetDownloadUrl(tag, GetAssetName(arch, ".exe"));
        return new List<Loading.LoaderBase>
        {
            new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"),
                new List<DownloadFile> { new(new[] { url }, output) })
        };
    }

    private string? GetCachedTag(UpdateArch arch) => _cachedArchitecture == arch ? _latestTag : GetLatestTag(arch);

    private string? GetLatestTag(UpdateArch arch)
    {
        var parameters = RequestParam.WithRetry;
        parameters.UseBrowserUserAgent = true;
        var tag = ReleaseCatalog.FindAsync((url, _) => Task.FromResult(Requester.FetchString(url, parameters)),
            ReleaseAssets.RepositoryUrl, ReleasePlatformTarget.Windows, null).GetAwaiter().GetResult();
        _latestTag = tag;
        _cachedArchitecture = arch;
        return tag;
    }

    private string GetAssetName(UpdateArch arch, string suffix)
    {
        return ReleaseAssets.GetAssetName(arch, "Release", selfContained, suffix);
    }

    private static string NormalizeVersion(string tag)
    {
        var value = tag.Trim();
        return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
    }
}
