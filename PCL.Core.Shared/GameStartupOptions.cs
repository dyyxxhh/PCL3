using System.Text;
using System.Text.RegularExpressions;

namespace PCL.Core.Shared.Minecraft;

public enum GameStartupWindowMode { Default, Fullscreen, Custom, Maximized }

public sealed record GameStartupOptions(
    bool AutoLanguage = true,
    string Language = "zh-CN",
    GameStartupWindowMode WindowMode = GameStartupWindowMode.Default,
    int Width = 854,
    int Height = 480);

public static class GameStartupOptionsFile
{
    public static bool IsLanguageCode(string input) =>
        Regex.IsMatch(input, @"\A[A-Za-z]{2,3}[-_][A-Za-z0-9]{2,8}\z", RegexOptions.CultureInvariant);

    public static bool ShouldUseDefaultLanguage(string? current, bool hasSaves) =>
        !hasSaves || string.IsNullOrWhiteSpace(current) || current.Equals("none", StringComparison.OrdinalIgnoreCase);

    public static bool IsFullscreen(string gameDirectory)
    {
        var rootOptions = Path.Combine(gameDirectory, "options.txt");
        try
        {
            InstanceBackupStore.RequireSafePath(rootOptions);
            var path = rootOptions;
            if (!File.Exists(rootOptions))
            {
                path = Path.Combine(gameDirectory, "config", "yosbr", "options.txt");
                InstanceBackupStore.RequireSafePath(path);
            }
            if (!File.Exists(path)) return false;
            return File.ReadAllLines(path, Encoding.UTF8)
                .LastOrDefault(line => line.StartsWith("fullscreen:", StringComparison.Ordinal)) is { } value &&
                value["fullscreen:".Length..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // Unknown launch state must not put an overlay on a possibly exclusive-fullscreen game.
        }
    }

    public static string ResolveLanguage(string? code, DateTime? releaseDate)
    {
        if (releaseDate is { } day && day > new DateTime(2000, 1, 1) && day <= new DateTime(2011, 11, 18))
            return "none";
        if (string.IsNullOrWhiteSpace(code) || code.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "none";
        var normalized = code.Trim().Replace('-', '_');
        var parts = normalized.Split('_', 2);
        if (parts.Length == 1) return normalized.ToLowerInvariant();
        var legacy = releaseDate is { } date && date >= new DateTime(2012, 1, 12) && date <= new DateTime(2016, 6, 8);
        return parts[0].ToLowerInvariant() + "_" + (legacy ? parts[1].ToUpperInvariant() : parts[1].ToLowerInvariant());
    }

    // Windows has launcher-specific locale and file IO. MCM applies these same language rules to its own instance.
    public static bool Apply(string gameDirectory, GameStartupOptions options, DateTime? releaseDate)
    {
        if (!options.AutoLanguage && options.WindowMode == GameStartupWindowMode.Default) return false;
        if (options.AutoLanguage && !IsLanguageCode(options.Language)) throw new ArgumentException("Invalid game language code.");
        var rootOptions = Path.Combine(gameDirectory, "options.txt");
        InstanceBackupStore.RequireSafePath(rootOptions);
        var path = rootOptions;
        if (!File.Exists(rootOptions))
        {
            var yosbrOptions = Path.Combine(gameDirectory, "config", "yosbr", "options.txt");
            InstanceBackupStore.RequireSafePath(yosbrOptions);
            if (File.Exists(yosbrOptions)) path = yosbrOptions;
        }

        var exists = File.Exists(path);
        var encoding = new UTF8Encoding(false, true);
        var bytes = exists ? File.ReadAllBytes(path) : [];
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = encoding.GetString(bytes.AsSpan(hasBom ? Encoding.UTF8.Preamble.Length : 0));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // Keep original separators, including mixed CRLF/LF and absence of a final newline.
        var lines = Regex.Split(text, "(\r\n|\n|\r)").ToList();
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.AutoLanguage)
        {
            var current = path == rootOptions
                ? lines.LastOrDefault(line => line.StartsWith("lang:", StringComparison.Ordinal))?[5..]
                : null;
            var defaultLanguage = ShouldUseDefaultLanguage(current, Directory.Exists(Path.Combine(gameDirectory, "saves")));
            var language = ResolveLanguage(defaultLanguage ? options.Language : current, releaseDate);
            if (!string.Equals(language, current ?? "none", StringComparison.Ordinal)) updates["lang"] = language;
            if (defaultLanguage && options.Language.Split('-', '_')[0].ToLowerInvariant() is "zh" or "ja" or "ko")
                updates["forceUnicodeFont"] = "true";
        }
        if (options.WindowMode != GameStartupWindowMode.Default)
            updates["fullscreen"] = options.WindowMode == GameStartupWindowMode.Fullscreen ? "true" : "false";

        foreach (var (key, value) in updates)
        {
            var prefix = key + ":";
            var found = false;
            for (var i = 0; i < lines.Count; i += 2)
            {
                if (!lines[i].StartsWith(prefix, StringComparison.Ordinal)) continue;
                lines[i] = prefix + value;
                found = true;
            }
            if (found) continue;
            if (lines[^1].Length != 0) lines.Add(newline);
            else lines.RemoveAt(lines.Count - 1);
            lines.Add(prefix + value);
            lines.Add(newline);
            lines.Add("");
        }

        var replacement = string.Concat(lines);
        if (replacement == text) return false;
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".pcl3-options-{Guid.NewGuid():N}.tmp");
        var ownsTemp = false;
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, fileOptions))
            {
                ownsTemp = true;
                if (OperatingSystem.IsLinux() && exists) File.SetUnixFileMode(stream.SafeFileHandle, File.GetUnixFileMode(path) & (UnixFileMode)0x1ff);
                if (hasBom) stream.Write(Encoding.UTF8.Preamble);
                stream.Write(encoding.GetBytes(replacement));
                stream.Flush(flushToDisk: true);
            }
            InstanceBackupStore.RequireSafePath(path);
            File.Move(temp, path, overwrite: exists);
            ownsTemp = false;
            return true;
        }
        finally
        {
            if (ownsTemp) File.Delete(temp);
        }
    }
}
