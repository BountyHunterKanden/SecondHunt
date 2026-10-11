using System;
using System.Diagnostics;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Release test notes T11 (touch controls, vanilla DS stylus), headless: -touchtest [arena=<name>]
    // Samus alone in an arena, driven through CampaignInput.TouchDown / TouchDx / TouchDy as the touch controls' touch area
    // sends them (MphRead PlayerTouch.cs). Checks:
    //   double tap: a second press 6-15 frames after the first jumps (vanilla 3-7 ticks); one tap, a gap too short or
    //     too long, and a double tap in the morph ball don't
    //   touch roll: a slow drag up rolls the ball forward, a drag right rolls it right, and it rolls on after the lift;
    //     no boost from it
    //   touch boost: a flick (> 90 px over 8 frames) boosts once; holding on and flicking again doesn't; after a lift it does
    internal static class TouchTest
    {
        private static int _fails;

        public static void Run(string[] args)
        {
            string? arenaName = null;
            foreach (string arg in args.Skip(1))
            {
                if (arg.StartsWith("arena=", StringComparison.OrdinalIgnoreCase)) arenaName = arg[6..];
                else throw new ArgumentException($"unknown option {arg}");
            }
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string arena = arenaName == null
                ? MatchArenas.All.First(a => MatchArenas.Supports(a, GameMode.Battle)).Name
                : (MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}")).Name;
            Console.WriteLine($"arena {arena}");
            _fails = 0;
            Wire();
            // the double tap: presses (frames down) separated by the gap between the two presses
            Check("one tap: no jump", !Taps(arena, morph: false, gap: null));
            Check("double tap, gap 10: jump", Taps(arena, morph: false, gap: 10));
            Check("double tap, gap 6: jump", Taps(arena, morph: false, gap: 6));
            Check("double tap, gap 14: jump", Taps(arena, morph: false, gap: 14));
            Check("double tap, gap 4: no jump", !Taps(arena, morph: false, gap: 4));
            Check("double tap, gap 24: no jump", !Taps(arena, morph: false, gap: 24));
            Roll(arena);
            Boost(arena);
            Remote(arena);
            Console.WriteLine(_fails == 0 ? "PASS" : $"FAIL ({_fails})");
        }

        // the LAN / online input carries the stylus; an old replay record (no touch bytes) reads as the stylus up
        private static void Wire()
        {
            var sent = new MphRecomp.Multiplayer.Net.NetInput { Seq = 7, Yaw = 30, Pitch = -5, AimEpoch = 3, ViewTick = 1234,
                Touching = true, TouchDx = -100, TouchDy = 17, SelectWeapon = BeamType.None };
            byte[] record = new byte[MphRecomp.Multiplayer.Net.NetInput.RecordSize];
            sent.WriteRecord(record);
            var got = MphRecomp.Multiplayer.Net.NetInput.ReadRecord(record);
            Check("net input: touch bytes round-trip", got.Touching && got.TouchDx == -100 && got.TouchDy == 17 && got.ViewTick == 1234 && got.Seq == 7);
            byte[] old = new byte[MphRecomp.Multiplayer.Net.NetInput.OldRecordSize];
            record.AsSpan(0, 11).CopyTo(old);
            record.AsSpan(MphRecomp.Multiplayer.Net.NetInput.Size, 4).CopyTo(old.AsSpan(11));
            var up = MphRecomp.Multiplayer.Net.NetInput.ReadRecord(MphRecomp.Multiplayer.Net.NetInput.UpgradeOldRecord(old));
            Check("old record: stylus up, the rest kept", !up.Touching && up.TouchDx == 0 && up.ViewTick == 1234 && up.AimEpoch == 3);
        }

        private static void Check(string what, bool ok)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) _fails++;
        }

        private static CampaignHost Start(string arena, bool morph)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena, TimeLimitSeconds = 600, Solo = true };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1" });
            CampaignHost host = CampaignHost.StartMatch(settings);
            for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++)
            {
                host.Step(default);
            }
            for (int f = 0; f < 90; f++) // settle on the floor
            {
                host.Step(default);
            }
            if (morph)
            {
                host.Step(new CampaignInput { Buttons = CampaignButtons.Morph, SelectWeapon = BeamType.None });
                for (int f = 0; f < 120 && !(PlayerEntity.Main.IsAltForm && !PlayerEntity.Main.IsMorphing); f++)
                {
                    host.Step(default);
                }
                for (int f = 0; f < 30; f++)
                {
                    host.Step(default);
                }
            }
            return host;
        }

        private static CampaignInput Touch(bool down, int dx = 0, int dy = 0)
            => new CampaignInput { SelectWeapon = BeamType.None, TouchDown = down, TouchDx = (sbyte)dx, TouchDy = (sbyte)dy };

        // a tap held 3 frames, then (gap = frames from the first press to the second) another; true when she left the floor
        private static bool Taps(string arena, bool morph, int? gap)
        {
            using CampaignHost host = Start(arena, morph);
            PlayerEntity me = PlayerEntity.Main;
            float y0 = me.Position.Y, y1 = y0;
            for (int f = 0; f < 60; f++)
            {
                bool down = f < 3 || gap is int g && f >= g && f < g + 3;
                host.Step(Touch(down));
                y1 = MathF.Max(y1, me.Position.Y);
            }
            return y1 > y0 + 0.3f;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.X, 0, v.Z);

        private static void Roll(string arena)
        {
            // forward: drag up (dy < 0) 3 px a frame for 30 frames, then let go
            foreach ((string name, int dx, int dy, bool right) in new[] { ("drag up", 0, -3, false), ("drag right", 3, 0, true) })
            {
                using CampaignHost host = Start(arena, morph: true);
                PlayerEntity me = PlayerEntity.Main;
                Check($"{name}: in the morph ball, touch area rolls", me.IsAltForm && me.HostTouchRolls);
                Vector3 aim = Flat(me.NetAimVector).Normalized();
                Vector3 side = Vector3.Cross(aim, Vector3.UnitY).Normalized(); // the camera's right
                Vector3 p0 = me.Position;
                bool boosted = false;
                host.Step(Touch(true));
                for (int f = 0; f < 30; f++)
                {
                    host.Step(Touch(true, dx, dy));
                    boosted |= me.Flags1.TestFlag(PlayerFlags1.Boosting);
                }
                Vector3 moved = Flat(me.Position - p0);
                float speedAtLift = Flat(me.Speed).Length;
                host.Step(Touch(false));
                Vector3 p1 = me.Position;
                for (int f = 0; f < 10; f++)
                {
                    host.Step(default);
                }
                float after = Flat(me.Position - p1).Length;
                Vector3 dir = right ? side : aim;
                float along = Vector3.Dot(moved, dir);
                Console.WriteLine($"    {name}: moved {moved.Length:0.000} ({along:0.000} along), speed at lift {speedAtLift:0.0000}, then {after:0.000} in 10 frames");
                Check($"{name}: rolls that way", moved.Length > 0.05f && along > 0.8f * moved.Length);
                Check($"{name}: rolls on after the lift", after > 0.01f);
                Check($"{name}: no boost from a slow drag", !boosted);
            }
            // the double tap does nothing in the morph ball (no jump; the game's ProcessAlt never reads it)
            Check("double tap in the morph ball: no hop", !Taps(arena, morph: true, gap: 10));
        }

        // a LAN / online seat: the host applies its client's NetInput (MatchRemote.SetRemoteInput); its stylus rolls its ball
        private static void Remote(string arena)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena, TimeLimitSeconds = 600 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Host" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Touch", Remote = true });
            using CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity seat = PlayerEntity.Players[1];
            var none = new MphRecomp.Multiplayer.Net.NetInput { SelectWeapon = BeamType.None, AimEpoch = (byte)seat.NetAimEpoch };
            void Step(MphRecomp.Multiplayer.Net.NetInput input)
            {
                input.AimEpoch = (byte)seat.NetAimEpoch;
                input.Yaw = seat.NetYaw;
                input.Pitch = seat.NetPitch;
                host.SetRemoteInput(1, input);
                host.Step(default);
            }
            for (int f = 0; f < 600 && !(seat.Health > 0 && seat.LoadFlags.TestFlag(LoadFlags.Active)); f++) Step(none);
            for (int f = 0; f < 90; f++) Step(none);
            Step(none with { Buttons = CampaignButtons.Morph });
            for (int f = 0; f < 150; f++) Step(none);
            Vector3 aim = Flat(seat.NetAimVector).Normalized();
            Vector3 p0 = seat.Position;
            Step(none with { Touching = true });
            for (int f = 0; f < 30; f++) Step(none with { Touching = true, TouchDy = -3 });
            Vector3 moved = Flat(seat.Position - p0);
            Console.WriteLine($"    remote seat: alt form {seat.IsAltForm}, moved {moved.Length:0.000} ({Vector3.Dot(moved, aim):0.000} along)");
            Check("remote seat: its stylus rolls its ball forward", seat.IsAltForm && moved.Length > 0.05f && Vector3.Dot(moved, aim) > 0.8f * moved.Length);
        }

        private static void Boost(string arena)
        {
            using CampaignHost host = Start(arena, morph: true);
            PlayerEntity me = PlayerEntity.Main;
            int boosts = 0;
            bool was = false;
            void Step(CampaignInput input)
            {
                host.Step(input);
                bool now = me.Flags1.TestFlag(PlayerFlags1.Boosting);
                if (now && !was) boosts++;
                was = now;
            }
            Step(Touch(true));
            for (int f = 0; f < 8; f++) Step(Touch(true, 0, -15)); // 120 px up in 8 frames
            Check($"flick: one boost (got {boosts})", boosts == 1);
            float speed = Flat(me.Speed).Length;
            Console.WriteLine($"    flick: speed {speed:0.000}");
            for (int f = 0; f < 60; f++) Step(Touch(true)); // held: the boost runs out
            for (int f = 0; f < 8; f++) Step(Touch(true, 0, -15));
            Check($"second flick without a lift: no boost (got {boosts})", boosts == 1);
            Step(Touch(false));
            for (int f = 0; f < 60; f++) Step(default);
            Step(Touch(true));
            for (int f = 0; f < 8; f++) Step(Touch(true, 0, 15));
            Check($"flick after a lift: boosts again (got {boosts})", boosts == 2);
        }
    }
}
