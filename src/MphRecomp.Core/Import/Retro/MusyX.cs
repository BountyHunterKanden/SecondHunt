using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Retro
{
    // Prime 1's sound effects (MusyX on the GameCube): a sound id the game passes (an EVNT sound point's, a CRSC's) goes
    // through the ATBL sound lookup (AudioGrp.pak; Metaforce CSfxManager::TranslateSFXID) to a MusyX sfx id, which an AGSC
    // audio group's project table maps to a SoundMacro in its pool; the macro starts a sample of its sample directory
    // (DSP-ADPCM in its sample data) at a note, at a volume, and fades / stops it on its own clock. Render plays one sfx's
    // macro offline into mono PCM at its sample's rate, so the app can play it as a plain one-shot.
    //   AGSC (MP1): "Audio/\0", "<group>\0", then pool, proj, samp, sdir, each a BE u32 length + its bytes.
    //   AGSC (Echoes): u32 version 1, "<group>\0", u16 group id, the four BE u32 lengths (pool, proj, sdir, samp), then
    //     the four blocks in that order; the blocks are MP1's (Shooting Gallery's turret sounds, Arenas/EchoesTurret.cs).
    //   proj: groups chained by their end offset (0xFFFFFFFF ends): u32 end, u16 id, u16 type; an sfx group (type 1) has its
    //     table's offset at +28: u16 count, u16 pad, then 10-byte entries u16 sfx id, u16 macro id, u8 priority,
    //     u8 max voices, u8 velocity, u8 pan, u8 key, u8 pad.
    //   pool: u32 macros offset (+ tables, keymaps, layers); macros: u32 size (header included), u16 id, u16 pad, then
    //     8-byte commands, the list ended by size 0xFFFFFFFF.
    //   sdir: 32-byte entries until id 0xFFFF: u16 id, u16 pad, u32 offset into samp, u32, u8 root key, u8, u16 rate,
    //     u32 samples (low 24 bits), u32 loop start, u32 loop length, u32 ADPCM block offset (into sdir): u16 bytes per
    //     frame, u8 ps, u8 loop ps, s16 loop hist2, s16 loop hist1, s16 coefficients[16].
    // A command is two BE words whose bytes, each word reversed, are the fields in order: [op, a, b, c, d, e, f, g]
    // (amuse's SoundMacro reader, https://gitlab.axiodl.com/AxioDL/amuse, MIT, Antidote / Jackoalan). Checked on the
    // owner's disc (GM8E01) against the drone's four sounds: the op is each command's 4th byte, StartSample's sample id
    // the two before it and an sdir id.
    public static class MusyX
    {
        // ATBL: BE u32 count, then s16 per game sound id (negative = none)
        public static short[] Lookup(byte[] atbl)
        {
            int n = (int)Be.U32(atbl, 0);
            var t = new short[n];
            for (int i = 0; i < n; i++) t[i] = Be.I16(atbl, 4 + i * 2);
            return t;
        }

        // a game sound id through the lookup: its MusyX sfx id, or -1
        public static int Translate(short[] lookup, uint id) => id < lookup.Length ? lookup[id] : -1;

        public static void WriteWav(string path, short[] pcm, int rate)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + pcm.Length * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((short)1);      // PCM
            w.Write((short)1);      // mono
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length * 2);
            foreach (short s in pcm) w.Write(s);
        }

        // a mono PCM16 WAV as WriteWav makes it (null for anything else)
        public static short[]? ReadWav(string path, out int rate)
        {
            rate = 0;
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
            int o = 12;
            short channels = 0, bits = 0;
            while (o + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, o, 4);
                int len = BitConverter.ToInt32(b, o + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(b, o + 10);
                    rate = BitConverter.ToInt32(b, o + 12);
                    bits = BitConverter.ToInt16(b, o + 22);
                }
                else if (id == "data")
                {
                    if (channels != 1 || bits != 16) return null;
                    int n = Math.Min(len, b.Length - o - 8) / 2;
                    var pcm = new short[n];
                    Buffer.BlockCopy(b, o + 8, pcm, 0, n * 2);
                    return pcm;
                }
                o += 8 + len + (len & 1);
            }
            return null;
        }
    }

    public sealed class Agsc
    {
        public sealed record Sfx(ushort Id, ushort Macro, byte Priority, byte MaxVoices, byte Velocity, byte Pan, byte Key);

        public sealed record Sample(ushort Id, uint Offset, byte RootKey, int Rate, int Count, int LoopStart, int LoopLength, short[] Coefs);

        public string Group = "";
        public readonly Dictionary<ushort, Sfx> Sounds = new();
        public readonly Dictionary<ushort, byte[]> Macros = new();
        public readonly Dictionary<ushort, Sample> Samples = new();
        byte[] _samp = Array.Empty<byte>();
        readonly Dictionary<ushort, short[]> _pcm = new();

        public static Agsc Parse(byte[] b)
        {
            var a = new Agsc();
            var secs = new byte[4][];
            byte[] pool, proj, sdir;
            if (Be.U32(b, 0) == 1)
            {
                // Echoes: the lengths first, sdir before samp
                a.Group = Be.CString(b, 4, out int e);
                int o = e + 1 + 2;
                int at = o + 16;
                for (int i = 0; i < 4; i++)
                {
                    int len = (int)Be.U32(b, o + 4 * i);
                    secs[i] = Be.Slice(b, at, len);
                    at += len;
                }
                pool = secs[0]; proj = secs[1]; sdir = secs[2];
                a._samp = secs[3];
            }
            else
            {
                Be.CString(b, 0, out int e);
                a.Group = Be.CString(b, e + 1, out e);
                int o = e + 1;
                for (int i = 0; i < 4; i++)
                {
                    int len = (int)Be.U32(b, o);
                    secs[i] = Be.Slice(b, o + 4, len);
                    o += 4 + len;
                }
                pool = secs[0]; proj = secs[1]; sdir = secs[3];
                a._samp = secs[2];
            }
            for (int p = 0; p + 8 <= proj.Length;)
            {
                uint end = Be.U32(proj, p);
                if (end == UInt32.MaxValue) break;
                if (Be.U16(proj, p + 6) == 1)
                {
                    int t = (int)Be.U32(proj, p + 28);
                    int n = Be.U16(proj, t);
                    for (int i = 0; i < n; i++)
                    {
                        int q = t + 4 + i * 10;
                        var s = new Sfx(Be.U16(proj, q), Be.U16(proj, q + 2), proj[q + 4], proj[q + 5], proj[q + 6], proj[q + 7], proj[q + 8]);
                        a.Sounds[s.Id] = s;
                    }
                }
                if (end <= p) break;
                p = (int)end;
            }
            for (int p = (int)Be.U32(pool, 0); p + 8 <= pool.Length;)
            {
                uint size = Be.U32(pool, p);
                if (size == UInt32.MaxValue || size < 8) break;
                a.Macros[Be.U16(pool, p + 4)] = Be.Slice(pool, p + 8, (int)size - 8);
                p += (int)size;
            }
            for (int p = 0; p + 32 <= sdir.Length && Be.U16(sdir, p) != 0xFFFF; p += 32)
            {
                int adpcm = (int)Be.U32(sdir, p + 28);
                var coefs = new short[16];
                for (int i = 0; i < 16; i++) coefs[i] = Be.I16(sdir, adpcm + 8 + i * 2);
                var s = new Sample(Be.U16(sdir, p), Be.U32(sdir, p + 4), sdir[p + 12], Be.U16(sdir, p + 14),
                    (int)(Be.U32(sdir, p + 16) & 0xFFFFFF), (int)Be.U32(sdir, p + 20), (int)Be.U32(sdir, p + 24), coefs);
                a.Samples[s.Id] = s;
            }
            return a;
        }

        // the whole sample decoded (cached)
        public short[] Pcm(Sample s)
        {
            if (_pcm.TryGetValue(s.Id, out short[]? done)) return done;
            var pcm = new short[s.Count];
            var state = new DspAdpcm.ChannelState();
            Span<short> frame = stackalloc short[DspAdpcm.SamplesPerFrame];
            for (int f = 0, at = 0; at < s.Count; f++, at += DspAdpcm.SamplesPerFrame)
            {
                int o = (int)s.Offset + f * DspAdpcm.FrameBytes;
                if (o + DspAdpcm.FrameBytes > _samp.Length) break;
                DspAdpcm.DecodeFrame(_samp.AsSpan(o, DspAdpcm.FrameBytes), s.Coefs, ref state, frame);
                frame.Slice(0, Math.Min(DspAdpcm.SamplesPerFrame, s.Count - at)).CopyTo(pcm.AsSpan(at));
            }
            _pcm[s.Id] = pcm;
            return pcm;
        }

        // What Render did, for the tests and the import log.
        public sealed class Rendered
        {
            public short[] Pcm = Array.Empty<short>();
            public int Rate;
            public readonly List<string> Steps = new();
            public readonly List<int> Skipped = new();   // ops played as no-ops
            // a looping sfx: one turn of the loop its voice is left in when the macro ends, at the voice's pitch and
            // gains (seamless played over and over; empty for a one-shot)
            public short[] Loop = Array.Empty<short>();
        }

        const float MaxSeconds = 10;

        // One sfx's macro played offline, mono at its first sample's rate. Plays: End, Stop, WaitTicks/WaitMs (fixed,
        // until the sample's end, or both), Loop-free macros' notes (SetNote, AddNote, RndNote at its low note),
        // ScaleVolume, Envelope / FadeIn (straight-line, no curve table), StartSample (its loop), StopSample. Anything
        // else (panning, pitch wheel range, age counters, priorities) has no say over a one-shot's sound and is skipped.
        public Rendered? Render(ushort sfxId)
        {
            if (!Sounds.TryGetValue(sfxId, out Sfx? sfx) || !Macros.TryGetValue(sfx.Macro, out byte[]? cmds)) return null;
            var r = new Rendered();
            Sample? first = null;
            for (int c = 0; c + 8 <= cmds.Length && first == null; c += 8)
            {
                if (cmds[c + 3] == 0x10) Samples.TryGetValue((ushort)((cmds[c + 1] << 8) | cmds[c + 2]), out first);
            }
            if (first == null) return null;
            r.Rate = first.Rate;
            int rate = r.Rate;
            var outp = new List<float>();
            float note = sfx.Key;
            float vol = sfx.Velocity / 127f;
            float env = 1, envFrom = 1, envTo = 1;
            int envAt = 0, envLen = 0;
            Sample? s = null;
            short[]? pcm = null;
            double pos = 0, step = 1;
            bool playing = false;
            int max = (int)(MaxSeconds * rate);

            int SampleLeft() => !playing || s == null ? 0 : s.LoopLength > 0 ? Int32.MaxValue : (int)Math.Ceiling((pcm!.Length - pos) / step);
            void Advance(int n)
            {
                for (int i = 0; i < n && outp.Count < max; i++)
                {
                    int t = outp.Count;
                    env = envLen <= 0 || t >= envAt + envLen ? envTo : envFrom + (envTo - envFrom) * (t - envAt) / envLen;
                    float v = 0;
                    if (playing && pcm != null)
                    {
                        int k = (int)pos;
                        double f = pos - k;
                        int k1 = k + 1 < pcm.Length ? k + 1 : s!.LoopLength > 0 ? s.LoopStart : k;
                        v = (float)(pcm[k] * (1 - f) + pcm[k1] * f);
                        pos += step;
                        int end = s!.LoopLength > 0 ? Math.Min(pcm.Length, s.LoopStart + s.LoopLength) : pcm.Length;
                        if (pos >= end)
                        {
                            if (s.LoopLength > 0) pos -= end - s.LoopStart;
                            else playing = false;
                        }
                    }
                    outp.Add(v * vol * env);
                }
            }
            int Ms(int ms) => (int)((long)ms * rate / 1000);
            void Ramp(float from, float to, int len)
            {
                envFrom = from;
                envTo = to;
                envAt = outp.Count;
                envLen = len;
            }

            var loops = new Dictionary<int, int>();
            Span<byte> s8 = stackalloc byte[8];
            for (int c = 0; c + 8 <= cmds.Length; c += 8)
            {
                // the two BE words, each reversed: [op, a, b, c, d, e, f, g]
                for (int k = 0; k < 4; k++)
                {
                    s8[k] = cmds[c + 3 - k];
                    s8[4 + k] = cmds[c + 7 - k];
                }
                int op = s8[0];
                ushort u16b = (ushort)(s8[6] | s8[7] << 8);
                switch (op)
                {
                    case 0x00: // End: the voice plays its sample out (a loop stops here)
                        r.Steps.Add("end");
                        if (playing && s!.LoopLength == 0) Advance(SampleLeft());
                        goto done;
                    case 0x01: // Stop
                        r.Steps.Add("stop");
                        goto done;
                    case 0x04: // WaitTicks [keyOff, random, sampleEnd, absolute, msSwitch, ticks u16]
                    case 0x07: // WaitMs    [keyOff, random, sampleEnd, absolute, -, ms u16]
                    {
                        bool sampleEnd = s8[3] != 0;
                        int wait = u16b == 0xFFFF ? Int32.MaxValue : op == 0x07 || s8[5] != 0 ? Ms(u16b) : Ms(u16b * 1000 / 240);
                        if (sampleEnd) wait = Math.Min(wait, SampleLeft());
                        if (wait == Int32.MaxValue)
                        {
                            r.Steps.Add("wait forever: end");
                            goto done;
                        }
                        r.Steps.Add($"wait {wait * 1000 / rate} ms{(sampleEnd ? " (or the sample's end)" : "")}");
                        Advance(wait);
                        break;
                    }
                    case 0x05: // Loop [count u16, step u16]: jump back to command `step`, count times
                    {
                        int step5 = s8[4] | s8[5] << 8;
                        loops.TryGetValue(c, out int left);
                        if (left == 0 && !loops.ContainsKey(c)) left = u16b;
                        if (left > 0)
                        {
                            loops[c] = left - 1;
                            r.Steps.Add($"loop to {step5} ({left} left)");
                            c = step5 * 8 - 8;
                        }
                        else loops.Remove(c);
                        break;
                    }
                    case 0x0D: // ScaleVolume [scale, add, table u16, original]
                        vol = (s8[1] * sfx.Velocity / 127f + (sbyte)s8[2]) / 127f;
                        r.Steps.Add($"volume {vol:0.00}");
                        break;
                    case 0x0F: // Envelope [scale, add, table u16, msSwitch, time u16]: to the level over the time
                    case 0x14: // FadeIn: from silence
                    {
                        float to = Math.Clamp((s8[1] + (sbyte)s8[2]) / 127f, 0, 1);
                        int len = s8[5] != 0 ? Ms(u16b) : Ms(u16b * 1000 / 240);
                        Ramp(op == 0x14 ? 0 : env, to, len);
                        r.Steps.Add($"{(op == 0x14 ? "fade in" : "envelope")} to {to:0.00} over {u16b} {(s8[5] != 0 ? "ms" : "ticks")}");
                        break;
                    }
                    case 0x10: // StartSample [sample u16, mode, offset u32]
                    {
                        ushort id = (ushort)(s8[1] | s8[2] << 8);
                        if (!Samples.TryGetValue(id, out s)) { playing = false; break; }
                        pcm = Pcm(s);
                        pos = Math.Min((int)(s8[4] | s8[5] << 8 | s8[6] << 16 | s8[7] << 24), Math.Max(0, pcm.Length - 1));
                        step = Math.Pow(2, (note - s.RootKey) / 12.0) * s.Rate / rate;
                        playing = pcm.Length > 0;
                        r.Steps.Add($"sample {id:x} ({pcm.Length} @ {s.Rate} Hz{(s.LoopLength > 0 ? $", loop {s.LoopStart}+{s.LoopLength}" : "")}) at note {note:0.##} (x{step:0.###})");
                        break;
                    }
                    case 0x11: // StopSample
                        playing = false;
                        r.Steps.Add("stop sample");
                        break;
                    case 0x17: // RndNote [low, detune, high, ...]: its low note (the drone's are one note)
                        note = (sbyte)s8[1] + (sbyte)s8[2] / 100f;
                        r.Steps.Add($"note {note:0.##}");
                        break;
                    case 0x18: // AddNote [add, detune]
                        note += (sbyte)s8[1] + (sbyte)s8[2] / 100f;
                        r.Steps.Add($"note {note:0.##}");
                        break;
                    case 0x19: // SetNote [key, detune]
                        note = (sbyte)s8[1] + (sbyte)s8[2] / 100f;
                        r.Steps.Add($"note {note:0.##}");
                        break;
                    default:
                        r.Skipped.Add(op);
                        break;
                }
            }
            done:
            if (playing && s != null && pcm != null && s.LoopLength > 0 && s.LoopStart < pcm.Length)
            {
                int len = Math.Min(s.LoopLength, pcm.Length - s.LoopStart);
                int n = Math.Max(1, (int)Math.Round(len / step));
                double st = (double)len / n;
                float g = vol * envTo;
                r.Loop = new short[n];
                for (int i = 0; i < n; i++)
                {
                    double q = i * st;
                    int k = (int)q;
                    double f = q - k;
                    int k0 = s.LoopStart + k, k1 = s.LoopStart + (k + 1) % len;
                    r.Loop[i] = (short)Math.Clamp(MathF.Round((float)(pcm[k0] * (1 - f) + pcm[k1] * f) * g), short.MinValue, short.MaxValue);
                }
                // the turn's last samples cross-faded into the ones just before the loop start (which lead into its first
                // sample): ADPCM decoded once through doesn't always meet itself at the loop end (a click each turn)
                int fade = Math.Min(256, n / 4);
                if (fade > 0 && s.LoopStart - (fade + 1) * st >= 0)
                {
                    for (int i = 0; i < fade; i++)
                    {
                        double q = s.LoopStart - (fade - i) * st;
                        int k = (int)q;
                        double f = q - k;
                        float pre = (float)(pcm[k] * (1 - f) + pcm[k + 1] * f) * g;
                        float w = (i + 1) / (float)(fade + 1);
                        int at = n - fade + i;
                        r.Loop[at] = (short)Math.Clamp(MathF.Round(r.Loop[at] * (1 - w) + pre * w), short.MinValue, short.MaxValue);
                    }
                }
            }
            // the voice ends: past the last command, a playing loop is cut, a one-shot plays out
            var res = new short[outp.Count];
            for (int i = 0; i < res.Length; i++) res[i] = (short)Math.Clamp(MathF.Round(outp[i]), short.MinValue, short.MaxValue);
            r.Pcm = res;
            return r;
        }
    }
}
