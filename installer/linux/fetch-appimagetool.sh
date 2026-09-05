#!/usr/bin/env bash
# Downloads the pinned appimagetool release and checks its digest.
#
#   installer/linux/fetch-appimagetool.sh <out-file>
#
# Pinned to a release and checked against its digest, the way the workflow actions are pinned: a
# packaging tool downloaded at build time is part of the supply chain too. Both workflows call this,
# so the pin lives in one place. Dependabot does not watch it; bump it by hand.
set -euo pipefail

out="$1"
version="1.9.0"
sha256="46fdd785094c7f6e545b61afcfb0f3d98d8eab243f644b4b17698c01d06083d1"

mkdir -p "$(dirname "$out")"
curl -sSL -o "$out" "https://github.com/AppImage/appimagetool/releases/download/$version/appimagetool-x86_64.AppImage"
echo "$sha256  $out" | sha256sum -c -
chmod +x "$out"
