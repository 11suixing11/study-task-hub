#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/linux-dev-env.sh"
dotnet build "$taskRoot/src/AccessProbe.LinuxBackend/AccessProbe.LinuxBackend.csproj" -c Release --nologo -v quiet
exec /usr/bin/python3 "$taskRoot/src/AccessProbe.Linux/main.py" --backend "$taskRoot/src/AccessProbe.LinuxBackend/bin/Release/net10.0/StudyTaskHub.LinuxBackend" "$@"
