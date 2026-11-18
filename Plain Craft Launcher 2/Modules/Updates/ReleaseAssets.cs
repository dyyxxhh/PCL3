namespace PCL;

/// <summary>
///     GitHub Release 资源命名。
///     主发布使用框架依赖版 <c>PCL3_{configuration}_{arch}.exe</c>，自动更新沿用该名称；
///     自带运行时的压缩单文件版使用 <c>PCL3_{configuration}_{arch}_selfcontained.exe</c> 与同名 <c>.sha256</c>。
/// </summary>
public static class ReleaseAssets
{
    public const string RepositoryUrl = "https://github.com/dyyxxhh/PCL3";
    public const string LatestReleaseUrl = RepositoryUrl + "/releases/latest";
    public const string SelfContainedSuffix = "_selfcontained";

    /// <summary>
    ///     当前构建对应的发布页配置名，与 Release publish 工作流的 configuration 输入一致。
    /// </summary>
    public static string ConfigurationName =>
#if BETA
        "Beta";
#else
        "Release";
#endif

    /// <summary>
    ///     生成发布页资源名。
    /// </summary>
    public static string GetAssetName(UpdateArch architecture, string configuration, bool selfContained, string suffix)
    {
        var arch = architecture == UpdateArch.arm64 ? "ARM64" : "x64";
        return $"PCL3_{configuration}_{arch}{(selfContained ? SelfContainedSuffix : "")}{suffix}";
    }

    /// <summary>
    ///     生成指定 tag 下的资源下载地址。
    /// </summary>
    public static string GetDownloadUrl(string tag, string assetName)
    {
        return $"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{assetName}";
    }
}
