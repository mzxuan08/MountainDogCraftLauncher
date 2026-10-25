#!/usr/bin/env bash
set -euo pipefail
MDCL_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$MDCL_ROOT"
export DOTNET_CLI_HOME="$MDCL_ROOT/artifacts/work/dotnet-home"
export TMPDIR="$MDCL_ROOT/artifacts/work/tmp"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$DOTNET_CLI_HOME" "$TMPDIR"
DOTNET=dotnet
if [[ -x "$MDCL_ROOT/../.dotnet10/dotnet" ]]; then DOTNET="$MDCL_ROOT/../.dotnet10/dotnet"; fi
"$DOTNET" restore MDCL.sln --locked-mode
"$DOTNET" build MDCL.sln -c Release --no-restore
"$DOTNET" test MDCL.sln -c Release --no-build --logger trx --results-directory artifacts/verification/release
BUILD_ID="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
COMMIT="$(python3 packaging/source_info.py "$MDCL_ROOT")"
SDK="$("$DOTNET" --version)"
RIDS=("${@}")
if [[ ${#RIDS[@]} -eq 0 ]]; then RIDS=(win-x64 osx-arm64 osx-x64); fi
for RID in "${RIDS[@]}"; do
  case "$RID" in win-x64|osx-arm64|osx-x64) ;; *) echo "Unsupported RID" >&2; exit 2;; esac
  OUTPUT="$MDCL_ROOT/artifacts/publish/$BUILD_ID/$RID"
  "$DOTNET" publish src/MDCL.Desktop/MDCL.Desktop.csproj -c Release -r "$RID" --self-contained true --no-restore -p:PublishTrimmed=false -p:PublishSingleFile=false -o "$OUTPUT"
  "$DOTNET" publish src/MDCL.Updater/MDCL.Updater.csproj -c Release -r "$RID" --self-contained true --no-restore -o "$OUTPUT/updater"
  python3 -c 'import json,pathlib,sys; p=pathlib.Path(sys.argv[1]); (p/"build-info.json").write_text(json.dumps(dict(rid=sys.argv[2],commit=sys.argv[3],sdk=sys.argv[4],signed=False)))' "$OUTPUT" "$RID" "$COMMIT" "$SDK"
done
python3 -c 'import json,pathlib,sys; r=pathlib.Path(sys.argv[1]); (r/"artifacts/publish-index.json").write_text(json.dumps({rid:str(r/"artifacts/publish"/sys.argv[2]/rid) for rid in sys.argv[3:]}))' "$MDCL_ROOT" "$BUILD_ID" "${RIDS[@]}"
python3 packaging/package.py --rids "${RIDS[@]}"
