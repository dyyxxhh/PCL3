using System.Text.Json;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class LauncherProcessTests
{
    [TestMethod]
    public async Task ClasspathUsesMavenCoordinatesAndInheritedJarId()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pcl-classpath-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions", "profile"));
        var java = Path.Combine(root, "java");
        var capture = Path.Combine(root, "args.txt");
        await File.WriteAllTextAsync(java, "#!/bin/sh\nprintf '%s\\n' \"$@\" > '" + capture + "'\n");
        File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var metadata = new JsonObject
        {
            ["id"] = "profile",
            ["jar"] = "base-game",
            ["mainClass"] = "example.Main",
            ["libraries"] = new JsonArray(
                new JsonObject { ["name"] = "com.example:sample:1.2.3" },
                new JsonObject { ["name"] = "com.example:forge:2.0:client" },
                new JsonObject { ["name"] = "org.example:custom:2.0", ["downloads"] = new JsonObject { ["artifact"] = new JsonObject { ["path"] = "custom/path.jar" } } }),
            ["arguments"] = new JsonObject { ["jvm"] = new JsonArray("-Djava.library.path=${natives_directory}", "-cp", "${classpath}"), ["game"] = new JsonArray() }
        };
        await File.WriteAllTextAsync(Path.Combine(root, "versions", "profile", "profile.json"), metadata.ToJsonString());
        try
        {
            using var process = await new MinecraftInstaller().LaunchAsync("profile", new MinecraftLaunchOptions(root, java, "Steve", "123", "token", "legacy"));
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode);
            var args = await File.ReadAllLinesAsync(capture);
            var classpath = args[Array.IndexOf(args, "-cp") + 1].Split(Path.PathSeparator);
            CollectionAssert.Contains(classpath, Path.Combine(root, "libraries", "com", "example", "sample", "1.2.3", "sample-1.2.3.jar"));
            CollectionAssert.Contains(classpath, Path.Combine(root, "libraries", "com", "example", "forge", "2.0", "forge-2.0-client.jar"));
            CollectionAssert.Contains(args, "-Djava.library.path=" + Path.Combine(root, "versions", "base-game", "natives"));
            CollectionAssert.Contains(classpath, Path.Combine(root, "libraries", "custom", "path.jar"));
            CollectionAssert.Contains(classpath, Path.Combine(root, "versions", "base-game", "base-game.jar"));
            Assert.IsFalse(classpath.Contains(Path.Combine(root, "versions", "profile", "profile.jar")));
        }
        finally { Directory.Delete(root, true); }
    }

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
