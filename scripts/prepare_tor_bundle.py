#!/usr/bin/env python3
"""Prepare a pinned Tor Project Expert Bundle for embedding in a release executable."""
import argparse
import hashlib
import io
import tarfile
import urllib.request
from pathlib import Path, PurePosixPath

VERSION = "16.0.1"
CHECKSUMS = {
    "linux-x86_64": "7039cb16060ab22e669852e55774d894b55f25ea8bd0ab7194d3478429a58725",
    "windows-x86_64": "9eed3c3ff1fe43df034ce360196c9ec95472560d77ff55614d0cd5120edea27b",
}

parser = argparse.ArgumentParser()
parser.add_argument("target", choices=CHECKSUMS)
parser.add_argument("output", type=Path)
parser.add_argument("--archive", type=Path, help="Use a previously downloaded archive with the same verified checksum")
args = parser.parse_args()
name = f"tor-expert-bundle-{args.target}-{VERSION}.tar.gz"
limit = 64 * 1024 * 1024
if args.archive:
    with args.archive.open("rb") as source:
        archive = source.read(limit + 1)
else:
    with urllib.request.urlopen(f"https://dist.torproject.org/torbrowser/{VERSION}/{name}", timeout=60) as source:
        archive = source.read(limit + 1)
if len(archive) > limit or hashlib.sha256(archive).hexdigest() != CHECKSUMS[args.target]:
    raise SystemExit(f"Invalid Tor Expert Bundle checksum: {name}")

args.output.parent.mkdir(parents=True, exist_ok=True)
with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as source:
    with tarfile.open(args.output, "w:gz") as destination:
        for member in source:
            path = PurePosixPath(member.name)
            if not member.isfile() or not path.parts or path.parts[0] not in {"tor", "data", "docs"}:
                continue
            if path.is_absolute() or ".." in path.parts or "\\" in member.name or ":" in member.name:
                raise SystemExit("Unexpected path in Tor Expert Bundle")
            with source.extractfile(member) as content:
                destination.addfile(member, content)
