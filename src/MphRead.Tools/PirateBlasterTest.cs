using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // The Pirate Blaster (MphRead Metadata/PirateBlaster.cs, Players/PlayerPirateBlaster.cs), headless:
    //   -pirateblastertest [arena=Combat Hall] [time=40]
    // seat 0 is a local Trooper; seat 1 a Samus bot. Checks:
    //   - the match switch: a Trooper seat's affinity weapon is the blaster (MatchSettings.PirateBlaster null = on), off
    //     with PirateBlaster = false, and a Samus seat keeps her own
    //   - the Trooper's AffinityWeapon pickup gives (and equips) the blaster; the blaster's own item gives it to Samus
    //   - holding fire: bolts in bursts of 3, 6 frames apart (BurstGap x2), a burst every 40 frames, 5 UA a burst
    //   - its shots: beam PirateBlaster, 12 damage (16 to the head), the Power Beam's effects and sounds without a crash
    // -pirateblastertest bots: a Trooper bot with the switch off and on; on, a bot holding the blaster fires it
    // The blaster is handed over outside the recorded inputs, so this test doesn't replay its match (-replaygolden covers
    // that the old matches still play exactly).
    internal static class PirateBlasterTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            float time = 40;
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaName = value; break;
                case "time": time = Single.Parse(value); break;
                case "bots": Bots(arenaName, time); return;
                case "pictures": Pictures(arenaName); return;
                case "icons": Icons(weaponIcon: false); Icons(weaponIcon: true); return;
                case "impact": // (T4 N7) the Prime hit effect on its own: PirateBlasterImpactProbe.cs
                    string Opt(string k, string d) => args.Skip(1).FirstOrDefault(a => a.StartsWith(k + "="))?[(k.Length + 1)..] ?? d;
                    PirateBlasterImpactProbe.Run(Opt("fx", Path.Combine(Path.GetTempPath(), "mph-bodytest", "bodies", "MP1PowerTrooper")),
                        Opt("out", Path.Combine(Path.GetTempPath(), "blaster_impact.png")),
                        Single.Parse(Opt("scale", "0.5"), System.Globalization.CultureInfo.InvariantCulture), Int32.Parse(Opt("emit", "2")));
                    return;
                case "clipfx": // (T4) the Power Trooper's clips' Prime effects (PirateBlasterImpactProbe.cs)
                    string Opt2(string k, string d) => args.Skip(1).FirstOrDefault(a => a.StartsWith(k + "="))?[(k.Length + 1)..] ?? d;
                    PirateBlasterImpactProbe.ClipFx(Opt2("disc", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "brawl_extract", "prime", "mp1.iso"))),
                        Opt2("match", "shoot,fire,attack").Split(','));
                    return;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var errors = new List<string>();
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = time };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true, BotLevel = 0 });

            // the switch
            var off = new MatchSettings { PirateBlaster = false };
            if (Characters.AffinityBeamFor(settings, settings.Players[0]) != BeamType.PirateBlaster)
            {
                errors.Add("switch: a Trooper seat's affinity isn't the blaster with PirateBlaster = null");
            }
            if (Characters.AffinityBeamFor(off, settings.Players[0]) != null)
            {
                errors.Add("switch: PirateBlaster = false still gives the Trooper the blaster");
            }
            if (Characters.AffinityBeamFor(settings, settings.Players[1]) != null)
            {
                errors.Add("switch: Samus got an app affinity weapon");
            }

            RingCheck(settings, errors);
            OmegaCheck(settings, errors);

            CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity me = PlayerEntity.Players[0];
            PlayerEntity samus = PlayerEntity.Players[1];
            if (me.AffinityBeam != BeamType.PirateBlaster)
            {
                errors.Add($"match start: seat 0's AffinityBeam is {me.AffinityBeam}");
            }
            if (samus.AffinityBeam != Weapons.GetAffinityBeam(Hunter.Samus))
            {
                errors.Add($"match start: Samus's AffinityBeam is {samus.AffinityBeam}");
            }

            long maxFrames = (long)(time * 60);
            var boltFrames = new List<long>();
            var boltUa = new List<int>();
            var damages = new SortedDictionary<string, int>();
            var seen = new HashSet<BeamProjectileEntity>();
            int lastBolts = 0, gives = 0, samusGiven = 0;
            bool given = false;
            while (!host.Ended && host.Frame < maxFrames)
            {
                var input = new CampaignInput();
                if (me.Health == 0)
                {
                    given = false;
                    if (host.Frame % 2 == 0) input.Buttons |= CampaignButtons.Shoot; // respawn
                }
                else if (!given && host.Frame > 30)
                {
                    // the Trooper's affinity item, as an arena's AffinityWeapon spawner would give it
                    me.HostPickUpWeapon(ItemType.AffinityWeapon);
                    given = true;
                    gives++;
                    if (me.CurrentWeapon != BeamType.PirateBlaster)
                    {
                        errors.Add($"frame {host.Frame}: the affinity pickup left seat 0 on {me.CurrentWeapon}");
                    }
                    if (samusGiven == 0 && samus.Health > 0)
                    {
                        samus.HostPickUpWeapon(ItemType.PirateBlaster);
                        samusGiven = samus.AvailableWeapons[BeamType.PirateBlaster] ? 1 : -1;
                    }
                }
                else if (given)
                {
                    input.Buttons |= CampaignButtons.Shoot; // held
                    input.AimDelta = new System.Numerics.Vector2(host.Frame % 240 < 120 ? 3 : -3, 0);
                }
                host.Step(input);
                if (me.HostPirateBlasterBolts != lastBolts)
                {
                    for (int i = lastBolts; i < me.HostPirateBlasterBolts; i++)
                    {
                        boltFrames.Add(host.Frame);
                        boltUa.Add(me.HostAmmo(0));
                    }
                    lastBolts = me.HostPirateBlasterBolts;
                }
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is BeamProjectileEntity b && b.Owner == me && seen.Add(b))
                    {
                        string k = $"{b.Beam} {b.Damage}/{b.HeadshotDamage}";
                        damages[k] = damages.GetValueOrDefault(k) + 1;
                    }
                }
            }

            // bursts: bolts 6 frames apart within a burst, 40 frames between first bolts, 5 UA a burst
            var bursts = new List<List<int>>();
            for (int i = 0; i < boltFrames.Count; i++)
            {
                if (i == 0 || boltFrames[i] - boltFrames[i - 1] > 6)
                {
                    bursts.Add(new List<int>());
                }
                bursts[^1].Add(i);
            }
            int full = 0, short_ = 0;
            var gaps = new SortedDictionary<long, int>();
            var starts = new SortedDictionary<long, int>();
            var uaSpent = new SortedDictionary<int, int>();
            for (int k = 0; k < bursts.Count; k++)
            {
                List<int> burst = bursts[k];
                if (burst.Count == 3) full++; else short_++;
                for (int j = 1; j < burst.Count; j++)
                {
                    long g = boltFrames[burst[j]] - boltFrames[burst[j - 1]];
                    gaps[g] = gaps.GetValueOrDefault(g) + 1;
                }
                int before = k == 0 ? -1 : boltUa[bursts[k - 1][^1]];
                if (before >= 0 && boltUa[burst[^1]] <= before)
                {
                    int spent = before - boltUa[burst[^1]];
                    uaSpent[spent] = uaSpent.GetValueOrDefault(spent) + 1;
                }
                if (k > 0)
                {
                    long s = boltFrames[burst[0]] - boltFrames[bursts[k - 1][0]];
                    starts[s] = starts.GetValueOrDefault(s) + 1;
                }
            }
            Console.WriteLine($"{arena.InGameName}, {host.Frame} frames: seat 0 gives {gives}, bolts {boltFrames.Count}, bursts {bursts.Count} (full {full}, cut short {short_}), "
                + $"deaths {GameState.Deaths[0]}, kills {GameState.Kills[0]}; Samus picked up the blaster item: {(samusGiven == 1 ? "yes" : samusGiven == 0 ? "not tried" : "NO")}");
            Console.WriteLine($"  gaps inside a burst: {string.Join(", ", gaps.Select(kv => $"{kv.Key}f x{kv.Value}"))}");
            Console.WriteLine($"  burst to burst: {string.Join(", ", starts.Select(kv => $"{kv.Key}f x{kv.Value}"))}");
            Console.WriteLine($"  UA a burst: {string.Join(", ", uaSpent.Select(kv => $"{kv.Key} x{kv.Value}"))}");
            Console.WriteLine($"  shots seen (beam damage/head): {string.Join(", ", damages.Select(kv => $"{kv.Key} x{kv.Value}"))}");
            if (gaps.Keys.Any(g => g != PirateBlaster.BurstGap * 2))
            {
                errors.Add("a gap inside a burst isn't 6 frames");
            }
            if (full < 5)
            {
                errors.Add($"only {full} full bursts");
            }
            // only a death cuts a burst short (the empty switch waits for a paid burst: the last 5 UA still fire 3 bolts)
            if (short_ > GameState.Deaths[0])
            {
                errors.Add($"{short_} bursts cut short with {GameState.Deaths[0]} deaths");
            }
            // 60 UA from the pickup = 12 bursts, then the Power Beam
            if (gives == 1 && GameState.Deaths[0] == 0 && bursts.Count != 12)
            {
                errors.Add($"{bursts.Count} bursts from one pickup's 60 UA (12 expected)");
            }
            // held fire: 40 frames burst to burst (longer only across a death and respawn)
            if (!starts.ContainsKey(40) || starts.Keys.Any(s => s < 40))
            {
                errors.Add("bursts aren't 40 frames apart");
            }
            if (uaSpent.Keys.Any(u => u != 5))
            {
                errors.Add("a burst didn't cost 5 UA (a pickup between bursts can top it up: check the list)");
            }
            if (damages.Keys.Any(k => !k.StartsWith("PirateBlaster ")))
            {
                errors.Add("seat 0 fired something that isn't the blaster");
            }
            if (!damages.Keys.Any(k => k == "PirateBlaster 12/16"))
            {
                errors.Add("no 12/16 blaster shot seen");
            }
            if (samusGiven != 1)
            {
                errors.Add("the blaster's item didn't give Samus the blaster");
            }
            foreach (string e in errors)
            {
                Console.WriteLine($"  WRONG {e}");
            }
            bool pass = errors.Count == 0;
            Console.WriteLine(pass ? "pirateblastertest PASS" : $"pirateblastertest FAIL ({errors.Count})");
            Environment.ExitCode = pass ? 0 : 1;
        }

        // the 7-sector weapon ring (owner 2026-10-09): up-left picks the blaster, the other sectors keep their weapons in
        // order, the round trip direction -> sector holds for all seven; a match with the blaster off keeps six
        private static void RingCheck(MatchSettings settings, List<string> errors)
        {
            var offSettings = new MatchSettings { Mode = settings.Mode, Arena = settings.Arena, TimeLimitSeconds = 20, PirateBlaster = false };
            offSettings.Players.AddRange(settings.Players);
            CampaignHost.StartMatch(offSettings);
            if (PlayerEntity.WeaponSlotCount != 6)
            {
                errors.Add($"ring: {PlayerEntity.WeaponSlotCount} slots with the blaster off");
            }
            CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity p = PlayerEntity.Players[0];
            if (PlayerEntity.WeaponSlotCount != 7)
            {
                errors.Add($"ring: {PlayerEntity.WeaponSlotCount} slots with the blaster on");
            }
            var order = Enumerable.Range(0, 7).Select(s => CampaignHost.WheelWeapon(s)).ToArray();
            if (!order.SequenceEqual(new[] { BeamType.VoltDriver, BeamType.Battlehammer, BeamType.Imperialist, BeamType.Judicator,
                BeamType.Magmaul, BeamType.ShockCoil, BeamType.PirateBlaster }))
            {
                errors.Add($"ring: slot order {string.Join(",", order)}");
            }
            if (!Enumerable.Range(0, 7).All(s => CampaignHost.WheelSector(CampaignHost.WheelDirection(s)) == s))
            {
                errors.Add("ring: a sector's middle doesn't map back to it");
            }
            if (CampaignHost.WheelWeapon(CampaignHost.WheelSector(new(0, -1))) != BeamType.Judicator
                || CampaignHost.WheelWeapon(CampaignHost.WheelSector(new(-0.3f, 0.95f))) != BeamType.PirateBlaster
                || CampaignHost.WheelWeapon(CampaignHost.WheelSector(new(0.3f, 0.95f))) != BeamType.VoltDriver)
            {
                errors.Add("ring: down isn't the Judicator, or up-left / up-right aren't the blaster / Volt Driver");
            }
            for (int i = 0; i < 300 && (p.Health == 0 || i < 60); i++)
            {
                host.Step(new CampaignInput { Buttons = i % 2 == 0 ? CampaignButtons.Shoot : 0 }); // spawn
            }
            for (int i = 0; i < 30; i++) host.Step(default);
            p.HostPickUpWeapon(ItemType.Magmaul);
            p.HostPickUpWeapon(ItemType.PirateBlaster);
            var ring = new WeaponRingInput();
            BeamType Pick(System.Numerics.Vector2 stick)
            {
                for (int i = 0; i < 10; i++)
                {
                    System.Numerics.Vector2 wheel = ring.Step(true, stick, p);
                    host.Step(new CampaignInput { Buttons = CampaignButtons.WeaponMenu, WeaponWheel = wheel });
                }
                string during = $"open {host.WeaponMenuOpen} sel {p.WeaponSelection} pointer {PlayerEntity.HostPointerX:0},{PlayerEntity.HostPointerY:0}";
                ring.Step(false, System.Numerics.Vector2.Zero, p);
                for (int i = 0; i < 30; i++) host.Step(default);
                Console.WriteLine($"  pick {stick}: {during} -> {p.CurrentWeapon} (health {p.Health}, alt {p.IsAltForm})");
                return p.CurrentWeapon;
            }
            BeamType mag = Pick(CampaignHost.WheelDirection(4));
            BeamType blaster = Pick(new System.Numerics.Vector2(-0.3f, 0.95f));
            BeamType none = Pick(CampaignHost.WheelDirection(3)); // the Judicator: not held, so the pick stays
            Console.WriteLine($"ring (7 slots): Magmaul sector -> {mag}, up-left -> {blaster}, Judicator sector (not held) -> {none}");
            if (mag != BeamType.Magmaul || blaster != BeamType.PirateBlaster || none != BeamType.PirateBlaster)
            {
                errors.Add("ring: the picks didn't equip as expected");
            }
        }

        // (T4 N22 + N15) the blaster's own key (D-pad cycling, a LAN/online guest's pick: CampaignInput.SelectWeapon) equips it;
        // with the Omega Cannon in the third slot, Power Beam keeps it there, the L2 ring released in the middle
        // (CentrePick) brings it back, and the blaster pick keeps it too (the blaster isn't a third-slot weapon)
        private static void OmegaCheck(MatchSettings settings, List<string> errors)
        {
            CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity p = PlayerEntity.Players[0];
            var idle = new CampaignInput { SelectWeapon = BeamType.None }; // (default's 0 is the Power Beam's key)
            for (int i = 0; i < 300 && (p.Health == 0 || i < 60); i++)
            {
                host.Step(new CampaignInput { Buttons = i % 2 == 0 ? CampaignButtons.Shoot : 0, SelectWeapon = BeamType.None }); // spawn
            }
            for (int i = 0; i < 30; i++) host.Step(idle);
            BeamType Select(BeamType beam)
            {
                for (int i = 0; i < 4; i++) host.Step(new CampaignInput { SelectWeapon = beam });
                for (int i = 0; i < 30; i++) host.Step(idle);
                return p.CurrentWeapon;
            }
            p.HostPickUpWeapon(ItemType.PirateBlaster);
            BeamType blaster = Select(BeamType.PirateBlaster);
            p.HostPickUpWeapon(ItemType.OmegaCannon);
            for (int i = 0; i < 30; i++) host.Step(idle);
            BeamType afterPickup = p.CurrentWeapon;
            BeamType power = Select(BeamType.PowerBeam);
            bool shown = p.HostRingOmegaShown;
            var ring = new WeaponRingInput();
            for (int i = 0; i < 20; i++)
            {
                System.Numerics.Vector2 wheel = ring.Step(true, System.Numerics.Vector2.Zero, p);
                host.Step(new CampaignInput { Buttons = CampaignButtons.WeaponMenu, WeaponWheel = wheel, SelectWeapon = BeamType.None });
            }
            ring.Step(false, System.Numerics.Vector2.Zero, p);
            BeamType centre = ring.CentrePick;
            BeamType omega = centre == BeamType.None ? p.CurrentWeapon : Select(centre);
            BeamType blaster2 = Select(BeamType.PirateBlaster);
            bool keptByBlaster = p.HostRingOmegaShown;
            BeamType omega2 = Select(BeamType.OmegaCannon);
            Console.WriteLine($"omega: blaster key -> {blaster}; Omega picked up -> {afterPickup}; Power Beam -> {power} (Omega in the ring {shown}); "
                + $"ring centre -> {centre} -> {omega}; blaster -> {blaster2} (Omega kept {keptByBlaster}) -> Omega key -> {omega2}");
            if (blaster != BeamType.PirateBlaster || blaster2 != BeamType.PirateBlaster)
            {
                errors.Add("omega: the blaster's key didn't equip it");
            }
            if (power != BeamType.PowerBeam || !shown || centre != BeamType.OmegaCannon || omega != BeamType.OmegaCannon)
            {
                errors.Add("omega: the ring's middle didn't bring the Omega Cannon back after the Power Beam");
            }
            if (!keptByBlaster || omega2 != BeamType.OmegaCannon)
            {
                errors.Add("omega: picking the blaster threw the Omega Cannon away");
            }
        }

        // -pirateblastertest pictures: the weapon menu with seven slots (corner arc, then the L2 ring), the blaster picked,
        // 1920x1080 HUD only -> extract_out/pirate_blaster/ring_arc.png, ring_ring.png
        private static void Pictures(string arenaName)
        {
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = 60 };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true, BotLevel = 0 });
            CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
            PlayerEntity p = PlayerEntity.Players[0];
            for (int i = 0; i < 300 && (p.Health == 0 || i < 60); i++)
            {
                host.Step(new CampaignInput { Buttons = i % 2 == 0 ? CampaignButtons.Shoot : 0 });
            }
            for (int i = 0; i < 30; i++) host.Step(default);
            foreach (ItemType item in new[] { ItemType.VoltDriver, ItemType.Battlehammer, ItemType.Judicator, ItemType.Magmaul, ItemType.PirateBlaster })
            {
                p.HostPickUpWeapon(item);
            }
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "pirate_blaster"));
            System.IO.Directory.CreateDirectory(dir);
            var pick = new CampaignInput { Buttons = CampaignButtons.WeaponMenu, WeaponWheel = CampaignHost.WheelDirection(6) };
            for (int i = 0; i < 10; i++) host.Step(pick);
            CampaignSim.RenderHud(host.Scene, 1920, 1080, System.IO.Path.Combine(dir, "ring_arc.png"));
            PlayerEntity.HostWeaponRing = true;
            host.Step(pick);
            CampaignSim.RenderHud(host.Scene, 1920, 1080, System.IO.Path.Combine(dir, "ring_ring.png"));
            PlayerEntity.HostWeaponRing = false;
            // the HUD with the blaster equipped (its weapon icon by the ammo bar), as the app sets the icon
            PlayerEntity.HostInstantWeaponIcon = true;
            for (int i = 0; i < 30; i++) host.Step(default);
            CampaignSim.RenderHud(host.Scene, 1920, 1080, System.IO.Path.Combine(dir, "hud_blaster.png"));
            PlayerEntity.HostInstantWeaponIcon = false;
            Console.WriteLine($"equipped {p.CurrentWeapon}; wrote hud_blaster.png");
            Console.WriteLine($"wrote {System.IO.Path.Combine(dir, "ring_arc.png")} + ring_ring.png (selection {p.WeaponSelection}, slots {PlayerEntity.WeaponSlotCount})");
            // (T4 N15) the ring with the Omega Cannon held in the third slot and the Power Beam equipped: Omega in the middle
            p.HostPickUpWeapon(ItemType.OmegaCannon);
            for (int i = 0; i < 30; i++) host.Step(default); // (default's SelectWeapon 0 = the Power Beam's key: back to it)
            PlayerEntity.HostWeaponRing = true;
            var rest = new CampaignInput { Buttons = CampaignButtons.WeaponMenu, SelectWeapon = BeamType.None };
            for (int i = 0; i < 10; i++) host.Step(rest);
            CampaignSim.RenderHud(host.Scene, 1920, 1080, System.IO.Path.Combine(dir, "ring_omega.png"));
            PlayerEntity.HostWeaponRing = false;
            Console.WriteLine($"wrote ring_omega.png (equipped {p.CurrentWeapon}, Omega in the middle {p.HostRingOmegaShown})");
        }

        // -pirateblastertest icons: the weapon menu icons (rad_wepsel.bin, every hunter) as palette-index text + the
        // blaster's icon, and a sheet at 8x -> extract_out/pirate_blaster/icons.png
        private static void Icons(bool weaponIcon)
        {
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "pirate_blaster"));
            System.IO.Directory.CreateDirectory(dir);
            string[] hunters = { "Samus", "Kanden", "Trace", "Sylux", "Nox", "Spire", "Weavel" };
            var sheets = new List<(string, int, int, IReadOnlyList<byte>, int, IReadOnlyList<MphRead.ColorRgba>)>();
            var cells = new List<(int Col, int Row)>();
            int rowCount = 0;
            foreach (string hunter in hunters)
            {
                string file = weaponIcon ? $@"_archives\local{hunter}\hud_weaponicon.bin" : $@"_archives\local{hunter}\rad_wepsel.bin";
                MphRead.Hud.HudObject obj;
                try
                {
                    obj = MphRead.Hud.HudInfo.GetHudObject(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{hunter}: {ex.Message}");
                    continue;
                }
                int frames = obj.CharacterData.Count / (obj.Width * obj.Height);
                Console.WriteLine($"{hunter}: {obj.Width}x{obj.Height}, {frames} frames, palette {obj.PaletteData.Count}");
                for (int f = 0; f < frames; f++)
                {
                    cells.Add((f, rowCount)); sheets.Add((hunter, f, obj.Width, obj.CharacterData, obj.Height, obj.PaletteData));
                }
                cells.Add((-1, rowCount++)); sheets.Add((hunter + " blaster", 0, obj.Width, (weaponIcon ? PlayerEntity.PirateBlasterWeaponIcon(obj).Frame : PlayerEntity.PirateBlasterSelectIcon(obj)), obj.Height, weaponIcon ? PlayerEntity.PirateBlasterWeaponIcon(obj).Palette : obj.PaletteData));
                if (hunter == "Samus")
                {
                    for (int i = 0; i < 16 && i < obj.PaletteData.Count; i++)
                    {
                        MphRead.ColorRgba c = obj.PaletteData[i];
                        Console.WriteLine($"  pal {i,2}: {c.Red,3} {c.Green,3} {c.Blue,3} {c.Alpha,3}");
                    }
                    for (int f = 0; f < frames; f++)
                    {
                        Console.WriteLine($"  frame {f}:");
                        for (int y = 0; y < obj.Height; y++)
                        {
                            var line = new System.Text.StringBuilder("    ");
                            for (int x = 0; x < obj.Width; x++)
                            {
                                int b = Pixel(obj.CharacterData, f, obj.Width, obj.Height, x, y);
                                line.Append(b == 0 ? '.' : "0123456789ABCDEF"[b]);
                            }
                            Console.WriteLine(line);
                        }
                    }
                    IReadOnlyList<byte> blaster = weaponIcon ? PlayerEntity.PirateBlasterWeaponIcon(obj).Frame : PlayerEntity.PirateBlasterSelectIcon(obj);
                    Console.WriteLine("  anim: " + String.Join(" ", obj.AnimParams.Select(a => $"{a.ImageIndex}x{a.Delay}")));
                    Console.WriteLine("  blaster:");
                    for (int y = 0; y < obj.Height; y++)
                    {
                        var line = new System.Text.StringBuilder("    ");
                        for (int x = 0; x < obj.Width; x++)
                        {
                            int b = Pixel(blaster, 0, obj.Width, obj.Height, x, y);
                            line.Append(b == 0 ? '.' : "0123456789ABCDEF"[b]);
                        }
                        Console.WriteLine(line);
                    }
                }
            }
            if (sheets.Count == 0)
            {
                return;
            }
            const int scale = 8;
            int cw = sheets[0].Item3 + 2, ch = sheets[0].Item5 + 2;
            int cols = cells.Max(c => c.Col) + 2;
            for (int n = 0; n < cells.Count; n++)
            {
                if (cells[n].Col < 0)
                {
                    cells[n] = (cols - 1, cells[n].Row);
                }
            }
            int rows = rowCount;
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(cols * cw * scale, rows * ch * scale,
                new SixLabors.ImageSharp.PixelFormats.Rgba32(24, 24, 32, 255));
            for (int n = 0; n < sheets.Count; n++)
            {
                (string _, int f, int w, IReadOnlyList<byte> data, int h, IReadOnlyList<MphRead.ColorRgba> pal) = sheets[n];
                int ox = cells[n].Col * cw + 1, oy = cells[n].Row * ch + 1;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int b = Pixel(data, f, w, h, x, y);
                        if (b == 0)
                        {
                            continue;
                        }
                        MphRead.ColorRgba c = pal[b];
                        var px = new SixLabors.ImageSharp.PixelFormats.Rgba32(c.Red, c.Green, c.Blue, 255);
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                img[(ox + x) * scale + sx, (oy + y) * scale + sy] = px;
                            }
                        }
                    }
                }
            }
            string png = System.IO.Path.Combine(dir, weaponIcon ? "weaponicon.png" : "icons.png");
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, png);
            Console.WriteLine($"wrote {png} ({sheets.Count} icons, last = the blaster)");
        }

        // one pixel of an 8x8-tiled frame (HudObjectInstance.DoTexture's layout)
        private static int Pixel(IReadOnlyList<byte> data, int frame, int w, int h, int x, int y)
        {
            int tilesX = w / 8;
            return data[frame * w * h + (y / 8) * tilesX * 64 + (x / 8) * 64 + (y % 8) * 8 + (x % 8)];
        }

        // -pirateblastertest bots [arena=] [time=]: a Trooper bot with the blaster on and off (MatchSettings.PirateBlaster):
        // frames it held the blaster, morphs, distance walked, shots fired
        private static void Bots(string arenaName, float time)
        {
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            bool pass = true;
            foreach (bool? on in new bool?[] { false, null })
            {
                var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = time, PirateBlaster = on };
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" });
                settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Recolor = 1, Bot = true, Character = Characters.Trooper.Id });
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true });
                CampaignHost host = CampaignHost.StartMatch(settings);
                PlayerEntity bot = PlayerEntity.Players[1];
                var held = new SortedDictionary<BeamType, int>();
                int morphs = 0, bolts0 = 0;
                bool wasAlt = false;
                float walked = 0;
                OpenTK.Mathematics.Vector3 last = bot.Position;
                long firstBlaster = -1;
                int blasterNoUa = 0;
                var shots = new SortedDictionary<BeamType, int>();
                while (!host.Ended && host.Frame < (long)(time * 60))
                {
                    var input = new CampaignInput();
                    host.Step(input);
                    if (bot.Health > 0)
                    {
                        held[bot.CurrentWeapon] = held.GetValueOrDefault(bot.CurrentWeapon) + 1;
                        if (bot.CurrentWeapon == BeamType.PirateBlaster && firstBlaster < 0) firstBlaster = host.Frame;
                        if (bot.CurrentWeapon == BeamType.PirateBlaster && bot.HostAmmo(0) < 5) blasterNoUa++;
                        if (bot.TimeSinceShot == 0) shots[bot.CurrentWeapon] = shots.GetValueOrDefault(bot.CurrentWeapon) + 1;
                        if (bot.IsAltForm && !wasAlt) morphs++;
                        wasAlt = bot.IsAltForm;
                        walked += (bot.Position - last).Length;
                    }
                    last = bot.Position;
                }
                bolts0 = bot.HostPirateBlasterBolts;
                Console.WriteLine($"blaster {(on == false ? "OFF" : "ON ")}: trooper bot morphs {morphs}, walked {walked:0}, blaster bolts {bolts0}, first blaster frame {firstBlaster} (held with UA < 5: {blasterNoUa}), "
                    + $"kills {GameState.Kills[1]} deaths {GameState.Deaths[1]}; held: {string.Join(", ", held.Select(kv => $"{kv.Key} {kv.Value}"))}; shots: {string.Join(", ", shots.Select(kv => $"{kv.Key} {kv.Value}"))}");
                // on: a bot that picked it up fires it; off: nobody gets it
                pass &= on == false ? bolts0 == 0 && firstBlaster < 0 : bolts0 > 0 || held.GetValueOrDefault(BeamType.PirateBlaster) < 3600; // big arenas: a stretch with no one to shoot
            }
            Console.WriteLine(pass ? "pirateblastertest bots PASS" : "pirateblastertest bots FAIL");
            Environment.ExitCode = pass ? 0 : 1;
        }
    }
}
