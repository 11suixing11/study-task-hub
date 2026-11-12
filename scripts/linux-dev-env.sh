#!/usr/bin/env bash
taskRoot="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
export DOTNET_ROOT="$taskRoot/.tools/dotnet"
export DOTNET_CLI_HOME="$taskRoot/.tools/dotnet-home"
export NUGET_PACKAGES="$taskRoot/.tools/nuget/packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$DOTNET_ROOT:$PATH"
