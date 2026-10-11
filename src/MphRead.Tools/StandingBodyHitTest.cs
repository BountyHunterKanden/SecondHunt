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
    // The Trooper's standing body (his Scout Drone out) is hit as a whole biped, not Weavel's 0.45 turret sphere, and
    // takes no headshots (owner 2026-10-09; MphRead HalfturretEntity.HostBodyHit), headless:
    //   -standinghittest [arena=Combat Hall]
    // seat 0 is a local Trooper driven by a script; seat 1 a Samus seat no input reaches (never moves) and only
    // owns the test's Power Beam shots (6 damage, 8 to the head: a head hit is flagged), each spawned 2.5 to the side
    // of the target and fired straight across it at a set height over the floor.
    //   - control, on his biped before the split: 1.0 hits without a headshot, 1.5 hits as a headshot
    //   - on the standing body after the split (the drone flown away first): 0.3, 0.8, 1.2, 1.45 and 1.55 hit (the old
    //     sphere: only up to ~0.9), never a headshot and always a Halfturret hit; 1.75 (over the 1.6 top) and 1.2 at 0.75
    //     to the side of its middle miss
    internal static class StandingBodyHitTest
    {
        public static void Run(string[] args)
        {
            string arenaName = "Combat Hall";
            foreach (string arg in args.Skip(1))
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arenaName = value; break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear(); // MphRead's Debug.Asserts would end this Debug process (as DroneTest)
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Characters.ScoutDroneOn = true;
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena.Name, TimeLimitSeconds = 300 };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            // not a bot and not remote (a remote seat turns the Trooper's abilities off): a human seat no input reaches
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Shooter" });
            CampaignHost host = CampaignHost.StartMatch(settings);
            Console.WriteLine($"Battle in {arena.InGameName}: seat 0 Trooper (local, scripted), seat 1 Samus (no input, still)");

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
            var equip = new EquipInfo
            {
                Weapon = Weapons.Current[0], // Power Beam
                InfiniteAmmo = true,
                Beams = SceneSetup.CreateBeamList(8, host.Scene)
            };

            // one shot from 2.5 to the side, straight across the target's axis at `height` over `floor`, `offset` off its
            // middle; waits for a hit (health or the standing body's energy drops) or 40 frames
            (bool Hit, DamageFlags Flags, bool OldSphere) Shoot(Vector3 axis, float floor, Vector3 side, float height, float offset)
            {
                Vector3 across = -side;
                Vector3 along = Vector3.Cross(Vector3.UnitY, side); // the shot's line moved off the middle
                var start = new Vector3(axis.X, floor + height, axis.Z) + side * 2.5f + along * offset;
                bool oldSphere = false;
                if (me.Flags2.TestFlag(PlayerFlags2.Halfturret))
                {
                    // what MphRead's turret sphere would say about the same line (a Power Beam's radius)
                    CollisionResult res = default;
                    float radius = Metadata.BeamRadiusValues[0];
                    oldSphere = CollisionDetection.CheckCylinderOverlapSphere(start, start + across * 5, me.Halfturret.Position,
                        radius + 0.45f, ref res);
                }
                int health = me.Health;
                int turret = me.Flags2.TestFlag(PlayerFlags2.Halfturret) ? me.Halfturret.Health : 0;
                BeamResultFlags spawned = BeamProjectileEntity.Spawn(shooter, equip, start, across, BeamSpawnFlags.None,
                    me.NodeRef, host.Scene);
                if (!spawned.TestFlag(BeamResultFlags.Spawned))
                {
                    Fail($"shot at {height:0.00} not spawned ({spawned})");
                }
                for (int f = 0; f < 40 && !host.Ended; f++)
                {
                    Step(default);
                    int nowTurret = me.Flags2.TestFlag(PlayerFlags2.Halfturret) ? me.Halfturret.Health : 0;
                    if (me.Health < health || nowTurret < turret)
                    {
                        Step(default, 10); // let it clear before the next
                        return (me.NetLastAttacker == shooter.SlotIndex, me.NetLastFlags, oldSphere);
                    }
                }
                return (false, DamageFlags.None, oldSphere);
            }

            // control: his biped, standing where he spawned (a match starts with him unspawned: Shoot spawns him)
            for (int f = 0; f < 600 && me.Health == 0; f++)
            {
                Step(new CampaignInput { Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
            }
            Step(default, 60);
            Console.WriteLine($"spawned: health {me.Health} at {me.Position}, standing {me.Flags1.TestFlag(PlayerFlags1.Standing)}; "
                + $"shooter health {shooter.Health}");
            Vector3 face = new Vector3(me.FacingVector.X, 0, me.FacingVector.Z).Normalized();
            Vector3 side = Vector3.Cross(face, Vector3.UnitY).Normalized();
            float bipedFloor = me.Position.Y + Fixed.ToFloat(me.Values.MinPickupHeight);
            foreach ((float h, bool head) in new[] { (1.0f, false), (1.5f, true) })
            {
                var r = Shoot(me.Position, bipedFloor, side, h, 0);
                bool isHead = r.Flags.TestFlag(DamageFlags.Headshot);
                Console.WriteLine($"biped {h:0.00} over the floor: {(r.Hit ? "hit" : "MISS")}{(isHead ? ", HEADSHOT" : "")}");
                if (!r.Hit || isHead != head)
                {
                    Fail($"biped at {h:0.00}: hit {r.Hit}, headshot {isHead} (expected hit, headshot {head})");
                }
            }

            // split, fly the drone forward and up, let go
            var morph = new CampaignInput { Buttons = CampaignButtons.Morph };
            Step(morph);
            for (int f = 0; f < 60 && !me.Flags2.TestFlag(PlayerFlags2.Halfturret); f++)
            {
                Step(default);
            }
            if (!me.Flags2.TestFlag(PlayerFlags2.Halfturret))
            {
                Fail("no split");
            }
            else
            {
                var fly = new CampaignInput { Buttons = CampaignButtons.Jump };
                fly.Move.Y = 1;
                Step(fly, 90);
                Step(default, 120);
                HalfturretEntity body = me.Halfturret;
                Vector3 axis = body.HostBodyPosition;
                float floor = axis.Y + Fixed.ToFloat(me.Values.MinPickupHeight);
                Vector3 bodyFace = new Vector3(body.FacingVector.X, 0, body.FacingVector.Z).Normalized();
                Vector3 bodySide = Vector3.Cross(bodyFace, Vector3.UnitY).Normalized();
                Console.WriteLine($"standing body: floor {floor:0.00}, the drone {(me.Position - axis).Xz.Length:0.0} away, in alt form {me.IsAltForm}, energy {body.Health}");
                var cases = new[] { (0.3f, 0f, true), (0.8f, 0f, true), (1.2f, 0f, true), (1.45f, 0f, true), (1.55f, 0f, true),
                    (1.75f, 0f, false), (1.2f, 0.75f, false) };
                foreach ((float h, float off, bool expect) in cases)
                {
                    if (!me.Flags2.TestFlag(PlayerFlags2.Halfturret))
                    {
                        Fail("the standing body went away mid-test");
                        break;
                    }
                    var r = Shoot(axis, floor, bodySide, h, off);
                    bool isHead = r.Flags.TestFlag(DamageFlags.Headshot);
                    bool isHalf = r.Flags.TestFlag(DamageFlags.Halfturret);
                    Console.WriteLine($"body {h:0.00} over the floor{(off != 0 ? $", {off:0.00} off the middle" : "")}: "
                        + $"{(r.Hit ? "hit" : "miss")}{(isHead ? ", HEADSHOT" : "")}{(r.Hit && !isHalf ? ", NOT a body hit" : "")} "
                        + $"(old sphere: {(r.OldSphere ? "hit" : "miss")})");
                    if (r.Hit != expect)
                    {
                        Fail($"body at {h:0.00} off {off:0.00}: hit {r.Hit}, expected {expect}");
                    }
                    if (r.Hit && (isHead || !isHalf))
                    {
                        Fail($"body at {h:0.00}: headshot {isHead}, Halfturret {isHalf} (expected no headshot, a Halfturret hit)");
                    }
                }
            }

            foreach (string e in errors)
            {
                Console.WriteLine("  " + e);
            }
            Console.WriteLine(errors.Count == 0 ? "standinghittest PASS" : "standinghittest FAIL");
        }
    }
}
