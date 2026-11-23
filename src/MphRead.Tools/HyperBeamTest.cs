using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -hyperbeamtest: the Hyper Beam (MphRead Metadata/HyperBeam.cs + PlayerHyperBeam.cs, docs/DARK_SAMUS_HYPER.md H2), in
    // real matches on Combat Hall.
    //   setup:   Dark Samus's affinity weapon is the Hyper Beam on her affinity row; Samus picking it up gets the plain row;
    //            HyperBeam = false (served matches, replays before format 60) and a public build give nobody it
    //   press:   one press: one bolt, 8 damage, 4 UA, for both rows
    //   stream:  a full charge let go (Corruption's, main.dol 11-13): the press's bolt, then a stream of 75 shots (the release
    //            and 74 more) every 2 frames (StreamGap 1 DS frame, Corruption's every other frame) for 150 frames, 3 damage each (Dark Samus's affinity row
    //            4), 25 UA for the lot, one charged-fire sound for it all; fire held through it charges nothing and fires
    //            nothing more; no homing
//   curl:    each charged shot flies Corruption's PhazonBurst curl (BeamHyperWind.cs): off its line, for its PSLT 50-80
//            frames unless it hits first, walls ending it; its seed and its keys as the WPSC's
    //   partial: let go just past MinCharge: a short stream (max(0.1, charge) x StreamTime) for about 10 UA
    //   empty:   a full stream from exactly its UA runs to its end before the empty weapon switch
    //   replay:  format 60 records the Hyper Beam as played (on in a dev build, off in a public one or a served match)
    //   bots:    a Dark Samus bot given it fires streams at players (a Samus bot's use of it is reported)
    //   ring:    with it on, the weapon ring has 8 slots (owner 2026-11-14 Ring A): the Hyper Beam straight up, the vanilla six
    //            then the blaster clockwise, the Judicator straight down; a ring pick up equips it once held; off: 7 slots
    // -hyperbeamtest probe <fx dir> <folded part id> [emit frames]: no test, one imported Corruption system flown as a bolt,
    //   its billboards a line a frame (Probe)
    internal static class HyperBeamTest
    {
        public static void Run(string[] args)
        {
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (args.Length >= 4 && args[1] == "probe")
            {
                Probe(args[2], Convert.ToUInt32(args[3], 16), args.Length >= 5 ? int.Parse(args[4]) : 12);
                return;
            }
            if (!ToolPaths.UseDesktopExtraction()) return;
            var errors = new List<string>();
            Setup(errors);
            Press(errors);
            Stream(errors);
            Curl(errors);
            Copies(errors);
            Partial(errors);
            Empty(errors);
            Replay(errors);
            Bots(errors);
            Ring(errors);
            foreach (string e in errors) Console.WriteLine("  FAIL " + e);
            Console.WriteLine(errors.Count == 0 ? "PASS hyperbeamtest" : $"FAIL hyperbeamtest ({errors.Count})");
        }

        // probe <fx dir> <part id> [emit frames]: an imported Corruption system (Corruption's rules) riding a frame that flies
        // 2.5 m a frame along +Y (as a PhazonBeam bolt), a line a frame: its billboards' count, size, colour and spread
        private static void Probe(string fx, uint id, int emit)
        {
            string swhc = System.IO.Path.Combine(fx, $"{id:x8}.swhc");
            if (System.IO.File.Exists(swhc))
            {
                // a swoosh (path mode, as a bolt's): its points newest first, a line a frame (y, radius, alpha)
                var w = new RetroSwoosh(MphRecomp.Import.Retro.Part.Swoosh.Load(System.IO.File.ReadAllBytes(swhc), mp3: true), 1) { Path = true };
                var spine = new List<(Vector3 Pos, float Radius, Vector4 Color)>();
                var tris = new List<RetroSwoosh.Vertex>();
                for (int f = 0; f < emit + 20; f++)
                {
                    if (f == emit) w.Emitting = false;
                    w.Place(Matrix4.CreateTranslation(0, 2.5f * Math.Min(f, emit), 0));
                    w.Step();
                    spine.Clear();
                    w.Spine(Matrix4.Identity, spine);
                    tris.Clear();
                    w.Triangles(Matrix4.Identity, tris, new Vector3(0, -5, 1));
                    var sb = new System.Text.StringBuilder($"  f{f,2}: {tris.Count / 3} tris{(w.Done ? " (done)" : "")}:");
                    foreach (var (pos, r, c) in spine) sb.Append(r < 0 ? " -" : $" {pos.Y:0.0}/{r:0.00}/{c.W:0.00}");
                    Console.WriteLine(sb.ToString());
                }
                return;
            }
            RetroParticles lib = RetroParticles.Load(fx, mp3: true);
            lib.RetroRules = true;
            var sys = new RetroParticleSystem(lib, id, Matrix4.Identity, follow: true);
            var bb = new List<RetroBillboard>();
            var models = new List<RetroModelParticle>();
            if (emit < 0)
            {
                // still (a charge orb): every 5th frame, what's drawn grouped by texture and blend (lines apart), with each
                // group's count, largest size / line length, spread from the centre and brightest alpha
                for (int f = 0; f < -emit; f++)
                {
                    sys.Step();
                    if (f % 5 != 0) continue;
                    bb.Clear();
                    sys.Collect(bb);
                    models.Clear();
                    sys.CollectModels(models);
                    var sb = new System.Text.StringBuilder($"  f{f,3}:");
                    foreach (var g in bb.GroupBy(b => (b.Texture, b.Mode, Line: b.LineEnd != Vector3.Zero, b.Sorted, b.AlphaFrom)))
                    {
                        float size = g.Max(b => g.Key.Line ? (b.LineEnd - b.Position).Length : b.Size);
                        sb.Append($" [{g.Key.Texture:x8} {g.Key.Mode}{(g.Key.Line ? " line" : "")}{(g.Key.Sorted ? " sort" : "")}"
                            + $"{(g.Key.AlphaFrom != 0 ? $" a{g.Key.AlphaFrom}" : "")} n{g.Count()} sz{size:0.000}"
                            + $" r{g.Min(b => b.Position.Length):0.000}-{g.Max(b => b.Position.Length):0.000} a{g.Max(b => b.Color.W):0.00} w{g.Max(b => b.LineWidthPx):0.0}"
                            + (g.Key.Sorted ? " {" + String.Join(" ", g.Select(b => $"{b.Size:0.00}/{b.Color.X:0.00},{b.Color.Y:0.00},{b.Color.Z:0.00},{b.Color.W:0.00}")) + "}" : "") + "]");
                    }
                    foreach (var g in models.GroupBy(m => (m.Texture, m.Additive)))
                    {
                        sb.Append($" [sq {g.Key.Texture:x8} add {g.Key.Additive} af{g.First().AlphaFrom} n{g.Count()} sc{g.Max(m => m.Transform.Row0.Xyz.Length):0.000}"
                            + $" r{g.Max(m => m.Transform.Row3.Xyz.Length):0.000} a{g.Max(m => m.Color.W):0.00} uv{g.Select(m => m.Uv).Distinct().Count()}:{g.First().Uv}"
                            + $" amed {g.Select(m => m.Color.W).OrderBy(x => x).ElementAt(g.Count() / 2):0.00} a>0.9 {g.Count(m => m.Color.W > 0.9f)} a<0.5 {g.Count(m => m.Color.W < 0.5f)}"
                            + $" rgb {String.Join(" ", g.Take(6).Select(m => $"{m.Color.X:0.00},{m.Color.Y:0.00},{m.Color.Z:0.00},{m.Color.W:0.00}"))}]");
                    }
                    Console.WriteLine(sb.ToString());
                }
                return;
            }
            for (int f = 0; f < emit + 20; f++)
            {
                if (f == emit) sys.Emitting = false;
                sys.Transform = Matrix4.CreateTranslation(0, 2.5f * Math.Min(f, emit), 0);
                sys.Step();
                bb.Clear();
                sys.Collect(bb);
                models.Clear();
                sys.CollectModels(models);
                if (models.Count > 0)
                {
                    float big = models.Max(m => m.Transform.Row0.Xyz.Length), bright = models.Max(m => m.Color.W);
                    Vector3 o = sys.Transform.Row3.Xyz;
                    float far = models.Max(m => (m.Transform.Row3.Xyz - o).Length), near = models.Min(m => (m.Transform.Row3.Xyz - o).Length);
                    Console.Write($"  dist {near:0.000}..{far:0.000} n0 {models[0].Transform.Row1.Xyz.Normalized()}");
                    Console.WriteLine($"  f{f,2}: {models.Count} squares/models, scale <= {big:0.000}, alpha <= {bright:0.00}, tex {models[0].Texture:x}, model {models[0].Model:x}, add {models[0].Additive}");
                }
                if (bb.Count == 0) { Console.WriteLine($"  f{f,2}: none{(sys.Done ? " (done)" : "")}"); continue; }
                float minY = float.MaxValue, maxY = float.MinValue, maxS = 0, maxA = 0;
                foreach (RetroBillboard b in bb)
                {
                    minY = Math.Min(minY, b.Position.Y);
                    maxY = Math.Max(maxY, b.Position.Y);
                    maxS = Math.Max(maxS, b.Size);
                    maxA = Math.Max(maxA, b.Color.W);
                }
                Console.WriteLine($"  f{f,2}: {bb.Count} boards, y {minY:0.0}..{maxY:0.0} (head {2.5f * Math.Min(f, emit):0.0}), size <= {maxS:0.00}, alpha <= {maxA:0.00}, tex {bb[0].Texture:x}, {bb[0].Mode} glow {bb[0].Glow}");
            }
        }

        private static MatchSettings Settings(bool dark, bool? hyperBeam)
        {
            var s = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, HyperBeam = hyperBeam };
            s.Players.Add(dark
                ? new MatchPlayer { Hunter = Hunter.Samus, Character = Characters.DarkSamus.Id, Nickname = "Player1" }
                : new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1" });
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, BotLevel = 0 });
            return s;
        }

        private static CampaignHost Start(bool dark, bool? hyperBeam, out PlayerEntity me, out PlayerEntity other)
        {
            CampaignHost host = CampaignHost.StartMatch(Settings(dark, hyperBeam));
            me = PlayerEntity.Players[0];
            other = PlayerEntity.Players[1];
            for (int f = 0; f < 900; f++)
            {
                host.Step(default); // (spawned, past the spawn invulnerability)
            }
            return host;
        }

        private static void Ring(List<string> errors)
        {
            CampaignHost.StartMatch(Settings(dark: false, hyperBeam: false));
            if (PlayerEntity.WeaponSlotCount != 7) errors.Add($"ring: {PlayerEntity.WeaponSlotCount} slots with the Hyper Beam off");
            CampaignHost host = Start(dark: false, hyperBeam: null, out PlayerEntity me, out _);
            if (PlayerEntity.WeaponSlotCount != 8) errors.Add($"ring: {PlayerEntity.WeaponSlotCount} slots with the Hyper Beam on");
            BeamType[] order = Enumerable.Range(0, 8).Select(CampaignHost.WheelWeapon).ToArray();
            if (!order.SequenceEqual(new[] { BeamType.VoltDriver, BeamType.Battlehammer, BeamType.Imperialist, BeamType.Judicator,
                BeamType.Magmaul, BeamType.ShockCoil, BeamType.PirateBlaster, BeamType.HyperBeam }))
            {
                errors.Add($"ring: slot order {string.Join(",", order)}");
            }
            if (!Enumerable.Range(0, 8).All(i => CampaignHost.WheelSector(CampaignHost.WheelDirection(i)) == i))
            {
                errors.Add("ring: a sector's middle doesn't map back to it");
            }
            BeamType At(float x, float y) => CampaignHost.WheelWeapon(CampaignHost.WheelSector(new(x, y)));
            if (At(0, 1) != BeamType.HyperBeam || At(0, -1) != BeamType.Judicator || At(-0.7f, 0.7f) != BeamType.PirateBlaster
                || At(0.7f, 0.7f) != BeamType.VoltDriver || At(1, 0) != BeamType.Battlehammer || At(-1, 0) != BeamType.ShockCoil)
            {
                errors.Add("ring: up isn't the Hyper Beam, down the Judicator, or the diagonals / sides are off");
            }
            me.HostPickUpWeapon(ItemType.Magmaul);
            me.HostPickUpWeapon(ItemType.HyperBeam);
            var ring = new WeaponRingInput();
            BeamType Pick(System.Numerics.Vector2 stick)
            {
                for (int i = 0; i < 10; i++)
                {
                    System.Numerics.Vector2 wheel = ring.Step(true, stick, me);
                    host.Step(new CampaignInput { Buttons = CampaignButtons.WeaponMenu, WeaponWheel = wheel });
                }
                ring.Step(false, System.Numerics.Vector2.Zero, me);
                for (int i = 0; i < 30; i++) host.Step(default);
                return me.CurrentWeapon;
            }
            BeamType mag = Pick(CampaignHost.WheelDirection(4));
            BeamType hyper = Pick(new System.Numerics.Vector2(0, 1));
            BeamType none = Pick(CampaignHost.WheelDirection(3)); // the Judicator: not held, so the pick stays
            Console.WriteLine($"ring (8 slots): Magmaul sector -> {mag}, up -> {hyper}, Judicator sector (not held) -> {none}");
            if (mag != BeamType.Magmaul || hyper != BeamType.HyperBeam || none != BeamType.HyperBeam)
            {
                errors.Add("ring: the picks didn't equip as expected");
            }
        }

        // turn until a beam has a clear line `reach` units ahead (the target goes on it)
        private static void FaceClear(CampaignHost host, PlayerEntity me, float reach)
        {
            for (int f = 0; f < 600; f++)
            {
                Vector3 a = me.Volume.SpherePosition;
                Vector3 ahead = Flat(me);
                Formats.CollisionResult res = default;
                if (!Formats.CollisionDetection.CheckBetweenPoints(a, a + ahead * reach, Formats.TestFlags.Beams, host.Scene, ref res))
                {
                    return;
                }
                host.Step(new CampaignInput { AimDelta = new System.Numerics.Vector2(4, 0) });
            }
        }

        private static Vector3 Flat(PlayerEntity me) => new Vector3(me.FacingVector.X, 0, me.FacingVector.Z).Normalized();

        // one frame with the target held 3 units ahead at 150 health; returns the damage it took
        private static int Step(CampaignHost host, PlayerEntity me, PlayerEntity other, bool shoot)
        {
            other.Position = me.Position + Flat(me) * 3;
            other.Speed = Vector3.Zero;
            other.Health = 150;
            me.Health = 150; // (the bot shoots back)
            host.Step(new CampaignInput { Buttons = shoot ? CampaignButtons.Shoot : CampaignButtons.None });
            return 150 - other.Health;
        }

        private static void Setup(List<string> errors)
        {
            MatchSettings on = Settings(dark: true, hyperBeam: null);
            MatchSettings off = Settings(dark: true, hyperBeam: false);
            if (Characters.AffinityBeamFor(on, on.Players[0]) != BeamType.HyperBeam) errors.Add("setup: Dark Samus's affinity isn't the Hyper Beam");
            if (Characters.AffinityBeamFor(off, off.Players[0]) != null) errors.Add("setup: HyperBeam = false still gives her the Hyper Beam");
            if (Characters.AffinityBeamFor(on, on.Players[1]) != null) errors.Add("setup: Kanden got an app affinity weapon");
            bool saved = MphRecomp.Config.RecompSettings.PublicBuild;
            try
            {
                MphRecomp.Config.RecompSettings.PublicBuild = true;
                if (Characters.AffinityBeamFor(on, on.Players[0]) != null) errors.Add("setup: a public build gives her the Hyper Beam");
            }
            finally
            {
                MphRecomp.Config.RecompSettings.PublicBuild = saved;
            }
            foreach ((bool dark, bool? hb, ItemType item, WeaponInfo? row, string label) in new (bool, bool?, ItemType, WeaponInfo?, string)[]
            {
                (true, null, ItemType.AffinityWeapon, HyperBeam.WeaponMPAffinity, "Dark Samus, affinity pickup"),
                (true, false, ItemType.AffinityWeapon, null, "Dark Samus, served/old replay"),
                (false, null, ItemType.HyperBeam, HyperBeam.WeaponMP, "Samus, Hyper Beam pickup"),
                (false, null, ItemType.AffinityWeapon, null, "Samus, affinity pickup"),
            })
            {
                using CampaignHost host = Start(dark, hb, out PlayerEntity me, out _);
                me.HostPickUpWeapon(item);
                Console.WriteLine($"[setup] {label}: holds {me.CurrentWeapon} ({me.EquipInfo.Weapon.Description})");
                if (row == null ? me.CurrentWeapon == BeamType.HyperBeam : me.EquipInfo.Weapon != row)
                {
                    errors.Add($"setup: {label} holds {me.EquipInfo.Weapon.Description}");
                }
            }
        }

        private static void Press(List<string> errors)
        {
            foreach (bool dark in new[] { false, true })
            {
                using CampaignHost host = Start(dark, null, out PlayerEntity me, out PlayerEntity other);
                FaceClear(host, me, 4);
                me.HostPickUpWeapon(dark ? ItemType.AffinityWeapon : ItemType.HyperBeam);
                me.HostRefillUa();
                int ua = me.HostAmmo(0), dealt = 0;
                for (int f = 0; f < 30; f++) dealt += Step(host, me, other, shoot: f < 2);
                int spent = ua - me.HostAmmo(0);
                string who = dark ? "Dark Samus" : "Samus";
                Console.WriteLine($"[press] {who}: dealt {dealt}, spent {spent} UA");
                if (dealt != 8 || spent != 4) errors.Add($"press: {who} dealt {dealt} / spent {spent}, expected 8 / 4");
            }
        }

        private sealed record Shots(int Press, int Charge, List<(int Frame, int Damage)> Hits, int Spent, int StreamShots,
            int HeldCharge, bool Streaming, int Sounds, bool LastCharged);

        // hold fire `hold` frames (a press's bolt, then the charge), let go and watch `watch` frames; from `again` frames
        // after letting go fire is held again (-1: never). Hits are (frame after letting go, damage)
        private static Shots Fire(CampaignHost host, PlayerEntity me, PlayerEntity other, int hold, int again = -1, int watch = 240)
        {
            int ua = me.HostAmmo(0), sounds = me.HostHyperBeamShots, press = 0, held = 0;
            for (int f = 0; f < hold; f++) press += Step(host, me, other, shoot: true);
            int charge = me.EquipInfo.ChargeLevel;
            var hits = new List<(int Frame, int Damage)>();
            for (int f = 0; f < watch; f++)
            {
                int d = Step(host, me, other, shoot: again >= 0 && f >= again);
                if (d > 0) hits.Add((f, d));
                if (me.HyperBeamStreaming) held = Math.Max(held, me.EquipInfo.ChargeLevel);
            }
            return new Shots(press, charge, hits, ua - me.HostAmmo(0), me.HostHyperBeamStreamShots, held, me.HyperBeamStreaming,
                me.HostHyperBeamShots - sounds, me.HostHyperBeamLastCharged);
        }

        // the stream's shots for a release at `charge` (PlayerHyperBeam: the release's, then one each StreamGap)
        private static int StreamShotsFor(int charge)
        {
            int length = (int)(Math.Clamp(charge / (float)(HyperBeam.FullCharge * 2), 0.1f, 1f) * HyperBeam.StreamTime * 2);
            return 1 + (length - 1) / (HyperBeam.StreamGap * 2);
        }

        private static string Hits(Shots s) => string.Join(" ", s.Hits.ConvertAll(h => $"{h.Frame}:{h.Damage}"));

        // the release's hits: `count` of `damage`, each StreamGap x2 frames after the one before
        private static void CheckStream(List<string> errors, string who, Shots s, int count, int damage)
        {
            if (s.Hits.Count != count) errors.Add($"{who}: {s.Hits.Count} hits, expected {count}");
            for (int i = 0; i < s.Hits.Count; i++)
            {
                // (its curl can rise into the head: the charged headshot, one more; and lands a frame either side)
                if (s.Hits[i].Damage != damage && s.Hits[i].Damage != damage + 1)
                {
                    errors.Add($"{who}: hit {i} dealt {s.Hits[i].Damage}, expected {damage} (or the headshot's {damage + 1})");
                    break;
                }
                if (i > 0 && Math.Abs(s.Hits[i].Frame - s.Hits[i - 1].Frame - HyperBeam.StreamGap * 2) > 1)
                {
                    errors.Add($"{who}: hits {i - 1}->{i} {s.Hits[i].Frame - s.Hits[i - 1].Frame} frames apart, expected {HyperBeam.StreamGap * 2} +-1");
                    break;
                }
            }
            if (s.StreamShots != count) errors.Add($"{who}: {s.StreamShots} stream shots, expected {count}");
            if (s.Streaming) errors.Add($"{who}: still streaming {s.Hits.Count} hits later");
        }

        private static void Stream(List<string> errors)
        {
            if (HyperBeam.WeaponMP.ChargedHoming != 0 || HyperBeam.StreamMP.ChargedHoming != 0) errors.Add("stream: the stream homes");
            int full = StreamShotsFor(HyperBeam.FullCharge * 2);
            if (full != 75) errors.Add($"stream: a full charge buys {full} shots, expected 75 (2.5 s at 30 a second)");
            // the most shots any 80 frames can hold (a shot flies at most its PSLT; streams only ever come further apart)
            int alive = 1 + BeamProjectileEntity.HyperWindLifeMax / (HyperBeam.StreamGap * 2);
            if (alive > HyperBeam.StreamPool) errors.Add($"stream: up to {alive} shots alive, only {HyperBeam.StreamPool} stream slots");
            foreach (bool dark in new[] { false, true })
            {
                using CampaignHost host = Start(dark, null, out PlayerEntity me, out PlayerEntity other);
                FaceClear(host, me, 4);
                me.HostPickUpWeapon(dark ? ItemType.AffinityWeapon : ItemType.HyperBeam);
                me.HostRefillUa();
                PlayerEntity.HostOwnHyperBeamSfx = true; // (shots are counted only when the app sounds them)
                // Dark Samus presses and holds fire again 10 frames into the stream, through it
                Shots s = Fire(host, me, other, hold: 170, again: dark ? 10 : -1);
                PlayerEntity.HostOwnHyperBeamSfx = false;
                string who = dark ? "stream: Dark Samus" : "stream: Samus";
                Console.WriteLine($"[{who[8..]}] the press dealt {s.Press}, charge {s.Charge}, then {s.Hits.Count} hits, {s.StreamShots} stream shots,"
                    + $" spent {s.Spent} UA, {s.Sounds} shot sound(s) (last charged {s.LastCharged}), charge held through it {s.HeldCharge}; hits {Hits(s)}");
                if (s.Press != 8) errors.Add($"{who}: the press dealt {s.Press}, expected 8");
                if (s.Charge != HyperBeam.FullCharge * 2) errors.Add($"{who}: charge {s.Charge}, expected {HyperBeam.FullCharge * 2}");
                CheckStream(errors, who, s, full, dark ? 4 : 3);
                if (s.Spent != 4 + 25) errors.Add($"{who}: spent {s.Spent} UA, expected 29 (the press 4, the stream 25)");
                if (s.Sounds != 2 || !s.LastCharged) errors.Add($"{who}: {s.Sounds} shot sounds (last charged {s.LastCharged}), expected 2 (the press, the release)");
                if (s.HeldCharge != 0) errors.Add($"{who}: fire held through the stream charged to {s.HeldCharge}");
            }
        }

        // a full stream let go with nobody ahead: every charged shot's flight
        private static void Curl(List<string> errors)
        {
            if (BeamProjectileEntity.HyperReleaseSeed(0, 0, 0) == BeamProjectileEntity.HyperReleaseSeed(0, 1, 0)
                || BeamProjectileEntity.HyperReleaseSeed(0, 0, 0) == BeamProjectileEntity.HyperReleaseSeed(0, 0, 1)
                || BeamProjectileEntity.HyperReleaseSeed(0, 0, 0) == BeamProjectileEntity.HyperReleaseSeed(1, 0, 0))
            {
                errors.Add("curl: two seats, lives or releases seed the same");
            }
            if (BeamProjectileEntity.HyperStreamSeed(7, 3) == BeamProjectileEntity.HyperStreamSeed(7, 4))
            {
                errors.Add("curl: two stream shots seed the same");
            }
            float[] y = BeamProjectileEntity.HyperWindKeysY, z = BeamProjectileEntity.HyperWindKeysZ;
            if (y.Length != 101 || z.Length != 101 || y[100] != 10 || y[20] != 6 || z[0] != 0.5f || z[7] != -0.538f || z[10] != 0)
            {
                errors.Add("curl: the WIND keys aren't the WPSC's");
            }
            if (BeamProjectileEntity.HyperWindKey(y, 10.5f) != (3f + 3.449f) / 2) errors.Add("curl: KEYP doesn't lerp");
            using CampaignHost host = Start(dark: false, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 20);
            me.HostPickUpWeapon(ItemType.HyperBeam);
            me.HostRefillUa();
            var shots = new Dictionary<BeamProjectileEntity, (Vector3 Start, Vector3 Line, int Frames, float Off, bool Wind)>();
            var done = new List<(int Frames, float Off, bool Hit, bool Wind)>();
            int peak = 0, full = 0; // the stream's shot slots (HyperBeam.StreamPool): most live at once, frames they were full
            for (int f = 0; f < 170 + 260; f++)
            {
                other.Position = me.Position - Flat(me) * 3; // (behind: nothing ahead but the walls)
                other.Health = 150;
                me.Health = 150;
                host.Step(new CampaignInput { Buttons = f < 170 ? CampaignButtons.Shoot : CampaignButtons.None });
                int live = me.EquipInfo.Beams.Skip(PlayerEntity.BeamPool).Count(bm => bm.Lifespan > 0);
                peak = Math.Max(peak, live);
                if (live == HyperBeam.StreamPool) full++;
                var seen = new HashSet<BeamProjectileEntity>();
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is not BeamProjectileEntity b || b.Owner != me || !b.Flags.TestFlag(BeamFlags.Charged) || b.Lifespan <= 0) continue;
                    if (b.Flags.TestFlag(BeamFlags.Collided))
                    {
                        if (shots.TryGetValue(b, out var c)) { done.Add((c.Frames, c.Off, true, c.Wind)); shots.Remove(b); }
                        continue; // (a hit lingers a few frames, still)
                    }
                    seen.Add(b);
                    // (a charged shot sits on the muzzle its first frame, as Corruption's, sim 21: its line is its first move's)
                    if (!shots.ContainsKey(b) && b.HyperWind && b.Position == b.SpawnPosition) continue;
                    if (!shots.TryGetValue(b, out var t)) t = (b.Position - b.Velocity, b.Velocity.Normalized(), 0, 0f, b.HyperWind);
                    Vector3 d = b.Position - t.Start;
                    float off = (d - t.Line * Vector3.Dot(d, t.Line)).Length;
                    shots[b] = (t.Start, t.Line, t.Frames + 1, Math.Max(t.Off, off), t.Wind && b.HyperWind);
                }
                foreach (var b in shots.Keys.Where(b => !seen.Contains(b)).ToList())
                {
                    var t = shots[b];
                    done.Add((t.Frames, t.Off, false, t.Wind));
                    shots.Remove(b);
                }
            }
            int hits = done.Count(t => t.Hit), curled = done.Count(t => t.Off > 1);
            int longest = done.Count == 0 ? 0 : done.Max(t => t.Frames);
            Console.WriteLine($"[curl] {done.Count} charged shots: {curled} off their line > 1 unit, {hits} ended by a hit, longest {longest} frames,"
                + $" off <= {(done.Count == 0 ? 0 : done.Max(t => t.Off)):0.0}; stream slots: {peak} of {HyperBeam.StreamPool} live at most,"
                + $" full {full} frames");
            // full slots hand the next shot the last one (BeamProjectileEntity.ChooseBeamSlot), cutting a live shot short
            if (full > 0) errors.Add($"curl: the stream's slots were full {full} frames (a stream shot cut a live one short)");
            if (done.Count < 75) errors.Add($"curl: {done.Count} charged shots flew, expected the stream's 75");
            if (done.Any(t => !t.Wind)) errors.Add("curl: a charged shot flew straight");
            if (longest > BeamProjectileEntity.HyperWindLifeMax + 1) errors.Add($"curl: a shot flew {longest} frames, past its PSLT");
            if (curled < done.Count / 2) errors.Add($"curl: only {curled} of {done.Count} shots left their line");
            if (hits == 0) errors.Add("curl: no shot hit a wall");
        }

        // owner 11-14 "send the seed with each shot ... i may need you to confirm it works": two copies of one seat (the
        // shooter's device and a client's puppet: the muzzle a step apart, as an online copy's is) fly every charged shot
        // of a release and its stream the same; the seat's next life flies them differently
        private static void Copies(List<string> errors)
        {
            var (a, sa) = Flights(0, 0);
            var (b, sb) = Flights(0.4f, 0);
            var (c, sc) = Flights(0, 1);
            int same = 0, frames = 0;
            float worst = 0;
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                int n = Math.Min(a[i].Count, b[i].Count);
                float off = 0;
                for (int f = 0; f < n; f++) off = Math.Max(off, (a[i][f] - b[i][f]).Length);
                frames += n;
                worst = Math.Max(worst, off);
                if (off < 0.001f) same++;
            }
            int seeds = sa.Zip(sb).Count(p => p.First == p.Second), lifeSame = sa.Zip(sc).Count(p => p.First == p.Second);
            Console.WriteLine($"[copies] {a.Count}/{b.Count} charged shots on two copies a step apart: {seeds} seeded the same, {same} flew"
                + $" within 0.001 over {frames} frames (worst {worst:0.0000}: its own muzzle and aim); the next life: {lifeSame} of {sc.Count} seeded the same");
            if (a.Count < 75 || b.Count != a.Count) errors.Add($"copies: {a.Count} and {b.Count} charged shots, expected 75 each");
            if (seeds != a.Count) errors.Add($"copies: only {seeds} of {a.Count} shots seeded the same");
            if (worst > 0.1f) errors.Add($"copies: a shot flew {worst} off its other copy");
            if (lifeSame > 0) errors.Add($"copies: {lifeSame} shots seeded the same in the next life");
        }

        // one full release and its stream from a fresh scene (the shooter moved sideways by shift, on its life): each charged
        // shot's path from where it was first seen, in spawn order, till it hits or bursts
        private static (List<List<Vector3>>, List<uint>) Flights(float shift, int life)
        {
            using CampaignHost host = Start(dark: false, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 20);
            Vector3 flat = Flat(me);
            me.Position += new Vector3(-flat.Z, 0, flat.X) * shift;
            GameState.Deaths[me.SlotIndex] = life;
            me.HostPickUpWeapon(ItemType.HyperBeam);
            me.HostRefillUa();
            // (beams are pooled: a beam not live last frame is a new shot)
            var paths = new List<List<Vector3>>();
            var seeds = new List<uint>();
            var live = new Dictionary<BeamProjectileEntity, (Vector3 Start, int Index, bool Done)>();
            for (int f = 0; f < 170 + 260; f++)
            {
                other.Position = me.Position - flat * 3;
                other.Health = 150;
                me.Health = 150;
                host.Step(new CampaignInput { Buttons = f < 170 ? CampaignButtons.Shoot : CampaignButtons.None });
                var now = new Dictionary<BeamProjectileEntity, (Vector3 Start, int Index, bool Done)>();
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e is not BeamProjectileEntity bm || bm.Owner != me || !bm.Flags.TestFlag(BeamFlags.Charged) || bm.Lifespan <= 0) continue;
                    if (!live.TryGetValue(bm, out var t))
                    {
                        t = (bm.Position - bm.Velocity, paths.Count, false);
                        paths.Add(new List<Vector3>());
                        seeds.Add(bm.HyperWindSeed);
                    }
                    if (bm.Flags.TestFlag(BeamFlags.Collided)) t.Done = true;
                    if (!t.Done) paths[t.Index].Add(bm.Position - t.Start);
                    now[bm] = t;
                }
                live = now;
            }
            GameState.Deaths[me.SlotIndex] = 0;
            return (paths, seeds);
        }

        private static void Partial(List<string> errors)
        {
            using CampaignHost host = Start(dark: false, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.HyperBeam);
            me.HostRefillUa();
            Shots s = Fire(host, me, other, hold: HyperBeam.MinCharge * 2 + 6, watch: 120);
            int expect = StreamShotsFor(s.Charge);
            Console.WriteLine($"[partial] charge {s.Charge}: {s.Hits.Count} hits, {s.StreamShots} stream shots (expected {expect}), spent {s.Spent} UA; hits {Hits(s)}");
            if (s.Charge < HyperBeam.MinCharge * 2) errors.Add($"partial: charge {s.Charge} is below MinCharge");
            if (expect < 2 || expect > 30) errors.Add($"partial: a release at {s.Charge} buys {expect} shots");
            CheckStream(errors, "partial", s, expect, 3);
            if (s.Spent - 4 < 10 || s.Spent - 4 > 12) errors.Add($"partial: the stream cost {s.Spent - 4} UA, expected 10-12");
        }

        // from exactly a press and a full stream's UA: the stream runs to its end, then vanilla's empty switch
        private static void Empty(List<string> errors)
        {
            using CampaignHost host = Start(dark: true, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.AffinityWeapon);
            me.HostRefillUa();
            me.EquipInfo.Ammo = 4 + 25;
            Shots s = Fire(host, me, other, hold: 170);
            int ua = me.HostAmmo(0);
            Console.WriteLine($"[empty] from 29 UA: charge {s.Charge}, {s.Hits.Count} hits, {s.StreamShots} stream shots, {ua} UA left, now holds {me.CurrentWeapon}");
            CheckStream(errors, "empty", s, StreamShotsFor(HyperBeam.FullCharge * 2), 4);
            if (ua != 0 || me.CurrentWeapon == BeamType.HyperBeam) errors.Add($"empty: {ua} UA left, holds {me.CurrentWeapon}");
        }

        private static void Bots(List<string> errors)
        {
            var s = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, HyperBeam = null };
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" });
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Character = Characters.DarkSamus.Id, Bot = true, BotLevel = 2 });
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true, BotLevel = 2 });
            // (the game's RNG runs on from match to match, as vanilla's; from the start value the bots' weapon picks are the
            // same whichever tests ran first)
            Rng.SetRng1(Rng.Rng1StartValue);
            Rng.SetRng2(Rng.Rng2StartValue);
            CampaignHost host = CampaignHost.StartMatch(s);
            PlayerEntity.HostOwnHyperBeamSfx = true; // (shots are counted only when the app sounds them)
            PlayerEntity dark = PlayerEntity.Players[1];
            PlayerEntity samus = PlayerEntity.Players[2];
            int darkHeld = 0, samusHeld = 0, charged = 0, lastShots = 0, streams = 0, streamShots = 0, lastStream = 0;
            for (int f = 0; f < 60 * 60; f++)
            {
                if (f % 600 == 300)
                {
                    dark.HostPickUpWeapon(ItemType.AffinityWeapon); // (and UA)
                    samus.HostPickUpWeapon(ItemType.HyperBeam);
                }
                foreach (PlayerEntity p in new[] { PlayerEntity.Players[0], dark, samus })
                {
                    if (p.Health > 0) p.Health = 150;
                }
                host.Step(default);
                if (dark.CurrentWeapon == BeamType.HyperBeam) darkHeld++;
                if (samus.CurrentWeapon == BeamType.HyperBeam) samusHeld++;
                if (samus.HostHyperBeamShots != lastShots && samus.HostHyperBeamLastCharged) charged++;
                lastShots = samus.HostHyperBeamShots;
                int ss = dark.HostHyperBeamStreamShots; // (1 at each release, then up by one a shot)
                if (ss < lastStream || lastStream == 0 && ss > 0) streams++;
                streamShots += ss >= lastStream ? ss - lastStream : ss;
                lastStream = ss;
            }
            PlayerEntity.HostOwnHyperBeamSfx = false;
            Console.WriteLine($"  bots: Dark Samus held it {darkHeld} f, {dark.HostHyperBeamShots} shots, {streams} streams of {streamShots} shots; "
                + $"Samus held it {samusHeld} f, {samus.HostHyperBeamShots} shots ({charged} charged)");
            if (streams == 0) errors.Add("bots: the Dark Samus bot never fired a stream");
            // (a Samus bot reaches it only from the vanilla states that pick weapon 1, like the blaster and the Paralyzer:
            // reported, not checked)
        }

        private static void Replay(List<string> errors)
        {
            bool saved = MphRecomp.Config.RecompSettings.PublicBuild;
            try
            {
                foreach ((bool pub, bool? set, bool expect, string label) in new (bool, bool?, bool, string)[]
                    { (false, null, true, "dev build"), (true, null, false, "public build"), (false, false, false, "served match") })
                {
                    MphRecomp.Config.RecompSettings.PublicBuild = pub;
                    var file = new MphRecomp.Replay.ReplayFile { Settings = new MatchSettings { HyperBeam = set } };
                    using var ms = new System.IO.MemoryStream();
                    file.Write(ms);
                    ms.Position = 0;
                    bool? recorded = MphRecomp.Replay.ReplayFile.Read(ms).Settings.HyperBeam;
                    Console.WriteLine($"[replay] {label}: records {recorded}");
                    if (recorded != expect) errors.Add($"replay: {label} recorded {recorded}, expected {expect}");
                }
            }
            finally
            {
                MphRecomp.Config.RecompSettings.PublicBuild = saved;
            }
        }
    }
}
