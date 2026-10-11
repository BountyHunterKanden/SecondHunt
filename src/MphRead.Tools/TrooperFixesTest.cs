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
    // Release test notes T5 (Trooper + Dark Samus fixes), headless:
    //   -trooperfixtest pad [arena=all]   N9: the Scout Drone on every pad that takes alt forms: flown with
    //                                     HostDronePadArc off (MphRead's alt-form flight) and on (biped gravity on the way
    //                                     down, then hover), next to the Trooper's own biped flight on the same pad. Checks: with the
    //                                     rule on it climbs as MphRead's (the pads are tuned for it), no longer lands hard
    //                                     (MphRead: 3x gravity on the way down) and carries at least as far.
    //   -trooperfixtest lanmenu           N31: a LAN match as the vanilla multiplayer menu builds it (MultiplayerMenu.
    //                                     BuildMatch) with a Trooper host seat and a joiner, as the joiner gets it
    //                                     (MatchClient.ForSeat): on the host and on the joiner the
    //                                     Trooper seat must get the Wall Cling + Scout Drone (it played vanilla Weavel
    //                                     on both before: BuildMatch left Abilities unset).
    internal static class TrooperFixesTest
    {
        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "pad";
            string arenaName = "all";
            foreach (string arg in args.Skip(2))
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
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            if (verb == "pad")
            {
                Pads(arenaName);
                return;
            }
            if (verb == "lanmenu")
            {
                LanMenu();
                return;
            }
            if (verb == "dronebot")
            {
                DroneBot(arenaName == "all" ? "Combat Hall" : arenaName);
                return;
            }
            throw new ArgumentException($"unknown verb {verb}");
        }

        private record Flight(bool Fired, float Apex, float Distance, int Frames, int HoverFrames, float LandSpeedY);

        private static void Pads(string arenaName)
        {
            List<string> arenas = arenaName == "all"
                ? MatchArenas.All.Where(a => MatchArenas.Supports(a, GameMode.Battle)).Select(a => a.Name).Distinct().ToList()
                : new List<string> { (MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}")).Name };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo dataField = typeof(JumpPadEntity).GetField("_data", flags)!;
            int pads = 0, fails = 0;
            foreach (string arena in arenas)
            {
                // the arena's alt-form pads
                var list = new List<(int Id, Vector3 Pos, bool Biped)>();
                using (CampaignHost probe = Start(arena))
                {
                    foreach (EntityBase e in probe.Scene.Entities)
                    {
                        if (e is JumpPadEntity pad && dataField.GetValue(pad) is JumpPadEntityData d && d.TriggerFlags.TestFlag(TriggerFlags.PlayerAlt))
                        {
                            list.Add((pad.Id, pad.Position, d.TriggerFlags.TestFlag(TriggerFlags.PlayerBiped)));
                        }
                    }
                }
                foreach ((int id, Vector3 pos, bool biped) in list)
                {
                    Flight off = Fly(arena, id, pos, drone: true, rule: false);
                    Flight on = Fly(arena, id, pos, drone: true, rule: true);
                    Flight? walk = biped ? Fly(arena, id, pos, drone: false, rule: true) : null;
                    string F(Flight f) => !f.Fired ? "not fired" : $"apex {f.Apex,5:0.00} dist {f.Distance,5:0.00} {f.Frames,3}f hover {f.HoverFrames,3}f land vy {f.LandSpeedY:0.00}";
                    Console.WriteLine($"{arena} pad {id}:");
                    Console.WriteLine($"   drone MPH: {F(off)}");
                    Console.WriteLine($"   drone arc: {F(on)}");
                    if (walk != null) Console.WriteLine($"   biped    : {F(walk)}");
                    if (!on.Fired)
                    {
                        continue;
                    }
                    pads++;
                    // the owner's N9: no hard slam (MphRead's alt-form flight comes down at 3x gravity), the climb as MphRead's
                    // (the pads are tuned for it), and it doesn't come up short
                    if (off.Fired && off.LandSpeedY < -0.3f && on.LandSpeedY < off.LandSpeedY * 0.7f
                        && (walk == null || on.LandSpeedY < walk.LandSpeedY * 1.2f)) // no softer than the biped's landing is fine
                    {
                        Console.WriteLine($"   FAIL: the drone still lands hard ({on.LandSpeedY:0.00} vs MphRead's {off.LandSpeedY:0.00})");
                        fails++;
                    }
                    if (off.Fired && MathF.Abs(on.Apex - off.Apex) > 0.05f)
                    {
                        Console.WriteLine($"   FAIL: the drone's climb isn't MphRead's (apex {on.Apex:0.00} vs {off.Apex:0.00})");
                        fails++;
                    }
                    if (off.Fired && off.Frames < 890 && off.Distance >= 1 && on.Distance < off.Distance * 0.9f) // (vertical pads: no distance)
                    {
                        Console.WriteLine($"   FAIL: the drone's flight carries less far than MphRead's alt-form flight");
                        fails++;
                    }
                }
            }
            Console.WriteLine(pads == 0 ? "FAIL: no alt-form pads flown" : fails == 0 ? $"PASS ({pads} pads)" : $"FAIL ({fails})");
        }

        private static void LanMenu()
        {
            MatchSettings menu = MphRecomp.Frontend.MultiplayerMenu.BuildMatch();
            // the seats as the lobby would have them: this player the Trooper, one LAN joiner (Samus)
            menu.Players.Clear();
            menu.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Character = Characters.Trooper.Id, Nickname = "host" });
            menu.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Remote = true, Nickname = "guest" });
            // (LanSession's START carries Abilities as is: WriteSettings / ReadSettings, LAN protocol 9)
            MatchSettings client = MatchClient.ForSeat(menu, 1);
            var problems = new List<string>();
            foreach ((string who, MatchSettings s) in new[] { ("host", menu), ("joiner", client) })
            {
                using CampaignHost host = CampaignHost.StartMatch(s);
                PlayerEntity trooper = PlayerEntity.Players[0];
                Console.WriteLine($"{who}: Abilities {s.Abilities?.ToString() ?? "unset"}, Trooper seat: Wall Cling {trooper.HostWallCling}, Scout Drone {trooper.HostScoutDrone}");
                if (!trooper.HostWallCling || !trooper.HostScoutDrone)
                {
                    problems.Add($"{who}'s Trooper seat plays vanilla Weavel");
                }
            }
            Console.WriteLine(problems.Count == 0 ? "PASS trooperfixtest lanmenu" : "FAIL trooperfixtest lanmenu: " + string.Join("; ", problems));
        }

        // A/B: -dronetest's match (Trooper player, Trooper bot, Kanden bot, 120 s, nothing pressed) with the T5 rules off
        // and on: the bot Trooper's drone shots and lasers
        private static void DroneBot(string arena)
        {
            Characters.ScoutDroneOn = true;
            foreach (bool rules in new[] { false, true })
            {
                var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find(arena)!.Name, TimeLimitSeconds = 120,
                    WallClingJumpRule = rules, DronePadArc = rules, HoverWalk = rules };
                settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
                settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Recolor = 1, Bot = true, BotLevel = 0, Character = Characters.Trooper.Id });
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, BotLevel = 0 });
                using CampaignHost host = CampaignHost.StartMatch(settings);
                for (int f = 0; f < 120 * 60 + 600; f++) host.Step(default);
                PlayerEntity bot = PlayerEntity.Players[1];
                Console.WriteLine($"rules {(rules ? "on " : "off")}: bot Trooper drone {bot.HostScoutDrone} splits {bot.DroneSplits} shots {bot.DroneShots}");
            }
        }

        private static CampaignHost Start(string arena)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = arena, TimeLimitSeconds = 600, Solo = true };
            settings.Players.Add(new MatchPlayer { Hunter = Characters.Trooper.Base, Nickname = "Player1", Character = Characters.Trooper.Id });
            CampaignHost host = CampaignHost.StartMatch(settings);
            for (int f = 0; f < 600 && !(PlayerEntity.Main.Health > 0 && PlayerEntity.Main.LoadFlags.TestFlag(LoadFlags.Active)); f++)
            {
                host.Step(default);
            }
            return host;
        }

        // the drone (or the biped) placed on the pad, nothing pressed: from the launch to the first frame standing
        private static Flight Fly(string arena, int padId, Vector3 padPos, bool drone, bool rule)
        {
            using CampaignHost host = Start(arena);
            PlayerEntity me = PlayerEntity.Main;
            me.HostDronePadArc = rule;
            // morph beside the pad (the body stays there), then put the drone on it
            host.PlacePlayer(padPos + new Vector3(3, 0.5f, 0), Vector3.UnitZ);
            for (int f = 0; f < 30; f++) host.Step(default);
            if (drone)
            {
                for (int f = 0; f < 60 && !me.IsAltForm; f++)
                {
                    host.Step(new CampaignInput { Buttons = f % 2 == 0 ? CampaignButtons.Morph : 0 });
                }
                for (int f = 0; f < 20; f++) host.Step(default);
            }
            host.PlacePlayer(padPos + new Vector3(0, 0.3f, 0), Vector3.UnitZ);
            int fired = -1, frames = 0, hover = 0;
            float apex = 0, dist = 0, landVy = 0, cap = me.DroneCap;
            bool climbing = true; // apex = the first climb only (vertical pads relaunch for the whole run)
            Vector3 start = me.Position;
            for (int f = 0; f < 900; f++)
            {
                Vector3 before = me.Speed;
                host.Step(default);
                if (fired < 0)
                {
                    if (me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                    {
                        fired = f;
                        start = me.Position;
                    }
                    else if (f > 60)
                    {
                        break;
                    }
                    continue;
                }
                frames++;
                if (Environment.GetEnvironmentVariable("PADTRACE") == "1" && frames <= 80 && frames % 4 == 1)
                {
                    Console.WriteLine($"      f{frames} y {me.Position.Y - start.Y:0.00} vy {me.Speed.Y:0.000} h {new Vector2(me.Speed.X, me.Speed.Z).Length:0.000} alt {me.IsAltForm}");
                }
                if (climbing)
                {
                    apex = MathF.Max(apex, me.Position.Y - start.Y);
                    climbing = me.Speed.Y > 0 || frames <= 2;
                }
                dist = MathF.Max(dist, new Vector2(me.Position.X - start.X, me.Position.Z - start.Z).Length);
                if (drone && me.Speed.Y > -0.07f && me.Speed.Y < 0 && me.DroneFloorGap >= 0 && me.DroneFloorGap <= cap
                    && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                {
                    hover++;
                }
                if (frames > 4 && (me.Flags1.TestFlag(PlayerFlags1.Standing) || !me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)))
                {
                    landVy = before.Y;
                    break;
                }
            }
            return new Flight(fired >= 0, apex, dist, frames, hover, landVy);
        }
    }
}
