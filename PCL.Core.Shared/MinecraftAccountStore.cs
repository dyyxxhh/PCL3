using System.Text.Json;

namespace PCL.Core.Shared.Authentication;

public sealed record StoredMinecraftAccount(string UserName, string Uuid, string RefreshToken);

public static class MinecraftAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string AccountPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrWhiteSpace(configHome)
                ? configHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "pcl-linux", "microsoft-account.json");
        }
    }

    public static async Task<StoredMinecraftAccount?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = AccountPath;
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredMinecraftAccount>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task SaveAsync(StoredMinecraftAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.RefreshToken);
        var path = AccountPath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, account, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, path, true);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static Task DeleteAsync()
    {
        var path = AccountPath;
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
