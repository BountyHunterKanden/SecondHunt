# Arm cannons for the native bodies (Trooper, Dark Samus)

First-person arm cannons for the Trooper and Dark Samus (2026-10-08/09). Plan items D1 and D2 in
`docs/CHARACTERS_AND_SKINS.md`. Everything here is drawing only. Shots, aim, hitboxes, the sim and replays are unchanged
(`-replaygolden` 9/9 exact after each change).

## A. First person (owner approved 2026-10-08 22:47)

- The cannon is cut from the body's own model (Core `Campaign/BodyArmCannon.cs`: the triangles weighted on the cannon
  arm's elbow subtree) and drawn with the body's own materials (App `Campaign/CampaignArmCannon.cs`). It replaces MPH's
  first-person gun on the frames MPH draws it (App `Campaign/MatchArmCannon.cs`).
- The cannon tip (muzzle locator: Trooper `R_gun_LCTR`, Dark Samus per `DarkSamusBody.TipOf`) sits on MPH's muzzle
  point, where the sim spawns every shot. It follows MPH's own gun motion (recoil, charge kick, the raise).
- Sizes: Trooper 0.7, Echoes Dark Samus 0.7, Corruption Dark Samus 0.75 (times the body scale).
- Trooper: the charge glow, muzzle flash and smoke sit under the spike by the hand (`BodyArmCannon.TrooperBlast`,
  through MphRead's `PlayerEntity.HostFxMuzzle`). Echoes Dark Samus: cannon lit x1.6 (`EchoesLight`).
- Tried and dropped: moving the cannon forward so its tip meets the flash (the forearm armour covers the screen); the
  Trooper's idle claw animation (owner 2026-10-08 23:57: reverted; the mechanism stays in `BodyArmCannon` unused).
- Default on. Gun-adjust pause (L3 + R3): D-pad left/right = MPH's gun / the cannon, up/down = size.

## B. Third person (owner OK 2026-10-09 ~00:30)

**Measured** (dev `--es armcannonmeasure 1` logs it each frame; player's frame, from the player's position):

| | Trooper (running, shooting) | Dark Samus (MP2 / MP3) |
|---|---|---|
| Body's cannon tip | 0.36 right, 0.50 up, 0.85 ahead | 0.25 right, 0.90 up, 1.00 ahead |
| Where the sim spawns the shot | 0.10 right, 0.67 up, 0.39 ahead | 0.10 right, 0.90 up, 0.45 ahead |
| Tip to shot spawn | 0.50-0.60 | 0.55-0.60 |
| Shot passes beside the tip | about 0.27 | about 0.17 |
| Barrel vs aim | 8-17 degrees off | 1-2 degrees (D14 holds the arm on the aim) |

MPH draws the third-person charge glow and muzzle flash at the hidden MPH model's shoot node (`Metadata.MuzzleOffests`
through the Samus/Weavel model's pose). With a native body drawn instead, that point is often nowhere near the arm
(beside the Trooper's head, at Dark Samus's waist).

**Built, option A (default on):** the glow and flash go on each native body's own cannon tip, every seat wearing one
(bots and other players too). `MatchArmCannon.CannonFx3P` works the tip out after each frame's pose
(`CampaignRetroBody.CannonInWorld`) and hands it to MphRead as an offset in the player's own frame
(`PlayerEntity.HostFxShootOffset`, additive host hook in `PlayerDraw.cs`; null = vanilla). The gun-adjust pause
toggle switches it together with the first-person cannon. Dev `--es armcannon3p 0` = vanilla placement.
Picture: `handoffs/first-person-arm-cannons_2026-10-08_pictures/12_partB_optionA.png`.

**Option B (beam drawn from the tip), built 2026-10-09, TURNED DOWN (owner ~02:35 on the Odin: "the bend looks bad"; dev only now, the Troopers get the hold + tip flash like Dark Samus, on the Odin 02:44:52):** owner 00:55 "the troopers are most
off" (with the flash on his tip, the shot still appears ~0.3 right of / 0.1-0.35 below / 0.4 behind his blade). Each shot
is drawn moved by `BeamProjectileEntity.HostDrawShift` (cannon tip minus the sim's spawn point), easing to nothing
`HostDrawFade` (2) units along its path: trail, past positions, model and in-flight effect. The sim's `Position`, every
hit and the impact effects are untouched (`GetDrawInfo` draws from moved copies and puts the sim's back exactly);
`-replaygolden view=third` 9/9 exact. The App sets the shift from `BeamProjectileEntity.HostSpawned` (end of `Spawn`,
per shot): `MatchArmCannon.ShiftCannonBeam`, from the seat's `HostFxShootOffset`, only for seats drawn in third person
(not the main player in first person: his first-person cannon's tip is already on the muzzle). Dev `--es armcannonbeam 1`
(Troopers) or `all` (every native body); `armcannonmeasure 1` also logs each moved shot. Together with the arm hold
(`--es armcannonhold 1`, below) the shot leaves from his blade on the aim. Picture:
`handoffs/first-person-arm-cannons_2026-10-08_pictures/13_trooper_fix.png`. Untested: spread shots (each gets the same
shift), the Shock Coil's trail, point-blank hits, on the Odin in motion.

## The Trooper's arm doesn't aim in third person (owner 00:55: correct the Troopers; hold built dev-off 2026-10-09)

- **What:** in third person the Trooper's cannon arm points 8-17 degrees off where he's aiming (measured while running
  and shooting, torso clips `A_standshot_pirate` / `A_gunloop_pirate`). His arm follows the Space Pirate clips, which
  hold the gun in their own pose; only the aim bone's twist (`NativeBodyAnimator.AimTwist`) turns him toward the aim.
  Dark Samus doesn't have this: her arm is held on the aim in every pose (D14, `DarkSamusBody`), 1-2 degrees off.
- **Why it matters:** with the flash now on his tip, a shot can visibly leave in a slightly different direction from
  the way his blade points, most noticeable from the side.
- **Likely fix, if the owner asks:** a Trooper version of D14: hold the cannon arm (right shoulder/elbow chain) on the
  aim direction over the clips, as `DarkSamusBody` does for her (aim-space hold of `R_shoulder`; a local override
  failed there, see `project_dark_samus_clipmap` memory and `handoffs/dark-samus-clip-map_*`). Owner decision first,
  like D14 was (vanilla hunters keep the cannon forward in every pose). The work is in the characters thread's files
  (`NativeBody.cs` / the Trooper's `BodyClips`), so ask Multiplayer characters and skins before touching them.
- **Built 2026-10-09, DEFAULT ON since 01:37 (owner; dev `--es armcannonhold 0` = off):** `CampaignArmCannon.HoldTrooperArm` reuses
  `DarkSamusBody.Hold` on the Trooper's `R_gun_LCTR` (frame 0 of `A_gunloop_pirate`) through his `Clips.AfterPose`, set
  from this thread's file (no edit to the characters thread's). Off while wall clinging. Barrel vs aim 0-2 degrees.
- **Measure again:** `--es armcannonmeasure 1` with a Trooper skin; the log's last field is the barrel-vs-aim angle.

## The Trooper's raised arm (2026-10-09 ~03:00; TURNED DOWN 16:46: the arm clipped through the body)

Owner ~03:00: "he is shooting so far right of where the beam actually happens" -- pose his upper body so the cannon is
where the shot really starts. MPH spawns every shot at the first-person muzzle: in the player's frame (left, up, forward
of p.Position) about (-0.10, 0.67, 0.39) at a level aim, i.e. eye height just right of centre (`-armcannontest reach`:
his Head_1 is at (0.18, 0.55, 0.22) -- the pirate stands side-on, right shoulder forward; R_shoulder (-0.18, 0.41, 0.20);
upper arm 0.22, elbow to tip 0.50). The hold leaves his tip at (-0.38, 0.41, 0.79): 0.28 right, 0.26 low. Even straight
up his upper arm can't put the elbow on the shot's line from where his shoulder is, so `CampaignArmCannon.RaiseArm`
(after the hold, every seat, not on walls): his chest straightened `TrooperLean` (20 deg, about Spine_2), the upper arm
turned so the elbow sits on the line MPH's shot flies along (`GunMuzzlePosition` + the aim, as far forward as the arm
reaches), the forearm along the aim with the held blade's roll. Result: tip on the line (lateral -0.103 vs -0.101,
height on the aimed line), 0 deg off; the shot starts inside his forearm and leaves from the blade. Dev
`--es armcannonraise 1` (`armcannonlean <deg>`; 30 looked the same as 20). Picture
`handoffs/first-person-arm-cannons_2026-10-08_pictures/14_trooper_raised.png`. Not seen: on the Odin in motion, wall
cling exits, the unmorph, extreme aim pitch.

**Turned down 2026-10-09 16:46** (owner, Odin screenshot): "they make it so that the arm clips through the body" --
elbow on the shot line + chest leaned back pushed the upper arm into his shoulder/head. `RaiseArm` is gone.

## The Trooper's new pose: chest turn + arm forward (2026-10-09 17:30; TURNED DOWN ~17:35, dev only)

Owner 16:46: the pose before the experiments (hold only) was the best so far; bring the cannon closer to where the
beam starts without contorting the arm or clipping it; a small gap is fine (vanilla models have one).
`CampaignArmCannon.AimArm` (after the hold, every seat, not on walls):
1. His chest (`Spine_2`) turns `TrooperTwist` (30 deg) about the vertical, bringing the right shoulder in toward the
   shot; the neck (`Neck_1`) turns back as much so the head still faces ahead (a shooter bringing the gun to the eye).
2. The shoulder turns as one frame (upper arm + elbow hinge axis) so the upper arm points forward and up
   `TrooperArmLift` (45 deg, capped at 70 deg above the horizontal with the aim), `TrooperArmFlare` out (0).
3. The elbow then only opens about its own hinge to put the barrel on the aim: no sideways bend, no twist.
Measured standing (Data Shrine, dev body views, level aim): tip off the shot's line 0.34 (hold only 0.64, the clips
0.82, the 16:54 arm-only try 0.44); tip (-0.20, 0.43, 0.92) vs the shot start (-0.09, 0.75, 0.43). The rest is height:
standing, his head is ~0.6 below MPH's eye (the shot starts above his horns), which no natural arm pose can close.
Side effect: the blade's roll follows the arm (the hinge is kept), so it reads edge-on from his right.
Live switch: the gun-adjust pause (L3 + R3), D-pad left/right cycles NEW pose -> OLD pose (hold only) -> MPH's gun.
Dev: `--es armcannonraise 0` (start on the old pose), `armcannonlift / armcannonflare / armcannontwist <deg>`.
Dev pictures: `--es armcannonbodyviews <dir> [armcannonbodyframe N] [armcannonposes "clips|hold|raise:45,0,30|..."]`
(`ArmCannonBodyViews.cs`: the posed body alone from six sides on one frame, all poses, + points.txt for marking the
shot line; solo arena `--solo 1 --match Battle --arena "Data Shrine" --hunter Weavel --skins 0:MP1WaveTrooper`, the
player spawns by render ~2000).

**All of it turned down 2026-10-09 ~17:35** (owner on the Odin: "This is bad too ... go back to the way it was before
we tried any of these methods"): the Trooper's arm is his own Space Pirate clips again (barrel 8-17 deg off the aim, as
documented above), the third-person flash/charge glow still on his cannon tip (option A, owner OK 00:45). The hold and
the new pose stay in the tree, dev only: `--es armcannonhold 1` (+ `--es armcannonraise 1`).

## Shots drawn from the arm (owner GO 2026-10-09 ~17:55; built 18:20, `App/Campaign/ArmCannonBeamPath.cs`)
A fresh start after the poses: the arm stays as his clips have it; the SHOT is drawn from the arm instead. Draw only:
every shot really starts at the eye (the sim's first-person muzzle) and flies, hits and replays exactly as before, so
it's fair, netcode and replays don't change, and each viewer's game draws shots from the cannon tip of the body IT
shows (players without the imports see Weavel = vanilla; nobody is locked out, nothing hosted).
Once per shot at its spawn (`BeamProjectileEntity.HostSpawned` -> `MatchArmCannon.ShiftCannonBeam` ->
`DrawShotFromArm`), for a Trooper drawn in third person (`HostFxShootOffset` = his posed tip):
- landing point E = the real shot's first wall hit along its path (MaxDistance, else 40 units);
- tip behind a wall from the eye: no shift, hidden for 0.6 (`HostDrawHide`);
- line tip -> E clear: `HostDrawShift = tip - spawn`, eased to nothing at E (`HostDrawFadeAt = |E - spawn|`): the bolt runs
  on the straight line tip -> E (the linear ease makes it exactly that line), meeting the real shot where it lands;
- blocked >= 2 from the tip: converge onto the real path 0.5 before the obstacle (if that line is clear);
- blocked beside him (< 2), or the converge line blocked: no shift, hidden until 0.3 past the obstacle;
- point-blank (E < 2 away) and blocked: vanilla.
Count (wall check, 18:00, 8 goldens): 92.7% clear, 3.6% point-blank, 1.6% beside, 2.1% mid-flight.
Upstream draw hooks: `HostDrawFadeAt`, `HostDrawHide`; the Imperialist ray (`SpawnSniperBeam`) drawn through
`HostShifted`; a ricochet (Judicator) clears the shift at its first bounce. Shock Coil / missiles / bolts go through
`GetDrawInfo`'s shifted positions. Magmaul arcs: E is a straight-line estimate.
Pause (L3 + R3, main player wearing a cannon): D-pad left/right cycles shots from the arm / vanilla shots / MPH.
Dev: `--es armcannonshots 0` (start vanilla) / `all` (Dark Samus too); `armcannonmeasure 1` logs `armshot:` per shot.
