#!/usr/bin/env python3
"""Fast static regression checks for the launcher fork's requested fixes."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Plain Craft Launcher 2"
CORE = ROOT / "PCL.Core"

metadata = json.loads((APP / "metadata.json").read_text(encoding="utf-8"))
version = metadata["version"]
full_version = version["base"] + (f'-{version["suffix"]}' if version["suffix"] else "")
assert re.fullmatch(r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?", full_version)

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
update_manager = (APP / "Modules/Updates/UpdateManager.cs").read_text(encoding="utf-8")
homepage = (APP / "Pages/PageLaunch/PageLaunchRight.xaml.cs").read_text(encoding="utf-8")
assert "ShowCEAnnounce();" not in (APP / "FormMain.xaml.cs").read_text(encoding="utf-8")
assert "AnnouncementService.Load();" not in update_manager
assert 'https://s3.pysio.online/pcl2-ce/apiv2/pages/announce.xaml' not in homepage
assert 'case 14:' in homepage and 'content = "";' in homepage
# 更新源必须是本仓库的 GitHub Releases，并按当前发布形态选择资产
assert 'new UpdatesGithubReleaseModel(BuildVariant.IsSelfContainedSingleFile)' in update_manager
# The easter egg belongs to Tools only, without stale global-button references.
assert "BtnExtraNeverClick" not in main_xaml
assert "BtnExtraNeverClick" not in (APP / "FormMain.xaml.cs").read_text(encoding="utf-8")
tools_xaml = ET.parse(APP / "Pages/PageTools/PageToolsTest.xaml").getroot()
never_click_buttons = tools_xaml.findall(".//*[@{http://schemas.microsoft.com/winfx/2006/xaml}Name='BtnNeverClick']")
assert len(never_click_buttons) == 1
assert never_click_buttons[0].get("Text") == "{loc:Loc Main.Extra.NeverClick.ToolTip}"
assert never_click_buttons[0].get("Click") == "BtnNeverClick_Click"
button_parents = {child: parent for parent in tools_xaml.iter() for child in parent}
launch_count_buttons = tools_xaml.findall(".//*[@Click='BtnLaunchCount_Click']")
assert len(launch_count_buttons) == 1
assert button_parents[never_click_buttons[0]] is button_parents[launch_count_buttons[0]]
assert button_parents[never_click_buttons[0]].tag.endswith("}WrapPanel")
assert "private void BtnNeverClick_Click(object sender, MouseButtonEventArgs e)" in (APP / "Pages/PageTools/PageToolsTest.xaml.cs").read_text(encoding="utf-8")
assert 'Visibility="Collapsed"' in (APP / "Pages/PageTools/PageToolsLeft.xaml").read_text(encoding="utf-8")
assert 'Visibility="Collapsed"' in (APP / "Pages/PageSetup/PageSetupLeft.xaml").read_text(encoding="utf-8")
assert 'id == FormMain.PageSubType.ToolsGameLink' in (APP / "Pages/PageTools/PageToolsLeft.xaml.cs").read_text(encoding="utf-8")
assert 'id == FormMain.PageSubType.SetupGameLink' in (APP / "Pages/PageSetup/PageSetupLeft.xaml.cs").read_text(encoding="utf-8")
for path in [CORE / "App/Paths.cs", CORE / "App/Essentials/RpcService.cs"]:
    assert path.exists(), path

# The download page hands the resolver a selected NeoForge or Cleanroom entry.
# Both loader kinds must be accepted regardless of whether the entry was supplied.
forgelike = (APP / "Pages/PageDownload/DownloadUi/ForgelikeLoader.cs").read_text(encoding="utf-8-sig")
resolver = forgelike.split("private static GameDownload.DlForgelikeEntry? _ResolveForgelikeInfo(", 1)[1]
resolver = resolver.split("private static GameDownload.DlForgelikeEntry _FindForgelikeEntry(", 1)[0]
for kind in ("NeoForge", "Cleanroom"):
    assert re.search(rf"case GameDownload\.DlForgelikeEntry\.ForgelikeType\.{kind}:\s+"
                     rf"info \?\?= new GameDownload\.Dl{kind}ListEntry\(", resolver), kind

for area, item, page, subtype in (("Setup", "ItemTor", "PageSetupTor", "SetupTor"),
                                  ("Tools", "ItemTorDownload", "PageToolsTorDownload", "ToolsTorDownload")):
    folder = APP / f"Pages/Page{area}"
    nav = ET.parse(folder / f"Page{area}Left.xaml")
    entry = nav.find(f".//*[@{{http://schemas.microsoft.com/winfx/2006/xaml}}Name='{item}']")
    assert entry is not None and entry.get("Type") == "RadioBox" and entry.get("Check") == "PageCheck"
    assert re.search(rf"\b{subtype} = {entry.get('Tag')},", (APP / "FormMain.xaml.cs").read_text(encoding="utf-8-sig"))
    code = (folder / f"Page{area}Left.xaml.cs").read_text(encoding="utf-8-sig")
    assert f"case FormMain.PageSubType.{subtype}:" in code and f"new {page}()" in code
    assert ET.parse(folder / f"{page}.xaml").getroot().tag.endswith("}MyPageRight")
assert not (APP / "Pages/PageSetup/TorSettingsDialog.cs").exists()
manual_tor = (APP / "Pages/PageTools/PageToolsTorDownload.xaml.cs").read_text(encoding="utf-8-sig")
assert "TorNetworkService.StartAsync" in manual_tor and "CreateHttpClient(allowAutoRedirect: false, useSystemProxy: false)" in manual_tor
assert "WindowsTorSession.IsPublic" not in manual_tor

print("branding/account/icon/game download/Tor navigation regression checks: PASS")
