#!/usr/bin/env python3
"""Build a Jellyfin catalog and ZIP from the published plugin and build.yaml."""

import argparse
import hashlib
import json
import re
import uuid
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path

import yaml


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", default="yourmomsbasement/HardwareEncode_AutoDetect")
    parser.add_argument("--previous-manifest", type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
        parser.error("repository must be owner/name")

    root = Path(__file__).resolve().parents[1]
    meta = yaml.safe_load((root / "build.yaml").read_text())
    version = str(meta["version"])
    if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version):
        raise ValueError("build.yaml version must have four numeric components")
    if ET.parse(root / "Directory.Build.props").findtext(".//Version") != version:
        raise ValueError("build.yaml and Directory.Build.props versions must match")
    uuid.UUID(meta["guid"])
    files = []
    for name in meta["artifacts"]:
        if Path(name).name != name:
            raise ValueError("artifacts must be filenames without directory components")
        path = root / "artifacts/plugin" / name
        if not path.is_file():
            raise FileNotFoundError(f"Publish the plugin first: {path}")
        files.append(path)

    output = root / "artifacts/repository"
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"HardwareAutoDetect_{version}.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for path in files:
            info = zipfile.ZipInfo(path.name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            bundle.writestr(info, path.read_bytes())

    plugin = {key: meta[key] for key in
              ("guid", "name", "description", "overview", "owner", "category")}
    previous_versions = []
    if args.previous_manifest:
        previous = json.loads(args.previous_manifest.read_text())
        if len(previous) != 1 or previous[0]["guid"] != plugin["guid"]:
            raise ValueError("previous manifest must contain this plugin only")
        previous_versions = previous[0]["versions"]
        if any(entry["version"] == version for entry in previous_versions):
            raise ValueError("version already released; increment both version files")
    plugin["versions"] = [{
        "version": version,
        "changelog": meta["changelog"],
        "targetAbi": meta["targetAbi"],
        "sourceUrl": f"https://github.com/{args.repository}/releases/download/v{version}/{archive.name}",
        # Jellyfin's catalog protocol uses MD5 for downloaded ZIP integrity.
        "checksum": hashlib.md5(archive.read_bytes()).hexdigest(),
        "timestamp": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }] + previous_versions
    (output / "manifest.json").write_text(json.dumps([plugin], indent=2) + "\n")
    print(f"Packaged {archive.name} and manifest.json in {output}")


if __name__ == "__main__":
    main()
