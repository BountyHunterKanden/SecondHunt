using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    //   press:   one press: one bolt, 14 damage, 6 UA
    //   charged: Samus's full charge: 40 damage, 25 UA, no homing (owner 2026-10-24, option a: flies straight)
    //   rapid:   Dark Samus holding a full charge: 8 damage every 8 frames (RapidGap 4 DS frames), 4 UA a shot; letting go
    //            ends it with no charged shot; held to empty it stops below one press's UA (vanilla's weapon switch)
    //   replay:  format 60 records the Hyper Beam as played (on in a dev build, off in a public one or a served match)
    //   bots:    a Dark Samus bot given it rapid-fires at players (a Samus bot's use of it is reported)
    internal static class HyperBeamTest
    {
        public static void Run(string[] args)
        {
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction()) return;
            var errors = new List<string>();
            Setup(errors);
            Press(errors);
            Charged(errors);
            Rapid(errors);
            Empty(errors);
            Replay(errors);
            Bots(errors);
            foreach (string e in errors) Console.WriteLine("  FAIL " + e);
            Console.WriteLine(errors.Count == 0 ? "PASS hyperbeamtest" : $"FAIL hyperbeamtest ({errors.Count})");
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
                if (dealt != 14 || spent != 6) errors.Add($"press: {who} dealt {dealt} / spent {spent}, expected 14 / 6");
            }
        }

        private static void Charged(List<string> errors)
        {
            if (HyperBeam.WeaponMP.ChargedHoming != 0) errors.Add("charged: the charged shot homes");
            using CampaignHost host = Start(dark: false, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.HyperBeam);
            me.HostRefillUa();
            int ua = me.HostAmmo(0), press = 0, charged = 0, hits = 0;
            for (int f = 0; f < 64; f++) press += Step(host, me, other, shoot: true);
            int charge = me.EquipInfo.ChargeLevel;
            for (int f = 0; f < 30; f++)
            {
                int d = Step(host, me, other, shoot: false);
                if (d > 0) hits++;
                charged += d;
            }
            int spent = ua - me.HostAmmo(0);
            Console.WriteLine($"[charged] Samus: the press dealt {press}, charge {charge}, then dealt {charged} in {hits} hit(s), spent {spent} UA");
            if (press != 14 || charged != 40 || hits != 1 || spent != 6 + 25)
            {
                errors.Add($"charged: press {press}, charged {charged} in {hits}, spent {spent}; expected 14, 40 in 1, 31");
            }
        }

        private static void Rapid(List<string> errors)
        {
            using CampaignHost host = Start(dark: true, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.AffinityWeapon);
            me.HostRefillUa();
            int ua = me.HostAmmo(0);
            var hits = new List<(int Frame, int Damage)>();
            int firstRapid = -1;
            const int held = 64 + 120;
            for (int f = 0; f < held; f++)
            {
                int d = Step(host, me, other, shoot: true);
                if (d > 0) hits.Add((f, d));
                if (firstRapid < 0 && me.HyperBeamRapid) firstRapid = f;
            }
            int shots = me.HostHyperBeamRapidShots;
            int spent = ua - me.HostAmmo(0);
            int after = 0;
            bool rapidAfter = false;
            for (int f = 0; f < 40; f++)
            {
                int d = Step(host, me, other, shoot: false);
                if (f >= 4) after += d; // (a rapid shot already in the air may land in the first frames)
                rapidAfter |= me.HyperBeamRapid;
            }
            Console.WriteLine($"[rapid] held {held} frames: rapid from frame {firstRapid}, {shots} rapid shots, spent {spent} UA,"
                + $" hits {string.Join(" ", hits.ConvertAll(h => $"{h.Frame}:{h.Damage}"))}; after letting go dealt {after}, still rapid {rapidAfter}");
            if (hits.Count < 2 || hits[0].Damage != 14) errors.Add("rapid: the press didn't land its 14 bolt first");
            var rapid = hits.GetRange(1, Math.Max(0, hits.Count - 1));
            if (rapid.Count < 10) errors.Add($"rapid: only {rapid.Count} rapid hits");
            for (int i = 0; i < rapid.Count; i++)
            {
                if (rapid[i].Damage != 8) { errors.Add($"rapid: hit {i} dealt {rapid[i].Damage}, expected 8"); break; }
                if (i > 0 && rapid[i].Frame - rapid[i - 1].Frame != HyperBeam.RapidGap * 2)
                {
                    errors.Add($"rapid: hits {i - 1}->{i} {rapid[i].Frame - rapid[i - 1].Frame} frames apart, expected {HyperBeam.RapidGap * 2}");
                    break;
                }
            }
            if (spent != 6 + shots * HyperBeam.RapidCost) errors.Add($"rapid: spent {spent} UA for the press + {shots} shots, expected {6 + shots * HyperBeam.RapidCost}");
            if (after != 0 || rapidAfter) errors.Add($"rapid: letting go still dealt {after} (rapid {rapidAfter})");
        }

        // held to empty from a full pool: rapid fire stops with less than one shot's UA left, and the charge is let go
        private static void Empty(List<string> errors)
        {
            using CampaignHost host = Start(dark: true, null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.AffinityWeapon);
            me.HostRefillUa();
            int f = 0, lastShot = 0, shots = 0;
            for (; f < 3000; f++)
            {
                Step(host, me, other, shoot: true);
                if (me.HostHyperBeamRapidShots != shots) { shots = me.HostHyperBeamRapidShots; lastShot = f; }
                if (f - lastShot > 60 && shots > 0) break;
            }
            int ua = me.HostAmmo(0);
            Console.WriteLine($"[empty] held from full: {shots} rapid shots, stopped at frame {lastShot} with {ua} UA, rapid {me.HyperBeamRapid}, now holds {me.CurrentWeapon}");
            // (vanilla switches off a weapon with less than one press's UA: 6, so the last 4-5 UA aren't fired)
            if (shots == 0 || ua >= HyperBeam.WeaponMPAffinity.AmmoCost || me.HyperBeamRapid) errors.Add($"empty: {shots} shots, {ua} UA left, rapid {me.HyperBeamRapid}");
        }

        private static void Bots(List<string> errors)
        {
            var s = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, HyperBeam = null };
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" });
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Character = Characters.DarkSamus.Id, Bot = true, BotLevel = 2 });
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Bot = true, BotLevel = 2 });
            CampaignHost host = CampaignHost.StartMatch(s);
            PlayerEntity.HostOwnHyperBeamSfx = true; // (shots are counted only when the app sounds them)
            PlayerEntity dark = PlayerEntity.Players[1];
            PlayerEntity samus = PlayerEntity.Players[2];
            int darkHeld = 0, samusHeld = 0, charged = 0, lastShots = 0;
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
            }
            PlayerEntity.HostOwnHyperBeamSfx = false;
            Console.WriteLine($"  bots: Dark Samus held it {darkHeld} f, {dark.HostHyperBeamShots} shots + {dark.HostHyperBeamRapidShots} rapid; "
                + $"Samus held it {samusHeld} f, {samus.HostHyperBeamShots} shots ({charged} charged)");
            if (dark.HostHyperBeamRapidShots == 0) errors.Add("bots: the Dark Samus bot never rapid-fired");
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
