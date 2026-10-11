# Golden replays (Replay mode R2c)

Recorded matches that every build must still play exactly. Each `.mphreplay` holds a match's settings, both RNG
start values, a fingerprint of the arena's entity/collision/node files (a device with different ones refuses it),
the local player's input per step and a checksum of the gameplay state after each step. No game data.
See `docs/REPLAY_MODE.md` (R2c).

Their specs (mode, arena, hunters, seed, length) live in `src/MphRead.Tools/ReplayTest.cs` (`Goldens`). They're
recorded by the Tools' scripted player, drawn at 256x192 as the app records, each starting like the first match after
launch, so re-recording one from its spec gives the same file byte for byte.

Check (needs the desktop extraction; ~11 s):

    dotnet MphRead.Tools.dll -replaygolden [view=nodraw|draw|third|spectate] [only=<name>]

or after a Tools build: `dotnet build src/MphRead.Tools/MphRead.Tools.csproj -c <config> -p:ReplayGolden=true`
(fails the build if one isn't exact).

If one fails, the game logic changed. If that was intended, bump `ReplayFile.CurrentSimVersion` (players' older
replays are then refused up front instead of stopping part-way), then re-record:

    dotnet MphRead.Tools.dll -replaygolden record

`record` is refused while a golden of the current sim version still fails, so a logic change can't slip in without
the version bump. If the failure is a bug, `-replayplay <file> trace=a-b` prints the player around the step.

On a device (Android, PC <-> device; `device_check.sh` explains itself): `tests/replays/device_check.sh verify`
plays these on the device and says which are exact; `device_check.sh record` plays a bot match on the device and
checks its replay on the PC. Install the build first (the board's install rules); the script doesn't install.
`SLEEP=0` keeps the screen on at the end (a phone in use). Done 2026-10-07: PC <-> Odin <-> Samsung all exact.
