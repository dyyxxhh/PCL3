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
assets = (UPDATES / "ReleaseAssets.cs").read_text(encoding="utf-8-sig")
assert re.findall(r"new (Updates\w+Model)\(", manager) == ["UpdatesGithubReleaseModel"]
# 自带运行时的发布版必须更新到同名变体资产，不能替换成框架依赖版
assert 'BuildVariant.IsSelfContainedSingleFile' in manager
assert 'https://github.com/dyyxxhh/PCL3' in assets
assert '"/releases/latest"' in assets
assert 'PCL3_{configuration}_{arch}' in assets
assert 'PCL-Community/PCL-CE/releases' not in source + assets
assert 'Requester.FetchJson' not in source
assert 'GetAssetName(arch, ".exe")' in source
assert 'GetAssetName(arch, ".sha256")' in source
assert 'ReleaseAssets.GetAssetName(arch, "Release", selfContained, suffix)' in source
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
# So a slow connection cannot delay visible feedback or register the same task twice.
click = page_cs.split('private async void BtnUpdate_Click(', 1)[-1].split('private async void BtnChangelogDetail_Click(', 1)[0]
assert 'Setup.Update.Preparing' in click, '下载并安装：等待网络时按钮没有立即显示正在准备'
start = 'UpdateManager.StartDownload(version, UpdateEnums.UpdateType.UpdateNow)'
assert click.index('BtnUpdate.IsEnabled = false') < click.index(start), '下载并安装：仍可重复点击'
assert click.index('Setup.Update.Preparing') < click.index(start), '下载并安装：反馈晚于下载开始'
manual_download = manager.split('internal static void StartDownload(', 1)[1].split('private static void ReportUpdateFailure(', 1)[0]
assert 'remoteServer.' not in manual_download, '下载并安装：重复联网检查会延迟下载任务'
assert 'new DownloadFile([version.DownloadUrl]' in manual_download
assert 'Loading.LoaderTaskbarAdd(loader)' in manual_download
assert 'BtnUpdate.IsEnabled = _updateInfo is not null' in page_cs, '更新失败后必须可重试'
assert 'Thread.Sleep(200)' not in page_cs
for path in (CORE / 'App/Localization/Languages').glob('*.xaml'):
    ET.parse(path)
metadata = json.loads((APP / 'metadata.json').read_text(encoding='utf-8'))
release_version = metadata['version']['base']
assert re.fullmatch(r'\d+\.\d+\.\d+', release_version), release_version
project = ET.parse(APP / 'Plain Craft Launcher 2.csproj').getroot()
for name in ('AssemblyVersion', 'FileVersion'):
    assert project.findtext(f'.//{name}') == release_version + '.0', name
workflow = (ROOT / '.github/workflows/release-publish.yml').read_text(encoding='utf-8')
# 主发布保持框架依赖版；自带运行时的压缩单文件版作为 ..._selfcontained 资产单独发布
reusable = (ROOT / '.github/workflows/reusable-build.yml').read_text(encoding='utf-8')
assert '--self-contained true' in reusable
assert '--no-self-contained' in reusable
assert '-p:EnableCompressionInSingleFile=true' in reusable
assert 'PCL3_${{ inputs.configuration }}_${{ inputs.architecture }}' in reusable
# 体积回归：框架依赖版即使内置 Tor 也应小于 70 MB。
assert 'test "$size" -lt 70000000' in reusable
assert 'test "$size" -lt 160000000' in reusable
assert 'variant: selfcontained' in (ROOT / '.github/workflows/build-test.yml').read_text(encoding='utf-8')
assert 'variant: selfcontained' in workflow
assert 'asset_suffix: _selfcontained' in workflow
assert 'sha256sum' in workflow
assert workflow.count('sigstore/gh-action-sigstore-python') == 1
variant = (APP / 'Modules/Base/Common/BuildVariant.cs').read_text(encoding='utf-8-sig')
assert 'APP_CONTEXT_DEPS_FILES' in variant
assert 'typeof(object).Assembly.Location' in variant
provider = (APP / 'Modules/Minecraft/Offline/WindowsOfflineLauncherProvider.cs').read_text(encoding='utf-8-sig')
assert 'BuildVariant.IsSelfContainedSingleFile' in provider
assert 'ReleaseAssets.GetAssetName(architecture, configuration, true, ".exe")' in provider
assert 'new Common.FileChecker(hash: sha256)' in provider
assert 'Common.GetFileSha256(cachePath)' in provider
# 发布页的校验文件是 PCL3_<配置>_<架构>_selfcontained.sha256，不能拼成 .exe.sha256
assert 'ReleaseAssets.GetAssetName(architecture, configuration, true, ".sha256")' in provider
assert 'assetName + ".sha256"' not in provider
exporter = (APP / 'Modules/Minecraft/Offline/WindowsOfflineBundleExporter.cs').read_text(encoding='utf-8-sig')
assert 'string launcherPath' in exporter
assert 'Environment.ProcessPath' not in exporter
linux_updater = (ROOT / 'PCL.Linux.Cli/LinuxSelfUpdater.cs').read_text(encoding='utf-8')
assert '-p:McmReleaseVersion="${{ inputs.version }}"' in workflow
assert 'MCM_${{ inputs.configuration }}_linux-x64.tar.gz' in workflow
assert 'MCM_Release_linux-x64.tar.gz' in linux_updater
assert 'test -x ./linux-artifact/mcm' in workflow
assert 'mcm README.md LICENSE' in workflow
linux_project = ET.parse(ROOT / 'PCL.Linux.Cli/PCL.Linux.Cli.csproj').getroot()
assert linux_project.findtext('.//AssemblyName') == 'mcm'
assert linux_project.find('.//AssemblyMetadata').attrib == {
    'Include': 'McmReleaseVersion', 'Value': '$(McmReleaseVersion)'
}
print('GitHub-only update source / no channels or CDK: PASS')
