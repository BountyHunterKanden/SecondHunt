using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MphRecomp.Import.Beyond
{
    public enum ChprSlot { Rot, Scale, Trans }

    // An animation record decoded to tracks (mp4anim_lib.Character.decode): per (joint, slot) either an animated track
    // with its keys or a constant from constant pool 0, plus the visibility bools and each stream's status.
    public sealed class ChprDecodedAnim
    {
        public string Name = "";
        public int Index, Set, Frames;
        public double RateFactor;                                  // the CompStream's f32 @12
        public readonly Dictionary<(string Joint, ChprSlot Slot), (ChprAnimCodec.Track Track, List<ChprAnimCodec.Key> Keys)> Channels = new();
        public readonly Dictionary<(string Joint, ChprSlot Slot), double[]> Consts = new();
        public readonly List<ChprAnimCodec.StreamStatus> Status = new();
        public readonly List<string> BoolOrder = new();            // first-set order
        public readonly Dictionary<string, int> Bools = new();

        public bool Ok => Status.All(s => s.Ok && s.RestZero);
        public string FailMessage => String.Join("; ", Status.Where(s => !s.Ok).Select(s => s.Msg));
    }

    // ok: Animation set. fail: a stream did not decode cleanly (Message = the streams' messages). error: the record could
    // not be decoded at all (not a CompStream, no table layout, ...). Same statuses and messages as index.json.
    public sealed class ChprAnimResult
    {
        public int Index;
        public string Name = "";
        public string Status = "";
        public string Message = "";
        public ChprAnimation? Animation;
        public bool Ok => Status == "ok";
    }

    // A Beyond character project's animations and skeleton from its CHPR bytes alone (no chpr.json, no dumper DLLs).
    // Port of brawl_extract/rig_work/gun/mp4anim/py (mp4anim_lib.Character, mp4pose.Rig, mp4anim_export.export_anim).
    //   var ch = ChprCharacter.Load(bytes);
    //   ChprAnimResult r = ch.Decode(ch.FindAnim("fp_idle_samus"));      // r.Animation: per-frame local TRS on the rest pose
    //   ch.Skeleton.RestWorld[i]                                          // bind matrices (IBM = inverse)
    public sealed class ChprCharacter
    {
        // ASSUMPTION (as the Python's): stored frame rate = BaseFps x the CompStream's rate factor (1.0 or 0.5)
        public const double BaseFps = 30.0;

        public Chpr Chpr = null!;
        public ChprSkeleton Skeleton = null!;
        public string[] AnimNames = Array.Empty<string>();
        public double[] Pool0 = Array.Empty<double>();
        readonly Dictionary<string, int> _byName = new();

        public static ChprCharacter Load(Stream s) => Load(Chpr.Read(s));

        public static ChprCharacter Load(byte[] chpr) => Load(Chpr.Parse(chpr));

        public static ChprCharacter Load(Chpr c)
        {
            var ch = new ChprCharacter { Chpr = c, Skeleton = ChprSkeleton.Build(c) };
            ch.AnimNames = Enumerable.Range(0, c.Anims.Count).Select(c.AnimName).ToArray();
            for (int i = 0; i < ch.AnimNames.Length; i++) ch._byName[ch.AnimNames[i]] = i;
            if (c.ConstPools.Count == 0) throw new InvalidDataException("CHPR: no constant pool");
            ch.Pool0 = c.ConstPools[0].Values.Select(v => float.IsFinite(v) ? (double)v : double.NaN).ToArray();
            return ch;
        }

        public int FindAnim(string name) => _byName.TryGetValue(name, out int i) ? i : -1;

        public ChprAnimCodec.RecordHeader Header(int i) => ChprAnimCodec.RecordHeader.Parse(Chpr.Data, Chpr.Anims[i].Offset);

        // Python slice P[a:a+n] (clamped at the end)
        double[] PoolSlice(int a, int n)
        {
            int m = Math.Max(0, Math.Min(n, Pool0.Length - a));
            var r = new double[m];
            Array.Copy(Pool0, a, r, 0, m);
            return r;
        }

        public ChprDecodedAnim DecodeRaw(int i)
        {
            var (off, sz) = Chpr.Anims[i];
            var h = ChprAnimCodec.RecordHeader.Parse(Chpr.Data, off);
            if (h.Type != 0) throw new ChprAnimException($"anim type {h.Type} (not a CompStream) is not handled");
            var cs = ChprAnimCodec.CompStream.Parse(Chpr.Data, off, sz);
            List<List<ChprAnimCodec.Track>> lays = ChprAnimCodec.ParseDescriptors(cs.S2, cs.Streams);
            var o = new ChprDecodedAnim { Name = AnimNames[i], Index = i, Frames = cs.Frames, RateFactor = cs.RateFactor, Set = h.Set };
            for (int si = 0; si < cs.Streams.Count; si++)
            {
                ChprAnimCodec.Stream st = cs.Streams[si];
                List<ChprAnimCodec.Track> lay = lays[si];
                List<ChprAnimCodec.Key>[]? decoded = null;
                if (st.TableStart > st.BitStart && lay.Count > 0)
                {
                    var (keys, status) = ChprAnimCodec.DecodeStream(cs.D, st.BitStart, st.TableStart - st.BitStart, lay, cs.Frames);
                    status.Stream = si;
                    o.Status.Add(status);
                    decoded = keys;
                }
                var clsOff = new Dictionary<int, int>();
                int acc = 0;
                foreach (int c in lay.Select(t => t.Class).Distinct().OrderBy(c => c))
                {
                    clsOff[c] = acc;
                    acc += lay.Count(t => t.Class == c);
                }
                foreach (ChprAnimCodec.Block blk in st.Blocks)
                {
                    if (blk.Kind is ChprAnimCodec.BlockKind.Joints or ChprAnimCodec.BlockKind.Root)
                    {
                        bool joints = blk.Kind == ChprAnimCodec.BlockKind.Joints;
                        List<string> mem = joints ? Skeleton.GroupMembers(blk.Group) : Enumerable.Repeat("root.move", blk.Records.Length).ToList();
                        ChprSlot[] slots = joints ? new[] { ChprSlot.Rot, ChprSlot.Scale, ChprSlot.Trans } : new[] { ChprSlot.Rot, ChprSlot.Trans };
                        for (int r = 0; r < mem.Count && r < blk.Records.Length; r++)
                        {
                            string n = mem[r];
                            ushort[] rec = blk.Records[r];
                            for (int k = 0; k < slots.Length && k < rec.Length; k++)
                            {
                                ChprSlot slot = slots[k];
                                int v = rec[k];
                                if ((v & 0x8000) != 0)
                                {
                                    int cl = (v >> 10) & 31;
                                    int ti = (clsOff.TryGetValue(cl, out int co) ? co : 0) + (v & 0x3ff);
                                    if (decoded == null || ti >= lay.Count) continue;
                                    o.Channels[(n, slot)] = (lay[ti], decoded[ti]);
                                }
                                else if (slot == ChprSlot.Rot)
                                {
                                    o.Consts[(n, slot)] = PoolSlice(v, 4);
                                }
                                else if ((v & 0x4000) != 0)
                                {
                                    // ONE pool value: a uniform scale delta
                                    int a = v & 0x3fff;
                                    if (a >= Pool0.Length) throw new ChprAnimException($"index {a} is out of bounds for axis 0 with size {Pool0.Length}");
                                    o.Consts[(n, slot)] = new[] { Pool0[a], Pool0[a], Pool0[a] };
                                }
                                else
                                {
                                    o.Consts[(n, slot)] = PoolSlice(v, 3);
                                }
                            }
                        }
                    }
                    else if (blk.Kind == ChprAnimCodec.BlockKind.Bools)
                    {
                        foreach (ushort v in blk.Values)
                        {
                            string key = Skeleton.AnimNodes.TryGetValue(v, out string? nm) ? nm : v.ToString();
                            if (!o.Bools.ContainsKey(key)) o.BoolOrder.Add(key);
                            o.Bools[key] = blk.Value;
                        }
                    }
                }
            }
            return o;
        }

        // mp4anim_export.export_anim: ok / fail / error, as index.json records them
        public ChprAnimResult Decode(int i)
        {
            var res = new ChprAnimResult { Index = i, Name = AnimNames[i] };
            try
            {
                ChprDecodedAnim a = DecodeRaw(i);
                if (!a.Ok)
                {
                    res.Status = "fail";
                    res.Message = a.FailMessage;
                    return res;
                }
                res.Animation = ChprPose.Export(Skeleton, a);
                res.Status = "ok";
            }
            catch (Exception e) when (e is ChprAnimException or InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                res.Status = "error";
                res.Message = e.Message.Length > 200 ? e.Message.Substring(0, 200) : e.Message;
            }
            return res;
        }
    }
}
