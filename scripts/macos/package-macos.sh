#!/usr/bin/env bash
# Sign, notarize, staple, and package the macOS demo for distribution.
#
# Prerequisites (one-time setup):
#   * "Developer ID Application: Ludicon LLC (LRTFSM8RZJ)" certificate in your login
#     keychain (NOT "Apple Distribution" — that one is for the App Store). Generate at
#     developer.apple.com/account → Certificates → "Developer ID Application", then
#     double-click the downloaded .cer. Confirm with:
#       security find-identity -v -p codesigning
#   * A notarytool keychain profile. Create it once:
#       xcrun notarytool store-credentials Ludicon \
#         --apple-id <your-apple-id> \
#         --team-id  LRTFSM8RZJ \
#         --password <app-specific-password>
#     (App-specific passwords come from appleid.apple.com.)
#
# Usage:
#   scripts/macos/package-macos.sh [path/to/app] [notary-profile]
# Defaults:
#   path/to/app    = Export~/macos/spark-unity-demo.app
#   notary-profile = Ludicon

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

APP="${1:-Export~/macos/spark-unity-demo.app}"
NOTARY_PROFILE="${2:-Ludicon}"
ENTITLEMENTS="$SCRIPT_DIR/entitlements.plist"

if [ ! -d "$APP" ];          then echo "error: app not found: $APP" >&2;          exit 1; fi
if [ ! -f "$ENTITLEMENTS" ]; then echo "error: missing $ENTITLEMENTS" >&2;         exit 1; fi

OUT_DIR="$(dirname "$APP")"
APP_NAME="$(basename "${APP%.app}")"
ZIP="$OUT_DIR/${APP_NAME}.zip"
DMG="$OUT_DIR/${APP_NAME}.dmg"

# Pick the Ludicon Developer ID Application identity from the login keychain.
IDENTITY="$(security find-identity -v -p codesigning \
            | awk -F'"' '/Developer ID Application:.*Ludicon/ {print $2; exit}')"
if [ -z "$IDENTITY" ]; then
    echo "error: no 'Developer ID Application: ... Ludicon ...' identity found." >&2
    echo "       Install the cert from developer.apple.com/account → Certificates." >&2
    exit 1
fi
echo ">>> Signing with: $IDENTITY"

# Strip extended attrs that Unity sometimes leaves (broke the original ad-hoc seal).
xattr -cr "$APP"

# Sign deep with hardened runtime + Unity-friendly entitlements.
codesign --force --deep --options runtime --timestamp \
         --entitlements "$ENTITLEMENTS" \
         --sign "$IDENTITY" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# Notarize the .app (ditto preserves bundle metadata better than zip).
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"
echo ">>> Notarizing $(basename "$ZIP") — this can take a few minutes..."
xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait

# Staple the ticket so the .app passes Gatekeeper offline.
xcrun stapler staple "$APP"
spctl -a -vv "$APP"

# Build a drag-to-install DMG.
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"

rm -f "$DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$STAGING" -ov -format UDZO "$DMG"

# Sign + notarize + staple the DMG too, so it passes Gatekeeper at mount.
codesign --force --timestamp --sign "$IDENTITY" "$DMG"
echo ">>> Notarizing $(basename "$DMG")..."
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$DMG"
spctl -a -vv --type install "$DMG"

rm -f "$ZIP"   # intermediate; the DMG is the deliverable

echo
echo "Done. Distribute: $DMG"
