using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace PCL.Linux.Cli;

internal static class SkillCommands
{
    private const string SkillName = "mcm";
    private const long MaxSkillBytes = 512 * 1024;
    private static readonly Uri SkillUri = new(McmInfo.RepositoryUrl + "/releases/latest/download/MCM_skill.md");
    private static readonly Uri HashUri = new(McmInfo.RepositoryUrl + "/releases/latest/download/MCM_skill.md.sha256");

    internal static async Task<int> InstallAsync(string[] args)
    {
        if (args.Length != 0) return Usage("Usage: mcm skill install");
        using var http = TorFallbackHttpMessageHandler.CreateResourceClient();
        return await InstallAsync(http, GetTargets(), Console.Out).ConfigureAwait(false);
    }

    internal static async Task<int> InstallAsync(HttpClient http,
        IReadOnlyList<(string Name, string Directory, bool Detected)> targets, TextWriter output)
    {
        if (!targets.Any(target => target.Detected))
        {
            foreach (var target in targets) output.WriteLine($"Skipped {target.Name}: tool directory was not found.");
            return 1;
        }
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MCM", McmInfo.ReleaseVersion));
        var content = await DownloadAsync(http, SkillUri).ConfigureAwait(false);
        var expected = ParseHash(await DownloadAsync(http, HashUri).ConfigureAwait(false));
        var actual = Convert.ToHexString(SHA256.HashData(content));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("MCM Skill SHA-256 verification failed.");
        var installed = 0;
        var failed = 0;
        foreach (var (name, directory, detected) in targets)
        {
            if (!detected) { output.WriteLine($"Skipped {name}: tool directory was not found."); continue; }
            try
            {
                var skillDirectory = Path.Combine(directory, SkillName);
                var destination = Path.Combine(skillDirectory, "SKILL.md");
                PCL.Core.Shared.Minecraft.InstanceBackupStore.RequireSafePath(destination);
                Directory.CreateDirectory(skillDirectory);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await file.WriteAsync(content).ConfigureAwait(false);
                    File.Move(temporary, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                installed++;
                output.WriteLine($"Installed MCM Skill for {name}: {destination}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failed++; output.WriteLine($"Failed {name}: {ex.Message}"); }
        }
        output.WriteLine($"MCM Skill installation finished: {installed} target(s) updated, {failed} failed, {targets.Count - installed - failed} skipped.");
        return installed == 0 || failed != 0 ? 1 : 0;
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, Uri uri)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is <= 0 or > MaxSkillBytes) throw new InvalidDataException("MCM Skill is too large.");
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); count != 0;
             count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false))
        {
            if (output.Length + count > MaxSkillBytes) throw new InvalidDataException("MCM Skill is too large.");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0 || response.Content.Headers.ContentLength is long expected && expected != output.Length) throw new InvalidDataException("MCM Skill has an invalid size.");
        return output.ToArray();
    }

    private static string ParseHash(byte[] data)
    {
        var value = System.Text.Encoding.UTF8.GetString(data).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit)) throw new InvalidDataException("MCM Skill hash file is invalid.");
        return value;
    }

    private static IReadOnlyList<(string Name, string Directory, bool Detected)> GetTargets()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agents = Path.Combine(home, ".agents", "skills");
        var claude = Path.Combine(home, ".claude", "skills");
        var gemini = Path.Combine(home, ".gemini", "skills");
        var cursor = Path.Combine(home, ".cursor", "skills");
        return [("Codex", agents, Directory.Exists(agents) || ExecutableExists("codex") || Directory.Exists(Path.Combine(home, ".codex"))),
            ("Claude Code", claude, Directory.Exists(claude) || ExecutableExists("claude") || Directory.Exists(Path.Combine(home, ".claude"))),
            ("Gemini CLI", gemini, Directory.Exists(gemini) || ExecutableExists("gemini") || Directory.Exists(Path.Combine(home, ".gemini"))),
            ("Cursor", cursor, Directory.Exists(cursor) || ExecutableExists("cursor") || Directory.Exists(Path.Combine(home, ".cursor")))];
    }

    private static bool ExecutableExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(item => Path.Combine(item, name)).FirstOrDefault(File.Exists);
        return path is not null;
    }

    private static int Usage(string message) { Console.Error.WriteLine(message); return 2; }
}
