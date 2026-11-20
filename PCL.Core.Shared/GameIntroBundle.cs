using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared;

/// <summary>Safely expands authenticated game-intro payloads for local game-process consumption.</summary>
public static class GameIntroBundle
{
    private const string Clip = "PCL3_LaunchIntro.mp4";
    private const string ClipHash = "e46a7af3098ee66b91d57a9f72b44153a65fb18d6a9f37bb8d63ea118b942692";
    private const string Agent = "PCL3_LaunchIntroAgent.jar";
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.DoNotVerify);
    private static string Root => Path.Combine(LocalData, "PCL3", "GameIntro");

    public static bool TryPrepare(out string mediaPath, out string agentPath)
    {
        mediaPath = "";
        agentPath = "";
        if (!Path.IsPathFullyQualified(LocalData)) return false;
        try
        {
            var digest = Encoding.ASCII.GetString(ReadPayload(Agent + ".sha256", 65)).TrimEnd('\n');
            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid game-intro agent digest.");
            using var media = ReadResource(Clip);
            using var bridge = ReadResource(Agent);
            if (bridge.Length is < 20 or > 1024 * 1024) return false;
            InstanceBackupStore.RequireSafePath(Root);
            Directory.CreateDirectory(Root);
            InstanceBackupStore.RequireSafePath(Root);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            mediaPath = Materialize(media, Path.Combine(Root, Clip), 5_209_693, ClipHash);
            agentPath = Materialize(bridge, Path.Combine(Root, $"{digest.ToLowerInvariant()}.jar"), bridge.Length, digest);
            InstanceBackupStore.RequireSafePath(mediaPath);
            InstanceBackupStore.RequireSafePath(agentPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            mediaPath = "";
            agentPath = "";
            return false;
        }
    }

    private static string Materialize(Stream input, string destination, long requiredLength, string expectedHash)
    {
        InstanceBackupStore.RequireSafePath(destination);
        if (IsTrusted(destination, requiredLength, expectedHash)) return destination;
        var temporary = Path.Combine(Root, $".{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                ownsTemporary = true;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    total += read;
                    if (total > requiredLength)
                        throw new InvalidDataException("The game-intro resource exceeds its manifest length.");
                    digest.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                if (total != requiredLength)
                    throw new InvalidDataException("The game-intro resource length does not match its manifest.");
                if (!CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(expectedHash)))
                    throw new InvalidDataException("The game-intro resource failed SHA-256 verification.");
                output.Flush(flushToDisk: true);
            }
            InstanceBackupStore.RequireSafePath(destination);
            File.Move(temporary, destination, overwrite: File.Exists(destination));
            ownsTemporary = false;
            return destination;
        }
        finally
        {
            if (ownsTemporary) File.Delete(temporary);
        }
    }

    private static bool IsTrusted(string path, long exactLength, string expectedHash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != exactLength) return false;
        using var stream = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(expectedHash));
    }

    private static Stream ReadResource(string name) => typeof(GameIntroBundle).Assembly.GetManifestResourceStream(name)
        ?? throw new FileNotFoundException("Missing embedded game-intro resource.", name);

    private static byte[] ReadPayload(string resource, int maximum)
    {
        using var stream = ReadResource(resource);
        using var output = new MemoryStream();
        var buffer = new byte[maximum + 1];
        while (output.Length <= maximum)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length));
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (output.Length > maximum) throw new InvalidDataException("Game-intro metadata is oversized.");
        return output.ToArray();
    }
}
