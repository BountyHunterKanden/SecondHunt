using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MphRecomp.Import.Retro;

namespace MphRecomp.Import.Effects
{
    // A native body's own particle effects (Prime 1), written beside it in <body>/fx/ by the body import: the effects its
    // clips play (each clip's EVNT particle points: PART on a bone, for so many frames, in a parent mode) and the hit
    // effect of its weapon's collision response table (CRSC: DEFS), and the effect where its laser burns (the drone's
    // CDroneLaser particle, from its SCLY properties), and the sounds those play (Retro/MusyX.cs: the ATBL sound lookup and
    // the AGSC audio group, both in AudioGrp.pak). The recipe asks for them:
    //   "fx": { "clips": ["A_shootloop_drone", ...], "crsc": "c59e2b05", "laser": "5cf355af",
    //           "sfx": { "lookup": "b7c1b3a7", "group": "98b283bf" } }
    // Out: every PART needed (the closure of their child systems) raw as <id>.part (their swooshes as <id>.swhc), their
    // textures as <txtr>.png (RGBA, the first row the top one), and fx.cfg:
    //   event <clip> <time s> <frames> <bone> <part> <mode 0 initial / 1 continuous emitter / 2 continuous system> <scale>
    //   sound <clip> <time s> <sfx id> <name>
    //   impact <part>                 impactsound <sfx id>          laser <part>
    //   sfx <sfx id> <MusyX sfx id>   its sound played offline: sfx_<sfx id>.wav (mono PCM16 at its sample's rate)
    // Core Campaign/RetroParticles.cs runs the PARTs; the app plays them (Scout Drone: CampaignRenderer.DroneFx.cs).
    // A weapon's shot by itself (the Pirate Blaster's: a Space Pirate's, WPSC aba4b71d) adds what the shot draws and plays:
    //   "parts": ["91f9a346", ...], "sounds": [548], and "group" may list several AGSC ids (each sound from the first that
    //   has it) -> part <id> lines (+ their PARTs) and those sounds' sfx lines / WAVs
    // EVNT layout (MP1, version 1/2) as Metaforce's CAnimPOIData / CPOINode readers (https://github.com/AxioDL/metaforce,
    // MIT License, Copyright (c) Metaforce Contributors).
    public static class BodyFx
    {
        public sealed record Spec(string[] Clips, uint Crsc, uint Laser = 0, uint SfxLookup = 0, uint SfxGroup = 0,
            uint[]? Parts = null, uint[]? Sounds = null, uint[]? MoreGroups = null);

        public static Spec? From(ImportRecipe recipe)
        {
            if (!recipe.Root.TryGetProperty("fx", out JsonElement fx)) return null;
            string[] clips = fx.TryGetProperty("clips", out JsonElement c) ? c.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
            uint crsc = fx.TryGetProperty("crsc", out JsonElement r) ? Hex(r.GetString()!) : 0;
            uint laser = fx.TryGetProperty("laser", out JsonElement l) ? Hex(l.GetString()!) : 0;
            uint lookup = 0, group = 0;
            uint[]? more = null;
            if (fx.TryGetProperty("sfx", out JsonElement sfx))
            {
                lookup = Hex(sfx.GetProperty("lookup").GetString()!);
                JsonElement g = sfx.GetProperty("group");
                uint[] groups = g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().Select(x => Hex(x.GetString()!)).ToArray() : new[] { Hex(g.GetString()!) };
                group = groups[0];
                if (groups.Length > 1) more = groups[1..];
            }
            uint[]? parts = fx.TryGetProperty("parts", out JsonElement ps) ? ps.EnumerateArray().Select(x => Hex(x.GetString()!)).ToArray() : null;
            uint[]? sounds = fx.TryGetProperty("sounds", out JsonElement ss) ? ss.EnumerateArray().Select(x => x.GetUInt32()).ToArray() : null;
            return new Spec(clips, crsc, laser, lookup, group, parts, sounds, more);
        }

        static uint Hex(string s) => UInt32.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        // the recipe's fx, if it has any (a no-op otherwise)
        public static void Export(Func<uint, byte[]> res, ImportRecipe recipe, uint ancs, string outDir)
        {
            if (From(recipe) is Spec spec) Export(res, spec, ancs, outDir);
        }

        public sealed record Event(string Clip, float Time, int Frames, string Bone, uint Part, int Mode, float Scale);
        public sealed record Sound(string Clip, float Time, uint Id, string Name);

        public static void Export(Func<uint, byte[]> res, Spec spec, uint ancs, string outDir)
        {
            string dir = Path.Combine(outDir, "fx");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var events = new List<Event>();
            var sounds = new List<Sound>();
            if (spec.Clips.Length > 0)
            {
                Ancs a = Ancs.Parse(res(ancs));
                Ancs.AnimSet set = a.Set ?? throw new InvalidDataException($"{ancs:x8}: no animation set");
                foreach (string clip in spec.Clips)
                {
                    Ancs.MetaAnim meta = set.Anims.FirstOrDefault(x => x.Name == clip).Anim
                        ?? throw new InvalidDataException($"{ancs:x8}: no clip {clip}");
                    uint anim = Ancs.Prims(meta).First().AnimId;
                    uint evnt = set.Resources.FirstOrDefault(x => x.Anim == anim).Evnt;
                    if (evnt == 0 || evnt == UInt32.MaxValue) evnt = Be.U32(res(anim), 8);   // MP1 ANIM header: its EVNT
                    if (evnt == 0 || evnt == UInt32.MaxValue) continue;
                    ReadEvnt(res(evnt), clip, events, sounds);
                }
            }
            Crsc? crsc = spec.Crsc != 0 ? Crsc.Load(res(spec.Crsc)) : null;
            // the PARTs and their textures
            var parts = new SortedDictionary<uint, Part>();
            var textures = new SortedSet<uint>();
            var todo = new Queue<uint>(events.Select(e => e.Part));
            if (crsc != null && crsc.DefaultEffect != 0) todo.Enqueue(crsc.DefaultEffect);
            if (spec.Laser != 0) todo.Enqueue(spec.Laser);
            foreach (uint id in spec.Parts ?? Array.Empty<uint>()) todo.Enqueue(id);
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (parts.ContainsKey(id)) continue;
                byte[] b = res(id);
                Part p = Part.Load(b);
                parts[id] = p;
                File.WriteAllBytes(Path.Combine(dir, $"{id:x8}.part"), b);
                foreach ((string type, uint dep) in p.Dependencies())
                {
                    if (type == "PART") todo.Enqueue(dep);
                    else if (type == "TXTR") textures.Add(dep);
                    else if (type == "SWHC" && !File.Exists(Path.Combine(dir, $"{dep:x8}.swhc")))
                    {
                        // a swoosh (the Pirate Blaster's shot trails three): raw as <id>.swhc, its texture with the rest
                        byte[] w = res(dep);
                        if (Part.Swoosh.Load(w).Texture is uint wt and not 0) textures.Add(wt);
                        File.WriteAllBytes(Path.Combine(dir, $"{dep:x8}.swhc"), w);
                    }
                }
            }
            foreach (uint t in textures)
            {
                byte[]? rgba = Txtr.Decode(res(t), out uint fmt, out int w, out int h);
                if (rgba == null) throw new InvalidDataException($"texture {t:x8}: format {fmt} is not decoded");
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
            }
            var cfg = new StringBuilder();
            cfg.Append("# the body's particle effects (Import/Effects/BodyFx.cs)\n");
            foreach (Event e in events)
            {
                cfg.Append(Inv($"event {e.Clip} {e.Time:R} {e.Frames} {e.Bone} {e.Part:x8} {e.Mode} {e.Scale:R}\n"));
            }
            foreach (Sound s in sounds) cfg.Append(Inv($"sound {s.Clip} {s.Time:R} {s.Id} {s.Name}\n"));
            if (crsc != null)
            {
                if (crsc.DefaultEffect != 0) cfg.Append($"impact {crsc.DefaultEffect:x8}\n");
                if (crsc.DefaultSound != UInt32.MaxValue) cfg.Append(Inv($"impactsound {crsc.DefaultSound}\n"));
            }
            if (spec.Laser != 0) cfg.Append($"laser {spec.Laser:x8}\n");
            foreach (uint id in spec.Parts ?? Array.Empty<uint>()) cfg.Append($"part {id:x8}\n");
            if (spec.SfxLookup != 0 && spec.SfxGroup != 0)
            {
                short[] lookup = MusyX.Lookup(res(spec.SfxLookup));
                Agsc[] groups = new[] { spec.SfxGroup }.Concat(spec.MoreGroups ?? Array.Empty<uint>()).Select(g => Agsc.Parse(res(g))).ToArray();
                var ids = new SortedSet<uint>(sounds.Select(x => x.Id).Concat(spec.Sounds ?? Array.Empty<uint>()));
                if (crsc != null && crsc.DefaultSound != UInt32.MaxValue) ids.Add(crsc.DefaultSound);
                foreach (uint id in ids)
                {
                    int m = MusyX.Translate(lookup, id);
                    if (m < 0) continue;
                    Agsc.Rendered? r = null;
                    foreach (Agsc group in groups)
                    {
                        if (group.Render((ushort)m) is Agsc.Rendered g && g.Pcm.Length > 0)
                        {
                            r = g;
                            break;
                        }
                    }
                    if (r == null) continue;
                    MusyX.WriteWav(Path.Combine(dir, SfxFile(id)), r.Pcm, r.Rate);
                    cfg.Append(Inv($"sfx {id} {m}\n"));
                }
            }
            File.WriteAllText(Path.Combine(dir, "fx.cfg"), cfg.ToString());
        }

        static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

        public static string SfxFile(uint id) => Inv($"sfx_{id}.wav");

        // MP1 EVNT: loop / user / particle / (version 2) sound point lists; each point: u16, name, u16 type, f32 time,
        // u32 state, u32 index, u8 unique, f32 weight, u32 character, u32 flags
        static void ReadEvnt(byte[] b, string clip, List<Event> events, List<Sound> sounds)
        {
            int o = 0;
            uint U32() { uint v = Be.U32(b, o); o += 4; return v; }
            float F32() { float v = Be.F32(b, o); o += 4; return v; }
            string S() { string v = Be.CString(b, o, out int e); o = e + 1; return v; }
            (string Name, float Time) Poi()
            {
                o += 2;
                string name = S();
                o += 2;
                float t = F32();
                o += 4 + 4 + 1 + 4 + 4 + 4;
                return (name, t);
            }
            uint version = U32();
            uint n = U32();
            for (uint i = 0; i < n; i++) { Poi(); o += 1; }
            n = U32();
            for (uint i = 0; i < n; i++) { Poi(); U32(); S(); }
            n = U32();
            for (uint i = 0; i < n; i++)
            {
                (_, float t) = Poi();
                int frames = (int)U32();
                o += 4;     // "PART"
                uint part = U32();
                string bone = S();
                float scale = F32();
                int mode = (int)U32();
                events.Add(new Event(clip, t, frames, bone, part, mode, scale));
            }
            if (version >= 2)
            {
                n = U32();
                for (uint i = 0; i < n; i++)
                {
                    (string name, float t) = Poi();
                    uint id = U32();
                    F32(); F32();
                    sounds.Add(new Sound(clip, t, id, name.Replace(' ', '_')));
                }
            }
        }

        // fx.cfg back (the app and the tests)
        public sealed class Config
        {
            public readonly List<Event> Events = new();
            public readonly List<Sound> Sounds = new();
            public uint Impact;
            public uint ImpactSound = UInt32.MaxValue;
            public uint Laser;
            public readonly List<uint> Parts = new();             // a weapon's shot ("part" lines)
            public readonly Dictionary<uint, int> Sfx = new();   // sound id -> its MusyX sfx id (sfx_<id>.wav beside it)

            public static Config? Load(string bodyDir)
            {
                string path = Path.Combine(bodyDir, "fx", "fx.cfg");
                if (!File.Exists(path)) return null;
                var c = new Config();
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (w.Length == 0 || w[0].StartsWith('#')) continue;
                    CultureInfo ci = CultureInfo.InvariantCulture;
                    switch (w[0])
                    {
                        case "event" when w.Length >= 8:
                            c.Events.Add(new Event(w[1], Single.Parse(w[2], ci), Int32.Parse(w[3], ci), w[4], Hex(w[5]), Int32.Parse(w[6], ci),
                                Single.Parse(w[7], ci)));
                            break;
                        case "sound" when w.Length >= 5:
                            c.Sounds.Add(new Sound(w[1], Single.Parse(w[2], ci), UInt32.Parse(w[3], ci), w[4]));
                            break;
                        case "impact" when w.Length >= 2: c.Impact = Hex(w[1]); break;
                        case "impactsound" when w.Length >= 2: c.ImpactSound = UInt32.Parse(w[1], ci); break;
                        case "laser" when w.Length >= 2: c.Laser = Hex(w[1]); break;
                        case "part" when w.Length >= 2: c.Parts.Add(Hex(w[1])); break;
                        case "sfx" when w.Length >= 3: c.Sfx[UInt32.Parse(w[1], ci)] = Int32.Parse(w[2], ci); break;
                    }
                }
                return c;
            }
        }
    }
}
