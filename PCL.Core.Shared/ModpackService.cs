using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PCL.Core.Shared.Minecraft;

public sealed record ModpackFile(string Path, string? Url, string? Sha1, string? CurseForgeProject = null, string? CurseForgeFile = null);
public sealed record ModpackPlan(string Name, string MinecraftVersion, string? Loader, string? LoaderVersion,
    IReadOnlyList<ModpackFile> Files, string OverridesPrefix, string ArchiveBaseFolder = "", IReadOnlyList<string>? AdditionalOverridePrefixes = null);

public sealed class ModpackService(HttpClient http, string? curseForgeApiKey = null)
{
    public ModpackPlan ReadPlan(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var modrinth = FindEntry(zip, "modrinth.index.json", out var baseFolder);
        if (modrinth is not null)
        {
            using var stream = modrinth.Open();
            var index = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("Modrinth index is invalid.");
            var dependencies = index["dependencies"]?.AsObject() ?? throw new InvalidDataException("Modrinth dependencies are missing.");
            var version = dependencies["minecraft"]?.GetValue<string>() ?? throw new InvalidDataException("Minecraft version is missing.");
            var loader = new[] { "fabric-loader", "quilt-loader", "forge", "neoforge" }.FirstOrDefault(dependencies.ContainsKey);
            var files = (index["files"]?.AsArray() ?? []).Where(file => file?["env"]?["client"]?.GetValue<string>() != "unsupported")
                .Select(file => new ModpackFile(
                    file?["path"]?.GetValue<string>() ?? throw new InvalidDataException("Modrinth file path is missing."),
                    file["downloads"]?.AsArray().FirstOrDefault()?.GetValue<string>(),
                    file["hashes"]?["sha1"]?.GetValue<string>())).ToArray();
            return new ModpackPlan(index["name"]?.GetValue<string>() ?? "Modrinth pack", version,
                loader switch { "fabric-loader" => "fabric", "quilt-loader" => "quilt", _ => loader },
                loader is null ? null : dependencies[loader]?.GetValue<string>(), files, baseFolder + "overrides/", baseFolder);
        }

        var curseForge = FindEntry(zip, "manifest.json", out baseFolder);
        if (curseForge is not null)
        {
            using var stream = curseForge.Open();
            var manifest = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("Manifest is invalid.");
            if (manifest["minecraft"] is JsonObject minecraft)
            {
                var version = minecraft["version"]?.GetValue<string>() ?? throw new InvalidDataException("Minecraft version is missing.");
                var rawLoader = minecraft["modLoaders"]?.AsArray().FirstOrDefault(item => item?["primary"]?.GetValue<bool>() == true)?["id"]?.GetValue<string>();
                var split = rawLoader?.Split('-', 2);
                var loader = split is { Length: 2 } ? split[0].ToLowerInvariant() : null;
                var loaderVersion = split is { Length: 2 } ? split[1] : null;
                var files = (manifest["files"]?.AsArray() ?? []).Where(file => file?["required"]?.GetValue<bool>() != false)
                    .Select(file => new ModpackFile("mods/", null, null,
                        file?["projectID"]?.ToString() ?? throw new InvalidDataException("CurseForge file is missing projectID."),
                        file["fileID"]?.ToString() ?? throw new InvalidDataException("CurseForge file is missing fileID."))).ToArray();
                return new ModpackPlan(manifest["name"]?.GetValue<string>() ?? "CurseForge pack", version, loader, loaderVersion,
                    files, baseFolder + (manifest["overrides"]?.GetValue<string>() ?? "overrides").TrimEnd('/') + "/", baseFolder);
            }
        }

        var mcbbs = FindEntry(zip, "mcbbs.packmeta", out baseFolder)
            ?? FindEntry(zip, "manifest.json", out baseFolder);
        if (mcbbs is not null)
        {
            using var stream = mcbbs.Open();
            var manifest = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("MCBBS manifest is invalid.");
            var addons = manifest["addons"]?.AsArray() ?? throw new InvalidDataException("MCBBS pack has no addons list.");
            var map = addons.ToDictionary(item => item?["id"]?.GetValue<string>() ?? "", item => item?["version"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);
            if (!map.TryGetValue("game", out var gameVersion) || string.IsNullOrWhiteSpace(gameVersion))
                throw new InvalidDataException("MCBBS pack does not specify a Minecraft version.");
            var loader = new[] { "forge", "neoforge", "fabric", "quilt" }.FirstOrDefault(map.ContainsKey);
            return new ModpackPlan(manifest["name"]?.GetValue<string>() ?? "MCBBS pack", gameVersion, loader,
                loader is null ? null : map[loader], [], baseFolder + "overrides/", baseFolder);
        }

        var hmcl = FindEntry(zip, "modpack.json", out baseFolder);
        if (hmcl is not null)
        {
            using var stream = hmcl.Open();
            var manifest = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("HMCL manifest is invalid.");
            var version = manifest["gameVersion"]?.GetValue<string>() ?? throw new InvalidDataException("HMCL manifest lacks gameVersion.");
            return new ModpackPlan(manifest["name"]?.GetValue<string>() ?? "HMCL pack", version, null, null, [], baseFolder + "minecraft/", baseFolder);
        }

        var mmc = FindEntry(zip, "mmc-pack.json", out baseFolder);
        if (mmc is not null)
        {
            using var stream = mmc.Open();
            var manifest = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException("MultiMC manifest is invalid.");
            string? gameVersion = null;
            string? loader = null;
            string? loaderVersion = null;
            foreach (var component in manifest["components"]?.AsArray() ?? [])
            {
                var id = component?["uid"]?.GetValue<string>();
                var version = component?["version"]?.GetValue<string>();
                if (id == "net.minecraft") gameVersion = version;
                else if (id == "net.minecraftforge") { loader = "forge"; loaderVersion = version; }
                else if (id is "net.neoforged" or "net.neoforged.neoforge") { loader = "neoforge"; loaderVersion = version; }
                else if (id == "net.fabricmc.fabric-loader") { loader = "fabric"; loaderVersion = version; }
                else if (id == "org.quiltmc.quilt-loader") { loader = "quilt"; loaderVersion = version; }
            }
            if (string.IsNullOrWhiteSpace(gameVersion)) throw new InvalidDataException("MultiMC manifest lacks net.minecraft component.");
            return new ModpackPlan("MultiMC pack", gameVersion, loader, loaderVersion, [], baseFolder + ".minecraft/", baseFolder,
                [baseFolder + "libraries/"]);
        }
        throw new NotSupportedException("Unsupported modpack format; supported manifests are Modrinth, CurseForge, MCBBS, HMCL, and MultiMC.");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string fileName, out string baseFolder)
    {
        var entry = zip.GetEntry(fileName) ?? zip.Entries.FirstOrDefault(item => item.FullName.EndsWith("/" + fileName, StringComparison.Ordinal));
        baseFolder = entry is null ? "" : entry.FullName[..^fileName.Length];
        return entry;
    }

    public async Task InstallFilesAsync(string archivePath, ModpackPlan plan, string instanceDirectory, CancellationToken cancellationToken = default)
    {
        foreach (var entry in plan.Files)
        {
            var file = entry;
            if (entry.CurseForgeProject is not null && entry.CurseForgeFile is not null)
                file = await ResolveCurseForgeFileAsync(entry, cancellationToken).ConfigureAwait(false);
            if (file.Url is null || file.Sha1 is null || !IsSha1(file.Sha1))
                throw new InvalidDataException($"A verified download is unavailable for {file.Path}.");
            var destination = SafePath(instanceDirectory, file.Path);
            if (destination.EndsWith("pcl-instance.json", StringComparison.Ordinal) || destination.EndsWith("pcl-mods.json", StringComparison.Ordinal))
                throw new InvalidDataException("Modpack files may not replace PCL instance metadata.");
            if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Modpack downloads must use HTTPS.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".part";
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 512L * 1024 * 1024)
                    throw new InvalidDataException("Modpack file exceeds the allowed download size.");
                await using (var output = File.Create(temporary))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    await CopyLimitedAsync(input, output, 512L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await using (var input = File.OpenRead(temporary))
                {
                    var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                    if (!actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Modpack hash verification failed for {file.Path}.");
                }
                File.Move(temporary, destination, overwrite: true);
                var modsDirectory = Path.GetFullPath(Path.Combine(instanceDirectory, "mods"));
                if (Path.GetDirectoryName(destination) == modsDirectory && file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(destination);
                    var source = file.CurseForgeProject is null ? ModSource.Modrinth : ModSource.CurseForge;
                    var projectId = file.CurseForgeProject ?? "";
                    var releaseId = file.CurseForgeFile ?? "";
                    if (source == ModSource.Modrinth)
                    {
                        using var lookup = await http.GetAsync($"https://api.modrinth.com/v2/version_file/{file.Sha1}?algorithm=sha1", cancellationToken).ConfigureAwait(false);
                        if (lookup.IsSuccessStatusCode)
                        {
                            var identity = await lookup.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
                            projectId = identity?["project_id"]?.GetValue<string>() ?? "";
                            releaseId = identity?["id"]?.GetValue<string>() ?? "";
                        }
                    }
                    await InstalledModService.TrackAsync(instanceDirectory, new InstalledMod(fileName, source,
                        projectId, Path.GetFileNameWithoutExtension(fileName), releaseId,
                        releaseId, false, file.Url, file.Sha1), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        using var zip = ZipFile.OpenRead(archivePath);
        var prefixes = new[] { plan.OverridesPrefix, plan.ArchiveBaseFolder + "client-overrides/" }
            .Concat(plan.AdditionalOverridePrefixes ?? [])
            .Where(prefix => !string.IsNullOrEmpty(prefix)).Distinct(StringComparer.Ordinal).ToArray();
        if (prefixes.Any(prefix => prefix.Contains("..", StringComparison.Ordinal) || prefix.StartsWith('/') || prefix.StartsWith('\\')))
            throw new InvalidDataException("Unsafe modpack overrides folder.");
        long totalOverrideBytes = 0;
        foreach (var entry in zip.Entries)
        {
            var prefix = prefixes.FirstOrDefault(value => entry.FullName.StartsWith(value, StringComparison.Ordinal));
            if (prefix is null || entry.FullName.EndsWith('/')) continue;
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Modpack symbolic links are not allowed.");
            if (entry.Length > 512L * 1024 * 1024 || (totalOverrideBytes += entry.Length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Modpack overrides exceed the allowed extraction size.");
            var relative = entry.FullName[prefix.Length..];
            var destination = SafePath(instanceDirectory, relative);
            if (destination.EndsWith("pcl-instance.json", StringComparison.Ordinal) || destination.EndsWith("pcl-mods.json", StringComparison.Ordinal))
                throw new InvalidDataException("Modpack overrides may not replace PCL instance metadata.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) throw new IOException($"Modpack override would overwrite an existing file: {entry.FullName}");
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExportModrinthAsync(ManagedMinecraftInstance instance, string destination, CancellationToken cancellationToken = default)
    {
        var mods = InstalledModService.List(instance.DirectoryPath);
        if (mods.Any(mod => !mod.Disabled && (mod.DownloadUrl is null || mod.Sha1 is null || !IsSha1(mod.Sha1))))
            throw new InvalidOperationException("Every enabled mod needs a tracked download URL and SHA-1 to export without redistributing third-party JARs.");
        var index = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = "1.0.0",
            ["name"] = instance.Name,
            ["summary"] = "Exported by PCL Linux CLI",
            ["files"] = new JsonArray(mods.Where(mod => !mod.Disabled).Select(mod => (JsonNode?)new JsonObject
            {
                ["path"] = "mods/" + mod.FileName,
                ["hashes"] = new JsonObject { ["sha1"] = mod.Sha1 },
                ["downloads"] = new JsonArray(mod.DownloadUrl),
                ["fileSize"] = new FileInfo(Path.Combine(instance.DirectoryPath, "mods", mod.FileName)).Length
            }).ToArray()),
            ["dependencies"] = new JsonObject { ["minecraft"] = instance.GameVersion }
        };
        if (instance.Loader is not null)
            ((JsonObject)index["dependencies"]!)[instance.Loader is "fabric" or "quilt" ? instance.Loader + "-loader" : instance.Loader] = instance.LoaderVersion;
        var temporary = destination + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("modrinth.index.json");
                await using (var stream = entry.Open())
                await using (var writer = new StreamWriter(stream))
                    await writer.WriteAsync(index.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await AddExportOverridesAsync(zip, instance.DirectoryPath, temporary, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task ExportCurseForgeAsync(ManagedMinecraftInstance instance, string destination, CancellationToken cancellationToken = default)
    {
        var mods = InstalledModService.List(instance.DirectoryPath).Where(mod => !mod.Disabled).ToArray();
        if (mods.Any(mod => mod.Source != ModSource.CurseForge || !long.TryParse(mod.ProjectId, out _)
            || !long.TryParse(mod.ReleaseId, out _)))
            throw new InvalidOperationException("CurseForge export requires every enabled mod to have tracked CurseForge project and file IDs.");
        var manifest = new JsonObject
        {
            ["minecraft"] = new JsonObject
            {
                ["version"] = instance.GameVersion,
                ["modLoaders"] = instance.Loader is null ? new JsonArray() : new JsonArray(new JsonObject
                {
                    ["id"] = instance.Loader + "-" + instance.LoaderVersion,
                    ["primary"] = true
                })
            },
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = instance.Name,
            ["version"] = "1.0.0",
            ["author"] = "PCL Linux CLI",
            ["files"] = new JsonArray(mods.Select(mod => (JsonNode?)new JsonObject
            {
                ["projectID"] = long.Parse(mod.ProjectId),
                ["fileID"] = long.Parse(mod.ReleaseId),
                ["required"] = true
            }).ToArray()),
            ["overrides"] = "overrides"
        };
        var temporary = destination + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("manifest.json");
                await using (var stream = entry.Open())
                await using (var writer = new StreamWriter(stream))
                    await writer.WriteAsync(manifest.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await AddExportOverridesAsync(zip, instance.DirectoryPath, temporary, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task AddExportOverridesAsync(ZipArchive zip, string instanceDirectory, string temporaryArchive, CancellationToken cancellationToken)
    {
        // Do not recursively archive an entire game directory: it may contain
        // account data, saves, logs, or third-party mod binaries.
        string[] directories = ["config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks"];
        var files = new List<string>();
        foreach (var directory in directories)
        {
            var root = Path.Combine(instanceDirectory, directory);
            if (!Directory.Exists(root)) continue;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cannot export a symbolic-link override directory.");
            files.AddRange(Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            }));
        }
        var options = Path.Combine(instanceDirectory, "options.txt");
        if (File.Exists(options) && (File.GetAttributes(options) & FileAttributes.ReparsePoint) == 0) files.Add(options);
        long total = 0;
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFullPath(file) == Path.GetFullPath(temporaryArchive)) continue;
            if (file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot bundle third-party JARs in exported overrides.");
            var length = new FileInfo(file).Length;
            if (length > 512L * 1024 * 1024 || (total += length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Export overrides exceed the allowed archive size.");
            var relative = Path.GetRelativePath(instanceDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            _ = SafePath(instanceDirectory, relative);
            var entry = zip.CreateEntry("overrides/" + relative, CompressionLevel.Optimal);
            await using var input = File.OpenRead(file);
            await using var output = entry.Open();
            await CopyLimitedAsync(input, output, 512L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ModpackFile> ResolveCurseForgeFileAsync(ModpackFile file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(curseForgeApiKey))
            throw new InvalidOperationException("CurseForge modpack installation requires PCL_CURSEFORGE_API_KEY.");
        var url = $"https://api.curseforge.com/v1/mods/{Uri.EscapeDataString(file.CurseForgeProject!)}/files/{Uri.EscapeDataString(file.CurseForgeFile!)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", curseForgeApiKey);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false))?["data"]
            ?? throw new InvalidDataException("CurseForge file response is empty.");
        var name = item["fileName"]?.GetValue<string>() ?? throw new InvalidDataException("CurseForge file lacks a name.");
        if (Path.GetFileName(name) != name || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CurseForge file name is unsafe.");
        var download = item["downloadUrl"]?.GetValue<string>();
        if (download is null) throw new InvalidOperationException("CurseForge did not provide a downloadable URL for this pack file.");
        return file with { Path = "mods/" + name, Url = download,
            Sha1 = item["hashes"]?.AsArray().FirstOrDefault(hash => hash?["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>() };
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("Modpack file exceeds the allowed download size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSha1(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(segment => segment is "." or ".." or ""))
            throw new InvalidDataException("Modpack contains an unsafe file path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Modpack file escapes its instance directory.");
        return destination;
    }
}
