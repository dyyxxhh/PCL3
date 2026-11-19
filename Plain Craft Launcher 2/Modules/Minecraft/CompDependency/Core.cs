using System.IO;
using PCL.Core.Minecraft.ResourceProject;
using CompFile = PCL.Comp.CompFile;
using CompLoaderType = PCL.Comp.CompLoaderType;
using CompProject = PCL.Comp.CompProject;
using LocalCompFile = PCL.LocalComp.LocalCompFile;

namespace PCL;

public static partial class CompDependency
{
    public static ModDependencyRequest BuildRequest(
        CompFile file,
        CompProject project,
        string targetMinecraftVersion,
        List<CompLoaderType> targetLoaders,
        string targetModsFolder)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(project);

        return new ModDependencyRequest
        {
            TargetMinecraftVersion = targetMinecraftVersion,
            TargetLoaders = ToLoaderNames(targetLoaders),
            RequiredDependencies = GetReferences(file),
            InstalledMods = ScanInstalledMods(targetModsFolder),
            ProjectResolver = ResolveProjectFiles
        };
    }

    internal static List<ModDependencyReference> GetReferences(CompFile file)
        => file.Dependencies.Concat(file.RawDependencies).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new ModDependencyReference
            {
                ProjectId = id,
                Source = GetSource(file.FromCurseForge),
                RequiredFileId = file.RequiredDependencyFileIds.GetValueOrDefault(id)
            }).ToList();

    internal static List<PCL.Network.DownloadFile> PrepareBatchDownloads(IEnumerable<CompFile> files,
        string version, List<CompLoaderType> loaders, string directory)
    {
        var roots = files.Where(file => file.Type == Comp.CompType.Mod).ToArray();
        var references = roots.SelectMany(GetReferences).ToList();
        if (references.Count == 0) return [];
        var installed = ScanInstalledMods(directory);
        foreach (var root in roots.Where(file => !string.IsNullOrWhiteSpace(file.ProjectId)))
        {
            var source = GetSource(root.FromCurseForge);
            installed.RemoveAll(mod => mod.SourceProjectId == root.ProjectId && mod.Source == source);
            installed.Add(new InstalledModIdentity { SourceProjectId = root.ProjectId, Source = source,
                FileId = root.Id, GameVersions = [version], Loaders = ToLoaderNames(loaders) });
        }
        var result = new ModDependencyResolver().Resolve(new ModDependencyRequest
        {
            TargetMinecraftVersion = version,
            TargetLoaders = ToLoaderNames(loaders),
            InstalledMods = installed,
            RequiredDependencies = references,
            ProjectResolver = ResolveProjectFiles
        });
        return ConfirmDependencyInstall(result) switch
        {
            Comp.CompDepsInstallTypes.WithDeps => BuildDependencyDownloads(result, directory).Select(item => item.File).ToList(),
            Comp.CompDepsInstallTypes.WithoutDeps => [],
            _ => throw new Common.CancelledException(),
        };
    }

    public static List<InstalledModIdentity> ScanInstalledMods(string targetModsFolder)
    {
        var result = new List<InstalledModIdentity>();
        if (string.IsNullOrWhiteSpace(targetModsFolder) || !Directory.Exists(targetModsFolder))
            return result;

        foreach (var path in Directory.GetFiles(targetModsFolder))
        {
            if (!LocalCompFile.IsModFile(path)) continue;

            var localFile = new LocalCompFile(path);
            localFile.Load();

            var source = localFile.Comp is null
                ? null
                : GetSource(localFile.Comp.FromCurseForge);
            var gameVersions = localFile
                    .CompFile?.GameVersions
                    ?.Where(static version => !string.IsNullOrWhiteSpace(version))
                    .Select(Comp.NormalizeGameVersion)
                    .Where(static version => !string.IsNullOrWhiteSpace(version))
                    .ToList()
                ?? [];
            var loaders = ToLoaderNames(localFile.CompFile?.ModLoaders);

            if (!string.IsNullOrWhiteSpace(localFile.Comp?.Id)
                && !string.IsNullOrWhiteSpace(source))
            {
                result.Add(
                    new InstalledModIdentity
                    {
                        SourceProjectId = localFile.Comp.Id,
                        Source = source,
                        ModId = localFile.ModId,
                        FileId = localFile.CompFile?.Id,
                        Disabled = localFile.State == LocalCompFile.LocalFileStatus.Disabled,
                        GameVersions = gameVersions,
                        Loaders = loaders
                    });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(localFile.CompFile?.ProjectId))
            {
                var fileSource = GetSource(localFile.CompFile.FromCurseForge);
                result.Add(
                    new InstalledModIdentity
                    {
                        SourceProjectId = localFile.CompFile.ProjectId,
                        Source = fileSource,
                        ModId = localFile.ModId,
                        FileId = localFile.CompFile?.Id,
                        Disabled = localFile.State == LocalCompFile.LocalFileStatus.Disabled,
                        GameVersions = gameVersions,
                        Loaders = loaders
                    });
                continue;
            }

            result.Add(
                new InstalledModIdentity
                {
                    SourceProjectId = null,
                    Source = null,
                    ModId = localFile.ModId,
                    GameVersions = gameVersions,
                    Loaders = loaders
                });
        }

        return result;
    }

    public static ModDependencyProject? ResolveProjectFiles(string source, string projectId)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(projectId))
            return null;

        var fromCurseForge =
            string.Equals(source, "CurseForge", StringComparison.OrdinalIgnoreCase);
        var files = Comp.CompFilesGet(projectId, fromCurseForge);
        if (!Comp.compProjectCache.TryGetValue(projectId, out var compProject))
            return null;

        if (compProject.FromCurseForge != fromCurseForge)
            return null;

        return new ModDependencyProject
        {
            ProjectId = compProject.Id,
            Source = source,
            ProjectName = compProject.TranslatedName ?? compProject.RawName,
            Files =
            [
                .. files.Select(compFile => new ModDependencyFile
                {
                    Id = compFile.Id,
                    DisplayName = compFile.DisplayName,
                    Version = compFile.Version,
                    GameVersions = compFile
                            .GameVersions
                            ?.Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .Select(Comp.NormalizeGameVersion)
                            .Where(static version =>
                                !string.IsNullOrWhiteSpace(version))
                            .ToList()
                        ?? [],
                    Loaders = ToLoaderNames(compFile.ModLoaders),
                    ReleaseType = MapReleaseType(compFile.Status),
                    ReleaseDate = compFile.ReleaseDate,
                    RequiredDependencies = GetReferences(compFile)
                })
            ]
        };
    }
}
