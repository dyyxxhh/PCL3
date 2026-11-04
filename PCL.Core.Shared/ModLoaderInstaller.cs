using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PCL.Core.Shared.Minecraft;

public sealed record ModLoaderInstallResult(string LaunchVersion, string LoaderVersion);

public sealed class ModLoaderInstaller(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<ModLoaderInstallResult> InstallAsync(string gameVersion, string loader,
        string? loaderVersion, string instanceDirectory, CancellationToken cancellationToken = default)
    {
        if (loader is "forge" or "neoforge")
            return await new ForgeLikeLoaderInstaller(_http).InstallAsync(gameVersion, loader,
                loaderVersion, instanceDirectory, cancellationToken).ConfigureAwait(false);
        var baseUri = loader.ToLowerInvariant() switch
        {
            "fabric" => "https://meta.fabricmc.net/v2/versions/loader/",
            "quilt" => "https://meta.quiltmc.org/v3/versions/loader/",
            _ => throw new ArgumentException("Supported loaders are fabric, quilt, forge, and neoforge.", nameof(loader))
        };
        using var response = await _http.GetAsync(baseUri + Uri.EscapeDataString(gameVersion), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var available = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        var selected = available.FirstOrDefault(entry =>
            entry?["loader"]?["version"]?.GetValue<string>() == loaderVersion)
            ?? (loaderVersion is null ? available.FirstOrDefault(entry => entry?["loader"]?["stable"]?.GetValue<bool>() == true)
                ?? available.FirstOrDefault(entry => entry?["loader"]?["version"]?.GetValue<string>()?.Contains("-", StringComparison.Ordinal) != true)
                ?? available.FirstOrDefault() : null)
            ?? throw new InvalidOperationException($"No compatible {loader} loader was found for Minecraft {gameVersion}.");
        loaderVersion = selected["loader"]?["version"]?.GetValue<string>()
            ?? throw new InvalidDataException("Loader metadata lacks a version.");
        var url = baseUri + Uri.EscapeDataString(gameVersion) + "/" + Uri.EscapeDataString(loaderVersion) + "/profile/json";
        using var profileResponse = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        profileResponse.EnsureSuccessStatusCode();
        var profile = await profileResponse.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Loader profile is empty.");
        if (profile["inheritsFrom"]?.GetValue<string>() != gameVersion)
            throw new InvalidDataException("Loader profile inherits from an unexpected Minecraft version.");
        var metadataPath = Path.Combine(instanceDirectory, "versions", gameVersion, gameVersion + ".json");
        var vanilla = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("The base Minecraft version is not installed.");
        var mainClass = profile["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Loader profile lacks a main class.");
        var launchId = profile["id"]?.GetValue<string>()
            ?? throw new InvalidDataException("Loader profile lacks an ID.");
        if (!Regex.IsMatch(launchId, "^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Loader profile contains an invalid version ID.");
        vanilla["id"] = launchId;
        vanilla["jar"] = gameVersion;
        vanilla["mainClass"] = mainClass;

        var vanillaLibraries = vanilla["libraries"]?.AsArray()
            ?? throw new InvalidDataException("Vanilla Minecraft metadata lacks libraries.");
        var loaderLibraries = profile["libraries"]?.AsArray() ?? [];
        var loaderNames = loaderLibraries.Select(library => library?["name"]?.GetValue<string>()).Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
        var mergedLibraries = new JsonArray();
        foreach (var entry in loaderLibraries)
        {
            var coordinate = entry?["name"]?.GetValue<string>()
                ?? throw new InvalidDataException("Loader library lacks a Maven coordinate.");
            var segments = coordinate.Split(':');
            if (segments.Length is < 3 or > 4 || segments.Any(part => !Regex.IsMatch(part, "^[A-Za-z0-9._+-]+$", RegexOptions.CultureInvariant)
                    || part is "." or ".."))
                throw new InvalidDataException("Loader library contains an invalid Maven coordinate.");
            var classifier = segments.Length == 4 ? "-" + segments[3] : "";
            var groupPath = segments[0].Replace('.', '/');
            var relative = $"{groupPath}/{segments[1]}/{segments[2]}/{segments[1]}-{segments[2]}{classifier}.jar";
            var repository = entry["url"]?.GetValue<string>() ?? "https://maven.fabricmc.net/";
            if (!Uri.TryCreate(repository, UriKind.Absolute, out var repoUri) || repoUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Loader library repository must use HTTPS.");
            var artifactUrl = new Uri(new Uri(repository.TrimEnd('/') + "/"), relative).AbsoluteUri;
            var path = Path.Combine(instanceDirectory, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));
            using (var checksumResponse = await _http.GetAsync(artifactUrl + ".sha1", cancellationToken).ConfigureAwait(false))
            {
                checksumResponse.EnsureSuccessStatusCode();
                var expectedSha1 = (await checksumResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().Split(' ')[0];
                if (!Regex.IsMatch(expectedSha1, "^[a-fA-F0-9]{40}$"))
                    throw new InvalidDataException("Loader library has no valid SHA-1 checksum.");
                using var download = await _http.GetAsync(artifactUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                download.EnsureSuccessStatusCode();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".part";
                try
                {
                    await using (var target = File.Create(temp))
                        await download.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    await using var verify = File.OpenRead(temp);
                    var hash = Convert.ToHexString(await SHA1.HashDataAsync(verify, cancellationToken).ConfigureAwait(false));
                    if (!hash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"SHA-1 mismatch in loader library {coordinate}.");
                    File.Move(temp, path, true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                mergedLibraries.Add(new JsonObject
                {
                    ["name"] = coordinate,
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject { ["path"] = relative, ["url"] = artifactUrl, ["sha1"] = expectedSha1 }
                    }
                });
            }
        }
        foreach (var library in vanillaLibraries)
            if (!loaderNames.Contains(library?["name"]?.GetValue<string>())) mergedLibraries.Add(library?.DeepClone());
        vanilla["libraries"] = mergedLibraries;
        if (profile["arguments"] is JsonObject arguments)
        {
            var merged = vanilla["arguments"]?.AsObject()
                ?? throw new InvalidDataException("Vanilla Minecraft metadata lacks launch arguments.");
            foreach (var section in new[] { "jvm", "game" })
            {
                if (arguments[section] is not JsonArray additions) continue;
                var targets = merged[section]?.AsArray() ?? throw new InvalidDataException($"Vanilla Minecraft lacks {section} arguments.");
                foreach (var item in additions) targets.Add(item?.DeepClone());
            }
        }
        var launchDirectory = Path.Combine(instanceDirectory, "versions", launchId);
        Directory.CreateDirectory(launchDirectory);
        var output = Path.Combine(launchDirectory, launchId + ".json");
        var temporaryOutput = output + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryOutput, vanilla.ToJsonString(), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryOutput, output, true);
        }
        finally
        {
            if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
        }
        return new ModLoaderInstallResult(launchId, loaderVersion);
    }
}
