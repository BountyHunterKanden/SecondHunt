using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Prime 1's room effects for the Prime 1 arenas (thread fbb8db, owner 2026-11-16: Phendrana Shorelines "should look,
    // sound and feel like Prime 1's"), shared by every Prime 1 room (Prime1Rooms.cs, the Main Quarry): imported from the
    // player's own disc at import time into hd/prime1_fx/ and listed in hd/prime1_fx.json; run by the app
    // (CampaignRenderer.Prime1Fx.cs).
    //   Snow (CEnvFxManager): the room's AreaAttributes FX type and density; MiscData.pak's flake (TXTR 79b31bbd), its
    //     ceiling fade (TXTR 5aced851, EnvGradient) and its wind table (DUMB 1c4edb17, 256 x/y pairs, metres/s). Prime
    //     hides flakes under a roof by a top-down depth of the room; here the import casts straight down through the
    //     room's collision on a grid (Roof: the first surface from above, MPH units) and the app fades flakes below it.
    //   Water: the Water object's splash (PART ef546c27, sfx 463) at the surface on entering (scale 2.8) and leaving
    //     (scale 1.9); leaving unmorphed, the visor runoff (PART 6ed6486e, sfx 2499); unmorphing in it 52e2d0fb + 2499;
    //     underwater, the Water's InsideFogColor and Prime's fullscreen multiply filter
    // Sounds: sfx id -> ATBL b7c1b3a7 (AudioGrp.pak) -> MusyX id -> its AGSC (463 and 2499 are both in 57fe7e67 "Misc").
    public static class Prime1Fx
    {
        public const string FxDir = "prime1_fx";
        public const string LookFile = "prime1_fx.json";
        public const uint Splash = 0xef546c27, Runoff = 0x6ed6486e, UnmorphRunoff = 0x52e2d0fb;
        public const uint SnowFlake = 0x79b31bbd, EnvGradient = 0x5aced851, SnowForces = 0x1c4edb17;
        public const uint SoundLookup = 0xb7c1b3a7;
        public const uint SplashSfx = 463, RunoffSfx = 2499;
        // a crate's break (Monitor Station's "Platform Generic Crate"s): its Relay's Effect "Effect Blow" (PART 09431aef,
        // scale 2, at the crate), its Generator's Sound "Sound-Box Explode" (sfx 3621); the pieces are Prime1Rooms.Crates'
        public const uint CrateFlash = 0x09431aef, CrateBreakSfx = 3621;
        public const float CrateFlashScale = 2;
        // Shorelines' Water (00020000-ish 'Water'): InsideFogColor
        public static readonly float[] InsideFog = { 0.443f, 0.569f, 0.624f };
        public const float SplashEnter = 2.8f, SplashExit = 1.9f;
        public const float RoofCell = 1.5f;   // MPH units
        public const short NoRoof = short.MinValue;

        public static string SoundFile(string role) => $"sfx_{role}.wav";

        public sealed class Look
        {
            public float Scale { get; set; } = 1;
            public Dictionary<string, uint> Fx { get; set; } = new();
            public List<Sound> Sounds { get; set; } = new();
            public float[] Fog { get; set; } = InsideFog;
            public float SplashEnterScale { get; set; } = SplashEnter;
            public float SplashExitScale { get; set; } = SplashExit;
            public Snow? Snow { get; set; }
        }

        public sealed class Sound
        {
            public string Role { get; set; } = "";
            public uint Sfx { get; set; }
            public int MusyX { get; set; }
            public float Seconds { get; set; }
        }

        public sealed class Snow
        {
            public int FxType { get; set; }        // 1 snow (2 rain, 3 underwater bubbles: not drawn yet)
            public float Density { get; set; }
            public string Flake { get; set; } = "";
            public string Gradient { get; set; } = "";
            public float[] Forces { get; set; } = Array.Empty<float>();   // x, y pairs (Prime's axes, metres/s)
            // the first surface from above per grid cell (MPH units * 8; NoRoof: none), row-major in X then Z
            public float RoofX0 { get; set; }
            public float RoofZ0 { get; set; }
            public float RoofCellSize { get; set; } = RoofCell;
            public int RoofW { get; set; }
            public int RoofH { get; set; }
            public float Top { get; set; }
            public short[] Roof { get; set; } = Array.Empty<short>();
        }

        // EchoesArena.Import, for Prime 1 rooms: area the room's pak, audio mp1's AudioGrp.pak, misc its MiscData.pak
        // (null: no snow), ray the room's collision (MPH units). moreFx / moreSfx: a room's own effects and sounds (role,
        // PART id / game sfx id; Main Quarry's crane and wall, MainQuarryCrane.Fx / Sfx)
        public static Look? Import(Pak area, Pak? audio, Pak? misc, List<Scly1.Obj> objs, ArenaRaycaster ray, float scale,
            string hdDir, List<string> notes, bool any = true, bool crates = false,
            IEnumerable<(string, uint)>? moreFx = null, IEnumerable<(string, uint)>? moreSfx = null)
        {
            string dir = Path.Combine(hdDir, FxDir), file = Path.Combine(hdDir, LookFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(file)) File.Delete(file);
            if (!any) return null;
            Directory.CreateDirectory(dir);
            var look = new Look { Scale = scale };
            bool water = objs.Any(o => o.Type == 0x20);

            // the water's (and crates') effects and what they need
            var textures = new SortedSet<uint>();
            var roles = new List<(string, uint)>();
            if (water) roles.AddRange(new[] { ("splash", Splash), ("runoff", Runoff), ("unmorph", UnmorphRunoff) });
            if (crates) roles.Add(("crate_flash", CrateFlash));
            if (moreFx != null) roles.AddRange(moreFx);
            if (roles.Count > 0)
            {
                var done = new HashSet<uint>();
                var todo = new Queue<uint>();
                foreach ((string role, uint id) in roles)
                {
                    if (!area.Contains(id)) { notes.Add($"prime1 fx: {role} ({id:x8}) not in the room's pak"); continue; }
                    look.Fx[role] = id;
                    todo.Enqueue(id);
                }
                var missing = new List<uint>();
                while (todo.Count > 0)
                {
                    uint id = todo.Dequeue();
                    if (!done.Add(id)) continue;
                    if (!area.Contains(id)) { missing.Add(id); continue; }
                    string type = area.TypeOf(id);
                    if (type is not ("PART" or "ELSC" or "SWHC")) continue;
                    byte[] b = area.Get(id);
                    IEnumerable<(string Type, uint Id)> deps = type switch
                    {
                        "PART" => Part.Load(b).Dependencies(),
                        "ELSC" => Elsc.Load(b).Dependencies(),
                        _ => Part.Swoosh.Load(b).Texture is uint t and not 0 and not UInt32.MaxValue ? new[] { ("TXTR", t) } : Array.Empty<(string, uint)>(),
                    };
                    File.WriteAllBytes(Path.Combine(dir, $"{id:x8}.{type.ToLowerInvariant()}"), b);
                    foreach ((string t, uint dep) in deps)
                    {
                        if (dep == 0 || dep == UInt32.MaxValue) continue;
                        if (t == "TXTR") textures.Add(dep);
                        else if (t is "PART" or "ELSC" or "SWHC") todo.Enqueue(dep);
                    }
                }
                notes.Add($"prime1 fx: effects {string.Join(", ", look.Fx.Keys)}, {done.Count - missing.Count} files"
                    + (missing.Count > 0 ? $"; not in the pak: {string.Join(", ", missing.Select(m => m.ToString("x8")))}" : ""));
            }
            int pngs = textures.Count(t => Png(area, t, dir, notes));

            // the snow
            Scly1.Obj? attrs = objs.FirstOrDefault(o => o.Type == 0x4E);
            if (attrs != null && attrs.Props.Length >= 13)
            {
                int fx = (int)Be.U32(attrs.Props, 5);
                float density = BitConverter.Int32BitsToSingle((int)Be.U32(attrs.Props, 9));
                if (fx == 1 && density > 0)
                {
                    if (misc == null || !misc.Contains(SnowFlake)) notes.Add("prime1 fx: snow, but no MiscData.pak (its flake); none drawn");
                    else
                    {
                        var snow = new Snow { FxType = fx, Density = density };
                        if (Png(misc, SnowFlake, dir, notes)) { snow.Flake = $"{SnowFlake:x8}.png"; pngs++; }
                        if (misc.Contains(EnvGradient) && Png(misc, EnvGradient, dir, notes)) { snow.Gradient = $"{EnvGradient:x8}.png"; pngs++; }
                        if (misc.Contains(SnowForces))
                        {
                            byte[] f = misc.Get(SnowForces);
                            snow.Forces = new float[f.Length / 4];
                            for (int i = 0; i < snow.Forces.Length; i++) snow.Forces[i] = MathF.Round(BitConverter.Int32BitsToSingle((int)Be.U32(f, i * 4)), 4);
                        }
                        Roof(snow, ray);
                        look.Snow = snow;
                        notes.Add($"prime1 fx: snow, density {density:0.##}, {snow.Forces.Length / 2} wind steps, roof grid {snow.RoofW}x{snow.RoofH} "
                            + $"({snow.Roof.Count(r => r != NoRoof)} cells under something)");
                    }
                }
            }

            // the sounds
            var sfx = new List<(string, uint)>();
            if (water) sfx.AddRange(new[] { ("splash", SplashSfx), ("runoff", RunoffSfx) });
            if (crates) sfx.Add(("crate_break", CrateBreakSfx));
            if (moreSfx != null) sfx.AddRange(moreSfx);
            if (sfx.Count > 0)
            {
                if (audio == null || !audio.Contains(SoundLookup)) notes.Add("prime1 fx: no AudioGrp.pak (its sound lookup); no sounds");
                else
                {
                    short[] lookup = MusyX.Lookup(audio.Get(SoundLookup));
                    foreach ((string role, uint id) in sfx)
                    {
                        int m = MusyX.Translate(lookup, id);
                        // (AudioGrp.pak's groups, then the room's own: Main Quarry's crane is in metroid5.pak's)
                        Agsc.Rendered? got = m < 0 ? null : FindSound(audio, (ushort)m) ?? FindSound(area, (ushort)m);
                        if (got == null) { notes.Add($"prime1 fx: {role} sound (sfx {id}, MusyX {m}) not found"); continue; }
                        // a pure loop (Main Quarry's crane_turn: start the sample, wait forever) is written as one turn of it, played looped
                        short[] pcm = got.Pcm.Length > 0 ? got.Pcm : got.Loop;
                        MusyX.WriteWav(Path.Combine(dir, SoundFile(role)), pcm, got.Rate);
                        look.Sounds.Add(new Sound { Role = role, Sfx = id, MusyX = m, Seconds = MathF.Round(pcm.Length / (float)got.Rate, 3) });
                    }
                    notes.Add($"prime1 fx: sounds {string.Join(", ", look.Sounds.Select(s => $"{s.Role} (sfx {s.Sfx} -> {s.MusyX}, {s.Seconds:0.00} s)"))}");
                }
            }
            notes.Add($"prime1 fx: {pngs} textures");
            if (look.Fx.Count == 0 && look.Snow == null && look.Sounds.Count == 0)
            {
                Directory.Delete(dir, true);
                return null;
            }
            File.WriteAllText(file, JsonSerializer.Serialize(look));
            return look;
        }

        static Agsc.Rendered? FindSound(Pak audio, ushort m)
        {
            foreach (uint gid in audio.Order)
            {
                if (audio.TypeOf(gid) != "AGSC") continue;
                Agsc g;
                try
                {
                    g = Agsc.Parse(audio.Get(gid));
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }
                if (!g.Sounds.ContainsKey(m)) continue;
                if (g.Render(m) is Agsc.Rendered x && (x.Pcm.Length > 0 || x.Loop.Length > 0)) return x;
            }
            return null;
        }

        static bool Png(Pak pak, uint t, string dir, List<string> notes)
        {
            if (!pak.Contains(t)) return false;
            byte[] tb = pak.Get(t);
            byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? MphRecomp.Import.Effects.HyperFx.Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
            if (rgba == null)
            {
                notes.Add($"prime1 fx: texture {t:x8} (format {Txtr.Header(tb).Format}) not decoded");
                return false;
            }
            PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
            return true;
        }

        // the first surface from above per cell, straight down through the room's collision
        static void Roof(Snow snow, ArenaRaycaster ray)
        {
            Vector3 lo = ray.Min, hi = ray.Max;
            int w = Math.Max(1, (int)MathF.Ceiling((hi.X - lo.X) / RoofCell)), h = Math.Max(1, (int)MathF.Ceiling((hi.Z - lo.Z) / RoofCell));
            float top = hi.Y + 1, bottom = lo.Y - 1;
            var roof = new short[w * h];
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    var from = new Vector3(lo.X + (x + 0.5f) * RoofCell, top, lo.Z + (z + 0.5f) * RoofCell);
                    (float T, Vector3 Normal)? hit = ray.Cast(from, new Vector3(from.X, bottom, from.Z));
                    roof[z * w + x] = hit is { } k ? (short)Math.Clamp(MathF.Round((top + (bottom - top) * k.T) * 8), short.MinValue + 1, short.MaxValue) : NoRoof;
                }
            }
            snow.RoofX0 = lo.X;
            snow.RoofZ0 = lo.Z;
            snow.RoofW = w;
            snow.RoofH = h;
            snow.Top = hi.Y;
            snow.Roof = roof;
        }

        public static Look? LoadLook(string hdDir)
        {
            string f = Path.Combine(hdDir, LookFile);
            if (!File.Exists(f)) return null;
            try
            {
                return JsonSerializer.Deserialize<Look>(File.ReadAllText(f));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
