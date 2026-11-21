#!/usr/bin/env python3
"""Check the Windows backup page's WPF routing and entry points without loading WPF."""
import re
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "Plain Craft Launcher 2"
PAGES = APP / "Pages/PageInstance"
LOCAL = "{clr-namespace:PCL}"
XAML = "{http://schemas.microsoft.com/winfx/2006/xaml}"


def read(path):
    return path.read_text(encoding="utf-8-sig")


left = ET.parse(PAGES / "PageInstanceLeft.xaml").getroot()
item = next((element for element in left.iter(LOCAL + "MyListItem")
             if element.get(XAML + "Name") == "ItemBackup"), None)
assert item is not None, "Instance sidebar must offer a backup page"
assert item.get("Type") == "RadioBox" and item.get("Check") == "PageCheck"
subtype = re.search(r"\bVersionBackup\s*=\s*(\d+)\s*,", read(APP / "FormMain.xaml.cs"))
assert subtype is not None and item.get("Tag") == subtype[1], "Backup sidebar tag must match its native route"
left_cs = read(PAGES / "PageInstanceLeft.xaml.cs")
assert re.search(r"case FormMain\.PageSubType\.VersionBackup:\s*return .*?new PageInstanceBackup\(\);", left_cs)

page = ET.parse(PAGES / "PageInstanceBackup.xaml").getroot()
assert page.tag == LOCAL + "MyPageRight" and page.get(XAML + "Class") == "PCL.PageInstanceBackup"
assert page.find(LOCAL + "MyScrollViewer") is not None
setup = ET.parse(PAGES / "PageInstanceSetup.xaml").getroot()
assert any(button.get("Click") == "OpenBackupSettings" for button in setup.iter(LOCAL + "MyButton"))
assert re.search(r"OpenBackupSettings\([^)]*\).*?\bItemBackup\.Checked\s*=\s*true", read(PAGES / "PageInstanceSetup.xaml.cs"), re.S)
service = read(APP / "Modules/Minecraft/WindowsInstanceBackup.cs")
page_cs = read(PAGES / "PageInstanceBackup.xaml.cs")
assert "ShowDialog(" not in page_cs + service and "new Window" not in page_cs + service, \
    "Backup must not open a separate WPF window"
assert list(page.iter(LOCAL + "MyCard")), "Backup must use native PCL cards"
for control, name in (("MyCheckBox", "CheckAutomatic"), ("MyComboBox", "ComboScope"),
                      ("MyTextBox", "TextRetention"), ("MyComboBox", "ComboBackups")):
    assert any(node.get(XAML + "Name") == name for node in page.iter(LOCAL + control)), name
for name, handler in (("BtnSave", "Save_Click"), ("BtnCreate", "Create_Click"),
                      ("BtnRestore", "Restore_Click"), ("BtnClone", "Clone_Click")):
    assert any(button.get(XAML + "Name") == name and button.get("Click") == handler
               for button in page.iter(LOCAL + "MyButton")), f"Missing backup operation: {name}"
    assert re.search(rf"\b{handler}\(", page_cs), f"Unwired backup operation: {name}"
assert "Backup.Save(" in page_cs and "Backup.CreateAsync(" in page_cs and "Backup.List(" in page_cs
assert "Backup.RestoreAsync(" in page_cs and "Backup.RegisterClone(" in page_cs
assert "Launcher.MyMsgBoxAsync(" in page_cs, "In-place restore must use a native confirmation"
assert "WindowsInstanceBackup.BeforeLaunch(" in read(APP / "Modules/Minecraft/Launch/Run.cs")
print("Windows instance backup native page routing and actions: PASS")
