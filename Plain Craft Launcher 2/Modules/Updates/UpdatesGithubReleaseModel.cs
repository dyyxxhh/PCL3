using System.Text.Json.Nodes;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Utils;
using PCL.Network;
using PCL.Network.Loaders;

namespace PCL;

/// <summary>GitHub Releases 更新源，供本分支使用，不兼容 PCL CE 的 Minio 清单。</summary>
public sealed class UpdatesGithubReleaseModel : IUpdateSource
{
    private const string ApiUrl = "https://api.github.com/repos/dyyxxhh/pcl3/releases";
    private JsonObject? _release;

    public UpdatesGithubReleaseModel(string name = "PCL₃ GitHub Releases") => SourceName = name;

    public string SourceName { get; set; }

    public bool IsAvailable() => true;

    public bool RefreshCache()
    {
        _release = GetRelease();
        return _release is not null;
    }

    public VersionDataModel GetLatestVersion(UpdateChannel channel, UpdateArch arch)
    {
        var release = GetReleaseForChannel(channel);
        var asset = FindAsset(release, arch, ".exe");
        var shaAsset = FindAsset(release, arch, ".sha256");
        if (asset is null || shaAsset is null)
            throw new Exception($"GitHub Release 缺少 {arch} 更新文件或 SHA-256 文件");

        var sha256 = Requester.FetchString(shaAsset["browser_download_url"]!.ToString(), RequestParam.WithRetry)
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new Exception("GitHub Release 返回了无效的 SHA-256 校验和");
        return new VersionDataModel
        {
            Source = SourceName,
            VersionName = NormalizeVersion(release["tag_name"]?.ToString()),
            VersionCode = 0,
            Sha256 = sha256,
            Changelog = release["body"]?.ToString() ?? string.Empty
        };
    }

    public bool IsLatest(UpdateChannel channel, UpdateArch arch, SemVer currentVersion, int currentVersionCode)
    {
        var latestVersion = NormalizeVersion(GetReleaseForChannel(channel)["tag_name"]?.ToString());
        return currentVersion >= SemVer.Parse(latestVersion);
    }

    public VersionAnnouncementDataModel GetAnnouncementList() => new() { Content = new() };

    public List<Loading.LoaderBase> GetDownloadLoader(UpdateChannel channel, UpdateArch arch, string output)
    {
        var release = GetReleaseForChannel(channel);
        var asset = FindAsset(release, arch, ".exe")
            ?? throw new Exception($"GitHub Release 缺少 {arch} 更新文件");
        return new List<Loading.LoaderBase>
        {
            new LoaderDownload(Lang.Text("Update.Task.DownloadUpdateFile"),
                new List<DownloadFile> { new(new[] { asset["browser_download_url"]!.ToString() }, output) })
        };
    }

    private JsonObject GetReleaseForChannel(UpdateChannel channel)
    {
        var releases = Requester.FetchJson(ApiUrl, RequestParam.WithRetry) as JsonArray
            ?? throw new Exception("GitHub Releases 返回格式无效");
        var release = releases.OfType<JsonObject>()
            .Where(x => x["draft"]?.GetValue<bool>() == false)
            .Where(x => x["prerelease"]?.GetValue<bool>() == false)
            .OrderByDescending(x => x["published_at"]?.ToString())
            .FirstOrDefault();
        return release ?? throw new Exception("GitHub Releases 没有可用版本");
    }

    private JsonObject? GetRelease()
    {
        try { return GetReleaseForChannel(UpdateChannel.stable); }
        catch { return null; }
    }

    private static JsonObject? FindAsset(JsonObject release, UpdateArch arch, string suffix)
    {
        var archName = arch == UpdateArch.arm64 ? "ARM64" : "x64";
        return (release["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x =>
            x["name"]?.ToString().EndsWith($"_{archName}{suffix}", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string NormalizeVersion(string? tag)
    {
        var value = (tag ?? string.Empty).Trim();
        return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value[1..] : value;
    }
}
