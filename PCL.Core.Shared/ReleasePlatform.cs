using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCL.Core.Shared;

public enum ReleasePlatformTarget { Both, Windows, Linux }

public static class ReleaseCatalog
{
    public const string ManifestName = "PCL3_update.json";
    private static readonly Version LastHistoricalRelease = new(0, 3, 8);
    private static readonly Regex EntryRegex = new("<entry\\b[\\s\\S]*?</entry>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TagRegex = new("releases/tag/(?<tag>[^\\\"'< >\\s?#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<string?> FindAsync(Func<string, CancellationToken, Task<string>> fetch,
        string repository, ReleasePlatformTarget platform, Version? current = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> tags;
        try { tags = await ListOfficialReleasesAsync(fetch, repository, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            // Preserve anonymous public-page checks when the GitHub REST endpoint is rate-limited.
            tags = await ListPublicReleasesAsync(fetch, repository, cancellationToken).ConfigureAwait(false);
        }
        var candidates = tags.Distinct(StringComparer.Ordinal)
            .Select(tag => (Tag: tag, Version: TryParseTag(tag, out var version) ? version : null))
            .Where(entry => entry.Version is not null && (current is null || entry.Version > current))
            .OrderByDescending(entry => entry.Version);
        foreach (var (tag, version) in candidates)
        {
            var manifestUrl = $"{repository}/releases/download/{Uri.EscapeDataString(tag)}/{ManifestName}";
            ReleasePlatformTarget scope;
            try
            {
                scope = ParseManifest(await fetch(manifestUrl, cancellationToken).ConfigureAwait(false), tag);
            }
            catch (HttpRequestException ex) when (version <= LastHistoricalRelease && ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                scope = ReleasePlatformTarget.Both;
            }
            if (scope == ReleasePlatformTarget.Both || scope == platform) return tag;
        }
        return null;
    }

    private static async Task<IReadOnlyList<string>> ListOfficialReleasesAsync(Func<string, CancellationToken, Task<string>> fetch,
        string repository, CancellationToken token)
    {
        var repo = new Uri(repository);
        var endpoint = "https://api.github.com/repos" + repo.AbsolutePath.TrimEnd('/') + "/releases";
        var tags = new List<string>();
        for (var page = 1; page <= 100; page++)
        {
            using var result = JsonDocument.Parse(await fetch($"{endpoint}?per_page=100&page={page}", token).ConfigureAwait(false));
            if (result.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub returned an invalid release list.");
            foreach (var release in result.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object || !release.TryGetProperty("draft", out var draft) ||
                    !release.TryGetProperty("prerelease", out var prerelease) || !release.TryGetProperty("tag_name", out var tag) ||
                    draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || tag.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("GitHub returned malformed release metadata.");
                if (!draft.GetBoolean() && !prerelease.GetBoolean()) tags.Add(tag.GetString()!);
            }
            if (result.RootElement.GetArrayLength() < 100) return tags;
        }
        throw new InvalidDataException("The maximum GitHub release listing size was exceeded.");
    }

    private static async Task<IReadOnlyList<string>> ListPublicReleasesAsync(Func<string, CancellationToken, Task<string>> fetch,
        string repository, CancellationToken token)
    {
        string feed;
        Exception? atomError = null;
        try { feed = await fetch(repository + "/releases.atom", token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { atomError = ex; feed = ""; }
        var tags = EntryRegex.Matches(feed).Select(match => TagRegex.Match(match.Value)).Where(match => match.Success)
            .Select(match => Uri.UnescapeDataString(match.Groups["tag"].Value)).ToList();
        if (tags.Count == 0)
        {
            try
            {
                var latest = await fetch(repository + "/releases/latest", token).ConfigureAwait(false);
                var match = TagRegex.Match(latest);
                if (match.Success) tags.Add(Uri.UnescapeDataString(match.Groups["tag"].Value));
                else throw new InvalidDataException("GitHub did not return a release tag.");
            }
            catch when (atomError is not null && !token.IsCancellationRequested) { throw atomError; }
        }
        return tags;
    }

    public static ReleasePlatformTarget ParseManifest(string json, string tag)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !ReadString("version", out var versionElement) ||
            !ReadString("releaseTag", out var releaseTagElement) || !TryParseTag(tag, out var expected) ||
            versionElement != expected.ToString() || releaseTagElement != tag)
            throw new InvalidDataException($"Update manifest does not match release {tag}.");
        if (!root.TryGetProperty("platform", out _) && expected <= LastHistoricalRelease)
            return ReleasePlatformTarget.Both;
        ReadString("platform", out var platform);
        return platform switch
        {
            "both" => ReleasePlatformTarget.Both,
            "windows" => ReleasePlatformTarget.Windows,
            "linux" => ReleasePlatformTarget.Linux,
            _ => throw new InvalidDataException($"Invalid update platform for {tag}.")
        };

        bool ReadString(string key, out string? value)
        {
            value = root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() : null;
            return value is not null;
        }
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        var text = tag.StartsWith('v') ? tag[1..] : "";
        if (!Regex.IsMatch(text, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant) || !Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, parsed.Build);
        return true;
    }
}

public static class ReleasePlatform
{
    public static ReleasePlatformTarget Current => OperatingSystem.IsWindows() ? ReleasePlatformTarget.Windows : ReleasePlatformTarget.Linux;
}
