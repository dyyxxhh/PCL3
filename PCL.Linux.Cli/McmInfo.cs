using System.Reflection;

namespace PCL.Linux.Cli;

internal static class McmInfo
{
    internal const string RepositoryUrl = "https://github.com/dyyxxhh/PCL3";

    internal static string ReleaseVersion => typeof(McmInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "McmReleaseVersion")?.Value is { Length: > 0 } version
            ? version : "development";

    internal static int Write(TextWriter output)
    {
        output.WriteLine($"MCM Linux CLI {ReleaseVersion}");
        output.WriteLine("项目：PCL₃（三氯化磷启动器），基于 PCL Community Edition 的 fork。");
        output.WriteLine("说明：Minecraft Java 版命令行启动器，提供游戏安装与启动、Java 管理、账户登录、模组与整合包管理、完整离线打包和局域网安装。");
        output.WriteLine("支持：官方 CLI 发布包面向 Linux x86_64 / glibc，已包含 .NET 运行时。Linux CLI 仍在开发中，与 Windows 桌面版的功能不完全一致。");
        output.WriteLine("离线包包含完整 Java；显卡驱动、OpenGL 和系统库须由目标电脑的操作系统提供。");
        output.WriteLine($"项目地址：{RepositoryUrl}");
        output.WriteLine($"下载更新：{RepositoryUrl}/releases");
        output.WriteLine($"使用文档：{RepositoryUrl}/blob/dev/PCL.Linux.Cli/README.md");
        output.WriteLine($"问题反馈：{RepositoryUrl}/issues");
        output.WriteLine($"许可证：Apache-2.0（第三方组件以各自许可证为准）；{RepositoryUrl}/blob/dev/LICENSE");
        output.WriteLine("命令帮助：mcm --help");
        output.WriteLine("检查更新：mcm update（需要联网）；mcm info 本身不访问网络。");
        return 0;
    }
}
