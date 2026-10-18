#!/usr/bin/env bash
# Replay mode R2b device check (docs/REPLAY_MODE.md): are replays exact between this PC and an Android device?
# Git Bash on Windows. The device is ANDROID_SERIAL (adb's own variable), or the only one attached.
#
#   tests/replays/device_check.sh verify [dump=a-b]
#       PC -> device: pushes the goldens here to <external files>/replays/golden, runs ReplayVerifyActivity (headless,
#       every step checked) and prints its lines ("verify <file>: <verdict>", then "verify: n/m exact on ...").
#       dump=a-b also saves each step's raw state to extract_out/replay/dump_verify_device.txt; diff it against the
#       PC's: dotnet MphRead.Tools.dll -replayverify tests/replays/<file> dump=a-b (-> dump_verify_pc.txt).
#   tests/replays/device_check.sh record [time=40] [arena=<name>] [mode=Battle]
#       device -> PC: plays a bot match on the device (3 bots, level 2), pulls the replay it saves to
#       extract_out/replay/device/ and checks it on the PC with -replayverify.
#
# Installing is not part of this. This script refuses to start if the device is awake with the app in front (the owner may
# be mid-test), and sends the device home and to sleep when it's done.
# Env: ADB, DOTNET, TOOLS (the Tools build folder, default src/MphRead.Tools/bin/Replay/net9.0), SLEEP=0 (don't sleep
# the device at the end).

set -u
repo=$(cd "$(dirname "$0")/../.." && pwd)
adb=${ADB:-adb}
command -v "$adb" >/dev/null 2>&1 || { echo "adb not found: put it on PATH or set ADB"; exit 1; }
dotnet=${DOTNET:-$USERPROFILE/.dotnet/dotnet.exe}
[ -x "$dotnet" ] || dotnet=dotnet
tools=${TOOLS:-$repo/src/MphRead.Tools/bin/Replay/net9.0}
pkg=com.mphrecomp.app
ext=/sdcard/Android/data/$pkg/files
out=$repo/extract_out/replay
export MSYS_NO_PATHCONV=1

die() { echo "device_check: $*" >&2; exit 2; }
dsh() { "$adb" shell "$@" | tr -d '\r'; }

# waits (up to $2 s) for a logcat line matching $1 (tag MPHReplay) since $since; prints it
wait_for() {
    local pattern=$1 limit=$2 waited=0 line
    while [ $waited -lt "$limit" ]; do
        line=$("$adb" logcat -d -v raw -T "$since" -s MPHReplay:* | tr -d '\r' | grep -E "$pattern" | tail -1)
        [ -n "$line" ] && { echo "$line"; return 0; }
        sleep 3; waited=$((waited + 3))
    done
    return 1
}

# SLEEP=0: home only, the screen stays on (a phone the owner is using)
finish() { dsh input keyevent KEYCODE_HOME; [ "${SLEEP:-1}" = 0 ] || dsh input keyevent KEYCODE_SLEEP; }

cmd=${1:-}; shift || true
dump=""; time=40; arena=""; mode=Battle
for a in "$@"; do
    case $a in
        dump=*) dump=${a#dump=} ;;
        time=*) time=${a#time=} ;;
        arena=*) arena=${a#arena=} ;;
        mode=*) mode=${a#mode=} ;;
        *) die "unknown option $a" ;;
    esac
done
[ "$cmd" = verify ] || [ "$cmd" = record ] || die "usage: device_check.sh verify [dump=a-b] | record [time=40] [arena=<name>] [mode=Battle]"

state=$("$adb" get-state 2>/dev/null | tr -d '\r')
[ "$state" = device ] || die "no device on adb (state: ${state:-none})"
model=$(dsh getprop ro.product.model)
echo "device: $model ($(dsh dumpsys battery | grep -m1 -E '^ *level:' | tr -d ' '))"

# the wake check, on its own, then the foreground gate
wake=$(dsh dumpsys power | grep -o 'mWakefulness=[A-Za-z]\+' | head -1)
echo "$wake"
if [ "$wake" = mWakefulness=Awake ]; then
    fg=$(dsh dumpsys activity activities | grep -i -m1 resumedactivity)
    case $fg in *$pkg*) die "the app is in front on an awake device (the owner may be testing): not starting. $fg" ;; esac
fi
dsh dumpsys package $pkg | grep -m1 lastUpdateTime
since="$(dsh date +%s).000"
dsh input keyevent KEYCODE_WAKEUP; dsh wm dismiss-keyguard

if [ "$cmd" = verify ]; then
    dsh mkdir -p $ext/replays/golden
    # a folder adb makes is shell's (rwxrws---): Android 13 (the Odin) then denies the app a listing
    dsh chmod 2777 $ext/replays/golden
    n=0
    for f in "$repo"/tests/replays/*.mphreplay; do
        "$adb" push "$(cygpath -w "$f")" "$ext/replays/golden/" >/dev/null || die "push failed: $f"
        n=$((n + 1))
    done
    echo "pushed $n golden(s)"
    extra=()
    [ -n "$dump" ] && extra=(--es dump "$dump")
    dsh am start -S -n $pkg/$pkg.ReplayVerifyActivity "${extra[@]}" >/dev/null
    done_line=$(wait_for '^verify: [0-9]+/[0-9]+ exact' $((n * 120 + 60)))
    found=$?
    "$adb" logcat -d -v raw -T "$since" -s MPHReplay:* | tr -d '\r' | grep -E '^(verifying|verify)'
    if [ -n "$dump" ]; then
        mkdir -p "$out"
        "$adb" logcat -d -v raw -T "$since" -s MPHReplayDump:* | tr -d '\r' | grep -v '^-----' > "$out/dump_verify_device.txt"
        echo "dump: $out/dump_verify_device.txt ($(wc -l < "$out/dump_verify_device.txt") lines)"
    fi
    finish
    [ $found -eq 0 ] || die "no result line in time (crash? logcat -s MPHReplay AndroidRuntime)"
    counts=${done_line#verify: }; counts=${counts%% *}
    [ "${counts%/*}" = "${counts#*/}" ] || exit 1
    exit 0
fi

# record
extra=(--es match "$mode" --es bots 3 --es botlevel 2 --es time "$time")
[ -n "$arena" ] && extra+=(--es arena "$arena")
dsh am start -S -n $pkg/$pkg.CampaignActivity "${extra[@]}" >/dev/null
echo "match started ($mode, ${time} s); waiting for the replay"
saved=$(wait_for '^saved ' $((time + 180))) || { finish; die "no 'saved' line (recording off in RECOMP SETTINGS? the match still running?)"; }
echo "$saved"
finish
name=${saved#saved }; name=${name%%: *}
mkdir -p "$out/device"
"$adb" pull "$ext/replays/$name" "$(cygpath -w "$out/device/$name")" >/dev/null || die "pull failed: $ext/replays/$name"
[ -f "$tools/MphRead.Tools.dll" ] || die "no Tools build in $tools (build -c Replay first)"
cd "$tools" && "$dotnet" MphRead.Tools.dll -replayverify "$(cygpath -w "$out/device/$name")"
