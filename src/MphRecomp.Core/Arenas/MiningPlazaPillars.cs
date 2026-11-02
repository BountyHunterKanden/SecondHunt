using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MphRead;
using MphRead.Editor;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRead.Utility;
using MphRecomp.Import;
using MphRecomp.Import.Retro;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Mining Plaza's two collapsing pillars (owner 2026-10-31: "collapse to missiles/Screw Attack
    // like in Echoes and STAY collapsed for the match"). Each is Echoes' own: the column platform (PLAT 'Pillar', its DCLN)
    // with its top, middle and bottom chunks, the stone cover by its foot, and the rubble it lands in (DCLN). Echoes' fall,
    // read from the room's timers and relays and the platform's waypoint (MiningPlaza.Pillars), on one timeline from the
    // hit: the middle chunk gone and the explosion at 0, the column sliding to its waypoint from 0.289 s and tipping from
    // 0.898 s, the top gone at 1.799 s, the bottom (and its collision) at 2.098 s, the cover gone and the rubble in (and
    // solid) at 2.7 s, the dust at 3 s. MPH has no Screw Attack: a missile topples it. Each is an MPH enemy MphRead's
    // damage never reaches (as EchoesBreakables): its collision (slot 0 the column, moving with it; slot 1 the bottom
    // chunk, then the rubble) is entity collision players stand on and beams hit (CollisionDetection.HostEntityCollision,
    // BeamCollideWith). Drawn as placed passes (CampaignArena: Pieces), its effects and sounds in the app
    // (CampaignRenderer.PillarFx.cs, from hd/pillar.json). In every multiplayer match in the room; LAN/online guests
    // mirror the host's (Net/NetWorld.cs). Bots route around them standing and over them fallen: the arena's node data has a
    // set per pillar state (StateTris, ArenaNav.BuildSets); a pillar's rubble landing sets its bit in Room.NodeData.SetSelector.
    public static class MiningPlazaPillars
    {
        public sealed class PillarSpot
        {
            public uint Id { get; set; }
            // the column's Echoes transform (MiningPlaza: x y z, rotation x y z in degrees, scale x y z) and its waypoint
            public float[] Xf { get; set; } = Array.Empty<float>();
            public float[] End { get; set; } = Array.Empty<float>();
            // Echoes metres to MPH units (EchoesArena.Info.Scale)
            public float Scale { get; set; }
            // the placed passes (-1: not drawn)
            public int Column { get; set; } = -1;
            public int Top { get; set; } = -1;
            public int Middle { get; set; } = -1;
            public int Bottom { get; set; } = -1;
            public int Cover { get; set; } = -1;
            public int Rubble { get; set; } = -1;
            // Echoes' lighting standing and fallen (EchoesCrates.Lighting)
            public float[]? Ambient { get; set; }
            public List<float[]>? Lights { get; set; }
            public float[]? DownAmbient { get; set; }
            public List<float[]>? DownLights { get; set; }
            // collision, MPH world triangles facing out (9 floats each)
            public float[] ColumnTris { get; set; } = Array.Empty<float>();
            public float[] BottomTris { get; set; } = Array.Empty<float>();
            public float[] RubbleTris { get; set; } = Array.Empty<float>();
            // the effects' spots (MPH): the explosion, the landing (and its axes, 3 x 3 unit vectors), the two dust clouds
            public float[] Explosion { get; set; } = Array.Empty<float>();
            public float[] Impact { get; set; } = Array.Empty<float>();
            public float[] ImpactAxes { get; set; } = Array.Empty<float>();
            public float[] Dust1 { get; set; } = Array.Empty<float>();
            public float[] Dust2 { get; set; } = Array.Empty<float>();
        }

        // ---- the timeline (seconds from the hit) ----

        public const float SlideStart = 0.289f, SlideEnd = 2.7f, TipStart = 0.898f, ExplosionOff = 1.33f, TopGone = 1.799f,
            BottomGone = 2.098f, Landed = 2.7f, DustAt = 3.0f, Done = 3.5f;
        // the tip (degrees about the column's own Y, from TipStart): Echoes' waypoint turn as the platform plays it
        static readonly (float T, float Deg)[] Tip = { (0, 0), (0.6f, 1.5f), (1.8f, -91), (1.92f, -86), (2.1f, -90) };

        static float TipAt(float t)
        {
            if (t <= Tip[0].T) return Tip[0].Deg;
            for (int i = 1; i < Tip.Length; i++)
            {
                if (t <= Tip[i].T) return Tip[i - 1].Deg + (Tip[i].Deg - Tip[i - 1].Deg) * (t - Tip[i - 1].T) / (Tip[i].T - Tip[i - 1].T);
            }
            return Tip[^1].Deg;
        }

        // the column's Echoes transform t seconds after the hit
        public static float[] FallXf(float[] xf, float[] end, float t)
        {
            var x = (float[])xf.Clone();
            float k = Math.Clamp((t - SlideStart) / (SlideEnd - SlideStart), 0, 1);
            for (int i = 0; i < 3; i++) x[i] = xf[i] + (end[i] - xf[i]) * k;
            x[4] = xf[4] + TipAt(t - TipStart);
            return x;
        }

        // object space to MPH world (row vectors) for an Echoes transform
        internal static Matrix4 Frame(float[] xf, float scale)
        {
            Vector3 M(Vector3 e) => new Vector3(e.X, e.Z, -e.Y) * scale;
            Vector3 o = M(ArenaHdWriter.Place(xf, Vector3.Zero));
            Vector4 R(Vector3 e) => new Vector4(M(ArenaHdWriter.Place(xf, e)) - o, 0);
            return new Matrix4(R(Vector3.UnitX), R(Vector3.UnitY), R(Vector3.UnitZ), new Vector4(o, 1));
        }

        // the column's motion t seconds in: its standing world place to where it is (Identity before the hit)
        public static Matrix4 Model(PillarSpot s, float t)
        {
            if (t <= 0 || s.Xf.Length < 9 || s.End.Length < 3) return Matrix4.Identity;
            return Frame(s.Xf, s.Scale).Inverted() * Frame(FallXf(s.Xf, s.End, t), s.Scale);
        }

        // the collision of the pillars in one state (bit i set: spot i fallen, its column where it lands + its rubble;
        // clear: standing, column + bottom chunk), for the bots' nav graph
        public static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> StateTris(IReadOnlyList<PillarSpot> spots, int state)
        {
            for (int i = 0; i < spots.Count; i++)
            {
                PillarSpot s = spots[i];
                bool down = (state & (1 << i)) != 0;
                Matrix4 m = down ? Model(s, Done) : Matrix4.Identity;
                foreach (var t in Tris(s.ColumnTris, m)) yield return t;
                foreach (var t in Tris(down ? s.RubbleTris : s.BottomTris, Matrix4.Identity)) yield return t;
            }
        }

        static IEnumerable<(Vector3, Vector3, Vector3)> Tris(float[] t, Matrix4 m)
        {
            for (int k = 0; k + 8 < t.Length; k += 9)
            {
                yield return (Vector3.TransformPosition(new Vector3(t[k], t[k + 1], t[k + 2]), m),
                    Vector3.TransformPosition(new Vector3(t[k + 3], t[k + 4], t[k + 5]), m),
                    Vector3.TransformPosition(new Vector3(t[k + 6], t[k + 7], t[k + 8]), m));
            }
        }

        // ---- the room registry and the entities ----

        static readonly Dictionary<int, List<PillarSpot>> _rooms = new();

        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.Pillars is { Count: > 0 } p) _rooms[roomId] = p;
                else _rooms.Remove(roomId);
            }
        }

        internal static CrateLight? Light(float[]? amb, List<float[]>? lights)
        {
            if (amb is not { Length: 3 } a || lights == null) return null;
            float[][] ls = lights.Where(l => l.Length == 6).Take(4).ToArray();
            return new CrateLight(new Vector3(a[0], a[1], a[2]), ls.Select(l => new Vector3(l[0], l[1], l[2])).ToArray(), ls.Select(l => new Vector3(l[3], l[4], l[5])).ToArray());
        }

        // SceneSetup.HostExtraEntities: a pillar entity each after every other entity (any multiplayer match in the room)
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            List<PillarSpot>? spots;
            lock (_rooms)
            {
                if (!GameState.Multiplayer || !_rooms.TryGetValue(roomId, out spots)) return entities;
            }
            var list = new List<EntityBase>(entities);
            int id = entities.Count == 0 ? 0 : entities.Max(e => e.Id) + 1;
            for (int i = 0; i < spots.Count; i++)
            {
                PillarSpot s = spots[i];
                list.Add(new MiningPlazaPillarEntity(id++, i, s, Light(s.Ambient, s.Lights), Light(s.DownAmbient, s.DownLights), scene));
            }
            return list;
        }

        // world triangles (9 floats each, already facing out) as MPH entity collision, in place (the entity's Identity)
        internal static CollisionInstance Shape(float[] t, string name, Terrain terrain = Terrain.Rock)
        {
            var editors = new List<CollisionDataEditor>();
            for (int k = 0; k + 8 < t.Length; k += 9)
            {
                var a = new Vector3(t[k], t[k + 1], t[k + 2]);
                var b = new Vector3(t[k + 3], t[k + 4], t[k + 5]);
                var c = new Vector3(t[k + 6], t[k + 7], t[k + 8]);
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared < 1e-10f) continue;
                n.Normalize();
                float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
                int axis = ay > ax && ay >= az ? 1 : az > ax && az > ay ? 2 : 0;
                var ed = new CollisionDataEditor { Plane = new Vector4(n, Vector3.Dot(n, a)), LayerMask = (ushort)(4 | axis), Terrain = terrain, Beams = true, Players = true };
                ed.Points.Add(a); ed.Points.Add(b); ed.Points.Add(c);
                editors.Add(ed);
            }
            byte[] bytes = RepackCollision.PackMphCollision(editors);
            CollisionHeader header = Read.ReadStruct<CollisionHeader>(bytes);
            return new CollisionInstance(name, Collision.ReadMphCollision(header, bytes, roomLayerMask: -1), isEntity: true);
        }

        // ---- the look: hd/pillar_fx/ + hd/pillar.json ----

        public const string FxDir = "pillar_fx", LookFile = "pillar.json";
        // the effects the room's pillar generators play (Metroid3.pak PARTs): the blast at the hit, the landing, the dust
        static readonly (string Role, uint Id)[] FxIds = { ("explosion", 0x30a7be36), ("impact", 0x55a664c4), ("dust", 0xdad8ecc6), ("gatedust", 0xed29e853),
            // the sand crates' (MiningPlazaCrates): their generator's trail, flash and dust
            ("crate_trail", 0x982c7701), ("crate_flash", 0x418d14d4), ("crate_dust", 0xa982d571) };
        // the room's pillar SONDs (Echoes game ids, volume 0-127): the hit, the slide (a loop), the scrape, the landing
        const uint SoundLookup = 0xb7c1b3a7;
        static readonly uint[] SoundGroups = { 0x3b13ca9f, 0x6e01e6a4, 0x00b10d9c, 0xa2f353cc };
        static readonly (string Role, uint Id, bool Loop, int Volume)[] SoundIds =
        {
            ("hit", 0x35b, false, 115), ("slide", 0x35a, true, 100), ("scrape", 0x200f, false, 127), ("impact", 0x330, false, 115),
            // the Luminoth gate's (MiningPlazaGate): the idle hum, the lower loop, the stop and the squeak
            ("gate_idle", 99, true, 100), ("gate_lower", 720, true, 95), ("gate_stop", 251, false, 127), ("gate_squeak", 338, false, 115),
            // the sand crates': the break and a piece's bounce
            ("crate_break", 0x535, false, 100), ("crate_bounce", 0x4b7, false, 80),
        };
        public static string SoundFile(string role) => $"pillar_{role}.wav";

        public sealed class PillarLook
        {
            public Dictionary<string, uint> Fx { get; set; } = new();
            public List<PillarSound> Sounds { get; set; } = new();
        }

        public sealed class PillarSound
        {
            public string Role { get; set; } = "";
            public uint Id { get; set; }
            public int Sfx { get; set; }
            public bool Loop { get; set; }
            public float Volume { get; set; } = 1;
            public float Seconds { get; set; }
        }

        // the gate's jingle (owner 2026-10-31: Echoes plays none at the Luminoth gate, so the room's only event jingle
        // stands in, Audio/evt_x_event_00.dsp, an RS03 stream; ArenaRecipe.Jingle): written mono as SoundFile("gate_jingle")
        public const string JingleFile = "Audio/evt_x_event_00.dsp";

        internal static PillarLook? ImportLook(Pak pak, Pak? audio, string hdDir, List<string> notes, bool any, byte[]? jingle = null, IEnumerable<(string Role, uint Id)>? moreFx = null)
        {
            string dir = Path.Combine(hdDir, FxDir), file = Path.Combine(hdDir, LookFile);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(file)) File.Delete(file);
            if (!any) return null;
            Directory.CreateDirectory(dir);
            var look = new PillarLook();
            var done = new HashSet<uint>();
            var textures = new SortedSet<uint>();
            var todo = new Queue<uint>();
            foreach ((string role, uint id) in moreFx == null ? FxIds : FxIds.Concat(moreFx))
            {
                if (!pak.Contains(id))
                {
                    notes.Add($"pillars: {role} effect ({id:x8}) not in the area's pak");
                    continue;
                }
                look.Fx[role] = id;
                todo.Enqueue(id);
            }
            var missing = new List<uint>();
            while (todo.Count > 0)
            {
                uint id = todo.Dequeue();
                if (!done.Add(id)) continue;
                if (!pak.Contains(id))
                {
                    missing.Add(id);
                    continue;
                }
                string type = pak.TypeOf(id);
                if (type is not ("PART" or "ELSC" or "SWHC")) continue;
                byte[] b = pak.Get(id);
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
                if (!pak.Contains(t)) continue;
                byte[] tb = pak.Get(t);
                byte[]? rgba = Txtr.Header(tb).Format == Txtr.RGBA8 ? MphRecomp.Import.Effects.HyperFx.Rgba8(tb, out int w, out int h) : Txtr.Decode(tb, out _, out w, out h);
                if (rgba == null)
                {
                    notes.Add($"pillars: texture {t:x8} (format {Txtr.Header(tb).Format}) not decoded");
                    continue;
                }
                PngOut.Write(Path.Combine(dir, $"{t:x8}.png"), rgba, w, h);
                pngs++;
            }
            notes.Add($"pillars: {look.Fx.Count} effects ({string.Join(", ", look.Fx.Keys)}), {done.Count - missing.Count} files, {pngs} textures"
                + (missing.Count > 0 ? $"; not in the pak: {string.Join(", ", missing.Select(m => m.ToString("x8")))}" : ""));
            if (audio == null || !audio.Contains(SoundLookup))
            {
                notes.Add("pillar sounds: no AudioGrp.pak (its sound lookup); none written");
            }
            else
            {
                short[] lookup = MusyX.Lookup(audio.Get(SoundLookup));
                var groups = new List<Agsc>();
                foreach (uint g in SoundGroups)
                {
                    if (pak.Contains(g)) groups.Add(Agsc.Parse(pak.Get(g)));
                    else if (audio.Contains(g)) groups.Add(Agsc.Parse(audio.Get(g)));
                }
                foreach ((string role, uint id, bool loop, int volume) in SoundIds)
                {
                    int m = MusyX.Translate(lookup, id);
                    Agsc.Rendered? got = null;
                    if (m >= 0) got = groups.Select(g => g.Render((ushort)m)).FirstOrDefault(x => x != null && (loop ? x.Loop : x.Pcm).Length > 0);
                    // else any group of the area's pak (then the audio pak's) that holds it
                    foreach (Pak p in new[] { pak, audio })
                    {
                        if (got != null || m < 0) break;
                        foreach (uint gid in p.Order)
                        {
                            if (p.TypeOf(gid) != "AGSC" || SoundGroups.Contains(gid)) continue;
                            Agsc g;
                            try
                            {
                                g = Agsc.Parse(p.Get(gid));
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                continue;
                            }
                            if (!g.Sounds.ContainsKey((ushort)m)) continue;
                            groups.Add(g);
                            if (g.Render((ushort)m) is Agsc.Rendered x && (loop ? x.Loop : x.Pcm).Length > 0) got = x;
                            break;
                        }
                    }
                    if (got == null)
                    {
                        notes.Add($"pillar sounds: {role} ({id:x4}, sfx {m}) not found in the audio groups");
                        continue;
                    }
                    short[] pcm = loop ? got.Loop : got.Pcm;
                    MusyX.WriteWav(Path.Combine(dir, SoundFile(role)), pcm, got.Rate);
                    look.Sounds.Add(new PillarSound { Role = role, Id = id, Sfx = m, Loop = loop, Volume = MathF.Round(volume / 127f, 3), Seconds = MathF.Round(pcm.Length / (float)got.Rate, 3) });
                }
                notes.Add($"pillar sounds: {string.Join(", ", look.Sounds.Select(s => $"{s.Role} (sfx {s.Sfx}, {s.Seconds:0.00} s)"))}");
            }
            if (jingle != null && RetroStream.IsRetroStream(jingle))
            {
                var rs = new RetroStream(jingle);
                RetroStreamReader rd = rs.OpenReader();
                var mix = new short[rs.SampleCount * rs.Channels];
                int frames = rd.Read(mix, rs.SampleCount);
                var pcm = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < rs.Channels; c++) sum += mix[i * rs.Channels + c];
                    pcm[i] = (short)(sum / rs.Channels);
                }
                MusyX.WriteWav(Path.Combine(dir, SoundFile("gate_jingle")), pcm, rs.SampleRate);
                look.Sounds.Add(new PillarSound { Role = "gate_jingle", Loop = false, Volume = 1, Seconds = MathF.Round(frames / (float)rs.SampleRate, 3) });
                notes.Add($"gate jingle: {JingleFile} ({rs.Channels} channels -> mono, {frames / (float)rs.SampleRate:0.00} s)");
            }
            else if (any) notes.Add("gate jingle: none (no jingle file in the recipe)");
            File.WriteAllText(file, JsonSerializer.Serialize(look));
            return look;
        }

        public static PillarLook? LoadLook(string hdDir)
        {
            string f = Path.Combine(hdDir, LookFile);
            if (!File.Exists(f)) return null;
            try
            {
                return JsonSerializer.Deserialize<PillarLook>(File.ReadAllText(f));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public sealed class MiningPlazaPillarEntity : EnemyInstanceEntity
    {
        public MiningPlazaPillars.PillarSpot Spot { get; }
        readonly CrateLight? _light, _downLight;
        // the spot's place in the room's list: its bit in the node data's set selector
        readonly int _index;
        // steps (60 Hz) since the hit; -1 standing
        int _fall = -1;
        bool _bottomOff, _rubbleOn;

        public bool Fallen => _fall >= 0;
        public int Breaks => Fallen ? 1 : 0;
        // seconds since the hit (-1 standing): the app's effects and sounds follow it
        public float FallTime => _fall < 0 ? -1 : _fall / 60f;

        public MiningPlazaPillarEntity(int id, int index, MiningPlazaPillars.PillarSpot spot, CrateLight? light, CrateLight? downLight, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), NodeRef.None, scene)
        {
            Id = id;
            _index = index;
            Spot = spot;
            _light = light;
            _downLight = downLight;
            float[] e = spot.Explosion;
            Position = e.Length == 3 ? new Vector3(e[0], e[1], e[2]) : Vector3.Zero;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(ArenaModelWriter.RoomNode);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
            Transform = Matrix4.Identity;
            SetCollision(MiningPlazaPillars.Shape(Spot.ColumnTris, $"plaza_pillar{Spot.Id:x8}"), 0);
            UpdateCollisionTransform(0, Matrix4.Identity);
            UpdateLinkedInverse(0);
            SetCollision(MiningPlazaPillars.Shape(Spot.BottomTris, $"plaza_chunk{Spot.Id:x8}"), 1);
            UpdateCollisionTransform(1, Matrix4.Identity);
            UpdateLinkedInverse(1);
        }

        public override bool GetTargetable() => false;

        protected override bool EnemyGetDrawInfo() => true;

        public override bool Process()
        {
            if (_fall >= 0)
            {
                if (_fall < MiningPlazaPillars.Done * 60) _fall++;
                Step();
            }
            return base.Process();
        }

        // the collision at the timeline's point
        void Step()
        {
            float t = FallTime;
            if (EntityCollision[0] != null)
            {
                UpdateLinkedInverse(0);
                UpdateCollisionTransform(0, MiningPlazaPillars.Model(Spot, t));
            }
            if (!_bottomOff && t >= MiningPlazaPillars.BottomGone)
            {
                _bottomOff = true;
                if (EntityCollision[1]?.Collision is { } c) c.Active = false;
            }
            if (!_rubbleOn && t >= MiningPlazaPillars.Landed)
            {
                _rubbleOn = true;
                SetCollision(MiningPlazaPillars.Shape(Spot.RubbleTris, $"plaza_rubble{Spot.Id:x8}"), 1);
                UpdateCollisionTransform(1, Matrix4.Identity);
                UpdateLinkedInverse(1);
                // the bots switch to the routes over the fallen column (PlayerAi.UpdateNodeDataSetSelection)
                if (_scene.Room?.NodeData is { } nd && _index < nd.SetIndices.Count) nd.SetSelector[_index] = true;
            }
        }

        public void Topple()
        {
            if (_fall >= 0) return;
            _fall = 0;
            Step();
        }

        public override void HandleMessage(MessageInfo info)
        {
            if (info.Message == Message.BeamCollideWith && !Fallen && !EchoesBreakables.Mirror && info.Sender is BeamProjectileEntity beam
                && beam.Owner?.Type != EntityType.EnemyInstance && beam.Beam == BeamType.Missile)
            {
                Topple();
            }
        }

        // a network client's pillar: the host's state (Net/NetWorld.cs). Down in the first snapshot: already fallen there
        int _netBreaks = -1;
        public void NetApply(bool up, bool hit, int breaks)
        {
            if (!up && !Fallen)
            {
                if (_netBreaks < 0)
                {
                    _fall = (int)(MiningPlazaPillars.Done * 60);
                    Step();
                }
                else Topple();
            }
            _netBreaks = breaks;
        }

        // what to draw now: each piece's motion (from its standing world place), pass and light
        // (added to the caller's list, fade 1: no iterator garbage, frame dips 2026-11-01)
        public void AddPieces(List<(Matrix4 Model, int Pass, CrateLight? Light, float Fade)> into)
        {
            float t = FallTime;
            bool landed = t >= MiningPlazaPillars.Landed;
            CrateLight? down = _downLight ?? _light;
            if (Spot.Column >= 0) into.Add((MiningPlazaPillars.Model(Spot, t), Spot.Column, landed ? down : _light, 1));
            if (Spot.Middle >= 0 && t < 0) into.Add((Matrix4.Identity, Spot.Middle, _light, 1));
            if (Spot.Top >= 0 && t < MiningPlazaPillars.TopGone) into.Add((Matrix4.Identity, Spot.Top, _light, 1));
            if (Spot.Bottom >= 0 && t < MiningPlazaPillars.BottomGone) into.Add((Matrix4.Identity, Spot.Bottom, _light, 1));
            if (Spot.Cover >= 0 && !landed) into.Add((Matrix4.Identity, Spot.Cover, _light, 1));
            if (Spot.Rubble >= 0 && landed) into.Add((Matrix4.Identity, Spot.Rubble, down, 1));
        }
    }
}
