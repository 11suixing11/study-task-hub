import json
from pathlib import Path
import shutil
import sys
import xml.etree.ElementTree as ET

backend, output, root = map(Path, sys.argv[1:])
output.mkdir(parents=True, exist_ok=True)
licenses = output / "licenses"
licenses.mkdir()
for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
    shutil.copyfile(root / ".tools/dotnet" / name, licenses / ("dotnet-" + name))
dependencies = json.loads((backend / "StudyTaskHub.LinuxBackend.deps.json").read_text())
records = []
for library, item in dependencies["libraries"].items():
    if item["type"] != "package":
        continue
    name, version = library.rsplit("/", 1)
    package = root / ".tools/nuget/packages" / name.lower() / version
    record = {"name":name, "version":version, "files":[]}
    nuspec = next(package.glob("*.nuspec"), None)
    if nuspec:
        document = ET.parse(nuspec)
        for node in document.iter():
            if node.tag.rsplit("}", 1)[-1] == "license":
                record["license"] = node.text
                record["licenseType"] = node.attrib.get("type")
        target = licenses / (name + "-" + version)
        target.mkdir()
        for file in package.rglob("*"):
            if file.is_file() and any(word in file.name.lower() for word in ("license", "notice", "copying")) and file.stat().st_size < 2*1024*1024:
                shutil.copyfile(file, target / file.name)
                record["files"].append(file.name)
        shutil.copyfile(nuspec, target / nuspec.name)
    records.append(record)
(output / "dependencies.json").write_text(json.dumps(records,ensure_ascii=False,indent=2)+"\n")
(output / "system-licenses.txt").write_text("GTK、libadwaita、WebKit、PyGObject、SQLite 等由系统依赖提供。对应许可和发行版声明位于 /usr/share/doc/<包名>/copyright。\n本包不复制系统 GUI 运行库。\n")
