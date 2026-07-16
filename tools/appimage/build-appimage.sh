#!/usr/bin/env bash
#
# Assemble an AppDir from a published linux-x64 Glacier folder and run appimagetool to
# produce a single-file .AppImage. Runs under Linux (invoked by tools/package.ps1 through
# WSL on a Windows host, or directly on a Linux packaging host).
#
# Usage:
#   build-appimage.sh <publishDir> <iconPng> <desktopFile> <appRunFile> <outAppImage>
#
#     publishDir   the Glacier-linux-x64 publish folder (Glacier binary + scripts/ + docs)
#     iconPng      a 256x256 Glacier.png (derived from src/Ged.App/Assets/AppIcon.png)
#     desktopFile  tools/appimage/Glacier.desktop
#     appRunFile   tools/appimage/AppRun
#     outAppImage  destination path, e.g. dist/Glacier-1.0.0-linux-x86_64.AppImage
#
# appimagetool itself is a BUILD TOOL, not a shipped dependency. It is fetched once into
# the packaging user's HOME (never into the repo); override its location with $APPIMAGETOOL.
# FUSE is sidestepped via APPIMAGE_EXTRACT_AND_RUN=1 so this works on hosts without libfuse2.
set -euo pipefail

if [ "$#" -ne 5 ]; then
  echo "usage: build-appimage.sh <publishDir> <iconPng> <desktopFile> <appRunFile> <outAppImage>" >&2
  exit 2
fi
PUBLISH="$1"; ICON="$2"; DESKTOP="$3"; APPRUN="$4"; OUT="$5"

for p in "$PUBLISH/Glacier" "$ICON" "$DESKTOP" "$APPRUN"; do
  [ -e "$p" ] || { echo "missing input: $p" >&2; exit 3; }
done

export APPIMAGE_EXTRACT_AND_RUN=1
export ARCH=x86_64

# --- Resolve (or fetch) appimagetool ---------------------------------------------------
TOOL="${APPIMAGETOOL:-$HOME/appimagetool-x86_64.AppImage}"
if [ ! -x "$TOOL" ]; then
  echo "appimagetool not found at $TOOL - fetching the official build..."
  TAG="$(curl -sSL -m 30 https://api.github.com/repos/AppImage/appimagetool/releases/latest \
          | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name" *: *"([^"]+)".*/\1/' || true)"
  [ -n "${TAG:-}" ] || TAG="continuous"
  URL="https://github.com/AppImage/appimagetool/releases/download/${TAG}/appimagetool-x86_64.AppImage"
  echo "  $URL"
  curl -sSL -m 180 -o "$TOOL" "$URL"
  chmod +x "$TOOL"
fi
echo "appimagetool: $TOOL"
"$TOOL" --version 2>&1 | head -1 || true

# --- Assemble the AppDir (Linux-native workdir so perms/symlinks are honoured) ---------
WORK="$(mktemp -d "${TMPDIR:-/tmp}/glacier-appdir.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
APPDIR="$WORK/Glacier.AppDir"
mkdir -p "$APPDIR/usr/bin" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps"

# The whole publish payload (binary + scripts/ + docs) goes under usr/bin so the portable
# beside-the-binary layout is intact under the read-only mount.
cp -a "$PUBLISH"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Glacier"

# Desktop entry + icon at the AppDir root (required by appimagetool) and in the FreeDesktop
# integration locations (used by appimaged / desktop installers).
install -m 0644 "$DESKTOP" "$APPDIR/Glacier.desktop"
install -m 0644 "$DESKTOP" "$APPDIR/usr/share/applications/Glacier.desktop"
install -m 0644 "$ICON"    "$APPDIR/Glacier.png"
install -m 0644 "$ICON"    "$APPDIR/usr/share/icons/hicolor/256x256/apps/Glacier.png"

install -m 0755 "$APPRUN" "$APPDIR/AppRun"

# --- Build -----------------------------------------------------------------------------
mkdir -p "$(dirname "$OUT")"
rm -f "$OUT"
echo "Running appimagetool -> $OUT"
"$TOOL" --no-appstream "$APPDIR" "$OUT"

chmod +x "$OUT"
echo "== AppImage built =="
ls -l "$OUT"
file "$OUT"
sha256sum "$OUT"
