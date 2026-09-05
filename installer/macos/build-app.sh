#!/usr/bin/env bash
# Wraps a published GitAlert folder as GitAlert.app, signs it ad hoc and packs it into a DMG.
#
#   installer/macos/build-app.sh <version> <rid> <publish-dir> <pngs-dir> <out-dir>
#
# <pngs-dir> holds gitalert-<size>.png for 16 to 1024, as `GitAlert --export-png <dir>` writes them;
# the iconset is built from those so the icon is the same drawing as everywhere else. There is no
# Developer ID yet, so the signature is ad hoc and Gatekeeper asks for "Open Anyway" on first launch.
set -euo pipefail

version="$1"
rid="$2"
publish="$3"
pngs="$4"
out="$5"

here="$(cd "$(dirname "$0")" && pwd)"
app="$out/GitAlert-$rid/GitAlert.app"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/GitAlert"

sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

iconset="$out/GitAlert-$rid/GitAlert.iconset"
rm -rf "$iconset"
mkdir -p "$iconset"

for size in 16 32 128 256 512; do
    double=$((size * 2))
    cp "$pngs/gitalert-$size.png" "$iconset/icon_${size}x${size}.png"
    cp "$pngs/gitalert-$double.png" "$iconset/icon_${size}x${size}@2x.png"
done

iconutil -c icns "$iconset" -o "$app/Contents/Resources/GitAlert.icns"

codesign --force --deep --sign - "$app"
codesign --verify --verbose=1 "$app"

staging="$out/GitAlert-$rid/dmg"
rm -rf "$staging"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"

dmg="$out/GitAlert-$version-$rid.dmg"
rm -f "$dmg"
hdiutil create -volname "GitAlert $version" -srcfolder "$staging" -ov -format UDZO "$dmg"

echo "built $dmg"
