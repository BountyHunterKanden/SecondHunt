using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead.Entities;
using MphRecomp.Replay;

namespace MphRead
{
    // -replayevents <file>: plays a replay headless and logs each hit (attacker, beam, flags, range), death (kills, streaks),
    // HUD message the watched seat got, hit-direction indicator on the watched seat, cloak and weapon change, so a player's
    // report of a match can be matched to what happened (owner 2026-10-31, the Fault Line online matches)
    internal static partial class ReplayTest
    {
        public static void Events(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replayevents <file>");
                return;
            }
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            var s = file.Settings;
            Console.WriteLine($"{s.Mode} in {s.Arena}, build {file.Build}, net '{file.Net}', own seat {file.OwnSeat}, local {s.LocalSeat},"
                + $" client {s.IsClient}, served {s.ServedHost}, net events {file.NetEvents.Count}, shot checks {file.ShotChecks.Count}");
            for (int i = 0; i < s.Players.Count; i++)
            {
                var p = s.Players[i];
                Console.WriteLine($"  seat {i}: {p.Nickname} {p.Hunter} bot={p.Bot} remote={p.Remote} team={p.Team} skin={p.Skin}"
                    + $" char={p.Character} recolor={p.Recolor}");
            }
            foreach (var o in file.Options)
            {
                Console.WriteLine($"  option {o.Key}={o.Value}");
            }
            // the hit claims the host took (its own copy of the shot missed; the shot's beam flies on in the replay)
            foreach (ReplayNetEvent e in file.NetEvents)
            {
                if (e.Kind == ReplayNetKind.Hit && e.Input != null)
                {
                    var c = MphRecomp.Campaign.CampaignHost.ReadClaimRecord(e.Seat, e.Input);
                    Console.WriteLine($"  CLAIM step {e.Step,6} ({e.Step / 60f,6:0.0}s) {c}");
                }
            }
            int watch = file.ViewerSeat;
            const BindingFlags inst = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo hits = typeof(PlayerEntity).GetField("_damageHits", inst)!;
            FieldInfo cloak = typeof(PlayerEntity).GetField("_cloakTimer", inst)!;
            FieldInfo queue = typeof(PlayerEntity).GetField("_hudMessageQueue", BindingFlags.NonPublic | BindingFlags.Static)!;
            Type msgType = typeof(PlayerEntity).GetNestedType("HudMessage", BindingFlags.NonPublic)!;
            PropertyInfo life = msgType.GetProperty("Lifetime")!;
            PropertyInfo text = msgType.GetProperty("Text")!;
            int[] lastHealth = new int[8];
            int[] lastWeapon = Enumerable.Repeat(-99, 8).ToArray();
            int[] lastCloak = new int[8];
            float[] lastLife = new float[20];
            ushort[] lastHitTime = new ushort[8];
            bool first = true;
            // where each beam hit struck, against the victim's head line (MaxPickupHeight - 0.3 above their feet)
            PlayerEntity.HitNote = (beam, target, point, head) =>
            {
                float line = Fixed.ToFloat(target.Values.MaxPickupHeight) - 0.3f;
                Console.WriteLine($"    note hit on {target.SlotIndex} beam {beam.Beam} y-above-feet {point.Y - target.Position.Y:0.000}"
                    + $" headline {line:0.000} head {head} alt {target.IsAltForm} dmg {beam.Damage:0.##}/{beam.HeadshotDamage:0.##}");
            };
            _beforeStep = host =>
            {
                var players = PlayerEntity.Players;
                int count = s.Players.Count;
                string Who(int i) => i < 0 || i >= count ? $"{i}:-" : $"{i}:{GameState.Nicknames[i]}({players[i].Hunter})";
                string t = $"f{host.Frame,6} ({host.Frame / 60f,6:0.0}s)";
                if (first)
                {
                    first = false;
                    for (int i = 0; i < count; i++)
                    {
                        lastHealth[i] = players[i].Health;
                    }
                }
                for (int i = 0; i < count; i++)
                {
                    PlayerEntity p = players[i];
                    if ((int)p.CurrentWeapon != lastWeapon[i])
                    {
                        Console.WriteLine($"  {t} {Who(i)} weapon {p.CurrentWeapon}");
                        lastWeapon[i] = (int)p.CurrentWeapon;
                    }
                    int c = (ushort)cloak.GetValue(p)!;
                    bool cloaking = p.Flags2.TestFlag(PlayerFlags2.Cloaking);
                    if ((c > 0) != (lastCloak[i] > 0))
                    {
                        Console.WriteLine($"  {t} {Who(i)} cloak timer {lastCloak[i]} -> {c} (cloaking {cloaking})");
                    }
                    lastCloak[i] = c;
                    if (p.Health < lastHealth[i])
                    {
                        int a = p.NetLastAttacker;
                        string range = a >= 0 && a < count ? $" range {(players[a].Position - p.Position).Length:0.0}" : "";
                        if (i == watch && a >= 0 && a < count)
                        {
                            // where the hit could have come from: the victim's own turret/drone (hit there), the attacker's
                            PlayerEntity at = players[a];
                            range += $" alt {p.IsAltForm}";
                            if (p.Flags2.TestFlag(PlayerFlags2.Halfturret))
                            {
                                range += $" myturret@{(p.Halfturret.Position - p.Position).Length:0.0} from-attacker {(at.Position - p.Halfturret.Position).Length:0.0}";
                            }
                            if (at.Flags2.TestFlag(PlayerFlags2.Halfturret))
                            {
                                range += $" attackerturret->me {(at.Halfturret.Position - p.Position).Length:0.0}";
                            }
                        }
                        Console.WriteLine($"  {t} HIT {Who(i)} {lastHealth[i]} -> {p.Health} by {Who(a)} beam {p.NetLastBeam}"
                            + $"{(p.NetLastDrone ? " drone" : "")} flags [{p.NetLastFlags}]{range}"
                            + (p.Health == 0 ? $"  DEATH kills [{String.Join(",", GameState.Kills.Take(count))}]"
                                + $" streaks [{String.Join(",", GameState.KillStreak.Take(count))}]" : ""));
                    }
                    lastHealth[i] = p.Health;
                }
                // the watched seat's hit-direction indicator
                PlayerEntity me = players[watch];
                var arr = (Array)hits.GetValue(me)!;
                for (int k = 0; k < arr.Length; k++)
                {
                    object entry = arr.GetValue(k)!;
                    var tuple = (ITuple)entry;
                    var who = (PlayerEntity?)tuple[1];
                    ushort tm = (ushort)tuple[2]!;
                    if (tm > lastHitTime[k])
                    {
                        string range = who != null ? $" range {(who.Position - me.Position).Length:0.0}" : "";
                        Console.WriteLine($"  {t} INDICATOR on {Who(watch)} from {(who == null ? "none" : Who(who.SlotIndex))}"
                            + $" src {tuple[0]}{range}");
                    }
                    lastHitTime[k] = tm;
                }
                var msgs = (IList)queue.GetValue(null)!;
                for (int k = 0; k < msgs.Count; k++)
                {
                    float l = (float)life.GetValue(msgs[k])!;
                    if (l > lastLife[k] + 0.0001f)
                    {
                        var chars = (char[])text.GetValue(msgs[k])!;
                        int end = Array.IndexOf(chars, '\0');
                        Console.WriteLine($"  {t} MSG \"{new string(chars, 0, end < 0 ? chars.Length : end)}\"");
                    }
                    lastLife[k] = l;
                }
            };
            try
            {
                Run r = Replay(file, View.NoDraw, types: false, reference: null, stopOnFail: false);
                Console.WriteLine(r.Verdict());
            }
            finally
            {
                _beforeStep = null;
            }
        }
    }
}
