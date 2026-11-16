#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/linux-dev-env.sh"
cd "$taskRoot"
dotnet run --project tests/LinuxChecks/LinuxChecks.csproj -c Release -- "$@"
