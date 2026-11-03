#!/usr/bin/env python3
"""Static regression checks: launcher updates must stay within this fork."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Plain Craft Launcher 2"
CORE = ROOT / "PCL.Core"
UPDATES = APP / "Modules/Updates"
manager = (UPDATES / "UpdateManager.cs").read_text(encoding="utf-8-sig")
source = (UPDATES / "UpdatesGithubReleaseModel.cs").read_text(encoding="utf-8-sig")
assert re.findall(r"new (Updates\w+Model)\(", manager) == ["UpdatesGithubReleaseModel"]
assert 'https://github.com/dyyxxhh/PCL3' in source
assert '"/releases/latest"' in source
assert 'PCL3_Release_{architecture}{suffix}' in source
assert 'PCL-Community/PCL-CE/releases' not in source
assert 'Requester.FetchJson' not in source
assert 'GetAssetName(arch, ".exe")' in source
assert 'GetAssetName(arch, ".sha256")' in source
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
metadata = json.loads((APP / 'metadata.json').read_text(encoding='utf-8'))
release_version = metadata['version']['base']
assert re.fullmatch(r'\d+\.\d+\.\d+', release_version), release_version
project = ET.parse(APP / 'Plain Craft Launcher 2.csproj').getroot()
for name in ('AssemblyVersion', 'FileVersion'):
    assert project.findtext(f'.//{name}') == release_version + '.0', name
workflow = (ROOT / '.github/workflows/release-publish.yml').read_text(encoding='utf-8')
linux_updater = (ROOT / 'PCL.Linux.Cli/LinuxSelfUpdater.cs').read_text(encoding='utf-8')
assert '-p:PclReleaseVersion="${{ inputs.version }}"' in workflow
assert 'PCL3_${{ inputs.configuration }}_linux-x64.tar.gz' in workflow
assert 'PCL3_Release_linux-x64.tar.gz' in linux_updater
print('GitHub-only update source / no channels or CDK: PASS')
