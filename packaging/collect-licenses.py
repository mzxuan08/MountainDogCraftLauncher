"""Refresh license evidence from the restored NuGet cache and upstream texts.
Run after changing lock files: python packaging/collect-licenses.py --cache <NuGet packages>.
"""
import argparse
import hashlib
import json
import shutil
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCES = {
    "Avalonia": "https://raw.githubusercontent.com/AvaloniaUI/Avalonia/master/licence.md",
    "CommunityToolkit": "https://raw.githubusercontent.com/CommunityToolkit/dotnet/main/License.md",
    "dotnet": "https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT",
    "dotnet-third-party": "https://raw.githubusercontent.com/dotnet/runtime/main/THIRD-PARTY-NOTICES.TXT",
    "SQLitePCLRaw": "https://raw.githubusercontent.com/ericsink/SQLitePCL.raw/master/LICENSE.TXT",
    "Inter": "https://raw.githubusercontent.com/rsms/inter/master/LICENSE.txt",
    "MicroCom": "https://raw.githubusercontent.com/kekekeks/MicroCom/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE",
    "Tmds": "https://raw.githubusercontent.com/tmds/Tmds.DBus/main/COPYING",
}


def main(cache):
    output = ROOT / "licenses"
    output.mkdir(exist_ok=True)
    sources = []
    for name, url in SOURCES.items():
        with urllib.request.urlopen(url, timeout=45) as response:
            data = response.read()
        path = output / (name + ".txt")
        path.write_bytes(data)
        sources.append({"file": path.name, "source": url, "sha256": hashlib.sha256(data).hexdigest()})
    packages = {}
    for lock in (ROOT / "src").glob("**/packages.lock.json"):
        for framework in json.loads(lock.read_text())["dependencies"].values():
            for name, data in framework.items():
                if data["type"] == "Project": continue
                packages[(name, data["resolved"])] = data
    report = []
    for (name, version), data in sorted(packages.items()):
        directory = cache / name.lower() / version.lower()
        nuspec = next(directory.glob("*.nuspec"))
        metadata = {node.tag.rsplit("}", 1)[-1]: node for node in ET.parse(nuspec).iter()}
        license_node = metadata.get("license")
        license_value = license_node.text if license_node is not None else "REVIEW REQUIRED"
        license_type = license_node.get("type") if license_node is not None else "unknown"
        local_texts = []
        for file in directory.iterdir():
            if file.is_file() and any(word in file.name.lower() for word in ("license", "licence", "notice", "copying")):
                if file.suffix.lower() in (".nupkg", ".sha512"): continue
                destination = output / "packages" / f"{name}-{version}" / file.name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(file, destination)
                local_texts.append(destination.relative_to(output).as_posix())
        if license_type == "file":
            source = directory / license_value
            destination = output / "packages" / f"{name}-{version}" / source.name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
            if destination.relative_to(output).as_posix() not in local_texts: local_texts.append(destination.relative_to(output).as_posix())
        report.append({"name": name, "version": version, "nugetContentHash": data["contentHash"],
            "licenseType": license_type, "license": license_value, "includedTexts": local_texts,
            "source": f"https://www.nuget.org/packages/{name}/{version}"})
    (output / "dependencies.json").write_text(json.dumps({"packages": report, "upstreamTexts": sources}, indent=2) + "\n", encoding="utf-8")
    print(f"Recorded {len(report)} locked dependencies and {len(sources)} upstream texts.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, required=True)
    main(parser.parse_args().cache)
