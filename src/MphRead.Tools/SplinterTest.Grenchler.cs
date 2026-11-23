using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Campaign.Retro;
using OpenTK.Mathematics;

namespace MphRead
{
    // -splintertest grenchler [out=] [ROOM]: the imported Grenchler (-splintertest import name=MP2Grenchler first) in a
    // campaign room (UNIT1_RM1), the player standing still: its numbers from enemy.cfg, its machine from spotting the player
    // to an attack (bite / beam / charge), the tail knocked off, death. PC only: the Grenchler isn't placeable yet.
    internal static partial class SplinterTest
    {
        static bool Alive(Scene scene, EnemyInstanceEntity s)
        {
            foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities()) if (e == s) return true;
            return false;
        }

        static void Grenchler(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            string dir = Path.Combine(Out(args), "MP2Grenchler");
            if (!RetroEnemyConfig.Exists(dir)) { Console.WriteLine($"no import at {dir}: run -splintertest import name=MP2Grenchler first"); return; }
            string roomName = args.Skip(2).FirstOrDefault(a => !a.StartsWith('-') && !a.Contains('=') && a is not ("trace" or "links" or "geo")) ?? "UNIT1_RM1";
            RetroEnemyConfig cfg = RetroEnemyConfig.Load(dir);
            Console.WriteLine($"== config {dir}: {cfg.Count} properties, type {cfg.Type}, fsm {cfg.FsmId:x8}");
            Check(cfg.Type == "GRCH" && cfg.Fsm != null && cfg.Fsm.States.Count == 43, $"GRCH, {cfg.Fsm?.States.Count} states (43)");
            Check(cfg.F32("b3774750/cf90d15e/f0668919", 0) == 200, $"health {cfg.F32("b3774750/cf90d15e/f0668919", 0)} (200)");
            Check(cfg.F32("df636c4b/f2d02613", 0) == 20 && cfg.F32("13e30e4d/f2d02613", 0) == 10, "bite 20, beam 10");
            Check(cfg.F32("04d51e3a", 0) == 80, $"tail health {cfg.F32("04d51e3a", 0)} (80)");

            if (args.Contains("links"))
            {
                // the raw links, indices included (the -fsmdump view names them only)
                static string L(MphRecomp.Import.Retro.Fsm.Link l) => $"{l.Kind.ToString()[0]}{l.Index}";
                for (int i = 0; i < cfg.Fsm!.States.Count; i++)
                    Console.WriteLine($"  s{i} {cfg.Fsm.States[i].Name}: {String.Join(" ", cfg.Fsm.States[i].Links.Select(L))}");
                for (int i = 0; i < cfg.Fsm.Transitions.Count; i++)
                {
                    MphRecomp.Import.Retro.Fsm.Node n = cfg.Fsm.Transitions[i];
                    Console.WriteLine($"  t{i} {(n.Extra == 1 ? "!" : "")}{n.Name}: {String.Join(" ", n.Links.Select(L))}");
                }
                return;
            }
            Console.WriteLine($"== live room {roomName}");
            using CampaignHost host = CampaignHost.Start(roomName);
            Scene scene = host.Scene;
            Steps(host, 10);
            Vector3 start = host.Player.Position;
            // a spot well off (charge / beam range) with a clear line to the player and floor under it
            Vector3? spot = null;
            for (int a = 0; a < 16 && spot == null; a++)
            {
                for (float r = 16; r >= 8 && spot == null; r -= 2)
                {
                    float ang = a * MathF.Tau / 16;
                    Vector3 p = start + new Vector3(MathF.Sin(ang), 0, MathF.Cos(ang)) * r;
                    CollisionResult res = default;
                    if (CollisionDetection.CheckBetweenPoints(start.AddY(0.8f), p.AddY(0.8f), TestFlags.None, scene, ref res)) continue;
                    res = default;
                    if (!CollisionDetection.CheckBetweenPoints(p.AddY(0.8f), p.AddY(-2), TestFlags.None, scene, ref res) || res.Plane.Y < 0.5f) continue;
                    spot = res.Position;
                }
            }
            Check(spot != null, $"player at {start}, open floor at {spot}");
            if (spot == null) { Console.WriteLine($"== {_fails} FAILED"); return; }
            Vector3 to = start - spot.Value;
            if (args.Contains("geo"))
            {
                for (float f = 0; f <= 1.001f; f += 1 / 32f)
                {
                    Vector3 q = Vector3.Lerp(spot.Value, start, f);
                    CollisionResult gr = default;
                    bool fl = CollisionDetection.CheckBetweenPoints(q.AddY(4), q.AddY(-4), TestFlags.None, scene, ref gr);
                    Console.WriteLine($"  x {q.X:0.00} floor {(fl ? gr.Position.Y.ToString("0.00") : "-")} n {gr.Plane.Y:0.00}");
                }
                foreach (float h in new[] { 0.3f, 0.6f, 1.0f, 1.5f, 2.0f, 2.5f })
                {
                    CollisionResult wr = default;
                    bool w = CollisionDetection.CheckBetweenPoints(spot.Value.AddY(h), start with { Y = spot.Value.Y + h }, TestFlags.None, scene, ref wr);
                    Console.WriteLine($"  at +{h}: {(w ? $"hit {wr.Position} n {wr.Plane.Xyz}" : "clear")}");
                }
                return;
            }
            float yaw = MathF.Atan2(to.X, to.Z);
            var g = new EchoesGrenchler(31001, cfg, spot.Value, yaw, scene, seed: 5);
            scene.AddEntity(g);
            Check(Alive(scene, g), $"spawned {to.Length:0.0} away, hp {g.Hp} of {g.HpMax} (200 x {EchoesGrenchler.HealthScale}), state {g.Brain.StateName}");
            Check(g.Vulnerability(EchoesSplinter.VDark) == 200 && g.Vulnerability(EchoesSplinter.VLight) == 50, "Dark 200, Light 50 (from the disc)");

            int hp0 = host.Player.Health;
            float minY = spot.Value.Y, closest = to.Length, flat = to.Xz.Length;
            var clips = new List<string>();
            bool trace = args.Contains("trace");
            int tick = 0;
            Steps(host, 60 * 20, () =>
            {
                if (trace && tick++ % 30 == 0)
                    Console.WriteLine($"  {tick / 60f:0.0}s {g.Brain.StateName} {g.Clip} at {g.Position} ground {g.Grounded} to player {(host.Player.Position - g.Position).Length:0.00}");
                minY = MathF.Min(minY, g.Position.Y);
                closest = MathF.Min(closest, (host.Player.Position - g.Position).Length);
                flat = MathF.Min(flat, (host.Player.Position.Xz - g.Position.Xz).Length);
                if (clips.Count == 0 || clips[^1] != g.Clip) clips.Add(g.Clip);
                if (host.Player.Health < 30) host.Player.Health = 99;   // keep the target standing for the whole run
            });
            Console.WriteLine($"  states: {String.Join(" > ", g.Visited.Take(60))}");
            Console.WriteLine($"  clips: {String.Join(" > ", clips.Take(40))}");
            Console.WriteLine($"  y min {minY:0.00}, closest {closest:0.00}, bites {g.Bites}, beams {g.Beams}, charges {g.Charges}, dealt {g.DamageDealt}");
            Check(g.Visited.Contains("Turn") && g.Visited.Contains("Pursue"), "saw the player: Turn -> Pursue");
            Check(g.Bites + g.Beams + g.Charges >= 2, $"attacked ({g.Bites} bites, {g.Beams} beams, {g.Charges} charges)");
            Check(g.Visited.Any(v => v.StartsWith("PauseBetween")), "paused between attacks");
            Check(minY > spot.Value.Y - 2.5f, $"kept to the floor (lowest {minY:0.00}, spawned at {spot.Value.Y:0.00})");
            Check(closest < 3.5f, $"reached the player ({closest:0.00})");
            Check(flat > 0.3f, $"stopped at the player's side, not inside them ({flat:0.00} across)");
            Check(g.DamageDealt > 0, $"hurt the player ({g.DamageDealt})");

            Console.WriteLine("== damage");
            float hp = g.Hp;
            g.TakeDamage(10, host.Player);
            Check(MathF.Abs(g.Hp - (hp - 10)) < 0.01f && g.Hits == 1, $"Power Beam 10 -> hp {g.Hp}");
            while (g.TailIntact && g.Hits < 40) g.TakeDamage(10, host.Player);
            Check(!g.TailIntact && g.Clip == EchoesGrenchler.KnockOff, $"tail off after {g.Hits} hits, plays {g.Clip}");
            Steps(host, 60 * 6);
            Console.WriteLine($"  tailless 6 s: {g.Bursts} bursts, state {g.Brain.StateName}");
            while (g.Hp > 0 && g.Hits < 200) g.TakeDamage(10, host.Player);
            Check(g.Dying && g.Clip == EchoesGrenchler.Death && Alive(scene, g), $"dead after {g.Hits} hits, plays {g.Clip}");
            Steps(host, 60 * 6);
            Check(!Alive(scene, g), "removed once the death clip ends");
            Console.WriteLine(_fails == 0 ? "== ALL OK" : $"== {_fails} FAILED");
        }
    }
}
