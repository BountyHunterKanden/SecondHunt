# Game endings (owner queue #36): what vanilla does after the ending movie

Owner's report (L3 #2): "Real game ending(s) from the base game instead of our filler menu". Today an ending movie
(`AfterMovie.EndGame`, MphRead `Movie.cs:297`) quits to the ship, `ShipNavigation` classifies it as `CampaignEnd.Ending`
and `CampaignShip.cs` opens the stand-in "MISSION COMPLETE" text panel. MphRead marks the gap as `todo: credits` at
`28_Gorea1B.cs:1028` (bad ending) and `31_Gorea2.cs:946` (good ending).

Found 2026-10-07 from the ROM (US rev 1, ov0 = the front end; session Start menu UI rebuild (cont.) [16cf9c]); built the
same day (section 3).

## 1. The sequence

1. **Ending movie** (bad: `BadEndingPart1` + `BadEndingPart2`; good: `GoodEnding`), then the game hands over to the
   front end.
2. **Front-end entry picks the credits.** ov0 init 0x21251ec reads game flags at `0x020CBD90 + 0x74`:
   bit 11 -> start page 6 with the good-ending flag cleared (bad ending); bit 12 -> page 6 with the flag set (good
   ending). The flag is bit 6 of `0x02147D04 + 0x68` (front-end state). Other bits of the same word pick the other
   entry pages: bit 9 -> 18 (main menu), bit 10 -> 13 (title), 13 -> 19, 14 -> 20, 15 -> 16, 16 -> 47/48, 8 -> 66.
3. **Credits, pages 6-12**, the same pages Options > CREDITS shows (ours already: `FrontendSession` case 46). After an
   ending they **can't be skipped**: call 11 (credits skipped, 0x2128afc) only acts when bit 4 of `state + 0x6c` is set,
   which is the "came from Options" bit.
4. **Credits over** (call 12, 0x2133168): bit 4 of `state + 0x6c` set -> clear it, page 21 (Options); else good-ending
   flag -> **page 49**, else **page 50**.
5. **Results page** (MARM pages 49 / 50, our `-fedump`):
   - page 49 (good): "CONGRATULATIONS", "mission complete!", complete %, total game time, gorea battle time, deaths,
     hunters and guardians defeated, OK button.
   - page 50 (bad): "mission complete...?", the same rows, gorea battle time shows "incomplete"; no CONGRATULATIONS.
   - Items fade in on delays 20-120 ticks; OK = A or a tap on the OK widget -> call 206.
6. **OK** (call 206, 0x212ef00, r0 = page):
   - page 49 only: cfg `0x020E8318 |= 0x100 | 0x200` (the sound-test unlock bits; matches the BizHawk note in
     `project_ship_readiness`: "true-ending results OK sets cfg bits 8/9"). Ours keeps the sound test always unlocked
     (owner 2026-10-05), so nothing to do there.
   - both: `cfg + 0x10 |= 0x10`, then the message box 0x2137c78 with string 104 "saving to the game card. do not touch
     the game card or the power button." for 90 ticks (the same box as file select's saves).
   - after the box (callback 0x2137af4): **page 2**, the boot logos -> intro movie -> title (call 8).

## 2. The numbers (ov0 0x211e538, story save = RAM 0x020E9710)

- total game time = story +0x109C + 0x1094 + 0x1098 + 0x10A0 (seconds), "%dh %02dm"
- gorea battle time = +0x1094 (Gorea 1) + 0x1098 (Gorea 2), "%dm %02ds" with the hours dropped; page 50 shows the
  static "incomplete" instead
- deaths = +0x1090 (MphRead `DeathTotal`), hunters and guardians defeated = +0x1088 (`HunterKills`)
- complete = arm9 0x205ae40 (the file select's completion %), "%d%s" with the game's "%"
- The game timer (arm9 0x2054980) adds time to +0x1094 in room 91 (Gorea_b1), +0x1098 in room 92 (Gorea_b2),
  +0x10A0 in rooms 89-92 while story +0x84 bit 1 is set, +0x109C everywhere else.
- The filler also keeps top-three records in the options save (`0x020E8318 + 0x7c / 0x88 / 0x94`: Gorea 1, Gorea 2 (good
  ending only), total). Not kept by us.

## 3. Built (2026-10-07, PC-verified; not on a device yet)

- MphRead `GameState.ModeStateAdventure` (the upstream "todo: game timer/boss record stuff", board T4): Gorea 1 / 2 time
  into `StorySave.Stats.Gorea1Time` / `Gorea2Time`; `CampaignSaves` keeps them (older saves read 0).
- `CampaignShip.OnHostEnded` (App): an ending saves the file (as the stand-in's YES did: cleared, at Oubliette), sets
  `GameEnding.Pending` (good = room 92; complete %, play time, Gorea time, deaths, kills) and leaves through `ExitSink`.
  A host with no menus (no ExitSink) keeps the stand-in panel.
- Front end (`GameEnding.cs`, `FrontendSession`): `ReturnPage()` opens at page 6 when an ending is pending; `Start` takes
  it; call 11 does nothing during ending credits; call 12 fills the placeholders (found by position on page 49) and goes
  to 49 / 50; A on the results page presses OK where it is (the item has no focused look); call 206 -> the file select's
  "saving to the game card" box -> page 2. Android's `GameActivity.OnRestart` now goes through `Start` too.
- PC: `FE_ENDING=good|bad -feflow "<t>:ok"`: credits 6-12 (A / B / taps ignored) -> 49 / 50 with the numbers -> a tap on
  OK (`ok` step) -> saving box -> logos 2-5 -> intro movie -> title 13. Options' credits still skip back to 21.

### 3a. Checked against vanilla and fixed (2026-10-07 PM, session Start menu UI rebuild (cont. 2))

BizHawk, USA rev 1: from a gameplay state, pause -> QUIT -> YES, with the front end's entry word (`0x020CBD90+0x74`)
switched to bit 12 / 11 when ov0's init (0x21251ec) reads it (handoffs/start-menu-ui-rebuild_2026-10-07-endings-files/
bizhawk/ending.lua).
- **Credits after an ending can't be skipped** (B, A, a tap all reach call 11, the page stays): as ours.
- **Credits timing: was ~2.4x too fast, fixed.** Vanilla held page 6 805 frames, 7-9 and 11 705, 10 765, 12 659 (to call
  12): 5050 frames in all; ours ~2200. ov0 0x2141214 runs a page's timers only while the page state (`+0x1E`) is 2, i.e.
  once its items have all come in (page 7: the 191-tick logo), and counts the timer from there (page clock `G+0x2C`
  against `G+0x30`, firing when it crosses the delay; timer flags bit 1 = fired, bit 0 = repeat). BizHawk read-hook on
  page 1's timer record: first read 11 frames after page 1 opened, as its 5-tick ESRB fade ended. `MenuEngine` now counts
  timers from `Settled` (`SettledFrame`), which also moves page 1 (ESRB, +5 ticks) and the delay-1 timers of 25/27/53
  (now 1 tick after their items are in). Ours now: page 7->8 351 ticks (702 frames, vanilla 705), 10->11 381 (762 / 765),
  6..49 5124 frames (vanilla 5050). `-mpmenu name` / `check` still pass.
- **A does not press OK on the results page**: A held on page 49 for 100 frames, no call 206; a tap on OK (DS 237,173)
  makes it. Ours pressed OK with A (a guess); removed: OK is touch / click only, as vanilla's.
- **OK**: call 206 (r0 = 49) -> message box string 104 -> 112 frames -> page 2 -> logos. Ours: the same box from
  `FileSelect.ShowSaving`, 49 ticks (98 frames).
- **What OK saves** (SRAM before/after): the loaded story file's next block (the story RAM as it is), plus the save's
  sections 4 and 5 (options / license; empty before in that save). So vanilla saves the story file on OK; ours saves it
  as the ending movie ends (same file, earlier: an app closed during the credits keeps the ending's save in ours, not in
  vanilla).
- **What the ending itself writes before the menus** (ov1 0x21045c8 good / 0x21046a8 bad, the "ending" game modes in the
  arm9 mode table at 0x20caf70 / 0x20caf50), all into RAM for OK to save: good only: options `+0x04 |= 0x2000000`; both: if
  complete >= 100 (arm9 0x205ae40): options `+0x10 |= 0x40`, `0x020EB8A8+0x18 |= 0x20000000`; always: options `+0x10 |=
  0x20`, `0x020EB8A8+0x18 |= 0x10000000`, story `+0x1084 |= 2` (MphRead `Field1084`); then 0x2104764 = the save prep (rooms
  89/91/92 room state cleared, their visited bits cleared, Omega Cannon bit and trigger state bit 12 cleared, the slot-3
  weapon replaced if it was the Omega Cannon; ours: ShipNavigation.PrepareForSave), then entry bit 12 / 11 and mode
  change (2, 5, 1). Ours sets none of the options / license bits or `Field1084` (see section 4).

### 3b. Test route (2026-10-07 PM)

- Core `Campaign/GoreaSkip.cs` (BossSkip's Gorea half, via `BossSkip.Step` in Gorea_b1/b2): steady hits through the game's
  damage path on whatever a player's shot could hurt (`CollideBeam`, not `Invincible`): 1A's arms, 1B's seal sphere up to
  each phase's 1000 mark, Gorea 2's sphere with an Omega Cannon beam the player never fires.
- Tools `-endingtest [bad|chain|good|all]`: headless, 3/3 PASS. bad: Gorea_b1 -> BadEndingPart1+2 -> Ending in 91 (Gorea 1
  47 s); chain: Gorea_b1 with the seal targets (bit 12 set in 1B's last phase, prompt 52 answered) -> Gorea2Intro ->
  Gorea_b2 -> Omega Cannon taken -> GoodEnding -> 92 (49 + 11 s); good: Gorea_b2 alone -> GoodEnding -> 92.
- Windows app: `SecondHunt --room Gorea_b1 --loadout all --boss down --movies off`: the real chain, CampaignShip ->
  GameEnding.Pending -> ExitGame -> menus at page 6 -> credits (11.7 s a page) -> page 50 (12%, "incomplete", 0h 00m) ->
  click OK -> saving box -> logos -> title.
- Android dev launcher: "Endings" (bad: Gorea_b1; good: Gorea_b2, take the Omega Cannon and step into the arena); the
  campaign's exit opens the menus (AppNav.ReturnToGame). Built, not on a device yet.

## 4. Still open

- A device run of a real ending (PC done end to end; Android route built, the Odin was off).
- The results page sits side by side like any page without its own composition (MenuLayout); a combined 16:9 layout is
  the owner's call.
- The ending's options / license bits and story `Field1084` (section 3a): vanilla's unlock / "beaten" marks; ours keeps
  none yet (the sound test is always unlocked in ours). What each one unlocks is not traced.
- The cfg top-3 records (Gorea 1, Gorea 2, total) are not kept.
- Rooms 89-92 bucket (+0x10A0) and the story +0x1084 bit 1 timer rule: only matter for the total, which uses our own play
  time.
- The saving box: ours 98 frames, vanilla's 112 after the ending's OK (the same box as file select's).
