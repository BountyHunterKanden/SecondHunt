# Putting the Second Hunt server online (milestone O4)

## 1. Rent (the owner's step)
- **A VPS:** Akamai/Linode "Nanode 1 GB" (~$5/mo, 1 TB transfer) in **Chicago or Dallas** (central US: even ping to
  both coasts). Image **Ubuntu 24.04 LTS**. Vultr/DigitalOcean $5-6 plans work the same.
- **SSH key, not a password:** paste the public key from the PC (`%USERPROFILE%\.ssh\secondhunt_deploy.pub`) into the
  provider's "SSH keys" box when creating the server. (Made 2026-10-08: ed25519, no passphrase, comment
  `secondhunt-deploy`; the private half `secondhunt_deploy` never leaves the PC.)
- **A domain (optional for the first tests):** ~$10-12/yr (any registrar). Add an **A record** to the server's IP. If the
  DNS is Cloudflare, set it to **DNS only (grey cloud)**: the match relay is UDP, which Cloudflare's proxy doesn't
  carry. No domain yet = plain `ws://<ip>/v1/ws` for testing.

## 2. Set up + deploy (from the PC)
```
.\deploy.ps1 -Server root@<server ip> -Setup -Domain play.example.com     (or leave -Domain out: no HTTPS yet)
```
setup.sh: updates, Caddy (HTTPS on its own), a `secondhunt` system user, firewall (22, 80, 443 TCP + relay 47780
UDP), SSH keys only (no password logins; the root password still works on Linode's LISH console), the systemd
service, nightly SQLite backups (14 kept). Then the Linux build goes up and starts.
Later updates: `.\deploy.ps1 -Server root@<server ip>`.

## 3. Check
- `https://<domain>/v1/health` (or `http://<ip>/v1/health`) answers `ok <version> online 0` (0.5.0 on 2026-10-09).
- `MphRead.Tools -onlinetest server=wss://<domain>/v1/ws` plays a PC match through the real server and relay.
- Logs: `journalctl -u secondhunt`. Data: `/var/lib/secondhunt/` (database + `backups/`).
- Health also shows the TURN GB counted this month: `ok 0.4.0 online 0 turn 0.00 GB`.

## 4. Cloudflare TURN key (decision 18, server 0.4.0+)
Optional: without it every match uses our own relay, as before. On the server only (never in the repo or this kit):
```
install -d -m 700 /etc/secondhunt
echo 'TURN_KEY_ID=<key id>' > /etc/secondhunt/turn.env
echo 'TURN_KEY_API_TOKEN=<api token>' >> /etc/secondhunt/turn.env
chmod 600 /etc/secondhunt/turn.env
systemctl restart secondhunt && journalctl -u secondhunt -n 20 | grep TURN     (expect "TURN: key set, cap 900 GB a month")
```
The unit reads it with `EnvironmentFile=-/etc/secondhunt/turn.env` (an older unit needs `-Setup` again or that line added +
`systemctl daemon-reload`). Cap: `--turn-cap-gb <n>` on the ExecStart line (default 900 of Cloudflare's free 1,000 GB a month).

Cost: ~$5/mo + domain ~$1/mo. Cloudflare TURN relays near players come with O5 (free tier 1 TB/mo).

## 5. Moderation: the admin tool and the word files (server 0.5.0+, milestone O7)
The admin tool is the server program itself, run on the VPS (no network surface). From the PC:
```
.\admin.ps1 reports                 the open report queue (reports all: handled ones too)
.\admin.ps1 show <id>               one report, with the chat lines a chat report carries
.\admin.ps1 dismiss <id> [note]     nothing to do (its chat text is erased at once)
.\admin.ps1 resolve <id> [note]     acted on it (kept 30 days with its text, then erased)
.\admin.ps1 player <who>            a player: name, matches, chat limits, reports, ban
.\admin.ps1 ban <who> <days|perm> <reason...>   ban the key (kicked within 15 s); their open reports are resolved
.\admin.ps1 unban <who>  /  bans
.\admin.ps1 mute <who> <hours|perm>  /  unmute <who>    typed chat only (quick messages still work)
.\admin.ps1 words "<text>"          what the filter makes of a line, and whether it passes as a name
.\admin.ps1 stats  /  help
```
`<who>` = a 12-digit friend code or NAME#0421. `admin.ps1` defaults to `-Server root@play.secondhuntonline.com` and the
`secondhunt_deploy` key; on the VPS itself the same commands are `secondhunt-admin <command>`.

Word files (`/opt/secondhunt/`): `words.txt` = the LDNOOBW English list (uploaded by every deploy; CC BY 4.0, credited in
THIRD_PARTY_NOTICES.md) and `words_owner.txt` = the owner's own (made empty by the first 0.5.0 deploy, never overwritten):
one entry per line, `word` = filtered as a whole word (with common endings and letter swaps), `*word` = also inside other
words, `-word` = allow a list entry again (e.g. `-butt`, `-sex`, `-xx`), `#` = comment. Both are re-read within 20 s
of an edit; no restart. Check with `.\admin.ps1 words "..."`.

Retention (automatic, at start and daily): handled reports 30 days, open ones 90 days. Typed chat is only in server
memory (3 h) unless a report copies it.

Report alerts (owner 2026-10-09): each new report pushes "new <kind> report. run: admin.ps1 reports" to the owner's
phone through ntfy (chat / voice / other at high priority). Set by `REPORT_ALERT_URL=https://ntfy.sh/<secret topic>` in
`/etc/secondhunt/turn.env` (the unit's EnvironmentFile), then `systemctl restart secondhunt`; the log says "report alerts:
on". The phone: the free ntfy app, subscribe to the same topic name. Only the kind leaves the server (no names, codes or
chat). The topic is in the owner's notes, not the repo: anyone who knows it could read or post to it, so pick a new one
(edit the line, restart, resubscribe) if it leaks.

## 6. Served games: the match host and its game files (server 0.8.0+, docs/DEDICATED_SERVERS.md)
Every deploy also puts up `SecondHuntMatchHost` (one process per served match) and the unit that passes
`--matchhost /opt/secondhunt/SecondHuntMatchHost --matchhost-data /var/lib/secondhunt/gamedata --max-matches 3`.
Served matches need the owner's own extracted game files on the server, picked by name (`gamedata.txt`, names only):
```
.\gamedata.ps1 -Extract <...\files\AMHE1> -Out <scratch folder>
.\deploy.ps1 -Server root@<server ip> -GameData <scratch folder>          (-GameKey AMHE1 by default)
```
They land in `/var/lib/secondhunt/gamedata/` (service user, 0700, never served, never in the repo); a later deploy
without `-GameData` keeps them. 652 files, 16 MB: room models/collision/entities/nodes, the hunter, weapon, pickup and
effect models, particle files, intro cameras, the bot personality table, HUD string tables and 2 music tables (MphRead's
room load reads them). No cart code (`_bin`: the match host runs with `Extract.NoCodeFiles`), sound, movies or menus.
Health then ends `served N/M` and the log says `served games: on, game version MPH USA/3`. Without the files (or the
match host) served games stay off and every game is player-hosted as before. Before a deploy with people in served
games: `secondhunt-admin drain on` (new games go player-hosted, running matches finish), then `drain off` after.
