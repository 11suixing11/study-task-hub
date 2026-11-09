"""Collect the licenses for the actual self-contained Windows publish output."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = ROOT / ".tools/nuget/packages"
APACHE_SHA256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"
SUPPORTED = {
    "anglesharp", "microsoft.data.sqlite", "microsoft.data.sqlite.core",
    "microsoft.web.webview2", "sqlitepclraw.bundle_e_sqlite3", "sqlitepclraw.core",
    "sqlitepclraw.lib.e_sqlite3", "sqlitepclraw.provider.e_sqlite3",
    "wpf-ui", "wpf-ui.abstractions",
}


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def collect(payload: Path) -> dict:
    payload = payload.resolve(strict=True)
    deps = json.loads((payload / "AccessProbe.deps.json").read_text(encoding="utf-8-sig"))
    runtime = json.loads((payload / "AccessProbe.runtimeconfig.json").read_text(encoding="utf-8-sig"))
    frameworks = {
        item["name"]: item["version"]
        for item in runtime["runtimeOptions"].get("includedFrameworks", [])
    }
    if not {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"} <= frameworks.keys():
        raise ValueError("License collection requires a self-contained WPF publish output")
    licenses = payload / "ThirdParty"
    provenance: list[dict] = []

    def copy(source: Path, target: str) -> None:
        if not source.is_file():
            raise FileNotFoundError(source)
        destination = licenses / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        provenance.append({
            "file": target,
            "source": source.relative_to(ROOT).as_posix(),
            "sha256": sha256(destination),
        })

    def write(target: str, body: str) -> None:
        destination = licenses / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(body, encoding="utf-8", newline="\n")

    core = PACKAGES / "microsoft.netcore.app.runtime.win-x64" / frameworks["Microsoft.NETCore.App"]
    desktop = PACKAGES / "microsoft.windowsdesktop.app.runtime.win-x64" / frameworks["Microsoft.WindowsDesktop.App"]
    copy(core / "LICENSE.TXT", ".NET/LICENSE.txt")
    copy(core / "THIRD-PARTY-NOTICES.TXT", ".NET/ThirdPartyNotices.txt")
    copy(desktop / "LICENSE", ".NET/WindowsDesktop-LICENSE.txt")

    package_specs: list[tuple[str, str]] = []
    metadata: list[dict] = []
    for name, value in sorted(deps["libraries"].items()):
        if value["type"] != "package":
            continue
        package_id, version = name.rsplit("/", 1)
        package = package_id.lower()
        if package not in SUPPORTED:
            raise ValueError(f"Add license handling for the new published package: {name}")
        package_specs.append((package, version))
        source = PACKAGES / package / version / f"{package}.nuspec"
        copy(source, f"package-metadata/{source.name}")
        xml = ElementTree.parse(source).getroot()
        fields = {node.tag.split("}")[-1]: node.text for node in xml.iter()}
        metadata.append({
            "package": fields["id"], "version": version,
            "license": fields.get("license"), "copyright": fields.get("copyright"),
            "licenseUrl": fields.get("licenseUrl"),
        })

    versions = dict(package_specs)
    required = {"anglesharp", "microsoft.data.sqlite.core", "microsoft.web.webview2", "wpf-ui", "wpf-ui.abstractions"}
    if not required <= versions.keys():
        raise ValueError("The expected published Windows dependencies are missing")
    copy(PACKAGES / "microsoft.web.webview2" / versions["microsoft.web.webview2"] / "LICENSE.txt", "WebView2/LICENSE.txt")
    copy(PACKAGES / "microsoft.web.webview2" / versions["microsoft.web.webview2"] / "NOTICE.txt", "WebView2/NOTICE.txt")
    copy(PACKAGES / "wpf-ui" / versions["wpf-ui"] / "LICENSE.md", "WPF-UI/LICENSE.md")
    copy(PACKAGES / "wpf-ui.abstractions" / versions["wpf-ui.abstractions"] / "LICENSE.md", "WPF-UI/Abstractions-LICENSE.md")
    copy(ROOT / "src/AccessProbe/ThirdParty/WPF-UI/ThirdPartyNotices.txt", "WPF-UI/ThirdPartyNotices.txt")
    for name in ("LICENSE.txt", "NOTICE.txt"):
        copy(ROOT / "src/AccessProbe/ThirdParty/Fluent-System-Icons" / name, f"Fluent-System-Icons/{name}")

    mit = (PACKAGES / "wpf-ui" / versions["wpf-ui"] / "LICENSE.md").read_text(encoding="utf-8-sig")
    old_copyright = "Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors. https://lepo.co/"
    if old_copyright not in mit or "Permission is hereby granted" not in mit:
        raise ValueError("The MIT license template has changed")
    for package, target in (("AngleSharp", "AngleSharp/LICENSE.txt"), ("Microsoft.Data.Sqlite.Core", "Microsoft.Data.Sqlite/LICENSE.txt")):
        record = next(item for item in metadata if item["package"].lower() == package.lower())
        if record["license"] != "MIT" or not record["copyright"]:
            raise ValueError(f"Unexpected license declaration: {package}")
        write(target, mit.replace(old_copyright, record["copyright"]))

    apache = ROOT / "packaging/windows/licenses/Apache-2.0.txt"
    if sha256(apache) != APACHE_SHA256:
        raise ValueError("The pinned canonical Apache 2.0 license has changed")
    copy(apache, "SQLitePCLRaw/LICENSE-2.0.txt")
    sqlite_records = [item for item in metadata if item["package"].lower().startswith("sqlitepclraw.")]
    if not sqlite_records or any(item["license"] != "Apache-2.0" for item in sqlite_records):
        raise ValueError("Unexpected SQLitePCLRaw license declaration")
    write("SQLitePCLRaw/NOTICE.txt", "\n".join([
        "SQLitePCLRaw", "Copyright 2014-2024 SourceGear, LLC", "Author: Eric Sink",
        "Licensed under the Apache License, Version 2.0.",
        "https://github.com/ericsink/SQLitePCL.raw",
        "Package versions and license declarations are retained in ../package-metadata.",
        "The bundled SQLite engine is public domain: https://sqlite.org/copyright.html", "",
    ]))
    write("SQLitePCLRaw/SQLite-Public-Domain.txt", "\n".join([
        "SQLite is in the Public Domain", "https://sqlite.org/copyright.html", "",
        "SQLite source code and documentation have been dedicated to the public domain.",
        "SQLitePCLRaw wrapper code is licensed separately under Apache 2.0.", "",
    ]))
    write("dependency-licenses.json", json.dumps(metadata, ensure_ascii=False, indent=2) + "\n")
    write("license-sources.json", json.dumps(provenance, ensure_ascii=False, indent=2) + "\n")
    write("README.txt", "\n".join([
        "Third-party licenses for the published Windows application", "",
        "Dependency versions are taken from AccessProbe.deps.json.",
        "The .NET and Windows Desktop licenses match the included runtime versions.",
        "WPF UI and Microsoft Fluent System Icons use the MIT license.",
        "WebView2 SDK licensing and notices are included in WebView2/.",
        "SQLitePCLRaw uses Apache 2.0; SQLite itself is public domain.",
        "The application brand icon is original artwork, separate from Fluent System Icons.", "",
    ]))
    return {"outcome": "collected", "packages": len(metadata), "runtimeVersions": frameworks,
            "licenseFiles": sum(path.is_file() for path in licenses.rglob("*"))}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(collect(args.payload), ensure_ascii=False))
