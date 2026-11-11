"""Collect Android dependency notices from the actual restore assets."""
import argparse
import hashlib
import json
import shutil
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]


def collect(assets: Path, output: Path) -> dict:
    restored = json.loads(assets.read_text(encoding="utf-8-sig"))
    output.mkdir(parents=True, exist_ok=True)
    copied = []

    def copy(source: Path, target: str) -> None:
        destination = output / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        copied.append({
            "file": target,
            "source": source.relative_to(ROOT).as_posix(),
            "sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
        })

    def write(target: str, text: str) -> None:
        destination = output / target
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(text, encoding="utf-8", newline="\n")

    packs = ROOT / ".tools/dotnet/packs"
    runtime = packs / "Microsoft.NETCore.App.Runtime.Mono.android-arm64/10.0.13"
    mit = (runtime / "LICENSE.TXT").read_text(encoding="utf-8-sig")
    copyright_line = "Copyright (c) .NET Foundation and Contributors"
    if copyright_line not in mit or "Permission is hereby granted" not in mit:
        raise ValueError("The runtime MIT license template has changed")
    for folder, version, target in (
        ("Microsoft.NETCore.App.Runtime.Mono.android-arm64", "10.0.13", ".NET"),
        ("Microsoft.Android.Runtime.Mono.36.android-arm64", "36.1.2", "Android-Mono"),
        ("Microsoft.Android.Runtime.36.android", "36.1.2", "Android"),
    ):
        pack = packs / folder / version
        copy(pack / "LICENSE.TXT", f"{target}/LICENSE.txt")
        copy(pack / "THIRD-PARTY-NOTICES.TXT", f"{target}/ThirdPartyNotices.txt")
        # The three shipped ABIs share these notices; reject differing packs.
        if folder.endswith("-arm64"):
            for arch in ("arm", "x64"):
                other = packs / (folder.removesuffix("arm64") + arch) / version
                for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
                    if (other / name).read_bytes() != (pack / name).read_bytes():
                        raise ValueError(f"Add separate notices for {other.name}")

    metadata = []
    for name, value in sorted(restored["libraries"].items()):
        if value["type"] != "package":
            continue
        package_id, version = name.rsplit("/", 1)
        package = ROOT / ".tools/nuget/packages" / value["path"]
        spec = package / (package_id.lower() + ".nuspec")
        fields = {node.tag.split("}")[-1]: node.text for node in ElementTree.parse(spec).getroot().iter()}
        license_name = fields.get("license")
        if license_name not in ("MIT", "Apache-2.0") or not fields.get("copyright"):
            raise ValueError(f"Add license handling for {name}")
        copy(spec, f"package-metadata/{spec.name}")
        metadata.append({
            "package": package_id, "version": version, "license": license_name,
            "copyright": fields["copyright"],
            "scope": "build_tool" if package_id == "Microsoft.NET.ILLink.Tasks" else "dependency",
        })
        if license_name == "MIT":
            write(f"{package_id}/LICENSE.txt", mit.replace(copyright_line, fields["copyright"]))

    sqlite = [record for record in metadata if record["package"].startswith("SQLitePCLRaw.")]
    if len(sqlite) != 4 or any(record["license"] != "Apache-2.0" for record in sqlite):
        raise ValueError("The Android SQLite dependency set has changed")
    copy(ROOT / "packaging/windows/licenses/Apache-2.0.txt", "SQLitePCLRaw/LICENSE-2.0.txt")
    write("SQLitePCLRaw/NOTICE.txt", "\n".join([
        "SQLitePCLRaw", sqlite[0]["copyright"], "Author: Eric Sink",
        "Licensed under the Apache License, Version 2.0.",
        "https://github.com/ericsink/SQLitePCL.raw",
        "The bundled SQLite engine is public domain: https://sqlite.org/copyright.html", "",
    ]))
    write("README.txt", "\n".join([
        "聚合平台 Android 第三方许可", "",
        "许可及依赖版本来自本次 Android 构建的 NuGet 还原清单和 .NET Android 运行时包。",
        "三种架构的运行时许可与声明一致，合并保存；ILLink 仅为构建工具。",
        "系统 Android WebView 由手机系统提供；本目录不包含 Windows WebView2 或 WPF UI。",
        "分发 APK 时请保留本目录。", "",
    ]))
    result = {
        "schema": 1, "platform": "android", "dependencies": metadata,
        "runtime": {"dotnet": "10.0.13", "android": "36.1.2", "abis": ["arm64-v8a", "armeabi-v7a", "x86_64"]},
        "sources": copied,
    }
    write("license-manifest.json", json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = collect(args.assets, args.output)
    print(json.dumps({"outcome": "passed", "dependencies": len(result["dependencies"]), "notices": len(result["sources"]) }))
