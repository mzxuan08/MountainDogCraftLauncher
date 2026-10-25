"""Package fresh outputs with source, locks, licenses, hashes and Unix modes.
Python 3.10+ standard library only; no network access during packaging.
"""
import argparse
import hashlib
import json
import shutil
import struct
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from source_info import revision, export


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def verify_binary(path, rid):
    with path.open("rb") as stream:
        header = stream.read(64)
        if rid == "win-x64":
            assert header[:2] == b"MZ", f"Missing PE header: {path.name}"
            stream.seek(struct.unpack_from("<I", header, 60)[0])
            assert stream.read(6) == b"PE\0\0\x64\x86", "Not an x64 PE executable"
        else:
            assert header[:4] == b"\xcf\xfa\xed\xfe", "Missing 64-bit Mach-O header"
            expected = 0x0100000C if rid == "osx-arm64" else 0x01000007
            assert struct.unpack_from("<I", header, 4)[0] == expected, "Wrong Mach-O CPU architecture"


def build(root, rids):
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    commit = revision(root)
    release = root / "artifacts/packages" / f"MDCL-{version}-{commit[:8]}"
    release.mkdir(parents=True, exist_ok=True)
    stage_root = root / "artifacts/work" / ("package-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%f"))
    stage_root.mkdir(parents=True)
    published_paths = json.loads((root / "artifacts/publish-index.json").read_text(encoding="utf-8-sig"))
    dependencies = json.loads((root / "licenses/dependencies.json").read_text(encoding="utf-8"))
    licensed = {(p["name"].lower(), p["version"]) for p in dependencies["packages"]}
    for lock in (root / "src").glob("**/packages.lock.json"):
        for framework in json.loads(lock.read_text())["dependencies"].values():
            for name, package in framework.items():
                if package["type"] != "Project":
                    assert (name.lower(), package["resolved"]) in licensed, f"License record missing for {name}"
    common = stage_root / "common"
    common.mkdir()
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        shutil.copy2(root / name, common)
    shutil.copytree(root / "licenses", common / "licenses")
    for lock in sorted(root.glob("**/packages.lock.json")):
        if "artifacts" in lock.relative_to(root).parts or "obj" in lock.relative_to(root).parts:
            continue
        destination = common / "dependency-lockfiles" / lock.relative_to(root)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(lock, destination)
    for name in ("global.json", "Directory.Packages.props"):
        shutil.copy2(root / name, common)
    targets = []
    for rid in rids:
        assert rid in ("win-x64", "osx-arm64", "osx-x64")
        published = Path(published_paths[rid])
        desktop = "MDCL.Desktop.exe" if rid == "win-x64" else "MDCL.Desktop"
        updater = "MDCL.Updater.exe" if rid == "win-x64" else "MDCL.Updater"
        verify_binary(published / desktop, rid)
        verify_binary(published / "updater" / updater, rid)
        info = json.loads((published / "build-info.json").read_text(encoding="utf-8-sig"))
        assert info["commit"] == commit, "Output is from another commit; republish before packaging"
        stage = stage_root / rid
        if rid == "win-x64":
            shutil.copytree(published, stage)
            shutil.copytree(common, stage, dirs_exist_ok=True)
            package = release / f"MDCL-{version}-{rid}.zip"
            with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
                for file in sorted(stage.rglob("*")):
                    if file.is_file(): archive.write(file, file.relative_to(stage).as_posix())
        else:
            app = stage / "MDCL.app"
            shutil.copytree(published, app / "Contents/MacOS")
            shutil.copytree(common, app / "Contents/Resources")
            plist = (root / "packaging/macos/Info.plist").read_text(encoding="utf-8")
            plist = plist.replace("<string>0.1.0</string>", f"<string>{version.split('-')[0]}</string>")
            (app / "Contents/Info.plist").write_text(plist, encoding="utf-8")
            (stage / "UNSIGNED-UNVERIFIED.txt").write_text("Unsigned and not notarized. Unix execution modes are in TAR; macOS runtime and Gatekeeper need real-device verification.\n", encoding="utf-8")
            package = release / f"MDCL-{version}-{rid}-UNSIGNED.app.tar.gz"
            def unix_modes(member):
                member.uid = member.gid = 0
                member.uname = member.gname = ""
                member.mode = 0o755 if member.isdir() or Path(member.name).name in (desktop, updater) else 0o644
                return member
            with tarfile.open(package, "w:gz", format=tarfile.PAX_FORMAT) as archive:
                archive.add(app, arcname="MDCL.app", filter=unix_modes)
                archive.add(stage / "UNSIGNED-UNVERIFIED.txt", arcname="UNSIGNED-UNVERIFIED.txt", filter=unix_modes)
        targets.append({"rid": rid, "package": package.name, "size": package.stat().st_size,
            "sha256": sha256(package), "signed": False, "runtimeVerification": "see evidence/release-matrix.md"})
    shutil.copytree(common, release, dirs_exist_ok=True)
    source = release / f"MDCL-{version}-source.zip"
    export(root, source)
    shutil.copytree(root / "docs/evidence", release / "evidence", dirs_exist_ok=True)
    write_json(release / "release-manifest.json", {"version": version, "commit": commit,
        "builtAt": datetime.now(timezone.utc).isoformat(), "targets": targets,
        "sourceArchive": source.name, "sourceSha256": sha256(source), "license": "MIT"})
    sums = [{"path": file.relative_to(release).as_posix(), "size": file.stat().st_size, "sha256": sha256(file)}
        for file in sorted(release.rglob("*")) if file.is_file() and file.name not in ("SHA256SUMS.txt", "SHA256SUMS.json")]
    write_json(release / "SHA256SUMS.json", sums)
    (release / "SHA256SUMS.txt").write_text("".join(f"{f['sha256']}  {f['path']}\n" for f in sums), encoding="utf-8")
    write_json(root / "artifacts/latest-release.json", {"path": str(release)})
    print(json.dumps({"release": str(release), "targets": targets}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--rids", nargs="+", default=["win-x64", "osx-arm64", "osx-x64"])
    args = parser.parse_args()
    build(args.root.resolve(), args.rids)
