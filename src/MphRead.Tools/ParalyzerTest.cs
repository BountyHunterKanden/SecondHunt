using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -paralyzertest: Zero Suit Samus's Paralyzer (MphRead Metadata/Paralyzer.cs + PlayerParalyzer.cs, docs/ZERO_SUIT_SAMUS.md
    // section 7), in real matches on Combat Hall.
    //   setup:  her seat's affinity weapon is the Paralyzer and she has 89/179 health; Samus 99/199; Settings.Paralyzer =
    //           false (replays before format 33) gives her neither
    //   rapid:  tapped fire on a target 3 units ahead for 20 s: one bolt a press (owner 10-15), 15 every 20 frames vs the
    //           Power Beam's 6 every 10, so about 1.25x its damage
    //   charge: her full charge hits for 30 and stuns 90 frames; a charged shot inside the 2 s immunity doesn't stun again,
    //           one after it does; a charged shot misses at 4 units (2 units reach from the muzzle, owner 10-15); nobody else can charge it (owner 10-15)
    //   stun:   her own seat stunned: no walking, jumping or morphing; turning and shooting still work; walking after
    internal static class ParalyzerTest
    {
        public static void Run(string[] args)
        {
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as ReplayTest)
            if (!ToolPaths.UseDesktopExtraction()) return;
            var errors = new List<string>();
            Setup(errors);
            Rapid(errors);
            Charge(errors);
            Buffered(errors);
            Others(errors);
            Stun(errors);
            if (Array.IndexOf(args, "bots") >= 0) Bots(errors);
            foreach (string e in errors) Console.WriteLine("  FAIL " + e);
            Console.WriteLine(errors.Count == 0 ? "PASS paralyzertest" : $"FAIL paralyzertest ({errors.Count})");
        }

        private static MatchSettings Settings(bool zeroSuit, bool? paralyzer)
        {
            var s = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, Paralyzer = paralyzer };
            s.Players.Add(zeroSuit
                ? new MatchPlayer { Hunter = Characters.ZeroSuit.Base, Character = Characters.ZeroSuit.Id, Nickname = "Player1" }
                : new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1" });
            s.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, BotLevel = 0 });
            return s;
        }

        private static CampaignHost Start(bool zeroSuit, bool? paralyzer, out PlayerEntity me, out PlayerEntity other)
        {
            CampaignHost host = CampaignHost.StartMatch(Settings(zeroSuit, paralyzer));
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

        private static void Place(PlayerEntity me, PlayerEntity other, float distance)
        {
            other.Position = me.Position + Flat(me) * distance;
            other.Speed = Vector3.Zero;
        }

        private static void Setup(List<string> errors)
        {
            MatchSettings on = Settings(zeroSuit: true, paralyzer: null);
            MatchSettings off = Settings(zeroSuit: true, paralyzer: false);
            if (Characters.AffinityBeamFor(on, on.Players[0]) != BeamType.Paralyzer) errors.Add("setup: Zero Suit's affinity isn't the Paralyzer");
            if (Characters.AffinityBeamFor(off, off.Players[0]) != null) errors.Add("setup: Paralyzer = false still gives her the Paralyzer");
            if (Characters.AffinityBeamFor(on, on.Players[1]) != null) errors.Add("setup: Kanden got an app affinity weapon");
            if (Characters.HealthPercentFor(on, on.Players[0]) != 90 || Characters.HealthPercentFor(off, off.Players[0]) != 100
                || Characters.HealthPercentFor(on, on.Players[1]) != 100)
            {
                errors.Add("setup: health percents wrong");
            }
            foreach ((bool zs, bool? par, int hp, int max, string label) in new (bool, bool?, int, int, string)[]
                { (true, null, 89, 179, "Zero Suit"), (true, false, 99, 199, "Zero Suit, old replay"), (false, null, 99, 199, "Samus") })
            {
                using CampaignHost host = Start(zs, par, out PlayerEntity me, out _);
                bool affinity = me.AffinityBeam == BeamType.Paralyzer;
                me.HostPickUpWeapon(ItemType.AffinityWeapon);
                Console.WriteLine($"[setup] {label}: health {me.Health}/{me.HealthMax}, affinity item gives {me.CurrentWeapon}");
                if (me.Health != hp || me.HealthMax != max) errors.Add($"setup: {label} health {me.Health}/{me.HealthMax}, expected {hp}/{max}");
                if (affinity != (zs && par != false) || (me.CurrentWeapon == BeamType.Paralyzer) != (zs && par != false))
                {
                    errors.Add($"setup: {label} affinity pickup gave {me.CurrentWeapon}");
                }
            }
        }

        // damage dealt to a target held `distance` ahead while fire is tapped every other frame
        private static int Fire(CampaignHost host, PlayerEntity me, PlayerEntity other, int frames, float distance = 3)
        {
            int dealt = 0;
            for (int f = 0; f < frames; f++)
            {
                Place(me, other, distance);
                other.Health = 150;
                me.Health = 150; // (the bot shoots back)
                me.HostRefillUa();
                host.Step(new CampaignInput { Buttons = f % 2 == 0 ? CampaignButtons.Shoot : CampaignButtons.None });
                dealt += 150 - other.Health;
                if (Log != null && 150 - other.Health > 0) Log.Add($"{f}:{150 - other.Health}");
            }
            return dealt;
        }

        private static List<string>? Log;

        private static void Rapid(List<string> errors)
        {
            if (Environment.GetEnvironmentVariable("PARALYZER_LOG") == "1") Log = new List<string>();
            const int frames = 1200;
            int power, paralyzer;
            using (CampaignHost host = Start(zeroSuit: true, paralyzer: null, out PlayerEntity me, out PlayerEntity other))
            {
                FaceClear(host, me, 4);
                power = Fire(host, me, other, frames);
            }
            if (Log != null) { Console.WriteLine("[rapid] PB hits " + string.Join(" ", Log)); Log.Clear(); }
            using (CampaignHost host = Start(zeroSuit: true, paralyzer: null, out PlayerEntity me, out PlayerEntity other))
            {
                FaceClear(host, me, 4);
                me.HostPickUpWeapon(ItemType.AffinityWeapon);
                paralyzer = Fire(host, me, other, frames);
                if (Log != null) Console.WriteLine("[rapid] Par hits " + string.Join(" ", Log));
                if (me.CurrentWeapon != BeamType.Paralyzer) errors.Add($"rapid: held {me.CurrentWeapon}");
            }
            float ratio = power == 0 ? 0 : paralyzer / (float)power;
            Console.WriteLine($"[rapid] {frames} frames tapped at 3 units: Power Beam {power}, Paralyzer {paralyzer} (x{ratio:0.000})");
            if (ratio < 1.15f || ratio > 1.35f) errors.Add($"rapid: Paralyzer/Power Beam = {ratio:0.000}, expected about 1.25");
        }

        // one charged shot: held 64 frames, let go, the target kept `distance` ahead until it lands (or 30 frames)
        private static (int Dealt, bool Stunned) Charged(CampaignHost host, PlayerEntity me, PlayerEntity other, float distance)
        {
            int stunsBefore = other.HostParalyzerStuns;
            HoldTapped = 0;
            for (int f = 0; f < 64; f++)
            {
                Place(me, other, distance);
                other.Health = 150;
                me.Health = 150; // (the bot shoots back, harder up close)
                me.HostRefillUa();
                host.Step(new CampaignInput { Buttons = CampaignButtons.Shoot });
                HoldTapped += 150 - other.Health;
            }
            int dealt = 0;
            for (int f = 0; f < 30 && dealt == 0; f++)
            {
                if (f == 0 || !other.ParalyzerStunned) Place(me, other, distance);
                other.Health = 150;
                host.Step(default);
                dealt = 150 - other.Health;
            }
            return (dealt, other.HostParalyzerStuns > stunsBefore);
        }

        // damage the press itself did before the charge (owner 10-15: "fire one shot before charging like the rest")
        private static int HoldTapped;

        private static void Wait(CampaignHost host, PlayerEntity other, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                other.Health = 150;
                PlayerEntity.Players[0].Health = 150; // (the bot shoots back)
                host.Step(default);
            }
        }

        private static void Charge(List<string> errors)
        {
            using CampaignHost host = Start(zeroSuit: true, paralyzer: null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 12);
            me.HostPickUpWeapon(ItemType.AffinityWeapon);
            (int dealt, bool stunned) = Charged(host, me, other, 1.5f);
            int timer = other.HostParalyzedTimer;
            Vector3 at = other.Position;
            Wait(host, other, 40);
            float drift = (other.Position.Xz - at.Xz).Length;
            Console.WriteLine($"[charge] at 1.5 units: the press dealt {HoldTapped}, then the charge dealt {dealt}, stunned {stunned} ({timer} frames left), moved {drift:0.000} in 40 frames");
            if (HoldTapped != 15) errors.Add($"charge: the press before the charge dealt {HoldTapped}, expected one bolt (15)");
            if (dealt != 30 || !stunned || timer < Paralyzer.StunFrames - 4) errors.Add($"charge: first shot dealt {dealt}, stunned {stunned}, timer {timer}");
            if (drift > 0.05f) errors.Add($"charge: the stunned bot moved {drift:0.000}");
            for (int f = 0; f < 200 && other.ParalyzerStunned; f++) Wait(host, other, 1);
            // straight away: inside the 2 s immunity (the charge itself takes 64 frames)
            (dealt, stunned) = Charged(host, me, other, 1.5f);
            Console.WriteLine($"[charge] right after the stun: dealt {dealt}, stunned {stunned}");
            if (dealt != 30 || stunned) errors.Add($"charge: inside the immunity dealt {dealt}, stunned {stunned}");
            Wait(host, other, Paralyzer.ImmunityFrames);
            (dealt, stunned) = Charged(host, me, other, 1.5f);
            Console.WriteLine($"[charge] after the immunity: dealt {dealt}, stunned {stunned}");
            if (dealt != 30 || !stunned) errors.Add($"charge: after the immunity dealt {dealt}, stunned {stunned}");
            for (int f = 0; f < 200 && other.ParalyzerStunned; f++) Wait(host, other, 1);
            Wait(host, other, Paralyzer.ImmunityFrames + 4);
            (dealt, stunned) = Charged(host, me, other, 4);
            Console.WriteLine($"[charge] at 4 units: dealt {dealt}, stunned {stunned}");
            if (dealt != 0) errors.Add($"charge: a charged shot reached 4 units ({dealt})");
            (dealt, stunned) = Charged(host, me, other, 2);
            Console.WriteLine($"[charge] at 2 units: dealt {dealt}, stunned {stunned}");
            if (dealt != 30 || !stunned) errors.Add($"charge: at 2 units dealt {dealt}, stunned {stunned}");
        }

        // a second press inside the 1/3 s cooldown, then held: its bolt still goes out (when the cooldown ends), before the
        // charge (owner 10-15: "fire one shot before charging like the rest of the beams")
        private static void Buffered(List<string> errors)
        {
            using CampaignHost host = Start(zeroSuit: true, paralyzer: null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.AffinityWeapon);
            int dealt = 0;
            for (int f = 0; f < 40; f++)
            {
                Place(me, other, 3);
                other.Health = 150;
                me.Health = 150;
                me.HostRefillUa();
                host.Step(new CampaignInput { Buttons = f == 0 || f >= 6 ? CampaignButtons.Shoot : CampaignButtons.None });
                dealt += 150 - other.Health;
            }
            Console.WriteLine($"[buffered] a press, another 6 frames later held: dealt {dealt}, charge {me.EquipInfo.ChargeLevel}");
            if (dealt != 30) errors.Add($"buffered: two presses dealt {dealt}, expected two bolts (30)");
        }

        private static void Others(List<string> errors)
        {
            using CampaignHost host = Start(zeroSuit: false, paralyzer: null, out PlayerEntity me, out PlayerEntity other);
            FaceClear(host, me, 4);
            me.HostPickUpWeapon(ItemType.Paralyzer);
            int charge = 0, tapped = 0;
            for (int f = 0; f < 64; f++)
            {
                Place(me, other, 3);
                other.Health = 150;
                me.HostRefillUa();
                host.Step(new CampaignInput { Buttons = CampaignButtons.Shoot });
                charge = Math.Max(charge, me.EquipInfo.ChargeLevel);
                tapped += 150 - other.Health;
            }
            Console.WriteLine($"[others] Samus's press: dealt {tapped}");
            if (tapped != 15) errors.Add($"others: a press dealt {tapped}, expected one bolt (15)");
            (int dealt, bool stunned) = Charged(host, me, other, 3);
            Console.WriteLine($"[others] Samus holding fire with the Paralyzer: holds {me.CurrentWeapon}, charge {charge}, then dealt {dealt}, stunned {stunned}");
            if (me.CurrentWeapon != BeamType.Paralyzer || charge > 0 || dealt == 30 || stunned) errors.Add($"others: charge {charge}, dealt {dealt}, stunned {stunned}");
        }

        // -paralyzertest bots: four Zero Suit bots for three minutes, each handed the Paralyzer now and then (no crash, they
        // fire it; stuns and kills reported)
        private static void Bots(List<string> errors)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, TimeLimitSeconds = 600 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" }); // (seat 0 is local: stands)
            for (int i = 1; i < 4; i++)
            {
                settings.Players.Add(new MatchPlayer { Hunter = Characters.ZeroSuit.Base, Character = Characters.ZeroSuit.Id, Bot = true, BotLevel = 2 });
            }
            using CampaignHost host = CampaignHost.StartMatch(settings);
            int held = 0, frames = 0, charging = 0, near = 0, released = 0;
            int[] lastLevel = new int[4];
            int chance = 0, chanceCharging = 0;
            for (int f = 0; f < 10800 && !host.Ended; f++)
            {
                if (f % 600 == 300)
                {
                    for (int i = 1; i < 4; i++) if (PlayerEntity.Players[i].Health > 0) PlayerEntity.Players[i].HostPickUpWeapon(ItemType.AffinityWeapon);
                }
                host.Step(default);
                for (int i = 1; i < 4; i++)
                {
                    if (PlayerEntity.Players[i].Health > 0)
                    {
                        frames++;
                        if (PlayerEntity.Players[i].CurrentWeapon == BeamType.Paralyzer)
                        {
                            held++;
                            int level = PlayerEntity.Players[i].EquipInfo.ChargeLevel;
                            if (level > 0) charging++;
                            if (lastLevel[i] >= 60 && level == 0) released++;
                            lastLevel[i] = level;
                            if (PlayerEntity.Players[i].AiData.HostTargetPlayer is PlayerEntity t && t.Health > 0 && t.ParalyzerStunnable
                                && (t.Position - PlayerEntity.Players[i].Position).Length < 10)
                            {
                                chance++;
                                if (level > 0) chanceCharging++;
                            }
                            for (int j = 0; j < 4; j++)
                            {
                                if (j != i && PlayerEntity.Players[j].Health > 0
                                    && (PlayerEntity.Players[j].Position - PlayerEntity.Players[i].Position).Length < 10)
                                {
                                    near++;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            int stuns = 0;
            for (int i = 0; i < 4; i++) stuns += PlayerEntity.Players[i].HostParalyzerStuns;
            Console.WriteLine($"[bots] 3 min: Paralyzer held {100f * held / Math.Max(1, frames):0}% of alive frames"
                + $" (charging {100f * charging / Math.Max(1, held):0}%, someone within 10 units {100f * near / Math.Max(1, held):0}%), target within 10 {chance} frames (charging {chanceCharging}), charged shots {released}, stuns {stuns},"
                + $" kills {string.Join(",", GameState.Points)}");
            if (held == 0) errors.Add("bots: no bot ever held the Paralyzer");
            if (stuns == 0) errors.Add("bots: no Zero Suit bot stunned anyone");
        }

        private static void Stun(List<string> errors)
        {
            using CampaignHost host = Start(zeroSuit: true, paralyzer: null, out PlayerEntity me, out PlayerEntity other);
            other.Position = me.Position + new Vector3(0, 30, 0); // (out of the way)
            me.HostPickUpWeapon(ItemType.AffinityWeapon);
            me.NetApplyParalyzed(Paralyzer.StunFrames, exact: true);
            Vector3 at = me.Position;
            float yaw = MathF.Atan2(me.FacingVector.X, me.FacingVector.Z);
            int ua = me.HostAmmo(0);
            for (int f = 0; f < 30; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, 1), AimDelta = new System.Numerics.Vector2(3, 0),
                    Buttons = f % 2 == 0 ? CampaignButtons.Shoot : CampaignButtons.None });
            }
            float moved = (me.Position.Xz - at.Xz).Length;
            float turned = MathF.Abs(MathF.Atan2(me.FacingVector.X, me.FacingVector.Z) - yaw);
            int shots = ua - me.HostAmmo(0);
            host.Step(new CampaignInput { Buttons = CampaignButtons.Jump });
            float y = me.Position.Y;
            for (int f = 0; f < 10; f++) host.Step(default);
            float rose = me.Position.Y - y;
            host.Step(new CampaignInput { Buttons = CampaignButtons.Morph });
            for (int f = 0; f < 10; f++) host.Step(default);
            bool morphed = me.IsAltForm || me.IsMorphing;
            Console.WriteLine($"[stun] stunned: walked {moved:0.000}, turned {turned:0.00} rad, shots {shots}, jump rose {rose:0.000},"
                + $" morphed {morphed}, {me.HostParalyzedTimer} frames left");
            if (moved > 0.05f) errors.Add($"stun: walked {moved:0.000} while stunned");
            if (turned < 0.05f) errors.Add("stun: couldn't turn while stunned");
            if (shots < 1) errors.Add($"stun: no shots while stunned"); // (one bolt a press, 1/3 s apart: owner 10-15)
            if (rose > 0.05f) errors.Add($"stun: jumped {rose:0.000} while stunned");
            if (morphed) errors.Add("stun: morphed while stunned");
            for (int f = 0; f < 200 && me.ParalyzerStunned; f++) host.Step(default);
            at = me.Position;
            for (int f = 0; f < 30; f++) host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, 1) });
            moved = (me.Position.Xz - at.Xz).Length;
            Console.WriteLine($"[stun] after: walked {moved:0.000} in 30 frames");
            if (moved < 0.3f) errors.Add($"stun: walked only {moved:0.000} after the stun");
        }
    }
}
