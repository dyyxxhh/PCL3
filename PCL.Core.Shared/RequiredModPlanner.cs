namespace PCL.Core.Shared.Minecraft;

public static class RequiredModPlanner
{
    public static async Task<IReadOnlyList<ModRelease>> PlanAsync(IEnumerable<ModRelease> roots,
        IReadOnlyList<InstalledMod> installed, Func<ModDependency, Task<ModRelease>> resolve)
    {
        var selected = new Dictionary<(ModSource, string), ModRelease>();
        var visited = new HashSet<(ModSource, string)>();
        var order = new List<ModRelease>();
        var rootList = roots.ToArray();
        foreach (var root in rootList) Select(root);
        foreach (var root in rootList) await VisitAsync(root, 0).ConfigureAwait(false);
        return order;

        void Select(ModRelease release)
        {
            var key = (release.Source, release.ProjectId);
            if (selected.TryGetValue(key, out var previous) && previous.VersionId != release.VersionId)
                throw new InvalidOperationException($"Required dependency {release.Source}:{release.ProjectId} has conflicting versions.");
            selected[key] = release;
        }

        async Task VisitAsync(ModRelease release, int depth)
        {
            if (!visited.Add((release.Source, release.ProjectId))) return;
            if (depth > 16 || visited.Count > 4096) throw new InvalidOperationException("Required dependency graph exceeds the supported size.");
            foreach (var dependency in release.RequiredDependencies ?? [])
            {
                var key = (dependency.Source, dependency.ProjectId);
                var current = installed.FirstOrDefault(mod => mod.Source == key.Source && mod.ProjectId == key.ProjectId);
                if (current?.Disabled == true) throw Problem(dependency, $"is disabled ({current.FileName})");
                if (selected.TryGetValue(key, out var planned))
                {
                    if (dependency.VersionId is not null && dependency.VersionId != planned.VersionId)
                        throw Problem(dependency, "has conflicting version requirements");
                }
                else
                {
                    if (current is not null)
                    {
                        if (dependency.VersionId is not null && current.ReleaseId != dependency.VersionId)
                            throw Problem(dependency, $"requires {dependency.VersionId}, but {current.ReleaseId} is installed");
                        if (string.IsNullOrEmpty(current.ReleaseId)) throw Problem(dependency, "has an unknown installed version");
                    }
                    try
                    {
                        planned = await resolve(dependency with { VersionId = current?.ReleaseId ?? dependency.VersionId }).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
                    { throw Problem(dependency, "could not be resolved for the selected Minecraft version and loader", ex); }
                    if (planned.Source != dependency.Source || planned.ProjectId != dependency.ProjectId ||
                        dependency.VersionId is not null && planned.VersionId != dependency.VersionId)
                        throw Problem(dependency, "resolved to a different project or version");
                    Select(planned);
                }
                await VisitAsync(planned, depth + 1).ConfigureAwait(false);
            }
            order.Add(release);
        }

        static InvalidOperationException Problem(ModDependency dependency, string message, Exception? cause = null) =>
            new($"Required dependency {dependency.Source}:{dependency.ProjectId} {message}.", cause);
    }
}
