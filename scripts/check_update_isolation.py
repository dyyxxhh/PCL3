#!/usr/bin/env python3
"""Static regression checks: launcher updates must stay within this fork."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Plain Craft Launcher 2"
CORE = ROOT / "PCL.Core"
UPDATES = APP / "Modules/Updates"
manager = (UPDATES / "UpdateManager.cs").read_text(encoding="utf-8-sig")
source = (UPDATES / "UpdatesGithubReleaseModel.cs").read_text(encoding="utf-8-sig")
assert re.findall(r"new (Updates\w+Model)\(", manager) == ["UpdatesGithubReleaseModel"]
assert 'https://api.github.com/repos/dyyxxhh/pcl3/releases' in source
assert '.Where(x => x["draft"]?.GetValue<bool>() == false)' in source
assert '.Where(x => x["prerelease"]?.GetValue<bool>() == false)' in source
assert 'asset["browser_download_url"]' in source
assert 'UpdateChannel.beta' not in source + manager
assert 'new LoaderDownload' in source
assert 'Update.Error.Sha256Mismatch' in manager
auto_update = manager[manager.index('public static void UpdateStart('):]
assert auto_update.index('remoteServer.IsLatest(') < auto_update.index('remoteServer.GetLatestVersion(')
assert 'return;' in auto_update[auto_update.index('remoteServer.IsLatest('):auto_update.index('remoteServer.GetLatestVersion(')]
for name in ('UpdatesMirrorChyanModel.cs', 'UpdatesMinioModel.cs', 'UpdatesRandomModel.cs'):
    assert not (UPDATES / name).exists(), name

for directory in (APP, CORE):
    for path in directory.rglob('*'):
        if path.suffix not in ('.cs', '.xaml') or {'bin', 'obj'} & set(path.parts):
            continue
        text = path.read_text(encoding='utf-8-sig')
        for forbidden in ('MirrorChyan', 'mirrorchyan', 'TextMirrorCDK', 'ComboSystemUpdateChannel',
                          'Config.Update.UpdateChannel', 'Setup.Update.Channel',
                          'PCL2-CE/releases', 'CE-Latest.exe', 'CEUpdateLog.md'):
            assert forbidden not in text, (path.relative_to(ROOT), forbidden)

page = APP / 'Pages/PageSetup/PageSetupUpdate.xaml'
ET.parse(page)
page_cs = page.with_suffix('.xaml.cs').read_text(encoding='utf-8-sig')
assert 'https://github.com/dyyxxhh/PCL3/releases/tag/v' in page_cs
assert 'ComboSystemUpdateMode' in page.read_text(encoding='utf-8-sig')
for path in (CORE / 'App/Localization/Languages').glob('*.xaml'):
    ET.parse(path)
print('GitHub-only update source / no channels or CDK: PASS')
