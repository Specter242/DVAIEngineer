#!/usr/bin/env bash
# Build the mod in Release and install it into the game's Mods folder.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dv="${DV_INSTALL_DIR:-$HOME/.local/share/Steam/steamapps/common/Derail Valley}"
dest="$dv/Mods/AIEngineer"
out="$repo/src/AIEngineer/bin/Release/net48"

# The bracket keeps pgrep from matching this script's own command line.
if pgrep -f 'DerailValley\.ex[e]' >/dev/null; then
    echo "note: Derail Valley is running; the new build loads on the next game start" >&2
fi

dotnet build "$repo/src/AIEngineer/AIEngineer.csproj" -c Release -p:DvInstallDir="$dv" -nologo -v quiet

mkdir -p "$dest"
# Settings.xml in $dest belongs to the player and is left in place. Each file
# is copied beside its target and renamed over it, so a running game keeps the
# file it loaded instead of seeing it rewritten.
for file in AIEngineer.dll AIEngineer.Core.dll Info.json; do
    cp "$out/$file" "$dest/.$file.new"
    mv -f "$dest/.$file.new" "$dest/$file"
done
echo "Installed to $dest"
