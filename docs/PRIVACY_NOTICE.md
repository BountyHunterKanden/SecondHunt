> Not legal advice. Written from what the server code actually keeps (`src/SecondHunt.Server/Store.cs`, `HubChat.cs`, `deploy/`). If
> the server changes what it keeps, update this file (and docs/ONLINE_RULES_IN_APP.md, the in-app page) in the same change.

# Second Hunt online play: privacy notice

Second Hunt is a free fan project run by one person (BountyHunterKanden). This notice covers **online play only**.
The campaign, bots and local multiplayer send nothing anywhere. Your game files stay on your device and are never
uploaded.

Contact for anything in this notice, including deleting your data: secondhuntgame@gmail.com.

## What we keep on the server

| What | Why | How long |
|---|---|---|
| Your **online profile key**: a random public key your device made. It isn't linked to your name, email or phone, and we never ask for those | It's how the server knows it's you | Until you delete your profile |
| Your **hunter name and tag** (e.g. KANDEN#4821) and your **friend code** | So others can find and add you | Until you delete your profile |
| Your **Hunter License** numbers (wins, kills, favourite hunter and so on, as the game counts them) | Others can view your license | Until you delete your profile |
| Your **friends, rivals, requests, blocks** and **recent players** lists | The friends features | Until you delete your profile (or remove them) |
| Your **online match count** and any **chat limits** on your profile | A moderator's chat mute | Kept after a profile delete, so a new profile can't wipe a ban or limit |
| **Reports** you send or that are about you, including, for a chat report, **the last few typed lines** of the reported player and of that game | So the moderator can check the report | Handled reports: 30 days. Unhandled: 90 days. Then deleted |
| **Bans**: the banned key, name, reason and end date, and a **scrambled ID of each device** the banned profile logged in from (a one-way hash of the device's own ID: Android's app ID or the computer's install ID) | Keeping a banned player out, also after they clear the app's data or reinstall it | Until the ban ends (or forever for a permanent ban) |
| **Nightly backups** of the above | Recovering from a server failure | The last 14 days |

## What we never keep

- **Voice.** Voice chat goes from player to player (through our relay or Cloudflare's) and is never recorded or stored.
- **Typed chat**, except inside a report. Chat lines sit in the server's memory for at most 3 hours so a report can
  include them; they are never written to disk unless someone reports them.
- **IP addresses.** The server needs your IP address to connect a match, but only holds it in memory while you're
  connected. It isn't written to any log or database.
- **Anything from your device** beyond the above: no contacts, no location, no advertising IDs, no analytics, no
  trackers, no ads. The game sends a scrambled device ID each time it logs in; the server holds it in memory while
  you're connected and only writes it down if your profile is banned (see Bans above).

## Who else handles it

- **Akamai (Linode)** hosts the server in the US (Chicago). Your profile data is stored there.
- **Cloudflare** relays some matches' traffic (its "TURN" service) when two players can't connect directly. It sees your
  IP address and the encrypted match traffic, not your profile. Cloudflare's privacy policy: https://www.cloudflare.com/privacypolicy/
- Nobody else. Nothing is sold or shared for advertising.

If you play from outside the US, your data is sent to and stored in the US.

## Your choices

- **Mute, block, report** any player from their name or their chat line.
- **Voice** is off for strangers by default; turn it on or off per player or for everyone.
- **Hide yourself** from name search (ONLINE PLAY > SETTINGS).
- **Delete your online profile:** ONLINE PLAY > SETTINGS > DELETE ONLINE PROFILE. That erases your name, license,
  friends and lists from the server at once (backups age out within 14 days). Your match count, any chat limit or ban,
  and reports stay for the times above.
- Ask for a copy of your data, or for anything above, at secondhuntgame@gmail.com.

## Children

Online play is for players 13 and older (`docs/LEGAL_AGE_NOTICE_13_PLUS.md`). If you believe a child under 13 has an
online profile, tell us at secondhuntgame@gmail.com and we'll delete it.

## Changes

We'll update this notice when what we keep changes, and show the new version in the game. Last changed: 2026-10-09.
