using System.IO;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Utils;

namespace PCL;

public static partial class GameLaunch
{
    public static JavaEntry? mcLaunchJavaSelected;

    internal static JavaEntry SelectOfflineExportJava(McInstance instance, string? executable = null)
    {
        var (minimum, maximum) = _GetMinecraftJavaVersionRange(instance);
        _ApplyRecommendedJavaVersion(ref minimum, instance);
        _ApplyModLoaderJavaRange(ref minimum, ref maximum, instance);
        var majors = _ApplyJsonJavaRequirements(ref minimum, ref maximum, instance);
        lock (JavaRuntime.JavaLock)
        {
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var selected = JavaRuntime.Javas.AddOrGet(executable);
                if (selected is null || !selected.IsEnabled || selected.Installation.Version < minimum || selected.Installation.Version > maximum
                    || majors is not null && !majors.Contains(selected.Installation.MajorVersion))
                    throw new InvalidOperationException("指定的 Java 不满足当前版本及加载器要求。");
                return selected;
            }
            return JavaRuntime.JavaSelect("未找到兼容的本地 Java，请指定 java.exe。", minimum, maximum, instance, true, majors)
                ?? throw new InvalidOperationException("未找到兼容的本地 Java，请指定 java.exe。");
        }
    }

    private static void McLaunchJava(Loading.LoaderTask<int, int> task)
    {
        var (minVer, maxVer) = _GetMinecraftJavaVersionRange();
        var recommendedComponent = _ApplyRecommendedJavaVersion(ref minVer);
        _ApplyModLoaderJavaRange(ref minVer, ref maxVer);
        var compatibleJavaMajorsSet = _ApplyJsonJavaRequirements(ref minVer, ref maxVer);

        lock (JavaRuntime.JavaLock)
        {
            _SelectOrDownloadJava(
                task,
                minVer,
                maxVer,
                compatibleJavaMajorsSet,
                recommendedComponent);
        }
    }

    private static (Version Min, Version Max) _GetMinecraftJavaVersionRange(McInstance? target = null)
    {
        var instance = target ?? InstanceList.McMcInstanceSelected!;
        var info = instance.Info;
        var releaseTime = instance.releaseTime;
        var minVer = new Version(0, 0, 0, 0);
        var maxVer = new Version(999, 999, 999, 999);

        if ((!info.Valid && releaseTime >= new DateTime(2024, 4, 2))
            || (info.Valid && info.vanilla >= new Version(20, 0, 5)))
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] MC 1.20.5+ (24w14a+) 要求至少 Java 21");
            minVer = new Version(21, 0, 0, 0);
        }
        else if ((!info.Valid && releaseTime >= new DateTime(2021, 11, 16))
                 || info is { Valid: true, vanilla.Major: >= 18 })
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] MC 1.18 pre2+ 要求至少 Java 17");
            minVer = new Version(17, 0, 0, 0);
        }
        else if ((!info.Valid && releaseTime >= new DateTime(2021, 5, 11))
                 || info is { Valid: true, vanilla.Major: >= 17 })
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] MC 1.17+ (21w19a+) 要求至少 Java 16");
            minVer = new Version(16, 0, 0, 0);
        }
        else if (releaseTime.Year >= 2017)
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] MC 1.12+ 要求至少 Java 8");
            minVer = new Version(1, 8, 0, 0);
        }
        else if (releaseTime <= new DateTime(2013, 5, 1)
                 && releaseTime.Year >= 2001)
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] MC 1.5.2- 要求最高 Java 12");
            maxVer = new Version(1, 8, 999, 999);
        }

        return (minVer, maxVer);
    }

    private static string? _ApplyRecommendedJavaVersion(ref Version minVer, McInstance? target = null)
    {
        var instance = target ?? InstanceList.McMcInstanceSelected!;
        var recommendedCode =
            instance.JsonObject?["javaVersion"]?["majorVersion"]?.ToObject<int>()
            ?? instance.JsonVersion?["java_version"]?.ToObject<int>()
            ?? 0;

        if (recommendedCode < 22)
            return null;

        McLaunchLog($"Mojang 要求至少使用 Java {recommendedCode}");
        minVer = new Version(recommendedCode, 0, 0, 0);
        var recommendedComponent =
            instance.JsonObject?["javaVersion"]?["component"]?.ToString()
            ?? instance.JsonVersion?["java_component"]?.ToString();
        return string.IsNullOrEmpty(recommendedComponent) ? null : recommendedComponent;
    }

    private static void _ApplyModLoaderJavaRange(ref Version minVer, ref Version maxVer, McInstance? target = null)
    {
        _ApplyOptiFineJavaRange(ref minVer, ref maxVer, target);
        _ApplyForgeJavaRange(ref minVer, ref maxVer, target);
        _ApplyCleanroomJavaRange(ref minVer, target);
        _ApplyFabricJavaRange(ref minVer, target);
        _ApplyLiteLoaderJavaRange(ref maxVer, target);
        _ApplyLabyModJavaRange(ref minVer, ref maxVer, target);
    }

    private static void _ApplyOptiFineJavaRange(ref Version minVer, ref Version maxVer, McInstance? target = null)
    {
        var info = (target ?? InstanceList.McMcInstanceSelected!).Info;
        if (!info.HasOptiFine || !info.Valid)
            return;

        switch (info.vanilla.Major)
        {
            case < 7:
                maxVer = new Version(1, 8, 999, 999);
                break;
            case >= 8 and < 12:
                minVer = new Version(1, 8, 0, 0);
                maxVer = new Version(1, 8, 999, 999);
                break;
            case 12:
                maxVer = new Version(1, 8, 999, 999);
                break;
        }
    }

    private static void _ApplyForgeJavaRange(ref Version minVer, ref Version maxVer, McInstance? target = null)
    {
        var info = (target ?? InstanceList.McMcInstanceSelected!).Info;
        if (!info.HasForge)
            return;

        if (info.vanilla >= new Version(6, 0, 1)
            && info.vanilla <= new Version(7, 0, 2))
        {
            minVer = _MaxVersion(minVer, new Version(1, 7, 0, 0));
            maxVer = _MinVersion(maxVer, new Version(1, 7, 999, 999));
        }
        else if (info.vanilla.Major <= 12 || !info.Valid)
        {
            maxVer = new Version(1, 8, 999, 999);
        }
        else
        {
            switch (info.vanilla.Major)
            {
                case <= 14:
                    minVer = _MaxVersion(minVer, new Version(1, 8, 0, 0));
                    maxVer = _MinVersion(maxVer, new Version(1, 10, 999, 999));
                    break;
                case 15:
                    minVer = _MaxVersion(minVer, new Version(1, 8, 0, 0));
                    maxVer = _MinVersion(maxVer, new Version(1, 15, 999, 999));
                    break;
                default:
                {
                    if (McVersionComparer.CompareVersionGe(info.Forge, "34.0.0")
                        && McVersionComparer.CompareVersionGe("36.2.25", info.Forge))
                        maxVer = _MinVersion(maxVer, new Version(1, 8, 0, 321));
                    else if (info.vanilla.Major is >= 18 and < 19 && info.HasOptiFine)
                        maxVer = _MinVersion(maxVer, new Version(1, 18, 999, 999));

                    break;
                }
            }
        }
    }

    private static void _ApplyCleanroomJavaRange(ref Version minVer, McInstance? target = null)
    {
        var info = (target ?? InstanceList.McMcInstanceSelected!).Info;
        if (!info.HasCleanroom)
            return;

        var cleanroomVersionText = info.Cleanroom;
        if (string.IsNullOrEmpty(cleanroomVersionText)
            || !Version.TryParse(cleanroomVersionText.Split('-')[0], out var cleanroomVersion))
        {
            Common.Log(
                $"[Launch] 无法解析 Cleanroom 版本号，跳过 Java 版本限制：{cleanroomVersionText ?? "null"}",
                Common.LogLevel.Debug);
        }
        else if (cleanroomVersion < new Version(0, 5, 0, 0))
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] Cleanroom 版本低于 0.5，要求至少 Java 21");
            minVer = _MaxVersion(minVer, new Version(21, 0, 0, 0));
        }
        else
        {
            if (Common.modeDebug)
                Common.Log("[Launch] [Debug] Cleanroom 版本高于 0.5，要求至少 Java 25");
            minVer = _MaxVersion(minVer, new Version(25, 0, 0, 0));
        }
    }

    private static void _ApplyFabricJavaRange(ref Version minVer, McInstance? target = null)
    {
        var info = (target ?? InstanceList.McMcInstanceSelected!).Info;
        if (!info.HasFabric || !info.Valid)
            return;

        minVer = info.vanilla.Major switch
        {
            >= 15 and <= 16 => _MaxVersion(minVer, new Version(1, 8, 0, 0)),
            >= 18 => _MaxVersion(minVer, new Version(1, 17, 0, 0)),
            _ => minVer
        };
    }

    private static void _ApplyLiteLoaderJavaRange(ref Version maxVer, McInstance? target = null)
    {
        var info = (target ?? InstanceList.McMcInstanceSelected!).Info;
        if (!info.HasLiteLoader || !info.Valid)
            return;

        if (Common.modeDebug)
            Common.Log("[Launch] [Debug] LiteLoader 要求最高 Java 8");
        maxVer = _MinVersion(maxVer, new Version(8, 999, 999, 999));
    }

    private static void _ApplyLabyModJavaRange(ref Version minVer, ref Version maxVer, McInstance? target = null)
    {
        if (!(target ?? InstanceList.McMcInstanceSelected!).Info.HasLabyMod)
            return;

        if (Common.modeDebug)
            Common.Log("[Launch] [Debug] LabyMod 要求至少 Java 21");
        minVer = _MaxVersion(minVer, new Version(21, 0, 0, 0));
        maxVer = new Version(999, 999, 999, 999);
    }

    private static IReadOnlySet<int>? _ApplyJsonJavaRequirements(
        ref Version minVer,
        ref Version maxVer, McInstance? target = null)
    {
        var instance = target ?? InstanceList.McMcInstanceSelected!;
        _ApplyJsonJavaVersionRequirement(instance, ref minVer, ref maxVer);
        return _ApplyCompatibleJavaMajors(instance, ref minVer, ref maxVer);
    }

    private static void _ApplyJsonJavaVersionRequirement(
        McInstance instance,
        ref Version minVer,
        ref Version maxVer)
    {
        var javaVersionNode = instance.JsonObject["javaVersion"];
        if (javaVersionNode is null)
            return;

        var majorVersion = Common.Val(javaVersionNode["majorVersion"]?.ToString() ?? "");
        if (Common.modeDebug)
            Common.Log($"[Launch] [Debug] JSON 中参数要求至少 Java {majorVersion}");

        var roundedMajor = (int)Math.Round(majorVersion);
        var requiredJavaVersion =
            majorVersion <= 8d
                ? new Version(1, roundedMajor, 0, 0)
                : new Version(roundedMajor, 0, 0, 0);
        minVer = _MaxVersion(minVer, requiredJavaVersion);

        if (maxVer < minVer)
            maxVer = new Version(999, 999, 999, 999);
    }

    private static IReadOnlySet<int>? _ApplyCompatibleJavaMajors(
        McInstance instance,
        ref Version minVer,
        ref Version maxVer)
    {
        if (instance.JsonObject["compatibleJavaMajors"] is not JsonArray compatibleMajors
            || compatibleMajors.Count == 0)
            return null;

        var majors = compatibleMajors
            .Select(major => (int)Math.Round(Common.Val(major?.ToString() ?? "")))
            .Where(major => major > 0)
            .Distinct()
            .OrderBy(major => major)
            .ToList();
        if (majors.Count == 0)
            return null;

        var compatibleMinVer = ToCompatibleVersion(majors.Min(), false);
        var compatibleMaxVer = ToCompatibleVersion(majors.Max(), true);
        if (Common.modeDebug)
            Common.Log(
                $"[Launch] [Debug] JSON 中要求的兼容 Java 主版本：{string.Join(", ", majors)}");

        var intersectMin = _MaxVersion(compatibleMinVer, minVer);
        var intersectMax = _MinVersion(compatibleMaxVer, maxVer);
        if (intersectMax < intersectMin)
        {
            intersectMin = compatibleMinVer;
            intersectMax = compatibleMaxVer;
        }

        minVer = intersectMin;
        maxVer = intersectMax;
        return majors.ToHashSet();

        static Version ToCompatibleVersion(int major, bool isMax)
        {
            return major <= 8
                ? new Version(1, major, isMax ? 999 : 0, isMax ? 999 : 0)
                : new Version(major, isMax ? 999 : 0, isMax ? 999 : 0, isMax ? 999 : 0);
        }
    }

    private static void _SelectOrDownloadJava(
        Loading.LoaderTask<int, int> task,
        Version minVer,
        Version maxVer,
        IReadOnlySet<int>? compatibleJavaMajorsSet,
        string? recommendedComponent)
    {
        _LogJavaRequirement(minVer, maxVer, compatibleJavaMajorsSet);
        mcLaunchJavaSelected = _TrySelectJava(minVer, maxVer, compatibleJavaMajorsSet);
        if (task.IsAborted)
            return;
        if (mcLaunchJavaSelected is not null)
        {
            _LogSelectedJava();
            return;
        }

        if (task.IsAborted)
            return;
        if (currentLaunchOptions?.Offline == true)
            throw new InvalidOperationException("离线启动缺少兼容的本地 Java；请在来源电脑补齐后重新导出离线包。");
        McLaunchLog("无合适的 Java，需要确认是否自动下载");

        var javaCode = _GetJavaDownloadCode(minVer, maxVer, compatibleJavaMajorsSet);
        if (!JavaRuntime.JavaDownloadConfirm($"Java {javaCode}"))
            throw new Exception("$$");

        _DownloadJava(task, recommendedComponent ?? javaCode);

        mcLaunchJavaSelected = _TrySelectJava(minVer, maxVer, compatibleJavaMajorsSet);
        if (task.IsAborted)
            return;
        if (mcLaunchJavaSelected is not null)
        {
            _LogSelectedJava();
            return;
        }

        HintService.Hint(Lang.Text("Minecraft.Launch.Error.NoJava"), HintType.Error);
        throw new Exception("$$");
    }

    private static void _LogJavaRequirement(
        Version minVer,
        Version maxVer,
        IReadOnlySet<int>? compatibleJavaMajorsSet)
    {
        McLaunchLog(
            $"Java 版本需求：最低 {minVer}，最高 {maxVer}{(compatibleJavaMajorsSet is null
                ? ""
                : $"，兼容主版本：{string.Join(", ",
                    compatibleJavaMajorsSet.OrderBy(major => major))}")}");
    }

    private static JavaEntry? _TrySelectJava(
        Version minVer,
        Version maxVer,
        IReadOnlySet<int>? compatibleJavaMajorsSet)
    {
        var instance = InstanceList.McMcInstanceSelected;
        if (instance is not null)
        {
            var markerPath = Path.Combine(instance.PathInstance, "pcl-offline.json");
            if (File.Exists(markerPath))
            {
                var marker = JsonNode.Parse(File.ReadAllText(markerPath))?.AsObject()
                    ?? throw new InvalidDataException("Offline version metadata is invalid.");
                var relative = marker["java"]?.GetValue<string>()
                    ?? throw new InvalidDataException("Offline version metadata has no Java path.");
                if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "." or ".."))
                    throw new InvalidDataException("Offline Java path is unsafe.");
                var root = Path.GetFullPath(instance.PathInstance);
                var javaPath = Path.GetFullPath(Path.Combine(root, relative));
                if (!javaPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Offline Java path escapes its version directory.");
                var bundledJava = JavaRuntime.Javas.AddOrGet(javaPath);
                if (bundledJava is null || !bundledJava.IsEnabled
                    || bundledJava.Installation.Version < minVer || bundledJava.Installation.Version > maxVer
                    || compatibleJavaMajorsSet is not null && !compatibleJavaMajorsSet.Contains(bundledJava.Installation.MajorVersion))
                    throw new InvalidDataException("Bundled Java is missing or incompatible with this Minecraft version.");
                return bundledJava;
            }
        }
        return JavaRuntime.JavaSelect(
            "$$",
            minVer,
            maxVer,
            instance,
            false,
            compatibleJavaMajorsSet);
    }

    private static void _LogSelectedJava()
    {
        McLaunchLog($"选择的 Java：{mcLaunchJavaSelected}");
    }

    private static string _GetJavaDownloadCode(
        Version minVer,
        Version maxVer,
        IReadOnlySet<int>? compatibleJavaMajorsSet)
    {
        var compatibleDownloadMajor = compatibleJavaMajorsSet
            ?.Where(major =>
            {
                var majorMinVer =
                    major <= 8
                        ? new Version(1, major, 0, 0)
                        : new Version(major, 0, 0, 0);
                var majorMaxVer =
                    major <= 8
                        ? new Version(1, major, 999, 999)
                        : new Version(major, 999, 999, 999);
                return majorMaxVer >= minVer && majorMinVer <= maxVer;
            })
            .OrderBy(major => major)
            .FirstOrDefault();

        if (compatibleDownloadMajor > 0)
            return compatibleDownloadMajor.Value.ToString();

        if (minVer >= new Version(1, 9))
            return minVer.Major.ToString();

        if (maxVer < new Version(1, 8))
        {
            Launcher.MyMsgBox(
                InstanceList.McMcInstanceSelected!.Info.HasForge
                    ? Lang.Text("Minecraft.Launch.Java.NeedLegacyJavaFixerOrJava7")
                    : Lang.Text("Minecraft.Launch.Java.NeedJava7"),
                Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
            throw new Exception("$$");
        }

        if (minVer > new Version(1, 8, 0, 140)
            && maxVer < new Version(1, 8, 0, 321))
        {
            Launcher.MyMsgBox(
                Lang.Text("Minecraft.Launch.Java.NeedJava8U141ToU320"),
                Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
            throw new Exception("$$");
        }

        if (minVer > new Version(1, 8, 0, 140))
        {
            Launcher.MyMsgBox(
                Lang.Text("Minecraft.Launch.Java.NeedJava8U141OrLater"),
                Lang.Text("Minecraft.Launch.Java.NotFound.Title"));
            throw new Exception("$$");
        }

        return 8.ToString();
    }

    private static void _DownloadJava(Loading.LoaderTask<int, int> task, string javaComponent)
    {
        var javaLoader = JavaRuntime.GetJavaDownloadLoader();
        try
        {
            javaLoader.Start(javaComponent, true);
            while (javaLoader.State == Common.LoadState.Loading && !task.IsAborted)
            {
                task.Progress = javaLoader.Progress;
                Thread.Sleep(10);
            }
        }
        finally
        {
            javaLoader.Abort();
        }
    }

    private static Version _MaxVersion(Version left, Version right)
    {
        return left > right ? left : right;
    }

    private static Version _MinVersion(Version left, Version right)
    {
        return left < right ? left : right;
    }
}