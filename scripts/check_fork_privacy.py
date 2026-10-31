#!/usr/bin/env python3
"""Guard fork attribution, privacy and user-facing project destinations."""
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'Plain Craft Launcher 2'
CORE = ROOT / 'PCL.Core'
errors = []
def check(ok, message):
    if not ok: errors.append(message)
for locale in ('zh-CN', 'zh-TW', 'en-US', 'en-GB'):
    path = CORE / f'App/Localization/Languages/{locale}.xaml'
    strings = {node.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Key'): node.text or '' for node in ET.parse(path).getroot()}
    info = strings['Setup.About.Community.Info']
    check('PCL CE' in info and any(word in info for word in ('上游', 'upstream')), f'{path.name}: upstream team attribution unclear')
    check('PCL CE' in strings['Setup.About.SpecialThanks.Info.IconDesign'], f'{path.name}: upstream icon credit misattributed')
    check('dyyxxhh' in strings.get('Setup.About.Maintainer.Info', ''), f'{path.name}: fork maintainer missing')
    check('Sentry' in strings['Setup.About.Legal.PrivacyText'], f'{path.name}: current privacy notice missing')
main = (APP / 'FormMain.xaml.cs').read_text(encoding='utf-8-sig')
check('Lang.Text("Main.Telemetry.' not in main, 'telemetry consent popup still active')
service = (CORE / 'App/Essentials/TelemetryService.cs').read_text(encoding='utf-8-sig')
check('SentrySdk.Init(' not in service and '_ReportDeviceEnvironment(' not in service, 'telemetry can still collect or initialize')
for path in (APP, CORE):
    for source_file in path.rglob('*.cs'):
        if {'bin','obj'} & set(source_file.parts): continue
        text = source_file.read_text(encoding='utf-8-sig')
        for endpoint in ('PCL-Community/PCL-CE/releases', 'PCL-Community/PCL2-CE/issues', 'PCL-Community/PCL2-CE/contributors'):
            check(endpoint not in text, f'{source_file.relative_to(ROOT)}: active upstream endpoint {endpoint}')
check('PCL-Community/PCL2-CE' not in (CORE / 'IO/Net/Http/HttpSenderExtension.cs').read_text(encoding='utf-8-sig'), 'upstream identity remains in HTTP User-Agent')
check('PCL-CE' not in (CORE / 'IO/Net/NetworkService.cs').read_text(encoding='utf-8-sig'), 'upstream identity remains in default HTTP User-Agent')
check('PCL-CE' not in (APP / 'Modules/Network/Http/RequestSigning.cs').read_text(encoding='utf-8-sig'), 'upstream identity remains in signed-request User-Agent')
if errors:
    raise SystemExit('\n'.join('FAIL ' + error for error in errors))
print('fork attribution / no telemetry / project links: PASS')
