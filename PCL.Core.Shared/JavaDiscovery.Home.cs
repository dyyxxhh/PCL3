namespace PCL.Core.Shared.Java;

public static partial class JavaDiscovery
{
    /// <summary>Resolves a Java home or bin/java, including links in parent directories.</summary>
    public static string ResolveHome(string value)
    {
        var path = ResolvePhysicalPath(value);
        if (!Directory.Exists(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Java home or executable was not found.", path);
            var bin = Path.GetDirectoryName(path)!;
            if (Path.GetFileName(bin) != "bin")
                throw new ArgumentException("Java must point to a complete Java home or its bin/java executable.");
            path = Path.GetDirectoryName(bin)!;
        }
        if (!File.Exists(Path.Combine(path, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java")))
            throw new InvalidDataException("The selected Java home does not contain bin/java.");
        return path;
    }

    internal static string ResolvePhysicalPath(string value)
    {
        var path = Path.GetFullPath(value);
        for (var hops = 0; hops < 64; hops++)
        {
            var root = Path.GetPathRoot(path)!;
            var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var followed = false;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                FileSystemInfo link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                var target = link.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new InvalidDataException($"Cannot resolve Java runtime link: {current}");
                path = Path.GetFullPath(Path.Combine(new[] { target.FullName }.Concat(parts[(i + 1)..]).ToArray()));
                followed = true;
                break;
            }
            if (!followed) return path;
        }
        throw new InvalidDataException("Java runtime links form a cycle or are nested too deeply.");
    }
}
