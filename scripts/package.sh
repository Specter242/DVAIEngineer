#!/usr/bin/env bash
# Build the release zip for Unity Mod Manager and Nexus Mods:
# dist/AIEngineer-<version>.zip, one AIEngineer folder holding exactly the
# files the mod needs, its player readme, changelog, and license; and beside
# it dist/AIEngineer-<version>.zip.json, the manifest the release is frozen
# by: version, commit, branch, size, and SHA-256.
#
# It refuses an uncommitted tree, a version that isn't three parts, and a
# CHANGELOG.md whose first release heading doesn't name the version. It runs
# the unit tests and the adapter check on the public build, builds the public
# release (-p:PublicRelease=true: no developer settings, test hooks, whistle
# menu, or self-test: no developer options or test tools in a release),
# fails on any compiler warning, and fails if any of those reach the DLL.
# Building a zip publishes nothing.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dv="${DV_INSTALL_DIR:-$HOME/.local/share/Steam/steamapps/common/Derail Valley}"
out="$repo/src/AIEngineer/bin/Release/net48"
version="$(python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))["Version"])' "$repo/src/AIEngineer/Info.json")"
zipfile="$repo/dist/AIEngineer-$version.zip"
manifest="$zipfile.json"
cap=(systemd-run --user --scope -q -p MemoryMax=6G -p MemorySwapMax=0)

echo "Packaging AI Engineer $version"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Info.json's version $version is not three parts: a release can't be built from it" >&2
    exit 1
fi
if [[ -n "$(git -C "$repo" status --porcelain)" ]]; then
    git -C "$repo" status --short >&2
    echo "The tree has uncommitted changes: commit them first" >&2
    exit 1
fi
# The changelog's first release heading names this version; an Unreleased section above it stays out of the zip.
first="$(grep -m1 -E '^## [0-9]' "$repo/CHANGELOG.md" || true)"
if [[ "$first" != "## $version ("* ]]; then
    echo "CHANGELOG.md's first release heading is '$first', not '## $version (date)'" >&2
    exit 1
fi
commit="$(git -C "$repo" rev-parse HEAD)"
branch="$(git -C "$repo" rev-parse --abbrev-ref HEAD)"

"${cap[@]}" dotnet test "$repo/tests/AIEngineer.Core.Tests/AIEngineer.Core.Tests.csproj" -c Release -nologo -v quiet
DV_INSTALL_DIR="$dv" PUBLIC_RELEASE=1 "$repo/scripts/check-adapter.sh" | grep -E "^(FAIL|skip)|adapter checks"

# Last, so the mod's build is this one, whatever the adapter check built before.
if ! build="$("${cap[@]}" dotnet build "$repo/src/AIEngineer/AIEngineer.csproj" -c Release -p:DvInstallDir="$dv" -p:PublicRelease=true --no-incremental -nologo 2>&1)"; then
    grep -E "error" <<<"$build" | sort -u >&2
    exit 1
fi
if grep -q -E "warning CS" <<<"$build"; then
    grep -E "warning CS" <<<"$build" | sort -u >&2
    exit 1
fi
"$repo/scripts/check-public-dll.sh" "$out/AIEngineer.dll"
if [[ "$(python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))["Version"])' "$out/Info.json")" != "$version" ]]; then
    echo "The build's Info.json doesn't name $version" >&2
    exit 1
fi

stage="$repo/dist/stage"
rm -rf "${stage:?}" "${zipfile:?}" "${manifest:?}"
mkdir -p "$stage/AIEngineer"
cp "$out/Info.json" "$out/AIEngineer.dll" "$out/AIEngineer.Core.dll" "$stage/AIEngineer/"
cp "$repo/PLAYER-README.md" "$stage/AIEngineer/README.md"
cp "$repo/LICENSE" "$stage/AIEngineer/"
# The changelog from its first release heading on.
sed -n "/^## [0-9]/,\$p" "$repo/CHANGELOG.md" | { echo "# AI Engineer: changelog"; echo; cat; } >"$stage/AIEngineer/CHANGELOG.md"

# Ship exactly these files: never game or third-party assemblies, settings, caches, or a self-test trigger.
expected="AIEngineer.Core.dll AIEngineer.dll CHANGELOG.md Info.json LICENSE README.md"
actual="$(cd "$stage/AIEngineer" && ls -A | LC_ALL=C sort | tr '\n' ' ' | sed 's/ $//')"
if [[ "$actual" != "$expected" ]]; then
    echo "Unexpected package contents: $actual" >&2
    exit 1
fi

mkdir -p "$repo/dist"
(cd "$stage" && zip -q -r -X "$zipfile" AIEngineer)
rm -rf "${stage:?}"
sum="$(sha256sum "$zipfile" | cut -d' ' -f1)"
printf '{\n  "id": "AIEngineer",\n  "version": "%s",\n  "commit": "%s",\n  "branch": "%s",\n  "file": "%s",\n  "bytes": %s,\n  "sha256": "%s",\n  "built": "%s"\n}\n' \
    "$version" "$commit" "$branch" "$(basename "$zipfile")" "$(stat -c %s "$zipfile")" "$sum" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$manifest"
unzip -l "$zipfile"
echo "Built $zipfile from $commit ($branch)"
(cd "$(dirname "$zipfile")" && sha256sum "$(basename "$zipfile")")
