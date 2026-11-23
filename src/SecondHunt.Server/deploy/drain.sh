#!/usr/bin/env bash
# The restart drain before a deploy restarts the server (deploy.ps1 runs it unless -NoDrain; DEPLOY.md, docs/RELEASE.md
# "Server deploy"; server 0.10.3, HubDrain.cs). Writes "restart <now> <cap>" to the drain file, so the running server tells
# everyone, ends the Find Game searches, takes no new lobbies, joins or served games and lets the running matches finish;
# then waits until /v1/health says " drained" (the last match ended, or the cap passed), at most cap + 30 s.
# A server older than 0.10.3 reads any drain file as "drain on" (no new served games) and never says " drained": then this
# waits only for " served 0/" (its peer-to-peer matches it can't see: those drop at the restart, as before).
# The drain file stays (deploy.ps1 removes it right before the restart); an owner's own "drain on" file is kept as drain.keep
# and put back then.
#   bash drain.sh [cap seconds, default 600]
# (the test hub: DRAIN_FILE=/var/lib/secondhunt-test/drain DRAIN_HEALTH=http://127.0.0.1:8790/v1/health DRAIN_USER=secondhunt)
set -u
cap=${1:-600}
f=${DRAIN_FILE:-/var/lib/secondhunt/drain}
health() { curl -fsS --max-time 5 "${DRAIN_HEALTH:-http://127.0.0.1:8080/v1/health}" 2>/dev/null || true; }
if [ -f "$f" ] && ! grep -q '^restart ' "$f"; then cp -p "$f" "$f.keep"; fi
printf 'restart %s %s\n' "$(date +%s)" "$cap" > "$f.tmp" && chown "${DRAIN_USER:-secondhunt}:${DRAIN_USER:-secondhunt}" "$f.tmp" && mv -f "$f.tmp" "$f"
echo "drain: restart requested (cap $cap s): $(health)"
end=$(( $(date +%s) + cap + 30 ))
next=0
sleep 3
while :; do
    h=$(health)
    case "$h" in
        *" drained"*) echo "drain: drained: $h"; exit 0 ;;
        "") echo "drain: the server doesn't answer: going ahead"; exit 0 ;;
        *" restart "*) ;;
        *" served 0/"*) echo "drain: an older server (no restart drain), no served match running: going ahead: $h"; exit 0 ;;
        *" served "*) ;;
        *) echo "drain: an older server (no restart drain): going ahead: $h"; exit 0 ;;
    esac
    now=$(date +%s)
    if [ "$now" -ge "$end" ]; then echo "drain: waited past the cap: going ahead: $h"; exit 0; fi
    if [ "$now" -ge "$next" ]; then echo "drain: waiting: $h"; next=$(( now + 30 )); fi
    sleep 2
done
