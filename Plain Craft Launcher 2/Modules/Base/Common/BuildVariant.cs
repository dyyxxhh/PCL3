using System.Reflection;

namespace PCL;

/// <summary>
///     当前进程的发布形态。
///     主发布是框架依赖版，依赖系统安装的 .NET 10 Desktop Runtime；
///     自带运行时版把共享框架打进单文件，便于在未安装 .NET 的电脑上运行。
/// </summary>
public static class BuildVariant
{
    /// <summary>
    ///     当前是否为自带 .NET 运行时的单文件发布版。
    ///     单文件发布时入口程序集没有落盘路径；框架依赖版的 deps 清单与核心库仍指向系统共享框架，
    ///     自带运行时版则全部来自 bundle，因此这三项同时为空。
    /// </summary>
    public static bool IsSelfContainedSingleFile { get; } =
        string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location)
        && string.IsNullOrEmpty(AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string)
        && string.IsNullOrEmpty(typeof(object).Assembly.Location);
}
