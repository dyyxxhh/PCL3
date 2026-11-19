using PCL.Core.App.Localization;
using PCL.Core.Minecraft.ResourceProject;
using CompFileStatus = PCL.Comp.CompFileStatus;
using CompLoaderType = PCL.Comp.CompLoaderType;

namespace PCL;

public static partial class CompDependency
{
    public static Comp.CompDepsInstallTypes ConfirmDependencyInstall(
        ModDependencyResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Unresolved is { Count: > 0 })
        {
            Common.Log($"[CompDeps] 无法解析: {result.Unresolved.Count} 个必需前置");
            var dependencies = string.Join(
                Environment.NewLine,
                result.Unresolved.Select(dep => Lang.Text(
                    "Download.Comp.Dependency.Unresolved.ListItem",
                    dep.Source,
                    dep.ProjectId,
                    dep.Reason)));
            var message = Lang.Text("Download.Comp.Dependency.Unresolved.Message", dependencies);
            var selectedButton = Launcher.MyMsgBox(
                message,
                Lang.Text("Download.Comp.Dependency.Unresolved.Title"),
                "返回调整依赖",
                Lang.Text("Common.Action.Cancel"),
                "仅安装主模组",
                isWarn: true,
                forceWait: true);

            return selectedButton == 3
                ? Comp.CompDepsInstallTypes.WithoutDeps
                : Comp.CompDepsInstallTypes.Cancel;
        }

        if (result.ToInstall is { Count: > 0 })
            Common.Log($"[CompDeps] 自动安装 {result.ToInstall.Count} 个必需前置");
        return Comp.CompDepsInstallTypes.WithDeps;
    }

    private static string GetSource(bool fromCurseForge)
    {
        return fromCurseForge ? "CurseForge" : "Modrinth";
    }

    private static List<string> ToLoaderNames(IEnumerable<CompLoaderType>? loaders)
    {
        if (loaders is null) return [];

        return
        [
            .. loaders
                .Where(static loader => loader != CompLoaderType.Any)
                .Select(static loader => loader.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    private static int MapReleaseType(CompFileStatus status)
    {
        return status switch
        {
            CompFileStatus.Release => 1,
            CompFileStatus.Beta => 2,
            CompFileStatus.Alpha => 3,
            _ => 1
        };
    }
}
