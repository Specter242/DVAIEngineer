#!/usr/bin/env bash
# Fails if a build of AIEngineer.dll holds any developer tool the public release
# leaves out (no developer options or test tools in a release): the
# developer settings and their labels, the whistle-timing menu, the settings
# drawer, the test hooks, and the self-test. Names are looked for among the
# DLL's identifiers (ASCII) and its string literals (UTF-16).
set -euo pipefail
dll="${1:?usage: scripts/check-public-dll.sh AIEngineer.dll}"
names=(ShowDeveloper DeveloperShown WhistleShortest WhistleShortestLong WhistleQuietEnds WhistleBounce WhistleDip
       WhistleVersion MoveOldWhistle WhistleMenu SettingsDrawer DrawForTest ForTest HitInPlace SelfTest)
labels=("Show developer settings" "Engineer ticks per second" "Log each route planned" "Log what the helper's whistle hears"
        "Developer" "selftest" "self-test")
found=()
literals="$(strings -el "$dll")"
for n in "${names[@]}"; do
    if grep -a -q -F "$n" "$dll" || grep -q -F "$n" <<<"$literals"; then found+=("$n"); fi
done
for l in "${labels[@]}"; do
    if grep -q -i -F "$l" <<<"$literals"; then found+=("\"$l\""); fi
done
if (( ${#found[@]} )); then
    echo "The public build holds developer tools: ${found[*]}" >&2
    exit 1
fi
echo "No developer settings, test hooks, whistle menu, or self-test in $(basename "$dll")"
