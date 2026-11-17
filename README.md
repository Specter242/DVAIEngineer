# AI Engineer for Derail Valley

A Unity Mod Manager mod that gives a locomotive an engineer, as in
Railroader: send it to a track, a cut of cars, or a job, and it finds the
route, sets the switches, and drives there while you run another train.
Queue more orders behind it, or hand it several shunting jobs at once and it
plans the moves. Diesel, electric, and steam, from the engineer's tablet, the
Comms Tablet's Engineers app, or the Comms Radio.

How to play it: [PLAYER-README.md](PLAYER-README.md) (the zip carries it as
`README.md`). Version history: [CHANGELOG.md](CHANGELOG.md).

## Install

Download the installable ZIP from
[GitHub Releases](https://github.com/Specter242/DVAIEngineer/releases/latest)
and install it with Unity Mod Manager. Unity Mod Manager also finds updates
through this repository's `repository.json`. Release tags hold the source of
that version.

Requires Derail Valley (Build 99) and Unity Mod Manager. Optional: Realistic
Brakes, Realistic Firing, the Comms Tablet, Railroad Contracts, Skin Manager,
and Multiplayer (the host drives). Game and third-party mod assemblies are not
included.

## Build

With the .NET 10 SDK, Mono (for the adapter check), and your own installed
game, whose assemblies the build references in place
(`Directory.Build.props`; override with `-p:DvInstallDir=...`):

    dotnet test tests/AIEngineer.Core.Tests     # the engine-free core's tests
    scripts/check-adapter.sh                    # the game members reached by name, under Mono
    scripts/deploy.sh                           # build and install into the game's Mods folder
    scripts/package.sh                          # the release zip and its manifest in dist/ (a clean tree only)

`scripts/package.sh` builds the public release (`-p:PublicRelease=true`):
without the in-game self-test, test hooks, or developer settings, which a
development build keeps.

## Layout

- `src/AIEngineer.Core`: the engine-free model (track graph, router, speed
  planning, driving, job planning), unit tested in `tests/AIEngineer.Core.Tests`.
- `src/AIEngineer`: the mod: reads the game's tracks and trains, drives the
  locos, and draws the engineer's tablet, the Comms Tablet's Engineers app,
  and the Comms Radio mode.
- `tools/AIEngineer.AdapterCheck`: checks, under Mono, the game's and other
  mods' members the mod reaches by reflection.

MIT licensed: [LICENSE](LICENSE).
