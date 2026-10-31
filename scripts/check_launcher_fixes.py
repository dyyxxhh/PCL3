#!/usr/bin/env python3
"""Fast static regression checks for the launcher fork's requested fixes."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Plain Craft Launcher 2"
CORE = ROOT / "PCL.Core"

profile = (APP / "Modules/Minecraft/ProfileUi.cs").read_text(encoding="utf-8")
profile_vm = (APP / "Pages/PageLaunch/PageLoginProfileViewModel.cs").read_text(encoding="utf-8")
profile_xaml = (APP / "Pages/PageLaunch/PageLoginProfile.xaml").read_text(encoding="utf-8")
main_xaml = (APP / "FormMain.xaml").read_text(encoding="utf-8")
app_cs = (APP / "Application.xaml.cs").read_text(encoding="utf-8")

assert "public static bool CanCreateOtherProfile()" in profile
assert "return true;" in profile[profile.index("public static bool CanCreateOtherProfile()"):profile.index("public static async Task CreateProfileAsync")]
assert "ShowMicrosoftHint" not in profile_vm
assert "RequireMicrosoftHint" not in profile_xaml
assert 'Title="PCL₃ - 三氯化磷启动器 "' in main_xaml
assert '<Border x:Name="CELogo" Grid.Column="1" Visibility="Collapsed"' in main_xaml
assert 'x:Name="PclThreeLogo"' in main_xaml
assert 'main.PclThreeLogo.Visibility = ToVisibility(value != 0 && value != 4);' in (APP / "Modules/Base/Setup/UI.cs").read_text(encoding="utf-8")
assert 'new SplashScreen(@"Images\\icon.png")' in app_cs

# These are protocol/storage identifiers and must not be changed to display branding.
for path in [CORE / "App/Paths.cs", CORE / "App/Essentials/RpcService.cs"]:
    assert path.exists(), path

print("branding/account/icon regression checks: PASS")
