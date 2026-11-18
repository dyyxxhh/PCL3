using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Java;
using PCL.Core.Shared.Minecraft;

namespace PCL;

internal static class WindowsOfflineBundleExporter
{
    public static async Task<OfflineBundleManifest> ExportAsync(McInstance instance, string destination,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? javaExecutable = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        instance.Load();
        if (instance.state == McInstanceState.Error) throw new InvalidOperationException("请先选择已安装且有效的游戏版本。");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到当前启动器。");
        if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location)
            || AppContext.GetData("APP_CONTEXT_DEPS_FILES") is string deps && !string.IsNullOrEmpty(deps))
            throw new InvalidOperationException("请使用自带 .NET 的单文件 PCL₃ 发布版导出完整离线包。");
        if (instance.Info.HasOptiFine || instance.Info.HasLiteLoader || instance.Info.HasLegacyFabric
            || instance.Info.HasCleanroom || instance.Info.HasLabyMod)
            throw new NotSupportedException("完整离线包当前支持原版、Fabric、Quilt、Forge 和 NeoForge。");

        var java = GameLaunch.SelectOfflineExportJava(instance, javaExecutable);
        var javaHome = JavaDiscovery.ResolveHome(java.Installation.JavaExePath);
        var launchVersion = instance.Name;
        var gameRoot = Path.GetFullPath(GameFolders.mcFolderSelected ?? throw new InvalidOperationException("尚未选择游戏目录。"));
        var merged = (JsonObject)instance.JsonObject.DeepClone();
        merged.Remove("inheritsFrom");
        merged.Remove("patches");
        merged["id"] = launchVersion;
        merged["jar"] = launchVersion;
        var files = new Dictionary<string, OfflineBundleSource>(StringComparer.OrdinalIgnoreCase);
        var normalizedLibraries = new JsonArray();
        var libraries = Libraries.McLibListGet(instance, includeInstanceJar: true);
        if (libraries.Count == 0) throw new InvalidDataException("找不到游戏客户端 JAR。");
        var client = libraries[^1];
        for (var i = 0; i < libraries.Count - 1; i++)
        {
            var library = libraries[i];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(i, libraries.Count, library.LocalPath) { Stage = "检查 Windows 支持库" });
            await ValidateFileAsync(library.LocalPath, library.Sha1, cancellationToken);
            var relative = Path.GetRelativePath(Path.Combine(gameRoot, "libraries"), library.LocalPath).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                relative = $"pcl-offline/local/{i}.jar";
            Add(library.LocalPath, "libraries/" + relative);
            var artifact = new JsonObject { ["path"] = relative, ["sha1"] = await Sha1Async(library.LocalPath, cancellationToken) };
            var normalized = new JsonObject { ["name"] = library.OriginalName ?? $"pcl.offline:dependency-{i}:1" };
            if (library.IsNatives)
            {
                normalized["natives"] = new JsonObject { ["windows"] = "natives-offline" };
                normalized["downloads"] = new JsonObject { ["classifiers"] = new JsonObject { ["natives-offline"] = artifact } };
            }
            else normalized["downloads"] = new JsonObject { ["artifact"] = artifact };
            normalizedLibraries.Add(normalized);
        }
        merged["libraries"] = normalizedLibraries;
        await ValidateFileAsync(client.LocalPath, client.Sha1, cancellationToken);
        Add(client.LocalPath, $"versions/{launchVersion}/{launchVersion}.jar");

        var indexName = GameAssets.McAssetsGetIndexName(instance);
        var indexPath = Path.Combine(gameRoot, "assets", "indexes", indexName + ".json");
        await ValidateFileAsync(indexPath, merged["assetIndex"]?["sha1"]?.GetValue<string>(), cancellationToken);
        Add(indexPath, $"assets/indexes/{indexName}.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("资源索引无效。");
        foreach (var item in index["objects"]?.AsObject() ?? throw new InvalidDataException("资源索引缺少 objects。"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = item.Value?["hash"]?.GetValue<string>();
            if (hash is null || hash.Length != 40 || !hash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("资源索引哈希无效。");
            var objectPath = Path.Combine(gameRoot, "assets", "objects", hash[..2], hash);
            await ValidateFileAsync(objectPath, hash, cancellationToken);
            Add(objectPath, $"assets/objects/{hash[..2]}/{hash}");
        }
        foreach (var asset in GameAssets.McAssetsListGet(instance))
        {
            await ValidateFileAsync(asset.localPath, asset.hash, cancellationToken);
            var assetRelative = Path.GetRelativePath(gameRoot, asset.localPath).Replace('\\', '/');
            if (assetRelative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase))
                assetRelative = "resources/" + asset.sourcePath.Replace('\\', '/');
            Add(asset.localPath, assetRelative);
        }
        foreach (var folder in new[] { "mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks", "resources" })
        {
            var root = Path.Combine(instance.PathIndie, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in OfflineBundleService.EnumeratePortableData(root))
                Add(file.SourcePath, folder + "/" + file.Path);
        }
        foreach (var file in new[] { "options.txt", "optionsof.txt", "optionsshaders.txt" })
        {
            var path = Path.Combine(instance.PathIndie, file);
            if (File.Exists(path)) Add(path, file);
        }
        var temp = Path.Combine(Path.GetTempPath(), "pcl3-offline-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(temp, merged.ToJsonString(), cancellationToken);
            Add(temp, $"versions/{launchVersion}/{launchVersion}.json");
            var loader = instance.Info.HasFabric ? "fabric" : instance.Info.HasQuilt ? "quilt"
                : instance.Info.HasForge ? "forge" : instance.Info.HasNeoForge ? "neoforge" : null;
            var loaderVersion = loader switch { "fabric" => instance.Info.Fabric, "quilt" => instance.Info.Quilt,
                "forge" => instance.Info.Forge, "neoforge" => instance.Info.NeoForge, _ => null };
            var metadata = new OfflineInstanceMetadata(instance.Name, instance.Info.VanillaName ?? instance.Name, loader, loaderVersion, launchVersion);
            return await OfflineBundleService.ExportWindowsAsync(metadata, java.Installation.MajorVersion, OfflineBundleService.CurrentTargetRuntime,
                javaHome, executable, Path.GetFullPath(destination), files.Values, progress, cancellationToken);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        void Add(string source, string relative)
        {
            var path = "instance/" + relative;
            if (files.TryGetValue(path, out var existing))
            {
                if (!string.Equals(Path.GetFullPath(existing.SourcePath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"离线文件路径冲突：{relative}");
                return;
            }
            files.Add(path, new OfflineBundleSource(path, source));
        }
    }

    private static async Task ValidateFileAsync(string path, string? sha1, CancellationToken token)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("缺少离线启动依赖，请先在来源电脑补齐。", path);
        if (!string.IsNullOrWhiteSpace(sha1) && !sha1.Equals(await Sha1Async(path, token), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"游戏文件 SHA-1 校验失败：{path}");
    }

    private static async Task<string> Sha1Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}
