using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared;

/// <summary>Downloads and safely expands authenticated game-intro payloads for local game-process consumption.</summary>
public static class GameIntroBundle
{
    private const string Clip = "PCL3_LaunchIntro.mp4";
    private const string ClipUrl = "https://github.com/dyyxxhh/PCL3/releases/latest/download/PCL3_LaunchIntro.mp4";
    private const string ClipHash = "84bc0cae7a5b47adb7726e8a263da52ff907f3841884e97905d752a25ca41029";
    private const long ClipLength = 14_408_526;
    private const string Agent = "PCL3_LaunchIntroAgent.jar";
    private const string AgentUrl = "https://github.com/dyyxxhh/PCL3/releases/latest/download/PCL3_LaunchIntroAgent.jar";
    private const string AgentHash = "ddaf62b2f9152e65ec5ab4e72d4683a054777370d2b35791ef639532867d287f";
    private const long AgentLength = 183_710;
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolderOption.DoNotVerify);
    private static string Root => Path.Combine(LocalData, "PCL3", "GameIntro");

    public static bool TryPrepare(out string mediaPath, out string agentPath, bool allowDownload = true)
    {
        mediaPath = "";
        agentPath = "";
        if (!Path.IsPathFullyQualified(LocalData)) return false;
        try
        {
            InstanceBackupStore.RequireSafePath(Root);
            Directory.CreateDirectory(Root);
            InstanceBackupStore.RequireSafePath(Root);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var media = Path.Combine(Root, ClipHash + ".mp4");
            var agent = Path.Combine(Root, $"{AgentHash}.jar");
            if (!allowDownload)
            {
                if (!IsTrusted(media, ClipLength, ClipHash) || !IsTrusted(agent, AgentLength, AgentHash)) return false;
                mediaPath = media;
                agentPath = agent;
            }
            else
            {
                mediaPath = MaterializeRemote(ClipUrl, media, ClipLength, ClipHash);
                agentPath = MaterializeRemote(AgentUrl, agent, AgentLength, AgentHash);
            }
            InstanceBackupStore.RequireSafePath(mediaPath);
            InstanceBackupStore.RequireSafePath(agentPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or
            HttpRequestException or OperationCanceledException or NotSupportedException)
        {
            mediaPath = "";
            agentPath = "";
            return false;
        }
    }

    private static string MaterializeRemote(string url, string destination, long requiredLength, string expectedHash)
    {
        InstanceBackupStore.RequireSafePath(destination);
        if (IsTrusted(destination, requiredLength, expectedHash)) return destination;
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var response = client.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, deadline.Token).GetAwaiter().GetResult();
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"Game-intro download returned {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength is { } length && length != requiredLength)
            throw new InvalidDataException("The game-intro download length does not match its manifest.");
        using var input = response.Content.ReadAsStreamAsync(deadline.Token).GetAwaiter().GetResult();
        return Materialize(input, destination, requiredLength, expectedHash, deadline.Token);
    }

    private static string Materialize(Stream input, string destination, long requiredLength, string expectedHash,
        CancellationToken cancellationToken = default)
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
                    var read = input.ReadAsync(buffer, cancellationToken).GetAwaiter().GetResult();
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
}
