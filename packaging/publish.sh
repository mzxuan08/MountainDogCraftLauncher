#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKSPACE="$(cd "$ROOT/.." && pwd)"
DOTNET="$WORKSPACE/.dotnet10/dotnet"
if [[ ! -x "$DOTNET" ]]; then DOTNET=dotnet; fi
export DOTNET_CLI_HOME="$WORKSPACE/.dotnet-home"
export LOCALAPPDATA="$WORKSPACE/.localappdata"
export NUGET_PACKAGES="$WORKSPACE/.nuget/packages"
export NUGET_HTTP_CACHE_PATH="$WORKSPACE/.nuget/http-cache"
export NUGET_PLUGINS_CACHE_PATH="$WORKSPACE/.nuget/plugins-cache"
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0
export AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export TEMP="$WORKSPACE/.tmp"
export TMP="$TEMP"
mkdir -p "$DOTNET_CLI_HOME" "$LOCALAPPDATA" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$NUGET_PLUGINS_CACHE_PATH" "$TEMP"
cd "$ROOT"
"$DOTNET" restore MDCL.sln --locked-mode
"$DOTNET" build MDCL.sln -c Release --no-restore
for RID in win-x64 osx-arm64 osx-x64; do
  "$DOTNET" publish src/MDCL.Desktop/MDCL.Desktop.csproj -c Release -r "$RID" --self-contained true --no-restore -o "$ROOT/artifacts/$RID"
done
