using System.Text;
using PCL.Core.Shared.Minecraft;

namespace PCL.Core.Shared.Tests;

[TestClass]
public sealed class GameStartupOptionsTests
{
    [TestMethod]
    [DataRow("2011-11-18", "none")]
    [DataRow("2012-01-12", "zh_CN")]
    [DataRow("2016-06-08", "zh_CN")]
    [DataRow("2016-06-09", "zh_cn")]
    public void LanguageMatchesWindowsReleaseBoundaries(string date, string expected)
    {
        Assert.AreEqual(expected, GameStartupOptionsFile.ResolveLanguage("ZH-cn", DateOnly.ParseExact(date, "yyyy-MM-dd").ToDateTime(TimeOnly.MinValue)));
    }

    [TestMethod]
    public void FirstRunUpdatesLanguageAndPreservesOtherSettingsAndEncoding()
    {
        using var directory = CreateDirectory();
        var path = Path.Combine(directory.Path, "options.txt");
        File.WriteAllText(path, "lang:en_us\r\nkey:namespace:value\r\nfullscreen:true\r\n", new UTF8Encoding(true));
        Assert.IsTrue(GameStartupOptionsFile.Apply(directory.Path, new(WindowMode: GameStartupWindowMode.Custom), new DateTime(2024, 7, 1)));
        CollectionAssert.AreEqual(Encoding.UTF8.Preamble.ToArray(), File.ReadAllBytes(path)[..3]);
        Assert.AreEqual("lang:zh_cn\r\nkey:namespace:value\r\nfullscreen:false\r\nforceUnicodeFont:true\r\n", File.ReadAllText(path));
        Assert.IsFalse(GameStartupOptionsFile.Apply(directory.Path, new(WindowMode: GameStartupWindowMode.Custom), new DateTime(2024, 7, 1)));
    }

    [TestMethod]
    public void KeepsExistingSaveLanguageAndFontPreference()
    {
        using var directory = CreateDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "saves"));
        var path = Path.Combine(directory.Path, "options.txt");
        File.WriteAllText(path, "lang:fr-FR\nforceUnicodeFont:false");
        GameStartupOptionsFile.Apply(directory.Path, new(), null);
        Assert.AreEqual("lang:fr_fr\nforceUnicodeFont:false", File.ReadAllText(path));
    }

    [TestMethod]
    public void AppliesWindowModeToYosbrEvenWhenLanguageAutomationIsDisabled()
    {
        using var directory = CreateDirectory();
        var yosbr = Directory.CreateDirectory(Path.Combine(directory.Path, "config", "yosbr"));
        var path = Path.Combine(yosbr.FullName, "options.txt");
        File.WriteAllText(path, "fullscreen:true\nlang:fr_fr\n");
        Assert.IsTrue(GameStartupOptionsFile.Apply(directory.Path,
            new(AutoLanguage: false, WindowMode: GameStartupWindowMode.Maximized), null));
        Assert.AreEqual("fullscreen:false\nlang:fr_fr\n", File.ReadAllText(path));
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "options.txt")));
    }

    [TestMethod]
    public void UpdatesYosbrWithoutShadowingItsTemplate()
    {
        using var directory = CreateDirectory();
        var yosbr = Directory.CreateDirectory(Path.Combine(directory.Path, "config", "yosbr"));
        var path = Path.Combine(yosbr.FullName, "options.txt");
        File.WriteAllText(path, "lang:en_us\ncustom:true\n");
        GameStartupOptionsFile.Apply(directory.Path, new(WindowMode: GameStartupWindowMode.Fullscreen), null);
        Assert.IsFalse(File.Exists(Path.Combine(directory.Path, "options.txt")));
        Assert.AreEqual("lang:zh_cn\ncustom:true\nforceUnicodeFont:true\nfullscreen:true\n", File.ReadAllText(path));
    }

    [TestMethod]
    public void MaximizedWindowModeStartsWindowedForTheWindowManager()
    {
        using var directory = CreateDirectory();
        var options = new GameStartupOptions(AutoLanguage: false, WindowMode: GameStartupWindowMode.Maximized);
        Assert.IsTrue(GameStartupOptionsFile.Apply(directory.Path, options, null));
        Assert.AreEqual("fullscreen:false\n", File.ReadAllText(Path.Combine(directory.Path, "options.txt")));
    }

    [TestMethod]
    public void DetectsPersistedFullscreenWithoutChangingTheDefaultWindowMode()
    {
        using var directory = CreateDirectory();
        Assert.IsFalse(GameStartupOptionsFile.IsFullscreen(directory.Path));
        var path = Path.Combine(directory.Path, "options.txt");
        File.WriteAllText(path, "fullscreen:false\nfullscreen:true\n");
        Assert.IsTrue(GameStartupOptionsFile.IsFullscreen(directory.Path));
        Assert.IsFalse(GameStartupOptionsFile.Apply(directory.Path, new(AutoLanguage: false), null));
        Assert.IsTrue(GameStartupOptionsFile.IsFullscreen(directory.Path));
        File.WriteAllText(path, "fullscreen:false\n");
        Assert.IsFalse(GameStartupOptionsFile.IsFullscreen(directory.Path));
    }

    [TestMethod]
    public void DetectsFullscreenFromTheSameYosbrTemplateUsedByStartupOptions()
    {
        using var directory = CreateDirectory();
        var yosbr = Directory.CreateDirectory(Path.Combine(directory.Path, "config", "yosbr"));
        File.WriteAllText(Path.Combine(yosbr.FullName, "options.txt"), "fullscreen:true\n");
        Assert.IsTrue(GameStartupOptionsFile.IsFullscreen(directory.Path));
    }

    [TestMethod]
    public void TreatsUnsafeFullscreenOptionsPathAsUnknownAndSkipsOverlay()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("This test requires Linux symlink permissions."); return; }
        using var directory = CreateDirectory();
        var target = Path.Combine(directory.Path, "target.txt");
        File.WriteAllText(target, "fullscreen:false\n");
        File.CreateSymbolicLink(Path.Combine(directory.Path, "options.txt"), target);
        Assert.IsTrue(GameStartupOptionsFile.IsFullscreen(directory.Path));
    }

    [TestMethod]
    public void TurningOffLanguageLeavesExistingOptionsUntouched()
    {
        using var directory = CreateDirectory();
        var path = Path.Combine(directory.Path, "options.txt");
        Assert.IsFalse(GameStartupOptionsFile.Apply(directory.Path, new(AutoLanguage: false), null));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void RefusesToWriteThroughAnOptionsSymlink()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("This test requires Linux symlink permissions."); return; }
        using var directory = CreateDirectory();
        var target = Path.Combine(directory.Path, "target.txt");
        File.WriteAllText(target, "original");
        File.CreateSymbolicLink(Path.Combine(directory.Path, "options.txt"), target);
        Assert.ThrowsExactly<IOException>(() => GameStartupOptionsFile.Apply(directory.Path, new(), null));
        Assert.AreEqual("original", File.ReadAllText(target));
    }

    [TestMethod]
    [DataRow("1.21.1", null, null, "vanilla-1.21.1")]
    [DataRow("1.21.1", "vanilla", null, "vanilla-1.21.1")]
    [DataRow("1.21.1", "fabric", "0.16.14", "fabric-1.21.1-0.16.14")]
    [DataRow("1.21.1", "forge", "52.1.20", "forge-1.21.1-52.1.20")]
    [DataRow("1.21.1", "neoforge", "21.1.172", "neoforge-1.21.1-21.1.172")]
    [DataRow("1.21.1", "forge", "52.1.19", null)]
    [DataRow("1.21.2", "fabric", "0.16.14", null)]
    public void SelectsOnlyEvidenceBackedIntroProfiles(string version, string? loader, string? loaderVersion, string? expected)
        => Assert.AreEqual(expected, GameIntroLaunch.SelectProfile(version, loader, loaderVersion));

    [TestMethod]
    [DataRow("vanilla-1.21.1", "net.minecraft.client.main.Main", true)]
    [DataRow("fabric-1.21.1-0.16.14", "net.fabricmc.loader.impl.launch.knot.KnotClient", true)]
    [DataRow("forge-1.21.1-52.1.20", "net.minecraftforge.bootstrap.ForgeBootstrap", true)]
    [DataRow("neoforge-1.21.1-21.1.172", "cpw.mods.bootstraplauncher.BootstrapLauncher", true)]
    [DataRow("forge-1.21.1-52.1.20", "net.minecraft.client.main.Main", false)]
    public void RequiresKnownLaunchEntrypoint(string profile, string mainClass, bool expected)
        => Assert.AreEqual(expected, GameIntroLaunch.MatchesEntryPoint(profile, mainClass));

    private static ScratchDirectory CreateDirectory() => new(Directory.CreateTempSubdirectory("pcl3-startup-tests-").FullName);

    private sealed record ScratchDirectory(string Path) : IDisposable
    {
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
