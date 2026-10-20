using System;
using System.Diagnostics;
using System.IO;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Replay;

namespace MphRead
{
    // -trooperspamreplay <file> [arena=...] [time=60]: records a replay with a Trooper seat 0 against 2 bots, in the current
    // format (the drone switch rule and the laser's no-push rule on): jumps with MORPH pressed every 3 frames, the drone's
    // laser, plain walking and shooting, in turns. Play it back with -replayplay <file> to check it's exact.
    internal static class TrooperSpamReplay
    {
        // -trooperspamreplay push: a jumping Kanden (seat 0, Jump pressed once) takes the drone laser's 15 ticks as
        // ScoutDroneLaserStep deals them (10 each, NoDmgInvuln, every 4 frames from his 2nd frame up), with the laser's
        // direction (MphRead's, HostDroneLaserNoPush off) and with none (on), next to no hits: how high and far he goes
        private static void Push()
        {
            foreach (string arena in new[] { "Combat Hall", "ECHOES PIPELINE" })
            {
                foreach (int how in new[] { 0, 1, 2 }) // no hits, with the laser's direction, with none
                {
                    var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find(arena)!.Name, TimeLimitSeconds = 600 };
                    settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" });
                    settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Character = Characters.Trooper.Id, Bot = true, BotLevel = 0 });
                    using CampaignHost host = CampaignHost.StartMatch(settings);
                    PlayerEntity me = PlayerEntity.Main;
                    for (int f = 0; f < 600 && !(me.Health > 0 && me.LoadFlags.TestFlag(LoadFlags.Active)); f++)
                    {
                        host.Step(default);
                    }
                    for (int f = 0; f < 60; f++)
                    {
                        host.Step(default); // settle on the floor
                    }
                    PlayerEntity trooper = PlayerEntity.Players[1];
                    OpenTK.Mathematics.Vector3 start = me.Position;
                    // a level shot rising a little, as a drone above a target on the ground lases it
                    OpenTK.Mathematics.Vector3 dir = new OpenTK.Mathematics.Vector3(1, 0.25f, 0).Normalized();
                    float apex = 0, far = 0;
                    int hp = (int)me.Health;
                    for (int f = 0; f < 120; f++)
                    {
                        if (how > 0 && f >= 2 && f < 2 + 15 * 4 && (f - 2) % 4 == 0 && me.Health > 0)
                        {
                            me.TakeDamage(10, DamageFlags.NoDmgInvuln, how == 1 ? dir : null, trooper);
                        }
                        host.Step(new CampaignInput { Buttons = f == 0 ? CampaignButtons.Jump : 0 });
                        apex = MathF.Max(apex, me.Position.Y - start.Y);
                        far = MathF.Max(far, new OpenTK.Mathematics.Vector2(me.Position.X - start.X, me.Position.Z - start.Z).Length);
                    }
                    Console.WriteLine($"{arena,-16} {(how == 0 ? "no hits      " : how == 1 ? "laser's dir  " : "no direction ")}: "
                        + $"apex {apex,6:0.00}  carried {far,6:0.00}  damage {hp - me.Health}");
                }
            }
        }

        public static void Run(string[] args)
        {
            if (args.Length == 2 && args[1] == "push")
            {
                Trace.Listeners.Clear();
                if (ToolPaths.UseDesktopExtraction())
                {
                    Push();
                }
                return;
            }
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -trooperspamreplay <file> [arena=...] [time=60]");
                return;
            }
            string path = Path.GetFullPath(args[1]);
            string arena = "Combat Hall";
            float time = 60;
            foreach (string arg in args[2..])
            {
                int eq = arg.IndexOf('=');
                string key = eq < 0 ? arg : arg[..eq];
                string value = eq < 0 ? "" : arg[(eq + 1)..];
                switch (key.ToLowerInvariant())
                {
                case "arena": arena = value; break;
                case "time": time = Single.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"unknown option {arg}");
                }
            }
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = (MatchArenas.Find(arena) ?? throw new ArgumentException($"no arena {arena}")).Name, TimeLimitSeconds = time };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, BotLevel = 2 });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Noxus, Bot = true, BotLevel = 2 });
            var rec = new ReplayRecorder(settings, "tools");
            using CampaignHost host = CampaignHost.StartMatch(settings);
            rec.Started(host.Scene);
            long maxFrames = (long)(time * 60) + 600;
            int splits = 0;
            while (!host.Ended && host.Frame < maxFrames)
            {
                // 4 s turns: MORPH spam in the air, the drone's laser, walking and shooting
                long f = host.Frame;
                int phase = (int)(f / 240 % 3);
                var input = new CampaignInput { Move = new System.Numerics.Vector2(0, 1), AimDelta = new System.Numerics.Vector2(f % 120 < 60 ? 1.5f : -1.5f, 0) };
                input.Buttons = phase switch
                {
                    0 => (f % 60 < 10 ? CampaignButtons.Jump : 0) | (f % 3 == 0 ? CampaignButtons.Morph : 0),
                    1 => (f % 240 == 0 && !PlayerEntity.Main.IsAltForm ? CampaignButtons.Morph : 0) | (f % 20 < 10 ? CampaignButtons.Shoot : 0),
                    _ => (f % 240 == 0 && PlayerEntity.Main.IsAltForm ? CampaignButtons.Morph : 0) | (f % 30 < 15 ? CampaignButtons.Shoot : 0),
                };
                host.Step(input);
                rec.File.Steps.Add(new ReplayStep { Input = input, Checksum = ReplayState.Capture(host.Scene).Checksum });
            }
            splits = PlayerEntity.Main.DroneSplits;
            rec.End(host.Ended);
            rec.File.Save(path);
            Console.WriteLine($"recorded {rec.File.Steps.Count} steps, format {ReplayFile.FormatVersion}, Trooper splits {splits}, "
                + $"drone shots {PlayerEntity.Main.DroneShots}, switch rule {PlayerEntity.Main.HostDroneSwitchRule}, "
                + $"laser no push {PlayerEntity.HostDroneLaserNoPush}: {path} ({new FileInfo(path).Length / 1024} KB)");
        }
    }
}
