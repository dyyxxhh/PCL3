using System.Text.Json;

namespace PCL.Core.Shared.Authentication;

public static class LauncherProfileStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string PathName => Path.Combine(Path.GetDirectoryName(MinecraftAccountStore.AccountPath)!, "selected-profile.json");

    public static async Task<LauncherProfile?> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(PathName)) return null;
        await using var stream = File.OpenRead(PathName);
        return await JsonSerializer.DeserializeAsync<LauncherProfile>(stream, Options, token);
    }

    public static async Task SaveAsync(LauncherProfile profile, CancellationToken token = default)
    {
        var path = PathName;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, profile, Options, token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Delete() { if (File.Exists(PathName)) File.Delete(PathName); }
}
