#!/usr/bin/env bash
# Check the game members the mod reaches by name against the installed
# game's assemblies, under Mono. Unity's native side is not needed.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dv="${DV_INSTALL_DIR:-$HOME/.local/share/Steam/steamapps/common/Derail Valley}"
project="$repo/tools/AIEngineer.AdapterCheck"

command -v dotnet >/dev/null || { echo "dotnet is required" >&2; exit 1; }
command -v mono >/dev/null || { echo "mono is required" >&2; exit 1; }
# PUBLIC_RELEASE=1 checks the public release's build (scripts/package.sh), whose
# developer settings, test hooks, and self-test must be gone.
mode=()
[[ "${PUBLIC_RELEASE:-}" == 1 ]] && mode=(-p:PublicRelease=true)
if ! build="$(dotnet build "$project/AIEngineer.AdapterCheck.csproj" -c Release -p:DvInstallDir="$dv" "${mode[@]}" --no-incremental -nologo 2>&1)"; then
    errors="$(grep -E "error" <<<"$build" | sort -u || true)"
    if [[ -n "$errors" ]]; then
        printf '%s\n' "$errors" >&2
    else
        printf '%s\n' "$build" >&2
    fi
    echo "adapter check build failed" >&2
    exit 1
fi
if grep -q -E "warning CS" <<<"$build"; then
    grep -E "warning CS" <<<"$build" | sort -u >&2
    exit 1
fi
# The Multiplayer mod, its API, Realistic Firing, and Skin Manager are checked in place when installed; they are
# never copied. SKIN_MANAGER names a Skin Manager build that isn't installed.
skin="${SKIN_MANAGER:-$dv/Mods/SkinManagerMod/SkinManagerMod.dll}"
DV_INSTALL_DIR="$dv" mono "$project/bin/Release/net48/AIEngineer.AdapterCheck.exe" "$dv/Mods/Multiplayer/MultiplayerAPI.dll" "$dv/Mods/Multiplayer/Multiplayer.dll" "$dv/Mods/RealisticFiring/RealisticFiring.dll" "$skin" "$@"
