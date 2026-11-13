#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/linux-dev-env.sh"
taskVersion=1.2.0~ubuntu5
if [[ $# -gt 0 ]]; then
  [[ $# == 2 && $1 == --version ]] || { echo 'Usage: build-linux.sh [--version VERSION]' >&2; exit 2; }
  taskVersion="$2"
fi
[[ "$taskVersion" =~ ^[0-9][0-9A-Za-z.+~:-]*$ ]] || { echo 'Invalid Debian version' >&2; exit 2; }
[[ "$(dpkg --print-architecture)" == amd64 ]] || { echo 'This package targets amd64' >&2; exit 2; }
mkdir -p "$taskRoot/.local/linux-packaging" "$taskRoot/artifacts/linux"
taskStage="$(mktemp -d "$taskRoot/.local/linux-packaging/stage.XXXXXX")"
trap 'rm -rf -- "$taskStage"' EXIT
taskOutput="$taskRoot/artifacts/linux"
mkdir -p "$taskStage/opt/study-task-hub/ui" "$taskStage/DEBIAN" "$taskStage/usr/bin" "$taskStage/usr/share/applications" "$taskStage/usr/share/icons/hicolor/512x512/apps"
dotnet publish "$taskRoot/src/AccessProbe.LinuxBackend/AccessProbe.LinuxBackend.csproj" -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=false -o "$taskStage/opt/study-task-hub/backend" --nologo -v minimal
cp "$taskRoot/src/AccessProbe.Linux/main.py" "$taskStage/opt/study-task-hub/ui/"
cp -r "$taskRoot/src/AccessProbe.Linux/study_task_hub" "$taskStage/opt/study-task-hub/ui/"
find "$taskStage/opt/study-task-hub/ui" -type d -name __pycache__ -exec rm -rf -- {} +
sed "s/@VERSION@/$taskVersion/g" "$taskRoot/packaging/linux/control.in" > "$taskStage/DEBIAN/control"
install -m 755 "$taskRoot/packaging/linux/study-task-hub" "$taskStage/usr/bin/study-task-hub"
install -m 644 "$taskRoot/packaging/linux/study-task-hub.desktop" "$taskStage/usr/share/applications/io.github.studyTaskHub.desktop"
python3 - "$taskRoot/design/icons/app-1024.png" "$taskStage/usr/share/icons/hicolor/512x512/apps/study-task-hub.png" <<'PY'
from PIL import Image
import sys
Image.open(sys.argv[1]).resize((512,512),Image.Resampling.LANCZOS).save(sys.argv[2])
PY
python3 "$taskRoot/scripts/collect-linux-licenses.py" "$taskStage/opt/study-task-hub/backend" "$taskStage/usr/share/doc/study-task-hub" "$taskRoot"
dpkg-deb --root-owner-group --build "$taskStage" "$taskOutput/study-task-hub_${taskVersion}_amd64.deb"
cd "$taskOutput"
sha256sum "study-task-hub_${taskVersion}_amd64.deb" > SHA256SUMS.txt
