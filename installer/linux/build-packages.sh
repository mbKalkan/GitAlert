#!/usr/bin/env bash
# Packs a published GitAlert folder three ways: a tar.gz, a .deb and an AppImage.
#
#   installer/linux/build-packages.sh <version> <publish-dir> <pngs-dir> <out-dir> <appimagetool>
#
# <pngs-dir> holds gitalert-<size>.png as `GitAlert --export-png <dir>` writes them. <appimagetool>
# is the appimagetool AppImage, downloaded and checksummed by the caller; it is run extracted so no
# FUSE is needed on the build machine.
set -euo pipefail

version="$1"
publish="$2"
pngs="$3"
out="$4"
appimagetool="$5"

here="$(cd "$(dirname "$0")" && pwd)"
work="$out/linux-work"
rm -rf "$work"
mkdir -p "$work" "$out"

# ---- tar.gz: the folder as published, plus the desktop entry and an icon --------------------

tarball="$out/GitAlert-$version-linux-x64.tar.gz"
stage="$work/tar/GitAlert-$version"
mkdir -p "$stage"
cp -R "$publish"/. "$stage/"
cp "$here/gitalert.desktop" "$stage/"
cp "$pngs/gitalert-256.png" "$stage/gitalert.png"
tar -czf "$tarball" -C "$work/tar" "GitAlert-$version"
echo "built $tarball"

# ---- .deb: the app under /usr/lib, a launcher in /usr/bin, menu entry and icons --------------

deb="$work/deb"
mkdir -p "$deb/DEBIAN" "$deb/usr/lib/gitalert" "$deb/usr/bin" "$deb/usr/share/applications"
cp -R "$publish"/. "$deb/usr/lib/gitalert/"
ln -s /usr/lib/gitalert/GitAlert "$deb/usr/bin/gitalert"
cp "$here/gitalert.desktop" "$deb/usr/share/applications/"

for size in 16 32 64 128 256 512; do
    mkdir -p "$deb/usr/share/icons/hicolor/${size}x${size}/apps"
    cp "$pngs/gitalert-$size.png" "$deb/usr/share/icons/hicolor/${size}x${size}/apps/gitalert.png"
done

size_kb=$(du -sk "$deb/usr" | cut -f1)
sed -e "s/__VERSION__/$version/" -e "s/__SIZE__/$size_kb/" "$here/control" > "$deb/DEBIAN/control"

dpkg-deb --build --root-owner-group "$deb" "$out/gitalert_${version}_amd64.deb"
echo "built $out/gitalert_${version}_amd64.deb"

# ---- AppImage: the same tree with AppRun, the desktop entry and the icon at the root ---------

appdir="$work/GitAlert.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/lib/gitalert" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -R "$publish"/. "$appdir/usr/lib/gitalert/"
ln -s ../lib/gitalert/GitAlert "$appdir/usr/bin/gitalert"
ln -s usr/lib/gitalert/GitAlert "$appdir/AppRun"
cp "$here/gitalert.desktop" "$appdir/"
cp "$here/gitalert.desktop" "$appdir/usr/share/applications/"
cp "$pngs/gitalert-256.png" "$appdir/gitalert.png"
cp "$pngs/gitalert-256.png" "$appdir/usr/share/icons/hicolor/256x256/apps/gitalert.png"
ln -s gitalert.png "$appdir/.DirIcon"

chmod +x "$appimagetool"
ARCH=x86_64 "$appimagetool" --appimage-extract-and-run "$appdir" "$out/GitAlert-$version-linux-x64.AppImage"
echo "built $out/GitAlert-$version-linux-x64.AppImage"
