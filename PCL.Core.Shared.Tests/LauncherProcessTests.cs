using System.Text.Json;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class LauncherProcessTests
{
    [TestMethod]
    public async Task ThirdPartyAgentIsPassedBeforeMainClass()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pcl-launch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions", "test"));
        var java = Path.Combine(root, "java");
        var agent = Path.Combine(root, "authlib-injector.jar");
        var capture = Path.Combine(root, "args.txt");
        await File.WriteAllTextAsync(java, "#!/bin/sh\nprintf '%s\\n' \"$@\" > '" + capture + "'\n");
        File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(agent, "test");
        await File.WriteAllTextAsync(Path.Combine(root, "versions", "test", "test.json"), JsonSerializer.Serialize(new { id = "test", mainClass = "example.Main", arguments = new { jvm = new[] { "-cp", "${classpath}" }, game = new[] { "--username", "${auth_player_name}", "--userType", "${user_type}" } } }));
        try
        {
            using var process = await new MinecraftInstaller().LaunchAsync("test", new MinecraftLaunchOptions(root, java, "Steve", "123", "token", "legacy", agent, "https://example.org/api"));
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode);
            var args = await File.ReadAllLinesAsync(capture);
            Assert.AreEqual("-javaagent:" + agent + "=https://example.org/api", args[0]);
            Assert.IsTrue(Array.IndexOf(args, "example.Main") > Array.IndexOf(args, "-Dauthlibinjector.side=client"));
            Assert.IsTrue(args.Contains("Steve"));
        }
        finally { Directory.Delete(root, true); }
    }
}
