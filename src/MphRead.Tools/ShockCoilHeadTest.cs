using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Shock Coil hits in the head zone (a user report 2026-10-16: "locking onto headshots and killing almost instantly"),
    // headless:
    //   -shockcoilheadtest [arena=Combat Hall] [contheads=0] [grace=0]   (0: HostContinuousHeadshots / HostShockCoilGrace off,
    //   replays before format 38 / 40)
    // The game scales a continuous beam's damage AND headshot damage by 1/32 per tick (rev 1 arm9 0x2041388-0x2041404), so
    // a Shock Coil hit in the head does what a body hit does and is never flagged a headshot; MphRead left the headshot
    // damage whole (10, +4 ramp) every 60 Hz frame. Seat 0 is a local Samus no input reaches after she spawns (stands
    // still), seat 1 a Sylux seat that owns the test's Shock Coil beams, one spawned every frame for 240 frames (4 s):
    //   - body: from 4 to her side at her middle's height (the line enters her cylinder's side: not the head zone)
    //   - head: from 4 over her head and 1.5 to the side (the line enters through the top: the head zone)
    // PASS with the fix: both cases hurt, the head case no more than 1.5x the body case and never flagged a headshot.
    // contheads=0 expects the old way (head flagged, far more damage) to show the test sees the difference.
    // The ramp's grace (rev 1 arm9 0x200FE04: dropped after 2 ticks without a hit): 120 frames on her, then no shot for 2
    // frames (one tick) keeps the ramp, for 4 frames (two ticks) drops it. grace=0 expects MphRead's old drop after 2 frames.
    internal static class ShockCoilHeadTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            bool contHeads = true;
            bool grace = true;
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaName = value; break;
                case "contheads": contHeads = value != "0"; break;
                case "grace": grace = value != "0"; break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as StandingBodyHitTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = 300 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Target" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Sylux, Nickname = "Shooter" });
            CampaignHost host = CampaignHost.StartMatch(settings);
            Console.WriteLine($"Battle in {arena.InGameName}: seat 0 Samus (local, still), seat 1 Sylux (no input); "
                + $"HostContinuousHeadshots {contHeads}, HostShockCoilGrace {grace}");

            var errors = new List<string>();
            void Fail(string what) => errors.Add($"frame {host.Frame}: {what}");
            void Step(CampaignInput input, int frames = 1)
            {
                for (int i = 0; i < frames && !host.Ended; i++)
                {
                    host.Step(input);
                }
            }

            PlayerEntity me = PlayerEntity.Players[0];
            PlayerEntity shooter = PlayerEntity.Players[1];
            shooter.HostContinuousHeadshots = contHeads;
            shooter.HostShockCoilGrace = grace;
            WeaponInfo coil = Weapons.Current.First(w => w.Beam == BeamType.ShockCoil);
            var equip = new EquipInfo
            {
                Weapon = coil,
                InfiniteAmmo = true,
                Beams = SceneSetup.CreateBeamList(8, host.Scene)
            };
            Console.WriteLine($"weapon: {coil.Description}, damage {coil.UnchargedDamage}, headshot {coil.HeadshotDamage}");

            for (int f = 0; f < 600 && me.Health == 0; f++)
            {
                Step(new CampaignInput { Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
            }
            Step(default, 60);
            Console.WriteLine($"spawned: health {me.Health} at {me.Position}, standing {me.Flags1.TestFlag(PlayerFlags1.Standing)}");
            Vector3 face = new Vector3(me.FacingVector.X, 0, me.FacingVector.Z).Normalized();
            Vector3 side = Vector3.Cross(face, Vector3.UnitY).Normalized();

            // a Shock Coil beam every frame for `frames` frames from `offset` off her middle (Position + 0.5, where the coil
            // homes); health topped up first so she can't die; the ramp resets in the gap before (2 frames without a hit)
            // (also the frame her first 100 health was gone: how fast a full-health hunter dies)
            (int Lost, int Hits, int Heads, int Kill) Burn(Vector3 offset, int frames)
            {
                Step(default, 10);
                const int full = 900;
                me.Health = full;
                int lost = 0;
                int hits = 0;
                int heads = 0;
                int kill = -1;
                for (int f = 0; f < frames && !host.Ended; f++)
                {
                    me.GetPosition(out Vector3 middle);
                    Vector3 start = middle + offset;
                    BeamProjectileEntity.Spawn(shooter, equip, start, (middle - start).Normalized(), BeamSpawnFlags.None,
                        me.NodeRef, host.Scene);
                    int before = me.Health;
                    Step(default);
                    if (me.Health < before)
                    {
                        lost += before - me.Health;
                        hits++;
                        if (me.NetLastFlags.TestFlag(DamageFlags.Headshot))
                        {
                            heads++;
                        }
                        if (kill < 0 && lost >= 100)
                        {
                            kill = f + 1;
                        }
                    }
                }
                return (lost, hits, heads, kill);
            }

            const int burnFrames = 240; // 4 s: the coil's ramp adds 1 per tick after each second on one target, up to 4 (rev 1 0x20422a4)
            var body = Burn(side * 4, burnFrames);
            var head = Burn(Vector3.UnitY * 4 + side * 1.5f, burnFrames);
            Console.WriteLine($"body, {burnFrames} frames: {body.Lost} health in {body.Hits} hits, {body.Heads} headshots, 100 gone at frame {body.Kill}");
            Console.WriteLine($"head, {burnFrames} frames: {head.Lost} health in {head.Hits} hits, {head.Heads} headshots, 100 gone at frame {head.Kill}");
            if (body.Lost == 0 || head.Lost == 0)
            {
                Fail("a case did no damage (the coil missed)");
            }
            if (body.Heads > 0)
            {
                Fail("the body case was flagged a headshot");
            }
            if (contHeads)
            {
                if (head.Heads > 0)
                {
                    Fail($"{head.Heads} Shock Coil hits flagged a headshot (vanilla: never)");
                }
                if (head.Lost > body.Lost * 3 / 2)
                {
                    Fail($"head damage {head.Lost} vs body {body.Lost} (vanilla: the same)");
                }
            }
            else if (head.Heads == 0 || head.Lost <= body.Lost * 3)
            {
                Fail($"contheads=0 should show MphRead's old headshots (head {head.Lost} in {head.Heads} headshots, body {body.Lost})");
            }

            // the ramp's timer after 120 frames on her, `gap` frames without a shot, then one more shot
            int AfterGap(int gap)
            {
                Step(default, 10);
                me.Health = 900;
                for (int f = 0; f < 120 + 1 + gap && !host.Ended; f++)
                {
                    if (f < 120 || f == 120 + gap)
                    {
                        me.GetPosition(out Vector3 middle);
                        Vector3 start = middle + side * 4;
                        BeamProjectileEntity.Spawn(shooter, equip, start, (middle - start).Normalized(), BeamSpawnFlags.None,
                            me.NodeRef, host.Scene);
                    }
                    Step(default);
                }
                return shooter.ShockCoilTimer;
            }
            foreach ((int gap, bool keepWithGrace) in new[] { (2, true), (4, false) })
            {
                int timer = AfterGap(gap);
                bool kept = timer >= 100;
                bool expect = grace && keepWithGrace;
                Console.WriteLine($"ramp after a {gap}-frame gap: timer {timer} ({(kept ? "kept" : "dropped")})");
                if (kept != expect)
                {
                    Fail($"{gap}-frame gap: ramp {(kept ? "kept" : "dropped")}, expected {(expect ? "kept" : "dropped")}");
                }
            }

            // a match client's instant hit feedback (PlayerHitClaims.HitClaimBeamHit -> NetPredictHit): a head-zone Shock Coil
            // hit isn't a headshot the host flags, so no HEADSHOT! (owner 2026-10-31: the Fault Line matches showed it); a
            // head hit of a beam with its own head damage (the Power Beam's) still says it
            bool PredictedHead(float damage, float headDamage)
            {
                var beam = new BeamProjectileEntity(host.Scene) { Owner = shooter, Damage = damage, HeadshotDamage = headDamage };
                var list = (System.Collections.IList)typeof(PlayerEntity).GetField("_netPredicted",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(me)!;
                int mainBefore = PlayerEntity.MainPlayerIndex;
                me.Health = 900;
                list.Clear();
                try
                {
                    PlayerEntity.MainPlayerIndex = 1;
                    PlayerEntity.NetClient = true;
                    PlayerEntity.HitClaimBeamHit(beam, me, me.Position, head: true, halfturret: false, damage: 10);
                }
                finally
                {
                    PlayerEntity.NetClient = false;
                    PlayerEntity.MainPlayerIndex = mainBefore;
                }
                bool shown = list.Count > 0 && (bool)((System.Runtime.CompilerServices.ITuple)list[0]!)[2]!;
                list.Clear();
                return shown;
            }
            bool coilHead = PredictedHead(coil.UnchargedDamage, coil.HeadshotDamage);
            bool powerHead = PredictedHead(8, 16);
            Console.WriteLine($"client prediction, head zone: Shock Coil HEADSHOT! {coilHead}, a beam with its own head damage {powerHead}");
            if (contHeads && coilHead)
            {
                Fail("a match client showed HEADSHOT! for a head-zone Shock Coil hit (the host never flags it)");
            }
            if (!powerHead)
            {
                Fail("a match client showed no HEADSHOT! for a real headshot");
            }

            foreach (string e in errors)
            {
                Console.WriteLine("  " + e);
            }
            Console.WriteLine(errors.Count == 0 ? "shockcoilheadtest PASS" : "shockcoilheadtest FAIL");
        }
    }
}
