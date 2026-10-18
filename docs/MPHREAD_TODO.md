# MphRead TODO list

MphRead marks every unfinished or uncertain spot in its code with a `todo` comment. Some are things the original game does that MphRead doesn't do yet, some are open questions about how the original works, and many are notes about the code itself, like a field whose purpose isn't known yet. This page lists every one of them from MphRead's own code, and crosses off the ones Second Hunt has fixed.

As of 2026-10-12: **1457** TODOs in MphRead, **41** fixed, **1400** still open (of those, 5 partly fixed and 1 to check), **5** kept on purpose because they're bugs in the original game that MphRead copies, and **11** checked against the original game and already matching it.

How to read it:
- Each line is one TODO: where it is, the method it's in, and the comment as MphRead wrote it.
- ~~Crossed off~~ means it's fixed. The note after it says what changed and where that's written down.
- "partly fixed" means part of it is done and the rest is still open.
- "kept on purpose" means the original game has the bug too, so it stays.
- "checked" means the question in the comment was answered from the original game's code and MphRead already does what it does, so nothing changes. The comment stays in the code.
- "needs a check" means the comment is gone from the code but no fix is written down for it yet.
- Line numbers point at the code as it is today. A TODO that's no longer in the code points at MphRead's original line instead (marked "upstream line").
- Most of the frame rate TODOs are exact already: MphRead runs the game at 60 Hz instead of the original's 30, and marks every timer, speed and step it converted. They stay open here until each one is checked.

## Summary

| Area | TODOs | Fixed | Open | Kept on purpose | Checked |
|---|---:|---:|---:|---:|---:|
| [Frame rate](#frame-rate) | 831 | 24 | 805 | 0 | 2 |
| [Naming](#naming) | 173 | 0 | 173 | 0 | 0 |
| [Rendering](#rendering) | 35 | 0 | 35 | 0 | 0 |
| [Wi-Fi rules](#wi-fi-rules) | 21 | 0 | 21 | 0 | 0 |
| [Viewer and debug tools](#viewer-and-debug-tools) | 15 | 0 | 15 | 0 | 0 |
| [Bots](#bots) | 13 | 0 | 13 | 0 | 0 |
| [HUD and menus](#hud-and-menus) | 12 | 0 | 12 | 0 | 0 |
| [Multiplayer license](#multiplayer-license) | 11 | 7 | 4 | 0 | 0 |
| [Sound and music](#sound-and-music) | 9 | 0 | 9 | 0 | 0 |
| [Saves](#saves) | 5 | 0 | 5 | 0 | 0 |
| [Rumble](#rumble) | 2 | 0 | 2 | 0 | 0 |
| [Everything else](#everything-else) | 330 | 10 | 306 | 5 | 9 |
| **All** | **1457** | **41** | **1400** | **5** | **11** |

An area is picked from the words in each comment ("FPS stuff" is frame rate, "names" is naming, "wifi" is the Wi-Fi rules, and so on), so a few TODOs sit in a neighbouring area.

## Fixed so far

41 fixed and 5 partly fixed. Each one also appears in its area below.

- [x] ~~[PlayerCamera.cs:206](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerCamera.cs#L206) (upstream line) in `PlayerEntity.UpdateCameraThird1`: sktodo: FPS stuff?~~ (fixed: morph ball camera follow matches the original per tick, measured; docs/ACCURACY.md A3)
- [x] ~~[PlayerCamera.cs:555](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerCamera.cs#L555) (upstream line) in `PlayerEntity.UpdateCameraThird2`: sktodo: FPS stuff?~~ (fixed: morph ball camera follow matches the original per tick, measured; docs/ACCURACY.md A3)
- [x] ~~[PlayerInput.cs:608](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L608) (upstream line) in `PlayerEntity.ProcessBiped`: todo: FPS stuff~~ (fixed: camera lean while strafing now matches the original, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:614](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L614) (upstream line) in `PlayerEntity.ProcessBiped`: todo: FPS stuff~~ (fixed: camera lean while strafing now matches the original, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:633](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L633) (upstream line) in `PlayerEntity.ProcessBiped`: sktodo: FPS stuff~~ (fixed: camera lean settles at the original's rate, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:649](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L649) (upstream line) in `PlayerEntity.ProcessBiped`: sktodo: FPS stuff~~ (fixed: camera lean settles at the original's rate, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:705](../src/MphRead/Entities/Players/PlayerInput.cs#L705) in `PlayerEntity.ProcessBiped`: todo: FPS stuff?~~ (fixed: speeds stay in the original's units and are halved where used; the jump arc is exact; docs/ACCURACY.md A, A1)
- [x] ~~[PlayerInput.cs:709](../src/MphRead/Entities/Players/PlayerInput.cs#L709) in `PlayerEntity.ProcessBiped`: todo: FPS stuff?~~ (fixed: jump speed checked: the jump arc matches the original tick for tick; docs/ACCURACY.md A1)
- [x] ~~[PlayerInput.cs:2125](../src/MphRead/Entities/Players/PlayerInput.cs#L2125) in `PlayerEntity.ProcessMovement`: todo: FPS stuff~~ (fixed: with the gravity step fixed, the jump arc matches the original tick for tick; docs/ACCURACY.md A1)
- [x] ~~[PlayerInput.cs:2130](../src/MphRead/Entities/Players/PlayerInput.cs#L2130) in `PlayerEntity.ProcessMovement`: todo: FPS stuff~~ (fixed: position step fixed (gravity and walking): exact per tick; docs/ACCURACY.md A1, A7)
- [x] ~~[TeleporterEntity.cs:306](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/TeleporterEntity.cs#L306) (upstream line) in `TeleporterEntity.ActivateAnimaton`: todo: FPS stuff~~ (fixed: the portal opening sound plays every time, checked against the original; README "What's been fixed")
- [x] ~~[PlayerEntity.cs:1921](../src/MphRead/Entities/Players/PlayerEntity.cs#L1921) in `PlayerEntity.TakeDamage`: todo: license info~~ (fixed: the license kill streak ends here (HostLicenseStreakEnd); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[PlayerEntity.cs:2257](../src/MphRead/Entities/Players/PlayerEntity.cs#L2257) in `PlayerEntity.TakeDamage`: todo: update license info~~ (fixed: a team kill ends the license kill streak (HostLicenseStreakEnd); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[PlayerEntity.cs:2270](../src/MphRead/Entities/Players/PlayerEntity.cs#L2270) in `PlayerEntity.TakeDamage`: todo: update license info~~ (fixed: kills, headshots and beam kills go to the license (HostLicenseKill); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[PlayerEntity.cs:2290](../src/MphRead/Entities/Players/PlayerEntity.cs#L2290) in `PlayerEntity.TakeDamage`: todo: update license info~~ (fixed: beam kills go to the license (HostLicenseKill); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[GameState.cs:262](../src/MphRead/GameState.cs#L262) in `GameState.UpdateTime`: todo: update license info etc.~~ (fixed: match time goes to the license (HostLicenseTime); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[GameState.cs:299](../src/MphRead/GameState.cs#L299) in `GameState.ProcessFrame`: todo: update MP playtime to license info~~ (fixed: match time goes to the license (HostLicenseTime); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[GameState.cs:1237](../src/MphRead/GameState.cs#L1237) in `GameState.UpdateState`: todo: update license info~~ (fixed: wins, places, streaks and arena counts are counted after the match like the original; docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[28_Gorea1B.cs:1039](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1039) in `Enemy28Entity.Behavior02`: todo: credits~~ (fixed: the credits and results after the ending are built in the menus (Second Hunt 0.2.0); docs/ENDINGS.md section 3)
- [x] ~~[31_Gorea2.cs:958](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L958) in `Enemy31Entity.State17`: todo: credits~~ (fixed: the credits and results after the ending are built in the menus (Second Hunt 0.2.0); docs/ENDINGS.md section 3)
- [x] ~~[PlayerEntity.cs:2127](../src/MphRead/Entities/Players/PlayerEntity.cs#L2127) in `PlayerEntity.TakeDamage`: todo: unlock hunter for multiplayer~~ (fixed: multiplayer hunters unlock from the campaign saves like the original; docs/MULTIPLAYER_MENU.md "UNLOCKS")
- [x] ~~[PlayerInput.cs:1177](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L1177) (upstream line) in `PlayerEntity.ProcessAlt`: todo?: if touch movement for alt form was a thing, this would need extra conditions~~ (fixed: touch controls built from the original's code (PlayerTouch.cs); PlayerTouch.cs; board release T11 (touch check passed 2026-10-10))
- [x] ~~[PlayerInput.cs:1346](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L1346) (upstream line) in `PlayerEntity.ProcessAlt`: todo: touch roll~~ (fixed: touch roll built from the original's code (PlayerTouch.cs); PlayerTouch.cs; board release T11 (touch check passed 2026-10-10))
- [x] ~~[PlayerInput.cs:1514](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L1514) (upstream line) in `PlayerEntity.ProcessAlt`: todo: touch boost~~ (fixed: touch boost built from the original's code (PlayerTouch.cs); PlayerTouch.cs; board release T11 (touch check passed 2026-10-10))
- [x] ~~[RoomEntity.cs:55](../src/MphRead/Entities/RoomEntity.cs#L55) in `RoomEntity.Setup`: todo: unlock the corresponding multiplayer arena when visiting a new planet~~ (fixed: multiplayer arenas unlock from the campaign saves like the original; docs/MULTIPLAYER_MENU.md "UNLOCKS")
- [x] ~~[Movie.cs:644](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Formats/Movie.cs#L644) (upstream line) in `VxDecoder.Decode`: todo: avoid list allocation? buffers are recycled anyway, but when decoding in realtime, we only need 4 frames~~ (fixed: the rewritten movie decoder keeps at most four frames and recycles them; Formats/Vx/VxDecoder.cs, docs/CLEANROOM_QUEUE.md)
- [x] ~~[Movie.cs:943](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Formats/Movie.cs#L943) (upstream line) in `VxFrame.Decode`: todo: this doesn't need to be allocated repeatedly~~ (fixed: the rewritten movie decoder reuses one bit reader; Formats/Vx/VxDecoder.cs, docs/CLEANROOM_QUEUE.md)

Partly fixed:

- [ ] [PlayerCollision.cs:831](../src/MphRead/Entities/Players/PlayerCollision.cs#L831) in `PlayerEntity.HandleCollision`: todo: FPS stuff (partly fixed: walkable slopes fixed for the biped, measured; morph balls, edges and steep faces keep the old step; docs/ACCURACY.md A9)
- [ ] [PlayerInput.cs:2027](../src/MphRead/Entities/Players/PlayerInput.cs#L2027) in `PlayerEntity.ProcessMovement`: todo: FPS stuff (partly fixed: walking and the rolling morph balls fixed, measured; jump pads and the Trace, Sylux and Weavel alt forms keep the old step; docs/ACCURACY.md A7, A8)
- [ ] [EntityBase.cs:462](../src/MphRead/Entities/EntityBase.cs#L462) in `EntityBase.GetDrawInfo`: todo: hide attached effects (partly fixed: the gunship hides its thruster flames when hidden; other entities still to do; README "What's been fixed", PlatformEntity.cs)
- [ ] [GameState.cs:445](../src/MphRead/GameState.cs#L445) in `GameState.ProcessFrame`: todo: update license info, stop SFX (partly fixed: license counted after the match; stopping sounds still to do; docs/MULTIPLAYER_MENU.md "Counting")
- [ ] [GameState.cs:515](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/GameState.cs#L515) (upstream line) in `GameState.ModeStateAdventure`: todo: game timer/boss record stuff (partly fixed: Gorea 1 and 2 times are kept and saved; boss records still to do (new TODO in GameState.cs); docs/ENDINGS.md section 3)
- [x] ~~[31_Gorea2.cs:546](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L546) (upstream line) in `Enemy31Entity`: todo: this is pretty jerky~~ (fixed: Gorea 2 hovered at twice the original speed with twice the jitter; now the original 30 Hz rate, rev 1 0x213D58C; docs/ACCURACY.md)
- [x] ~~[28_Gorea1B.cs:494](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L494) in `Enemy28Entity.Func213B678`: sktodo: FPS stuff?~~ (fixed: the grapple reeled the player in twice as fast (step per 30 Hz tick applied every 60 Hz frame); rev 1 0x213B640; docs/ACCURACY.md)
- [x] ~~[28_Gorea1B.cs:630](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/28_Gorea1B.cs#L630) (upstream line) in `Enemy28Entity.State07`: sktodo: FPS stuff~~ (fixed: the grapple swing added Gorea's speed at half the original rate; rev 1 0x2139AEC; docs/ACCURACY.md)
- [x] ~~[31_Gorea2.cs:773](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L773) (upstream line) in `Enemy31Entity.State08`: todo: FPS stuff~~ (fixed: the timer clamp was `Max` (at least 15 ticks) where the original takes `Min` (at most 15); rev 1 0x213F124; docs/ACCURACY.md)
- [x] ~~[31_Gorea2.cs:1007](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L1007) (upstream line) in `Enemy31Entity.Func213D80C`: todo: FPS stuff~~ (fixed: the snap distance was halved like a per-frame step, but it is a distance (0x555 = 1/3); rev 1 0x213D7C8; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:302](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L302) in `Enemy24Entity.EnemyInitialize`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:302](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L302) in `Enemy24Entity.EnemyInitialize`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:562](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L562) in `Enemy24Entity.State00`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:923](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L923) in `Enemy24Entity.State13`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:924](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L924) in `Enemy24Entity.State13`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:1435](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1435) in `Enemy24Entity.Behavior20`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:1477](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1477) in `Enemy24Entity.Behavior21`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[31_Gorea2.cs:144](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L144) (upstream line) in `Enemy31Entity.CheckPlayerCollision`: todo: FPS stuff~~ (fixed: the push went up and down too (the original zeroes Y) and stacked twice per tick on held contact; rev 1 0x2140854; docs/ACCURACY.md)
- [x] ~~[31_Gorea2.cs:292](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L292) (upstream line) in `Enemy31Entity.UpdateLaserTargeting`: todo: FPS stuff~~ (fixed: 1/24 per frame is not the original's 1/12 per tick (it closes the gap geometrically); now 1 - sqrt(1 - 341/4096) per frame; rev 1 0x213F614; docs/ACCURACY.md)

## Frame rate

831 TODOs: 24 fixed, 805 open, 2 checked.

### AreaVolumeEntity.cs

`src/MphRead/Entities/AreaVolumeEntity.cs`

- [ ] [AreaVolumeEntity.cs:44](../src/MphRead/Entities/AreaVolumeEntity.cs#L44) in `AreaVolumeEntity.AreaVolumeEntity`: todo: FPS stuff

### ArtifactEntity.cs

`src/MphRead/Entities/ArtifactEntity.cs`

- [ ] [ArtifactEntity.cs:129](../src/MphRead/Entities/ArtifactEntity.cs#L129) in `ArtifactEntity.Process`: todo: FPS stuff

### BeamProjectileEntity.cs

`src/MphRead/Entities/BeamProjectileEntity.cs`

- [ ] [BeamProjectileEntity.cs:162](../src/MphRead/Entities/BeamProjectileEntity.cs#L162) in `BeamProjectileEntity.Process`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:645](../src/MphRead/Entities/BeamProjectileEntity.cs#L645) in `BeamProjectileEntity.CheckCollision`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:675](../src/MphRead/Entities/BeamProjectileEntity.cs#L675) in `BeamProjectileEntity.CheckCollision`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:862](../src/MphRead/Entities/BeamProjectileEntity.cs#L862) in `BeamProjectileEntity.PlayRicochetSfx`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:952](../src/MphRead/Entities/BeamProjectileEntity.cs#L952) in `BeamProjectileEntity.OnCollision`: todo: frame time stuff
- [ ] [BeamProjectileEntity.cs:1471](../src/MphRead/Entities/BeamProjectileEntity.cs#L1471) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1474](../src/MphRead/Entities/BeamProjectileEntity.cs#L1474) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1477](../src/MphRead/Entities/BeamProjectileEntity.cs#L1477) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1481](../src/MphRead/Entities/BeamProjectileEntity.cs#L1481) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1504](../src/MphRead/Entities/BeamProjectileEntity.cs#L1504) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1555](../src/MphRead/Entities/BeamProjectileEntity.cs#L1555) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1647](../src/MphRead/Entities/BeamProjectileEntity.cs#L1647) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1844](../src/MphRead/Entities/BeamProjectileEntity.cs#L1844) in `BeamProjectileEntity.Spawn`: todo: FPS stuff
- [ ] [BeamProjectileEntity.cs:1846](../src/MphRead/Entities/BeamProjectileEntity.cs#L1846) in `BeamProjectileEntity.Spawn`: todo: FPS stuff

### BombEntity.cs

`src/MphRead/Entities/BombEntity.cs`

- [ ] [BombEntity.cs:44](../src/MphRead/Entities/BombEntity.cs#L44) in `BombEntity.Initialize`: todo: FPS stuff
- [ ] [BombEntity.cs:243](../src/MphRead/Entities/BombEntity.cs#L243) in `BombEntity.Process`: todo: FPS stuff
- [ ] [BombEntity.cs:515](../src/MphRead/Entities/BombEntity.cs#L515) in `BombEntity.ProcessTargeting`: todo: FPS stuff
- [ ] [BombEntity.cs:516](../src/MphRead/Entities/BombEntity.cs#L516) in `BombEntity.ProcessTargeting`: todo: FPS stuff
- [ ] [BombEntity.cs:528](../src/MphRead/Entities/BombEntity.cs#L528) in `BombEntity.ProcessTargeting`: todo: FPS stuff

### CamSeqEntity.cs

`src/MphRead/Entities/CamSeq/CamSeqEntity.cs`

- [ ] [CamSeqEntity.cs:80](../src/MphRead/Entities/CamSeq/CamSeqEntity.cs#L80) in `CamSeqEntity.Process`: todo: FPS stuff
- [ ] [CamSeqEntity.cs:84](../src/MphRead/Entities/CamSeq/CamSeqEntity.cs#L84) in `CamSeqEntity.Process`: todo: FPS stuff
- [ ] [CamSeqEntity.cs:183](../src/MphRead/Entities/CamSeq/CamSeqEntity.cs#L183) in `CamSeqEntity.TryStart`: todo: FPS stuff
- [ ] [CamSeqEntity.cs:235](../src/MphRead/Entities/CamSeq/CamSeqEntity.cs#L235) in `CamSeqEntity.Start`: todo: FPS stuff

### CameraSequence.cs

`src/MphRead/Entities/CamSeq/CameraSequence.cs`

- [ ] [CameraSequence.cs:99](../src/MphRead/Entities/CamSeq/CameraSequence.cs#L99) in `CameraSequence.Process`: todo: FPS stuff

### DoorEntity.cs

`src/MphRead/Entities/DoorEntity.cs`

- [ ] [DoorEntity.cs:323](../src/MphRead/Entities/DoorEntity.cs#L323) in `DoorEntity.Process`: todo: FPS stuff
- [ ] [DoorEntity.cs:373](../src/MphRead/Entities/DoorEntity.cs#L373) in `DoorEntity.Process`: todo: FPS stuff

### 00_WarWasp.cs

`src/MphRead/Entities/Enemies/00_WarWasp.cs`

- [ ] [00_WarWasp.cs:56](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L56) in `Enemy00Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [00_WarWasp.cs:100](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L100) in `Enemy00Entity.StartMovingToward`: todo: FPS stuff
- [ ] [00_WarWasp.cs:118](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L118) in `Enemy00Entity.MoveInCircle`: todo: FPS stuff
- [ ] [00_WarWasp.cs:211](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L211) in `Enemy00Entity.Behavior01`: todo: FPS stuff
- [ ] [00_WarWasp.cs:374](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L374) in `Enemy00Entity.Behavior09`: todo: FPS stuff

### 01_Zoomer.cs

`src/MphRead/Entities/Enemies/01_Zoomer.cs`

- [ ] [01_Zoomer.cs:54](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L54) in `Enemy01Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [01_Zoomer.cs:78](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L78) in `Enemy01Entity.EnemyProcess`: todo: FPS stuff
- [ ] [01_Zoomer.cs:91](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L91) in `Enemy01Entity.EnemyProcess`: todo: FPS stuff
- [ ] [01_Zoomer.cs:146](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L146) in `Enemy01Entity.EnemyProcess`: todo: FPS stuff
- [ ] [01_Zoomer.cs:151](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L151) in `Enemy01Entity.EnemyProcess`: todo: FPS stuff
- [ ] [01_Zoomer.cs:154](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L154) in `Enemy01Entity.EnemyProcess`: todo: FPS stuff
- [ ] [01_Zoomer.cs:168](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L168) in `Enemy01Entity.EnemyProcess`: todo: FPS stuff

### 02_Temroid.cs

`src/MphRead/Entities/Enemies/02_Temroid.cs`

- [ ] [02_Temroid.cs:109](../src/MphRead/Entities/Enemies/02_Temroid.cs#L109) in `Enemy02Entity.Func21648A4`: todo: FPS stuff
- [ ] [02_Temroid.cs:115](../src/MphRead/Entities/Enemies/02_Temroid.cs#L115) in `Enemy02Entity.Func21648A4`: todo: FPS stuff
- [ ] [02_Temroid.cs:121](../src/MphRead/Entities/Enemies/02_Temroid.cs#L121) in `Enemy02Entity.Func21648A4`: todo: FPS stuff
- [ ] [02_Temroid.cs:122](../src/MphRead/Entities/Enemies/02_Temroid.cs#L122) in `Enemy02Entity.Func21648A4`: todo: FPS stuff
- [ ] [02_Temroid.cs:160](../src/MphRead/Entities/Enemies/02_Temroid.cs#L160) in `Enemy02Entity.Func21648A4`: todo: FPS stuff
- [ ] [02_Temroid.cs:187](../src/MphRead/Entities/Enemies/02_Temroid.cs#L187) in `Enemy02Entity.Func216469C`: sktodo: FPS?
- [ ] [02_Temroid.cs:198](../src/MphRead/Entities/Enemies/02_Temroid.cs#L198) in `Enemy02Entity.Func216469C`: sktodo: FPS?
- [ ] [02_Temroid.cs:202](../src/MphRead/Entities/Enemies/02_Temroid.cs#L202) in `Enemy02Entity.Func216469C`: sktodo: FPS?
- [ ] [02_Temroid.cs:207](../src/MphRead/Entities/Enemies/02_Temroid.cs#L207) in `Enemy02Entity.Func216469C`: todo: FPS stuff
- [ ] [02_Temroid.cs:248](../src/MphRead/Entities/Enemies/02_Temroid.cs#L248) in `Enemy02Entity.EnemyProcess`: todo: FPS stuff
- [ ] [02_Temroid.cs:280](../src/MphRead/Entities/Enemies/02_Temroid.cs#L280) in `Enemy02Entity.EnemyProcess`: todo: FPS stuff
- [ ] [02_Temroid.cs:342](../src/MphRead/Entities/Enemies/02_Temroid.cs#L342) in `Enemy02Entity.UpdateHeight`: todo: FPS stuff
- [ ] [02_Temroid.cs:457](../src/MphRead/Entities/Enemies/02_Temroid.cs#L457) in `Enemy02Entity.State08`: todo: FPS stuff

### 03_Petrasyl1.cs

`src/MphRead/Entities/Enemies/03_Petrasyl1.cs`

- [ ] [03_Petrasyl1.cs:67](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L67) in `Enemy03Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:89](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L89) in `Enemy03Entity.UpdateState`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:98](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L98) in `Enemy03Entity.UpdateState`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:100](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L100) in `Enemy03Entity.UpdateState`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:109](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L109) in `Enemy03Entity.UpdateState`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:130](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L130) in `Enemy03Entity.State01`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:137](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L137) in `Enemy03Entity.State01`: todo: FPS stuff
- [ ] [03_Petrasyl1.cs:153](../src/MphRead/Entities/Enemies/03_Petrasyl1.cs#L153) in `Enemy03Entity.State01`: todo: FPS stuff

### 04_Petrasyl2.cs

`src/MphRead/Entities/Enemies/04_Petrasyl2.cs`

- [ ] [04_Petrasyl2.cs:72](../src/MphRead/Entities/Enemies/04_Petrasyl2.cs#L72) in `Enemy04Entity.UpdateState`: todo: FPS stuff
- [ ] [04_Petrasyl2.cs:93](../src/MphRead/Entities/Enemies/04_Petrasyl2.cs#L93) in `Enemy04Entity.UpdateMovement`: todo: FPS stuff
- [ ] [04_Petrasyl2.cs:97](../src/MphRead/Entities/Enemies/04_Petrasyl2.cs#L97) in `Enemy04Entity.UpdateMovement`: todo: FPS stuff
- [ ] [04_Petrasyl2.cs:116](../src/MphRead/Entities/Enemies/04_Petrasyl2.cs#L116) in `Enemy04Entity.UpdateMovement`: todo: FPS stuff
- [ ] [04_Petrasyl2.cs:123](../src/MphRead/Entities/Enemies/04_Petrasyl2.cs#L123) in `Enemy04Entity.UpdateMovement`: todo: FPS stuff
- [ ] [04_Petrasyl2.cs:135](../src/MphRead/Entities/Enemies/04_Petrasyl2.cs#L135) in `Enemy04Entity.UpdateMovement`: todo: FPS stuff

### 05_Petrasyl3.cs

`src/MphRead/Entities/Enemies/05_Petrasyl3.cs`

- [ ] [05_Petrasyl3.cs:61](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L61) in `Enemy05Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:71](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L71) in `Enemy05Entity.UpdateState`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:94](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L94) in `Enemy05Entity.UpdateState`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:102](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L102) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:132](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L132) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:142](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L142) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:150](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L150) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:158](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L158) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:180](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L180) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff
- [ ] [05_Petrasyl3.cs:201](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L201) in `Enemy05Entity.UpdateMovement`: todo: FPS stuff

### 06_Petrasyl4.cs

`src/MphRead/Entities/Enemies/06_Petrasyl4.cs`

- [ ] [06_Petrasyl4.cs:62](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L62) in `Enemy06Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:63](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L63) in `Enemy06Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:74](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L74) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:94](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L94) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:98](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L98) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:99](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L99) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:118](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L118) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:122](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L122) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:123](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L123) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:144](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L144) in `Enemy06Entity.UpdateState`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:155](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L155) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:187](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L187) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:198](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L198) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:206](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L206) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:214](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L214) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:237](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L237) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:258](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L258) in `Enemy06Entity.UpdateMovement`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:272](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L272) in `Enemy06Entity.State0`: todo: FPS stuff
- [ ] [06_Petrasyl4.cs:315](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L315) in `Enemy06Entity.State4`: todo: FPS stuff

### 10_BarbedWarWasp.cs

`src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs`

- [ ] [10_BarbedWarWasp.cs:76](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L76) in `Enemy10Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [10_BarbedWarWasp.cs:122](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L122) in `Enemy10Entity.StartMovingToward`: todo: FPS stuff
- [ ] [10_BarbedWarWasp.cs:140](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L140) in `Enemy10Entity.MoveInCircle`: todo: FPS stuff
- [ ] [10_BarbedWarWasp.cs:199](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L199) in `Enemy10Entity.State3`: todo: FPS stuff
- [ ] [10_BarbedWarWasp.cs:209](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L209) in `Enemy10Entity.State3`: todo: FPS stuff
- [ ] [10_BarbedWarWasp.cs:330](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L330) in `Enemy10Entity.Behavior04`: todo: FPS stuff
- [ ] [10_BarbedWarWasp.cs:349](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L349) in `Enemy10Entity.Behavior05`: todo: FPS stuff

### 11_Shriekbat.cs

`src/MphRead/Entities/Enemies/11_Shriekbat.cs`

- [ ] [11_Shriekbat.cs:120](../src/MphRead/Entities/Enemies/11_Shriekbat.cs#L120) in `Enemy11Entity.Behavior01`: todo: FPS stuff
- [ ] [11_Shriekbat.cs:122](../src/MphRead/Entities/Enemies/11_Shriekbat.cs#L122) in `Enemy11Entity.Behavior01`: todo: FPS stuff
- [ ] [11_Shriekbat.cs:137](../src/MphRead/Entities/Enemies/11_Shriekbat.cs#L137) in `Enemy11Entity.Behavior02`: todo: FPS stuff
- [ ] [11_Shriekbat.cs:154](../src/MphRead/Entities/Enemies/11_Shriekbat.cs#L154) in `Enemy11Entity.Behavior03`: todo: FPS stuff
- [ ] [11_Shriekbat.cs:156](../src/MphRead/Entities/Enemies/11_Shriekbat.cs#L156) in `Enemy11Entity.Behavior03`: todo: FPS stuff

### 12_Geemer.cs

`src/MphRead/Entities/Enemies/12_Geemer.cs`

- [ ] [12_Geemer.cs:54](../src/MphRead/Entities/Enemies/12_Geemer.cs#L54) in `Enemy12Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [12_Geemer.cs:57](../src/MphRead/Entities/Enemies/12_Geemer.cs#L57) in `Enemy12Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [12_Geemer.cs:126](../src/MphRead/Entities/Enemies/12_Geemer.cs#L126) in `Enemy12Entity.EnemyProcess`: todo: FPS stuff
- [ ] [12_Geemer.cs:139](../src/MphRead/Entities/Enemies/12_Geemer.cs#L139) in `Enemy12Entity.EnemyProcess`: todo: FPS stuff
- [ ] [12_Geemer.cs:194](../src/MphRead/Entities/Enemies/12_Geemer.cs#L194) in `Enemy12Entity.EnemyProcess`: todo: FPS stuff
- [ ] [12_Geemer.cs:199](../src/MphRead/Entities/Enemies/12_Geemer.cs#L199) in `Enemy12Entity.EnemyProcess`: todo: FPS stuff
- [ ] [12_Geemer.cs:202](../src/MphRead/Entities/Enemies/12_Geemer.cs#L202) in `Enemy12Entity.EnemyProcess`: todo: FPS stuff
- [ ] [12_Geemer.cs:216](../src/MphRead/Entities/Enemies/12_Geemer.cs#L216) in `Enemy12Entity.EnemyProcess`: todo: FPS stuff
- [ ] [12_Geemer.cs:243](../src/MphRead/Entities/Enemies/12_Geemer.cs#L243) in `Enemy12Entity.EnemyTakeDamage`: todo: FPS stuff
- [ ] [12_Geemer.cs:248](../src/MphRead/Entities/Enemies/12_Geemer.cs#L248) in `Enemy12Entity.EnemyTakeDamage`: todo: FPS stuff

### 16_Blastcap.cs

`src/MphRead/Entities/Enemies/16_Blastcap.cs`

- [ ] [16_Blastcap.cs:38](../src/MphRead/Entities/Enemies/16_Blastcap.cs#L38) in `Enemy16Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [16_Blastcap.cs:42](../src/MphRead/Entities/Enemies/16_Blastcap.cs#L42) in `Enemy16Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [16_Blastcap.cs:98](../src/MphRead/Entities/Enemies/16_Blastcap.cs#L98) in `Enemy16Entity.Behavior00`: todo: FPS stuff
- [ ] [16_Blastcap.cs:177](../src/MphRead/Entities/Enemies/16_Blastcap.cs#L177) in `Enemy16Entity.Behavior06`: todo: FPS stuff

### 18_AlimbicTurret.cs

`src/MphRead/Entities/Enemies/18_AlimbicTurret.cs`

- [ ] [18_AlimbicTurret.cs:71](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L71) in `Enemy18Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:74](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L74) in `Enemy18Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:139](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L139) in `Enemy18Entity.State0`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:140](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L140) in `Enemy18Entity.State0`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:182](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L182) in `Enemy18Entity.State3`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:205](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L205) in `Enemy18Entity.Behavior00`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:249](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L249) in `Enemy18Entity.Behavior02`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:280](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L280) in `Enemy18Entity.Behavior05`: todo: FPS stuff
- [ ] [18_AlimbicTurret.cs:290](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L290) in `Enemy18Entity.EnemyGetDrawInfo`: todo: FPS stuff

### 19_Cretaphid.cs

`src/MphRead/Entities/Enemies/19_Cretaphid.cs`

- [ ] [19_Cretaphid.cs:45](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L45) in `Enemy19Entity`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:46](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L46) in `Enemy19Entity`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:91](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L91) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:115](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L115) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:119](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L119) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:121](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L121) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:125](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L125) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:127](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L127) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:131](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L131) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:133](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L133) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:134](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L134) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:135](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L135) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:137](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L137) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:138](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L138) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:139](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L139) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:141](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L141) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:142](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L142) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:143](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L143) in `Enemy19Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:352](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L352) in `Enemy19Entity.Sub2135F54`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:366](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L366) in `Enemy19Entity.Sub2135F54`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:380](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L380) in `Enemy19Entity.Sub2135F54`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:441](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L441) in `Enemy19Entity.HandleMessage`: todo: FPS stuff
- [ ] [19_Cretaphid.cs:553](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L553) in `Enemy19Entity.Behavior03`: todo: FPS stuff

### 20_CretaphidEye.cs

`src/MphRead/Entities/Enemies/20_CretaphidEye.cs`

- [ ] [20_CretaphidEye.cs:85](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L85) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:89](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L89) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:93](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L93) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:102](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L102) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:106](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L106) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:110](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L110) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:125](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L125) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:129](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L129) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:133](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L133) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:142](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L142) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:146](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L146) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:150](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L150) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:159](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L159) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:163](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L163) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:167](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L167) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:178](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L178) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:182](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L182) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:186](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L186) in `Enemy20Entity.UpdateState`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:389](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L389) in `Enemy20Entity.EnemyTakeDamage`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:478](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L478) in `Enemy20Entity.EnemyGetDrawInfo`: todo: FPS stuff
- [ ] [20_CretaphidEye.cs:491](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L491) in `Enemy20Entity.EnemyGetDrawInfo`: todo: FPS stuff

### 23_PsychoBit.cs

`src/MphRead/Entities/Enemies/23_PsychoBit.cs`

- [ ] [23_PsychoBit.cs:92](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L92) in `Enemy23Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:93](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L93) in `Enemy23Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:184](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L184) in `Enemy23Entity.EnemyTakeDamage`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:185](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L185) in `Enemy23Entity.EnemyTakeDamage`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:220](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L220) in `Enemy23Entity.UpdateMoveTarget`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:266](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L266) in `Enemy23Entity.UpdateSpeed`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:329](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L329) in `Enemy23Entity.State03`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:330](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L330) in `Enemy23Entity.State03`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:391](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L391) in `Enemy23Entity.Behavior00`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:418](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L418) in `Enemy23Entity.Behavior01`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:419](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L419) in `Enemy23Entity.Behavior01`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:471](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L471) in `Enemy23Entity.Behavior05`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:489](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L489) in `Enemy23Entity.Behavior06`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:493](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L493) in `Enemy23Entity.Behavior06`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:497](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L497) in `Enemy23Entity.Behavior06`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:498](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L498) in `Enemy23Entity.Behavior06`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:513](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L513) in `Enemy23Entity.Behavior07`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:553](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L553) in `Enemy23Entity.MoveAway`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:554](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L554) in `Enemy23Entity.MoveAway`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:595](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L595) in `Enemy23Entity.Behavior11`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:596](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L596) in `Enemy23Entity.Behavior11`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:614](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L614) in `Enemy23Entity.Behavior12`: todo: FPS stuff
- [ ] [23_PsychoBit.cs:615](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L615) in `Enemy23Entity.Behavior12`: todo: FPS stuff

### 24_Gorea1A.cs

`src/MphRead/Entities/Enemies/24_Gorea1A.cs`

- [ ] [24_Gorea1A.cs:102](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L102) in `GoreaEnemyEntityBase.SeekTargetFacing`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:202](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L202) in `GoreaEnemyEntityBase.IsAtEndFrame`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:302](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L302) in `Enemy24Entity.EnemyInitialize`: todo: FPS stuff
- [x] ~~[24_Gorea1A.cs:302](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L302) in `Enemy24Entity.EnemyInitialize`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:302](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L302) in `Enemy24Entity.EnemyInitialize`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [ ] [24_Gorea1A.cs:488](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L488) in `Enemy24Entity.IncrementAllMaterialColors`: todo: FPS stuff
- [x] ~~[24_Gorea1A.cs:562](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L562) in `Enemy24Entity.State00`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [ ] [24_Gorea1A.cs:627](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L627) in `Enemy24Entity.State02`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:636](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L636) in `Enemy24Entity.State03`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:691](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L691) in `Enemy24Entity.CheckTargeting`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:701](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L701) in `Enemy24Entity.CheckTargeting`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:842](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L842) in `Enemy24Entity.State08`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:894](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L894) in `Enemy24Entity.State12`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:922](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L922) in `Enemy24Entity.State13`: todo: FPS stuff
- [x] ~~[24_Gorea1A.cs:923](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L923) in `Enemy24Entity.State13`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [x] ~~[24_Gorea1A.cs:924](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L924) in `Enemy24Entity.State13`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [ ] [24_Gorea1A.cs:1017](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1017) in `Enemy24Entity.Behavior04`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1033](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1033) in `Enemy24Entity.Behavior05`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1039](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1039) in `Enemy24Entity.Behavior05`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1051](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1051) in `Enemy24Entity.Behavior06`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1080](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1080) in `Enemy24Entity.Behavior07`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1111](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1111) in `Enemy24Entity.StopAndSetUp`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1112](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1112) in `Enemy24Entity.StopAndSetUp`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1114](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1114) in `Enemy24Entity.StopAndSetUp`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1133](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1133) in `Enemy24Entity.StartShots`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1135](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1135) in `Enemy24Entity.StartShots`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1136](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1136) in `Enemy24Entity.StartShots`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1137](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1137) in `Enemy24Entity.StartShots`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1156](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1156) in `Enemy24Entity.StartShots`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1157](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1157) in `Enemy24Entity.StartShots`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1224](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1224) in `Enemy24Entity.Behavior08`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1324](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1324) in `Enemy24Entity.TrySprintingRoomInVolume`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1340](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1340) in `Enemy24Entity.Behavior12`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1378](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1378) in `Enemy24Entity.Behavior15`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1411](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1411) in `Enemy24Entity.Behavior18`: todo: FPS stuff
- [x] ~~[24_Gorea1A.cs:1435](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1435) in `Enemy24Entity.Behavior20`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [ ] [24_Gorea1A.cs:1462](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1462) in `Enemy24Entity.RegenerateArms`: todo: FPS stuff
- [x] ~~[24_Gorea1A.cs:1477](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1477) in `Enemy24Entity.Behavior21`: todo: FPS stuff~~ (fixed: these timers count down to -1 in the original (N + 1 ticks); now + 1 frame at 60 Hz instead of half a tick early; rev 1 0x2134484; docs/ACCURACY.md)
- [ ] [24_Gorea1A.cs:1533](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1533) in `Enemy24Entity.DrawArmRegen`: todo: FPS stuff
- [ ] [24_Gorea1A.cs:1555](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1555) in `Enemy24Entity.UpdateArmMaterials`: todo: FPS stuff

### 26_GoreaArm.cs

`src/MphRead/Entities/Enemies/26_GoreaArm.cs`

- [ ] [26_GoreaArm.cs:78](../src/MphRead/Entities/Enemies/26_GoreaArm.cs#L78) in `Enemy26Entity.UpdateWeapon`: todo: FPS stuff
- [ ] [26_GoreaArm.cs:180](../src/MphRead/Entities/Enemies/26_GoreaArm.cs#L180) in `Enemy26Entity.EnemyTakeDamage`: todo: FPS stuff

### 28_Gorea1B.cs

`src/MphRead/Entities/Enemies/28_Gorea1B.cs`

- [ ] [28_Gorea1B.cs:90](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L90) in `Enemy28Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:91](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L91) in `Enemy28Entity.EnemyInitialize`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:107](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L107) in `Enemy28Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:111](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L111) in `Enemy28Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:201](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L201) in `Enemy28Entity.State01`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:243](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L243) in `Enemy28Entity.State03`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:320](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L320) in `Enemy28Entity.Func213BCB8`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:355](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L355) in `Enemy28Entity.UpdateGrappleDrawMatrix`: todo: FPS stuff // sktodo: confirm
- [ ] [28_Gorea1B.cs:373](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L373) in `Enemy28Entity.UpdateGrappleDrawInt`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:375](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L375) in `Enemy28Entity.UpdateGrappleDrawInt`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:390](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L390) in `Enemy28Entity.Func213B90C`: todo: FPS stuff // sktodo: confirm
- [x] ~~[28_Gorea1B.cs:494](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L494) in `Enemy28Entity.Func213B678`: sktodo: FPS stuff?~~ (fixed: the grapple reeled the player in twice as fast (step per 30 Hz tick applied every 60 Hz frame); rev 1 0x213B640; docs/ACCURACY.md)
- [ ] [28_Gorea1B.cs:538](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L538) in `Enemy28Entity.Func213C624`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:565](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L565) in `Enemy28Entity.Func213C4DC`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:576](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L576) in `Enemy28Entity.Func213C4DC`: sktodo: FPS stuff // sktodo: confirm
- [ ] [28_Gorea1B.cs:604](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L604) in `Enemy28Entity.TickGrappleDamage`: todo: FPS stuff
- [x] ~~[28_Gorea1B.cs:630](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/28_Gorea1B.cs#L630) (upstream line) in `Enemy28Entity.State07`: sktodo: FPS stuff~~ (fixed: the grapple swing added Gorea's speed at half the original rate; rev 1 0x2139AEC; docs/ACCURACY.md)
- [ ] [28_Gorea1B.cs:642](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L642) in `Enemy28Entity.State07`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:748](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L748) in `Enemy28Entity.State11`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:783](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L783) in `Enemy28Entity.Func213B2B4`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:852](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L852) in `Enemy28Entity.Func213AF2C`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:858](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L858) in `Enemy28Entity.Func213AF2C`: sktodo: FPS stuff? (checked: the original moves the Trocra by Gorea's per-tick speed (rev 1 0x213AF34); `_speed` is already per 60 Hz frame)
- [ ] [28_Gorea1B.cs:884](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L884) in `Enemy28Entity.Func213AF2C`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:905](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L905) in `Enemy28Entity.Func213B188`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:940](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L940) in `Enemy28Entity.State12`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:960](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L960) in `Enemy28Entity.Deactivate`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1012](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1012) in `Enemy28Entity.Behavior00`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1090](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1090) in `Enemy28Entity.Behavior03`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1101](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1101) in `Enemy28Entity.Behavior04`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:1134](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1134) in `Enemy28Entity.Behavior06`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1162](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1162) in `Enemy28Entity.Behavior09`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1163](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1163) in `Enemy28Entity.Behavior09`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1168](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1168) in `Enemy28Entity.Behavior09`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1237](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1237) in `Enemy28Entity.Behavior12`: sktodo: FPS stuff
- [ ] [28_Gorea1B.cs:1257](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1257) in `Enemy28Entity.Behavior14`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:1268](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1268) in `Enemy28Entity.Behavior15`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:1339](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1339) in `Enemy28Entity.EnemyGetDrawInfo`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:1354](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1354) in `Enemy28Entity.EnemyGetDrawInfo`: todo: FPS stuff
- [ ] [28_Gorea1B.cs:1376](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1376) in `Enemy28Entity.DrawMindTricks`: todo: FPS stuff

### 29_GoreaSealSphere1.cs

`src/MphRead/Entities/Enemies/29_GoreaSealSphere1.cs`

- [ ] [29_GoreaSealSphere1.cs:110](../src/MphRead/Entities/Enemies/29_GoreaSealSphere1.cs#L110) in `Enemy29Entity.EnemyTakeDamage`: todo: FPS stuff

### 30_Trocra.cs

`src/MphRead/Entities/Enemies/30_Trocra.cs`

- [ ] [30_Trocra.cs:126](../src/MphRead/Entities/Enemies/30_Trocra.cs#L126) in `Enemy30Entity.EnemyTakeDamage`: todo: FPS stuff

### 31_Gorea2.cs

`src/MphRead/Entities/Enemies/31_Gorea2.cs`

- [ ] [31_Gorea2.cs:93](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L93) in `Enemy31Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [31_Gorea2.cs:94](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L94) in `Enemy31Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [31_Gorea2.cs:95](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L95) in `Enemy31Entity.EnemyInitialize`: todo: FPS stuff
- [x] ~~[31_Gorea2.cs:144](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L144) (upstream line) in `Enemy31Entity.CheckPlayerCollision`: todo: FPS stuff~~ (fixed: the push went up and down too (the original zeroes Y) and stacked twice per tick on held contact; rev 1 0x2140854; docs/ACCURACY.md)
- [ ] [31_Gorea2.cs:192](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L192) in `Enemy31Entity.UpdateChestMaterial`: todo: FPS stuff
- [ ] [31_Gorea2.cs:251](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L251) in `Enemy31Entity.HandleMessage`: todo: FPS stuff
- [ ] [31_Gorea2.cs:267](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L267) in `Enemy31Entity.Teleport`: todo: FPS stuff
- [x] ~~[31_Gorea2.cs:292](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L292) (upstream line) in `Enemy31Entity.UpdateLaserTargeting`: todo: FPS stuff~~ (fixed: 1/24 per frame is not the original's 1/12 per tick (it closes the gap geometrically); now 1 - sqrt(1 - 341/4096) per frame; rev 1 0x213F614; docs/ACCURACY.md)
- [ ] [31_Gorea2.cs:316](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L316) in `Enemy31Entity.UpdateLaserTargeting`: todo: FPS stuff (checked: the original steps a fixed 0x155 along the laser per tick (rev 1 0x213F758), so half per frame is right)
- [ ] [31_Gorea2.cs:367](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L367) in `Enemy31Entity.UpdateMaterialColors`: todo: FPS stuff
- [ ] [31_Gorea2.cs:457](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L457) in `Enemy31Entity.Func213D3C8`: todo: FPS stuff
- [ ] [31_Gorea2.cs:468](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L468) in `Enemy31Entity.Func213D3C8`: todo: FPS stuff
- [ ] [31_Gorea2.cs:491](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L491) in `Enemy31Entity.Func213D30C`: todo: FPS stuff
- [ ] [31_Gorea2.cs:508](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L508) in `Enemy31Entity.Func213D30C`: todo: FPS stuff
- [ ] [31_Gorea2.cs:519](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L519) in `Enemy31Entity.Func213D204`: todo: FPS stuff
- [ ] [31_Gorea2.cs:536](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L536) in `Enemy31Entity.Func213D204`: todo: FPS stuff
- [ ] [31_Gorea2.cs:548](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L548) in `Enemy31Entity.Func213D5D0`: todo: FPS stuff?
- [ ] [31_Gorea2.cs:607](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L607) in `Enemy31Entity.State01`: todo: FPS stuff
- [ ] [31_Gorea2.cs:666](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L666) in `Enemy31Entity.State04`: todo: FPS stuff
- [ ] [31_Gorea2.cs:728](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L728) in `Enemy31Entity.State06`: todo: FPS stuff
- [ ] [31_Gorea2.cs:758](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L758) in `Enemy31Entity.State07`: todo: FPS stuff
- [ ] [31_Gorea2.cs:769](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L769) in `Enemy31Entity.State08`: todo: FPS stuff
- [ ] [31_Gorea2.cs:774](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L774) in `Enemy31Entity.State08`: todo: FPS stuff
- [x] ~~[31_Gorea2.cs:773](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L773) (upstream line) in `Enemy31Entity.State08`: todo: FPS stuff~~ (fixed: the timer clamp was `Max` (at least 15 ticks) where the original takes `Min` (at most 15); rev 1 0x213F124; docs/ACCURACY.md)
- [ ] [31_Gorea2.cs:883](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L883) in `Enemy31Entity.State09`: todo: FPS stuff
- [ ] [31_Gorea2.cs:1000](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1000) in `Enemy31Entity.InitTrigger`: todo: FPS stuff
- [x] ~~[31_Gorea2.cs:1007](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L1007) (upstream line) in `Enemy31Entity.Func213D80C`: todo: FPS stuff~~ (fixed: the snap distance was halved like a per-frame step, but it is a distance (0x555 = 1/3); rev 1 0x213D7C8; docs/ACCURACY.md)
- [ ] [31_Gorea2.cs:1023](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1023) in `Enemy31Entity.Func213D80C`: todo: FPS stuff
- [ ] [31_Gorea2.cs:1030](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1030) in `Enemy31Entity.Func213D80C`: sktodo: FPS stuff?
- [ ] [31_Gorea2.cs:1291](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1291) in `Enemy31Entity.Behavior09`: todo: FPS stuff
- [ ] [31_Gorea2.cs:1404](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1404) in `Enemy31Entity.Func2140390`: todo: FPS stuff
- [ ] [31_Gorea2.cs:1462](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1462) in `Enemy31Entity.Behavior17`: todo: FPS stuff

### 32_GoreaSealSphere2.cs

`src/MphRead/Entities/Enemies/32_GoreaSealSphere2.cs`

- [ ] [32_GoreaSealSphere2.cs:145](../src/MphRead/Entities/Enemies/32_GoreaSealSphere2.cs#L145) in `Enemy32Entity.EnemyTakeDamage`: todo: FPS stuff

### 33_GoreaMeteor.cs

`src/MphRead/Entities/Enemies/33_GoreaMeteor.cs`

- [ ] [33_GoreaMeteor.cs:74](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L74) in `Enemy33Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:75](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L75) in `Enemy33Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:76](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L76) in `Enemy33Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:81](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L81) in `Enemy33Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:141](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L141) in `Enemy33Entity.UpdateSpeed`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:192](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L192) in `Enemy33Entity.EnemyTakeDamage`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:239](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L239) in `Enemy33Entity.SpawnItemDrop`: todo: FPS stuff
- [ ] [33_GoreaMeteor.cs:311](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L311) in `Enemy33Entity.EnemyGetDrawInfo`: todo: FPS stuff

### 35_Voldrum.cs

`src/MphRead/Entities/Enemies/35_Voldrum.cs`

- [ ] [35_Voldrum.cs:34](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L34) in `Enemy35Entity`: todo: FPS stuff
- [ ] [35_Voldrum.cs:81](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L81) in `Enemy35Entity.Setup`: todo: FPS stuff
- [ ] [35_Voldrum.cs:101](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L101) in `Enemy35Entity.EnemyProcess`: todo: FPS stuff
- [ ] [35_Voldrum.cs:127](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L127) in `Enemy35Entity.EnemyProcess`: todo: FPS suff
- [ ] [35_Voldrum.cs:138](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L138) in `Enemy35Entity.UpdateRollSfx`: todo: FPS stuff
- [ ] [35_Voldrum.cs:201](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L201) in `Enemy35Entity.HandleCollision`: todo: FPS stuff
- [ ] [35_Voldrum.cs:366](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L366) in `Enemy35Entity.Behavior02`: todo: FPS stuff
- [ ] [35_Voldrum.cs:371](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L371) in `Enemy35Entity.Behavior02`: todo: FPS stuff
- [ ] [35_Voldrum.cs:394](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L394) in `Enemy35Entity.Behavior03`: todo: FPS stuff
- [ ] [35_Voldrum.cs:395](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L395) in `Enemy35Entity.Behavior03`: todo: FPS stuff
- [ ] [35_Voldrum.cs:396](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L396) in `Enemy35Entity.Behavior03`: todo: FPS stuff
- [ ] [35_Voldrum.cs:411](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L411) in `Enemy35Entity.Behavior04`: todo: FPS stuff
- [ ] [35_Voldrum.cs:437](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L437) in `Enemy35Entity.Behavior05`: todo: FPS stuff

### 36_Voldrum.cs

`src/MphRead/Entities/Enemies/36_Voldrum.cs`

- [ ] [36_Voldrum.cs:69](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L69) in `Enemy36Entity.Setup`: todo: FPS stuff
- [ ] [36_Voldrum.cs:101](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L101) in `Enemy36Entity.EnemyProcess`: todo: FPS stuff
- [ ] [36_Voldrum.cs:127](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L127) in `Enemy36Entity.EnemyProcess`: todo: FPS suff
- [ ] [36_Voldrum.cs:189](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L189) in `Enemy36Entity.State3`: todo: FPS stuff
- [ ] [36_Voldrum.cs:190](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L190) in `Enemy36Entity.State3`: todo: FPS stuff
- [ ] [36_Voldrum.cs:229](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L229) in `Enemy36Entity.Behavior01`: todo: FPS stuff
- [ ] [36_Voldrum.cs:250](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L250) in `Enemy36Entity.Behavior03`: todo: FPS stuff
- [ ] [36_Voldrum.cs:262](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L262) in `Enemy36Entity.Behavior03`: todo: FPS stuff
- [ ] [36_Voldrum.cs:290](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L290) in `Enemy36Entity.Behavior04`: todo: FPS stuff

### 37_Quadtroid.cs

`src/MphRead/Entities/Enemies/37_Quadtroid.cs`

- [ ] [37_Quadtroid.cs:56](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L56) in `Enemy37Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:57](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L57) in `Enemy37Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:100](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L100) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:129](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L129) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:138](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L138) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:148](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L148) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:162](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L162) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:192](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L192) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:253](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L253) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:288](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L288) in `Enemy37Entity.EnemyProcess`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:440](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L440) in `Enemy37Entity.Func214E668`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:456](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L456) in `Enemy37Entity.Func214E5EC`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:472](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L472) in `Enemy37Entity.Func214E58C`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:569](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L569) in `Enemy37Entity.Func214EB84`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:691](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L691) in `Enemy37Entity.Func214E994`: todo: FPS stuff
- [ ] [37_Quadtroid.cs:718](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L718) in `Enemy37Entity.Func214D9F8`: todo: FPS stuff

### 38_CrashPillar.cs

`src/MphRead/Entities/Enemies/38_CrashPillar.cs`

- [ ] [38_CrashPillar.cs:48](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L48) in `Enemy38Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:49](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L49) in `Enemy38Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:50](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L50) in `Enemy38Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:66](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L66) in `Enemy38Entity.EnemyProcess`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:70](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L70) in `Enemy38Entity.EnemyProcess`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:123](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L123) in `Enemy38Entity.DoThing`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:142](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L142) in `Enemy38Entity.State06`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:227](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L227) in `Enemy38Entity.Behavior00`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:228](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L228) in `Enemy38Entity.Behavior00`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:241](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L241) in `Enemy38Entity.Behavior01`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:327](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L327) in `Enemy38Entity.Behavior09`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:346](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L346) in `Enemy38Entity.Behavior10`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:348](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L348) in `Enemy38Entity.Behavior10`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:349](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L349) in `Enemy38Entity.Behavior10`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:390](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L390) in `Enemy38Entity.Behavior14`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:403](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L403) in `Enemy38Entity.Behavior15`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:414](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L414) in `Enemy38Entity.Behavior16`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:430](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L430) in `Enemy38Entity.Behavior17`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:444](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L444) in `Enemy38Entity.Behavior18`: todo: FPS stuff
- [ ] [38_CrashPillar.cs:447](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L447) in `Enemy38Entity.Behavior18`: todo: FPS stuff

### 39_FireSpawn.cs

`src/MphRead/Entities/Enemies/39_FireSpawn.cs`

- [ ] [39_FireSpawn.cs:82](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L82) in `Enemy39Entity.EnemyInitialize`: todo: FPS stuff
- [ ] [39_FireSpawn.cs:159](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L159) in `Enemy39Entity.State2`: todo: FPS stuff
- [ ] [39_FireSpawn.cs:212](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L212) in `Enemy39Entity.State4`: todo: FPS stuff
- [ ] [39_FireSpawn.cs:251](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L251) in `Enemy39Entity.State5`: todo: FPS stuff
- [ ] [39_FireSpawn.cs:276](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L276) in `Enemy39Entity.Behavior0`: todo: FPS stuff
- [ ] [39_FireSpawn.cs:325](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L325) in `Enemy39Entity.Behavior1`: todo: FPS stuff

### 40_EnemySpawner.cs

`src/MphRead/Entities/Enemies/40_EnemySpawner.cs`

- [ ] [40_EnemySpawner.cs:95](../src/MphRead/Entities/Enemies/40_EnemySpawner.cs#L95) in `Enemy40Entity.EnemyProcess`: todo: FPS stuff

### 41_Slench.cs

`src/MphRead/Entities/Enemies/41_Slench.cs`

- [ ] [41_Slench.cs:208](../src/MphRead/Entities/Enemies/41_Slench.cs#L208) in `Enemy41Entity.ChangeState`: todo: FPS stuff
- [ ] [41_Slench.cs:209](../src/MphRead/Entities/Enemies/41_Slench.cs#L209) in `Enemy41Entity.ChangeState`: todo: FPS stuff
- [ ] [41_Slench.cs:254](../src/MphRead/Entities/Enemies/41_Slench.cs#L254) in `Enemy41Entity.ChangeState`: todo: FPS stuff
- [ ] [41_Slench.cs:302](../src/MphRead/Entities/Enemies/41_Slench.cs#L302) in `Enemy41Entity.ChangeState`: todo: FPS stuff
- [ ] [41_Slench.cs:346](../src/MphRead/Entities/Enemies/41_Slench.cs#L346) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:371](../src/MphRead/Entities/Enemies/41_Slench.cs#L371) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:410](../src/MphRead/Entities/Enemies/41_Slench.cs#L410) in `Enemy41Entity.EnemyProcess`: todo?: technically FPS stuff here since it activates them on successive frames
- [ ] [41_Slench.cs:469](../src/MphRead/Entities/Enemies/41_Slench.cs#L469) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:478](../src/MphRead/Entities/Enemies/41_Slench.cs#L478) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:481](../src/MphRead/Entities/Enemies/41_Slench.cs#L481) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:483](../src/MphRead/Entities/Enemies/41_Slench.cs#L483) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:487](../src/MphRead/Entities/Enemies/41_Slench.cs#L487) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:506](../src/MphRead/Entities/Enemies/41_Slench.cs#L506) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:512](../src/MphRead/Entities/Enemies/41_Slench.cs#L512) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:517](../src/MphRead/Entities/Enemies/41_Slench.cs#L517) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:558](../src/MphRead/Entities/Enemies/41_Slench.cs#L558) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:559](../src/MphRead/Entities/Enemies/41_Slench.cs#L559) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:578](../src/MphRead/Entities/Enemies/41_Slench.cs#L578) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:580](../src/MphRead/Entities/Enemies/41_Slench.cs#L580) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:585](../src/MphRead/Entities/Enemies/41_Slench.cs#L585) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:586](../src/MphRead/Entities/Enemies/41_Slench.cs#L586) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:607](../src/MphRead/Entities/Enemies/41_Slench.cs#L607) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:609](../src/MphRead/Entities/Enemies/41_Slench.cs#L609) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:637](../src/MphRead/Entities/Enemies/41_Slench.cs#L637) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:665](../src/MphRead/Entities/Enemies/41_Slench.cs#L665) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:695](../src/MphRead/Entities/Enemies/41_Slench.cs#L695) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:725](../src/MphRead/Entities/Enemies/41_Slench.cs#L725) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:738](../src/MphRead/Entities/Enemies/41_Slench.cs#L738) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:762](../src/MphRead/Entities/Enemies/41_Slench.cs#L762) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:774](../src/MphRead/Entities/Enemies/41_Slench.cs#L774) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:820](../src/MphRead/Entities/Enemies/41_Slench.cs#L820) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:822](../src/MphRead/Entities/Enemies/41_Slench.cs#L822) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:829](../src/MphRead/Entities/Enemies/41_Slench.cs#L829) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:836](../src/MphRead/Entities/Enemies/41_Slench.cs#L836) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:847](../src/MphRead/Entities/Enemies/41_Slench.cs#L847) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:854](../src/MphRead/Entities/Enemies/41_Slench.cs#L854) in `Enemy41Entity.EnemyProcess`: todo: FPS stuff
- [ ] [41_Slench.cs:957](../src/MphRead/Entities/Enemies/41_Slench.cs#L957) in `Enemy41Entity.ProcessRecoil`: todo: FPS stuff
- [ ] [41_Slench.cs:962](../src/MphRead/Entities/Enemies/41_Slench.cs#L962) in `Enemy41Entity.ProcessRecoil`: todo: FPS stuff
- [ ] [41_Slench.cs:970](../src/MphRead/Entities/Enemies/41_Slench.cs#L970) in `Enemy41Entity.ProcessRecoil`: todo: FPS stuff
- [ ] [41_Slench.cs:971](../src/MphRead/Entities/Enemies/41_Slench.cs#L971) in `Enemy41Entity.ProcessRecoil`: todo: FPS stuff
- [ ] [41_Slench.cs:980](../src/MphRead/Entities/Enemies/41_Slench.cs#L980) in `Enemy41Entity.ProcessRecoil`: todo: FPS stuff; plausible intermediate values added
- [ ] [41_Slench.cs:1026](../src/MphRead/Entities/Enemies/41_Slench.cs#L1026) in `Enemy41Entity.SetUpSlam`: todo: FPS stuff
- [ ] [41_Slench.cs:1191](../src/MphRead/Entities/Enemies/41_Slench.cs#L1191) in `Enemy41Entity.EnemyTakeDamage`: todo: FPS stuff
- [ ] [41_Slench.cs:1205](../src/MphRead/Entities/Enemies/41_Slench.cs#L1205) in `Enemy41Entity.EnemyTakeDamage`: todo: FPS stuff

### 44_SlenchSynapse.cs

`src/MphRead/Entities/Enemies/44_SlenchSynapse.cs`

- [ ] [44_SlenchSynapse.cs:125](../src/MphRead/Entities/Enemies/44_SlenchSynapse.cs#L125) in `Enemy44Entity.ChangeState`: todo: FPS stuff
- [ ] [44_SlenchSynapse.cs:142](../src/MphRead/Entities/Enemies/44_SlenchSynapse.cs#L142) in `Enemy44Entity.ChangeState`: todo: FPS stuff
- [ ] [44_SlenchSynapse.cs:165](../src/MphRead/Entities/Enemies/44_SlenchSynapse.cs#L165) in `Enemy44Entity.EnemyProcess`: todo: FPS stuff
- [ ] [44_SlenchSynapse.cs:197](../src/MphRead/Entities/Enemies/44_SlenchSynapse.cs#L197) in `Enemy44Entity.EnemyProcess`: todo: FPS stuff

### 45_SlenchTurret.cs

`src/MphRead/Entities/Enemies/45_SlenchTurret.cs`

- [ ] [45_SlenchTurret.cs:91](../src/MphRead/Entities/Enemies/45_SlenchTurret.cs#L91) in `Enemy45Entity.UpdateAnimationFrame`: todo: FPS stuff
- [ ] [45_SlenchTurret.cs:175](../src/MphRead/Entities/Enemies/45_SlenchTurret.cs#L175) in `Enemy45Entity.State2`: todo: FPS stuff
- [ ] [45_SlenchTurret.cs:224](../src/MphRead/Entities/Enemies/45_SlenchTurret.cs#L224) in `Enemy45Entity.Behavior04`: todo: FPS stuff

### 46_LesserIthrak.cs

`src/MphRead/Entities/Enemies/46_LesserIthrak.cs`

- [ ] [46_LesserIthrak.cs:34](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L34) in `Enemy46Entity`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:35](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L35) in `Enemy46Entity`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:85](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L85) in `Enemy46Entity.Setup`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:87](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L87) in `Enemy46Entity.Setup`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:115](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L115) in `Enemy46Entity.EnemyProcess`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:121](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L121) in `Enemy46Entity.EnemyProcess`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:122](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L122) in `Enemy46Entity.EnemyProcess`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:144](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L144) in `Enemy46Entity.EnemyProcess`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:263](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L263) in `Enemy46Entity.State03`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:264](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L264) in `Enemy46Entity.State03`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:297](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L297) in `Enemy46Entity.State08`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:303](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L303) in `Enemy46Entity.State08`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:304](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L304) in `Enemy46Entity.State08`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:395](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L395) in `Enemy46Entity.Behavior00`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:411](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L411) in `Enemy46Entity.Behavior01`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:412](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L412) in `Enemy46Entity.Behavior01`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:437](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L437) in `Enemy46Entity.Behavior03`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:450](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L450) in `Enemy46Entity.Behavior04`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:451](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L451) in `Enemy46Entity.Behavior04`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:470](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L470) in `Enemy46Entity.Behavior05`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:483](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L483) in `Enemy46Entity.Behavior05`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:500](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L500) in `Enemy46Entity.Behavior06`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:514](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L514) in `Enemy46Entity.Behavior07`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:549](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L549) in `Enemy46Entity.Behavior10`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:569](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L569) in `Enemy46Entity.Behavior11`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:579](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L579) in `Enemy46Entity.Behavior12`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:582](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L582) in `Enemy46Entity.Behavior12`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:597](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L597) in `Enemy46Entity.Behavior13`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:640](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L640) in `Enemy46Entity.StartRecoil`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:672](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L672) in `Enemy46Entity.Behavior17`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:673](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L673) in `Enemy46Entity.Behavior17`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:687](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L687) in `Enemy46Entity.Behavior18`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:702](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L702) in `Enemy46Entity.Behavior19`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:729](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L729) in `Enemy46Entity.Behavior20`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:731](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L731) in `Enemy46Entity.Behavior20`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:745](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L745) in `Enemy46Entity.Behavior21`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:746](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L746) in `Enemy46Entity.Behavior21`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:752](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L752) in `Enemy46Entity.Behavior22`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:771](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L771) in `Enemy46Entity.Behavior23`: todo: FPS stuff
- [ ] [46_LesserIthrak.cs:785](../src/MphRead/Entities/Enemies/46_LesserIthrak.cs#L785) in `Enemy46Entity.Behavior24`: todo: FPS stuff

### 49_ForceFieldLock.cs

`src/MphRead/Entities/Enemies/49_ForceFieldLock.cs`

- [ ] [49_ForceFieldLock.cs:175](../src/MphRead/Entities/Enemies/49_ForceFieldLock.cs#L175) in `Enemy49Entity.EnemyProcess`: todo: FPS stuff
- [ ] [49_ForceFieldLock.cs:179](../src/MphRead/Entities/Enemies/49_ForceFieldLock.cs#L179) in `Enemy49Entity.EnemyProcess`: todo: FPS stuff
- [ ] [49_ForceFieldLock.cs:203](../src/MphRead/Entities/Enemies/49_ForceFieldLock.cs#L203) in `Enemy49Entity.LockHit`: todo: FPS stuff

### EnemyInstanceEntity.cs

`src/MphRead/Entities/EnemyInstanceEntity.cs`

- [ ] [EnemyInstanceEntity.cs:321](../src/MphRead/Entities/EnemyInstanceEntity.cs#L321) in `EnemyInstanceEntity.DrawGeneric`: todo: FPS stuff

### EnemySpawnEntity.cs

`src/MphRead/Entities/EnemySpawnEntity.cs`

- [ ] [EnemySpawnEntity.cs:50](../src/MphRead/Entities/EnemySpawnEntity.cs#L50) in `EnemySpawnEntity.EnemySpawnEntity`: todo: FPS stuff
- [ ] [EnemySpawnEntity.cs:200](../src/MphRead/Entities/EnemySpawnEntity.cs#L200) in `EnemySpawnEntity.Process`: todo: FPS stuff
- [ ] [EnemySpawnEntity.cs:301](../src/MphRead/Entities/EnemySpawnEntity.cs#L301) in `EnemySpawnEntity.HandleMessage`: todo: FPS stuff

### EntityBase.cs

`src/MphRead/Entities/EntityBase.cs`

- [ ] [EntityBase.cs:305](../src/MphRead/Entities/EntityBase.cs#L305) in `EntityBase.UpdateAnimFrames`: todo: FPS stuff

### ItemInstanceEntity.cs

`src/MphRead/Entities/ItemInstanceEntity.cs`

- [ ] [ItemInstanceEntity.cs:149](../src/MphRead/Entities/ItemInstanceEntity.cs#L149) in `ItemInstanceEntity.Process`: todo: FPS stuff
- [ ] [ItemInstanceEntity.cs:160](../src/MphRead/Entities/ItemInstanceEntity.cs#L160) in `ItemInstanceEntity.Process`: todo: FPS stuff

### ItemSpawnEntity.cs

`src/MphRead/Entities/ItemSpawnEntity.cs`

- [ ] [ItemSpawnEntity.cs:57](../src/MphRead/Entities/ItemSpawnEntity.cs#L57) in `ItemSpawnEntity.ItemSpawnEntity`: todo: FPS stuff
- [ ] [ItemSpawnEntity.cs:106](../src/MphRead/Entities/ItemSpawnEntity.cs#L106) in `ItemSpawnEntity.Process`: todo: FPS stuff
- [ ] [ItemSpawnEntity.cs:199](../src/MphRead/Entities/ItemSpawnEntity.cs#L199) in `ItemSpawnEntity.SpawnItemDrop`: todo: FPS stuff

### JumpPadEntity.cs

`src/MphRead/Entities/JumpPadEntity.cs`

- [ ] [JumpPadEntity.cs:119](../src/MphRead/Entities/JumpPadEntity.cs#L119) in `JumpPadEntity.Process`: todo: FPS stuff

### PlatformEntity.cs

`src/MphRead/Entities/PlatformEntity.cs`

- [ ] [PlatformEntity.cs:154](../src/MphRead/Entities/PlatformEntity.cs#L154) in `PlatformEntity.PlatformEntity`: todo: FPS stuff
- [ ] [PlatformEntity.cs:170](../src/MphRead/Entities/PlatformEntity.cs#L170) in `PlatformEntity.PlatformEntity`: todo: FPS stuff
- [ ] [PlatformEntity.cs:173](../src/MphRead/Entities/PlatformEntity.cs#L173) in `PlatformEntity.PlatformEntity`: todo: FPS stuff
- [ ] [PlatformEntity.cs:174](../src/MphRead/Entities/PlatformEntity.cs#L174) in `PlatformEntity.PlatformEntity`: todo: FPS stuff
- [ ] [PlatformEntity.cs:656](../src/MphRead/Entities/PlatformEntity.cs#L656) in `PlatformEntity.Process`: todo: FPS stuff
- [ ] [PlatformEntity.cs:710](../src/MphRead/Entities/PlatformEntity.cs#L710) in `PlatformEntity.Process`: todo: FPS stuff
- [ ] [PlatformEntity.cs:1067](../src/MphRead/Entities/PlatformEntity.cs#L1067) in `PlatformEntity.Recoil`: todo: FPS stuff
- [ ] [PlatformEntity.cs:1068](../src/MphRead/Entities/PlatformEntity.cs#L1068) in `PlatformEntity.Recoil`: todo: FPS stuff
- [ ] [PlatformEntity.cs:1230](../src/MphRead/Entities/PlatformEntity.cs#L1230) in `PlatformEntity.UpdateState`: todo: FPS stuff

### DynamicLightEntity.cs

`src/MphRead/Entities/Players/DynamicLightEntity.cs`

- [ ] [DynamicLightEntity.cs:26](../src/MphRead/Entities/Players/DynamicLightEntity.cs#L26) in `DynamicLightEntityBase`: todo?: FPS stuff

### HalfturretEntity.cs

`src/MphRead/Entities/Players/HalfturretEntity.cs`

- [ ] [HalfturretEntity.cs:127](../src/MphRead/Entities/Players/HalfturretEntity.cs#L127) in `HalfturretEntity.OnTakeDamage`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:137](../src/MphRead/Entities/Players/HalfturretEntity.cs#L137) in `HalfturretEntity.OnFrozen`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:150](../src/MphRead/Entities/Players/HalfturretEntity.cs#L150) in `HalfturretEntity.OnSetOnFire`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:169](../src/MphRead/Entities/Players/HalfturretEntity.cs#L169) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:199](../src/MphRead/Entities/Players/HalfturretEntity.cs#L199) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:203](../src/MphRead/Entities/Players/HalfturretEntity.cs#L203) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:236](../src/MphRead/Entities/Players/HalfturretEntity.cs#L236) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:250](../src/MphRead/Entities/Players/HalfturretEntity.cs#L250) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:301](../src/MphRead/Entities/Players/HalfturretEntity.cs#L301) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:302](../src/MphRead/Entities/Players/HalfturretEntity.cs#L302) in `HalfturretEntity.Process`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:337](../src/MphRead/Entities/Players/HalfturretEntity.cs#L337) in `HalfturretEntity.UpdateAim`: todo: FPS stuff
- [ ] [HalfturretEntity.cs:345](../src/MphRead/Entities/Players/HalfturretEntity.cs#L345) in `HalfturretEntity.UpdateAim`: todo?: seems okay without FPS stuff here, but we need to confirm
- [ ] [HalfturretEntity.cs:419](../src/MphRead/Entities/Players/HalfturretEntity.cs#L419) in `HalfturretEntity.GetDrawInfo`: todo: FPS stuff

### PlayerAi.cs

`src/MphRead/Entities/Players/PlayerAi.cs`

- [ ] [PlayerAi.cs:505](../src/MphRead/Entities/Players/PlayerAi.cs#L505) in `PlayerAiData.InitializeSub`: todo: FPS stuff
- [ ] [PlayerAi.cs:506](../src/MphRead/Entities/Players/PlayerAi.cs#L506) in `PlayerAiData.InitializeSub`: todo: FPS stuff
- [ ] [PlayerAi.cs:972](../src/MphRead/Entities/Players/PlayerAi.cs#L972) in `PlayerAiData.AggroFunc214864C`: sktodo-ai: also maybe FPS stuff for this field?
- [ ] [PlayerAi.cs:1059](../src/MphRead/Entities/Players/PlayerAi.cs#L1059) in `PlayerAiData.UpdateAggroExpiration`: sktodo-ai: FPS stuff? review Field6 (expiration) changes
- [ ] [PlayerAi.cs:1221](../src/MphRead/Entities/Players/PlayerAi.cs#L1221) in `PlayerAiData.Execute`: todo: FPS stuff
- [ ] [PlayerAi.cs:1282](../src/MphRead/Entities/Players/PlayerAi.cs#L1282) in `PlayerAiData.UpdatePathWeights`: todo: FPS stuff
- [ ] [PlayerAi.cs:3018](../src/MphRead/Entities/Players/PlayerAi.cs#L3018) in `PlayerAiData.Func1_2148EA8`: sktodo-ai: FPS stuff?
- [ ] [PlayerAi.cs:3098](../src/MphRead/Entities/Players/PlayerAi.cs#L3098) in `PlayerAiData.Func2_213EA10`: todo: FPS stuff
- [ ] [PlayerAi.cs:3118](../src/MphRead/Entities/Players/PlayerAi.cs#L3118) in `PlayerAiData.Func2_213EA48`: todo: FPS stuff
- [ ] [PlayerAi.cs:3262](../src/MphRead/Entities/Players/PlayerAi.cs#L3262) in `PlayerAiData.Func2_213EA48`: todo: FPS stuff
- [ ] [PlayerAi.cs:3265](../src/MphRead/Entities/Players/PlayerAi.cs#L3265) in `PlayerAiData.Func2_213EA48`: sktodo-ai: FPS stuff? review this and Field6
- [ ] [PlayerAi.cs:3391](../src/MphRead/Entities/Players/PlayerAi.cs#L3391) in `PlayerAiData.Func2_213EA48`: todo: FPS stuff
- [ ] [PlayerAi.cs:3478](../src/MphRead/Entities/Players/PlayerAi.cs#L3478) in `PlayerAiData.Func2_213EA48`: sktodo-ai: FPS stuff if this is called repeatedly
- [ ] [PlayerAi.cs:3499](../src/MphRead/Entities/Players/PlayerAi.cs#L3499) in `PlayerAiData.Func2_213EA48`: todo: FPS stuff
- [ ] [PlayerAi.cs:3527](../src/MphRead/Entities/Players/PlayerAi.cs#L3527) in `PlayerAiData.Func2_213EA48`: sktodo-ai: FPS stuff if this is called repeatedly
- [ ] [PlayerAi.cs:3577](../src/MphRead/Entities/Players/PlayerAi.cs#L3577) in `PlayerAiData.Func2142EB0`: todo: FPS stuff
- [ ] [PlayerAi.cs:3666](../src/MphRead/Entities/Players/PlayerAi.cs#L3666) in `PlayerAiData.Func2_213DDCC`: todo: FPS stuff
- [ ] [PlayerAi.cs:3689](../src/MphRead/Entities/Players/PlayerAi.cs#L3689) in `PlayerAiData.Func2_213DA88`: todo: FPS stuff
- [ ] [PlayerAi.cs:3730](../src/MphRead/Entities/Players/PlayerAi.cs#L3730) in `PlayerAiData.Func2_213DA88`: todo: FPS stuff
- [ ] [PlayerAi.cs:3740](../src/MphRead/Entities/Players/PlayerAi.cs#L3740) in `PlayerAiData.Func2142FC0`: todo: FPS stuff
- [ ] [PlayerAi.cs:3744](../src/MphRead/Entities/Players/PlayerAi.cs#L3744) in `PlayerAiData.Func2142FC0`: todo: FPS stuff
- [ ] [PlayerAi.cs:3762](../src/MphRead/Entities/Players/PlayerAi.cs#L3762) in `PlayerAiData.Func2_213E148`: todo: FPS stuff
- [ ] [PlayerAi.cs:3853](../src/MphRead/Entities/Players/PlayerAi.cs#L3853) in `PlayerAiData.Func2_213E934`: todo: FPS stuff (the game divides by 2)
- [ ] [PlayerAi.cs:3888](../src/MphRead/Entities/Players/PlayerAi.cs#L3888) in `PlayerAiData.Func2143578`: todo: FPs stuff
- [ ] [PlayerAi.cs:3919](../src/MphRead/Entities/Players/PlayerAi.cs#L3919) in `PlayerAiData.Func2_213E684`: todo: FPS stuff
- [ ] [PlayerAi.cs:3929](../src/MphRead/Entities/Players/PlayerAi.cs#L3929) in `PlayerAiData.Func2_213E684`: todo: FPS stuff
- [ ] [PlayerAi.cs:3934](../src/MphRead/Entities/Players/PlayerAi.cs#L3934) in `PlayerAiData.Func2_213E684`: todo: FPS stuff
- [ ] [PlayerAi.cs:3945](../src/MphRead/Entities/Players/PlayerAi.cs#L3945) in `PlayerAiData.Func2_213E684`: todo: FPS stuff
- [ ] [PlayerAi.cs:3952](../src/MphRead/Entities/Players/PlayerAi.cs#L3952) in `PlayerAiData.Func2_213E684`: todo: FPS stuff
- [ ] [PlayerAi.cs:4000](../src/MphRead/Entities/Players/PlayerAi.cs#L4000) in `PlayerAiData.Func2_213E3C4`: todo: FPS stuff
- [ ] [PlayerAi.cs:4009](../src/MphRead/Entities/Players/PlayerAi.cs#L4009) in `PlayerAiData.Func2142D38`: todo: FPS stuff
- [ ] [PlayerAi.cs:4017](../src/MphRead/Entities/Players/PlayerAi.cs#L4017) in `PlayerAiData.Func2142D38`: todo: FPS stuff
- [ ] [PlayerAi.cs:4039](../src/MphRead/Entities/Players/PlayerAi.cs#L4039) in `PlayerAiData.Func2_213E31C`: todo: FPS stuff
- [ ] [PlayerAi.cs:4101](../src/MphRead/Entities/Players/PlayerAi.cs#L4101) in `PlayerAiData.Func2_213E274`: todo: FPS stuff
- [ ] [PlayerAi.cs:4110](../src/MphRead/Entities/Players/PlayerAi.cs#L4110) in `PlayerAiData.Func21430B4`: todo: FPS stuff
- [ ] [PlayerAi.cs:4141](../src/MphRead/Entities/Players/PlayerAi.cs#L4141) in `PlayerAiData.Func2_213E1CC`: todo: FPS stuff
- [ ] [PlayerAi.cs:4156](../src/MphRead/Entities/Players/PlayerAi.cs#L4156) in `PlayerAiData.Func2_213D9B8`: todo: FPS stuff
- [ ] [PlayerAi.cs:4693](../src/MphRead/Entities/Players/PlayerAi.cs#L4693) in `PlayerAiData.Func3_213CA70`: todo: FPS stuff
- [ ] [PlayerAi.cs:4698](../src/MphRead/Entities/Players/PlayerAi.cs#L4698) in `PlayerAiData.Func3_213CA58`: todo: FPS stuff
- [ ] [PlayerAi.cs:4703](../src/MphRead/Entities/Players/PlayerAi.cs#L4703) in `PlayerAiData.Func3_213CA2C`: sktodo: does this call count really need to be multiplied by 2? // todo: FPS stuff
- [ ] [PlayerAi.cs:4791](../src/MphRead/Entities/Players/PlayerAi.cs#L4791) in `PlayerAiData.Func3_213C600`: sktodo: does this call count really need to be multiplied by 2? // todo: FPS stuff
- [ ] [PlayerAi.cs:5542](../src/MphRead/Entities/Players/PlayerAi.cs#L5542) in `PlayerAiData.Func3_213AD88`: sktodo-ai: FPS stuff?
- [ ] [PlayerAi.cs:5552](../src/MphRead/Entities/Players/PlayerAi.cs#L5552) in `PlayerAiData.Func3_213ACE8`: sktodo-ai: FPS stuff?
- [ ] [PlayerAi.cs:5734](../src/MphRead/Entities/Players/PlayerAi.cs#L5734) in `PlayerAiData.Func3_213A868`: todo: FPS stuff
- [ ] [PlayerAi.cs:5778](../src/MphRead/Entities/Players/PlayerAi.cs#L5778) in `PlayerAiData.Func3_213A714`: todo: FPS stuff
- [ ] [PlayerAi.cs:6300](../src/MphRead/Entities/Players/PlayerAi.cs#L6300) in `PlayerAiData.Func4_21462DC`: todo: FPS stuff
- [ ] [PlayerAi.cs:6515](../src/MphRead/Entities/Players/PlayerAi.cs#L6515) in `PlayerAiData.CheckUnmorph`: todo: FPS stuff
- [ ] [PlayerAi.cs:6642](../src/MphRead/Entities/Players/PlayerAi.cs#L6642) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6646](../src/MphRead/Entities/Players/PlayerAi.cs#L6646) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6650](../src/MphRead/Entities/Players/PlayerAi.cs#L6650) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6666](../src/MphRead/Entities/Players/PlayerAi.cs#L6666) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6669](../src/MphRead/Entities/Players/PlayerAi.cs#L6669) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6673](../src/MphRead/Entities/Players/PlayerAi.cs#L6673) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6678](../src/MphRead/Entities/Players/PlayerAi.cs#L6678) in `PlayerAiData.Func2144B88`: todo: FPS stuff (see below)
- [ ] [PlayerAi.cs:6684](../src/MphRead/Entities/Players/PlayerAi.cs#L6684) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6686](../src/MphRead/Entities/Players/PlayerAi.cs#L6686) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6690](../src/MphRead/Entities/Players/PlayerAi.cs#L6690) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6691](../src/MphRead/Entities/Players/PlayerAi.cs#L6691) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6697](../src/MphRead/Entities/Players/PlayerAi.cs#L6697) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6714](../src/MphRead/Entities/Players/PlayerAi.cs#L6714) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6718](../src/MphRead/Entities/Players/PlayerAi.cs#L6718) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6720](../src/MphRead/Entities/Players/PlayerAi.cs#L6720) in `PlayerAiData.Func2144B88`: sktodo-ai: FPS stuff, by usage --\> leading shots
- [ ] [PlayerAi.cs:6731](../src/MphRead/Entities/Players/PlayerAi.cs#L6731) in `PlayerAiData.Func2144B88`: sktodo-ai: FPS stuff, by usage --\> speed affecting camera
- [ ] [PlayerAi.cs:6778](../src/MphRead/Entities/Players/PlayerAi.cs#L6778) in `PlayerAiData.Func2144B88`: todo: FPS stuff
- [ ] [PlayerAi.cs:6859](../src/MphRead/Entities/Players/PlayerAi.cs#L6859) in `PlayerAiData.Func2145738`: todo: FPS stuff
- [ ] [PlayerAi.cs:6861](../src/MphRead/Entities/Players/PlayerAi.cs#L6861) in `PlayerAiData.Func2145738`: todo: FPS stuff
- [ ] [PlayerAi.cs:6869](../src/MphRead/Entities/Players/PlayerAi.cs#L6869) in `PlayerAiData.Func2145738`: todo: FPS stuff
- [ ] [PlayerAi.cs:6871](../src/MphRead/Entities/Players/PlayerAi.cs#L6871) in `PlayerAiData.Func2145738`: todo: FPS stuff
- [ ] [PlayerAi.cs:6945](../src/MphRead/Entities/Players/PlayerAi.cs#L6945) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:6954](../src/MphRead/Entities/Players/PlayerAi.cs#L6954) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:6966](../src/MphRead/Entities/Players/PlayerAi.cs#L6966) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:6986](../src/MphRead/Entities/Players/PlayerAi.cs#L6986) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:6998](../src/MphRead/Entities/Players/PlayerAi.cs#L6998) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:7022](../src/MphRead/Entities/Players/PlayerAi.cs#L7022) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:7081](../src/MphRead/Entities/Players/PlayerAi.cs#L7081) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:7124](../src/MphRead/Entities/Players/PlayerAi.cs#L7124) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:7175](../src/MphRead/Entities/Players/PlayerAi.cs#L7175) in `PlayerAiData.Func2143A40`: todo: FPS stuff
- [ ] [PlayerAi.cs:7344](../src/MphRead/Entities/Players/PlayerAi.cs#L7344) in `PlayerAiData.PressButton`: todo: FPS stuff
- [ ] [PlayerAi.cs:7354](../src/MphRead/Entities/Players/PlayerAi.cs#L7354) in `PlayerAiData.PressL`: todo: FPS stuff
- [ ] [PlayerAi.cs:7370](../src/MphRead/Entities/Players/PlayerAi.cs#L7370) in `PlayerAiData.Func2140094`: todo: FPS stuff
- [ ] [PlayerAi.cs:7533](../src/MphRead/Entities/Players/PlayerAi.cs#L7533) in `PlayerAiData.Func2140584`: todo: FPS stuff
- [ ] [PlayerAi.cs:7534](../src/MphRead/Entities/Players/PlayerAi.cs#L7534) in `PlayerAiData.Func2140584`: todo: FPS stuff
- [ ] [PlayerAi.cs:7537](../src/MphRead/Entities/Players/PlayerAi.cs#L7537) in `PlayerAiData.Func2140584`: todo: FPS stuff
- [ ] [PlayerAi.cs:7539](../src/MphRead/Entities/Players/PlayerAi.cs#L7539) in `PlayerAiData.Func2140584`: todo: FPS stuff
- [ ] [PlayerAi.cs:7568](../src/MphRead/Entities/Players/PlayerAi.cs#L7568) in `PlayerAiData.Func2140584`: todo: FPS stuff
- [ ] [PlayerAi.cs:7570](../src/MphRead/Entities/Players/PlayerAi.cs#L7570) in `PlayerAiData.Func2140584`: todo: FPS stuff
- [ ] [PlayerAi.cs:7848](../src/MphRead/Entities/Players/PlayerAi.cs#L7848) in `PlayerAiData.CheckSpireClimbInput`: todo: FPS stuff
- [ ] [PlayerAi.cs:7854](../src/MphRead/Entities/Players/PlayerAi.cs#L7854) in `PlayerAiData.CheckSpireClimbInput`: todo: FPS stuff
- [ ] [PlayerAi.cs:8137](../src/MphRead/Entities/Players/PlayerAi.cs#L8137) in `PlayerAiData.Func2140B18`: todo: FPS stuff
- [ ] [PlayerAi.cs:8148](../src/MphRead/Entities/Players/PlayerAi.cs#L8148) in `PlayerAiData.Func2140B18`: todo: FPS stuff
- [ ] [PlayerAi.cs:8154](../src/MphRead/Entities/Players/PlayerAi.cs#L8154) in `PlayerAiData.Func2140B18`: todo: FPS stuff
- [ ] [PlayerAi.cs:10129](../src/MphRead/Entities/Players/PlayerAi.cs#L10129) in `PlayerAiData.IsNodeInRange`: todo: FPS stuff
- [ ] [PlayerAi.cs:10408](../src/MphRead/Entities/Players/PlayerAi.cs#L10408) in `PlayerAiData.Func2137AA4`: todo: FPS stuff
- [ ] [PlayerAi.cs:11171](../src/MphRead/Entities/Players/PlayerAi.cs#L11171) in `PlayerAiData.OnTakeDamage`: todo: FPS stuff
- [ ] [PlayerAi.cs:11369](../src/MphRead/Entities/Players/PlayerAi.cs#L11369) in `PlayerAiData.GetOuptut`: todo: FPS stuff

### PlayerCamera.cs

`src/MphRead/Entities/Players/PlayerCamera.cs`

- [ ] [PlayerCamera.cs:46](../src/MphRead/Entities/Players/PlayerCamera.cs#L46) in `PlayerEntity.SwitchCamera`: todo: FPS stuff
- [ ] [PlayerCamera.cs:53](../src/MphRead/Entities/Players/PlayerCamera.cs#L53) in `PlayerEntity.UpdateCamera`: todo: FPS stuff
- [ ] [PlayerCamera.cs:95](../src/MphRead/Entities/Players/PlayerCamera.cs#L95) in `PlayerEntity.UpdateCameraFirst`: todo: FPS stuff
- [ ] [PlayerCamera.cs:97](../src/MphRead/Entities/Players/PlayerCamera.cs#L97) in `PlayerEntity.UpdateCameraFirst`: todo: FPS stuff
- [ ] [PlayerCamera.cs:100](../src/MphRead/Entities/Players/PlayerCamera.cs#L100) in `PlayerEntity.UpdateCameraFirst`: todo: FPS stuff
- [ ] [PlayerCamera.cs:179](../src/MphRead/Entities/Players/PlayerCamera.cs#L179) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [ ] [PlayerCamera.cs:186](../src/MphRead/Entities/Players/PlayerCamera.cs#L186) in `PlayerEntity.UpdateCameraThird1`: sktodo: FPS stuff?
- [ ] [PlayerCamera.cs:196](../src/MphRead/Entities/Players/PlayerCamera.cs#L196) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [ ] [PlayerCamera.cs:198](../src/MphRead/Entities/Players/PlayerCamera.cs#L198) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [x] ~~[PlayerCamera.cs:206](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerCamera.cs#L206) (upstream line) in `PlayerEntity.UpdateCameraThird1`: sktodo: FPS stuff?~~ (fixed: morph ball camera follow matches the original per tick, measured; docs/ACCURACY.md A3)
- [ ] [PlayerCamera.cs:220](../src/MphRead/Entities/Players/PlayerCamera.cs#L220) in `PlayerEntity.UpdateCameraThird1`: sktodo: FPS stuff?
- [ ] [PlayerCamera.cs:264](../src/MphRead/Entities/Players/PlayerCamera.cs#L264) in `PlayerEntity.UpdateCameraThird1`: sktodo: FPS stuff?
- [ ] [PlayerCamera.cs:280](../src/MphRead/Entities/Players/PlayerCamera.cs#L280) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [ ] [PlayerCamera.cs:313](../src/MphRead/Entities/Players/PlayerCamera.cs#L313) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [ ] [PlayerCamera.cs:350](../src/MphRead/Entities/Players/PlayerCamera.cs#L350) in `PlayerEntity.UpdateCameraThird1`: todO: FPS stuff
- [ ] [PlayerCamera.cs:385](../src/MphRead/Entities/Players/PlayerCamera.cs#L385) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [ ] [PlayerCamera.cs:492](../src/MphRead/Entities/Players/PlayerCamera.cs#L492) in `PlayerEntity.UpdateCameraThird1`: todo: FPS stuff
- [ ] [PlayerCamera.cs:510](../src/MphRead/Entities/Players/PlayerCamera.cs#L510) in `PlayerEntity.UpdateCameraThird2`: todo: FPS stuff
- [ ] [PlayerCamera.cs:518](../src/MphRead/Entities/Players/PlayerCamera.cs#L518) in `PlayerEntity.UpdateCameraThird2`: sktodo: FPS stuff
- [ ] [PlayerCamera.cs:525](../src/MphRead/Entities/Players/PlayerCamera.cs#L525) in `PlayerEntity.UpdateCameraThird2`: sktodo: FPS stuff
- [x] ~~[PlayerCamera.cs:555](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerCamera.cs#L555) (upstream line) in `PlayerEntity.UpdateCameraThird2`: sktodo: FPS stuff?~~ (fixed: morph ball camera follow matches the original per tick, measured; docs/ACCURACY.md A3)
- [ ] [PlayerCamera.cs:574](../src/MphRead/Entities/Players/PlayerCamera.cs#L574) in `PlayerEntity.UpdateCameraFree`: todo: FPS stuff
- [ ] [PlayerCamera.cs:603](../src/MphRead/Entities/Players/PlayerCamera.cs#L603) in `PlayerEntity.UpdateCameraFree`: todo: FPS stuff
- [ ] [PlayerCamera.cs:894](../src/MphRead/Entities/Players/PlayerCamera.cs#L894) in `CameraInfo.Update`: todo: FPS stuff

### PlayerCollision.cs

`src/MphRead/Entities/Players/PlayerCollision.cs`

- [ ] [PlayerCollision.cs:90](../src/MphRead/Entities/Players/PlayerCollision.cs#L90) in `PlayerEntity.CheckPlayerCollision`: todo: FPS stuff
- [ ] [PlayerCollision.cs:95](../src/MphRead/Entities/Players/PlayerCollision.cs#L95) in `PlayerEntity.CheckPlayerCollision`: todo: FPS stuff
- [ ] [PlayerCollision.cs:179](../src/MphRead/Entities/Players/PlayerCollision.cs#L179) in `PlayerEntity.CheckAltAttackHit1`: todo: FPS stuff
- [ ] [PlayerCollision.cs:201](../src/MphRead/Entities/Players/PlayerCollision.cs#L201) in `PlayerEntity.CheckAltAttackHit1`: todo: FPS stuff
- [ ] [PlayerCollision.cs:258](../src/MphRead/Entities/Players/PlayerCollision.cs#L258) in `PlayerEntity.CheckAltAttackHit2`: todo: FPS stuff
- [ ] [PlayerCollision.cs:311](../src/MphRead/Entities/Players/PlayerCollision.cs#L311) in `PlayerEntity.CheckAltAttackHitEnemy1`: todo: FPS stuff
- [ ] [PlayerCollision.cs:364](../src/MphRead/Entities/Players/PlayerCollision.cs#L364) in `PlayerEntity.AltAttackHitDoor`: todo: FPS stuff
- [ ] [PlayerCollision.cs:831](../src/MphRead/Entities/Players/PlayerCollision.cs#L831) in `PlayerEntity.HandleCollision`: todo: FPS stuff (partly fixed: walkable slopes fixed for the biped, measured; morph balls, edges and steep faces keep the old step; docs/ACCURACY.md A9)
- [ ] [PlayerCollision.cs:835](../src/MphRead/Entities/Players/PlayerCollision.cs#L835) in `PlayerEntity.HandleCollision`: todo: FPS stuff
- [ ] [PlayerCollision.cs:845](../src/MphRead/Entities/Players/PlayerCollision.cs#L845) in `PlayerEntity.HandleCollision`: todo: FPS stuff

### PlayerDraw.cs

`src/MphRead/Entities/Players/PlayerDraw.cs`

- [ ] [PlayerDraw.cs:129](../src/MphRead/Entities/Players/PlayerDraw.cs#L129) in `PlayerEntity.Draw`: todo: FPS stuff
- [ ] [PlayerDraw.cs:134](../src/MphRead/Entities/Players/PlayerDraw.cs#L134) in `PlayerEntity.Draw`: todo: FPS stuff
- [ ] [PlayerDraw.cs:218](../src/MphRead/Entities/Players/PlayerDraw.cs#L218) in `PlayerEntity.Draw`: todo: FPS stuff
- [ ] [PlayerDraw.cs:500](../src/MphRead/Entities/Players/PlayerDraw.cs#L500) in `PlayerEntity.DrawShadow`: todo: FPS stuff
- [ ] [PlayerDraw.cs:601](../src/MphRead/Entities/Players/PlayerDraw.cs#L601) in `PlayerEntity`: todo: FPS stuff

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:385](../src/MphRead/Entities/Players/PlayerEntity.cs#L385) in `PlayerEntity`: todo: all FPS stuff with speed
- [ ] [PlayerEntity.cs:414](../src/MphRead/Entities/Players/PlayerEntity.cs#L414) in `PlayerEntity`: todo: FPS stuff
- [ ] [PlayerEntity.cs:646](../src/MphRead/Entities/Players/PlayerEntity.cs#L646) in `PlayerEntity.Initialize`: todo: FPS stuff (use floats)
- [ ] [PlayerEntity.cs:805](../src/MphRead/Entities/Players/PlayerEntity.cs#L805) in `PlayerEntity.Spawn`: todo: FPS stuff?
- [ ] [PlayerEntity.cs:836](../src/MphRead/Entities/Players/PlayerEntity.cs#L836) in `PlayerEntity.Spawn`: todo: FPS stuff
- [ ] [PlayerEntity.cs:853](../src/MphRead/Entities/Players/PlayerEntity.cs#L853) in `PlayerEntity.Spawn`: todo: FPS stuff
- [ ] [PlayerEntity.cs:894](../src/MphRead/Entities/Players/PlayerEntity.cs#L894) in `PlayerEntity.Spawn`: todo: FPS stuff
- [ ] [PlayerEntity.cs:1272](../src/MphRead/Entities/Players/PlayerEntity.cs#L1272) in `PlayerEntity.UpdateMorphBallTrail`: todo: FPS stuff
- [ ] [PlayerEntity.cs:1569](../src/MphRead/Entities/Players/PlayerEntity.cs#L1569) in `PlayerEntity.UpdateGunAnimation`: todo: FPS stuff
- [ ] [PlayerEntity.cs:1620](../src/MphRead/Entities/Players/PlayerEntity.cs#L1620) in `PlayerEntity.UpdateGunAnimation`: todo: FPS stuff
- [ ] [PlayerEntity.cs:1623](../src/MphRead/Entities/Players/PlayerEntity.cs#L1623) in `PlayerEntity.UpdateGunAnimation`: todo: FPS stuff
- [ ] [PlayerEntity.cs:1626](../src/MphRead/Entities/Players/PlayerEntity.cs#L1626) in `PlayerEntity.UpdateGunAnimation`: todo: FPS stuff
- [ ] [PlayerEntity.cs:1714](../src/MphRead/Entities/Players/PlayerEntity.cs#L1714) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2385](../src/MphRead/Entities/Players/PlayerEntity.cs#L2385) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2413](../src/MphRead/Entities/Players/PlayerEntity.cs#L2413) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2415](../src/MphRead/Entities/Players/PlayerEntity.cs#L2415) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2418](../src/MphRead/Entities/Players/PlayerEntity.cs#L2418) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2420](../src/MphRead/Entities/Players/PlayerEntity.cs#L2420) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2422](../src/MphRead/Entities/Players/PlayerEntity.cs#L2422) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2429](../src/MphRead/Entities/Players/PlayerEntity.cs#L2429) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2445](../src/MphRead/Entities/Players/PlayerEntity.cs#L2445) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2452](../src/MphRead/Entities/Players/PlayerEntity.cs#L2452) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2480](../src/MphRead/Entities/Players/PlayerEntity.cs#L2480) in `PlayerEntity.TakeDamage`: todo: FPS stuff?
- [ ] [PlayerEntity.cs:2507](../src/MphRead/Entities/Players/PlayerEntity.cs#L2507) in `PlayerEntity.TakeDamage`: todo: FPS stuff?
- [ ] [PlayerEntity.cs:2550](../src/MphRead/Entities/Players/PlayerEntity.cs#L2550) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2558](../src/MphRead/Entities/Players/PlayerEntity.cs#L2558) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2567](../src/MphRead/Entities/Players/PlayerEntity.cs#L2567) in `PlayerEntity.TakeDamage`: todo: FPS stuff
- [ ] [PlayerEntity.cs:2575](../src/MphRead/Entities/Players/PlayerEntity.cs#L2575) in `PlayerEntity.TakeDamage`: todo: FPS stuff

### PlayerHud.cs

`src/MphRead/Entities/Players/PlayerHud.cs`

- [ ] [PlayerHud.cs:608](../src/MphRead/Entities/Players/PlayerHud.cs#L608) in `PlayerEntity.UpdateHud`: todo: FPS stuff
- [ ] [PlayerHud.cs:638](../src/MphRead/Entities/Players/PlayerHud.cs#L638) in `PlayerEntity.UpdateHealthbars`: todo: FPS stuff
- [ ] [PlayerHud.cs:647](../src/MphRead/Entities/Players/PlayerHud.cs#L647) in `PlayerEntity.UpdateHealthbars`: todo: FPS stuff
- [ ] [PlayerHud.cs:667](../src/MphRead/Entities/Players/PlayerHud.cs#L667) in `PlayerEntity.UpdateHealthbars`: todo: FPS stuff
- [ ] [PlayerHud.cs:671](../src/MphRead/Entities/Players/PlayerHud.cs#L671) in `PlayerEntity.UpdateHealthbars`: todo: FPS stuff
- [ ] [PlayerHud.cs:684](../src/MphRead/Entities/Players/PlayerHud.cs#L684) in `PlayerEntity.UpdateAmmoBar`: todo: FPS stuff
- [ ] [PlayerHud.cs:711](../src/MphRead/Entities/Players/PlayerHud.cs#L711) in `PlayerEntity.UpdateBoostBombs`: todo: FPS stuff
- [ ] [PlayerHud.cs:715](../src/MphRead/Entities/Players/PlayerHud.cs#L715) in `PlayerEntity.UpdateBoostBombs`: todo: FPS stuff
- [ ] [PlayerHud.cs:820](../src/MphRead/Entities/Players/PlayerHud.cs#L820) in `PlayerEntity.UpdateDamageIndicators`: todo: FPS stuff
- [ ] [PlayerHud.cs:840](../src/MphRead/Entities/Players/PlayerHud.cs#L840) in `PlayerEntity.HudOnFiredShot`: todo: FPS stuff
- [ ] [PlayerHud.cs:952](../src/MphRead/Entities/Players/PlayerHud.cs#L952) in `PlayerEntity.UpdateDisruptedState`: todo: FPS stuff
- [ ] [PlayerHud.cs:968](../src/MphRead/Entities/Players/PlayerHud.cs#L968) in `PlayerEntity.UpdateDisruptedState`: todo: FPS stuff
- [ ] [PlayerHud.cs:973](../src/MphRead/Entities/Players/PlayerHud.cs#L973) in `PlayerEntity.UpdateDisruptedState`: todo: FPS stuff
- [ ] [PlayerHud.cs:1816](../src/MphRead/Entities/Players/PlayerHud.cs#L1816) in `PlayerEntity.ProcessHudBounty`: todo: FPS stuff
- [ ] [PlayerHud.cs:1833](../src/MphRead/Entities/Players/PlayerHud.cs#L1833) in `PlayerEntity.ProcessHudCapture`: todo: FPS stuff
- [ ] [PlayerHud.cs:2150](../src/MphRead/Entities/Players/PlayerHud.cs#L2150) in `PlayerEntity.DrawOctolithInst`: todo: FPS stuff
- [ ] [PlayerHud.cs:3065](../src/MphRead/Entities/Players/PlayerHud.cs#L3065) in `PlayerEntity.DrawQueuedHudMessages`: todo: FPS stuff

### PlayerInput.cs

`src/MphRead/Entities/Players/PlayerInput.cs`

- [ ] [PlayerInput.cs:568](../src/MphRead/Entities/Players/PlayerInput.cs#L568) in `PlayerEntity.ProcessBiped`: itodo: button aim (see free cam code for FPS-independent stuff)
- [ ] [PlayerInput.cs:609](../src/MphRead/Entities/Players/PlayerInput.cs#L609) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:642](../src/MphRead/Entities/Players/PlayerInput.cs#L642) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [x] ~~[PlayerInput.cs:608](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L608) (upstream line) in `PlayerEntity.ProcessBiped`: todo: FPS stuff~~ (fixed: camera lean while strafing now matches the original, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:614](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L614) (upstream line) in `PlayerEntity.ProcessBiped`: todo: FPS stuff~~ (fixed: camera lean while strafing now matches the original, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:633](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L633) (upstream line) in `PlayerEntity.ProcessBiped`: sktodo: FPS stuff~~ (fixed: camera lean settles at the original's rate, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:649](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L649) (upstream line) in `PlayerEntity.ProcessBiped`: sktodo: FPS stuff~~ (fixed: camera lean settles at the original's rate, measured; docs/ACCURACY.md A2)
- [x] ~~[PlayerInput.cs:705](../src/MphRead/Entities/Players/PlayerInput.cs#L705) in `PlayerEntity.ProcessBiped`: todo: FPS stuff?~~ (fixed: speeds stay in the original's units and are halved where used; the jump arc is exact; docs/ACCURACY.md A, A1)
- [x] ~~[PlayerInput.cs:709](../src/MphRead/Entities/Players/PlayerInput.cs#L709) in `PlayerEntity.ProcessBiped`: todo: FPS stuff?~~ (fixed: jump speed checked: the jump arc matches the original tick for tick; docs/ACCURACY.md A1)
- [ ] [PlayerInput.cs:712](../src/MphRead/Entities/Players/PlayerInput.cs#L712) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:801](../src/MphRead/Entities/Players/PlayerInput.cs#L801) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:811](../src/MphRead/Entities/Players/PlayerInput.cs#L811) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:814](../src/MphRead/Entities/Players/PlayerInput.cs#L814) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:819](../src/MphRead/Entities/Players/PlayerInput.cs#L819) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:822](../src/MphRead/Entities/Players/PlayerInput.cs#L822) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:823](../src/MphRead/Entities/Players/PlayerInput.cs#L823) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:824](../src/MphRead/Entities/Players/PlayerInput.cs#L824) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:826](../src/MphRead/Entities/Players/PlayerInput.cs#L826) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:837](../src/MphRead/Entities/Players/PlayerInput.cs#L837) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:910](../src/MphRead/Entities/Players/PlayerInput.cs#L910) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:912](../src/MphRead/Entities/Players/PlayerInput.cs#L912) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:948](../src/MphRead/Entities/Players/PlayerInput.cs#L948) in `PlayerEntity.ProcessBiped`: todo: FPS stuff?
- [ ] [PlayerInput.cs:966](../src/MphRead/Entities/Players/PlayerInput.cs#L966) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:975](../src/MphRead/Entities/Players/PlayerInput.cs#L975) in `PlayerEntity.ProcessBiped`: todo: FPS stuff
- [ ] [PlayerInput.cs:1024](../src/MphRead/Entities/Players/PlayerInput.cs#L1024) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1035](../src/MphRead/Entities/Players/PlayerInput.cs#L1035) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1037](../src/MphRead/Entities/Players/PlayerInput.cs#L1037) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1039](../src/MphRead/Entities/Players/PlayerInput.cs#L1039) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1095](../src/MphRead/Entities/Players/PlayerInput.cs#L1095) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1126](../src/MphRead/Entities/Players/PlayerInput.cs#L1126) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1130](../src/MphRead/Entities/Players/PlayerInput.cs#L1130) in `PlayerEntity.TryFireWeapon`: todo: FPS stuff
- [ ] [PlayerInput.cs:1237](../src/MphRead/Entities/Players/PlayerInput.cs#L1237) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1305](../src/MphRead/Entities/Players/PlayerInput.cs#L1305) in `PlayerEntity.ProcessAlt`: itodo: button aim (see free cam code for FPS-independent stuff)
- [ ] [PlayerInput.cs:1455](../src/MphRead/Entities/Players/PlayerInput.cs#L1455) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1461](../src/MphRead/Entities/Players/PlayerInput.cs#L1461) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1474](../src/MphRead/Entities/Players/PlayerInput.cs#L1474) in `PlayerEntity.ProcessAlt`: todo: FPS stuff ^
- [ ] [PlayerInput.cs:1522](../src/MphRead/Entities/Players/PlayerInput.cs#L1522) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1589](../src/MphRead/Entities/Players/PlayerInput.cs#L1589) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1596](../src/MphRead/Entities/Players/PlayerInput.cs#L1596) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1608](../src/MphRead/Entities/Players/PlayerInput.cs#L1608) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1615](../src/MphRead/Entities/Players/PlayerInput.cs#L1615) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1617](../src/MphRead/Entities/Players/PlayerInput.cs#L1617) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1619](../src/MphRead/Entities/Players/PlayerInput.cs#L1619) in `PlayerEntity.ProcessAlt`: todo: FPS stuff
- [ ] [PlayerInput.cs:1644](../src/MphRead/Entities/Players/PlayerInput.cs#L1644) in `PlayerEntity.ProcessAlt`: todo: FPS stuff?
- [ ] [PlayerInput.cs:1755](../src/MphRead/Entities/Players/PlayerInput.cs#L1755) in `PlayerEntity.SpawnBomb`: todo: FPS stuff
- [ ] [PlayerInput.cs:1762](../src/MphRead/Entities/Players/PlayerInput.cs#L1762) in `PlayerEntity.SpawnBomb`: todo: FPS stuff
- [ ] [PlayerInput.cs:1785](../src/MphRead/Entities/Players/PlayerInput.cs#L1785) in `PlayerEntity.EndAltAttack`: todo: FPS stuff
- [ ] [PlayerInput.cs:1789](../src/MphRead/Entities/Players/PlayerInput.cs#L1789) in `PlayerEntity.EndAltAttack`: todo: FPS stuff
- [ ] [PlayerInput.cs:1799](../src/MphRead/Entities/Players/PlayerInput.cs#L1799) in `PlayerEntity.EndAltAttack`: todo: FPS stuff
- [ ] [PlayerInput.cs:1864](../src/MphRead/Entities/Players/PlayerInput.cs#L1864) in `PlayerEntity.SameArcCut`: todo: FPS stuff
- [ ] [PlayerInput.cs:1876](../src/MphRead/Entities/Players/PlayerInput.cs#L1876) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:1893](../src/MphRead/Entities/Players/PlayerInput.cs#L1893) in `PlayerEntity.ProcessMovement`: todo: FPS stuff?
- [ ] [PlayerInput.cs:1898](../src/MphRead/Entities/Players/PlayerInput.cs#L1898) in `PlayerEntity.ProcessMovement`: todo: FPS stuff?
- [ ] [PlayerInput.cs:1910](../src/MphRead/Entities/Players/PlayerInput.cs#L1910) in `PlayerEntity.ProcessMovement`: todo: FPS stuff?
- [ ] [PlayerInput.cs:1917](../src/MphRead/Entities/Players/PlayerInput.cs#L1917) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:1927](../src/MphRead/Entities/Players/PlayerInput.cs#L1927) in `PlayerEntity.ProcessMovement`: todo: FPS stuff?
- [ ] [PlayerInput.cs:1931](../src/MphRead/Entities/Players/PlayerInput.cs#L1931) in `PlayerEntity.ProcessMovement`: todo: FPS stuff?
- [ ] [PlayerInput.cs:1968](../src/MphRead/Entities/Players/PlayerInput.cs#L1968) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:1975](../src/MphRead/Entities/Players/PlayerInput.cs#L1975) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:2027](../src/MphRead/Entities/Players/PlayerInput.cs#L2027) in `PlayerEntity.ProcessMovement`: todo: FPS stuff (partly fixed: walking and the rolling morph balls fixed, measured; jump pads and the Trace, Sylux and Weavel alt forms keep the old step; docs/ACCURACY.md A7, A8)
- [ ] [PlayerInput.cs:2048](../src/MphRead/Entities/Players/PlayerInput.cs#L2048) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [x] ~~[PlayerInput.cs:2125](../src/MphRead/Entities/Players/PlayerInput.cs#L2125) in `PlayerEntity.ProcessMovement`: todo: FPS stuff~~ (fixed: with the gravity step fixed, the jump arc matches the original tick for tick; docs/ACCURACY.md A1)
- [x] ~~[PlayerInput.cs:2130](../src/MphRead/Entities/Players/PlayerInput.cs#L2130) in `PlayerEntity.ProcessMovement`: todo: FPS stuff~~ (fixed: position step fixed (gravity and walking): exact per tick; docs/ACCURACY.md A1, A7)
- [ ] [PlayerInput.cs:2149](../src/MphRead/Entities/Players/PlayerInput.cs#L2149) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:2190](../src/MphRead/Entities/Players/PlayerInput.cs#L2190) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:2249](../src/MphRead/Entities/Players/PlayerInput.cs#L2249) in `PlayerEntity.ProcessMovement`: todo: FPS stuff
- [ ] [PlayerInput.cs:2252](../src/MphRead/Entities/Players/PlayerInput.cs#L2252) in `PlayerEntity.ProcessMovement`: todo: FPS stuff

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:210](../src/MphRead/Entities/Players/PlayerProcess.cs#L210) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:213](../src/MphRead/Entities/Players/PlayerProcess.cs#L213) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:290](../src/MphRead/Entities/Players/PlayerProcess.cs#L290) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:294](../src/MphRead/Entities/Players/PlayerProcess.cs#L294) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:302](../src/MphRead/Entities/Players/PlayerProcess.cs#L302) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:309](../src/MphRead/Entities/Players/PlayerProcess.cs#L309) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:322](../src/MphRead/Entities/Players/PlayerProcess.cs#L322) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:380](../src/MphRead/Entities/Players/PlayerProcess.cs#L380) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:422](../src/MphRead/Entities/Players/PlayerProcess.cs#L422) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:426](../src/MphRead/Entities/Players/PlayerProcess.cs#L426) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:448](../src/MphRead/Entities/Players/PlayerProcess.cs#L448) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:480](../src/MphRead/Entities/Players/PlayerProcess.cs#L480) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:488](../src/MphRead/Entities/Players/PlayerProcess.cs#L488) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:504](../src/MphRead/Entities/Players/PlayerProcess.cs#L504) in `PlayerEntity.ProcessPlayer`: todo FPS stuff
- [ ] [PlayerProcess.cs:545](../src/MphRead/Entities/Players/PlayerProcess.cs#L545) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:552](../src/MphRead/Entities/Players/PlayerProcess.cs#L552) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:563](../src/MphRead/Entities/Players/PlayerProcess.cs#L563) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:571](../src/MphRead/Entities/Players/PlayerProcess.cs#L571) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:579](../src/MphRead/Entities/Players/PlayerProcess.cs#L579) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:590](../src/MphRead/Entities/Players/PlayerProcess.cs#L590) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:668](../src/MphRead/Entities/Players/PlayerProcess.cs#L668) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:673](../src/MphRead/Entities/Players/PlayerProcess.cs#L673) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:697](../src/MphRead/Entities/Players/PlayerProcess.cs#L697) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:707](../src/MphRead/Entities/Players/PlayerProcess.cs#L707) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:722](../src/MphRead/Entities/Players/PlayerProcess.cs#L722) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:827](../src/MphRead/Entities/Players/PlayerProcess.cs#L827) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:844](../src/MphRead/Entities/Players/PlayerProcess.cs#L844) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:872](../src/MphRead/Entities/Players/PlayerProcess.cs#L872) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:874](../src/MphRead/Entities/Players/PlayerProcess.cs#L874) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:878](../src/MphRead/Entities/Players/PlayerProcess.cs#L878) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:883](../src/MphRead/Entities/Players/PlayerProcess.cs#L883) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:900](../src/MphRead/Entities/Players/PlayerProcess.cs#L900) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1005](../src/MphRead/Entities/Players/PlayerProcess.cs#L1005) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff (balances out here)
- [ ] [PlayerProcess.cs:1060](../src/MphRead/Entities/Players/PlayerProcess.cs#L1060) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1119](../src/MphRead/Entities/Players/PlayerProcess.cs#L1119) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1124](../src/MphRead/Entities/Players/PlayerProcess.cs#L1124) in `PlayerEntity.ProcessPlayer`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1135](../src/MphRead/Entities/Players/PlayerProcess.cs#L1135) in `PlayerEntity.ProcessPlayer`: todo:FPS stuff
- [ ] [PlayerProcess.cs:1168](../src/MphRead/Entities/Players/PlayerProcess.cs#L1168) in `PlayerEntity.ActivateJumpPad`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1176](../src/MphRead/Entities/Players/PlayerProcess.cs#L1176) in `PlayerEntity.ActivateJumpPad`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1178](../src/MphRead/Entities/Players/PlayerProcess.cs#L1178) in `PlayerEntity.ActivateJumpPad`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1189](../src/MphRead/Entities/Players/PlayerProcess.cs#L1189) in `PlayerEntity.ActivateJumpPad`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1313](../src/MphRead/Entities/Players/PlayerProcess.cs#L1313) in `PlayerEntity.PickUpItems`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1324](../src/MphRead/Entities/Players/PlayerProcess.cs#L1324) in `PlayerEntity.PickUpItems`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1335](../src/MphRead/Entities/Players/PlayerProcess.cs#L1335) in `PlayerEntity.PickUpItems`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1627](../src/MphRead/Entities/Players/PlayerProcess.cs#L1627) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1629](../src/MphRead/Entities/Players/PlayerProcess.cs#L1629) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1630](../src/MphRead/Entities/Players/PlayerProcess.cs#L1630) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1631](../src/MphRead/Entities/Players/PlayerProcess.cs#L1631) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1632](../src/MphRead/Entities/Players/PlayerProcess.cs#L1632) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1637](../src/MphRead/Entities/Players/PlayerProcess.cs#L1637) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1639](../src/MphRead/Entities/Players/PlayerProcess.cs#L1639) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1668](../src/MphRead/Entities/Players/PlayerProcess.cs#L1668) in `PlayerEntity.UpdateAltTransform`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1726](../src/MphRead/Entities/Players/PlayerProcess.cs#L1726) in `PlayerEntity.UpdateStinglarvaSegments`: todo: FPS stuff
- [ ] [PlayerProcess.cs:2123](../src/MphRead/Entities/Players/PlayerProcess.cs#L2123) in `PlayerEntity.GetRespawnPoint`: todo: FPS stuff
- [ ] [PlayerProcess.cs:2130](../src/MphRead/Entities/Players/PlayerProcess.cs#L2130) in `PlayerEntity.GetTimeUntilRespawn`: todo: FPS stuff

### PlayerScan.cs

`src/MphRead/Entities/Players/PlayerScan.cs`

- [ ] [PlayerScan.cs:224](../src/MphRead/Entities/Players/PlayerScan.cs#L224) in `PlayerEntity.UpdateScanHud`: todo: FPS stuff

### PlayerSound.cs

`src/MphRead/Entities/Players/PlayerSound.cs`

- [ ] [PlayerSound.cs:255](../src/MphRead/Entities/Players/PlayerSound.cs#L255) in `PlayerEntity.UpdateMovementSfxAmount`: todo: FPS stuff
- [ ] [PlayerSound.cs:333](../src/MphRead/Entities/Players/PlayerSound.cs#L333) in `PlayerEntity.PlayLandingSfx`: todo: FPS stuff

### TeleporterEntity.cs

`src/MphRead/Entities/TeleporterEntity.cs`

- [ ] [TeleporterEntity.cs:224](../src/MphRead/Entities/TeleporterEntity.cs#L224) in `TeleporterEntity.Process`: todo-ai: FPS stuff
- [x] ~~[TeleporterEntity.cs:306](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/TeleporterEntity.cs#L306) (upstream line) in `TeleporterEntity.ActivateAnimaton`: todo: FPS stuff~~ (fixed: the portal opening sound plays every time, checked against the original; README "What's been fixed")

### TriggerVolumeEntity.cs

`src/MphRead/Entities/TriggerVolumeEntity.cs`

- [ ] [TriggerVolumeEntity.cs:48](../src/MphRead/Entities/TriggerVolumeEntity.cs#L48) in `TriggerVolumeEntity.TriggerVolumeEntity`: todo: FPS stuff
- [ ] [TriggerVolumeEntity.cs:114](../src/MphRead/Entities/TriggerVolumeEntity.cs#L114) in `TriggerVolumeEntity.Trigger`: todo: FPS stuff
- [ ] [TriggerVolumeEntity.cs:177](../src/MphRead/Entities/TriggerVolumeEntity.cs#L177) in `TriggerVolumeEntity.Process`: todo: FPS stuff
- [ ] [TriggerVolumeEntity.cs:236](../src/MphRead/Entities/TriggerVolumeEntity.cs#L236) in `TriggerVolumeEntity.HandleMessage`: todo: FPS stuff
- [ ] [TriggerVolumeEntity.cs:257](../src/MphRead/Entities/TriggerVolumeEntity.cs#L257) in `TriggerVolumeEntity.HandleMessage`: todo: FPS stuff

### GameState.cs

`src/MphRead/GameState.cs`

- [ ] [GameState.cs:671](../src/MphRead/GameState.cs#L671) in `GameState.ModeStatePrimeHunter`: todo: FPS stuff

### Renderer.cs

`src/MphRead/Renderer.cs`

- [ ] [Renderer.cs:1411](../src/MphRead/Renderer.cs#L1411) in `Scene.OnUpdateFrame`: todo: FPS stuff
- [ ] [Renderer.cs:2227](../src/MphRead/Renderer.cs#L2227) in `Scene.InitEffectElement`: todo: FPS stuff
- [ ] [Renderer.cs:2497](../src/MphRead/Renderer.cs#L2497) in `Scene.ProcessEffects`: todo: maybe revisit this frame time hack
- [ ] [Renderer.cs:2735](../src/MphRead/Renderer.cs#L2735) in `Scene.ProcessEffects`: todo: frame time scaling for speed/accel

## Naming

173 TODOs: 0 fixed, 173 open.

### 00_WarWasp.cs

`src/MphRead/Entities/Enemies/00_WarWasp.cs`

- [ ] [00_WarWasp.cs:12](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L12) in `Enemy00Entity`: todo: enum
- [ ] [00_WarWasp.cs:17](../src/MphRead/Entities/Enemies/00_WarWasp.cs#L17) in `Enemy00Entity`: todo: enum

### 02_Temroid.cs

`src/MphRead/Entities/Enemies/02_Temroid.cs`

- [ ] [02_Temroid.cs:63](../src/MphRead/Entities/Enemies/02_Temroid.cs#L63) in `Enemy02Entity`: todo: function names

### 10_BarbedWarWasp.cs

`src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs`

- [ ] [10_BarbedWarWasp.cs:13](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L13) in `Enemy10Entity`: todo: enum
- [ ] [10_BarbedWarWasp.cs:18](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L18) in `Enemy10Entity`: todo: enum

### 12_Geemer.cs

`src/MphRead/Entities/Enemies/12_Geemer.cs`

- [ ] [12_Geemer.cs:20](../src/MphRead/Entities/Enemies/12_Geemer.cs#L20) in `Enemy12Entity`: todo: field names

### 19_Cretaphid.cs

`src/MphRead/Entities/Enemies/19_Cretaphid.cs`

- [ ] [19_Cretaphid.cs:339](../src/MphRead/Entities/Enemies/19_Cretaphid.cs#L339) in `Enemy19Entity`: sktodo: function names

### 24_Gorea1A.cs

`src/MphRead/Entities/Enemies/24_Gorea1A.cs`

- [ ] [24_Gorea1A.cs:262](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L262) in `Enemy24Entity`: sktodo: field names

### 28_Gorea1B.cs

`src/MphRead/Entities/Enemies/28_Gorea1B.cs`

- [ ] [28_Gorea1B.cs:29](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L29) in `Enemy28Entity`: sktodo: field names, documentation, whatever (grapple segments)
- [ ] [28_Gorea1B.cs:106](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L106) in `Enemy28Entity.EnemyInitialize`: sktodo: field names (stretching speed and/or things those lines)
- [ ] [28_Gorea1B.cs:154](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L154) in `Enemy28Entity.EnemyProcess`: sktodo: variable name
- [ ] [28_Gorea1B.cs:258](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L258) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:258](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L258) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:286](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L286) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:316](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L316) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:382](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L382) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:382](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L382) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:385](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L385) in `Enemy28Entity.Func213B90C`: sktodo: variable name
- [ ] [28_Gorea1B.cs:426](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L426) in `Enemy28Entity.Func213B90C`: sktodo: variable names
- [ ] [28_Gorea1B.cs:458](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L458) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:443](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L443) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:486](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L486) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:497](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L497) in `Enemy28Entity.Func213BC3C`: sktodo: variable names
- [ ] [28_Gorea1B.cs:526](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L526) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:497](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L497) in `Enemy28Entity.Func213B678`: sktodo: variable names
- [ ] [28_Gorea1B.cs:561](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L561) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:584](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L584) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:544](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L544) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:561](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L561) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:584](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L584) in `Enemy28Entity`: sktodo: member name (note: this is copied from Quadtroid)
- [ ] [28_Gorea1B.cs:757](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L757) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:788](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L788) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:842](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L842) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:894](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L894) in `Enemy28Entity`: sktodo: member name
- [ ] [28_Gorea1B.cs:1432](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1432) in `Enemy28Entity`: sktodo: member name

### 31_Gorea2.cs

`src/MphRead/Entities/Enemies/31_Gorea2.cs`

- [ ] [31_Gorea2.cs:321](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L321) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:411](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L411) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:450](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L450) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:484](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L484) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:512](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L512) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:544](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L544) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:697](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L697) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1005](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1005) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1014](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1014) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1039](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1039) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1052](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1052) in `Enemy31Entity`: todo: member name (note: this is copied from Quadtroid)
- [ ] [31_Gorea2.cs:1058](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1058) in `Enemy31Entity`: todo: member name (note: this is copied from Quadtroid)
- [ ] [31_Gorea2.cs:1083](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1083) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1098](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1098) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1111](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1111) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1136](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1136) in `Enemy31Entity`: todo: member name, flag names
- [ ] [31_Gorea2.cs:1317](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1317) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1393](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1393) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1413](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1413) in `Enemy31Entity`: todo: member name
- [ ] [31_Gorea2.cs:1420](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1420) in `Enemy31Entity`: todo: member name

### 33_GoreaMeteor.cs

`src/MphRead/Entities/Enemies/33_GoreaMeteor.cs`

- [ ] [33_GoreaMeteor.cs:205](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L205) in `Enemy33Entity`: todo: member name

### 37_Quadtroid.cs

`src/MphRead/Entities/Enemies/37_Quadtroid.cs`

- [ ] [37_Quadtroid.cs:978](../src/MphRead/Entities/Enemies/37_Quadtroid.cs#L978) in `Enemy37Entity`: todo: function names

### 38_CrashPillar.cs

`src/MphRead/Entities/Enemies/38_CrashPillar.cs`

- [ ] [38_CrashPillar.cs:107](../src/MphRead/Entities/Enemies/38_CrashPillar.cs#L107) in `Enemy38Entity`: sktodo: function name

### 39_FireSpawn.cs

`src/MphRead/Entities/Enemies/39_FireSpawn.cs`

- [ ] [39_FireSpawn.cs:141](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L141) in `Enemy39Entity`: todo: function names

### EnemyInstanceEntity.cs

`src/MphRead/Entities/EnemyInstanceEntity.cs`

- [ ] [EnemyInstanceEntity.cs:34](../src/MphRead/Entities/EnemyInstanceEntity.cs#L34) in `EnemyInstanceEntity`: todo: names ("next?")

### PlatformEntity.cs

`src/MphRead/Entities/PlatformEntity.cs`

- [ ] [PlatformEntity.cs:63](../src/MphRead/Entities/PlatformEntity.cs#L63) in `PlatformEntity`: todo: names

### PlayerAi.cs

`src/MphRead/Entities/Players/PlayerAi.cs`

- [ ] [PlayerAi.cs:37](../src/MphRead/Entities/Players/PlayerAi.cs#L37) in `PlayerAiData`: todo: member name -- set by teleporter
- [ ] [PlayerAi.cs:43](../src/MphRead/Entities/Players/PlayerAi.cs#L43) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:52](../src/MphRead/Entities/Players/PlayerAi.cs#L52) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:73](../src/MphRead/Entities/Players/PlayerAi.cs#L73) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:179](../src/MphRead/Entities/Players/PlayerAi.cs#L179) in `PlayerAiData`: todo: member names -- and is the first set even used?
- [ ] [PlayerAi.cs:758](../src/MphRead/Entities/Players/PlayerAi.cs#L758) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:888](../src/MphRead/Entities/Players/PlayerAi.cs#L888) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:908](../src/MphRead/Entities/Players/PlayerAi.cs#L908) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:932](../src/MphRead/Entities/Players/PlayerAi.cs#L932) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:952](../src/MphRead/Entities/Players/PlayerAi.cs#L952) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1033](../src/MphRead/Entities/Players/PlayerAi.cs#L1033) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1079](../src/MphRead/Entities/Players/PlayerAi.cs#L1079) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1303](../src/MphRead/Entities/Players/PlayerAi.cs#L1303) in `PlayerAiData`: todo: member name (and delete the following)
- [ ] [PlayerAi.cs:1365](../src/MphRead/Entities/Players/PlayerAi.cs#L1365) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1372](../src/MphRead/Entities/Players/PlayerAi.cs#L1372) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1378](../src/MphRead/Entities/Players/PlayerAi.cs#L1378) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1389](../src/MphRead/Entities/Players/PlayerAi.cs#L1389) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1395](../src/MphRead/Entities/Players/PlayerAi.cs#L1395) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1402](../src/MphRead/Entities/Players/PlayerAi.cs#L1402) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1409](../src/MphRead/Entities/Players/PlayerAi.cs#L1409) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1415](../src/MphRead/Entities/Players/PlayerAi.cs#L1415) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1423](../src/MphRead/Entities/Players/PlayerAi.cs#L1423) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1688](../src/MphRead/Entities/Players/PlayerAi.cs#L1688) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1757](../src/MphRead/Entities/Players/PlayerAi.cs#L1757) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:1978](../src/MphRead/Entities/Players/PlayerAi.cs#L1978) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:2041](../src/MphRead/Entities/Players/PlayerAi.cs#L2041) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:3091](../src/MphRead/Entities/Players/PlayerAi.cs#L3091) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:3540](../src/MphRead/Entities/Players/PlayerAi.cs#L3540) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:3564](../src/MphRead/Entities/Players/PlayerAi.cs#L3564) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:3736](../src/MphRead/Entities/Players/PlayerAi.cs#L3736) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:3774](../src/MphRead/Entities/Players/PlayerAi.cs#L3774) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:3859](../src/MphRead/Entities/Players/PlayerAi.cs#L3859) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:3877](../src/MphRead/Entities/Players/PlayerAi.cs#L3877) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:4006](../src/MphRead/Entities/Players/PlayerAi.cs#L4006) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:4043](../src/MphRead/Entities/Players/PlayerAi.cs#L4043) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:4105](../src/MphRead/Entities/Players/PlayerAi.cs#L4105) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:4179](../src/MphRead/Entities/Players/PlayerAi.cs#L4179) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:4443](../src/MphRead/Entities/Players/PlayerAi.cs#L4443) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:5808](../src/MphRead/Entities/Players/PlayerAi.cs#L5808) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:6468](../src/MphRead/Entities/Players/PlayerAi.cs#L6468) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6489](../src/MphRead/Entities/Players/PlayerAi.cs#L6489) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6503](../src/MphRead/Entities/Players/PlayerAi.cs#L6503) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6521](../src/MphRead/Entities/Players/PlayerAi.cs#L6521) in `PlayerAiData`: todo: member name -- dword\_214C75C, dword\_214C750
- [ ] [PlayerAi.cs:6525](../src/MphRead/Entities/Players/PlayerAi.cs#L6525) in `PlayerAiData`: todo: member name -- Func2145C14() updates X, Func21447E8() updates X and Y
- [ ] [PlayerAi.cs:6566](../src/MphRead/Entities/Players/PlayerAi.cs#L6566) in `PlayerAiData`: todo: member name -- Func2145C14() updates X, Func21447E8() updates X and Y
- [ ] [PlayerAi.cs:6594](../src/MphRead/Entities/Players/PlayerAi.cs#L6594) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6619](../src/MphRead/Entities/Players/PlayerAi.cs#L6619) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6817](../src/MphRead/Entities/Players/PlayerAi.cs#L6817) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6908](../src/MphRead/Entities/Players/PlayerAi.cs#L6908) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:6923](../src/MphRead/Entities/Players/PlayerAi.cs#L6923) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7196](../src/MphRead/Entities/Players/PlayerAi.cs#L7196) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7240](../src/MphRead/Entities/Players/PlayerAi.cs#L7240) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7257](../src/MphRead/Entities/Players/PlayerAi.cs#L7257) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7271](../src/MphRead/Entities/Players/PlayerAi.cs#L7271) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7285](../src/MphRead/Entities/Players/PlayerAi.cs#L7285) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7308](../src/MphRead/Entities/Players/PlayerAi.cs#L7308) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7320](../src/MphRead/Entities/Players/PlayerAi.cs#L7320) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7330](../src/MphRead/Entities/Players/PlayerAi.cs#L7330) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7360](../src/MphRead/Entities/Players/PlayerAi.cs#L7360) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7467](../src/MphRead/Entities/Players/PlayerAi.cs#L7467) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7633](../src/MphRead/Entities/Players/PlayerAi.cs#L7633) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7658](../src/MphRead/Entities/Players/PlayerAi.cs#L7658) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7668](../src/MphRead/Entities/Players/PlayerAi.cs#L7668) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7701](../src/MphRead/Entities/Players/PlayerAi.cs#L7701) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7742](../src/MphRead/Entities/Players/PlayerAi.cs#L7742) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7750](../src/MphRead/Entities/Players/PlayerAi.cs#L7750) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7779](../src/MphRead/Entities/Players/PlayerAi.cs#L7779) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7790](../src/MphRead/Entities/Players/PlayerAi.cs#L7790) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7797](../src/MphRead/Entities/Players/PlayerAi.cs#L7797) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7811](../src/MphRead/Entities/Players/PlayerAi.cs#L7811) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7818](../src/MphRead/Entities/Players/PlayerAi.cs#L7818) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7827](../src/MphRead/Entities/Players/PlayerAi.cs#L7827) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7860](../src/MphRead/Entities/Players/PlayerAi.cs#L7860) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:7911](../src/MphRead/Entities/Players/PlayerAi.cs#L7911) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8069](../src/MphRead/Entities/Players/PlayerAi.cs#L8069) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8076](../src/MphRead/Entities/Players/PlayerAi.cs#L8076) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8087](../src/MphRead/Entities/Players/PlayerAi.cs#L8087) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8113](../src/MphRead/Entities/Players/PlayerAi.cs#L8113) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8162](../src/MphRead/Entities/Players/PlayerAi.cs#L8162) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8176](../src/MphRead/Entities/Players/PlayerAi.cs#L8176) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8772](../src/MphRead/Entities/Players/PlayerAi.cs#L8772) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8779](../src/MphRead/Entities/Players/PlayerAi.cs#L8779) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8786](../src/MphRead/Entities/Players/PlayerAi.cs#L8786) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8793](../src/MphRead/Entities/Players/PlayerAi.cs#L8793) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8800](../src/MphRead/Entities/Players/PlayerAi.cs#L8800) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8807](../src/MphRead/Entities/Players/PlayerAi.cs#L8807) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8814](../src/MphRead/Entities/Players/PlayerAi.cs#L8814) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:8821](../src/MphRead/Entities/Players/PlayerAi.cs#L8821) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:9852](../src/MphRead/Entities/Players/PlayerAi.cs#L9852) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:9911](../src/MphRead/Entities/Players/PlayerAi.cs#L9911) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:9956](../src/MphRead/Entities/Players/PlayerAi.cs#L9956) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:9997](../src/MphRead/Entities/Players/PlayerAi.cs#L9997) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10020](../src/MphRead/Entities/Players/PlayerAi.cs#L10020) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10177](../src/MphRead/Entities/Players/PlayerAi.cs#L10177) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10209](../src/MphRead/Entities/Players/PlayerAi.cs#L10209) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10266](../src/MphRead/Entities/Players/PlayerAi.cs#L10266) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10296](../src/MphRead/Entities/Players/PlayerAi.cs#L10296) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10338](../src/MphRead/Entities/Players/PlayerAi.cs#L10338) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10381](../src/MphRead/Entities/Players/PlayerAi.cs#L10381) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10424](../src/MphRead/Entities/Players/PlayerAi.cs#L10424) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10598](../src/MphRead/Entities/Players/PlayerAi.cs#L10598) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10654](../src/MphRead/Entities/Players/PlayerAi.cs#L10654) in `PlayerAiData`: todo: member name
- [ ] [PlayerAi.cs:10895](../src/MphRead/Entities/Players/PlayerAi.cs#L10895) in `PlayerAiData`: todo: member names
- [ ] [PlayerAi.cs:11144](../src/MphRead/Entities/Players/PlayerAi.cs#L11144) in `PlayerAiData`: todo: member name

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:307](../src/MphRead/Entities/Players/PlayerEntity.cs#L307) in `PlayerEntity`: todo: names

### RoomEntity.cs

`src/MphRead/Entities/RoomEntity.cs`

- [ ] [RoomEntity.cs:1023](../src/MphRead/Entities/RoomEntity.cs#L1023) in `RoomEntity.FindVisibleRoomParts`: todo: names/purposes
- [ ] [RoomEntity.cs:1092](../src/MphRead/Entities/RoomEntity.cs#L1092) in `RoomEntity`: todo: name/purpose
- [ ] [RoomEntity.cs:1108](../src/MphRead/Entities/RoomEntity.cs#L1108) in `RoomEntity`: todo: name/purpose

### TeleporterEntity.cs

`src/MphRead/Entities/TeleporterEntity.cs`

- [ ] [TeleporterEntity.cs:20](../src/MphRead/Entities/TeleporterEntity.cs#L20) in `TeleporterEntity`: todo: names
- [ ] [TeleporterEntity.cs:294](../src/MphRead/Entities/TeleporterEntity.cs#L294) in `TeleporterEntity`: todo: names

### CollisionDetection.cs

`src/MphRead/Formats/CollisionDetection.cs`

- [ ] [CollisionDetection.cs:38](../src/MphRead/Formats/CollisionDetection.cs#L38) in `CollisionResult`: todo: field name
- [ ] [CollisionDetection.cs:41](../src/MphRead/Formats/CollisionDetection.cs#L41) in `CollisionResult`: todo: field name

## Rendering

35 TODOs: 0 fixed, 35 open.

### BeamEffectEntity.cs

`src/MphRead/Entities/BeamEffectEntity.cs`

- [ ] [BeamEffectEntity.cs:72](../src/MphRead/Entities/BeamEffectEntity.cs#L72) in `BeamEffectEntity.Reposition`: todo?: update more effect stuff?
- [ ] [BeamEffectEntity.cs:93](../src/MphRead/Entities/BeamEffectEntity.cs#L93) in `BeamEffectEntity.Create`: ptodo: effect and type 0 both need to use mtxptr

### BeamProjectileEntity.cs

`src/MphRead/Entities/BeamProjectileEntity.cs`

- [ ] [BeamProjectileEntity.cs:1740](../src/MphRead/Entities/BeamProjectileEntity.cs#L1740) in `BeamProjectileEntity.Spawn`: btodo: load all collision effects, splat effects, etc. in room setup

### 18_AlimbicTurret.cs

`src/MphRead/Entities/Enemies/18_AlimbicTurret.cs`

- [ ] [18_AlimbicTurret.cs:320](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L320) in `Enemy18Entity.EnemyGetDrawInfo`: todo?: could do this in the shader

### 20_CretaphidEye.cs

`src/MphRead/Entities/Enemies/20_CretaphidEye.cs`

- [ ] [20_CretaphidEye.cs:460](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L460) in `Enemy20Entity.EnemyGetDrawInfo`: sktodo: the beam model is being drawn too thin, which also messes up the scrolling animation

### 24_Gorea1A.cs

`src/MphRead/Entities/Enemies/24_Gorea1A.cs`

- [ ] [24_Gorea1A.cs:218](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L218) in `GoreaEnemyEntityBase`: todo: once material colors (and alpha) are all using floats early, update this math to use floats

### 31_Gorea2.cs

`src/MphRead/Entities/Enemies/31_Gorea2.cs`

- [ ] [31_Gorea2.cs:363](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L363) in `Enemy31Entity`: todo: once material colors (and alpha) are all using floats early, update these functions to use floats

### 49_ForceFieldLock.cs

`src/MphRead/Entities/Enemies/49_ForceFieldLock.cs`

- [ ] [49_ForceFieldLock.cs:20](../src/MphRead/Entities/Enemies/49_ForceFieldLock.cs#L20) in `Enemy49Entity`: todo?: technically this has a custom draw function, but I don't think we need it

### EntityBase.cs

`src/MphRead/Entities/EntityBase.cs`

- [ ] [EntityBase.cs:352](../src/MphRead/Entities/EntityBase.cs#L352) in `EntityBase.UpdateTransforms`: todo: could skip this unless a relevant material property changed this update (and we're going to draw this entity)
- [ ] [EntityBase.cs:462](../src/MphRead/Entities/EntityBase.cs#L462) in `EntityBase.GetDrawInfo`: todo: hide attached effects (partly fixed: the gunship hides its thruster flames when hidden; other entities still to do; README "What's been fixed", PlatformEntity.cs)

### PlatformEntity.cs

`src/MphRead/Entities/PlatformEntity.cs`

- [ ] [PlatformEntity.cs:831](../src/MphRead/Entities/PlatformEntity.cs#L831) in `PlatformEntity.Process`: todo: if "is\_visible" returns false (and other conditions), don't draw the effects

### HalfturretEntity.cs

`src/MphRead/Entities/Players/HalfturretEntity.cs`

- [ ] [HalfturretEntity.cs:436](../src/MphRead/Entities/Players/HalfturretEntity.cs#L436) in `HalfturretEntity.GetDrawInfo`: todo?: could do this in the shader

### PlayerDraw.cs

`src/MphRead/Entities/Players/PlayerDraw.cs`

- [ ] [PlayerDraw.cs:212](../src/MphRead/Entities/Players/PlayerDraw.cs#L212) in `PlayerEntity.Draw`: todo?: could do this in the shader
- [ ] [PlayerDraw.cs:290](../src/MphRead/Entities/Players/PlayerDraw.cs#L290) in `PlayerEntity.Draw`: todo?: the game uses an alternate projection matrix to draw this

### PlayerHud.cs

`src/MphRead/Entities/Players/PlayerHud.cs`

- [ ] [PlayerHud.cs:1158](../src/MphRead/Entities/Players/PlayerHud.cs#L1158) in `PlayerEntity.DrawHudObjects`: todo: once we have masking that can account for various things (in this case, not drawing the scan lines
- [ ] [PlayerHud.cs:2621](../src/MphRead/Entities/Players/PlayerHud.cs#L2621) in `PlayerEntity.DrawModeRules`: todo?: ideally this should be in a process method, not draw

### RoomEntity.cs

`src/MphRead/Entities/RoomEntity.cs`

- [ ] [RoomEntity.cs:968](../src/MphRead/Entities/RoomEntity.cs#L968) in `RoomEntity.FindVisibleRoomParts`: todo?: link this portal into the draw list
- [ ] [RoomEntity.cs:1067](../src/MphRead/Entities/RoomEntity.cs#L1067) in `RoomEntity.FindVisibleRoomParts`: todo?: link this portal into the draw list

### Collada.cs

`src/MphRead/Export/Collada.cs`

- [ ] [Collada.cs:327](../src/MphRead/Export/Collada.cs#L327) in `Collada.ExportRecolor`: todo: handle material translation (find an example where it's nonzero)
- [ ] [Collada.cs:328](../src/MphRead/Export/Collada.cs#L328) in `Collada.ExportRecolor`: todo: handle texture matrices?

### Scripting.cs

`src/MphRead/Export/Scripting.cs`

- [ ] [Scripting.cs:361](../src/MphRead/Export/Scripting.cs#L361) in `Scripting.GenerateScript`: todo: make sure active\_material doesn't cause problems with untextured meshes

### Effects.cs

`src/MphRead/Formats/Effects.cs`

- [ ] [Effects.cs:1075](../src/MphRead/Formats/Effects.cs#L1075) in `EffectParticle.SetVecsC0`: sktodo: move view matrix transform to the shader
- [ ] [Effects.cs:1086](../src/MphRead/Formats/Effects.cs#L1086) in `EffectParticle.SetVecsD4`: todo?: if this needs to be implemented, revisit what it actually does and make it work in the shader

### Formats.cs

`src/MphRead/Formats/Formats.cs`

- [ ] [Formats.cs:196](../src/MphRead/Formats/Formats.cs#L196) in `Material`: todo: revisit the use as a polygon ID
- [ ] [Formats.cs:1132](../src/MphRead/Formats/Formats.cs#L1132): todo: some subtypes might not use their volume? if so, don't render them (confirm that all AreaVolumes do, also)

### HudInfo.cs

`src/MphRead/HUD/HudInfo.cs`

- [ ] [HudInfo.cs:1088](../src/MphRead/HUD/HudInfo.cs#L1088) in `HudInfo.TestObjects`: ltodo: parse characters for each palette that's actually specified by the attr, I guess?
- [ ] [HudInfo.cs:1932](../src/MphRead/HUD/HudInfo.cs#L1932) in `HudElements.HudObjects`: todo: modify HUD graphics/palettes for Guardians

### Metadata.cs

`src/MphRead/Metadata/Metadata.cs`

- [ ] [Metadata.cs:2176](../src/MphRead/Metadata/Metadata.cs#L2176) in `Metadata.DoubleDamageImg`: todo: confirm all texture shares with load\_object

### Read.cs

`src/MphRead/Read.cs`

- [ ] [Read.cs:936](../src/MphRead/Read.cs#L936) in `Read.LoadEffectFromPath`: todo: move the model reference to the element instead of the particle definitions

### Renderer.cs

`src/MphRead/Renderer.cs`

- [ ] [Renderer.cs:1338](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Renderer.cs#L1338) (upstream line) in `Scene.OnRenderFrame`: todo?: decals shouldn't render unless they have \~equal depth to the previous polygon, (removed from the code without a recorded fix: needs a check)
- [ ] [Renderer.cs:2789](../src/MphRead/Renderer.cs#L2789) in `Scene`: todo: revisit this (could allocate based on number of meshes on load)

### SceneSetup.cs

`src/MphRead/SceneSetup.cs`

- [ ] [SceneSetup.cs:550](../src/MphRead/SceneSetup.cs#L550) in `SceneSetup.LoadResources`: todo: this could also allocate effect lists and stuff, since we don't need those if there's no room

### Selection.cs

`src/MphRead/Selection.cs`

- [ ] [Selection.cs:487](../src/MphRead/Selection.cs#L487) in `Selection.SelectNext`: todo: it would be nice to have node/mesh selection follow the hierarchy (with meshes "overflowing" to the next node)

### RepackCollision.cs

`src/MphRead/Utility/RepackCollision.cs`

- [ ] [RepackCollision.cs:553](../src/MphRead/Utility/RepackCollision.cs#L553) in `RepackCollision.ThrowIfInvalid`: todo: throw if the polygon is concave/malformed?

### RepackModel.cs

`src/MphRead/Utility/RepackModel.cs`

- [ ] [RepackModel.cs:63](../src/MphRead/Utility/RepackModel.cs#L63) in `Repack.TestRepack`: todo: support texture shares

## Wi-Fi rules

21 TODOs: 0 fixed, 21 open.

### BeamProjectileEntity.cs

`src/MphRead/Entities/BeamProjectileEntity.cs`

- [ ] [BeamProjectileEntity.cs:459](../src/MphRead/Entities/BeamProjectileEntity.cs#L459) in `BeamProjectileEntity.CheckCollision`: todo?: else wifi check
- [ ] [BeamProjectileEntity.cs:987](../src/MphRead/Entities/BeamProjectileEntity.cs#L987) in `BeamProjectileEntity.CheckSplashDamage`: todo?: wifi conditions

### OctolithFlagEntity.cs

`src/MphRead/Entities/OctolithFlagEntity.cs`

- [ ] [OctolithFlagEntity.cs:75](../src/MphRead/Entities/OctolithFlagEntity.cs#L75) in `OctolithFlagEntity.Process`: todo?: lots of wifi stuff

### HalfturretEntity.cs

`src/MphRead/Entities/Players/HalfturretEntity.cs`

- [ ] [HalfturretEntity.cs:313](../src/MphRead/Entities/Players/HalfturretEntity.cs#L313) in `HalfturretEntity.Process`: todo?: wifi stuff
- [ ] [HalfturretEntity.cs:319](../src/MphRead/Entities/Players/HalfturretEntity.cs#L319) in `HalfturretEntity.Process`: todo?: wifi stuff

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:1777](../src/MphRead/Entities/Players/PlayerEntity.cs#L1777) in `PlayerEntity.TakeDamage`: todo: and not on wifi
- [ ] [PlayerEntity.cs:1813](../src/MphRead/Entities/Players/PlayerEntity.cs#L1813) in `PlayerEntity.TakeDamage`: todo?: and either main player or not wifi
- [ ] [PlayerEntity.cs:1854](../src/MphRead/Entities/Players/PlayerEntity.cs#L1854) in `PlayerEntity.TakeDamage`: todo?: something for wifi
- [ ] [PlayerEntity.cs:1865](../src/MphRead/Entities/Players/PlayerEntity.cs#L1865) in `PlayerEntity.TakeDamage`: todo?: something for wifi
- [ ] [PlayerEntity.cs:1884](../src/MphRead/Entities/Players/PlayerEntity.cs#L1884) in `PlayerEntity.TakeDamage`: todo?: the game encodes the beam in the damage flags for wifi stuff
- [ ] [PlayerEntity.cs:2393](../src/MphRead/Entities/Players/PlayerEntity.cs#L2393) in `PlayerEntity.TakeDamage`: todo?: if wifi, only do this if main player
- [ ] [PlayerEntity.cs:2403](../src/MphRead/Entities/Players/PlayerEntity.cs#L2403) in `PlayerEntity.TakeDamage`: todo?: if wifi, only do this if main player
- [ ] [PlayerEntity.cs:2443](../src/MphRead/Entities/Players/PlayerEntity.cs#L2443) in `PlayerEntity.TakeDamage`: todo?: if wifi, only do this if main player

### PlayerInput.cs

`src/MphRead/Entities/Players/PlayerInput.cs`

- [ ] [PlayerInput.cs:1137](../src/MphRead/Entities/Players/PlayerInput.cs#L1137) in `PlayerEntity.TryFireWeapon`: todo?: set the flag if wifi
- [ ] [PlayerInput.cs:1696](../src/MphRead/Entities/Players/PlayerInput.cs#L1696) in `PlayerEntity.SpawnBomb`: todo?: wi-fi condition and alternate function for spawning Lockjaw bombs
- [ ] [PlayerInput.cs:1721](../src/MphRead/Entities/Players/PlayerInput.cs#L1721) in `PlayerEntity.SpawnBomb`: todo?: wifi stuff

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:49](../src/MphRead/Entities/Players/PlayerProcess.cs#L49) in `PlayerEntity.ProcessPlayer`: todo?: something with wifi lockjaw bomb
- [ ] [PlayerProcess.cs:221](../src/MphRead/Entities/Players/PlayerProcess.cs#L221) in `PlayerEntity.ProcessPlayer`: todo?: something with wi-fi
- [ ] [PlayerProcess.cs:1038](../src/MphRead/Entities/Players/PlayerProcess.cs#L1038) in `PlayerEntity.ProcessPlayer`: todo?: if wifi and not main player
- [ ] [PlayerProcess.cs:1058](../src/MphRead/Entities/Players/PlayerProcess.cs#L1058) in `PlayerEntity.ProcessPlayer`: todo?: something if wifi
- [ ] [PlayerProcess.cs:1162](../src/MphRead/Entities/Players/PlayerProcess.cs#L1162) in `PlayerEntity.ProcessPlayer`: todo?: something for wifi

## Viewer and debug tools

15 TODOs: 0 fixed, 15 open.

### ArtifactEntity.cs

`src/MphRead/Entities/ArtifactEntity.cs`

- [ ] [ArtifactEntity.cs:135](../src/MphRead/Entities/ArtifactEntity.cs#L135) in `ArtifactEntity.Process`: todo: visualize pickup volumes

### BeamProjectileEntity.cs

`src/MphRead/Entities/BeamProjectileEntity.cs`

- [ ] [BeamProjectileEntity.cs:387](../src/MphRead/Entities/BeamProjectileEntity.cs#L387) in `BeamProjectileEntity.CheckCollision`: todo: visualize player collision (and rename some "pickup" fields)
- [ ] [BeamProjectileEntity.cs:2039](../src/MphRead/Entities/BeamProjectileEntity.cs#L2039) in `BeamProjectileEntity`: todo: visualize (also shadow freeze bug)

### BombEntity.cs

`src/MphRead/Entities/BombEntity.cs`

- [ ] [BombEntity.cs:292](../src/MphRead/Entities/BombEntity.cs#L292) in `BombEntity`: todo: visualize

### 01_Zoomer.cs

`src/MphRead/Entities/Enemies/01_Zoomer.cs`

- [ ] [01_Zoomer.cs:20](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L20) in `Enemy01Entity`: sktodo: implement vector visualization with line loops and use it to classify these fields

### 05_Petrasyl3.cs

`src/MphRead/Entities/Enemies/05_Petrasyl3.cs`

- [ ] [05_Petrasyl3.cs:17](../src/MphRead/Entities/Enemies/05_Petrasyl3.cs#L17) in `Enemy05Entity`: todo: visualize stuff and also get this field name

### 06_Petrasyl4.cs

`src/MphRead/Entities/Enemies/06_Petrasyl4.cs`

- [ ] [06_Petrasyl4.cs:17](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L17) in `Enemy06Entity`: todo: visualize stuff and also get this field name

### 39_FireSpawn.cs

`src/MphRead/Entities/Enemies/39_FireSpawn.cs`

- [ ] [39_FireSpawn.cs:13](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L13) in `Enemy39Entity`: todo: visualize

### ItemInstanceEntity.cs

`src/MphRead/Entities/ItemInstanceEntity.cs`

- [ ] [ItemInstanceEntity.cs:151](../src/MphRead/Entities/ItemInstanceEntity.cs#L151) in `ItemInstanceEntity.Process`: todo: visualize

### PlayerCollision.cs

`src/MphRead/Entities/Players/PlayerCollision.cs`

- [ ] [PlayerCollision.cs:21](../src/MphRead/Entities/Players/PlayerCollision.cs#L21) in `PlayerEntity`: todo: visualize EVERYTHING
- [ ] [PlayerCollision.cs:122](../src/MphRead/Entities/Players/PlayerCollision.cs#L122) in `PlayerEntity`: todo: more visualization

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:306](../src/MphRead/Entities/Players/PlayerEntity.cs#L306) in `PlayerEntity`: todo: visualize
- [ ] [PlayerEntity.cs:1203](../src/MphRead/Entities/Players/PlayerEntity.cs#L1203) in `PlayerEntity`: todo: visualize

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:1213](../src/MphRead/Entities/Players/PlayerProcess.cs#L1213) in `PlayerEntity.PickUpItems`: todo: visualize

### Collision.cs

`src/MphRead/Formats/Collision.cs`

- [ ] [Collision.cs:458](../src/MphRead/Formats/Collision.cs#L458) in `MphCollisionInfo.GetDrawInfo`: todo: visualize extra things like slipperiness, reflection, damage

## Bots

13 TODOs: 0 fixed, 13 open.

### PlayerAi.cs

`src/MphRead/Entities/Players/PlayerAi.cs`

- [ ] [PlayerAi.cs:50](../src/MphRead/Entities/Players/PlayerAi.cs#L50) in `PlayerAiData`: sktodo-ai: confirm this should be 10 and not 11 like field4C
- [ ] [PlayerAi.cs:235](../src/MphRead/Entities/Players/PlayerAi.cs#L235) in `PlayerAiData.UpdateVisibility`: sktodo-ai: presumably these are done one player per frame for efficiency,
- [ ] [PlayerAi.cs:621](../src/MphRead/Entities/Players/PlayerAi.cs#L621) in `PlayerAiData.ProcessInput`: sktodo-ai: this would set mouse aim values, but it's only used for Morph Ball,
- [ ] [PlayerAi.cs:784](../src/MphRead/Entities/Players/PlayerAi.cs#L784) in `PlayerAiData.UpdateAggro`: sktodo-ai: bug? should this be a continue? or is the byte array check only expected to pass for one player?
- [ ] [PlayerAi.cs:823](../src/MphRead/Entities/Players/PlayerAi.cs#L823) in `PlayerAiData.UpdateAggro`: sktodo-ai: same as above
- [ ] [PlayerAi.cs:834](../src/MphRead/Entities/Players/PlayerAi.cs#L834) in `PlayerAiData.UpdateAggro`: sktodo-ai: same as above
- [ ] [PlayerAi.cs:2290](../src/MphRead/Entities/Players/PlayerAi.cs#L2290) in `PlayerAiData.Func1_2149C50`: sktodo-ai: enum/mapping for these
- [ ] [PlayerAi.cs:3238](../src/MphRead/Entities/Players/PlayerAi.cs#L3238) in `PlayerAiData.Func2_213EA48`: sktodo-ai: this is being repeated a lot, including the call to Func2145C14() in some cases
- [ ] [PlayerAi.cs:5665](../src/MphRead/Entities/Players/PlayerAi.cs#L5665) in `PlayerAiData.Func3_213A9B8`: todo-ai: need to confirm what's done with this alpha value and if it needs (re)normalization
- [ ] [PlayerAi.cs:5684](../src/MphRead/Entities/Players/PlayerAi.cs#L5684) in `PlayerAiData.Func3_213A94C`: todo-ai: need to confirm what's done with this alpha value and if it needs (re)normalization
- [ ] [PlayerAi.cs:6840](../src/MphRead/Entities/Players/PlayerAi.cs#L6840) in `PlayerAiData.Func2145738`: sktodo-ai: add a common function for this
- [ ] [PlayerAi.cs:6856](../src/MphRead/Entities/Players/PlayerAi.cs#L6856) in `PlayerAiData.Func2145738`: sktodo-ai: add a common function for the beam stuff
- [ ] [PlayerAi.cs:7760](../src/MphRead/Entities/Players/PlayerAi.cs#L7760) in `PlayerAiData.Func2141EA8`: sktodo-ai: common

## HUD and menus

12 TODOs: 0 fixed, 12 open.

### BeamProjectileEntity.cs

`src/MphRead/Entities/BeamProjectileEntity.cs`

- [ ] [BeamProjectileEntity.cs:674](../src/MphRead/Entities/BeamProjectileEntity.cs#L674) in `BeamProjectileEntity.CheckCollision`: todo: handle messages like this

### ObjectEntity.cs

`src/MphRead/Entities/ObjectEntity.cs`

- [ ] [ObjectEntity.cs:607](../src/MphRead/Entities/ObjectEntity.cs#L607) in `ObjEffFlags`: todo: send scan message

### PlatformEntity.cs

`src/MphRead/Entities/PlatformEntity.cs`

- [ ] [PlatformEntity.cs:309](../src/MphRead/Entities/PlatformEntity.cs#L309) in `PlatformEntity.Initialize`: todo: send scan message

### PlayerDialog.cs

`src/MphRead/Entities/Players/PlayerDialog.cs`

- [ ] [PlayerDialog.cs:687](../src/MphRead/Entities/Players/PlayerDialog.cs#L687) in `PlayerEntity.DrawDialogs`: todo?: JP has an empty message here; add a bugfix?

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:1345](../src/MphRead/Entities/Players/PlayerEntity.cs#L1345) in `PlayerEntity.InitializeWeapon`: todo: set values for HUD graphics, probably

### PlayerHud.cs

`src/MphRead/Entities/Players/PlayerHud.cs`

- [ ] [PlayerHud.cs:472](../src/MphRead/Entities/Players/PlayerHud.cs#L472) in `PlayerEntity.LoadModeRules`: todo: text wrapping should be based on current window width

### PlayerInput.cs

`src/MphRead/Entities/Players/PlayerInput.cs`

- [ ] [PlayerInput.cs:46](../src/MphRead/Entities/Players/PlayerInput.cs#L46) in `PlayerEntity.ProcessInput`: todo: actual pause menu should require pressed

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:1473](../src/MphRead/Entities/Players/PlayerProcess.cs#L1473) in `PlayerEntity.PickUpWeapon`: todo: update HUD
- [ ] [PlayerProcess.cs:1905](../src/MphRead/Entities/Players/PlayerProcess.cs#L1905) in `PlayerEntity.UpdateForm`: todo?: update HUD if main player

### Formats.cs

`src/MphRead/Formats/Formats.cs`

- [ ] [Formats.cs:1319](../src/MphRead/Formats/Formats.cs#L1319) in `NodeLayer`: todo?: 0x1048 shows up in menus, including inside the ship

### GameState.cs

`src/MphRead/GameState.cs`

- [ ] [GameState.cs:289](../src/MphRead/GameState.cs#L289) in `GameState.ProcessFrame`: todo: process dialogs or something for 1P
- [ ] [GameState.cs:1584](../src/MphRead/GameState.cs#L1584) in `GameState.CommitSettings`: sktodo: commit menu options, including save slot

## Multiplayer license

11 TODOs: 7 fixed, 4 open.

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [x] ~~[PlayerEntity.cs:1921](../src/MphRead/Entities/Players/PlayerEntity.cs#L1921) in `PlayerEntity.TakeDamage`: todo: license info~~ (fixed: the license kill streak ends here (HostLicenseStreakEnd); docs/MULTIPLAYER_MENU.md "Counting")
- [ ] [PlayerEntity.cs:2189](../src/MphRead/Entities/Players/PlayerEntity.cs#L2189) in `PlayerEntity.TakeDamage`: todo: update license
- [x] ~~[PlayerEntity.cs:2257](../src/MphRead/Entities/Players/PlayerEntity.cs#L2257) in `PlayerEntity.TakeDamage`: todo: update license info~~ (fixed: a team kill ends the license kill streak (HostLicenseStreakEnd); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[PlayerEntity.cs:2270](../src/MphRead/Entities/Players/PlayerEntity.cs#L2270) in `PlayerEntity.TakeDamage`: todo: update license info~~ (fixed: kills, headshots and beam kills go to the license (HostLicenseKill); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[PlayerEntity.cs:2290](../src/MphRead/Entities/Players/PlayerEntity.cs#L2290) in `PlayerEntity.TakeDamage`: todo: update license info~~ (fixed: beam kills go to the license (HostLicenseKill); docs/MULTIPLAYER_MENU.md "Counting")

### PlayerInput.cs

`src/MphRead/Entities/Players/PlayerInput.cs`

- [ ] [PlayerInput.cs:1084](../src/MphRead/Entities/Players/PlayerInput.cs#L1084) in `PlayerEntity.TryFireWeapon`: todo: update license stats

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:1083](../src/MphRead/Entities/Players/PlayerProcess.cs#L1083) in `PlayerEntity.ProcessPlayer`: todo: update license stats

### GameState.cs

`src/MphRead/GameState.cs`

- [x] ~~[GameState.cs:262](../src/MphRead/GameState.cs#L262) in `GameState.UpdateTime`: todo: update license info etc.~~ (fixed: match time goes to the license (HostLicenseTime); docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[GameState.cs:299](../src/MphRead/GameState.cs#L299) in `GameState.ProcessFrame`: todo: update MP playtime to license info~~ (fixed: match time goes to the license (HostLicenseTime); docs/MULTIPLAYER_MENU.md "Counting")
- [ ] [GameState.cs:445](../src/MphRead/GameState.cs#L445) in `GameState.ProcessFrame`: todo: update license info, stop SFX (partly fixed: license counted after the match; stopping sounds still to do; docs/MULTIPLAYER_MENU.md "Counting")
- [x] ~~[GameState.cs:1237](../src/MphRead/GameState.cs#L1237) in `GameState.UpdateState`: todo: update license info~~ (fixed: wins, places, streaks and arena counts are counted after the match like the original; docs/MULTIPLAYER_MENU.md "Counting")

## Sound and music

9 TODOs: 0 fixed, 9 open.

### 24_Gorea1A.cs

`src/MphRead/Entities/Enemies/24_Gorea1A.cs`

- [ ] [24_Gorea1A.cs:804](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L804) in `Enemy24Entity.PlayBeamShotSfx`: todo: this is a hack so the Shock Coil SFX doesn't play every frame. I'm not sure what (checked 2026-10-11, still open: the original game's sound code would restart it too (rev 1 0x20741B8), which disagrees with MphRead's author's ear; to settle by listening in an emulator; docs/ACCURACY.md)

### PlayerInput.cs

`src/MphRead/Entities/Players/PlayerInput.cs`

- [ ] [PlayerInput.cs:136](../src/MphRead/Entities/Players/PlayerInput.cs#L136) in `PlayerEntity.ProcessTouchInput`: todo?: play SFX for the "hold scan" feature

### PlayerSound.cs

`src/MphRead/Entities/Players/PlayerSound.cs`

- [ ] [PlayerSound.cs:506](../src/MphRead/Entities/Players/PlayerSound.cs#L506) in `PlayerEntity.StopAllSfx`: todo: the game stops music here, but I think we have other cases covered, and need to not stop for teleporters
- [ ] [PlayerSound.cs:657](../src/MphRead/Entities/Players/PlayerSound.cs#L657) in `PlayerEntity.UpdateTimedSounds`: sfxtodo: escape sequence and pause stuff

### GameState.cs

`src/MphRead/GameState.cs`

- [ ] [GameState.cs:290](../src/MphRead/GameState.cs#L290) in `GameState.ProcessFrame`: todo: update SFX
- [ ] [GameState.cs:320](../src/MphRead/GameState.cs#L320) in `GameState.ProcessFrame`: todo: stop music/SFX, state bits/disconnect message?
- [ ] [GameState.cs:417](../src/MphRead/GameState.cs#L417) in `GameState.ProcessFrame`: sfxtodo: stop more kinds of SFX? fade for 1P mode?
- [ ] [GameState.cs:730](../src/MphRead/GameState.cs#L730) in `GameState.UpdateFrame`: todo: fade SFX

### Menu.cs

`src/MphRead/Menu.cs`

- [ ] [Menu.cs:2670](../src/MphRead/Menu.cs#L2670) in `Menu.ShowSoundTest`: todo?: support playing SFX scripts

## Saves

5 TODOs: 0 fixed, 5 open.

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:190](../src/MphRead/Entities/Players/PlayerEntity.cs#L190) in `PlayerEntity`: todo?: could save space with a union
- [ ] [PlayerEntity.cs:1001](../src/MphRead/Entities/Players/PlayerEntity.cs#L1001) in `PlayerEntity.InitEnemyHunter`: todo: update story save for multiplayer unlock

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:1296](../src/MphRead/Entities/Players/PlayerProcess.cs#L1296) in `PlayerEntity.PickUpItems`: todo: update story save

### GameState.cs

`src/MphRead/GameState.cs`

- [ ] [GameState.cs:1427](../src/MphRead/GameState.cs#L1427) in `GameState.RestoreCleanSave`: todo: save and restore more fields
- [ ] [GameState.cs:2024](../src/MphRead/GameState.cs#L2024) in `SaveStats`: todo: this should probably be stored globally and not deleted along with the save slot

## Rumble

2 TODOs: 0 fixed, 2 open.

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:1991](../src/MphRead/Entities/Players/PlayerEntity.cs#L1991) in `PlayerEntity.TakeDamage`: todo: rumble
- [ ] [PlayerEntity.cs:2595](../src/MphRead/Entities/Players/PlayerEntity.cs#L2595) in `PlayerEntity.TakeDamage`: todo: rumble

## Everything else

330 TODOs: 10 fixed, 306 open, 5 kept on purpose, 9 checked.

### BeamProjectileEntity.cs

`src/MphRead/Entities/BeamProjectileEntity.cs`

- [ ] [BeamProjectileEntity.cs:137](../src/MphRead/Entities/BeamProjectileEntity.cs#L137) in `BeamProjectileEntity.Process`: todo?: might need to revisit
- [ ] [BeamProjectileEntity.cs:340](../src/MphRead/Entities/BeamProjectileEntity.cs#L340) in `BeamProjectileEntity.CheckCollision`: todo: some of these properties are compatible with the entity moving, some aren't
- [ ] [BeamProjectileEntity.cs:563](../src/MphRead/Entities/BeamProjectileEntity.cs#L563) in `BeamProjectileEntity.CheckCollision`: todo: it's kind of lame that you can't get headshots at this range
- [ ] [BeamProjectileEntity.cs:1364](../src/MphRead/Entities/BeamProjectileEntity.cs#L1364) in `BeamProjectileEntity.DrawTrail4`: todo?: not sure if dividing by 4 is strictly correct here
- [ ] [BeamProjectileEntity.cs:1494](../src/MphRead/Entities/BeamProjectileEntity.cs#L1494) in `BeamProjectileEntity.Spawn`: todo?: figure out what the intent behind this actually is
- [ ] [BeamProjectileEntity.cs:1636](../src/MphRead/Entities/BeamProjectileEntity.cs#L1636) in `BeamProjectileEntity.Spawn`: todo?: it's kind of lame that double damage doesn't affect Shock Coil
- [ ] [BeamProjectileEntity.cs:1639](../src/MphRead/Entities/BeamProjectileEntity.cs#L1639) in `BeamProjectileEntity.Spawn`: todo: this is the same as the ammo calculation but with ge instead of gt

### CamSeqEntity.cs

`src/MphRead/Entities/CamSeq/CamSeqEntity.cs`

- [ ] [CamSeqEntity.cs:23](../src/MphRead/Entities/CamSeq/CamSeqEntity.cs#L23) in `CamSeqEntity`: todo: clear when changing rooms

### CameraSequence.cs

`src/MphRead/Entities/CamSeq/CameraSequence.cs`

- [ ] [CameraSequence.cs:179](../src/MphRead/Entities/CamSeq/CameraSequence.cs#L179) in `CameraSequence.Process`: todo?: the game only does this when ptr\_tbl\_idx is 14

### DoorEntity.cs

`src/MphRead/Entities/DoorEntity.cs`

- [ ] [DoorEntity.cs:372](../src/MphRead/Entities/DoorEntity.cs#L372) in `DoorEntity.Process`: todo: bits 8/9 and 10 are basically a counter and a bool, and we should just replace them with that

### 01_Zoomer.cs

`src/MphRead/Entities/Enemies/01_Zoomer.cs`

- [ ] [01_Zoomer.cs:63](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L63) in `Enemy01Entity.EnemyProcess`: todo?: owner ent col (although it's unused)
- [ ] [01_Zoomer.cs:111](../src/MphRead/Entities/Enemies/01_Zoomer.cs#L111) in `Enemy01Entity.EnemyProcess`: sktodo: convert this to float math

### 02_Temroid.cs

`src/MphRead/Entities/Enemies/02_Temroid.cs`

- [ ] [02_Temroid.cs:45](../src/MphRead/Entities/Enemies/02_Temroid.cs#L45) in `Enemy02Entity.EnemyInitialize`: sktodo: animation? revisit after implementing Metroid from FH

### 06_Petrasyl4.cs

`src/MphRead/Entities/Enemies/06_Petrasyl4.cs`

- [ ] [06_Petrasyl4.cs:148](../src/MphRead/Entities/Enemies/06_Petrasyl4.cs#L148) in `Enemy06Entity`: todo: this is nearly identical to the Petrasyl3 version

### 10_BarbedWarWasp.cs

`src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs`

- [ ] [10_BarbedWarWasp.cs:51](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L51) in `Enemy10Entity`: todo: this is mostly identical to War Wasp
- [ ] [10_BarbedWarWasp.cs:135](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L135) in `Enemy10Entity`: todo: this is identical to War Wasp except for the increment
- [ ] [10_BarbedWarWasp.cs:170](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L170) in `Enemy10Entity`: todo: identical to Warp Wasp
- [ ] [10_BarbedWarWasp.cs:181](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L181) in `Enemy10Entity`: todo: identical to Warp Wasp
- [ ] [10_BarbedWarWasp.cs:253](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L253) in `Enemy10Entity`: todo: same as War Wasp Behavior02 except for the state comparison and replacement value in StartMovingToward
- [ ] [10_BarbedWarWasp.cs:290](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L290) in `Enemy10Entity`: todo: same as War Wasp Behavior01 except it doesn't have \_attackDelay
- [ ] [10_BarbedWarWasp.cs:310](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L310) in `Enemy10Entity`: todo: same as War Wasp Behavior03 except for the step distance
- [ ] [10_BarbedWarWasp.cs:354](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L354) in `Enemy10Entity`: todo: same as War Wasp Behavior06 except for setting the animation
- [ ] [10_BarbedWarWasp.cs:373](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L373) in `Enemy10Entity`: todo: same as War Wasp Behavior07
- [ ] [10_BarbedWarWasp.cs:384](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L384) in `Enemy10Entity`: todo: same as War Wasp Behavior08
- [ ] [10_BarbedWarWasp.cs:395](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L395) in `Enemy10Entity`: todo: same as War Wasp Behavior10
- [ ] [10_BarbedWarWasp.cs:407](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L407) in `Enemy10Entity`: todo: same as War Wasp
- [ ] [10_BarbedWarWasp.cs:421](../src/MphRead/Entities/Enemies/10_BarbedWarWasp.cs#L421) in `Enemy10Entity`: todo: same as War Wasp

### 11_Shriekbat.cs

`src/MphRead/Entities/Enemies/11_Shriekbat.cs`

- [ ] [11_Shriekbat.cs:74](../src/MphRead/Entities/Enemies/11_Shriekbat.cs#L74) in `Enemy11Entity`: todo: really no need for these to be separate functions

### 12_Geemer.cs

`src/MphRead/Entities/Enemies/12_Geemer.cs`

- [ ] [12_Geemer.cs:37](../src/MphRead/Entities/Enemies/12_Geemer.cs#L37) in `Enemy12Entity`: todo: identical to Zoomer except for a few values
- [ ] [12_Geemer.cs:116](../src/MphRead/Entities/Enemies/12_Geemer.cs#L116) in `Enemy12Entity.EnemyProcess`: todo: everything after this is the same as Zoomer (without the hit\_player or sub call at the end)
- [ ] [12_Geemer.cs:159](../src/MphRead/Entities/Enemies/12_Geemer.cs#L159) in `Enemy12Entity.EnemyProcess`: sktodo: convert this to float math

### 18_AlimbicTurret.cs

`src/MphRead/Entities/Enemies/18_AlimbicTurret.cs`

- [ ] [18_AlimbicTurret.cs:87](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L87) in `Enemy18Entity.EnemyProcess`: todo?: owner ent col (although it's unused)
- [ ] [18_AlimbicTurret.cs:286](../src/MphRead/Entities/Enemies/18_AlimbicTurret.cs#L286) in `Enemy18Entity.EnemyGetDrawInfo`: todo: is\_visible

### 20_CretaphidEye.cs

`src/MphRead/Entities/Enemies/20_CretaphidEye.cs`

- [ ] [20_CretaphidEye.cs:468](../src/MphRead/Entities/Enemies/20_CretaphidEye.cs#L468) in `Enemy20Entity.EnemyGetDrawInfo`: sktodo: the beam col model also looks bad, also not sure why

### 23_PsychoBit.cs

`src/MphRead/Entities/Enemies/23_PsychoBit.cs`

- [ ] [23_PsychoBit.cs:70](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L70) in `Enemy23Entity.EnemyInitialize`: todo: same hack is applied when seeking target facing below, since a Psycho Bit moving straight up out of its spawner
- [ ] [23_PsychoBit.cs:460](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L460) in `Enemy23Entity`: todo: same as Behavior00 except using the first pair of factors and not setting animation
- [ ] [23_PsychoBit.cs:606](../src/MphRead/Entities/Enemies/23_PsychoBit.cs#L606) in `Enemy23Entity`: todo: nearly the same as Behavior11

### 24_Gorea1A.cs

`src/MphRead/Entities/Enemies/24_Gorea1A.cs`

- [ ] [24_Gorea1A.cs:436](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L436) in `Enemy24Entity`: todo: bugfix: the tracks for Battlehammer and Shock Coil are swapped (kept on purpose: the original game has this bug too; docs/ACCURACY.md D)
- [ ] [24_Gorea1A.cs:470](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L470) in `Enemy24Entity.EnemyProcess`: todo?: this seems much better aligned with the offset in X instead of Z. add a bugifx? (checked: the original game uses the same Z offset (rev 1 0x21344FC))
- [ ] [24_Gorea1A.cs:511](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L511) in `Enemy24Entity`: sktodo: this is the same as the leg with the values baked in -- share code?
- [ ] [24_Gorea1A.cs:620](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L620) in `Enemy24Entity`: sktodo (document): walking
- [ ] [24_Gorea1A.cs:632](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L632) in `Enemy24Entity`: sktodo (document): sprinting
- [ ] [24_Gorea1A.cs:839](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L839) in `Enemy24Entity`: sktodo: (document) slam attack
- [ ] [24_Gorea1A.cs:891](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L891) in `Enemy24Entity`: sktodo: (document) swipe attack?
- [ ] [24_Gorea1A.cs:910](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L910) in `Enemy24Entity`: sktodo: (document) switching to Gorea1B
- [ ] [24_Gorea1A.cs:1134](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1134) in `Enemy24Entity.StartShots`: todo: is it a bug that ShotCooldown is used for the right arm instead of AutofireCooldown? (checked: the original game does the same, both arms take their cooldown from the weapon table +0x10 (rev 1 0x2136C5C))
- [ ] [24_Gorea1A.cs:1182](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1182) in `Enemy24Entity.SetShotAnimation`: todo: is it a bug that only one arm's flags are updated? (checked: the original game does the same (rev 1 0x2136DD8))
- [ ] [24_Gorea1A.cs:1304](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1304) in `Enemy24Entity.Behavior11`: sktodo: is this timer ever set? (checked: the original game never sets this timer either (rev 1 0x21346F4, 0x2136A24))
- [ ] [24_Gorea1A.cs:1415](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1415) in `Enemy24Entity`: sktodo: (document) at least one use is timer for giving up on turning to face the player in state 1
- [ ] [24_Gorea1A.cs:1416](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1416) in `Enemy24Entity`: sktodo: also timer for stopping when initially walking toward player in state 2 (called via Behavior22)
- [ ] [24_Gorea1A.cs:1513](../src/MphRead/Entities/Enemies/24_Gorea1A.cs#L1513) in `Enemy24Entity.EnemyGetDrawInfo`: sktodo: does this happen? (checked: never happens, 1A is hidden before 1B shows and the other way round, as in the original game)

### 28_Gorea1B.cs

`src/MphRead/Entities/Enemies/28_Gorea1B.cs`

- [ ] [28_Gorea1B.cs:42](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L42) in `Enemy28Entity`: sktodo: grapple timer?
- [ ] [28_Gorea1B.cs:173](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L173) in `Enemy28Entity.CheckPlayerCollision`: todo: is it a bug that this doesn't normalize the vector like 1A and legs do? (checked: the original game doesn't normalize it either (rev 1 0x213BC90-0x213BDC4))
- [ ] [28_Gorea1B.cs:269](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L269) in `Enemy28Entity.Func2139FE8`: sktodo: (document) rotating the vector to the end point by +/-3 degrees, then updating all the other vecs
- [ ] [28_Gorea1B.cs:287](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L287) in `Enemy28Entity`: sktodo: (document) setting up equal segments in a straight line between first and (updated) last
- [ ] [28_Gorea1B.cs:350](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L350) in `Enemy28Entity.UpdateGrappleDrawMatrix`: todo: implement "count\_1" to support multiple grapple beams?
- [ ] [28_Gorea1B.cs:401](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L401) in `Enemy28Entity.Func213B90C`: sktodo: add a bugfix (checked: the original game never writes the clamped vector back (rev 1 0x213B86C-0x213BB84); MphRead's fix is already in and makes no visible difference, left as is)
- [ ] [28_Gorea1B.cs:444](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L444) in `Enemy28Entity`: sktodo: (document) interpolating vector based on "index and percentage to next index" from e.g. field38
- [ ] [28_Gorea1B.cs:500](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L500) in `Enemy28Entity.Func213B678`: todo?: consider fx32 math in some places? discrepancies accumulate over time,
- [ ] [28_Gorea1B.cs:629](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L629) in `Enemy28Entity.State07`: sktodo: I'm not changing the multiplication by 0.04 since I think the above will usually cause \_field10 to get a fresh value every frame? (checked: the stale value and the x0.04 (163/4096) are the original game's (rev 1 0x21399F8); the push direction next to it was wrong and is fixed, see docs/ACCURACY.md)
- [ ] [28_Gorea1B.cs:876](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L876) in `Enemy28Entity.Func213AF2C`: sktodo (document): throwing after timer
- [x] ~~[28_Gorea1B.cs:1039](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1039) in `Enemy28Entity.Behavior02`: todo: credits~~ (fixed: the credits and results after the ending are built in the menus (Second Hunt 0.2.0); docs/ENDINGS.md section 3)
- [ ] [28_Gorea1B.cs:1433](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1433) in `Enemy28Entity`: sktodo: (document) coming up with successive vectors based on the fractional index
- [ ] [28_Gorea1B.cs:1452](../src/MphRead/Entities/Enemies/28_Gorea1B.cs#L1452) in `Enemy28Entity`: sktodo: parameter and variable names

### 31_Gorea2.cs

`src/MphRead/Entities/Enemies/31_Gorea2.cs`

- [x] ~~[31_Gorea2.cs:546](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Enemies/31_Gorea2.cs#L546) (upstream line) in `Enemy31Entity`: todo: this is pretty jerky~~ (fixed: Gorea 2 hovered at twice the original speed with twice the jitter; now the original 30 Hz rate, rev 1 0x213D58C; docs/ACCURACY.md)
- [x] ~~[31_Gorea2.cs:958](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L958) in `Enemy31Entity.State17`: todo: credits~~ (fixed: the credits and results after the ending are built in the menus (Second Hunt 0.2.0); docs/ENDINGS.md section 3)
- [ ] [31_Gorea2.cs:1452](../src/MphRead/Entities/Enemies/31_Gorea2.cs#L1452) in `Enemy31Entity`: todo: this is the same as Behavior09, except a different

### 33_GoreaMeteor.cs

`src/MphRead/Entities/Enemies/33_GoreaMeteor.cs`

- [ ] [33_GoreaMeteor.cs:308](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L308) in `Enemy33Entity.EnemyGetDrawInfo`: todo: when shot once with the Power Beam, the meteor stays red, add bugfix? (checked: the original game keeps it red too (rev 1 0x2140DC4))
- [ ] [33_GoreaMeteor.cs:372](../src/MphRead/Entities/Enemies/33_GoreaMeteor.cs#L372) in `Enemy33Entity`: todo: this is the same as Behavior00 except field1B6 is used

### 35_Voldrum.cs

`src/MphRead/Entities/Enemies/35_Voldrum.cs`

- [ ] [35_Voldrum.cs:61](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L61) in `Enemy35Entity.Setup`: todo: this additional hack is needed so the orientation isn't totally wrong after spawning,
- [ ] [35_Voldrum.cs:218](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L218) in `Enemy35Entity`: todo: this is similar to Psycho Bit (cylinder only)
- [ ] [35_Voldrum.cs:231](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L231) in `Enemy35Entity`: todo: this is similar to Psycho Bit (doesn't have cross vector)
- [ ] [35_Voldrum.cs:246](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L246) in `Enemy35Entity`: todo: this is similar to Psycho Bit (no values struct, no y speed)
- [ ] [35_Voldrum.cs:296](../src/MphRead/Entities/Enemies/35_Voldrum.cs#L296) in `Enemy35Entity`: todo: could be shared as part of the speed update function

### 36_Voldrum.cs

`src/MphRead/Entities/Enemies/36_Voldrum.cs`

- [ ] [36_Voldrum.cs:33](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L33) in `Enemy36Entity`: todo: share code (pass header values common with S00)
- [ ] [36_Voldrum.cs:41](../src/MphRead/Entities/Enemies/36_Voldrum.cs#L41) in `Enemy36Entity.Setup`: todo: see comment about this hack in Enemy35Entity

### 39_FireSpawn.cs

`src/MphRead/Entities/Enemies/39_FireSpawn.cs`

- [ ] [39_FireSpawn.cs:164](../src/MphRead/Entities/Enemies/39_FireSpawn.cs#L164) in `Enemy39Entity.State2`: todo?: main player slot index for consistency?

### 41_Slench.cs

`src/MphRead/Entities/Enemies/41_Slench.cs`

- [ ] [41_Slench.cs:1024](../src/MphRead/Entities/Enemies/41_Slench.cs#L1024) in `Enemy41Entity.SetUpSlam`: todo: maybe make a bugfix for the jumping/snapping before slamming?

### 49_ForceFieldLock.cs

`src/MphRead/Entities/Enemies/49_ForceFieldLock.cs`

- [ ] [49_ForceFieldLock.cs:18](../src/MphRead/Entities/Enemies/49_ForceFieldLock.cs#L18) in `Enemy49Entity`: todo: revisit this?

### 50_HitZone.cs

`src/MphRead/Entities/Enemies/50_HitZone.cs`

- [ ] [50_HitZone.cs:80](../src/MphRead/Entities/Enemies/50_HitZone.cs#L80) in `Enemy50Entity.HandleMessage`: todo?: main player slot index for consistency?

### EnemyInstanceEntity.cs

`src/MphRead/Entities/EnemyInstanceEntity.cs`

- [ ] [EnemyInstanceEntity.cs:14](../src/MphRead/Entities/EnemyInstanceEntity.cs#L14) in `EnemyInstanceEntityData`: todo: nullable?

### EnemySpawnEntity.cs

`src/MphRead/Entities/EnemySpawnEntity.cs`

- [ ] [EnemySpawnEntity.cs:546](../src/MphRead/Entities/EnemySpawnEntity.cs#L546) in `FhEnemySpawnEntity`: todo: FH enemy spawning

### EntityBase.cs

`src/MphRead/Entities/EntityBase.cs`

- [ ] [EntityBase.cs:14](../src/MphRead/Entities/EntityBase.cs#L14) in `EntityBase`: todo: use init for Id
- [ ] [EntityBase.cs:37](../src/MphRead/Entities/EntityBase.cs#L37) in `EntityBase`: todo: look into getting rid of this in favor of EntityCollision
- [ ] [EntityBase.cs:749](../src/MphRead/Entities/EntityBase.cs#L749) in `EntityBase`: todo: item and enemy spawners

### FlagBaseEntity.cs

`src/MphRead/Entities/FlagBaseEntity.cs`

- [ ] [FlagBaseEntity.cs:78](../src/MphRead/Entities/FlagBaseEntity.cs#L78) in `FlagBaseEntity`: todo: is\_visible

### ForceFieldEntity.cs

`src/MphRead/Entities/ForceFieldEntity.cs`

- [ ] [ForceFieldEntity.cs:20](../src/MphRead/Entities/ForceFieldEntity.cs#L20) in `ForceFieldEntity`: todo: start replace/removing the Active property on EntityBase

### ItemInstanceEntity.cs

`src/MphRead/Entities/ItemInstanceEntity.cs`

- [ ] [ItemInstanceEntity.cs:24](../src/MphRead/Entities/ItemInstanceEntity.cs#L24): todo: preallocation

### ItemSpawnEntity.cs

`src/MphRead/Entities/ItemSpawnEntity.cs`

- [ ] [ItemSpawnEntity.cs:238](../src/MphRead/Entities/ItemSpawnEntity.cs#L238) in `FhItemSpawnEntity.Process`: todo: FH item spawning logic
- [ ] [ItemSpawnEntity.cs:248](../src/MphRead/Entities/ItemSpawnEntity.cs#L248) in `FhItemSpawnEntity`: todo: FH entity node ref

### JumpPadEntity.cs

`src/MphRead/Entities/JumpPadEntity.cs`

- [ ] [JumpPadEntity.cs:220](../src/MphRead/Entities/JumpPadEntity.cs#L220) in `FhJumpPadEntity.GetModelTransform`: todo: it would be nicer to just compute beamTransform as relative on creation

### NodeDefenseEntity.cs

`src/MphRead/Entities/NodeDefenseEntity.cs`

- [ ] [NodeDefenseEntity.cs:357](../src/MphRead/Entities/NodeDefenseEntity.cs#L357) in `NodeDefenseEntity`: todo: is\_visible

### ObjectEntity.cs

`src/MphRead/Entities/ObjectEntity.cs`

- [ ] [ObjectEntity.cs:429](../src/MphRead/Entities/ObjectEntity.cs#L429) in `ObjectEntity.Process`: todo: add an option to disable this check

### OctolithFlagEntity.cs

`src/MphRead/Entities/OctolithFlagEntity.cs`

- [ ] [OctolithFlagEntity.cs:357](../src/MphRead/Entities/OctolithFlagEntity.cs#L357) in `OctolithFlagEntity`: todo: is\_visible for base and flag

### PlatformEntity.cs

`src/MphRead/Entities/PlatformEntity.cs`

- [ ] [PlatformEntity.cs:67](../src/MphRead/Entities/PlatformEntity.cs#L67) in `PlatformEntity`: todo: would be nice to have the ability to manipulate these transforms manually
- [ ] [PlatformEntity.cs:523](../src/MphRead/Entities/PlatformEntity.cs#L523) in `PlatformEntity.Activate`: todo: update port flags
- [ ] [PlatformEntity.cs:886](../src/MphRead/Entities/PlatformEntity.cs#L886) in `PlatformEntity.GetDrawInfo`: todo?: the game has two conditions, one for checking the node ref when that flag is set,
- [ ] [PlatformEntity.cs:1625](../src/MphRead/Entities/PlatformEntity.cs#L1625) in `FhPlatformEntity.FhPlatformEntity`: todo: support loading genericmover, and do something similar for unused MPH models
- [ ] [PlatformEntity.cs:1645](../src/MphRead/Entities/PlatformEntity.cs#L1645) in `FhPlatformEntity.Process`: todo: collision and stuff
- [ ] [PlatformEntity.cs:1656](../src/MphRead/Entities/PlatformEntity.cs#L1656) in `FhPlatformEntity.Process`: todo: must be active

### HalfturretEntity.cs

`src/MphRead/Entities/Players/HalfturretEntity.cs`

- [ ] [HalfturretEntity.cs:30](../src/MphRead/Entities/Players/HalfturretEntity.cs#L30) in `HalfturretEntity`: todo: is this always zero?
- [ ] [HalfturretEntity.cs:523](../src/MphRead/Entities/Players/HalfturretEntity.cs#L523) in `HalfturretEntity.GetEmission`: todo?: it's kinda weird that this doesn't use the team emission color
- [ ] [HalfturretEntity.cs:531](../src/MphRead/Entities/Players/HalfturretEntity.cs#L531) in `HalfturretEntity`: todo: share with player

### PlayerAi.cs

`src/MphRead/Entities/Players/PlayerAi.cs`

- [ ] [PlayerAi.cs:1277](../src/MphRead/Entities/Players/PlayerAi.cs#L1277) in `PlayerAiData.UpdatePathWeights`: todo: revisit this hack. in order to make sure time-based and occurrence-based state changes happen in same
- [ ] [PlayerAi.cs:2112](../src/MphRead/Entities/Players/PlayerAi.cs#L2112) in `PlayerAiData.Func1_214A098`: todo?: this condition is just for efficiency, but it's probably the wrong value since OC is also checked
- [ ] [PlayerAi.cs:2122](../src/MphRead/Entities/Players/PlayerAi.cs#L2122) in `PlayerAiData.Func1_214A098`: todo: bugfix: this should load the pickup type, but loads the beam ID instead (kept on purpose: the original game has this bug too; docs/ACCURACY.md D)
- [ ] [PlayerAi.cs:2657](../src/MphRead/Entities/Players/PlayerAi.cs#L2657) in `PlayerAiData.Func1_21495A4`: todo: bugfix: this is clearly supposed to check Omega Cannon (kept on purpose: the original game has this bug too; docs/ACCURACY.md D)
- [ ] [PlayerAi.cs:3048](../src/MphRead/Entities/Players/PlayerAi.cs#L3048) in `PlayerAiData.Func1_2148DF8`: todo: not sure this volume update matters
- [ ] [PlayerAi.cs:3679](../src/MphRead/Entities/Players/PlayerAi.cs#L3679) in `PlayerAiData.Func2_213DA88`: todo?: bug? using zero for this means all the "vectors" below are from the origin
- [ ] [PlayerAi.cs:3866](../src/MphRead/Entities/Players/PlayerAi.cs#L3866) in `PlayerAiData.Func21449DC`: todo: accuracy?
- [ ] [PlayerAi.cs:4367](../src/MphRead/Entities/Players/PlayerAi.cs#L4367) in `PlayerAiData.Func3_213D388`: todo?: if we exposed "get entity from storage," we wouldn't need a loop
- [ ] [PlayerAi.cs:5207](../src/MphRead/Entities/Players/PlayerAi.cs#L5207) in `PlayerAiData.Func3_213B690`: todo?: confirm if both these fields should be used, given this is not Capture mode
- [ ] [PlayerAi.cs:5449](../src/MphRead/Entities/Players/PlayerAi.cs#L5449) in `PlayerAiData`: todo: depending on what these are used for, we might need a hack for Guardians
- [ ] [PlayerAi.cs:5650](../src/MphRead/Entities/Players/PlayerAi.cs#L5650) in `PlayerAiData.Func3_213AA64`: todo?: bug? checking distance to the origin instead of to this bot or another entity
- [ ] [PlayerAi.cs:5824](../src/MphRead/Entities/Players/PlayerAi.cs#L5824) in `PlayerAiData.Func4_21462DC`: todo: field7 doesn't appear to be updated to anything other than 0
- [ ] [PlayerAi.cs:6864](../src/MphRead/Entities/Players/PlayerAi.cs#L6864) in `PlayerAiData.Func2145738`: todo: bugfix: the game's math is wrong here, using charge pct as a factor between uncharged and min charge values. (kept on purpose: the original game has this bug too; docs/ACCURACY.md D)
- [ ] [PlayerAi.cs:7352](../src/MphRead/Entities/Players/PlayerAi.cs#L7352) in `PlayerAiData.PressL`: todo?: could add bugfix for checking L instead of Magmaul -- not sure how big the impact is (might lead
- [ ] [PlayerAi.cs:8850](../src/MphRead/Entities/Players/PlayerAi.cs#L8850) in `PlayerAiData.FindEntityRef`: todo?: add behavior for seeking deathalt item/spawn?
- [ ] [PlayerAi.cs:8948](../src/MphRead/Entities/Players/PlayerAi.cs#L8948) in `PlayerAiData.FindEntityRef`: todo?: bugfix? this pattern suggests that item spawns are supposed to have their node data
- [ ] [PlayerAi.cs:10048](../src/MphRead/Entities/Players/PlayerAi.cs#L10048) in `PlayerAiData.FindHighestNode`: todo?: bugfixs? the game has a loop that looks like it should find the node with the highest Y pos,
- [ ] [PlayerAi.cs:10781](../src/MphRead/Entities/Players/PlayerAi.cs#L10781) in `PlayerAiData.UpdateNodeDataSetSelection`: todo?: simplify the bit shifting addition nonsense?
- [ ] [PlayerAi.cs:10798](../src/MphRead/Entities/Players/PlayerAi.cs#L10798) in `PlayerAiData.UpdateNodeDataSetSelection`: todo?: it's kind of jank how this is called for each bot, but updates all bots

### PlayerCamera.cs

`src/MphRead/Entities/Players/PlayerCamera.cs`

- [ ] [PlayerCamera.cs:308](../src/MphRead/Entities/Players/PlayerCamera.cs#L308) in `PlayerEntity.UpdateCameraThird1`: todo?: similar to above except for some signs/comparisons
- [ ] [PlayerCamera.cs:380](../src/MphRead/Entities/Players/PlayerCamera.cs#L380) in `PlayerEntity.UpdateCameraThird1`: todo?: similar to above except for some signs/comparisons
- [ ] [PlayerCamera.cs:570](../src/MphRead/Entities/Players/PlayerCamera.cs#L570) in `PlayerEntity`: todo: enhanced free cam
- [ ] [PlayerCamera.cs:626](../src/MphRead/Entities/Players/PlayerCamera.cs#L626) in `PlayerEntity.UpdateCameraFree`: itodo: x and y sensitivity
- [ ] [PlayerCamera.cs:700](../src/MphRead/Entities/Players/PlayerCamera.cs#L700) in `PlayerEntity.UpdateCameraFree`: itodo: this
- [ ] [PlayerCamera.cs:760](../src/MphRead/Entities/Players/PlayerCamera.cs#L760) in `PlayerEntity.UpdateCameraSpectator`: camtodo
- [ ] [PlayerCamera.cs:775](../src/MphRead/Entities/Players/PlayerCamera.cs#L775) in `PlayerEntity.UpdateMatchEndCamera`: todo?: the game updates all players' cameras, possibly because of (non-existent) spectator mode?
- [ ] [PlayerCamera.cs:923](../src/MphRead/Entities/Players/PlayerCamera.cs#L923) in `CameraInfo.Update`: todo?: set transposes and stuff

### PlayerCollision.cs

`src/MphRead/Entities/Players/PlayerCollision.cs`

- [ ] [PlayerCollision.cs:821](../src/MphRead/Entities/Players/PlayerCollision.cs#L821) in `PlayerEntity.HandleCollision`: todo?: the response when moving into walls laterally is also not accurate ("wall sliding")
- [ ] [PlayerCollision.cs:912](../src/MphRead/Entities/Players/PlayerCollision.cs#L912) in `PlayerEntity.HandleCollision`: todo: revisit these calculations and improve the wall climbing "stickiness" issue

### PlayerDialog.cs

`src/MphRead/Entities/Players/PlayerDialog.cs`

- [ ] [PlayerDialog.cs:346](../src/MphRead/Entities/Players/PlayerDialog.cs#L346) in `PlayerEntity.ShowDialogEvent`: todo: avoid allocation
- [ ] [PlayerDialog.cs:372](../src/MphRead/Entities/Players/PlayerDialog.cs#L372) in `PlayerEntity.ShowDialogEvent`: todo?: it's lame that UA tank is the only pickup without a frame/icon

### PlayerDraw.cs

`src/MphRead/Entities/Players/PlayerDraw.cs`

- [ ] [PlayerDraw.cs:122](../src/MphRead/Entities/Players/PlayerDraw.cs#L122) in `PlayerEntity.Draw`: todo: entity visibility check needs to do more than use active room parts
- [ ] [PlayerDraw.cs:179](../src/MphRead/Entities/Players/PlayerDraw.cs#L179) in `PlayerEntity.Draw`: todo: we can just figure out the angle directly from the facing vector
- [ ] [PlayerDraw.cs:611](../src/MphRead/Entities/Players/PlayerDraw.cs#L611) in `PlayerEntity.DrawDeathParticles`: todo: the angle stuff could be removed

### PlayerEntity.cs

`src/MphRead/Entities/Players/PlayerEntity.cs`

- [ ] [PlayerEntity.cs:203](../src/MphRead/Entities/Players/PlayerEntity.cs#L203) in `PlayerEntity`: todo: these settings can change
- [ ] [PlayerEntity.cs:352](../src/MphRead/Entities/Players/PlayerEntity.cs#L352) in `PlayerEntity`: todo: unused?
- [ ] [PlayerEntity.cs:353](../src/MphRead/Entities/Players/PlayerEntity.cs#L353) in `PlayerEntity`: todo: unused?
- [ ] [PlayerEntity.cs:522](../src/MphRead/Entities/Players/PlayerEntity.cs#L522) in `PlayerEntity.Create`: todo: update controls
- [ ] [PlayerEntity.cs:659](../src/MphRead/Entities/Players/PlayerEntity.cs#L659) in `PlayerEntity.Initialize`: todo?: point module
- [ ] [PlayerEntity.cs:771](../src/MphRead/Entities/Players/PlayerEntity.cs#L771) in `PlayerEntity.Spawn`: todo: MP1P
- [ ] [PlayerEntity.cs:780](../src/MphRead/Entities/Players/PlayerEntity.cs#L780) in `PlayerEntity.Spawn`: todo?: a lot of this doesn't need to be set at all in create/init when it gets set every time you spawn anyway
- [ ] [PlayerEntity.cs:784](../src/MphRead/Entities/Players/PlayerEntity.cs#L784) in `PlayerEntity.Spawn`: todo: much of this is the same as what's done in init, so we could use a common method
- [ ] [PlayerEntity.cs:917](../src/MphRead/Entities/Players/PlayerEntity.cs#L917) in `PlayerEntity.Spawn`: todo: MP1P
- [ ] [PlayerEntity.cs:950](../src/MphRead/Entities/Players/PlayerEntity.cs#L950) in `PlayerEntity.Spawn`: todo: update more UI fields
- [ ] [PlayerEntity.cs:1022](../src/MphRead/Entities/Players/PlayerEntity.cs#L1022) in `PlayerEntity.InitEnemyHunter`: todo: move to metadata
- [ ] [PlayerEntity.cs:1303](../src/MphRead/Entities/Players/PlayerEntity.cs#L1303) in `PlayerEntity.InitializeWeapon`: todo: MP1P
- [ ] [PlayerEntity.cs:2078](../src/MphRead/Entities/Players/PlayerEntity.cs#L2078) in `PlayerEntity.TakeDamage`: todo: update stats
- [x] ~~[PlayerEntity.cs:2127](../src/MphRead/Entities/Players/PlayerEntity.cs#L2127) in `PlayerEntity.TakeDamage`: todo: unlock hunter for multiplayer~~ (fixed: multiplayer hunters unlock from the campaign saves like the original; docs/MULTIPLAYER_MENU.md "UNLOCKS")
- [ ] [PlayerEntity.cs:2282](../src/MphRead/Entities/Players/PlayerEntity.cs#L2282) in `PlayerEntity.TakeDamage`: todo?: the game also updates another kills stat(?) here
- [ ] [PlayerEntity.cs:2516](../src/MphRead/Entities/Players/PlayerEntity.cs#L2516) in `PlayerEntity.TakeDamage`: todo: clean this up
- [ ] [PlayerEntity.cs:2541](../src/MphRead/Entities/Players/PlayerEntity.cs#L2541) in `PlayerEntity.TakeDamage`: todo: diagonals (behind feature switch)
- [ ] [PlayerEntity.cs:2616](../src/MphRead/Entities/Players/PlayerEntity.cs#L2616) in `PlayerEntity.LoadWeaponNames`: todo: Guardian alt form

### PlayerHud.cs

`src/MphRead/Entities/Players/PlayerHud.cs`

- [ ] [PlayerHud.cs:114](../src/MphRead/Entities/Players/PlayerHud.cs#L114) in `PlayerEntity.SetUpHud`: todo: only load what needs to be loaded for the mode
- [ ] [PlayerHud.cs:161](../src/MphRead/Entities/Players/PlayerHud.cs#L161) in `PlayerEntity.SetUpHud`: todo: left-handed mode
- [ ] [PlayerHud.cs:645](../src/MphRead/Entities/Players/PlayerHud.cs#L645) in `PlayerEntity.UpdateHealthbars`: todo?: update radar lights
- [ ] [PlayerHud.cs:680](../src/MphRead/Entities/Players/PlayerHud.cs#L680) in `PlayerEntity.UpdateAmmoBar`: todo?:
- [ ] [PlayerHud.cs:691](../src/MphRead/Entities/Players/PlayerHud.cs#L691) in `PlayerEntity.UpdateAmmoBar`: todo?: update radar lights
- [ ] [PlayerHud.cs:729](../src/MphRead/Entities/Players/PlayerHud.cs#L729) in `PlayerEntity.UpdateWeaponSelect`: todo: invert for left-handed mode
- [ ] [PlayerHud.cs:1827](../src/MphRead/Entities/Players/PlayerHud.cs#L1827) in `PlayerEntity.ProcessHudCapture`: todo: share common colors
- [ ] [PlayerHud.cs:2655](../src/MphRead/Entities/Players/PlayerHud.cs#L2655) in `PlayerEntity`: todo: size/shape (seemingly only used by the bottom screen rank, which is 16x16/square instead of 8x8/square)
- [ ] [PlayerHud.cs:2978](../src/MphRead/Entities/Players/PlayerHud.cs#L2978) in `PlayerEntity.WrapText`: todo?:
- [ ] [PlayerHud.cs:3068](../src/MphRead/Entities/Players/PlayerHud.cs#L3068) in `PlayerEntity.DrawQueuedHudMessages`: todo: support font size

### PlayerInput.cs

`src/MphRead/Entities/Players/PlayerInput.cs`

- [ ] [PlayerInput.cs:326](../src/MphRead/Entities/Players/PlayerInput.cs#L326) in `PlayerEntity.UpdateAimY`: itodo: this
- [ ] [PlayerInput.cs:369](../src/MphRead/Entities/Players/PlayerInput.cs#L369) in `PlayerEntity.UpdateAimX`: itodo: this
- [ ] [PlayerInput.cs:506](../src/MphRead/Entities/Players/PlayerInput.cs#L506) in `PlayerEntity.ProcessBiped`: itodo: x and y sensitivity
- [ ] [PlayerInput.cs:528](../src/MphRead/Entities/Players/PlayerInput.cs#L528) in `PlayerEntity.ProcessBiped`: sktodo: threshold values
- [ ] [PlayerInput.cs:832](../src/MphRead/Entities/Players/PlayerInput.cs#L832) in `PlayerEntity.ProcessBiped`: todo?: auto release
- [ ] [PlayerInput.cs:925](../src/MphRead/Entities/Players/PlayerInput.cs#L925) in `PlayerEntity.ProcessBiped`: todo: use the ability flag for the morph touch button too, even though the game doesn't
- [ ] [PlayerInput.cs:1061](../src/MphRead/Entities/Players/PlayerInput.cs#L1061) in `PlayerEntity.TryFireWeapon`: todo?: make this more solid to avoid e.g. the battlehammer ammo cost thing
- [x] ~~[PlayerInput.cs:1177](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L1177) (upstream line) in `PlayerEntity.ProcessAlt`: todo?: if touch movement for alt form was a thing, this would need extra conditions~~ (fixed: touch controls built from the original's code (PlayerTouch.cs); PlayerTouch.cs; board release T11 (touch check passed 2026-10-10))
- [ ] [PlayerInput.cs:1245](../src/MphRead/Entities/Players/PlayerInput.cs#L1245) in `PlayerEntity.ProcessAlt`: todo?: field35C targeting(?) stuff
- [ ] [PlayerInput.cs:1251](../src/MphRead/Entities/Players/PlayerInput.cs#L1251) in `PlayerEntity.ProcessAlt`: sktodo: threshold values
- [ ] [PlayerInput.cs:1281](../src/MphRead/Entities/Players/PlayerInput.cs#L1281) in `PlayerEntity.ProcessAlt`: itodo: x and y sensitivity
- [ ] [PlayerInput.cs:1334](../src/MphRead/Entities/Players/PlayerInput.cs#L1334) in `PlayerEntity.ProcessAlt`: todo: update field684 (using sign)
- [ ] [PlayerInput.cs:1367](../src/MphRead/Entities/Players/PlayerInput.cs#L1367) in `PlayerEntity.ProcessAlt`: todo: update field688 (using sign)
- [ ] [PlayerInput.cs:1384](../src/MphRead/Entities/Players/PlayerInput.cs#L1384) in `PlayerEntity.ProcessAlt`: todo: update field684
- [ ] [PlayerInput.cs:1393](../src/MphRead/Entities/Players/PlayerInput.cs#L1393) in `PlayerEntity.ProcessAlt`: todo: update field684
- [x] ~~[PlayerInput.cs:1346](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L1346) (upstream line) in `PlayerEntity.ProcessAlt`: todo: touch roll~~ (fixed: touch roll built from the original's code (PlayerTouch.cs); PlayerTouch.cs; board release T11 (touch check passed 2026-10-10))
- [x] ~~[PlayerInput.cs:1514](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Entities/Players/PlayerInput.cs#L1514) (upstream line) in `PlayerEntity.ProcessAlt`: todo: touch boost~~ (fixed: touch boost built from the original's code (PlayerTouch.cs); PlayerTouch.cs; board release T11 (touch check passed 2026-10-10))
- [ ] [PlayerInput.cs:1935](../src/MphRead/Entities/Players/PlayerInput.cs#L1935) in `PlayerEntity.ProcessMovement`: todo: check how much of this overwrites stuff done above
- [ ] [PlayerInput.cs:2291](../src/MphRead/Entities/Players/PlayerInput.cs#L2291) in `PlayerEntity.ProcessInput`: todo: multiple input?
- [ ] [PlayerInput.cs:2353](../src/MphRead/Entities/Players/PlayerInput.cs#L2353) in `PlayerEntity.ProcessInput`: todo?: deal with overflow or whatever
- [ ] [PlayerInput.cs:2379](../src/MphRead/Entities/Players/PlayerInput.cs#L2379) in `PlayerEntity.ProcessInput`: todo?: besides the code duplication, input processing like this should work even if
- [ ] [PlayerInput.cs:2404](../src/MphRead/Entities/Players/PlayerInput.cs#L2404) in `PlayerEntity.ProcessInput`: todo?: deal with overflow or whatever

### PlayerProcess.cs

`src/MphRead/Entities/Players/PlayerProcess.cs`

- [ ] [PlayerProcess.cs:202](../src/MphRead/Entities/Players/PlayerProcess.cs#L202) in `PlayerEntity.ProcessPlayer`: todo: and some global is not set
- [ ] [PlayerProcess.cs:219](../src/MphRead/Entities/Players/PlayerProcess.cs#L219) in `PlayerEntity.ProcessPlayer`: todo: or forced
- [ ] [PlayerProcess.cs:241](../src/MphRead/Entities/Players/PlayerProcess.cs#L241) in `PlayerEntity.ProcessPlayer`: todo: MP1P
- [ ] [PlayerProcess.cs:385](../src/MphRead/Entities/Players/PlayerProcess.cs#L385) in `PlayerEntity.ProcessPlayer`: todo?: FH leftover ammo recharge stuff
- [ ] [PlayerProcess.cs:452](../src/MphRead/Entities/Players/PlayerProcess.cs#L452) in `PlayerEntity.ProcessPlayer`: todo: set to 5/31 if recent touch input
- [ ] [PlayerProcess.cs:666](../src/MphRead/Entities/Players/PlayerProcess.cs#L666) in `PlayerEntity.ProcessPlayer`: todo: bugfix: because Shock Coil restarts every frame, texcoord and other animations don't play (kept on purpose: the original game has this bug too; docs/ACCURACY.md D)
- [ ] [PlayerProcess.cs:1550](../src/MphRead/Entities/Players/PlayerProcess.cs#L1550) in `PlayerEntity.TrySwitchForms`: todo: playable Guardian

### PlayerScan.cs

`src/MphRead/Entities/Players/PlayerScan.cs`

- [ ] [PlayerScan.cs:430](../src/MphRead/Entities/Players/PlayerScan.cs#L430) in `PlayerEntity.DrawScanObjects`: todo: the box and icon both have notable issues with aspect ratio

### PlayerSound.cs

`src/MphRead/Entities/Players/PlayerSound.cs`

- [ ] [PlayerSound.cs:15](../src/MphRead/Entities/Players/PlayerSound.cs#L15) in `PlayerEntity.PlayHunterSfx`: todo: MP1P

### RoomEntity.cs

`src/MphRead/Entities/RoomEntity.cs`

- [x] ~~[RoomEntity.cs:55](../src/MphRead/Entities/RoomEntity.cs#L55) in `RoomEntity.Setup`: todo: unlock the corresponding multiplayer arena when visiting a new planet~~ (fixed: multiplayer arenas unlock from the campaign saves like the original; docs/MULTIPLAYER_MENU.md "UNLOCKS")
- [ ] [RoomEntity.cs:72](../src/MphRead/Entities/RoomEntity.cs#L72) in `RoomEntity.Setup`: todo?: unload collision, etc.
- [ ] [RoomEntity.cs:328](../src/MphRead/Entities/RoomEntity.cs#L328) in `RoomEntity.AddConnector`: todo?: update visited connectors
- [ ] [RoomEntity.cs:389](../src/MphRead/Entities/RoomEntity.cs#L389) in `RoomEntity.UpdateTransition`: todo: revisit this count?
- [ ] [RoomEntity.cs:426](../src/MphRead/Entities/RoomEntity.cs#L426) in `RoomEntity.LoadRoom`: todo: revisit flags
- [ ] [RoomEntity.cs:475](../src/MphRead/Entities/RoomEntity.cs#L475) in `RoomEntity.StartTransition`: todo: MP1P
- [ ] [RoomEntity.cs:502](../src/MphRead/Entities/RoomEntity.cs#L502) in `RoomEntity.StartTransition`: todo?: unload more stuff
- [ ] [RoomEntity.cs:785](../src/MphRead/Entities/RoomEntity.cs#L785) in `RoomEntity.EndTransition`: todo?: faster loading makes this transition kind of abrupt
- [ ] [RoomEntity.cs:1064](../src/MphRead/Entities/RoomEntity.cs#L1064) in `RoomEntity.FindVisibleRoomParts`: todo?: base these values on the aspect ratio or something?
- [ ] [RoomEntity.cs:1069](../src/MphRead/Entities/RoomEntity.cs#L1069) in `RoomEntity.FindVisibleRoomParts`: todo: couldn't we bail a lot earlier for either condition?
- [ ] [RoomEntity.cs:1095](../src/MphRead/Entities/RoomEntity.cs#L1095) in `RoomEntity.Func2117F84`: todo: no need to do this multiple times
- [ ] [RoomEntity.cs:1491](../src/MphRead/Entities/RoomEntity.cs#L1491) in `RoomEntity.IsNodeVisible`: todo: if this never happens, we can just get rid of mask
- [ ] [RoomEntity.cs:1582](../src/MphRead/Entities/RoomEntity.cs#L1582) in `RoomEntity.DrawRoomParts`: todo: use visibility list for portals too

### TriggerVolumeEntity.cs

`src/MphRead/Entities/TriggerVolumeEntity.cs`

- [ ] [TriggerVolumeEntity.cs:32](../src/MphRead/Entities/TriggerVolumeEntity.cs#L32) in `TriggerVolumeEntity.TriggerVolumeEntity`: todo: change the display/color when inactive (same for AreaVolumes)

### Collada.cs

`src/MphRead/Export/Collada.cs`

- [ ] [Collada.cs:640](../src/MphRead/Export/Collada.cs#L640) in `Collada.ExportDlist`: todo: support DIF\_AMB somehow

### Scripting.cs

`src/MphRead/Export/Scripting.cs`

- [ ] [Scripting.cs:55](../src/MphRead/Export/Scripting.cs#L55) in `Scripting.PrintAnimations`: todo: animate diffuse/ambient/specular light colors

### AiPersonality.cs

`src/MphRead/Formats/AiPersonality.cs`

- [ ] [AiPersonality.cs:63](../src/MphRead/Formats/AiPersonality.cs#L63) in `AiPersonality.Load`: todo?: if replacing enemy hunters, consider loading the offset belonging to the one replaced

### Collision.cs

`src/MphRead/Formats/Collision.cs`

- [ ] [Collision.cs:17](../src/MphRead/Formats/Collision.cs#L17) in `EntityCollision`: todo: do we need both?

### CollisionDetection.cs

`src/MphRead/Formats/CollisionDetection.cs`

- [ ] [CollisionDetection.cs:111](../src/MphRead/Formats/CollisionDetection.cs#L111) in `CollisionDetection.CheckBetweenPoints`: sktodo: handle FH collision
- [ ] [CollisionDetection.cs:123](../src/MphRead/Formats/CollisionDetection.cs#L123) in `CollisionDetection.CheckBetweenPoints`: todo: counter
- [ ] [CollisionDetection.cs:372](../src/MphRead/Formats/CollisionDetection.cs#L372) in `CollisionDetection`: todo: revisit this approach
- [ ] [CollisionDetection.cs:402](../src/MphRead/Formats/CollisionDetection.cs#L402) in `CollisionDetection.CheckSphereBetweenPoints`: sktodo: handle FH collision
- [ ] [CollisionDetection.cs:622](../src/MphRead/Formats/CollisionDetection.cs#L622) in `CollisionDetection.CheckInRadius`: sktodo: handle FH collision
- [ ] [CollisionDetection.cs:632](../src/MphRead/Formats/CollisionDetection.cs#L632) in `CollisionDetection.CheckInRadius`: todo: counter
- [ ] [CollisionDetection.cs:793](../src/MphRead/Formats/CollisionDetection.cs#L793) in `CollisionDetection.GetRoomCandidatesForLimits`: sktodo: handle FH collision
- [ ] [CollisionDetection.cs:893](../src/MphRead/Formats/CollisionDetection.cs#L893) in `CollisionDetection.GetEntityCandidates`: sktodo: handle FH collision
- [ ] [CollisionDetection.cs:958](../src/MphRead/Formats/CollisionDetection.cs#L958) in `CollisionDetection.GetRoomCandidatesForPoints`: sktodo: handle FH collision
- [ ] [CollisionDetection.cs:1625](../src/MphRead/Formats/CollisionDetection.cs#L1625) in `CollisionDetection.CheckPortBetweenPoints`: todo?: the game returns false after a check with flags bit 1, but I don't think that's ever set

### Effects.cs

`src/MphRead/Formats/Effects.cs`

- [ ] [Effects.cs:164](../src/MphRead/Formats/Effects.cs#L164) in `EffectFuncBase.FxFunc13`: todo: revisit this hack
- [ ] [Effects.cs:588](../src/MphRead/Formats/Effects.cs#L588) in `EffectEntry`: todo: pre-size?
- [ ] [Effects.cs:756](../src/MphRead/Formats/Effects.cs#L756) in `EffectElementEntry`: todo: pre-size?
- [ ] [Effects.cs:762](../src/MphRead/Formats/Effects.cs#L762) in `EffectElementEntry`: todo: pre-size?
- [ ] [Effects.cs:1412](../src/MphRead/Formats/Effects.cs#L1412) in `EffectParticle.AddRenderItem`: todo: investigate this

### Entity.cs

`src/MphRead/Formats/Entity.cs`

- [ ] [Entity.cs:20](../src/MphRead/Formats/Entity.cs#L20) in `EntityEntry`: todo: use this for partial room visibility
- [ ] [Entity.cs:30](../src/MphRead/Formats/Entity.cs#L30) in `FhEntityEntry`: todo: same as above
- [ ] [Entity.cs:250](../src/MphRead/Formats/Entity.cs#L250) in `ItemSpawnEntityData`: todo: parent? child?

### EntityClass.cs

`src/MphRead/Formats/EntityClass.cs`

- [ ] [EntityClass.cs:498](../src/MphRead/Formats/EntityClass.cs#L498) in `ItemSpawnEntityEditor`: todo: parent? child?

### EntityEnemy.cs

`src/MphRead/Formats/EntityEnemy.cs`

- [ ] [EntityEnemy.cs:205](../src/MphRead/Formats/EntityEnemy.cs#L205) in `EnemySpawnEntityData`: todo: display spheres
- [ ] [EntityEnemy.cs:258](../src/MphRead/Formats/EntityEnemy.cs#L258) in `EnemySpawnEntityEditor`: todo: display sphere

### Formats.cs

`src/MphRead/Formats/Formats.cs`

- [ ] [Formats.cs:42](../src/MphRead/Formats/Formats.cs#L42) in `Node`: sktodo: remove
- [ ] [Formats.cs:179](../src/MphRead/Formats/Formats.cs#L179) in `Material`: todo: yep
- [ ] [Formats.cs:190](../src/MphRead/Formats/Formats.cs#L190) in `Material`: todo: yep
- [ ] [Formats.cs:443](../src/MphRead/Formats/Formats.cs#L443): todo: this class is basically duplicated, should just go from raw struct to entry? or also make this a readonly struct?
- [ ] [Formats.cs:554](../src/MphRead/Formats/Formats.cs#L554): todo: review whether we still need this class after the entity refactor (entity classes aren't using it)
- [ ] [Formats.cs:1284](../src/MphRead/Formats/Formats.cs#L1284): todo: FH game modes

### Frontend.cs

`src/MphRead/Formats/Frontend.cs`

- [ ] [Frontend.cs:164](../src/MphRead/Formats/Frontend.cs#L164) in `Frontend.Parse`: todo: parse DP version (different header)

### Model.cs

`src/MphRead/Formats/Model.cs`

- [ ] [Model.cs:429](../src/MphRead/Formats/Model.cs#L429) in `Model.FilterNodes`: todo: refactor this
- [ ] [Model.cs:498](../src/MphRead/Formats/Model.cs#L498) in `Model`: todo: rename/relocate (and now also deduplicate)
- [ ] [Model.cs:575](../src/MphRead/Formats/Model.cs#L575) in `Model`: todo: merge this into one that works for everything
- [ ] [Model.cs:958](../src/MphRead/Formats/Model.cs#L958) in `Recolor`: todo: just return float color early

### Movie.cs

`src/MphRead/Formats/Movie.cs`

- [x] ~~[Movie.cs:644](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Formats/Movie.cs#L644) (upstream line) in `VxDecoder.Decode`: todo: avoid list allocation? buffers are recycled anyway, but when decoding in realtime, we only need 4 frames~~ (fixed: the rewritten movie decoder keeps at most four frames and recycles them; Formats/Vx/VxDecoder.cs, docs/CLEANROOM_QUEUE.md)
- [x] ~~[Movie.cs:943](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/Formats/Movie.cs#L943) (upstream line) in `VxFrame.Decode`: todo: this doesn't need to be allocated repeatedly~~ (fixed: the rewritten movie decoder reuses one bit reader; Formats/Vx/VxDecoder.cs, docs/CLEANROOM_QUEUE.md)

### NodeData.cs

`src/MphRead/Formats/NodeData.cs`

- [ ] [NodeData.cs:17](../src/MphRead/Formats/NodeData.cs#L17) in `ReadNodeData.TestAll`: todo: version 4
- [ ] [NodeData.cs:31](../src/MphRead/Formats/NodeData.cs#L31) in `ReadNodeData.ReadData`: todo: this
- [ ] [NodeData.cs:35](../src/MphRead/Formats/NodeData.cs#L35) in `ReadNodeData.ReadData`: todo: version 4 (unit2\_Land\_Node.bin)
- [ ] [NodeData.cs:42](../src/MphRead/Formats/NodeData.cs#L42) in `ReadNodeData.ReadData`: todo: when there are multiple sets and when the sets contain multiple lists,

### RawFormats.cs

`src/MphRead/Formats/RawFormats.cs`

- [ ] [RawFormats.cs:204](../src/MphRead/Formats/RawFormats.cs#L204) in `TextureAnimation`: todo: do these need to be used?

### Sound.cs

`src/MphRead/Formats/Sound.cs`

- [ ] [Sound.cs:235](../src/MphRead/Formats/Sound.cs#L235) in `SoundRead`: todo: confirm no unused bytes (given the padding)

### Types.cs

`src/MphRead/Formats/Types.cs`

- [ ] [Types.cs:79](../src/MphRead/Formats/Types.cs#L79) in `RenderItem.RenderItem`: todo: consider using ArrayPool
- [ ] [Types.cs:340](../src/MphRead/Formats/Types.cs#L340) in `Matrix`: todo: could replace this with "keep 3x2"

### GameState.cs

`src/MphRead/GameState.cs`

- [ ] [GameState.cs:195](../src/MphRead/GameState.cs#L195) in `GameState.Setup`: todo: allow other colors (and I guess disable the emission then too)
- [ ] [GameState.cs:408](../src/MphRead/GameState.cs#L408) in `GameState.ProcessFrame`: todo: 1P time up? isn't that handled by death countdown etc.?
- [ ] [GameState.cs:451](../src/MphRead/GameState.cs#L451) in `GameState.ProcessFrame`: todo: more stuff?
- [ ] [GameState.cs:515](https://github.com/NoneGiven/MphRead/blob/26cd8a6fe93dc5e525d1a1bb304fe96001111e55/src/MphRead/GameState.cs#L515) (upstream line) in `GameState.ModeStateAdventure`: todo: game timer/boss record stuff (partly fixed: Gorea 1 and 2 times are kept and saved; boss records still to do (new TODO in GameState.cs); docs/ENDINGS.md section 3)
- [ ] [GameState.cs:1727](../src/MphRead/GameState.cs#L1727) in `StorySave.StorySave`: todo: initialize more fields

### HudInfo.cs

`src/MphRead/HUD/HudInfo.cs`

- [ ] [HudInfo.cs:1053](../src/MphRead/HUD/HudInfo.cs#L1053) in `HudInfo.TestObjects`: todo: share this
- [ ] [HudInfo.cs:1062](../src/MphRead/HUD/HudInfo.cs#L1062) in `HudInfo.TestObjects`: todo?: if 256 colors were used, these would need to be parsed differently
- [ ] [HudInfo.cs:1413](../src/MphRead/HUD/HudInfo.cs#L1413) in `HudElements`: todo: Guardian portrait

### Memory.cs

`src/MphRead/Memory.cs`

- [ ] [Memory.cs:157](../src/MphRead/Memory.cs#L157) in `AddressInfo.AddressInfo`: todo: class
- [ ] [Memory.cs:160](../src/MphRead/Memory.cs#L160) in `AddressInfo.AddressInfo`: todo
- [ ] [Memory.cs:163](../src/MphRead/Memory.cs#L163) in `AddressInfo.AddressInfo`: todo
- [ ] [Memory.cs:164](../src/MphRead/Memory.cs#L164) in `AddressInfo.AddressInfo`: todo
- [ ] [Memory.cs:167](../src/MphRead/Memory.cs#L167) in `AddressInfo.AddressInfo`: todo
- [ ] [Memory.cs:169](../src/MphRead/Memory.cs#L169) in `AddressInfo.AddressInfo`: todo
- [ ] [Memory.cs:170](../src/MphRead/Memory.cs#L170) in `AddressInfo.AddressInfo`: todo
- [ ] [Memory.cs:301](../src/MphRead/Memory.cs#L301) in `Memory.SetBaseAddress`: todo: no idea if this works for non-AMHP1
- [ ] [Memory.cs:351](../src/MphRead/Memory.cs#L351) in `Memory.Run`: todo: we should just detect the version automatically

### MemoryArrays.cs

`src/MphRead/MemoryArrays.cs`

- [ ] [MemoryArrays.cs:316](../src/MphRead/MemoryArrays.cs#L316) in `StructArray.Set`: todo: easiest would be to just copy each property, but that would cause multiple writes;

### Menu.cs

`src/MphRead/Menu.cs`

- [ ] [Menu.cs:610](../src/MphRead/Menu.cs#L610) in `Menu.ShowMenuPrompts`: todo: handle saving for multiplayer
- [ ] [Menu.cs:2404](../src/MphRead/Menu.cs#L2404) in `Menu`: todo?: other enemy hunter combinations

### Enemies.cs

`src/MphRead/Metadata/Enemies.cs`

- [ ] [Enemies.cs:2979](../src/MphRead/Metadata/Enemies.cs#L2979) in `Metadata`: todo: document other replacements

### FrontendMeta.cs

`src/MphRead/Metadata/FrontendMeta.cs`

- [ ] [FrontendMeta.cs:221](../src/MphRead/Metadata/FrontendMeta.cs#L221) in `Metadata`: sktodo: multiple animation files

### Metadata.cs

`src/MphRead/Metadata/Metadata.cs`

- [ ] [Metadata.cs:578](../src/MphRead/Metadata/Metadata.cs#L578) in `Metadata`: todo: consolidate stuff like this
- [ ] [Metadata.cs:1290](../src/MphRead/Metadata/Metadata.cs#L1290) in `Metadata`: todo: files not referenced by this list: powerBeamNoSplatMP\_PS.bin, sparksDown\_PS.bin

### Rooms.cs

`src/MphRead/Metadata/Rooms.cs`

- [ ] [Rooms.cs:270](../src/MphRead/Metadata/Rooms.cs#L270) in `Metadata`: todo: unused?
- [ ] [Rooms.cs:3516](../src/MphRead/Metadata/Rooms.cs#L3516) in `Metadata._roomIds`: todo?
- [ ] [Rooms.cs:3582](../src/MphRead/Metadata/Rooms.cs#L3582) in `Metadata._roomIds`: todo?
- [ ] [Rooms.cs:3615](../src/MphRead/Metadata/Rooms.cs#L3615) in `Metadata._roomIds`: todo?
- [ ] [Rooms.cs:3648](../src/MphRead/Metadata/Rooms.cs#L3648) in `Metadata._roomIds`: todo: ?
- [ ] [Rooms.cs:3681](../src/MphRead/Metadata/Rooms.cs#L3681) in `Metadata._roomIds`: todo: ?
- [ ] [Rooms.cs:3791](../src/MphRead/Metadata/Rooms.cs#L3791) in `Metadata._roomIds`: todo: room ID 8 has the same files as MP1, but a few different parameters

### Weapons.cs

`src/MphRead/Metadata/Weapons.cs`

- [ ] [Weapons.cs:37](../src/MphRead/Metadata/Weapons.cs#L37) in `EquipInfo`: todo?: now that we have these overrides, I don't think enemies and bots setting

### Read.cs

`src/MphRead/Read.cs`

- [ ] [Read.cs:598](../src/MphRead/Read.cs#L598) in `Read.LoadAnimation`: todo?: we need to read as many UV entries as the node frame count, causing a buggy animation
- [ ] [Read.cs:727](../src/MphRead/Read.cs#L727) in `Read`: todo: might as well just convert to float early
- [ ] [Read.cs:979](../src/MphRead/Read.cs#L979) in `Read.GetParticle`: ptodo: see what the game does here; gib3/gib4 nodes are probably meant to be used for these

### Renderer.cs

`src/MphRead/Renderer.cs`

- [ ] [Renderer.cs:363](../src/MphRead/Renderer.cs#L363) in `Scene`: todo: disallow if camera roll is not zero?
- [ ] [Renderer.cs:547](../src/MphRead/Renderer.cs#L547) in `Scene.AddPlayer`: todo: revisit flags
- [ ] [Renderer.cs:618](../src/MphRead/Renderer.cs#L618) in `Scene.OnLoad`: todo: probably revisit this
- [ ] [Renderer.cs:1586](../src/MphRead/Renderer.cs#L1586) in `Scene.UpdateProjection`: todo: update this only when the viewport or camera values change
- [ ] [Renderer.cs:1887](../src/MphRead/Renderer.cs#L1887) in `Scene.TransformCamera`: todo: only update this when the camera values change
- [ ] [Renderer.cs:2539](../src/MphRead/Renderer.cs#L2539) in `Scene.ProcessEffects`: todo: these should really just be FxFuncInfo properties instead of a dictionary
- [ ] [Renderer.cs:3093](../src/MphRead/Renderer.cs#L3093) in `Scene.UpdateScene`: todo: need to handle destroying vs. unloading etc.
- [ ] [Renderer.cs:3502](../src/MphRead/Renderer.cs#L3502) in `Scene.RenderItem`: todo: implement this for volumes as well
- [ ] [Renderer.cs:4150](../src/MphRead/Renderer.cs#L4150) in `Scene.DrawHudDamageModel`: ltodo: we only need to update this loop and matrix stack update if the viewport changes
- [ ] [Renderer.cs:4935](../src/MphRead/Renderer.cs#L4935) in `Scene.OutputUpdate`: todo: this causes flickering
- [ ] [Renderer.cs:5258](../src/MphRead/Renderer.cs#L5258) in `Scene.OutputGetEntityInfo`: rtodo: use entity fields for parent/child

### Scene.cs

`src/MphRead/Scene.cs`

- [ ] [Scene.cs:437](../src/MphRead/Scene.cs#L437) in `LinkedListEnumeratorSpecialized`: todo: revisit for First Hunt entity types

### SceneSetup.cs

`src/MphRead/SceneSetup.cs`

- [ ] [SceneSetup.cs:14](../src/MphRead/SceneSetup.cs#L14) in `SceneSetup`: todo: artifact flags
- [ ] [SceneSetup.cs:156](../src/MphRead/SceneSetup.cs#L156) in `SceneSetup.UpdateAreaHunters`: todo?: the game does this in the cockpit
- [ ] [SceneSetup.cs:273](../src/MphRead/SceneSetup.cs#L273) in `SceneSetup.InitHunterSpawns`: todo?: determine bot level based on octoliths (unused)
- [ ] [SceneSetup.cs:551](../src/MphRead/SceneSetup.cs#L551) in `SceneSetup.LoadResources`: todo: sort this all out by game mode/etc. for what's actually needed
- [ ] [SceneSetup.cs:552](../src/MphRead/SceneSetup.cs#L552) in `SceneSetup.LoadResources`: todo: add an assert if any loading occurs after room init (besides manual model/entity loading)
- [ ] [SceneSetup.cs:596](../src/MphRead/SceneSetup.cs#L596) in `SceneSetup.LoadCommonHunterResources`: todo?: same as above
- [ ] [SceneSetup.cs:647](../src/MphRead/SceneSetup.cs#L647) in `SceneSetup.LoadBombResources`: todo?: not all of these need to be loaded depending on the hunters/mode
- [ ] [SceneSetup.cs:843](../src/MphRead/SceneSetup.cs#L843) in `SceneSetup.LoadEnemyResources`: todo?: pre-allocation(?)
- [ ] [SceneSetup.cs:1057](../src/MphRead/SceneSetup.cs#L1057) in `SceneSetup.LoadItemResources`: todo: pre-allocate
- [ ] [SceneSetup.cs:1086](../src/MphRead/SceneSetup.cs#L1086) in `SceneSetup.LoadItem`: todo: affinity weapon replacement
- [ ] [SceneSetup.cs:1122](../src/MphRead/SceneSetup.cs#L1122) in `SceneSetup.GetExtraEntities`: todo: load entities into unused rooms to make them playable

### Selection.cs

`src/MphRead/Selection.cs`

- [ ] [Selection.cs:183](../src/MphRead/Selection.cs#L183) in `Selection.OnKeyDown`: ctodo: allow toggling collision entries and individual planes
- [ ] [Selection.cs:257](../src/MphRead/Selection.cs#L257) in `Selection.OnKeyHeld`: todo: some transforms (sniper targets in UNIT4\_RM2) aren't consistent when first changing the rotation
- [ ] [Selection.cs:370](../src/MphRead/Selection.cs#L370) in `Selection`: todo: select other animation types, and enable playing in reverse

### Music.cs

`src/MphRead/Sound/Music.cs`

- [ ] [Music.cs:826](../src/MphRead/Sound/Music.cs#L826) in `MusicPlayer.Load`: todo: should look at allocations (including recreating these objects, but especially the byte and float lists internal to NCSF)

### Strings.cs

`src/MphRead/Strings.cs`

- [ ] [Strings.cs:46](../src/MphRead/Strings.cs#L46) in `Strings.ReadStringTable`: todo?: are those not supposed to be parsed? (e.g. boost)

### TestMisc.cs

`src/MphRead.Tools/Testing/TestMisc.cs`

- [ ] [TestMisc.cs:188](../src/MphRead.Tools/Testing/TestMisc.cs#L188) in `TestMisc.ConvertRoomToMph`: todo: nodedata
- [ ] [TestMisc.cs:267](../src/MphRead.Tools/Testing/TestMisc.cs#L267) in `TestMisc.ConvertRoomToFh`: todo: nodedata

### Extract.cs

`src/MphRead/Utility/Extract.cs`

- [ ] [Extract.cs:551](../src/MphRead/Utility/Extract.cs#L551) in `Extract.LoadRuntimeData`: todo: values

### RepackCollision.cs

`src/MphRead/Utility/RepackCollision.cs`

- [ ] [RepackCollision.cs:82](../src/MphRead/Utility/RepackCollision.cs#L82) in `RepackCollision.RepackMphRoom`: todo: once portal editing is supported, calculate planes etc. instead of preserving values
- [ ] [RepackCollision.cs:196](../src/MphRead/Utility/RepackCollision.cs#L196) in `RepackCollision`: todo: handle Level MP5
- [ ] [RepackCollision.cs:972](../src/MphRead/Utility/RepackCollision.cs#L972) in `RepackCollision.RepackFhCollision`: todo: entry index

### RepackEntity.cs

`src/MphRead/Utility/RepackEntity.cs`

- [ ] [RepackEntity.cs:577](../src/MphRead/Utility/RepackEntity.cs#L577) in `Repack.ConvertFhToMph`: sktodo
- [ ] [RepackEntity.cs:1114](../src/MphRead/Utility/RepackEntity.cs#L1114) in `Repack`: todo: better output
- [ ] [RepackEntity.cs:1122](../src/MphRead/Utility/RepackEntity.cs#L1122) in `Repack.CompareRooms`: todo: better game/version selection

### RepackModel.cs

`src/MphRead/Utility/RepackModel.cs`

- [ ] [RepackModel.cs:93](../src/MphRead/Utility/RepackModel.cs#L93) in `Repack.TestRepack`: todo: support animation shares, I guess
- [ ] [RepackModel.cs:1239](../src/MphRead/Utility/RepackModel.cs#L1239) in `Repack.PackModel`: todo: support bounds calculation for models with weighted transforms

## TODOs added in Second Hunt

17 TODO comments in this code aren't in MphRead. Most mark the same 60 Hz conversion in code Second Hunt added. They aren't counted above.

- [ ] [HalfturretEntity.cs:468](../src/MphRead/Entities/Players/HalfturretEntity.cs#L468) in `HalfturretEntity`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:46](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L46) in `PlayerAiData`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:217](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L217) in `PlayerAiData`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:232](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L232) in `PlayerAiData.BotPadFlight`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:301](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L301) in `PlayerAiData.BotDrift`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:350](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L350) in `PlayerAiData.BotClimb`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:354](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L354) in `PlayerAiData.BotClimb`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:363](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L363) in `PlayerAiData.BotClimb`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:445](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L445) in `PlayerAiData.BotItemTick`: todo: FPS stuff
- [ ] [PlayerAiUnstick.cs:455](../src/MphRead/Entities/Players/PlayerAiUnstick.cs#L455) in `PlayerAiData.BotItemTick`: todo: FPS stuff
- [ ] [PlayerDraw.cs:55](../src/MphRead/Entities/Players/PlayerDraw.cs#L55) in `PlayerEntity`: todo: FPS stuff (as Draw's)
- [ ] [PlayerInput.cs:1752](../src/MphRead/Entities/Players/PlayerInput.cs#L1752) in `PlayerEntity.SpawnBomb`: todo: FPS stuff
- [ ] [PlayerPirateBlaster.cs:68](../src/MphRead/Entities/Players/PlayerPirateBlaster.cs#L68) in `PlayerEntity.UpdatePirateBlasterBurst`: todo: FPS stuff
- [ ] [PlayerProcess.cs:1201](../src/MphRead/Entities/Players/PlayerProcess.cs#L1201) in `PlayerEntity.ActivateJumpPad`: todo: FPS stuff
- [ ] [PlayerTouch.cs:144](../src/MphRead/Entities/Players/PlayerTouch.cs#L144) in `PlayerEntity.TouchBoost`: todo: FPS stuff
- [ ] [GameState.cs:548](../src/MphRead/GameState.cs#L548) in `GameState.ModeStateAdventure`: play time is the host's own. todo: the boss records (top three times kept in the options save)
- [ ] [Renderer.cs:4132](../src/MphRead/Renderer.cs#L4132) in `Scene.DrawHudDamageModel`: todo: host drawing of the damage direction indicator
