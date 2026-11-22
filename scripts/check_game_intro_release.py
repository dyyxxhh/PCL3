#!/usr/bin/env python3
"""Reject Release assets that the launcher's built-in game-intro pins would reject."""
import hashlib
import pathlib
import re
import sys


BUNDLE_SOURCE = pathlib.Path(__file__).resolve().parents[1] / "PCL.Core.Shared" / "GameIntroBundle.cs"


def verify(source, prefix, asset):
    # Read the launcher's pins, not a separate checksum generated from the asset itself.
    digest_pin = re.search(rf'\bconst string {prefix}Hash\s*=\s*"([0-9a-f]{{64}})";', source)
    length_pin = re.search(rf'\bconst long {prefix}Length\s*=\s*([\d_]+);', source)
    if digest_pin is None or length_pin is None:
        raise SystemExit(f"Failed to read the launcher pins for {asset.name}.")
    if asset.stat().st_size != int(length_pin[1].replace("_", "")):
        raise SystemExit(f"{asset.name} does not match the launcher's pinned length.")
    with asset.open("rb") as payload:
        digest = hashlib.file_digest(payload, "sha256").hexdigest()
    if digest != digest_pin[1]:
        raise SystemExit(f"{asset.name} does not match the launcher's pinned SHA-256.")
    print(f"Verified {asset.name}: {asset.stat().st_size} bytes, SHA-256 {digest}")


def main(arguments):
    if len(arguments) != 2:
        raise SystemExit("Usage: check_game_intro_release.py <clip.mp4> <agent.jar>")
    source = BUNDLE_SOURCE.read_text(encoding="utf-8")
    for prefix, filename in zip(("Clip", "Agent"), arguments, strict=True):
        verify(source, prefix, pathlib.Path(filename))


if __name__ == "__main__":
    main(sys.argv[1:])
