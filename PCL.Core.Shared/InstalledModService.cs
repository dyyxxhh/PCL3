using System.Text.Json;

namespace PCL.Core.Shared.Minecraft;

public sealed record InstalledMod(string FileName, ModSource Source, string ProjectId, string ProjectName, string ReleaseId, string ReleaseName, bool Disabled,
    string? DownloadUrl = null, string? Sha1 = null);

public static class InstalledModService
{
    private const string ManifestName = "pcl-mods.json";

    public static IReadOnlyList<InstalledMod> List(string instanceDirectory)
    {
        var manifest = Load(instanceDirectory);
        var mods = Path.Combine(instanceDirectory, "mods");
        if (!Directory.Exists(mods)) return [];
        return Directory.EnumerateFiles(mods, "*.jar", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(mods, "*.jar.disabled", SearchOption.TopDirectoryOnly))
            .Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var known = manifest.FirstOrDefault(item => item.FileName == fileName);
                return known ?? new InstalledMod(fileName, ModSource.Modrinth, "", fileName, "", "", fileName.EndsWith(".disabled", StringComparison.Ordinal));
            })
            .OrderBy(mod => mod.FileName, StringComparer.Ordinal)
            .ToArray();
    }

    public static async Task TrackAsync(string instanceDirectory, InstalledMod mod, CancellationToken cancellationToken = default)
    {
        if (!IsSafeName(mod.FileName)) throw new InvalidDataException("Invalid mod file name.");
        var entries = Load(instanceDirectory);
        entries.RemoveAll(existing => existing.FileName == mod.FileName
            || (!string.IsNullOrEmpty(mod.ProjectId) && existing.Source == mod.Source && existing.ProjectId == mod.ProjectId));
        entries.Add(mod);
        await SaveAsync(instanceDirectory, entries, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> InstallReleaseAsync(string instanceDirectory, ModRelease release, string projectName,
        ModPlatformService platform, CancellationToken cancellationToken = default)
    {
        var mods = List(instanceDirectory);
        var existing = mods.FirstOrDefault(mod => mod.Source == release.Source && mod.ProjectId == release.ProjectId);
        if (existing?.ReleaseId == release.VersionId) return ModPath(instanceDirectory, existing.FileName);
        var disabled = existing?.Disabled == true;
        var targetName = release.FileName + (disabled ? ".disabled" : "");
        var target = ModPath(instanceDirectory, targetName);
        if (mods.Any(mod => (mod.FileName == release.FileName || mod.FileName == targetName)
            && (mod.Source != release.Source || mod.ProjectId != release.ProjectId)))
            throw new IOException($"The release file name '{release.FileName}' belongs to another installed mod.");
        if (File.Exists(target) && existing?.FileName != targetName)
            throw new IOException("The target mod file already exists.");
        var installed = await platform.InstallReleaseAsync(release, Path.Combine(instanceDirectory, "mods"), cancellationToken,
            overwrite: existing?.FileName == release.FileName).ConfigureAwait(false);
        if (disabled) File.Move(installed, target, overwrite: existing?.FileName == targetName);
        await TrackAsync(instanceDirectory, new InstalledMod(targetName, release.Source, release.ProjectId, projectName,
            release.VersionId, release.VersionName, disabled, release.DownloadUrl, release.Sha1), cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.FileName != targetName)
            await RemoveAsync(instanceDirectory, existing.FileName, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public static async Task SetDisabledAsync(string instanceDirectory, string fileName, bool disabled, CancellationToken cancellationToken = default)
    {
        var from = ModPath(instanceDirectory, fileName);
        if (!File.Exists(from)) throw new FileNotFoundException("Mod file does not exist.", from);
        var targetName = disabled ? fileName + ".disabled" : fileName[..^".disabled".Length];
        if (disabled == fileName.EndsWith(".disabled", StringComparison.Ordinal))
            throw new InvalidOperationException(disabled ? "Mod is already disabled." : "Mod is already enabled.");
        var to = ModPath(instanceDirectory, targetName);
        if (File.Exists(to)) throw new IOException("The target mod file already exists.");
        File.Move(from, to);
        var entries = Load(instanceDirectory);
        var index = entries.FindIndex(mod => mod.FileName == fileName);
        if (index >= 0)
        {
            entries[index] = entries[index] with { FileName = targetName, Disabled = disabled };
            await SaveAsync(instanceDirectory, entries, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task RemoveAsync(string instanceDirectory, string fileName, CancellationToken cancellationToken = default)
    {
        var path = ModPath(instanceDirectory, fileName);
        if (!File.Exists(path)) throw new FileNotFoundException("Mod file does not exist.", path);
        File.Delete(path);
        var entries = Load(instanceDirectory);
        entries.RemoveAll(mod => mod.FileName == fileName);
        await SaveAsync(instanceDirectory, entries, cancellationToken).ConfigureAwait(false);
    }

    private static List<InstalledMod> Load(string instanceDirectory)
    {
        var path = Path.Combine(instanceDirectory, ManifestName);
        return !File.Exists(path) ? [] : JsonSerializer.Deserialize<List<InstalledMod>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Installed mods manifest is invalid.");
    }

    private static async Task SaveAsync(string instanceDirectory, List<InstalledMod> entries, CancellationToken cancellationToken)
    {
        var path = Path.Combine(instanceDirectory, ManifestName);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ModPath(string instanceDirectory, string fileName)
    {
        if (!IsSafeName(fileName)) throw new ArgumentException("Invalid mod file name.", nameof(fileName));
        return Path.Combine(instanceDirectory, "mods", fileName);
    }

    private static bool IsSafeName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') &&
        (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase));
}
