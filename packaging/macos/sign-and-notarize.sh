#!/usr/bin/env bash
set -euo pipefail
# Run on a macOS machine after packaging/package.ps1 produced the .app tree,
# or after `dotnet publish` on macOS.
# Required environment:
#   MACOS_SIGN_IDENTITY   e.g. "Developer ID Application: Name (TEAMID)"
#   MACOS_TEAM_ID         Apple Developer Team ID
#   MACOS_NOTARY_PROFILE  notarytool keychain profile name
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${1:-}"
if [[ -z "$APP" || ! -d "$APP" ]]; then
  echo "usage: $0 /path/to/MDCL.app" >&2
  exit 2
fi
: "${MACOS_SIGN_IDENTITY:?MACOS_SIGN_IDENTITY is required}"
: "${MACOS_TEAM_ID:?MACOS_TEAM_ID is required}"
: "${MACOS_NOTARY_PROFILE:?MACOS_NOTARY_PROFILE is required}"

codesign --force --deep --options runtime --timestamp \
  --entitlements "$ROOT/packaging/macos/entitlements.plist" \
  --sign "$MACOS_SIGN_IDENTITY" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
ZIP="${APP%.app}-notarize.zip"
/usr/bin/ditto -c -k --keepParent "$APP" "$ZIP"
xcrun notarytool submit "$ZIP" --keychain-profile "$MACOS_NOTARY_PROFILE" --wait
xcrun stapler staple "$APP"
spctl --assess --type execute --verbose=4 "$APP"
echo "Signed, notarized and stapled: $APP"
