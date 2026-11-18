using System.Text.RegularExpressions;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>GitHub Releases 更新源，供本分支使用，不依赖受限 GitHub REST API。</summary>
/// <param name="selfContained">当前是否为自带运行时的发布版；为 True 时下载对应的压缩单文件资产。</param>
/// <param name="name">更新源显示名称。</param>
public sealed class UpdatesGithubReleaseModel(bool selfContained = false, string name = "PCL₃ GitHub Releases")
    : IUpdateSource
{
    private string? _latestTag;

    public string SourceName { get; set; } = name;

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        _latestTag = null;
        return true;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var tag = _latestTag ??= GetLatestTag();
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
        _latestTag = GetLatestTag();
        return currentVersion >= SemVer.Parse(NormalizeVersion(_latestTag));
    }

    public VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = new() };

    public List<Loading.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var tag = _latestTag ??= GetLatestTag();
        var url = ReleaseAssets.GetDownloadUrl(tag, GetAssetName(arch, ".exe"));
        return new List<Loading.LoaderBase>
        {
            new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"),
                new List<DownloadFile> { new(new[] { url }, output) })
        };
    }

    private static string GetLatestTag()
    {
        var htmlParam = RequestParam.WithRetry;
        htmlParam.UseBrowserUserAgent = true;
        var html = Requester.FetchString(ReleaseAssets.LatestReleaseUrl, htmlParam);
        var patterns = new[]
        {
            """<meta\b(?=[^>]*\bproperty\s*=\s*["']og:url["'])(?=[^>]*\bcontent\s*=\s*["']https://github\.com/dyyxxhh/PCL3/releases/tag/(?<tag>[^"'#?&<>\s]+)["'])[^>]*>""",
            """<link\b(?=[^>]*\brel\s*=\s*["']canonical["'])(?=[^>]*\bhref\s*=\s*["']https://github\.com/dyyxxhh/PCL3/releases/tag/(?<tag>[^"'#?&<>\s]+)["'])[^>]*>""",
            """https://github\.com/dyyxxhh/PCL3/releases/tag/(?<tag>[^"'<>\s?#]+)"""
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (match.Success) return Uri.UnescapeDataString(match.Groups["tag"].Value);
        }
        throw new Exception("GitHub Release 页面未包含最新版本标记");
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
