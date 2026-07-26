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
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_CMD="cd '$SCRIPT_DIR' && APPDATA=\$HOME/.xlcore/msbuild-appdata dotnet build PfPresets.csproj -c Release -p:EnableWindowsTargeting=true --no-incremental"

# --no-incremental for the same reason as build.bat: Dalamud only reloads a dev
# plugin when the DLL's timestamp changes, so always force a fresh write.
if command -v dotnet >/dev/null 2>&1; then
  sh -c "$BUILD_CMD"
else
  distrobox enter mywine -- sh -c "$BUILD_CMD"
fi

echo "[OK] Build complete: $SCRIPT_DIR/bin/Release/PfPresets.dll"
echo "     Scan Dev Plugins in-game to reload."
