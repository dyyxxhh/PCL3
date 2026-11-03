namespace PCL.Core.Shared;

/// <summary>Rules for one-time configuration migrations across launcher version boundaries.</summary>
public static class LauncherMigrationRules
{
    /// <summary>The legacy homepage migration applies only when crossing its original version boundary.</summary>
    public static bool ShouldResetLegacyHomepage(int previousVersion, int currentVersion, int selectedPreset)
        => previousVersion > 0 && previousVersion < 521 && currentVersion >= 521 && selectedPreset >= 3;
}
