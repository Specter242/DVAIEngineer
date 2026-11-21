#!/usr/bin/env bash
# Fails if a build of AIEngineer.dll holds any developer tool the public release
# leaves out (no developer options or test tools in a release): the
# developer settings and their labels, the whistle-timing menu, the settings
# drawer, the test hooks, and the self-test. Names are looked for among the
# DLL's identifiers (ASCII) and its string literals (UTF-16).
set -euo pipefail
command -v strings >/dev/null || { echo "strings is required" >&2; exit 1; }
dll="${1:?usage: scripts/check-public-dll.sh AIEngineer.dll}"
if [[ ! -f "$dll" || ! -r "$dll" ]]; then
    echo "Cannot read assembly: $dll" >&2
    exit 1
fi
has_match() {
    local rc=0
    grep "$@" && return 0 || rc=$?
    if (( rc == 1 )); then
        return 1
    fi
    echo "grep failed with exit code $rc" >&2
    exit "$rc"
}
names=(ShowDeveloper DeveloperShown WhistleShortest WhistleShortestLong WhistleQuietEnds WhistleBounce WhistleDip
       WhistleVersion MoveOldWhistle WhistleMenu SettingsDrawer DrawForTest HitInPlace SelfTest
       LastNotified FireButtons OnButtonFor OnButton Cards PaperReplaced GlassAt LocoForTest
       PressSlotForTest OpenForTest PickTrackForTest DoForTest PickForTest GlassReadForTest FireLitForTest
       RefreshForTest UndoForTest)
labels=("Show developer settings" "Engineer ticks per second" "Log each route planned" "Log what the helper's whistle hears"
        "Developer" "selftest" "self-test")
found=()
literals="$(strings -el "$dll")"
for n in "${names[@]}"; do
    if has_match -a -q -F "$n" "$dll" || has_match -q -F "$n" <<<"$literals"; then found+=("$n"); fi
done
for l in "${labels[@]}"; do
    if has_match -q -i -F "$l" <<<"$literals"; then found+=("\"$l\""); fi
done
if (( ${#found[@]} )); then
    echo "The public build holds developer tools: ${found[*]}" >&2
    exit 1
fi
echo "No developer settings, test hooks, whistle menu, or self-test in $(basename "$dll")"
