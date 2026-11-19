using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared;

internal static class TorExpertBundle
{
    internal static Stream? Open() => typeof(TorExpertBundle).Assembly.GetManifestResourceStream("PCL.TorExpertBundle.tar.gz");

    internal static void Extract(Stream bundle, string destination)
    {
        using var gzip = new GZipStream(bundle, CompressionMode.Decompress);
        using var archive = new TarReader(gzip);
        var bytesRemaining = 160L * 1024 * 1024;
        while (archive.GetNextEntry() is { } entry)
        {
            var parts = entry.Name.Split('/');
            if (parts.Length < 2 || parts[0] is not ("tor" or "data" or "docs") ||
                parts.Any(part => part is "" or "." or ".." || part.Any(c => c is '\\' or ':' || char.IsControl(c))) ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null || entry.Length > bytesRemaining)
                throw new InvalidDataException("The embedded Tor runtime archive contains an invalid entry.");
            var path = Path.Combine(destination, Path.Combine(parts));
            InstanceBackupStore.RequireSafePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                entry.DataStream.CopyTo(output);
            bytesRemaining -= entry.Length;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                (entry.Mode & UnixFileMode.UserExecute));
        }
    }

    internal static string[][] GetBuiltinBridges(string root)
    {
        var config = Path.Combine(root, "tor", "pluggable_transports", "pt_config.json");
        if (!File.Exists(config)) return [];
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        return json.RootElement.GetProperty("bridges").EnumerateObject()
            .Select(group => TorNetworkService.ParseBridges(group.Value))
            .Where(bridges => bridges.Length > 0).ToArray();
    }
}
