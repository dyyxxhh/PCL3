#!/usr/bin/env python3
"""Offline smoke regressions for a built MCM Linux CLI executable."""
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


def main():
    executable = Path(sys.argv[1]).resolve(strict=True)
    assert executable.name == "mcm", executable
    with tempfile.TemporaryDirectory(prefix="mcm-cli-check-") as temporary:
        root = Path(temporary)
        environment = os.environ.copy()
        for name in ("MCM_GAME_DIR", "PCL_GAME_DIR"):
            environment.pop(name, None)
        environment["XDG_CONFIG_HOME"] = str(root / "config")

        def run(*arguments, expected=0, env=None):
            result = subprocess.run(
                [str(executable), *arguments], env=env or environment,
                stdin=subprocess.DEVNULL, capture_output=True, text=True,
                timeout=30,
            )
            assert result.returncode == expected, (arguments, result.returncode, result.stdout, result.stderr)
            return result.stdout

        def instance(game, name, legacy=False):
            directory = game / "instances" / name
            (directory / "mods").mkdir(parents=True)
            metadata = {
                "Name": name, "GameVersion": "1.21.1", "Loader": "fabric",
                "LoaderVersion": "0.16.0", "LaunchVersion": "fabric-profile",
            }
            file_name = "pcl-instance.json" if legacy else "mcm-instance.json"
            (directory / file_name).write_text(json.dumps(metadata), encoding="utf-8")
            return directory

        help_text = run("--help")
        assert "MCM Linux CLI" in help_text
        assert "mcm -R <file.jar>" in help_text
        assert "pcl " not in help_text

        game = root / "game with spaces"
        selected = instance(game, "selected")
        untouched = instance(game, "untouched")
        (selected / "mods" / "sample.jar").write_bytes(b"mock mod")
        (selected / "mods" / "keep.jar").write_bytes(b"keep")
        (untouched / "mods" / "sample.jar").write_bytes(b"untouched")
        (selected / "pcl-mods.json").write_text(json.dumps([{
            "FileName": "sample.jar", "Source": 0, "ProjectId": "sample",
            "ProjectName": "Sample", "ReleaseId": "v1", "ReleaseName": "1.0", "Disabled": False,
        }]), encoding="utf-8")
        options = ("--instance", "selected", "--game-dir", str(game))
        run("-R", "sample.jar", "--game-dir", str(game), expected=2)
        assert (selected / "mods" / "sample.jar").exists()
        run("-R", "../outside.jar", *options, expected=2)
        run("mod", "disable", "sample.jar", *options)
        assert (selected / "mods" / "sample.jar.disabled").exists()
        manifest = json.loads((selected / "mcm-mods.json").read_text(encoding="utf-8"))
        assert manifest[0]["Disabled"] and manifest[0]["ProjectId"] == "sample"
        run("mod", "enable", "sample.jar.disabled", *options)
        run("-R", "sample.jar", *options)
        assert not (selected / "mods" / "sample.jar").exists()
        assert (selected / "mods" / "keep.jar").exists()
        assert (untouched / "mods" / "sample.jar").read_bytes() == b"untouched"
        assert json.loads((selected / "mcm-mods.json").read_text(encoding="utf-8")) == []
        run("mod", "disable", "keep.jar", *options)
        run("-R", "keep.jar.disabled", *options)
        assert not (selected / "mods" / "keep.jar.disabled").exists()
        run("-R", "missing.jar", *options, expected=2)

        legacy_game = root / "legacy-game"
        instance(legacy_game, "legacy", legacy=True)
        configured = environment | {"MCM_GAME_DIR": str(game), "PCL_GAME_DIR": str(legacy_game)}
        assert "selected\t" in run("instance", "list", env=configured)
        assert "legacy\t" in run("instance", "list", "--game-dir", str(legacy_game), env=configured)
        configured.pop("MCM_GAME_DIR")
        assert "legacy\t" in run("instance", "list", env=configured)

        run("login", "offline", "SmokeUser")
        profile = root / "config" / "mcm" / "selected-profile.json"
        assert profile.exists()
        assert stat.S_IMODE(profile.stat().st_mode) == 0o600
        run("logout")
        assert not profile.exists()

    print("MCM CLI naming/mod removal/legacy compatibility smoke checks: PASS")


if __name__ == "__main__":
    main()
