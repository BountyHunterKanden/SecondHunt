> **Filled in 2026-10-09 from the owner's answers** (name, report checks, appeals, 13+ everywhere). Not legal advice.
> (docs/ONLINE_PLAY.md section 8)

# Second Hunt online play: 13 and older

Shown the first time a player opens ONLINE PLAY (before an online profile is made), and again under ONLINE PLAY >
SETTINGS > ABOUT ONLINE PLAY. Short enough for one screen; the full texts are the privacy notice
(`docs/PRIVACY_NOTICE.md`) and the code of conduct (`docs/CODE_OF_CONDUCT.md`).

## The screen text

**ONLINE PLAY IS FOR PLAYERS 13 AND OLDER**

Online, you play and talk with people you don't know: quick messages, typed chat and voice.

- You must be **13 or older** to play online. If you're younger, play the campaign, the bots or local multiplayer
  instead: none of those go online.
- Typed chat goes through a word filter, but people can still be unkind. You can **mute**, **block** or **report**
  anyone from their name.
- Voice is **off for strangers** until you turn it on. Voice is never recorded.
- Never share your real name, address, school, phone number or passwords.
- Breaking the code of conduct can get your online profile banned.

[ I'M 13 OR OLDER ]   [ BACK ]

More: privacy notice, code of conduct (ABOUT ONLINE PLAY).

## Notes for the owner

- **Why 13:** the US children's privacy law (COPPA) puts strict parental-consent rules on online services that knowingly
  collect data from children under 13. A 13+ line, with no data collected from players who say they're younger, is
  the usual way small games stay out of it. The UK and EU set the age for a child's own consent to data use at 13
  (UK) to 16 (some EU countries). The owner's choice (2026-10-09): 13 and up everywhere, one rule for all players.
- **It's a self-declaration** (no ID checks): the risk assessment (`docs/UK_ONLINE_SAFETY_ACT_RISK_ASSESSMENT.md`)
  explains why that fits a service of this size and what else limits the risk.
- **Where players see it** (no popups, owner 2026-10-09): a dim line on the ONLINE PLAY hub (`OnlineMenus.DrawHub`),
  the rules page from OPTIONS' bottom-left line (docs/ONLINE_RULES_IN_APP.md), and ONLINE PLAY > SETTINGS > ABOUT
  ONLINE PLAY (`NoticeText` in `src/MphRecomp.Core/Frontend/OnlineMenus.Settings.cs`).
