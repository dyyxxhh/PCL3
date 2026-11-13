using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class ForgeLikeLoaderInstallerTests
{
    [TestMethod]
    [DataRow("neoforge", "1.20.4", "20.4.200")]
    [DataRow("neoforge", "1.20.1", "1.20.1-47.1.106")]
    [DataRow("forge", "1.20.1", "1.20.1-47.4.0")]
    public async Task InstallsWithoutLaunchingMinecraftFirst(string loader, string gameVersion, string mavenVersion)
        => await InstallWithLauncherProfilesAsync(loader, gameVersion, mavenVersion, null);

    [TestMethod]
    public async Task PreservesExistingLauncherProfiles()
        => await InstallWithLauncherProfilesAsync("neoforge", "1.20.4", "20.4.200",
            "{\n  \"profiles\": {\"Existing\": {\"name\": \"My profile\"}}, \"selectedProfile\": \"Existing\", \"custom\": true\n}\n");

    private static async Task InstallWithLauncherProfilesAsync(string loader, string gameVersion, string mavenVersion, string? existingProfiles)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The installer process fixture requires a POSIX shell.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "pcl-forgelike-test-" + Guid.NewGuid().ToString("N"), "game with spaces");
        var fixtureRoot = Path.GetDirectoryName(root)!;
        var launchId = gameVersion + "-" + loader + "-" + mavenVersion;
        var installerBytes = Encoding.UTF8.GetBytes("verified installer fixture");
        var checksum = Convert.ToHexString(SHA1.HashData(installerBytes));
        using var http = new HttpClient(new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("maven-metadata.xml", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent($"<metadata><versioning><versions><version>{mavenVersion}</version></versions></versioning></metadata>") };
            if (path.EndsWith(".sha1", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent(checksum) };
            if (path.EndsWith("-installer.jar", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(installerBytes) };
            return new(HttpStatusCode.NotFound);
        }));
        try
        {
            var vanillaDirectory = Path.Combine(root, "versions", gameVersion);
            Directory.CreateDirectory(vanillaDirectory);
            await File.WriteAllTextAsync(Path.Combine(vanillaDirectory, gameVersion + ".json"),
                new JsonObject
                {
                    ["id"] = gameVersion,
                    ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
                    ["mainClass"] = "net.minecraft.client.main.Main",
                    ["libraries"] = new JsonArray(),
                    ["arguments"] = new JsonObject { ["jvm"] = new JsonArray(), ["game"] = new JsonArray() }
                }.ToJsonString());
            await File.WriteAllTextAsync(Path.Combine(vanillaDirectory, gameVersion + ".jar"), "vanilla fixture");
            var profilesPath = Path.Combine(root, "launcher_profiles.json");
            if (existingProfiles is not null) await File.WriteAllTextAsync(profilesPath, existingProfiles);
            else Assert.IsFalse(File.Exists(profilesPath));

            var javaDirectory = Path.Combine(root, "runtime", "fixture", "bin");
            Directory.CreateDirectory(javaDirectory);
            var javaPath = Path.Combine(javaDirectory, "java");
            // Match the official installer's first-run gate at the real process boundary.
            await File.WriteAllTextAsync(javaPath, $$"""
                #!/bin/sh
                if [ "$1" = "-version" ]; then
                    echo 'openjdk version "17.0.1"' >&2
                    exit 0
                fi
                test "$1" = "-jar" && test "$3" = "--installClient" || exit 2
                cd "$4" || exit 3
                if [ ! -f launcher_profiles.json ]; then
                    echo 'The directory is missing a launcher profile. Please run the minecraft launcher first.' >&2
                    exit 1
                fi
                mkdir -p 'versions/{{launchId}}'
                printf '%s' '{"id":"{{launchId}}","inheritsFrom":"{{gameVersion}}","mainClass":"example.Loader","libraries":[]}' > 'versions/{{launchId}}/{{launchId}}.json'
                """);
            File.SetUnixFileMode(javaPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await new ModLoaderInstaller(http).InstallAsync(gameVersion, loader, mavenVersion, root);

            Assert.AreEqual(launchId, result.LaunchVersion);
            var merged = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "versions", launchId, launchId + ".json")))!;
            Assert.AreEqual(gameVersion, merged["jar"]!.GetValue<string>());
            Assert.AreEqual("example.Loader", merged["mainClass"]!.GetValue<string>());
            var profilesText = await File.ReadAllTextAsync(profilesPath);
            if (existingProfiles is not null)
            {
                Assert.AreEqual(existingProfiles, profilesText, "Existing official/third-party launcher settings must be preserved byte for byte.");
            }
            else
            {
                var profiles = JsonNode.Parse(profilesText)!;
                Assert.AreEqual("PCL", profiles["selectedProfile"]!.GetValue<string>());
                Assert.AreEqual("PCL", profiles["profiles"]!["PCL"]!["name"]!.GetValue<string>());
                Assert.AreEqual("latest-release", profiles["profiles"]!["PCL"]!["lastVersionId"]!.GetValue<string>());
                Assert.IsNotNull(profiles["clientToken"]);
            }
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
