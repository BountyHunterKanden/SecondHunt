using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // The Trooper's Shield Wall (docs/CHARACTERS_AND_SKINS.md D11, MphRead Players/PlayerShield.cs), headless:
    //   -shieldtest [arena=Combat Hall] [time=120] [level=2] [troopers=1,2] [shooter] [aimsign=1] [me=trooper] [out=<dir>]
    // a Battle with trooper bots (Weavel + Character "trooper") and a plain bot; seat 0 is the local player with no input,
    // or with `shooter` turns to the nearest trooper and fires (so troopers plant facing seat 0's camera: the frames where
    // one does, close by, are listed for desktop screenshots of the replay). `me=trooper`: seat 0 is a trooper too, turns
    // to the nearest player, holds the shield button ~4 s whenever it can plant, and fires when aimed (the steps with his
    // barrier up are listed: a third-person replay shows it from behind). `imperialist` (with me=trooper; pass an out=
    // outside the repo, its replay isn't reproducible): seat 0 holds the Imperialist mid-match, which must zoom and
    // never plant. Also checks the HUD's weapon icon: with PlayerEntity.HostInstantWeaponIcon (as the app sets it) it shows
    // the Imperialist on the first frame after the switch, without it the icon is still spinning.
    // Prints what each seat's barrier did (plants, shots blocked, damage taken by it, breaks, bots' flanking frames), then
    // records the match, saves it, loads the file and replays it: every step's checksum must match.
    internal static class ShieldWallTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            float time = 120;
            int level = 2;
            int[] troopers = { 1, 2 };
            string? outDir = null;
            bool shooter = false;
            float aimSign = 1;
            bool meTrooper = false;
            bool imperialist = false;
            bool iconSpin = false; // with imperialist: the HUD weapon icon without the app's instant-icon hook (MphRead's spin)
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaName = value; break;
                case "time": time = Single.Parse(value); break;
                case "level": level = Int32.Parse(value); break;
                case "troopers": troopers = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Int32.Parse).ToArray(); break;
                case "out": outDir = value; break;
                case "shooter": shooter = true; break;
                case "aimsign": aimSign = Single.Parse(value); break;
                case "me": meTrooper = value.Equals("trooper", StringComparison.OrdinalIgnoreCase); shooter |= meTrooper; break;
                case "imperialist": imperialist = true; break;
                case "iconspin": iconSpin = true; break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            Characters.ShieldWallTest = true; // the Shield Wall is parked (D16): only this test gives the Trooper it
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Model ff = Read.GetModelInstance("ForceField").Model;
            Console.WriteLine($"ForceField model: {ff.Nodes.Count} nodes, {ff.Recolors.Count} palettes; node 0 bounds "
                + $"{ff.Nodes[0].MinBounds} .. {ff.Nodes[0].MaxBounds}");

            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = time };
            settings.Players.Add(new MatchPlayer
            {
                Hunter = meTrooper ? Characters.Trooper.Base : Hunter.Samus,
                Nickname = "Player1",
                Character = meTrooper ? Characters.Trooper.Id : null
            });
            Hunter[] others = { Hunter.Kanden, Hunter.Spire, Hunter.Trace };
            for (int seat = 1; seat <= 3; seat++)
            {
                bool trooper = troopers.Contains(seat);
                settings.Players.Add(new MatchPlayer
                {
                    Hunter = trooper ? Characters.Trooper.Base : others[seat - 1],
                    Recolor = trooper ? seat - 1 : 0,
                    Bot = true,
                    BotLevel = level,
                    Character = trooper ? Characters.Trooper.Id : null
                });
            }
            Console.WriteLine($"Battle in {arena.InGameName}, {time} s, bot level {level}: "
                + string.Join(", ", settings.Players.Select((p, i) => $"{i} {p.Hunter}{(p.Character != null ? " (TROOPER)" : "")}{(p.Bot ? "" : " local")}")));

            // record
            var rec = new ReplayRecorder(settings, "shieldtest");
            CampaignHost host = CampaignHost.StartMatch(settings);
            rec.Started(host.Scene);
            long maxFrames = (long)(time * 60) + 600;
            var sw = Stopwatch.StartNew();
            int upFrames = 0;
            var facingFrames = new System.Collections.Generic.List<int>();
            float aimErr = 0;
            int aimed = 0;
            int heldFrames = 0;
            var myUpSteps = new System.Collections.Generic.List<int>();
            int impFrames = 0, impShieldFrames = 0, impZoomFrames = 0, impPlantsBefore = 0;
            string? iconCheck = null;
            BeamType lastWeapon = BeamType.None;
            PlayerEntity.HostInstantWeaponIcon = imperialist && !iconSpin; // as the app sets it (CampaignRenderer.SetPlayerHooks)
            if (imperialist && !meTrooper)
            {
                throw new ArgumentException("imperialist needs me=trooper");
            }
            while (!host.Ended && host.Frame < maxFrames)
            {
                var input = new CampaignInput();
                PlayerEntity me = PlayerEntity.Players[0];
                if (shooter && me.Health > 0)
                {
                    PlayerEntity? target = null;
                    float best = Single.MaxValue;
                    for (int i = 1; i < settings.Players.Count; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        float d = (p.Position - me.Position).LengthSquared;
                        if ((p.HostShieldWall || meTrooper) && p.Health > 0 && d < best)
                        {
                            best = d;
                            target = p;
                        }
                    }
                    if (target != null)
                    {
                        OpenTK.Mathematics.Vector3 to = target.Position - me.Position;
                        OpenTK.Mathematics.Vector3 aim = me.NetAimVector;
                        float yawTo = MathF.Atan2(to.X, to.Z), yawAim = MathF.Atan2(aim.X, aim.Z);
                        float dyaw = (yawTo - yawAim) * 180 / MathF.PI;
                        while (dyaw > 180) dyaw -= 360;
                        while (dyaw < -180) dyaw += 360;
                        float horiz = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
                        float pitchTo = MathF.Atan2(to.Y + 0.6f, horiz) * 180 / MathF.PI;
                        float pitchAim = MathF.Asin(Math.Clamp(aim.Y, -1, 1)) * 180 / MathF.PI;
                        float dpitch = pitchTo - pitchAim;
                        // MphRead turns -delta / 4 degrees per frame; half the error each frame, at most 6 degrees
                        input.AimDelta = new System.Numerics.Vector2(-aimSign * Math.Clamp(dyaw * 0.5f, -6, 6) * 4,
                            -Math.Clamp(dpitch * 0.5f, -6, 6) * 4);
                        aimErr += MathF.Abs(dyaw);
                        aimed++;
                        if (MathF.Abs(dyaw) < 10)
                        {
                            input.Buttons |= CampaignButtons.Shoot;
                        }
                    }
                }
                if (meTrooper && me.Health > 0)
                {
                    // hold the shield ~4 s once planted, then let go; while it can plant, tap until it does (a fresh press)
                    if (me.ShieldUp)
                    {
                        heldFrames++;
                        if (heldFrames < 240)
                        {
                            input.Buttons |= CampaignButtons.Zoom;
                        }
                    }
                    else
                    {
                        heldFrames = 0;
                        if (me.ShieldCooldown == 0 && host.Frame % 2 == 0)
                        {
                            input.Buttons |= CampaignButtons.Zoom;
                        }
                    }
                }
                if (imperialist && host.Frame >= 2400 && host.Frame < 4800)
                {
                    // the middle third: seat 0 holds the Imperialist (given by the test, outside the sim: no replay
                    // check in this mode), so the zoom button must zoom and never plant
                    me.AvailableWeapons[BeamType.Imperialist] = true;
                    var ammo = (int[])typeof(PlayerEntity).GetField("_ammo",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(me)!;
                    ammo[0] = ammo[1] = 400; // its shots (the test's, as the weapon)
                    if (me.CurrentWeapon != BeamType.Imperialist && host.Frame % 2 == 0) // a fresh press
                    {
                        input.SelectWeapon = BeamType.Imperialist;
                    }
                }
                host.Step(input);
                if (me.ShieldUp)
                {
                    myUpSteps.Add(rec.File.Steps.Count);
                }
                if (imperialist && me.CurrentWeapon == BeamType.Imperialist && me.Health > 0)
                {
                    impFrames++;
                    impShieldFrames += me.ShieldUp ? 1 : 0;
                    impZoomFrames += me.EquipInfo.Zoomed ? 1 : 0;
                    if (lastWeapon != BeamType.Imperialist && iconCheck == null)
                    {
                        // the first frame with it: the HUD weapon icon's state (MphRead PlayerHud.cs HudOnWeaponSwitch)
                        object? icon = typeof(PlayerEntity).GetField("_weaponIconInst",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(me);
                        if (icon is MphRead.Hud.HudObjectInstance inst && inst.AnimFrames != null)
                        {
                            int want = inst.AnimFrames[(int)BeamType.Imperialist];
                            bool shown = inst.CurrentFrame == want && inst.Timer <= 0;
                            iconCheck = $"icon frame {inst.CurrentFrame} (Imperialist's {want}), spin timer {inst.Timer:0.00} -> "
                                + (shown ? "SHOWN AT ONCE" : "STILL SPINNING");
                        }
                        else
                        {
                            iconCheck = "no HUD weapon icon here";
                        }
                    }
                }
                lastWeapon = me.CurrentWeapon;
                if (imperialist && host.Frame == 2400)
                {
                    impPlantsBefore = me.ShieldPlants;
                }
                for (int i = 1; i < settings.Players.Count; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    OpenTK.Mathematics.Vector3 rel = me.Position - p.Position;
                    rel.Y = 0;
                    if (p.ShieldUp && rel.Length < 14 && rel.Length > 2
                        && OpenTK.Mathematics.Vector3.Dot(rel.Normalized(), p.ShieldFacing) > 0.8f)
                    {
                        facingFrames.Add(rec.File.Steps.Count);
                        break;
                    }
                }
                rec.Step(input, host.Scene);
                for (int i = 0; i < settings.Players.Count; i++)
                {
                    if (PlayerEntity.Players[i].ShieldUp)
                    {
                        upFrames++;
                    }
                }
            }
            rec.End(host.Ended);
            if (shooter)
            {
                Console.WriteLine($"shooter: mean aim error {(aimed > 0 ? aimErr / aimed : 0):0.0} degrees over {aimed} frames; "
                    + $"a trooper's barrier faces seat 0 close by on {facingFrames.Count} frames: "
                    + string.Join(" ", facingFrames.Where((f, i) => i == 0 || facingFrames[i - 1] != f - 1).Take(12)));
            }
            if (meTrooper)
            {
                var ranges = new System.Collections.Generic.List<string>();
                for (int i = 0; i < myUpSteps.Count; i++)
                {
                    int start = myUpSteps[i];
                    while (i + 1 < myUpSteps.Count && myUpSteps[i + 1] == myUpSteps[i] + 1)
                    {
                        i++;
                    }
                    ranges.Add($"{start}-{myUpSteps[i]}");
                }
                Console.WriteLine($"me=trooper: seat 0's barrier up on {myUpSteps.Count} steps: {string.Join(" ", ranges.Take(16))}");
            }
            Console.WriteLine($"recorded {rec.File.Steps.Count} steps in {sw.Elapsed.TotalSeconds:0.0} s (ended {host.Ended}); "
                + $"barrier up {upFrames} seat-frames");
            int plants = 0, blocks = 0, breaks = 0, flank = 0, shieldSeats = 0;
            for (int i = 0; i < settings.Players.Count; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                shieldSeats += p.HostShieldWall ? 1 : 0;
                plants += p.ShieldPlants;
                blocks += p.ShieldBlocks;
                breaks += p.ShieldBreaks;
                flank += p.ShieldFlankFrames;
                Console.WriteLine($"  seat {i} {p.Hunter,-7} shield {(p.HostShieldWall ? "yes" : "no "),-3} plants {p.ShieldPlants,3} "
                    + $"blocks {p.ShieldBlocks,4} (damage {p.ShieldBlockedDamage,5}) breaks {p.ShieldBreaks,3} flank frames {p.ShieldFlankFrames,5} "
                    + $"kills {GameState.Points[i],3} deaths {GameState.Deaths[i],3}");
            }

            // replay through a file
            string dir = outDir ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "characters_mockups", "shieldtest"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "shieldtest.mphreplay");
            rec.File.Save(path);
            ReplayFile file = ReplayFile.Load(path);
            int fileTroopers = file.Settings.Players.Count(Characters.HasShieldWall);
            string? why = ReplayPlayer.CheckBeforeStart(file);
            bool exact = false;
            int checkedSteps = 0;
            if (why == null)
            {
                var player = new ReplayPlayer(file);
                CampaignHost replay = player.Start();
                why = player.CheckAfterStart();
                while (why == null && !replay.Ended && player.TryNext(out CampaignInput input))
                {
                    replay.Step(input);
                    if (!player.Check(replay.Scene))
                    {
                        why = player.Failure ?? $"failed at step {player.FailedAt}";
                        break;
                    }
                    checkedSteps++;
                }
                exact = why == null && checkedSteps == file.Steps.Count;
                if (why == null && !exact)
                {
                    why = $"stopped after {checkedSteps} of {file.Steps.Count} steps";
                }
            }
            Console.WriteLine($"replay file {path}: format {ReplayFile.FormatVersion}, {fileTroopers} trooper seats in the file; "
                + (exact ? $"EXACT ({checkedSteps} steps)" : $"NOT EXACT: {why}"));

            int wantSeats = troopers.Length + (meTrooper ? 1 : 0);
            if (imperialist)
            {
                bool impOk = impFrames > 0 && impShieldFrames == 0 && impZoomFrames > 0 && impPlantsBefore > 0;
                bool iconOk = iconCheck != null && iconCheck.EndsWith(iconSpin ? "STILL SPINNING" : "SHOWN AT ONCE");
                Console.WriteLine($"HUD weapon icon ({(iconSpin ? "MphRead's spin" : "instant hook on")}): {iconCheck ?? "never switched"} "
                    + (iconOk ? "OK" : "WRONG"));
                impOk &= iconOk;
                Console.WriteLine($"imperialist: seat 0 held it {impFrames} frames, zoomed {impZoomFrames}, barrier up {impShieldFrames} "
                    + $"(must be 0); plants before it {impPlantsBefore}, in all {PlayerEntity.Players[0].ShieldPlants}; "
                    + (impOk ? "OK" : "WRONG") + " (replay not compared: the test gave the weapon outside the sim)");
                Console.WriteLine(impOk && shieldSeats == wantSeats ? "shieldtest PASS" : "shieldtest FAIL");
                Environment.ExitCode = impOk ? 0 : 1;
                return;
            }
            bool pass = shieldSeats == wantSeats && fileTroopers == wantSeats && plants > 0 && blocks > 0 && exact;
            Console.WriteLine(pass ? "shieldtest PASS" : $"shieldtest FAIL (shield seats {shieldSeats}, plants {plants}, blocks {blocks}, breaks {breaks}, flank {flank}, exact {exact})");
            Environment.ExitCode = pass ? 0 : 1;
        }
    }
}
