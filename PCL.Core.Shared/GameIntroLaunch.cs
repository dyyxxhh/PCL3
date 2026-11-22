namespace PCL.Core.Shared.Minecraft;

/// <summary>Private per-launch parameters connecting the verified JVM bridge to a local playback host.</summary>
public sealed record GameIntroLaunch(string AgentPath, string Endpoint, string Profile = "vanilla-1.21.1")
{
    public static bool IsEnabled(string? setting) => setting == "1";

    public static string? SelectProfile(string? gameVersion, string? loader, string? loaderVersion) =>
        (gameVersion, loader, loaderVersion) switch
        {
            ("1.21.1", null or "vanilla", null) => "vanilla-1.21.1",
            ("1.21.11", null or "vanilla", null) => "vanilla-1.21.11",
            ("26.3", null or "vanilla", null) => "vanilla-26.3",
            ("1.21.1", "fabric", "0.16.14") => "fabric-1.21.1-0.16.14",
            ("1.21.1", "forge", "52.1.20") => "forge-1.21.1-52.1.20",
            ("1.21.1", "neoforge", "21.1.172") => "neoforge-1.21.1-21.1.172",
            _ => null
        };

    public static bool MatchesEntryPoint(string profile, string? mainClass) => (profile, mainClass) switch
    {
        ("vanilla-1.21.1" or "vanilla-1.21.11" or "vanilla-26.3", "net.minecraft.client.main.Main") => true,
        ("fabric-1.21.1-0.16.14", "net.fabricmc.loader.impl.launch.knot.KnotClient") => true,
        ("forge-1.21.1-52.1.20", "net.minecraftforge.bootstrap.ForgeBootstrap") => true,
        ("neoforge-1.21.1-21.1.172", "cpw.mods.bootstraplauncher.BootstrapLauncher") => true,
        _ => false
    };
}
