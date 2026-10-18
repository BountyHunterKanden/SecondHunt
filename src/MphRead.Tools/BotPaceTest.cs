using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -botpacetest [seconds=90] [arenas=Combat Hall,Alinos Perch,Data Shrine]: bot difficulty vs vanilla's 30 Hz bot brain
    // (PlayerEntity.HostBotTickPace). For each bot level (1-3 stars = BotLevel 0-2) with the tick pace off (MphRead's: the
    // per-tick aim step every 60 Hz frame) and on: one Samus bot against a human Samus who strafes left / right (never
    // shoots, health refilled every frame). Prints the bot's largest turn per frame and per tick (vanilla: 5 / 15 / 20 deg
    // per tick), its mean turn speed while turning, its jumps, and the damage it dealt per minute.
    internal static class BotPaceTest
    {
        // the bot's aim (PlayerInput UpdateAimX turns it by the AI's aim step; the camera adds its own effects)
        private static readonly System.Reflection.FieldInfo _gunVec1 = typeof(PlayerEntity).GetField("_gunVec1",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        private static readonly System.Reflection.FieldInfo _facingVector = typeof(PlayerEntity).GetField("_facingVector",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        private static int _logged;
        private static int _period = 90; // period=<frames>: each strafe's length (vanilla_bot.lua's MPH_PERIOD; DS frames = ours)

        private record struct Result(float MaxFrame, float MaxTick, float TurnRate, int Jumps, float DamagePerMin);

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            int seconds = 90;
            string[] arenas = ["Combat Hall", "Alinos Perch", "Data Shrine"];
            Hunter botHunter = Hunter.Samus;
            string? csv = null; // csv=<path prefix>: per tick rows like the emulator's vanilla_bot.lua (vbot.csv)
            int[] levels = [0, 1, 2];
            foreach (string arg in args.Skip(1))
            {
                if (arg.StartsWith("seconds=")) seconds = Int32.Parse(arg[8..]);
                else if (arg.StartsWith("arenas=")) arenas = arg[7..].Split(',');
                else if (arg.StartsWith("bot=")) botHunter = Enum.Parse<Hunter>(arg[4..], ignoreCase: true);
                else if (arg.StartsWith("csv=")) csv = arg[4..];
                else if (arg.StartsWith("period=")) _period = Int32.Parse(arg[7..]);
                else if (arg.StartsWith("levels=")) levels = arg[7..].Split(',').Select(l => Int32.Parse(l) - 1).ToArray();
            }
            bool pass = true;
            foreach (int level in levels)
            {
                foreach (bool pace in new[] { false, true })
                {
                    var results = arenas.Select(a => Match(a, level, pace, seconds, botHunter,
                        csv == null ? null : $"{csv}_{a.Replace(' ', '_')}_L{level + 1}_{(pace ? "on" : "off")}.csv")).ToList();
                    float maxFrame = results.Max(r => r.MaxFrame);
                    float maxTick = results.Max(r => r.MaxTick);
                    Console.WriteLine($"level {level + 1} pace {(pace ? "on " : "off")}: max turn {maxFrame,5:0.00} deg/frame"
                        + $" {maxTick,5:0.00} deg/tick, turning {results.Average(r => r.TurnRate),6:0.0} deg/s,"
                        + $" jumps {results.Sum(r => r.Jumps),3}, damage {results.Average(r => r.DamagePerMin),6:0.0}/min"
                        + $"  [{String.Join(" | ", results.Select(r => $"{r.DamagePerMin:0}/min {r.Jumps}j"))}]");
                    // vanilla's step is 5 / 15 / 20 deg per tick; with the pace on no tick may turn further (+0.1 for float slop)
                    float step = new[] { 5f, 15, 20 }[level];
                    if (pace && maxTick > step + 0.1f)
                    {
                        Console.WriteLine($"  FAIL: turned {maxTick:0.00} deg in one tick (vanilla's most is {step})");
                        pass = false;
                    }
                }
            }
            Console.WriteLine(pass ? "PASS botpacetest" : "FAIL botpacetest");
        }

        private static Result Match(string arena, int level, bool pace, int seconds, Hunter botHunter, string? csv)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find(arena)!.Name,
                TimeLimitSeconds = seconds + 60, BotTickPace = pace };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = botHunter, Recolor = 1, Bot = true, BotLevel = level });
            using CampaignHost host = CampaignHost.StartMatch(settings);
            host.Scene.HostDrawFreeSim = true; // as a real match (ReplaySession.Started): places Spire's rocks without a draw
            PlayerEntity me = PlayerEntity.Players[0];
            PlayerEntity bot = PlayerEntity.Players[1];
            for (int f = 0; f < 600 && !(me.Health > 0 && me.LoadFlags.TestFlag(LoadFlags.Active)); f++)
            {
                host.Step(default);
            }
            float maxFrame = 0, maxTick = 0, turned = 0;
            int turningFrames = 0, jumps = 0, damage = 0;
            bool wasGrounded = true;
            Vector3? prev = null, prev2 = null;
            Vector3 lastPos = bot.Position;
            using System.IO.StreamWriter? rows = csv == null ? null : new System.IO.StreamWriter(csv);
            rows?.WriteLine("f,dmg,bhp,bx,by,bz,fx,fz,ax,ay,az");
            int tickDamage = 0;
            // shots the bot fired (TimeSinceShot back to 0), by weapon and the charge level they went out at; R held frames
            var shots = new SortedDictionary<string, int>();
            int rDown = 0, rPresses = 0, altFrames = 0, altAttacks = 0, altAttackFrames = 0;
            bool rWas = false, attackWas = false;
            var hitSizes = new SortedDictionary<int, int>();
            for (int f = 0; f < seconds * 60; f++)
            {
                // strafe one way for 1.5 s, then the other
                var input = new CampaignInput { Move = new System.Numerics.Vector2(f / _period % 2 == 0 ? 1 : -1, 0) };
                int before = me.Health;
                host.Step(input);
                // (a hit, not a death plane / instant kill: those zero the health whatever it was)
                if (me.Health < before && me.Health > 0)
                {
                    damage += before - me.Health;
                    hitSizes[before - me.Health] = hitSizes.GetValueOrDefault(before - me.Health) + 1;
                }
                tickDamage += me.Health < before && me.Health > 0 ? before - me.Health : 0;
                me.Health = me.HealthMax;
                if (bot.TimeSinceShot == 0)
                {
                    int chg = bot.EquipInfo.ChargeLevel;
                    string key = $"{bot.CurrentWeapon}:{(chg == 0 ? "0" : chg < bot.EquipInfo.Weapon.MinCharge * 2 ? "<min" : "charged")}";
                    shots[key] = shots.GetValueOrDefault(key) + 1;
                }
                bool rNow = bot.Controls.Shoot.IsDown;
                rDown += rNow ? 1 : 0;
                rPresses += rNow && !rWas ? 1 : 0;
                rWas = rNow;
                altFrames += bot.IsAltForm ? 1 : 0;
                bool attackNow = bot.Flags2.TestFlag(PlayerFlags2.AltAttack);
                altAttackFrames += attackNow ? 1 : 0;
                altAttacks += attackNow && !attackWas ? 1 : 0;
                attackWas = attackNow;
                if (rows != null && f % 2 == 1)
                {
                    // one row per tick (2 frames), the vanilla trace's columns: +0x4C = _facingVector, +0x598 = CameraInfo.Facing
                    var fv = (Vector3)_facingVector.GetValue(bot)!;
                    Vector3 cf = bot.CameraInfo.Facing;
                    rows.WriteLine(String.Create(System.Globalization.CultureInfo.InvariantCulture, $"{f / 2},{tickDamage},{bot.Health},"
                        + $"{bot.Position.X:0.0000},{bot.Position.Y:0.0000},{bot.Position.Z:0.0000},"
                        + $"{fv.X:0.00000},{fv.Z:0.00000},{cf.X:0.00000},{cf.Y:0.00000},{cf.Z:0.00000}"));
                    tickDamage = 0;
                }
                // (morphing / unmorphing: the aim follows the ball's camera, not the AI's step; zoomed: UpdateAimX scales the
                // step by the zoom's FOV, as vanilla does)
                if (bot.Health <= 0 || !bot.LoadFlags.TestFlag(LoadFlags.Active) || bot.IsAltForm || bot.IsMorphing || bot.IsUnmorphing
                    || bot.EquipInfo.Zoomed)
                {
                    prev = prev2 = null;
                    continue;
                }
                Vector3 facing = ((Vector3)_gunVec1.GetValue(bot)!).WithY(0);
                if (facing.LengthSquared < 1e-6f)
                {
                    continue;
                }
                facing = facing.Normalized();
                if (prev is Vector3 p)
                {
                    float deg = MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(facing, p), -1, 1)));
                    maxFrame = MathF.Max(maxFrame, deg);
                    if (Environment.GetEnvironmentVariable("BOTPACE_LOG") == "1" && pace && deg > new[] { 5f, 15, 20 }[level] / (pace ? 2 : 1) + 0.1f && _logged++ < 40)
                    {
                        Console.WriteLine($"    big turn {arena} L{level + 1} pace {pace} f{f}: {deg:0.0} deg, moved {(bot.Position - lastPos).Length:0.00},"
                            + $" alt {bot.IsAltForm} morphing {bot.IsMorphing}/{bot.IsUnmorphing} grounded {bot.Flags1.TestFlag(PlayerFlags1.Grounded)}"
                            + $" zoom {bot.EquipInfo.Zoomed} hp {bot.Health} pad {bot.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)}");
                    }
                    if (deg > 0.25f)
                    {
                        turned += deg;
                        turningFrames++;
                    }
                    // a tick = any two frames in a row (with the pace on, each turns at most half a tick's step)
                    if (prev2 is Vector3 p2)
                    {
                        maxTick = MathF.Max(maxTick, MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(facing, p2), -1, 1))));
                    }
                }
                bool grounded = bot.Flags1.TestFlag(PlayerFlags1.Grounded);
                if (wasGrounded && !grounded && bot.Speed.Y > 0)
                {
                    jumps++;
                }
                wasGrounded = grounded;
                prev2 = prev;
                prev = facing;
                lastPos = bot.Position;
            }
            if (Environment.GetEnvironmentVariable("BOTPACE_SHOTS") == "1")
            {
                Console.WriteLine($"    shots {arena} L{level + 1} pace {pace}: R presses {rPresses / (seconds / 60f):0}/min, R held {rDown * 100f / (seconds * 60):0}%,"
                    + $" fired/min [{String.Join(", ", shots.Select(s => $"{s.Key} {s.Value / (seconds / 60f):0.0}"))}]");
                Console.WriteLine($"      alt form {altFrames * 100f / (seconds * 60):0}%, alt attacks {altAttacks / (seconds / 60f):0.0}/min"
                    + $" ({altAttackFrames * 100f / (seconds * 60):0.0}% of frames), hits [{String.Join(", ", hitSizes.Select(h => $"{h.Value}x{h.Key}"))}]");
            }
            return new Result(maxFrame, maxTick, turningFrames == 0 ? 0 : turned / turningFrames * 60, jumps, damage / (seconds / 60f));
        }
    }
}
