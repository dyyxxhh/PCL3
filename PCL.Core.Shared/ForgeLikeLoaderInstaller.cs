using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PCL.Core.Shared.Java;

namespace PCL.Core.Shared.Minecraft;

public sealed class ForgeLikeLoaderInstaller(HttpClient http)
{
    public async Task<ModLoaderInstallResult> InstallAsync(string gameVersion, string loader, string? requestedVersion,
        string gameDirectory, CancellationToken cancellationToken = default)
    {
        if (loader is not ("forge" or "neoforge")) throw new ArgumentException("Invalid Forge-like loader.", nameof(loader));
        var legacyNeoForge = loader == "neoforge" && gameVersion == "1.20.1";
        var artifact = loader == "forge" ? "net/minecraftforge/forge" : legacyNeoForge ? "net/neoforged/forge" : "net/neoforged/neoforge";
        var repository = loader == "forge" ? "https://maven.minecraftforge.net/" : "https://maven.neoforged.net/releases/";
        var metadataUrl = repository + artifact + "/maven-metadata.xml";
        using var metadataResponse = await http.GetAsync(metadataUrl, cancellationToken).ConfigureAwait(false);
        metadataResponse.EnsureSuccessStatusCode();
        var metadataText = await metadataResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var xmlSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        using var metadataReader = XmlReader.Create(new StringReader(metadataText), xmlSettings);
        var xml = XDocument.Load(metadataReader);
        var versions = xml.Descendants("version").Select(node => node.Value).ToArray();
        var mavenVersion = SelectMavenVersion(versions, gameVersion, loader, requestedVersion);
        var artifactName = loader == "forge" ? "forge" : legacyNeoForge ? "forge" : "neoforge";
        var installerName = $"{artifactName}-{mavenVersion}-installer.jar";
        var installerUrl = repository + artifact + "/" + Uri.EscapeDataString(mavenVersion) + "/" + installerName;
        using var checksumResponse = await http.GetAsync(installerUrl + ".sha1", cancellationToken).ConfigureAwait(false);
        checksumResponse.EnsureSuccessStatusCode();
        var sha1 = (await checksumResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().Split(' ')[0];
        if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
            throw new InvalidDataException("Forge-like installer has no valid SHA-1 checksum.");
        var cache = Path.Combine(gameDirectory, "loader-cache");
        Directory.CreateDirectory(cache);
        var installerPath = Path.Combine(cache, installerName);
        var temporary = installerPath + ".part";
        try
        {
            using var installerResponse = await http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            installerResponse.EnsureSuccessStatusCode();
            await using (var output = File.Create(temporary))
                await installerResponse.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await using (var input = File.OpenRead(temporary))
            {
                var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Forge-like installer SHA-1 verification failed.");
            }
            File.Move(temporary, installerPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var installer = new MinecraftInstaller();
        var major = await installer.GetRequiredJavaMajorAsync(gameVersion, gameDirectory, cancellationToken).ConfigureAwait(false);
        var java = installer.FindInstalledJavaRuntime(major, gameDirectory)
            ?? JavaDiscovery.Scan().FirstOrDefault(runtime => JavaDiscovery.ParseMajorVersion(runtime.Version) == major)?.ExecutablePath
            ?? await installer.InstallJavaRuntimeAsync(major, gameDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        var processInfo = new ProcessStartInfo(java)
        {
            WorkingDirectory = gameDirectory,
            UseShellExecute = false
        };
        processInfo.ArgumentList.Add("-jar");
        processInfo.ArgumentList.Add(installerPath);
        processInfo.ArgumentList.Add("--installClient");
        processInfo.ArgumentList.Add(gameDirectory);
        await PrepareLauncherProfilesAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        using var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Could not start the Forge-like installer.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{loader} installer exited with code {process.ExitCode}; check installer output above.");

        var versionsPath = Path.Combine(gameDirectory, "versions");
        var candidates = Directory.EnumerateFiles(versionsPath, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileNameWithoutExtension(path), gameVersion, StringComparison.Ordinal))
            .Select(path => new { Path = path, Data = JsonNode.Parse(File.ReadAllText(path))?.AsObject() })
            .Where(candidate => candidate.Data?["inheritsFrom"]?.GetValue<string>() == gameVersion
                && candidate.Data["id"]?.GetValue<string>()?.Contains(loader == "forge" ? "forge" : "neoforge", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var shortVersion = loader == "forge" || legacyNeoForge ? mavenVersion[(gameVersion.Length + 1)..] : mavenVersion;
        var candidateIds = candidates.Select(item => item.Data?["id"]?.GetValue<string>()).OfType<string>();
        var expectedProfileId = SelectProfileId(candidateIds, shortVersion);
        var candidate = candidates.FirstOrDefault(item => item.Data?["id"]?.GetValue<string>() == expectedProfileId)
            ?? throw new InvalidDataException($"{loader} installer did not produce a matching client version profile in {versionsPath}.");
        var profile = candidate.Data!;
        var launchId = profile["id"]?.GetValue<string>() ?? throw new InvalidDataException("Installed loader profile has no ID.");
        if (!Regex.IsMatch(launchId, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)
            || launchId != Path.GetFileNameWithoutExtension(candidate.Path))
            throw new InvalidDataException("Installer produced an unsafe version ID.");
        var basePath = Path.Combine(versionsPath, gameVersion, gameVersion + ".json");
        var vanilla = JsonNode.Parse(await File.ReadAllTextAsync(basePath, cancellationToken).ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException("Vanilla Minecraft metadata is missing.");
        MergeProfile(vanilla, profile, gameVersion, launchId);
        var destination = Path.Combine(versionsPath, launchId, launchId + ".json");
        var outputTemp = destination + ".tmp";
        try
        {
            await File.WriteAllTextAsync(outputTemp, vanilla.ToJsonString(), cancellationToken).ConfigureAwait(false);
            File.Move(outputTemp, destination, true);
        }
        finally
        {
            if (File.Exists(outputTemp)) File.Delete(outputTemp);
        }
        return new ModLoaderInstallResult(launchId, shortVersion);
    }

    private static async Task PrepareLauncherProfilesAsync(string gameDirectory, CancellationToken cancellationToken)
    {
        // Like PCL's McFolderLauncherProfilesJsonCreate, supply the official launcher's
        // marker before installing. Forge/NeoForge treat its absence as "run Minecraft first".
        var path = Path.Combine(gameDirectory, "launcher_profiles.json");
        if (File.Exists(path)) return;
        var profiles = new JsonObject
        {
            ["profiles"] = new JsonObject
            {
                ["PCL"] = new JsonObject
                {
                    ["icon"] = "Grass",
                    ["name"] = "PCL",
                    ["lastVersionId"] = "latest-release",
                    ["type"] = "latest-release",
                    ["lastUsed"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'.0000Z'", CultureInfo.InvariantCulture)
                }
            },
            ["selectedProfile"] = "PCL",
            ["clientToken"] = "23323323323323323323323323323333"
        };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, profiles.ToJsonString(), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another installer/launcher created the file; keep its settings.
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string SelectMavenVersion(IEnumerable<string> versions, string gameVersion, string loader, string? requestedVersion = null)
    {
        var minecraftParts = gameVersion.Split('.');
        var neoForgePrefix = minecraftParts.Length is 2 or 3 && minecraftParts[0] == "1"
            ? minecraftParts[1] + "." + (minecraftParts.Length == 3 ? minecraftParts[2] : "0") + "."
            : gameVersion + ".";
        var prefix = loader == "forge" || gameVersion == "1.20.1"
            ? gameVersion + "-"
            : neoForgePrefix;
        var available = versions.Where(version => version.StartsWith(prefix, StringComparison.Ordinal)
            && Regex.IsMatch(version[prefix.Length..], "^[0-9]+(?:\\.[0-9]+)*$", RegexOptions.CultureInvariant)).ToArray();
        if (requestedVersion is not null)
            return available.FirstOrDefault(version => version == requestedVersion || version[prefix.Length..] == requestedVersion)
                ?? throw new InvalidOperationException($"No compatible {loader} version '{requestedVersion}' was found for Minecraft {gameVersion}.");
        return available.OrderByDescending(version => Version.TryParse(version[prefix.Length..].Contains('.')
                ? version[prefix.Length..] : "0." + version[prefix.Length..], out var parsed) ? parsed : new Version(0, 0))
            .FirstOrDefault() ?? throw new InvalidOperationException($"No compatible {loader} version was found for Minecraft {gameVersion}.");
    }

    public static string SelectProfileId(IEnumerable<string> profileIds, string loaderVersion)
        => profileIds.FirstOrDefault(id => id.Equals(loaderVersion, StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("-" + loaderVersion, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"No generated loader profile matches version '{loaderVersion}'.");

    private static void MergeProfile(JsonObject vanilla, JsonObject profile, string gameVersion, string launchId)
    {
        vanilla["id"] = launchId;
        vanilla["jar"] = gameVersion;
        vanilla["mainClass"] = profile["mainClass"]?.GetValue<string>()
            ?? throw new InvalidDataException("Installed loader profile has no main class.");
        var baseLibraries = vanilla["libraries"]?.AsArray() ?? throw new InvalidDataException("Vanilla libraries are missing.");
        var extraLibraries = profile["libraries"]?.AsArray() ?? [];
        var extraNames = extraLibraries.Select(item => item?["name"]?.GetValue<string>()).Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
        var libraries = new JsonArray();
        foreach (var library in extraLibraries) libraries.Add(library?.DeepClone());
        foreach (var library in baseLibraries)
            if (!extraNames.Contains(library?["name"]?.GetValue<string>())) libraries.Add(library?.DeepClone());
        vanilla["libraries"] = libraries;
        var targetArgs = vanilla["arguments"]?.AsObject();
        if (targetArgs is not null && profile["arguments"] is JsonObject extra)
            foreach (var name in new[] { "jvm", "game" })
            {
                if (targetArgs[name] is not JsonArray target || extra[name] is not JsonArray additions) continue;
                foreach (var argument in additions) target.Add(argument?.DeepClone());
            }
    }
}
