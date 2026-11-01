using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PCL.Core.Shared.Java;

public sealed record JavaRuntime(string ExecutablePath, string Version);

public static partial class JavaDiscovery
{
    public static IReadOnlyList<string> EnumerateCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        AddPathCandidates(candidates);
        AddCommonCandidates(candidates);
        return candidates.Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<JavaRuntime> Scan()
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var found = new Dictionary<string, JavaRuntime>(pathComparer);
        foreach (var candidate in EnumerateCandidates())
        {
            var runtime = Probe(candidate);
            if (runtime is null) continue;
            var canonicalPath = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(candidate);
            found.TryAdd(canonicalPath, runtime with { ExecutablePath = canonicalPath });
        }
        return found.Values.OrderBy(runtime => runtime.ExecutablePath, pathComparer).ToArray();
    }

    public static JavaRuntime? Probe(string executablePath)
    {
        if (!File.Exists(executablePath)) return null;
        var version = TryReadVersion(executablePath);
        return version is null ? null : new JavaRuntime(Path.GetFullPath(executablePath), version);
    }

    public static Version NormalizeVersion(Version version)
        => version.Major == 1 && version.Minor >= 0
            ? new Version(version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0))
            : version;

    public static int ParseMajorVersion(string version)
    {
        var normalized = version.StartsWith("1.", StringComparison.Ordinal) ? version[2..] : version;
        var digitCount = normalized.TakeWhile(char.IsAsciiDigit).Count();
        return digitCount > 0 && int.TryParse(normalized[..digitCount], out var major) ? major : 0;
    }

    private static string? TryReadVersion(string candidate)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = candidate,
                ArgumentList = { "-version" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (process is null) return null;

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
                return null;
            }

            var match = JavaVersionRegex().Match(string.Concat(stdout, "\n", stderr));
            return process.ExitCode == 0 && match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void AddPathCandidates(ISet<string> candidates)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return;
        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(directory, executable));
    }

    private static void AddCommonCandidates(ISet<string> candidates)
    {
        if (!OperatingSystem.IsLinux()) return;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { "/usr/lib/jvm", "/usr/java", Path.Combine(home, ".jdks"), Path.Combine(home, ".local", "share", "jdk") };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                AddCandidates(root);
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    AddCandidates(child);
                    foreach (var nested in Directory.EnumerateDirectories(child)) AddCandidates(nested);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A protected installation must not prevent other Java paths from being scanned.
            }
        }

        void AddCandidates(string directory)
        {
            candidates.Add(Path.Combine(directory, "java"));
            candidates.Add(Path.Combine(directory, "bin", "java"));
        }
    }

    [GeneratedRegex("(?:version \"|openjdk )([0-9]+(?:\\.[0-9]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaVersionRegex();
}
