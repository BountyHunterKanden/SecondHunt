using System;
using System.Linq;
using System.Reflection;
using MphRecomp.Arenas;
using MphRead.Entities;

namespace MphRead
{
    // Prime 1 Main Quarry's stacked crates (CrateSpot.Above, owner 2026-11-20 "no floating crates"):
    //   -quarrystacktest import=<folder holding arenas/P1_MainQuarry>: a Battle from that import (its one bot shoots crates too); breaks one lower crate
    //       of each stack. PASS = the crate on it breaks the same frame, a top crate broken alone leaves the lower ones up,
    //       no top crate is ever up while a crate under it is down, and each top crate grows back.
    public static class QuarryStackTest
    {
        public static void Run(string[] args)
        {
            string? import = args.Skip(1).FirstOrDefault(a => a.StartsWith("import="))?[7..];
            if (import == null)
            {
                Console.WriteLine("usage: -quarrystacktest import=<folder>");
                return;
            }
            if (!ToolPaths.UseDesktopExtraction()) return;
            EchoesArena.ImportRoot = import;
            EchoesArena.EnsureRegistered();
            Console.WriteLine($"registered from the import: {EchoesArena.RegisterImported(import)}");
            EchoesArena.Def def = EchoesArena.Find("P1_MainQuarry")!;
            var settings = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Battle, def.Room, Hunter.Samus, 1, 0);
            settings.TimeLimitSeconds = 600;
            using var host = MphRecomp.Campaign.CampaignHost.StartMatch(settings);
            var crates = new System.Collections.Generic.List<MiningPlazaCrateEntity>();
            foreach (EntityBase e in host.Scene.Entities) if (e is MiningPlazaCrateEntity c) crates.Add(c);
            var tops = crates.Where(c => c.UnderMe.Count > 0).ToList();
            Console.WriteLine($"{crates.Count} crates, {tops.Count} on others: "
                + string.Join(", ", tops.Select(t => $"{t.Spot.Id:x8} on {string.Join("+", t.UnderMe.Select(u => u.Spot.Id.ToString("x8")))}")));
            MethodInfo brk = typeof(MiningPlazaCrateEntity).GetMethod("Break", BindingFlags.Instance | BindingFlags.NonPublic)!;
            bool pass = tops.Count == 2;
            int floating = 0;
            var was = crates.Select(c => c.Up).ToArray();
            var grewBack = new System.Collections.Generic.HashSet<MiningPlazaCrateEntity>();
            for (int f = 0; f < 60 * 80; f++)
            {
                for (int i = 0; i < crates.Count; i++)
                {
                    if (crates[i].Up && !was[i] && tops.Contains(crates[i])) grewBack.Add(crates[i]);
                    if (crates[i].Up != was[i]) Console.WriteLine($"  frame {f}: {crates[i].Spot.Id:x8} {(crates[i].Up ? "up" : "down")}");
                    was[i] = crates[i].Up;
                }
                if (f == 60)
                {
                    foreach (MiningPlazaCrateEntity t in tops)
                    {
                        MiningPlazaCrateEntity low = t.UnderMe[0];
                        brk.Invoke(low, new object?[] { null });
                        bool ok = !low.Up && !t.Up && t.UnderMe.Skip(1).All(u => u.Up);
                        Console.WriteLine($"broke {low.Spot.Id:x8}: top {t.Spot.Id:x8} {(t.Up ? "STILL UP" : "broke")}, other lower crates up: {t.UnderMe.Skip(1).All(u => u.Up)} {(ok ? "ok" : "FAIL")}");
                        pass &= ok;
                    }
                }
                if (f == 60 * 40)
                {
                    // the regrow is done: a top crate alone
                    MiningPlazaCrateEntity? t = tops.FirstOrDefault(x => x.Up && x.UnderMe.All(u => u.Up));
                    if (t == null) { Console.WriteLine("40 s: no whole stack up to break a top crate alone FAIL"); pass = false; }
                    else
                    {
                        brk.Invoke(t, new object?[] { null });
                        bool ok = !t.Up && t.UnderMe.All(u => u.Up);
                        Console.WriteLine($"40 s: broke top {t.Spot.Id:x8} alone: lower ones up {t.UnderMe.All(u => u.Up)} {(ok ? "ok" : "FAIL")}");
                        pass &= ok;
                    }
                }
                foreach (MiningPlazaCrateEntity t in tops)
                {
                    if (t.Up && t.UnderMe.Any(u => !u.Up)) floating++;
                }
                host.Step(new MphRecomp.Campaign.CampaignInput { SelectWeapon = BeamType.None });
            }
            Console.WriteLine($"frames with a top crate up over a broken one: {floating}");
            Console.WriteLine($"top crates grown back: {grewBack.Count} of {tops.Count}");
            pass &= floating == 0 && grewBack.Count == tops.Count;
            Console.WriteLine(pass ? "PASS" : "FAIL");
        }
    }
}
