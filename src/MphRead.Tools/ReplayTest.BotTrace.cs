using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // -replaybots <file> <fromSec> <toSec> [every frames=10]: plays a replay and prints each bot's position, speed, held
    // move buttons, target, node and AI state path through a stretch of the match (match review 11-04: bots standing
    // still or hopping in place)
    internal static partial class ReplayTest
    {
        public static void BotTrace(string[] args)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("usage: -replaybots <file> <fromSec> <toSec> [every frames]");
                return;
            }
            float from = Single.Parse(args[2]);
            float to = Single.Parse(args[3]);
            int every = args.Length > 4 ? Int32.Parse(args[4]) : 10;
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            Console.WriteLine($"{file.Settings.Mode} in {file.Settings.Arena}, {file.Steps.Count} steps, local seat {file.Settings.LocalSeat}");
            bool header = false;
            _beforeStep = host =>
            {
                if (!header)
                {
                    header = true;
                    for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
                    {
                        PlayerEntity p = PlayerEntity.Players[i];
                        if (p.LoadFlags.TestFlag(LoadFlags.Active))
                        {
                            string? ch = i < file.Settings.Players.Count ? file.Settings.Players[i].Character : null;
                            Console.WriteLine($"seat {i}: {ch ?? p.Hunter.ToString()}{(ch != null ? $" (on {p.Hunter})" : "")}{(p.IsBot ? $" bot level {p.BotLevel}" : "")}");
                        }
                    }
                }
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (host.Frame == 1 && e is MphRecomp.Arenas.EchoesBreakableEntity br && br.Grate)
                    {
                        Console.WriteLine($"f1 grate {br.Index} box {br.Min} .. {br.Max}");
                    }
                    if (host.Frame == 1 && e is ItemInstanceEntity ie)
                    {
                        Console.WriteLine($"f1 item {ie.ItemType} at {ie.Position}");
                    }
                    if (e is MphRecomp.Arenas.MiningPlazaGateEntity g && (host.Frame == 1 || g.OpenTime is > 0 and < 0.02f))
                    {
                        float[] tr = g.Spot.Tris;
                        Vector3 lo = new(Single.MaxValue), hi = new(Single.MinValue);
                        for (int k = 0; k + 2 < tr.Length; k += 3)
                        {
                            var v = new Vector3(tr[k], tr[k + 1], tr[k + 2]);
                            lo = Vector3.ComponentMin(lo, v);
                            hi = Vector3.ComponentMax(hi, v);
                        }
                        Console.WriteLine($"f{host.Frame} gate {(g.Opened ? "open" : "shut")} tris {tr.Length / 9} box {lo} .. {hi}");
                    }
                }
                float t = host.Frame / 60f;
                if (t < from || t > to || host.Frame % every != 0)
                {
                    return;
                }
                PrintBots(host);
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

        private static void PrintBots(CampaignHost host)
        {
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (!p.IsBot || !p.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                Console.WriteLine(PlayerEntity.HostBotTraceLine(p, (int)host.Frame));
            }
        }

        // -replaybotskf <file> <sidecar> <fromSec> <toSec> [every frames=10]: as -replaybots, but from a device's own keyframes
        // (pulled from <data>/replays/keyframes): the PC's float math can part from the device's (11-06: the Odin's Mining Plaza
        // match parted at step 1086 on the PC), so the trace starts from the device's exact state at the keyframe before
        // fromSec and reloads the next one whenever a step's check fails. The sidecar is read here, past its build check, so run
        // it against the device build's MphRead / MphRecomp.Core dlls (a copy of this folder with those two swapped in): its
        // types and statics must match the writer's; the step checks after a load say whether it read right
        public static void BotTraceKeyframes(string[] args)
        {
            if (args.Length < 5)
            {
                Console.WriteLine("usage: -replaybotskf <file> <sidecar> <fromSec> <toSec> [every frames]");
                return;
            }
            int from = (int)(Single.Parse(args[3]) * 60);
            int to = (int)(Single.Parse(args[4]) * 60);
            int every = args.Length > 5 ? Int32.Parse(args[5]) : 10;
            if (!Setup())
            {
                return;
            }
            ReplayFile file = ReplayFile.Load(args[1]);
            var records = new SortedList<int, (int Anchor, int Raw, byte[] Data)>();
            using (var r = new BinaryReader(File.OpenRead(args[2]), Encoding.UTF8))
            {
                r.ReadBytes(8); // MPHKFS1
                int version = r.ReadInt32();
                string build = r.ReadString();
                int objects = r.ReadInt32(), leaves = r.ReadInt32();
                ulong signature = r.ReadUInt64();
                int ev = r.ReadInt32(), whole = r.ReadInt32();
                Console.WriteLine($"sidecar v{version} build {build} objects {objects} leaves {leaves} signature {signature} every {ev}/{whole}"
                    + $" (this build {ReplayPrep.BuildId})");
                while (r.BaseStream.Position + 16 <= r.BaseStream.Length)
                {
                    int step = r.ReadInt32(), anchor = r.ReadInt32(), raw = r.ReadInt32(), length = r.ReadInt32();
                    if (step < 0 || r.BaseStream.Position + length > r.BaseStream.Length) break;
                    records[step] = (anchor, raw, r.ReadBytes(length));
                }
            }
            Console.WriteLine($"{records.Count} keyframes, last {records.Keys[^1]}");
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: false, viewWidth: 1280, viewHeight: 720);
            try
            {
                if (player.CheckAfterStart() is string after)
                {
                    Console.WriteLine("refused: " + after);
                    return;
                }
                SimSnapshot.KeyframeBase kbase = ReplayCheckpoints.KeyframeBase(host);
                Console.WriteLine($"this walk: objects {kbase.ObjectCount} leaves {kbase.LeafCount} signature {kbase.Signature}");
                var loader = new SimSnapshot.KeyframeLoader(kbase);
                // (the device's step-0 walk hashes differently from the PC's with the same object / leaf counts: its own
                // signature, at byte 24 of each keyframe, is swapped for this walk's so the loader takes it)
                byte[] Unpack(int step)
                {
                    byte[] d = ReplayPrep.Decompress(records[step].Data, records[step].Raw);
                    BitConverter.TryWriteBytes(d.AsSpan(24), kbase.Signature);
                    return d;
                }
                bool Seek(int target)
                {
                    int at = -1;
                    foreach (int k in records.Keys)
                    {
                        if (k > target) break;
                        if (records[k].Anchor < 0 || records.ContainsKey(records[k].Anchor)) at = k;
                    }
                    if (at < 0)
                    {
                        Console.WriteLine($"no keyframe at or before {target}");
                        return false;
                    }
                    int anchor = records[at].Anchor;
                    string? why = anchor >= 0 ? loader.Load(Unpack(anchor)) : null;
                    why ??= loader.Load(Unpack(at));
                    if (why != null)
                    {
                        Console.WriteLine($"keyframe {at} refused: {why}");
                        return false;
                    }
                    foreach (PlayerEntity p in PlayerEntity.Players) p.HostAfterRestore();
                    player.Rewind(at);
                    Console.WriteLine($"-- keyframe {at}");
                    return true;
                }
                if (args.Length > 6 && args[6] == "types")
                {
                    foreach (int k in new[] { 0, records.Keys[0] })
                    {
                        if (!records.ContainsKey(k)) continue;
                        var tr = new BinaryReader(new MemoryStream(ReplayPrep.Decompress(records[k].Data, records[k].Raw)));
                        tr.ReadBytes(32);
                        int n = tr.Read7BitEncodedInt();
                        Console.WriteLine($"keyframe {k}: {n} types");
                        for (int t = 0; t < n; t++) Console.WriteLine("  " + tr.ReadString());
                    }
                    return;
                }
                if (!Seek(from)) return;
                bool exact = true;
                int fails = 0;
                while (player.Position < to && !host.Ended && player.TryNext(out CampaignInput input))
                {
                    if (host.Frame >= from && host.Frame % every == 0) PrintBots(host);
                    host.Step(input);
                    if (exact && !player.Check(host.Scene))
                    {
                        exact = false;
                        fails++;
                        Console.WriteLine($"-- differs at step {player.FailedAt}");
                    }
                    if (!exact && records.ContainsKey(player.Position))
                    {
                        if (!Seek(player.Position)) return;
                        exact = true;
                    }
                }
                Console.WriteLine($"done at step {player.Position}, {fails} stretches parted from the device");
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
