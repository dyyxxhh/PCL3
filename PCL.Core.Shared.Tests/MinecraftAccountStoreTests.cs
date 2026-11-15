using System.Text.Json;
using PCL.Core.Shared.Authentication;

namespace PCL.Core.Shared.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MinecraftAccountStoreTests
{
    [TestMethod]
    public async Task SaveStoresRefreshTokenWithOwnerOnlyPermissions()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = Path.Combine(Path.GetTempPath(), "pcl-account-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
        try
        {
            var expected = new StoredMinecraftAccount("TestUser", "uuid", "refresh-token");
            await MinecraftAccountStore.SaveAsync(expected);
            Assert.AreEqual(Path.Combine(root, "mcm", "microsoft-account.json"), MinecraftAccountStore.AccountPath);
            var actual = await MinecraftAccountStore.LoadAsync();
            Assert.AreEqual(expected, actual);
            if (OperatingSystem.IsLinux())
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(MinecraftAccountStore.AccountPath));
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(MinecraftAccountStore.AccountPath)!));
            }
            await MinecraftAccountStore.DeleteAsync();
            Assert.IsFalse(File.Exists(MinecraftAccountStore.AccountPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LegacyAccountsRemainReadableAndLogoutRemovesBothLocations()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = Path.Combine(Path.GetTempPath(), "mcm-account-compatibility-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
        try
        {
            var legacy = Path.Combine(root, "pcl-linux");
            Directory.CreateDirectory(legacy);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var oldAccount = new StoredMinecraftAccount("OldUser", "old-uuid", "mock-old-token");
            var oldProfile = new LauncherProfile("microsoft", "OldUser", "old-uuid");
            await File.WriteAllTextAsync(Path.Combine(legacy, "microsoft-account.json"), JsonSerializer.Serialize(oldAccount, options));
            await File.WriteAllTextAsync(Path.Combine(legacy, "selected-profile.json"), JsonSerializer.Serialize(oldProfile, options));
            Assert.AreEqual(oldAccount, await MinecraftAccountStore.LoadAsync());
            Assert.AreEqual(oldProfile, await LauncherProfileStore.LoadAsync());

            var newAccount = new StoredMinecraftAccount("NewUser", "new-uuid", "mock-new-token");
            var newProfile = new LauncherProfile("microsoft", "NewUser", "new-uuid");
            await MinecraftAccountStore.SaveAsync(newAccount);
            await LauncherProfileStore.SaveAsync(newProfile);
            Assert.AreEqual(Path.Combine(root, "mcm", "selected-profile.json"), LauncherProfileStore.PathName);
            Assert.AreEqual(newAccount, await MinecraftAccountStore.LoadAsync());
            Assert.AreEqual(newProfile, await LauncherProfileStore.LoadAsync());
            Assert.IsTrue(File.Exists(Path.Combine(legacy, "microsoft-account.json")));
            if (OperatingSystem.IsLinux())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(LauncherProfileStore.PathName));

            await MinecraftAccountStore.DeleteAsync();
            LauncherProfileStore.Delete();
            Assert.IsNull(await MinecraftAccountStore.LoadAsync());
            Assert.IsNull(await LauncherProfileStore.LoadAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
