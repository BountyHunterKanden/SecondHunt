using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead;
using MphRead.Entities;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Echoes' grapple points in its multiplayer arenas (docs/GRAPPLE.md G0, 2026-10-30): each GrapplePoint (GRAP) script
    // object and its GrappleInfo, read at import into arena.json Grapples, and a GrapplePointEntity per point joining the
    // arena's entities when a match loads it (the player's swing: MphRead PlayerGrapple.cs). Two arenas have them: one in
    // Sidehopper Station, three in Shooting Gallery (owner 10-30: only one of those is real; GrappleDumpTool's reach check
    // settles which, and Real lists the ids kept). The ring the point hangs in ("Grapple Point Actor", model 09837e11)
    // is the nearest actor of that name.
    public static class EchoesGrapple
    {
        // GrapplePoint's properties: GrappleInfo (a struct) and the fields in it (PrimeWorldEditor's Echoes templates)
        public const uint GrappleInfoId = 0x6A2872D8;
        public const uint LengthId = 0x7A797E4F, AttachLengthId = 0x1D852415, SpringConstantId = 0xFDF5CB82, SpringLengthId = 0x2EC254E3,
            SpringTardisId = 0x987F68C7, SwingForceId = 0x1A6B49AB, SwingMaxForceId = 0x1E5124D4, ArcAngleId = 0x450EA130,
            TurnAngleId = 0x55F2BC8F, CameraPitchId = 0xF12BACE9, CameraMaxPitchId = 0xF9E45827, ConstrainToAxisId = 0x11B6A17A;
        public const string RingName = "Grapple Point Actor";

        // the points kept per arena (Def.Key -> GRAP ids); an arena not listed keeps all of its points.
        // Shooting Gallery: NOT SETTLED YET (parked 10-30). Owner guesses the middle point (00000193, x=0); the
        // -grappledump reach check (still to write) decides. Until then every point is kept.
        public static readonly Dictionary<string, uint[]> Real = new();

        // every GRAP in an area's scripts, active or not (the dump lists them all)
        public static List<GrappleSpot> Read(List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map)
        {
            var rings = scripts.Where(o => o.Name == RingName).ToList();
            var list = new List<GrappleSpot>();
            foreach (Mrea.ScriptObject o in scripts.Where(o => o.Type == "GRAP"))
            {
                float[] t = o.Transform;
                Mrea.Property? g = o.Root.Find(GrappleInfoId);
                float F(uint id, float fallback) => g?.Find(id) is { Data.Length: >= 4 } p ? p.F32 : fallback;
                Vector3 pos = map(t[0], t[1], t[2]);
                // the ring: the nearest "Grapple Point Actor" within 3 Echoes units
                Mrea.ScriptObject? ring = rings.Select(r => (r, d: Dist(r.Transform, t))).Where(x => x.d < 3).OrderBy(x => x.d).Select(x => x.r).FirstOrDefault();
                Vector3? ringPos = ring == null ? null : map(ring.Transform[0], ring.Transform[1], ring.Transform[2]);
                list.Add(new GrappleSpot
                {
                    Id = o.Id,
                    Name = o.Name,
                    Active = o.Active,
                    Layer = o.Layer,
                    Pos = R3(pos),
                    Echoes = new[] { t[0], t[1], t[2] },
                    Ring = ringPos is { } rp ? R3(rp) : null,
                    RingId = ring?.Id,
                    Length = F(LengthId, 10),
                    AttachLength = F(AttachLengthId, 10),
                    SpringConstant = F(SpringConstantId, 1),
                    SpringLength = F(SpringLengthId, 1),
                    SpringTardis = F(SpringTardisId, 1),
                    SwingForce = F(SwingForceId, 1),
                    SwingMaxForce = F(SwingMaxForceId, 1),
                    ArcAngle = F(ArcAngleId, 45),
                    TurnAngle = F(TurnAngleId, 90),
                    CameraPitch = F(CameraPitchId, 0),
                    CameraMaxPitch = F(CameraMaxPitchId, 0),
                    ConstrainToAxis = g?.Find(ConstrainToAxisId)?.Bool ?? false,
                });
            }
            return list;

            static float Dist(float[] a, float[] b) => MathF.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
        }

        static float[] R3(Vector3 v) => new[] { MathF.Round(v.X, 4), MathF.Round(v.Y, 4), MathF.Round(v.Z, 4) };

        // the import: the arena's real points (Real), null when it has none
        public static List<GrappleSpot>? Import(string key, List<Mrea.ScriptObject> scripts, Func<float, float, float, Vector3> map, List<string> notes)
        {
            List<GrappleSpot> all = Read(scripts, map).Where(s => s.Active).ToList();
            if (all.Count == 0) return null;
            List<GrappleSpot> kept = Real.TryGetValue(key, out uint[]? ids) ? all.Where(s => ids.Contains(s.Id)).ToList() : all;
            notes.Add($"{kept.Count} grapple point(s): " + string.Join(", ", kept.Select(s => $"{s.Id:x8} at {s.Pos[0]:0.00} {s.Pos[1]:0.00} {s.Pos[2]:0.00}"))
                + (kept.Count < all.Count ? $" ({all.Count - kept.Count} left out: " + string.Join(", ", all.Except(kept).Select(s => $"{s.Id:x8}")) + ")" : ""));
            return kept.Count == 0 ? null : kept;
        }

        // ---- the beam's look and sounds: hd/grapple_fx/ + hd/grapple.json (G2, owner 10-30: Echoes' own beam) ----

        public const string FxDir = "grapple_fx", LookFile = "grapple.json";
        // Samus's grapple beam effects (SamGunFx.pak; CPlayerGun's grapple arm: claw at the tip, hit on the catch, muzzle at
        // the hand, a segment drawn at each point along the wave, the swoosh along it)
        static readonly (string Role, uint Id)[] FxIds =
        {
            ("claw", 0xd5a18910), ("hit", 0xcfc222b0), ("muzzle", 0x2cc7f7f5), ("segment", 0x87c0bdb2), ("swoosh", 0xed0f768f),
        };
        // the grapple's sounds (Echoes game ids, AudioGrp.pak's ATBL to MusyX sfx in AGSC 67fb1c2e): fire (one shot) and the
        // beam's loop (out and on the rope)
        const uint SoundLookup = 0xb7c1b3a7, SoundGroup = 0x67fb1c2e;
        static readonly (string Role, uint Id, bool Loop)[] SoundIds = { ("fire", 0x258a, false), ("loop", 0x2589, true) };
        public static string SoundFile(string role) => $"grapple_{role}.wav";

        // the effects' files (PART/SWHC as the disc has them, their sub-effects, textures as PNG) and the sounds (WAV), and
        // grapple.json listing them; nothing (and any old ones gone) when the arena has no points or the pak is missing
        internal static GrappleLook? ImportLook(Pak? gunFx, Pak? audio, string hdDir, List<string> notes, bool any)
        {
            string dir = Path.Combine(hdDir, FxDir), file = Path.Combine(hdDir, LookFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(file)) File.Delete(file);
            if (!any) return null;
            if (gunFx == null)
            {
                notes.Add("grapple beam: no SamGunFx.pak; no beam look");
                return null;
            }
            Directory.CreateDirectory(dir);
            var look = new GrappleLook();
            var done = new HashSet<uint>();
            var textures = new SortedSet<uint>();
            var todo = new Queue<uint>();
            foreach ((string role, uint id) in FxIds)
            {
                if (!gunFx.Contains(id))
                {
                    notes.Add($"grapple beam: {role} ({id:x8}) not in SamGunFx.pak");
                    continue;
                }
                look.Fx[role] = new GrappleFx { Kind = gunFx.TypeOf(id).ToLowerInvariant(), Id = id };
                todo.Enqueue(id);
            }
            var missing = new List<uint>();
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!done.Add(id)) continue;
                if (!gunFx.Contains(id))
                {
                    missing.Add(id);
                    continue;
                }
                string type = gunFx.TypeOf(id);
                if (type is not ("PART" or "ELSC" or "SWHC")) continue;
                byte[] b = gunFx.Get(id);
                IEnumerable<(string Type, uint Id)> deps = type switch
                {
                    "PART" => Part.Load(b, echoes: true).Dependencies(),
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
            int pngs = 0;
            foreach (uint t in textures)
            {
                if (!gunFx.Contains(t)) continue;
                byte[] tb = gunFx.Get(t);
                byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? MphRecomp.Import.Effects.HyperFx.Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
                if (rgba == null)
                {
                    notes.Add($"grapple beam: texture {t:x8} (format {Txtr.Header(tb).Format}) not decoded");
                    continue;
                }
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
                pngs++;
            }
            notes.Add($"grapple beam: {look.Fx.Count} effects ({string.Join(", ", look.Fx.Keys)}), {done.Count - missing.Count} files, {pngs} textures"
                + (missing.Count > 0 ? $"; not in the pak: {string.Join(", ", missing.Select(m => m.ToString("x8")))}" : ""));
            if (audio == null || !audio.Contains(SoundLookup) || !audio.Contains(SoundGroup))
            {
                notes.Add("grapple sounds: no AudioGrp.pak (or its grapple group); none written");
            }
            else
            {
                short[] lookup = MusyX.Lookup(audio.Get(SoundLookup));
                Agsc g = Agsc.Parse(audio.Get(SoundGroup));
                foreach ((string role, uint id, bool loop) in SoundIds)
                {
                    int m = MusyX.Translate(lookup, id);
                    if (m < 0 || g.Render((ushort)m) is not Agsc.Rendered x || (loop ? x.Loop : x.Pcm).Length == 0)
                    {
                        notes.Add($"grapple sounds: {role} ({id:x4}, sfx {m}) not rendered");
                        continue;
                    }
                    short[] pcm = loop ? x.Loop : x.Pcm;
                    MusyX.WriteWav(Path.Combine(dir, SoundFile(role)), pcm, x.Rate);
                    look.Sounds.Add(new GrappleSound { Role = role, Id = id, Sfx = m, Loop = loop, Seconds = MathF.Round(pcm.Length / (float)x.Rate, 3) });
                }
                notes.Add($"grapple sounds: {string.Join(", ", look.Sounds.Select(s => $"{s.Role} (sfx {s.Sfx}, {s.Seconds:0.00} s)"))}");
            }
            File.WriteAllText(file, JsonSerializer.Serialize(look));
            return look;
        }

        public static GrappleLook? LoadLook(string hdDir)
        {
            string f = Path.Combine(hdDir, LookFile);
            if (!File.Exists(f)) return null;
            try
            {
                return JsonSerializer.Deserialize<GrappleLook>(File.ReadAllText(f));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- a match ----

        static readonly Dictionary<int, GrappleSpot[]> _rooms = new();
        // a test's points, ahead of the registered ones (a match start registers the arenas again)
        static readonly Dictionary<int, GrappleSpot[]> _testRooms = new();

        // a match in an Echoes arena with the GRAPPLE rule on (on by default: owner 10-30, GRAPPLE.md s5)
        // (no match setting yet: G5 adds the rule; nothing fires a grapple until G4's button either)
        public static bool Enabled { get; set; } = true;

        // an arena registered (info) or gone (null)
        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.Grapples is { Count: > 0 } list) _rooms[roomId] = list.ToArray();
                else _rooms.Remove(roomId);
            }
        }

        // a test's points for a room (Tools -grappledump sim: the disc's points on an arena imported before G0); null = back
        // to the registered ones
        public static void SetRoomSpots(int roomId, GrappleSpot[]? spots)
        {
            lock (_rooms)
            {
                if (spots is { Length: > 0 }) _testRooms[roomId] = spots;
                else _testRooms.Remove(roomId);
            }
        }

        public static bool Has(int roomId)
        {
            lock (_rooms) return _testRooms.ContainsKey(roomId) || _rooms.ContainsKey(roomId);
        }

        // the room's points join its entities; PlayerEntity.HostGrapplePoints = them (null = none: the swing code is off)
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            PlayerEntity.HostGrapplePoints = null;
            // wave 2: off in public builds (owner 2026-11-07)
            if (!Enabled || MphRecomp.Config.RecompSettings.PublicBuild || !GameState.Multiplayer) return entities;
            GrappleSpot[]? spots;
            lock (_rooms)
            {
                if (!_testRooms.TryGetValue(roomId, out spots) && !_rooms.TryGetValue(roomId, out spots)) return entities;
            }
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            var points = new List<GrapplePointEntity>();
            foreach (GrappleSpot s in spots)
            {
                var p = new GrapplePointEntity(id++, new Vector3(s.Pos[0], s.Pos[1], s.Pos[2]), ArenaModelWriter.RoomNode, scene);
                points.Add(p);
                list.Add(p);
            }
            PlayerEntity.HostGrapplePoints = points;
            return list;
        }
    }

    // hd/grapple.json: the beam's effects by role (FxDir/<id>.<kind>) and its sounds (FxDir/grapple_<role>.wav)
    public sealed class GrappleLook
    {
        public Dictionary<string, GrappleFx> Fx { get; set; } = new();
        public List<GrappleSound> Sounds { get; set; } = new();
    }

    public sealed class GrappleFx
    {
        public string Kind { get; set; } = "";
        public uint Id { get; set; }
    }

    public sealed class GrappleSound
    {
        public string Role { get; set; } = "";
        public uint Id { get; set; }
        public int Sfx { get; set; }
        public bool Loop { get; set; }
        public float Seconds { get; set; }
    }

    // arena.json Grapples: one point (MPH units; Echoes = the disc's own position) and its GrappleInfo as the disc has it
    public sealed class GrappleSpot
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public bool Active { get; set; } = true;
        public int Layer { get; set; }
        public float[] Pos { get; set; } = new float[3];
        public float[] Echoes { get; set; } = new float[3];
        public float[]? Ring { get; set; }
        public uint? RingId { get; set; }
        public float Length { get; set; }
        public float AttachLength { get; set; }
        public float SpringConstant { get; set; }
        public float SpringLength { get; set; }
        public float SpringTardis { get; set; }
        public float SwingForce { get; set; }
        public float SwingMaxForce { get; set; }
        public float ArcAngle { get; set; }
        public float TurnAngle { get; set; }
        public float CameraPitch { get; set; }
        public float CameraMaxPitch { get; set; }
        public bool ConstrainToAxis { get; set; }
    }
}
