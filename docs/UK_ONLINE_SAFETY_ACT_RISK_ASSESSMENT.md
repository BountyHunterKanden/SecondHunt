> **Filled in 2026-10-09 from the owner's answers** (name, report checks, appeals, 13+ everywhere). Not legal advice.
> A plain-language
> first version of the written risk assessments the UK Online Safety Act 2023 asks of user-to-user services (Ofcom's
> illegal content risk assessment and children's risk assessment guidance), sized for a one-person free fan game. The
> owner checks it against Ofcom's current guidance before UK players can chat, signs it, and reviews it on the date
> below.

# Second Hunt online play: UK Online Safety Act risk assessment

| | |
|---|---|
| Service | Second Hunt online play (Android and Windows app; server `play.secondhuntonline.com`) |
| Provider / named person responsible | BountyHunterKanden, sole developer and moderator |
| Contact | secondhuntgame@gmail.com |
| Assessment date | 2026-10-09 |
| Next review | 12 months after approval, or before any change to the features in section 1, whichever comes first |
| Size | Small: a free hobby project, about 65 online profiles on 2026-10-09, mostly test profiles |

## 1. What users can do with each other

| Feature | Who with | Notes |
|---|---|---|
| Matches (2-8 players) | Friends, invited players, or strangers (Find Game, public games) | Gameplay only |
| Hunter names (NAME#tag) and Hunter License | Anyone can see them | Names pass the word filter; the license holds game statistics only, no free text, no pictures |
| Quick messages | Players in the same game | 18 fixed phrases ("GG", "Nice shot!", ...): no free text |
| Typed chat | Players in the same game (lobby, shown in matches) | 80 characters, word filter, rate limits; **no private messages**, no chat outside a game |
| Voice chat | Players in the same game | **Off for strangers by default**, on for friends; never recorded |
| Friends, rivals, requests, parties, invites | Players who add each other | Requests can be refused; block stops them |

Not offered: private/direct messages, pictures, video, links that open, user profiles with free text, user-made
content of any kind, payments, trading, or any way to find a player's location.

## 2. Illegal content risks

Ofcom's priority harms, rated for this service (low / medium / high), with why:

| Harm | Risk | Why |
|---|---|---|
| Child sexual exploitation and abuse (grooming) | **Medium** | Strangers can talk to each other in games (typed, and voice if both turn it on). No private messages, no images, no off-game contact tools, 13+ only, voice off for strangers by default lower it; gaming chat is a known grooming route, so it isn't low |
| Hate, harassment, threats, abuse | **Medium** | Typed chat and voice between strangers; names. The word filter, mute, block and report lower it |
| Terrorism, extremism | Low | 80-character lines in small game lobbies; no broadcast, no media |
| Fraud and scams | Low | No payments or trading; links aren't clickable |
| Encouraging suicide or self-harm | Low | Possible in chat, nothing in the service promotes it; reportable |
| Drugs, weapons, other priority offences | Low | No media, no marketplace |
| Intimate image abuse, CSAM images | Very low | **No way to send images or files** |
| Foreign interference, other | Very low | |

## 3. Children (13-17)

Online play is for 13 and older, by self-declaration (`docs/LEGAL_AGE_NOTICE_13_PLUS.md`). Players aged 13-17 can use it,
so it's "likely to be accessed by children". Risks to them: contact from adult strangers (grooming), bullying, seeing
swearing or hateful words, sharing personal details. Under-13s may lie about their age; the service has no ID or
facial age checks, which would be out of proportion to its size and features; the lack of private messages and media
is the main protection.

## 4. What's in place (built 2026-10-09, milestone O7; server 0.5.0)

- **13+ notice** before any online profile is made.
- **No private messages, no images, no voice recording.** Chat only exists inside a game.
- **Word filter** on typed chat and names (the LDNOOBW English list plus the owner's own additions, re-read live; it
  catches common spellings: letter swaps, stretched and spaced words). Names that fail become HUNTER.
- **New-profile limits:** typed chat to strangers opens only after 3 online matches with other people; until then it
  reaches only the player's friends and rivals. Quick messages work from the start. This slows ban dodging with fresh
  profiles.
- **Rate limits:** 4 typed lines per 10 s, at least 0.7 s apart, no repeated line within 10 s; quick messages 6 per 10 s.
- **Voice off for strangers** by default; per-player and global switches.
- **Mute** (chat and voice) and **block** any player; **report** from any name or chat line (reasons: name, chat,
  voice, cheating, other). A chat report attaches the reported player's and the game's last lines from the server's
  memory, so reports carry evidence without the service storing chat.
- **Moderation:** the owner's admin tool (report queue, show, dismiss/resolve, chat mute, ban by profile key, ban list,
  filter test); a ban kicks the player within 15 seconds and blocks new logins with that key.
- **Data kept to a minimum** (`docs/PRIVACY_NOTICE.md`): no IP logs, no chat logs, reports deleted after 30/90 days.
- **Code of conduct** shown in the game (`docs/CODE_OF_CONDUCT.md`).

## 5. How reports are handled

- The owner checks the report queue (`admin.ps1 reports`) at least every 2 days and on any
  report marked chat or voice. Each new report also sends a phone alert (high priority for chat, voice and other).
- Grooming, threats of violence, sexual content involving a child: **ban at once**, keep the report, and report to the
  police / the NCA (UK) or NCMEC CyberTipline (US) as the law requires (UK: CEOP, https://www.ceop.police.uk/; US: https://report.cybertip.org/).
- Hate and harassment: chat mute or ban for some days; repeat = permanent ban.
- Players can appeal by writing to the contact address; the owner answers within 14 days.

## 6. Remaining risk and when to look again

Remaining risk after the measures: **low to medium** for grooming and harassment, low for the rest. Look again
(and update this file) before adding any of: private messages, images or other media, voice on for strangers by
default, user-made content, larger lobbies, or if reports show a pattern. Record each review below.

| Date | Change | By |
|---|---|---|
| 2026-10-09 | First draft (O7) | Social O7 thread |
