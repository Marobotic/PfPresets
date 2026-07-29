#!/usr/bin/env bash
# build.sh — Linux counterpart of build.bat. Run from anywhere:
#   bash /var/mnt/windows/Users/Maro/Documents/Project/PfPresets/build.sh
#
# Requirements (already set up 2026-07-25):
#   - dotnet-sdk-10.0 inside the `mywine` distrobox
#   - APPDATA shim: ~/.xlcore/msbuild-appdata/XIVLauncher/addon/Hooks/dev
#     -> symlink to ~/.xlcore/dalamud/Hooks/dev (satisfies the csproj's
#     $(appdata) HintPaths without touching the project file)
#
# In-game, the dev plugin location points straight at bin/Release, so there is
# no deploy step — build, then "Scan Dev Plugins" in Dalamud to reload:
#   Z:\var\mnt\windows\Users\Maro\Documents\Project\PfPresets\bin\Release\PfPresets.dll
#
# Community ratings (Phase 2) are compiled in by default here, because this is the
# dev loop for the third-party build. Pass --no-ratings for the conservative build
# that omits them entirely and stays submittable to the official Dalamud repo:
#   bash build.sh --no-ratings
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ENABLE_RATINGS=true
for arg in "$@"; do
  case "$arg" in
    --no-ratings) ENABLE_RATINGS=false ;;
    --ratings)    ENABLE_RATINGS=true ;;
    *) echo "Unknown option: $arg (expected --ratings or --no-ratings)" >&2; exit 2 ;;
  esac
done

BUILD_CMD="cd '$SCRIPT_DIR' && APPDATA=\$HOME/.xlcore/msbuild-appdata dotnet build PfPresets.csproj -c Release -p:EnableWindowsTargeting=true -p:EnableRatings=$ENABLE_RATINGS --no-incremental"

# --no-incremental for the same reason as build.bat: Dalamud only reloads a dev
# plugin when the DLL's timestamp changes, so always force a fresh write.
if command -v dotnet >/dev/null 2>&1; then
  sh -c "$BUILD_CMD"
else
  distrobox enter mywine -- sh -c "$BUILD_CMD"
fi

echo "[OK] Build complete: $SCRIPT_DIR/bin/Release/PfPresets.dll"
if [ "$ENABLE_RATINGS" = "true" ]; then
  echo "     Community ratings: COMPILED IN (third-party repo build)."
else
  echo "     Community ratings: omitted (official repo build)."
fi
echo "     Scan Dev Plugins in-game to reload."
