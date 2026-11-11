using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRead
{
    // Weavel's split (the Halfturret takes half his health) isn't a hit, owner report 2026-10-31 (the Fault Line online
    // matches: WEABOT's health box on the owner's HUD with nobody near him). The host's last hit stayed the hit before the
    // split, so a match client that took the drop as damage (its split check missed) credited that attacker again: their
    // HUD showed the opponent box. Headless:
    //   -splitcredittest
    //   - host: seat 0 (Samus) hits seat 1 (Weavel), seat 1 splits: his last hit is cleared (no attacker in the snapshot)
    //   - match client: a health drop applied with no attacker doesn't open seat 0's opponent box for seat 1; with the
    //     old snapshot (attacker seat 0) it does (the test sees the difference)
    internal static class SplitCreditTest
    {
        public static void Run()
        {
            Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, TimeLimitSeconds = 60 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Weavel, Nickname = "WEABOT", Bot = true, BotLevel = 0 });
            const BindingFlags inst = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo opponent = typeof(PlayerEntity).GetField("_opponentIndex", inst)!;
            FieldInfo opponentTimer = typeof(PlayerEntity).GetField("_opponentHealthbarTimer", inst)!;
            MethodInfo enterAlt = typeof(PlayerEntity).GetMethod("EnterAltForm", inst)!;
            var errors = new List<string>();

            (PlayerEntity, PlayerEntity) Start()
            {
                CampaignHost host = CampaignHost.StartMatch(settings);
                PlayerEntity a = PlayerEntity.Players[0];
                PlayerEntity b = PlayerEntity.Players[1];
                for (int f = 0; f < 600 && (a.Health == 0 || b.Health == 0); f++)
                {
                    host.Step(new CampaignInput { Buttons = f % 2 == 0 ? CampaignButtons.Shoot : 0 });
                }
                if (a.Health == 0 || b.Health == 0)
                {
                    errors.Add("seats not spawned");
                }
                return (a, b);
            }

            // host
            (PlayerEntity p0, PlayerEntity p1) = Start();
            BeamProjectileEntity beam = p0.EquipInfo.Beams![0];
            beam.Owner = p0;
            beam.Beam = BeamType.PowerBeam;
            p1.TakeDamage(10, DamageFlags.IgnoreInvuln, null, beam);
            int hitBy = p1.NetLastAttacker;
            int before = p1.Health;
            enterAlt.Invoke(p1, null);
            bool split = p1.Flags2.TestFlag(PlayerFlags2.Halfturret);
            Console.WriteLine($"host: hit by seat {hitBy}, split {split}, health {before} -> {p1.Health}, last attacker now {p1.NetLastAttacker}"
                + $", snapshot LastAttacker {p1.NetCapture().LastAttacker}");
            if (hitBy != 0)
            {
                errors.Add($"host: the hit wasn't recorded (attacker {hitBy})");
            }
            if (!split || p1.Health >= before)
            {
                errors.Add("host: no split");
            }
            if (p1.NetLastAttacker != -1 || p1.NetCapture().LastAttacker != -1)
            {
                errors.Add($"host: the split kept seat {p1.NetLastAttacker} as the last attacker");
            }

            // match client: a drop the split check misses (no alt flag), with the new snapshot and the old one
            foreach (sbyte attacker in new sbyte[] { -1, 0 })
            {
                (p0, p1) = Start();
                opponent.SetValue(p0, -1);
                opponentTimer.SetValue(p0, 0f);
                PlayerNetState s = p1.NetCapture();
                s.Health = (ushort)(p1.Health / 2);
                s.LastAttacker = attacker;
                s.LastBeam = 0;
                s.LastFlags = 0;
                PlayerEntity.NetClient = true;
                try
                {
                    p1.NetApply(s, pose: false);
                }
                finally
                {
                    PlayerEntity.NetClient = false;
                }
                int box = (int)opponent.GetValue(p0)!;
                Console.WriteLine($"client, snapshot attacker {attacker}: seat 1 health {p1.Health}, seat 0's opponent box {box}");
                if (attacker == -1 && box != -1)
                {
                    errors.Add("client: a drop with no attacker opened seat 0's opponent box");
                }
                if (attacker == 0 && box != 1)
                {
                    errors.Add("client: the old snapshot (attacker seat 0) didn't open the box (the test can't see the bug)");
                }
            }

            foreach (string e in errors)
            {
                Console.WriteLine("  " + e);
            }
            Console.WriteLine(errors.Count == 0 ? "splitcredittest PASS" : "splitcredittest FAIL");
        }
    }
}
