using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PCL.Core.Shared.Minecraft;

public enum ModSource
{
    Modrinth,
    CurseForge
}

public sealed record ModProject(
    ModSource Source,
    string Id,
    string Slug,
    string Name,
    string Description,
    long Downloads,
    string? IconUrl);

public sealed record ModDependency(ModSource Source, string ProjectId, string? VersionId);

public sealed record ModRelease(
    ModSource Source,
    string ProjectId,
    string VersionId,
    string VersionName,
    string FileName,
    string DownloadUrl,
    string? Sha1,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ModDependency>? RequiredDependencies = null);

public sealed class ModPlatformService(HttpClient http, string? curseForgeApiKey = null)
{
    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string CurseForgeApi = "https://api.curseforge.com/v1";

    public async Task<IReadOnlyList<ModProject>> SearchModsAsync(
        string query,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A search query is required.", nameof(query));
        var projects = new List<ModProject>();
        projects.AddRange(await SearchModrinthAsync(query, gameVersion, loader, cancellationToken).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(curseForgeApiKey))
            projects.AddRange(await SearchCurseForgeAsync(query, gameVersion, loader, cancellationToken).ConfigureAwait(false));
        return projects.OrderByDescending(project => project.Downloads).ToArray();
    }

    public async Task<ModRelease> GetLatestCompatibleReleaseAsync(
        ModProject project,
        string gameVersion,
        string loader,
        CancellationToken cancellationToken = default,
        string? requestedVersion = null)
    {
        return project.Source switch
        {
            ModSource.Modrinth => await GetModrinthReleaseAsync(project.Id, gameVersion, loader, requestedVersion, cancellationToken).ConfigureAwait(false),
            ModSource.CurseForge => await GetCurseForgeReleaseAsync(project.Id, gameVersion, loader, requestedVersion, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(project))
        };
    }

    public async Task<string> InstallReleaseAsync(
        ModRelease release,
        string modsDirectory,
        CancellationToken cancellationToken = default,
        bool overwrite = false)
    {
        Directory.CreateDirectory(modsDirectory);
        var fileName = SanitizeFileName(release.FileName);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected mod release is not a JAR file.");
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Mod downloads must use HTTPS.");
        if (release.Sha1 is null || release.Sha1.Length != 40 || !release.Sha1.All(Uri.IsHexDigit))
            throw new InvalidDataException("The selected mod release does not provide a valid SHA-1 checksum.");
        var destination = Path.GetFullPath(Path.Combine(modsDirectory, fileName));
        var root = Path.GetFullPath(modsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The mod file path escapes the instance mods directory.");
        if (File.Exists(destination) && !overwrite)
            throw new IOException($"A mod file already exists at {destination}; remove it or update the tracked project first.");

        var temporary = destination + ".download";
        try
        {
            using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

            await using (var input = File.OpenRead(temporary))
            {
                var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(release.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-1 verification failed for {fileName}.");
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<IReadOnlyList<ModProject>> SearchModrinthAsync(string query, string? gameVersion, string? loader, CancellationToken cancellationToken)
    {
        List<string[]> facets = [["project_type:mod"]];
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add([$"versions:{gameVersion}"]);
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add([$"categories:{NormalizeLoader(loader)}"]);
        var url = $"{ModrinthApi}/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(facets))}&limit=20";
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (json?["hits"]?.AsArray() ?? [])
            .Where(hit => hit is not null)
            .Select(hit => new ModProject(
                ModSource.Modrinth,
                hit!["project_id"]?.GetValue<string>() ?? "",
                hit["slug"]?.GetValue<string>() ?? "",
                hit["title"]?.GetValue<string>() ?? "",
                hit["description"]?.GetValue<string>() ?? "",
                hit["downloads"]?.GetValue<long>() ?? 0,
                hit["icon_url"]?.GetValue<string>()))
            .Where(project => project.Id.Length > 0)
            .ToArray();
    }

    private async Task<IReadOnlyList<ModProject>> SearchCurseForgeAsync(string query, string? gameVersion, string? loader, CancellationToken cancellationToken)
    {
        using var request = CreateCurseForgeRequest($"{CurseForgeApi}/mods/search?gameId=432&classId=6&pageSize=20&sortField=6&sortOrder=desc&searchFilter={Uri.EscapeDataString(query)}"
            + (string.IsNullOrWhiteSpace(gameVersion) ? "" : $"&gameVersion={Uri.EscapeDataString(gameVersion)}")
            + (string.IsNullOrWhiteSpace(loader) ? "" : $"&modLoaderType={GetCurseForgeLoaderType(loader)}"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return (json?["data"]?.AsArray() ?? [])
            .Where(item => item is not null)
            .Select(item => new ModProject(
                ModSource.CurseForge,
                item!["id"]?.ToString() ?? "",
                item["slug"]?.GetValue<string>() ?? "",
                item["name"]?.GetValue<string>() ?? "",
                item["summary"]?.GetValue<string>() ?? "",
                item["downloadCount"]?.GetValue<long>() ?? 0,
                item["logo"]?["url"]?.GetValue<string>()))
            .Where(project => project.Id.Length > 0)
            .ToArray();
    }

    private async Task<ModRelease> GetModrinthReleaseAsync(string projectId, string gameVersion, string loader, string? requestedVersion, CancellationToken cancellationToken)
    {
        var versions = Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(new[] { gameVersion }));
        var loaders = Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(new[] { NormalizeLoader(loader) }));
        var url = $"{ModrinthApi}/project/{Uri.EscapeDataString(projectId)}/version?game_versions={versions}&loaders={loaders}";
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var releases = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        var release = releases.FirstOrDefault(item => item is not null && (requestedVersion is null
            || requestedVersion.Equals(item["id"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)
            || requestedVersion.Equals(item["version_number"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("No compatible Modrinth release was found for this game version, loader, and requested mod version.");
        var files = release["files"]?.AsArray() ?? [];
        var file = files.FirstOrDefault(item => item?["primary"]?.GetValue<bool>() == true)
            ?? files.FirstOrDefault()
            ?? throw new InvalidDataException("Modrinth release has no downloadable file.");
        return new ModRelease(ModSource.Modrinth, projectId,
            release["id"]?.GetValue<string>() ?? "", release["version_number"]?.GetValue<string>() ?? "",
            file["filename"]?.GetValue<string>() ?? throw new InvalidDataException("Modrinth file has no filename."),
            file["url"]?.GetValue<string>() ?? throw new InvalidDataException("Modrinth file has no download URL."),
            file["hashes"]?["sha1"]?.GetValue<string>(),
            DateTimeOffset.TryParse(release["date_published"]?.GetValue<string>(), out var date) ? date : null,
            (release["dependencies"]?.AsArray() ?? []).Where(dependency => dependency?["dependency_type"]?.GetValue<string>() == "required")
                .Select(dependency => dependency?["project_id"]?.GetValue<string>() is { } projectId
                    ? new ModDependency(ModSource.Modrinth, projectId, dependency?["version_id"]?.GetValue<string>()) : null)
                .OfType<ModDependency>().ToArray());
    }

    private async Task<ModRelease> GetCurseForgeReleaseAsync(string projectId, string gameVersion, string loader, string? requestedVersion, CancellationToken cancellationToken)
    {
        var url = $"{CurseForgeApi}/mods/{Uri.EscapeDataString(projectId)}/files?pageSize=50&gameVersion={Uri.EscapeDataString(gameVersion)}&modLoaderType={GetCurseForgeLoaderType(loader)}&sortField=3&sortOrder=desc";
        using var request = CreateCurseForgeRequest(url);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var release = (json?["data"]?.AsArray() ?? []).FirstOrDefault(item => item is not null && (requestedVersion is null
            || requestedVersion.Equals(item["id"]?.ToString(), StringComparison.OrdinalIgnoreCase)
            || requestedVersion.Equals(item["displayName"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("No compatible CurseForge release was found for this game version, loader, and requested mod version.");
        var downloadUrl = release["downloadUrl"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            var fileId = release["id"]?.ToString() ?? throw new InvalidDataException("CurseForge release has no file ID.");
            using var downloadRequest = CreateCurseForgeRequest($"{CurseForgeApi}/mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(fileId)}/download-url");
            using var downloadResponse = await http.SendAsync(downloadRequest, cancellationToken).ConfigureAwait(false);
            downloadResponse.EnsureSuccessStatusCode();
            var downloadJson = await downloadResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
            downloadUrl = downloadJson?["data"]?.GetValue<string>();
        }
        return new ModRelease(ModSource.CurseForge, projectId,
            release["id"]?.ToString() ?? "", release["displayName"]?.GetValue<string>() ?? "",
            release["fileName"]?.GetValue<string>() ?? throw new InvalidDataException("CurseForge release has no filename."),
            downloadUrl ?? throw new InvalidOperationException("CurseForge did not provide a download URL for this file."),
            release["hashes"]?.AsArray().FirstOrDefault(hash => hash?["algo"]?.GetValue<int>() == 1)?["value"]?.GetValue<string>(),
            DateTimeOffset.TryParse(release["fileDate"]?.GetValue<string>(), out var date) ? date : null,
            (release["dependencies"]?.AsArray() ?? []).Where(dependency => dependency?["relationType"]?.GetValue<int>() == 3)
                .Select(dependency => dependency?["modId"]?.ToString() is { } modId
                    ? new ModDependency(ModSource.CurseForge, modId, null) : null)
                .OfType<ModDependency>().ToArray());
    }

    private HttpRequestMessage CreateCurseForgeRequest(string url)
    {
        if (string.IsNullOrWhiteSpace(curseForgeApiKey))
            throw new InvalidOperationException("CurseForge requires a configured PCL_CURSEFORGE_API_KEY.");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", curseForgeApiKey);
        return request;
    }

    private static string NormalizeLoader(string loader) => loader.ToLowerInvariant() switch
    {
        "fabric" => "fabric",
        "forge" => "forge",
        "neoforge" => "neoforge",
        "quilt" => "quilt",
        _ => throw new ArgumentException($"Unsupported mod loader: {loader}", nameof(loader))
    };

    private static int GetCurseForgeLoaderType(string loader) => NormalizeLoader(loader) switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => throw new ArgumentException($"Unsupported mod loader: {loader}", nameof(loader))
    };

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("The downloaded mod has an invalid file name.");
        return name;
    }
}
