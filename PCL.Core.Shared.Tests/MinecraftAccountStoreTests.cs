using PCL.Core.Shared.Authentication;

namespace PCL.Core.Shared.Tests;

[TestClass]
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
}
