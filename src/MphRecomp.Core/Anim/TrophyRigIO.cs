using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Which DS hunter skeleton each Brawl trophy is rigged to (Samus suits all share Samus's rig).
    public static class TrophyRigs
    {
        static readonly Dictionary<string, Hunter> RigOf = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Kanden"] = Hunter.Kanden, ["Sylux"] = Hunter.Sylux, ["Weavel"] = Hunter.Weavel, ["Trace"] = Hunter.Trace,
            ["Noxus"] = Hunter.Noxus, ["Spire"] = Hunter.Spire, ["Samus"] = Hunter.Samus, ["SamusR1"] = Hunter.Samus,
            ["Barriersuit"] = Hunter.Samus, ["Gravitysuit"] = Hunter.Samus, ["Normalsuit"] = Hunter.Samus,
            ["Darksuit"] = Hunter.Samus, ["Darksamus"] = Hunter.Samus, ["Phazonsuit"] = Hunter.Samus, ["SzerosuitR1"] = Hunter.Samus,
            // Retro's own suit models from the owner's Prime 1 / Echoes discs (prime_to_trophy.py): bind pose, Retro's skin
            ["MP1PowerSuit"] = Hunter.Samus, ["MP1GravitySuit"] = Hunter.Samus, ["MP1VariaSuit"] = Hunter.Samus, ["MP1FusionSuit"] = Hunter.Samus,
            ["MP2VariaSuit"] = Hunter.Samus, ["MP2DarkSuit"] = Hunter.Samus, ["MP2LightSuit"] = Hunter.Samus, ["MP2DarkSamus"] = Hunter.Samus, ["MP1FusionTubes"] = Hunter.Samus,
            ["MP3VariaSuit"] = Hunter.Samus, ["MP3ZeroSuit"] = Hunter.Samus, ["MP3DarkSamus"] = Hunter.Samus, ["MP3PEDSuit"] = Hunter.Samus,
            // Metroid Prime 4: Beyond (the owner's own dump, Prime 4 Model Dumper glTF -> mp4_to_trophy.py): bind pose, Retro's skin
            ["MP4Sylux"] = Hunter.Sylux,
            ["MP4DefaultSuit"] = Hunter.Samus, ["MP4PsychicSuit"] = Hunter.Samus, ["MP4PsychicGlove"] = Hunter.Samus,
            ["MP4ViolaSuit"] = Hunter.Samus, ["MP4ViolaSuitIC"] = Hunter.Samus, ["MP4LegacySuit"] = Hunter.Samus,
            // Beyond's Space Pirates (gun in the right forearm, blade on the left), driven by Weavel's clips like the others
            ["MP4SpacePirate"] = Hunter.Weavel, ["MP4SpacePiratePBC"] = Hunter.Weavel,
            // Space Pirates (Prime 1 / Echoes) as custom players: their own Retro rig, driven by WEAVEL's clips (MPH's own Space
            // Pirate; the pirates' skeleton has his layout and an arm blade like his). Only the ones carrying an arm cannon, as
            // the hunters do -- no flying pirates, no melee pirate, no elite forms (Elite, Elite Phazon, Omega, Dark Elite)
            ["MP1SpacePirate"] = Hunter.Weavel, ["MP1PowerTrooper"] = Hunter.Weavel, ["MP1IceTrooper"] = Hunter.Weavel, ["MP1WaveTrooper"] = Hunter.Weavel,
            ["MP1PlasmaTrooper"] = Hunter.Weavel, ["MP2PirateTrooper"] = Hunter.Weavel, ["MP2DarkPirateTrooper"] = Hunter.Weavel, ["MP2PirateCommando"] = Hunter.Weavel,
            ["MP2DarkCommando"] = Hunter.Weavel,
        };
        // the Space Pirates fire one-handed (gun arm only): their off (blade) arm keeps the legs layer's clip while firing
        public static bool OffHandStaysDown(string trophy) =>
            RigOf.TryGetValue(trophy, out var h) && h == Hunter.Weavel && trophy.StartsWith("MP", StringComparison.OrdinalIgnoreCase);
        public static bool IsFiringClip(int clip) =>
            clip is (int)PlayerAnimation.Shoot or (int)PlayerAnimation.Charge or (int)PlayerAnimation.ChargeShoot;
        // "<Trophy>Tpose" = that trophy put in a rest pose (hand_rig_build.py): the same hunter
        public static bool TryHunterFor(string trophy, out Hunter h) =>
            RigOf.TryGetValue(trophy, out h) || (trophy.EndsWith("Tpose", StringComparison.OrdinalIgnoreCase) && RigOf.TryGetValue(trophy[..^5], out h))
            // "<Trophy>Rest" = that trophy imported from a community rig's clean rest pose (rest_import.py): the same hunter
            || (trophy.EndsWith("Rest", StringComparison.OrdinalIgnoreCase) && RigOf.TryGetValue(trophy[..^4], out h));
    }

    // A trophy's hand-marked landmarks (see TrophyLandmarks for the file format).
    public sealed class LandmarkSet
    {
        // joint positions in the trophy's own coordinates, by DS node name ("<leaf>.center" = part centre)
        public Dictionary<string, Vector3> Joints = new(StringComparer.Ordinal);
        // the trophy is sculpted as its own mirror image (e.g. the weapon on the other arm than the game
        // model): flip its X before rigging. L_/R_ names are the rig's sides AFTER the flip.
        public bool MirrorX;
        // extra ownership segments (trophy coordinates): mesh nearest to one of these belongs to that bone --
        // for decorations the skeleton's own segments don't reach (horns, crests, back spikes)
        public List<(string Bone, Vector3 A, Vector3 B)> Segments = new();
        // body parts swapped for the GAME model's own part (e.g. a trophy posed with a blade where the game
        // model carries its gun): the trophy's mesh on that bone is hidden and the DS part rides the bone,
        // scaled about its joint
        public List<(string Bone, float Scale)> Replace = new();
        // attach just PART of the game model's bone mesh: the triangles beyond `Cut` (game units, along that
        // part's long axis from its joint -- e.g. Weavel's gun without the forearm that runs into it), lined up
        // with the trophy limb and seated at `Anchor` (trophy coordinates, e.g. the trophy's wrist)
        public List<(string Bone, float Cut, Vector3 Anchor, float Scale)> Attach = new();
        // trophy mesh pieces to leave out (a piece = connected mesh; each point picks the piece it lies on)
        public List<Vector3> Hide = new();
    }

    // Hand-marked joint positions for a trophy, in the trophy's own (DAE) coordinates: "<dae>.joints.txt"
    // next to it ('#' comments). Lines:
    //   Name x y z                   a joint (the DS skeleton's node names)
    //   <leaf>.center x y z          the middle of a leaf bone's body part (foot, hand, head, cannon...)
    //   <bone>.seg x1 y1 z1 x2 y2 z2 extra ownership segment for <bone> (repeatable)
    //   mirror                       the trophy is the game model's mirror image -- flip X before rigging
    //   replace <bone> [scale]       hide the trophy's part on <bone>, draw the game model's part there
    //   attach <bone> <cut> x y z [scale]  draw the game part of <bone> beyond <cut>, seated at x y z
    //   hide x y z                   leave out the trophy mesh piece at x y z (a blade, a hand)
    // User-local data derived from the user's own files -- never shipped.
    public static class TrophyLandmarks
    {
        public static string PathFor(string daePath) => Path.ChangeExtension(daePath, ".joints.txt");

        public static LandmarkSet Load(string path)
        {
            var set = new LandmarkSet();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                float F(int i) => float.Parse(t[i], ci);
                if (t.Length == 1 && t[0].Equals("mirror", StringComparison.OrdinalIgnoreCase)) set.MirrorX = true;
                else if (t.Length is 2 or 3 && t[0].Equals("replace", StringComparison.OrdinalIgnoreCase))
                    set.Replace.Add((t[1], t.Length == 3 ? F(2) : 1f));
                else if (t.Length is 6 or 7 && t[0].Equals("attach", StringComparison.OrdinalIgnoreCase))
                    set.Attach.Add((t[1], F(2), new Vector3(F(3), F(4), F(5)), t.Length == 7 ? F(6) : 1f));
                else if (t.Length == 4 && t[0].Equals("hide", StringComparison.OrdinalIgnoreCase))
                    set.Hide.Add(new Vector3(F(1), F(2), F(3)));
                else if (t.Length == 7 && t[0].EndsWith(".seg", StringComparison.Ordinal))
                    set.Segments.Add((t[0][..^4], new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(6))));
                else if (t.Length == 4) set.Joints[t[0]] = new Vector3(F(1), F(2), F(3));
                else throw new FormatException($"{Path.GetFileName(path)}: bad line '{raw}'");
            }
            return set;
        }

        public static void Save(string path, IEnumerable<(string Name, Vector3 P, string Note)> joints, string header)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { "# " + header, "# Name x y z   (trophy/DAE coordinates; <leaf>.center = middle of that body part)" };
            foreach (var (name, p, note) in joints)
                lines.Add(string.Format(ci, "{0,-18} {1,10:0.0000} {2,10:0.0000} {3,10:0.0000}", name, p.X, p.Y, p.Z) + (note.Length > 0 ? "   # " + note : ""));
            File.WriteAllLines(path, lines);
        }
    }

    // The user's chunk grouping for a trophy (Rig Studio): which bone owns each trophy vertex -- the way the
    // game's own models are built from rigid per-bone chunks -- and how each joint's seam behaves (rigid, as
    // on the DS, or a narrow blend for a continuous surface). "<dae>.chunks.txt" next to the trophy:
    //   vertices <count>
    //   seam <child bone> rigid | blend <width as a fraction of body height>
    //   run <bone|auto> <count>      consecutive vertices (rig vertex order) owned by that bone
    //   stiff <0|1> <count>          consecutive vertices that keep their shape (1) or may bend (0)
    // User-local data derived from the user's own files -- never shipped.
    public sealed class TrophyChunks
    {
        public int[] Owner = Array.Empty<int>();   // per rig vertex: bone index, -1 = automatic
        public bool[] Stiff = Array.Empty<bool>(); // per rig vertex: never blended (armour plates, blades...)
        public Dictionary<string, (bool Rigid, float Width)> Seams = new(StringComparer.Ordinal);
        public const float DefaultSeamWidth = 0.02f;

        public static string PathFor(string daePath) => Path.ChangeExtension(daePath, ".chunks.txt");

        public static TrophyChunks Load(string path, IReadOnlyList<string> boneNames)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var c = new TrophyChunks();
            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < boneNames.Count; i++) idx[boneNames[i]] = i;
            var owner = new List<int>();
            var stiff = new List<bool>();
            int declared = -1;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t[0] == "vertices" && t.Length == 2) declared = int.Parse(t[1], ci);
                else if (t[0] == "seam" && t.Length >= 3)
                    c.Seams[t[1]] = t[2] == "rigid" ? (true, 0f) : (false, t.Length >= 4 ? float.Parse(t[3], ci) : DefaultSeamWidth);
                else if (t[0] == "run" && t.Length == 3)
                {
                    int b = t[1] == "auto" ? -1 : idx.TryGetValue(t[1], out int bi) ? bi : throw new FormatException($"{Path.GetFileName(path)}: unknown bone '{t[1]}'");
                    owner.AddRange(Enumerable.Repeat(b, int.Parse(t[2], ci)));
                }
                else if (t[0] == "stiff" && t.Length == 3) stiff.AddRange(Enumerable.Repeat(t[1] == "1", int.Parse(t[2], ci)));
                else throw new FormatException($"{Path.GetFileName(path)}: bad line '{raw}'");
            }
            if (declared >= 0 && declared != owner.Count) throw new FormatException($"{Path.GetFileName(path)}: runs cover {owner.Count} vertices, header says {declared}");
            c.Owner = owner.ToArray();
            if (stiff.Count > 0 && stiff.Count != owner.Count) throw new FormatException($"{Path.GetFileName(path)}: stiff runs cover {stiff.Count} vertices, owner runs {owner.Count}");
            c.Stiff = stiff.Count > 0 ? stiff.ToArray() : new bool[owner.Count];
            return c;
        }

        public static void Save(string path, TrophyChunks c, IReadOnlyList<string> boneNames, string header)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { "# " + header, $"vertices {c.Owner.Length}" };
            foreach (var (bone, (rigid, width)) in c.Seams.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                lines.Add(rigid ? $"seam {bone} rigid" : string.Format(ci, "seam {0} blend {1:0.####}", bone, width));
            for (int i = 0; i < c.Owner.Length;)
            {
                int j = i;
                while (j < c.Owner.Length && c.Owner[j] == c.Owner[i]) j++;
                lines.Add($"run {(c.Owner[i] < 0 ? "auto" : boneNames[c.Owner[i]])} {j - i}");
                i = j;
            }
            if (c.Stiff.Length == c.Owner.Length && c.Stiff.Any(x => x))
                for (int i = 0; i < c.Stiff.Length;)
                {
                    int j = i;
                    while (j < c.Stiff.Length && c.Stiff[j] == c.Stiff[i]) j++;
                    lines.Add($"stiff {(c.Stiff[i] ? 1 : 0)} {j - i}");
                    i = j;
                }
            File.WriteAllLines(path, lines);
        }
    }

    // A fitted rig is DERIVED data (from the user's ROM + the user's trophy export): cached next to the
    // trophy so the ~1 minute fit runs once, never shipped. Keyed by a hash of the .dae bytes + the
    // rigger version, so re-exporting the trophy or changing the rigger refits automatically.
    public static class TrophyRigIO
    {
        public const int Version = 20;   // bump whenever TrophyRigger's output changes
        const uint Magic = 0x4749524D;  // "MRIG"

        public static ulong HashFile(string path)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in File.ReadAllBytes(path)) { h ^= b; h *= 1099511628211UL; }
            h ^= (ulong)Version; h *= 1099511628211UL;
            return h;
        }

        // cache key: the .dae bytes, the landmark + chunk files (if any), and the rigger version
        public static ulong CacheKey(string daePath)
        {
            ulong h = HashFile(daePath);
            string jp = TrophyLandmarks.PathFor(daePath);
            if (File.Exists(jp)) h = (h ^ HashFile(jp)) * 1099511628211UL;
            string cp = TrophyChunks.PathFor(daePath);
            if (File.Exists(cp)) h = (h ^ HashFile(cp)) * 1099511628211UL;
            return h;
        }

        public static void Save(string path, TrophyRig rig, ulong daeHash)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Magic); w.Write(Version); w.Write(daeHash); w.Write(rig.Rig);
            WriteM(w, rig.TrophyToRig);
            w.Write(rig.Lengths.Length);
            for (int i = 0; i < rig.Lengths.Length; i++)
            {
                w.Write(rig.Lengths[i]); WriteM(w, rig.BindWorld[i]);
                var o = rig.Offsets.Length > i ? rig.Offsets[i] : null;
                w.Write(o.HasValue); if (o.HasValue) { w.Write(o.Value.X); w.Write(o.Value.Y); w.Write(o.Value.Z); }
            }
            w.Write(rig.Vertices.Length);
            foreach (var v in rig.Vertices) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            foreach (int b in rig.Bones) w.Write((byte)b);
            foreach (float f in rig.Weights) w.Write(f);
            w.Write(rig.ChamferStart); w.Write(rig.ChamferGlobal); w.Write(rig.ChamferFinal); w.Write(rig.RefPose);
            w.Write(rig.Attachments.Length);
            foreach (var a in rig.Attachments)
            {
                w.Write(a.Node); w.Write(a.Scale); w.Write(a.Cut); w.Write(a.HidesOwnMesh);
                w.Write(a.Anchor.HasValue); if (a.Anchor is Vector3 an) { w.Write(an.X); w.Write(an.Y); w.Write(an.Z); }
            }
            w.Write(rig.Hidden.Length);
            foreach (bool h in rig.Hidden) w.Write(h);
            w.Write(rig.BindPos != null);
            if (rig.BindPos != null)
            {
                foreach (var p in rig.BindPos) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                foreach (var q in rig.BindRot!) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
            }
            w.Write(rig.CutTris.Count);
            foreach (var (a, b, c) in rig.CutTris) { w.Write(a); w.Write(b); w.Write(c); }
        }

        // null if missing, from another rigger version, or for a different .dae (anyKey: accept any .dae/joints key --
        // for a BEFORE snapshot, which was made before the joints changed)
        public static TrophyRig? Load(string path, ulong daeHash, bool anyKey = false)
        {
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return null;
            if (r.ReadUInt64() != daeHash && !anyKey) return null;
            var rig = new TrophyRig { Rig = r.ReadString(), TrophyToRig = ReadM(r) };
            int n = r.ReadInt32();
            rig.Lengths = new float[n]; rig.BindWorld = new Matrix4[n]; rig.InvBind = new Matrix4[n]; rig.Offsets = new Vector3?[n];
            for (int i = 0; i < n; i++)
            {
                rig.Lengths[i] = r.ReadSingle(); rig.BindWorld[i] = ReadM(r); rig.InvBind[i] = Matrix4.Invert(rig.BindWorld[i]);
                if (r.ReadBoolean()) rig.Offsets[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            int nv = r.ReadInt32();
            rig.Vertices = new Vector3[nv];
            for (int i = 0; i < nv; i++) rig.Vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            rig.Bones = new int[nv * 4]; rig.Weights = new float[nv * 4];
            for (int i = 0; i < nv * 4; i++) rig.Bones[i] = r.ReadByte();
            for (int i = 0; i < nv * 4; i++) rig.Weights[i] = r.ReadSingle();
            rig.ChamferStart = r.ReadSingle(); rig.ChamferGlobal = r.ReadSingle(); rig.ChamferFinal = r.ReadSingle(); rig.RefPose = r.ReadString();
            rig.Attachments = new TrophyRig.Attachment[r.ReadInt32()];
            for (int i = 0; i < rig.Attachments.Length; i++)
            {
                var a = new TrophyRig.Attachment { Node = r.ReadInt32(), Scale = r.ReadSingle(), Cut = r.ReadSingle(), HidesOwnMesh = r.ReadBoolean() };
                if (r.ReadBoolean()) a.Anchor = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                rig.Attachments[i] = a;
            }
            rig.Hidden = new bool[r.ReadInt32()];
            for (int i = 0; i < rig.Hidden.Length; i++) rig.Hidden[i] = r.ReadBoolean();
            if (r.ReadBoolean())
            {
                rig.BindPos = new Vector3[nv]; rig.BindRot = new Quaternion[nv];
                for (int i = 0; i < nv; i++) rig.BindPos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                for (int i = 0; i < nv; i++) rig.BindRot[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
            for (int i = r.ReadInt32(); i > 0; i--) rig.CutTris.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
            rig.BuildIndex();
            return rig;
        }

        // replace a fitted rig's weights with ones from a file (see the "mia" variant)
        static void LoadWeights(string path, TrophyRig rig, DsSkeleton sk, Action<string>? log)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lines = File.ReadAllLines(path);
            if (lines.Length != rig.Vertices.Length) throw new FormatException($"{Path.GetFileName(path)}: {lines.Length} lines, the rig has {rig.Vertices.Length} vertices");
            for (int v = 0; v < lines.Length; v++)
            {
                var t = lines[v].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int k = 0;
                for (; k < 4 && 2 * k + 1 < t.Length; k++)
                {
                    int b = sk.IndexOf(t[2 * k]);
                    if (b < 0) throw new FormatException($"{Path.GetFileName(path)}: unknown bone '{t[2 * k]}'");
                    rig.Bones[v * 4 + k] = b; rig.Weights[v * 4 + k] = float.Parse(t[2 * k + 1], ci);
                }
                for (; k < 4; k++) { rig.Bones[v * 4 + k] = rig.Bones[v * 4]; rig.Weights[v * 4 + k] = 0; }
            }
            log?.Invoke($"rig: weights replaced from {Path.GetFileName(path)}");
        }

        static void WriteM(BinaryWriter w, Matrix4 m)
        {
            w.Write(m.M11); w.Write(m.M12); w.Write(m.M13); w.Write(m.M14); w.Write(m.M21); w.Write(m.M22); w.Write(m.M23); w.Write(m.M24);
            w.Write(m.M31); w.Write(m.M32); w.Write(m.M33); w.Write(m.M34); w.Write(m.M41); w.Write(m.M42); w.Write(m.M43); w.Write(m.M44);
        }
        static Matrix4 ReadM(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // Load the cached rig for a trophy .dae, or fit + cache it. `corners` = the DAE's triangle corners.
        public static TrophyRig LoadOrFit(string daePath, Model hunterModel, IReadOnlyList<Vector3> corners, Action<string>? log = null)
        {
            string cache = Path.ChangeExtension(daePath, ".mphrig");
            ulong hash = CacheKey(daePath);
            var rig = Load(cache, hash);
            if (rig != null) { log?.Invoke($"rig: loaded cache {Path.GetFileName(cache)}"); return rig; }
            log?.Invoke("rig: no valid cache -- fitting (about a minute)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            rig = TrophyRigger.Fit(hunterModel, corners, FitOptions(daePath, hunterModel, log));
            log?.Invoke($"rig: fitted in {sw.Elapsed.TotalSeconds:0.0}s (Chamfer {rig.ChamferStart:0.000} -> {rig.ChamferFinal:0.000}); saving {Path.GetFileName(cache)}");
            try { Save(cache, rig, hash); } catch (Exception ex) { log?.Invoke("rig: cache save failed: " + ex.Message); }
            return rig;
        }

        // hand-marked joints and chunk grouping, when present, drive the fit (and are part of the cache key)
        static TrophyRigger.Options FitOptions(string daePath, Model hunterModel, Action<string>? log)
        {
            string jp = TrophyLandmarks.PathFor(daePath);
            string cp = TrophyChunks.PathFor(daePath);
            return new TrophyRigger.Options
            {
                Log = log,
                Landmarks = File.Exists(jp) ? TrophyLandmarks.Load(jp) : null,
                Chunks = File.Exists(cp) ? TrophyChunks.Load(cp, new DsSkeleton(hunterModel).Names) : null,
                CornerMaterial = CornerMaterials(MphRecomp.Assets.DaeModel.Load(daePath)),
            };
        }

        // per corner (in the order the renderer and the tools build the corner list: meshes in file order, each mesh's
        // positions): a number per material name
        public static int[] CornerMaterials(MphRecomp.Assets.DaeModel dae)
        {
            var ids = new Dictionary<string, int>(); var res = new List<int>();
            foreach (var m in dae.Meshes)
            {
                if (!ids.TryGetValue(m.Material, out int id)) ids[m.Material] = id = ids.Count;
                for (int i = 0; i < m.Positions.Count; i++) res.Add(id);
            }
            return res.ToArray();
        }

        // Alternative rigs for side-by-side comparison with the rig above (the viewer flips between them). Every
        // one: bones claim only mesh pieces they mostly run through (claim 0.5), then the trophy is straightened
        // and bound in the straightened pose (TrophyUnpose.Straighten).
        //   straight -- straightened into the game's stance (shoulders at the centre of their motion)
        //   clean    -- + clean boundaries: armour panels between creases move whole with one bone
        //   tpose    -- clean, straightened into a literal T-pose instead (as the hand-made rigs are)
        //   hinge    -- clean, with a quarter-width blend at each joint: plates pivot on a thin line there instead of
        //               bending over a wide band (chitin / armour, e.g. Trace)
        //   mia      -- a SECOND OPINION: skinning weights predicted by Make-It-Animatable (a local AI auto-rigger,
        //               run on the PC) folded onto the game's bones -- "<dae>.mia.weights.txt", one line per rig
        //               vertex: up to 4 "<bone> <weight>" pairs -- on our skeleton, straightened
        //   rigid    -- like the game's own models: every piece moves SOLIDLY with one bone and only the joints bend.
        //               The clean rig's ownership becomes chunks (RigidChunks), refitted through the chunk path (a narrow
        //               blend band only where neighbouring bones meet, never inside armour), straightened with every
        //               single-bone vertex carried whole (only the joint bands are reshaped)
        //   retro    -- RETRO'S OWN RIG copied: built like rigid, but from "<dae>.retro.txt" (retro_copy.py on the PC, from
        //               the owner's own Prime disc -- study data, never shipped), which gives EVERY vertex the bone Retro's
        //               skin gives the same spot of the same texture, marks where Retro blends, and places the joints
        //               where Retro's are; the rigid rules only decide what it has no answer for (a gun Nintendo added)
        //   pieces   -- THE STATUE'S OWN PIECES: built like retro, from "<dae>.pieces.txt" (make_pieces.py on the PC): the
        //               posed trophy's geometry as it is, each piece on one bone (a hand-made rig of the same mesh decides
        //               which, the game's model decides the pads), soft only in joint bands, joints on the seams between
        //               pieces, head and feet facing as the hand-made rig's rest pose has them
        // Each is cached as "<dae>.<key>.mphrig".
        public sealed record RigVariant(string Key, string Label, float Claim, float PanelDominance, float Crease, bool TPose, string? Weights = null, float Blend = 1f, bool Rigid = false, string? Layout = null);
        public static readonly RigVariant[] Variants =
        {
            new("straight", "NEW (straightened)", 0.5f, 0f, 35f, false),
            new("clean", "CLEAN (armour panels whole)", 0.5f, 0.6f, 70f, false),
            new("tpose", "CLEAN + T-POSE", 0.5f, 0.6f, 70f, true),
            new("hinge", "HINGE (plates pivot at joints)", 0.5f, 0.6f, 70f, false, null, 0.25f),
            new("mia", "MIA (AI auto-rigger's weights)", 0.5f, 0f, 35f, false, ".mia.weights.txt"),
            new("rigid", "RIGID (solid pieces, bends only at joints)", 0.5f, 0.6f, 70f, false, null, 1f, true),
            new("retro", "RETRO (Retro's own rig, copied)", 0.5f, 0.6f, 70f, false, null, 1f, true, ".retro.txt"),
            new("pieces", "PIECES (the statue's own pieces, one bone each)", 0.5f, 0.6f, 70f, false, null, 1f, true, ".pieces.txt"),
        };
        // the layout a rigid-built variant starts from: its own file, else the hand-made fighter layout
        public static string LayoutPathFor(string daePath, RigVariant v) => Path.ChangeExtension(daePath, v.Layout ?? ".fighter.txt");
        public const float StraightClaim = 0.5f;
        public static string VariantPathFor(string daePath, RigVariant v) => Path.ChangeExtension(daePath, "." + v.Key + ".mphrig");
        public static ulong VariantKey(string daePath, RigVariant v)
        {
            ulong h = (CacheKey(daePath) ^ 0x544847494152545FUL) * 1099511628211UL;   // = the original "straight" key
            if (v.Key == "straight") return h;
            if (v.Weights != null && File.Exists(Path.ChangeExtension(daePath, v.Weights))) h = (h ^ HashFile(Path.ChangeExtension(daePath, v.Weights))) * 1099511628211UL;
            foreach (char c in $"{v.Key}|{v.Claim}|{v.PanelDominance}|{v.Crease}|{v.TPose}|{v.Blend}" + (v.Rigid ? $"|rigid{RigidChunksVersion}" : "")) { h ^= c; h *= 1099511628211UL; }
            // the rigid rig also depends on a hand-made layout, when one is given
            if (v.Rigid && File.Exists(LayoutPathFor(daePath, v))) h = (h ^ HashFile(LayoutPathFor(daePath, v))) * 1099511628211UL;
            return h;
        }
        // Chunks for the RIGID rig from a fitted rig, the way the game's own models are built:
        //  1. every vertex wholly on ONE bone: the bone of the game model's own part at that spot (step 0 below), else
        //     the fit's main bone;
        //  2. ARMOUR SHELLS whole: the surface splits into panels at its own creases (faces meeting at more than 70 deg,
        //     as the clean rig does); a panel whose area is at least ShellMajority on one bone moves wholly with it, even
        //     where a little of it reaches past a joint (a shin guard's spike rising above the knee is part of the shin);
        //     a sleeve that really wraps a joint (about half on each side) is still split there;
        //  3. separate small pieces (a gun, a pad, a tube) whole on their majority bone;
        //  4. shells and pieces are STIFF -- they never blend, so where they meet plain surface the joint's soft side takes
        //     the whole bend; plain surface on both sides of a joint shares it;
        //  5. stray fragments -- a few vertices of one bone inside another's region, which would each be ringed by
        //     stretching seams -- join the region around them.
        public const float ShellMajority = 0.75f, ShellCreaseDeg = 70f, PieceFraction = 0.05f, IntrusionShare = 0.25f;
        public const float GameVoteReach = 0.08f;   // a game vertex farther than this (x height) has no say
        public const float ShoulderCapT = 0.1f;   // shoulder cap = upper-arm surface less than this far (x upper-arm length) past the joint
        public const float TemplateReach = 0.2f;   // a hand-made layout's torso answer may reach this far (x limb length) down a limb
        public const int RigidChunksVersion = 10;   // part of the rigid rig's cache key: bump whenever RigidChunks changes
        // A HAND-MADE RIG'S LAYOUT transferred onto the trophy ("<dae>.fighter.txt", made on the PC by
        // brawl_extract/rig_work/session16/scripts/fighter_transfer.py from Nintendo's own Brawl fighter -- the owner's disc,
        // study data, never shipped): per rig vertex the game bone the fighter's weights give that spot ("-" = no answer)
        // and whether it is SOFT (the fighter blends there: its ribbed abdomen), plus seam widths. The rigid rig's chunk rules
        // start from it instead of guessing the torso.
        public sealed class FighterTemplate
        {
            public int[] Owner = Array.Empty<int>(); public bool[] Soft = Array.Empty<bool>();
            public Dictionary<string, (bool Rigid, float Width)> Seams = new(StringComparer.Ordinal);
            // "head_turn" (9 numbers, rig space, p' = R p): the turn laying the trophy's helmet, about its joint in the game's
            // Idle, onto the hand-made model's straight-ahead one (see ApplyHeadTurn); null = none
            public Matrix3? HeadTurn;
            // "exact": the layout is a whole rig (Retro's), not a torso hint -- every vertex it answers keeps that bone, the
            // rigid rules (shoulder caps, shells, intruders, pieces, fragments) only decide the rest
            public bool Exact;
            // "joint <name> x y z" (trophy coordinates): replaces that landmark for this rig only
            public Dictionary<string, Vector3> Joints = new(StringComparer.Ordinal);
        }
        public static string FighterTemplatePathFor(string daePath) => Path.ChangeExtension(daePath, ".fighter.txt");
        public static FighterTemplate LoadFighterTemplate(string path, IReadOnlyList<string> boneNames)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < boneNames.Count; i++) idx[boneNames[i]] = i;
            var t = new FighterTemplate(); var owner = new List<int>(); var soft = new List<bool>(); int declared = -1;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var w = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (w[0] == "vertices") declared = int.Parse(w[1], ci);
                else if (w[0] == "seam" && w.Length >= 3) t.Seams[w[1]] = w[2] == "rigid" ? (true, 0f) : (false, w.Length >= 4 ? float.Parse(w[3], ci) : TrophyChunks.DefaultSeamWidth);
                else if (w[0] == "run") owner.AddRange(Enumerable.Repeat(w[1] == "-" ? -1 : idx.TryGetValue(w[1], out int b) ? b : throw new FormatException($"{Path.GetFileName(path)}: unknown bone '{w[1]}'"), int.Parse(w[2], ci)));
                else if (w[0] == "soft") soft.AddRange(Enumerable.Repeat(w[1] == "1", int.Parse(w[2], ci)));
                else if (w[0] == "exact") t.Exact = true;
                else if (w[0] == "joint" && w.Length >= 5) t.Joints[w[1]] = new Vector3(float.Parse(w[2], ci), float.Parse(w[3], ci), float.Parse(w[4], ci));
                else if (w[0] == "head_turn" && w.Length >= 10)
                {
                    var m = w.Skip(1).Take(9).Select(x => float.Parse(x, ci)).ToArray();
                    // stored for column vectors; OpenTK multiplies row vectors, so keep the transpose
                    t.HeadTurn = new Matrix3(m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]);
                }
                else throw new FormatException($"{Path.GetFileName(path)}: bad line '{raw}'");
            }
            if (declared >= 0 && (owner.Count != declared || soft.Count != declared)) throw new FormatException($"{Path.GetFileName(path)}: covers {owner.Count}/{soft.Count} vertices, header says {declared}");
            t.Owner = owner.ToArray(); t.Soft = soft.ToArray();
            return t;
        }

        public static TrophyChunks RigidChunks(TrophyRig rig, IReadOnlyList<Vector3> corners, DsSkeleton sk, Action<string>? log = null, FighterTemplate? template = null)
        {
            int nv = rig.Vertices.Length;
            var owner = new int[nv]; var stiff = new bool[nv];
            for (int v = 0; v < nv; v++) owner[v] = rig.Bones[v * 4];
            var pos = rig.Vertices.Select(p => Vector3.TransformPosition(p, rig.TrophyToRig)).ToArray();
            // a hand-made layout, when given, decides the torso outright (the head vote below is then skipped); its soft
            // vertices never become stiff (the abdomen must bend)
            bool useTemplate = template != null && template.Owner.Length == nv && template.Soft.Length == nv;
            if (template != null && !useTemplate) log?.Invoke($"  fighter template IGNORED: it covers {template.Owner.Length} vertices, the trophy has {nv}");
            var soft = new bool[nv]; var locked = new bool[nv];
            if (useTemplate && template!.Exact)
            {
                int given = 0;
                for (int v = 0; v < nv; v++)
                {
                    soft[v] = template.Soft[v];
                    if (template.Owner[v] < 0) continue;
                    owner[v] = template.Owner[v]; locked[v] = true; stiff[v] = !soft[v]; given++;
                }
                log?.Invoke($"  rigid chunks: a whole rig's layout gives {given} of {nv} vertices their bone outright ({soft.Count(s => s)} soft, where it blends)");
            }
            else if (useTemplate)
            {
                // ...but never past a LIMB's joint: a torso bone's answer that lies more than TemplateReach of a limb's
                // length down that limb (Nintendo's pelvis piece laid over Fusion Samus's bulkier hips reached halfway down
                // both thighs, and the thigh seam tore at every step) stays with the limb
                var limbs = new List<(int Parent, Vector3 J, Vector3 U, float Len)>();
                for (int c = 0; c < sk.Count; c++)
                {
                    int above = sk.Parent[c];
                    if (above < 0 || !rig.FittedDsNode.Contains(c)) continue;
                    int end = Enumerable.Range(0, sk.Count).FirstOrDefault(k => sk.Parent[k] == c && rig.FittedDsNode.Contains(k), -1);
                    if (end < 0) continue;   // leaves (head, pads, hands) are decided by the layout itself
                    var j = rig.BindWorld[c].ExtractTranslation(); var e = rig.BindWorld[end].ExtractTranslation();
                    float len = (e - j).Length;
                    if (len > 1e-5f) limbs.Add((above, j, (e - j) / len, len));
                }
                int given = 0, held = 0;
                for (int v = 0; v < nv; v++)
                {
                    soft[v] = template!.Soft[v];
                    int b = template.Owner[v];
                    if (b < 0) continue;
                    if (limbs.Any(l => l.Parent == b && Vector3.Dot(pos[v] - l.J, l.U) > TemplateReach * l.Len && b != owner[v]))
                    { held++; soft[v] = false; continue; }
                    owner[v] = b; given++;
                }
                log?.Invoke($"  rigid chunks: a hand-made layout gives {given} vertices their bone ({soft.Count(s => s)} soft, where it blends); {held} reaching down a limb stay with it");
            }
            // 0. the HEAD (the part hanging off the chest) is decided by the game model itself: its mesh posed on the trophy's fitted skeleton (rig.FittedDs, same
            //    space) votes -- the nearest few game vertices, closer ones counting more. A whole helmet is the head's
            //    because the game's helmet is there; ownership by the nearest bone LINE gave a big helmet's back to the
            //    chest, whose line runs closer (the head's line is short). Limbs keep the fit's owner: the game's arm
            //    and leg meshes have the game's proportions, not the trophy's, so away from the joints they don't lie
            //    on the trophy's limbs (voting everywhere made thighs and boots jagged). Where the game model has
            //    nothing near (a trophy's own decoration), the fit's owner stays.
            //    "The chest" = the nearest bone above the head that carries game mesh (Spine_2 on Samus; Trace's head hangs
            //    off a Collar bone through a Neck_1 that carries none); what hangs off it = bones carrying mesh with none
            //    below them whose nearest mesh-carrying bone above is the chest (Samus: head + shoulder pads).
            var carries = rig.FittedDsNode.ToHashSet();
            int CarryingAbove(int b) { for (int p = sk.Parent[b]; p >= 0; p = sk.Parent[p]) if (carries.Contains(p)) return p; return -1; }
            bool CarriesBelow(int b)
            {
                foreach (int c in carries) for (int p = sk.Parent[c]; p >= 0; p = sk.Parent[p]) if (p == b) return true;
                return false;
            }
            int head = sk.IndexOf("Head_1");
            int chest = head >= 0 && carries.Contains(head) ? CarryingAbove(head) : -1;
            // ...but only the HEAD is voted on: the game's shoulder pads are Varia-style, and on a suit without them (Fusion
            // Samus) the pad meshes lie over her back, which then went to the pad bones and tore at every shrug. Pads keep
            // the fit's owner (which already gives real pads, Barriersuit's, to the pad bones).
            var hanging = Enumerable.Range(0, sk.Count).Where(b => b == head && chest >= 0 && carries.Contains(b) && CarryingAbove(b) == chest && !CarriesBelow(b)).ToHashSet();
            if (!useTemplate && hanging.Count > 0 && rig.FittedDs.Length == rig.FittedDsNode.Length && rig.FittedDs.Length > 0)
            {
                float h = pos.Max(p => p.Y) - pos.Min(p => p.Y), far = GameVoteReach * h;
                const int K = 6;
                var bestD = new float[K]; var bestN = new int[K];
                int voted = 0;
                for (int v = 0; v < nv; v++)
                {
                    // ONE WAY: chest surface may join the head, never the reverse -- a game head smaller than the trophy's
                    // (Noxus's tall helmet and crest) would otherwise hand most of the trophy's helmet to the chest
                    if (owner[v] != chest) continue;
                    Array.Fill(bestD, float.MaxValue);
                    for (int g = 0; g < rig.FittedDs.Length; g++)
                    {
                        float d = (rig.FittedDs[g] - pos[v]).LengthSquared;
                        if (d >= bestD[K - 1]) continue;
                        int k = K - 1;
                        while (k > 0 && bestD[k - 1] > d) { bestD[k] = bestD[k - 1]; bestN[k] = bestN[k - 1]; k--; }
                        bestD[k] = d; bestN[k] = rig.FittedDsNode[g];
                    }
                    if (MathF.Sqrt(bestD[0]) > far) continue;
                    var vote = new Dictionary<int, float>();
                    for (int k = 0; k < K; k++) vote[bestN[k]] = vote.GetValueOrDefault(bestN[k]) + 1f / (MathF.Sqrt(bestD[k]) + 0.002f * h);
                    int top = vote.OrderByDescending(kv => kv.Value).First().Key;
                    if (top != chest && !hanging.Contains(top)) continue;
                    if (top != owner[v]) voted++;
                    owner[v] = top;
                }
                log?.Invoke($"  rigid chunks: the game model's own parts decide {voted} chest / {string.Join("/", hanging.Select(b => sk.Names[b]))} vertices differently from the fit");
            }
            // 0b. SHOULDER CAPS: the upper-arm bone keeps only the arm past its joint. Surface it owns on the chest side of
            //     the shoulder joint (the cap / hump above the arm) goes to that side's shoulder PAD bone -- which is how
            //     the game builds it: its pads cover the shoulder masses and move nearly with the chest (left pad 8 deg
            //     from the chest at the 95th percentile of every clip, while the left upper arm swings 123) and halfway
            //     on the gun arm (38 vs 44). Owned by the upper arm, a Fusion suit's shoulder hump swung the full 123 and
            //     tore the chest along its edge. No pad bone: the bone the arm hangs from takes it.
            {
                var capLog = new List<string>();
                foreach (string side in new[] { "L_", "R_" })
                {
                    int arm = sk.IndexOf(side + "shoulder");
                    if (arm < 0 || !carries.Contains(arm)) continue;
                    int elbow = Enumerable.Range(0, sk.Count).FirstOrDefault(c => sk.Parent[c] == arm && carries.Contains(c), -1);
                    if (elbow < 0) continue;
                    int pad = sk.IndexOf(side + "varias2_SDK");
                    int to = pad >= 0 && carries.Contains(pad) ? pad : CarryingAbove(arm);
                    if (to < 0) continue;
                    var j = rig.BindWorld[arm].ExtractTranslation(); var e = rig.BindWorld[elbow].ExtractTranslation();
                    float len = (e - j).Length;
                    if (len < 1e-5f) continue;
                    var u = (e - j) / len;
                    int moved = 0;
                    for (int v = 0; v < nv; v++)
                        if (owner[v] == arm && !locked[v] && Vector3.Dot(pos[v] - j, u) < ShoulderCapT * len) { owner[v] = to; moved++; }
                    if (moved > 0) capLog.Add($"{sk.Names[arm]}->{sk.Names[to]}={moved}");
                }
                if (capLog.Count > 0) log?.Invoke("  rigid chunks: shoulder caps (upper-arm surface on the chest side of its joint) go to the pad / chest: " + string.Join(" ", capLog));
            }
            var adj = new HashSet<int>[nv];
            for (int v = 0; v < nv; v++) adj[v] = new HashSet<int>();
            var tris = new List<int>();
            for (int i = 0; i + 2 < corners.Count; i += 3)
            {
                int a = rig.IndexOf(new System.Numerics.Vector3(corners[i].X, corners[i].Y, corners[i].Z));
                int b = rig.IndexOf(new System.Numerics.Vector3(corners[i + 1].X, corners[i + 1].Y, corners[i + 1].Z));
                int c = rig.IndexOf(new System.Numerics.Vector3(corners[i + 2].X, corners[i + 2].Y, corners[i + 2].Z));
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c || rig.HidesTriangle(a, b, c)) continue;
                adj[a].Add(b); adj[a].Add(c); adj[b].Add(a); adj[b].Add(c); adj[c].Add(a); adj[c].Add(b);
                tris.Add(a); tris.Add(b); tris.Add(c);
            }
            int nt = tris.Count / 3;
            // 2. panels between creases
            var fn = new Vector3[nt]; var area = new float[nt];
            for (int t = 0; t < nt; t++)
            {
                var cr = Vector3.Cross(pos[tris[3 * t + 1]] - pos[tris[3 * t]], pos[tris[3 * t + 2]] - pos[tris[3 * t]]);
                area[t] = 0.5f * cr.Length; fn[t] = cr.LengthSquared > 1e-20f ? cr.Normalized() : Vector3.Zero;
            }
            var par = new int[nt]; for (int t = 0; t < nt; t++) par[t] = t;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
            var edgeTris = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < nt; t++)
                for (int c = 0; c < 3; c++)
                {
                    int a = tris[3 * t + c], b = tris[3 * t + (c + 1) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (!edgeTris.TryGetValue(key, out var l)) edgeTris[key] = l = new List<int>();
                    l.Add(t);
                }
            float cosCrease = MathF.Cos(ShellCreaseDeg * MathF.PI / 180f);
            foreach (var l in edgeTris.Values)
                for (int x = 0; x < l.Count; x++)
                    for (int y = x + 1; y < l.Count; y++)
                        if (fn[l[x]] != Vector3.Zero && fn[l[y]] != Vector3.Zero && Vector3.Dot(fn[l[x]], fn[l[y]]) >= cosCrease)
                            par[Find(l[x])] = Find(l[y]);
            var panelVotes = new Dictionary<int, Dictionary<int, float>>();
            for (int t = 0; t < nt; t++)
            {
                int p = Find(t);
                if (!panelVotes.TryGetValue(p, out var vv)) panelVotes[p] = vv = new Dictionary<int, float>();
                for (int c = 0; c < 3; c++) vv[owner[tris[3 * t + c]]] = vv.GetValueOrDefault(owner[tris[3 * t + c]]) + area[t] / 3f;
            }
            var shellOf = new Dictionary<int, int>();
            foreach (var (p, vv) in panelVotes)
            {
                float sum = vv.Values.Sum(); var top = vv.OrderByDescending(kv => kv.Value).First();
                if (sum > 0 && top.Value >= ShellMajority * sum) shellOf[p] = top.Key;
            }
            // a vertex on shells of different bones (a crease where two shells meet) keeps its own owner and stays soft
            var vShell = new int[nv]; Array.Fill(vShell, -1);
            for (int t = 0; t < nt; t++)
            {
                if (!shellOf.TryGetValue(Find(t), out int so)) continue;
                for (int c = 0; c < 3; c++) { int v = tris[3 * t + c]; vShell[v] = vShell[v] == -1 || vShell[v] == so ? so : -2; }
            }
            int shellV = 0;
            for (int v = 0; v < nv; v++) if (vShell[v] >= 0 && !soft[v] && !locked[v]) { owner[v] = vShell[v]; stiff[v] = true; shellV++; }
            // 2b. INTRUDERS in a panel no single bone holds: a neighbouring bone owning a small corner of the panel that is
            //     not where its own surface is (under IntrusionShare of that bone's whole area) is sticking in from next
            //     door -- a shin guard's spike rising past the knee into the thigh's reach, the collar round the neck
            //     taken by the helmet's bone -- so that corner moves solidly with the panel's main bone. A bone whose
            //     surface mostly IS in the panel (the foot in a guard that runs on into the boot) keeps its part: the
            //     panel really bends there.
            var boneArea = new Dictionary<int, float>();
            foreach (var vv in panelVotes.Values) foreach (var (b, a) in vv) boneArea[b] = boneArea.GetValueOrDefault(b) + a;
            var intruders = new Dictionary<(int P, int B), int>();   // (panel, intruding bone) -> the panel's main bone
            foreach (var (p, vv) in panelVotes)
            {
                if (shellOf.ContainsKey(p)) continue;
                int main = vv.OrderByDescending(kv => kv.Value).First().Key;
                foreach (var (b, a) in vv)
                    if (b != main && (sk.Parent[b] == main || sk.Parent[main] == b) && a < IntrusionShare * boneArea[b]) intruders[(p, b)] = main;
            }
            int intrV = 0; var intrLog = new Dictionary<string, int>();
            for (int t = 0; t < nt; t++)
            {
                int p = Find(t);
                for (int c = 0; c < 3; c++)
                {
                    int v = tris[3 * t + c];
                    if (stiff[v] || soft[v] || locked[v] || !intruders.TryGetValue((p, owner[v]), out int to)) continue;
                    string k = $"{sk.Names[owner[v]]}->{sk.Names[to]}";
                    intrLog[k] = intrLog.GetValueOrDefault(k) + 1;
                    owner[v] = to; stiff[v] = true; intrV++;
                }
            }
            if (intrV > 0) log?.Invoke($"  rigid chunks: {intrV} vertices sticking into a panel from a neighbouring bone go with the panel's own bone: " + string.Join(" ", intrLog.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
            // 3. separate small pieces whole
            var island = new int[nv]; Array.Fill(island, -1);
            var pieces = new List<List<int>>();
            for (int v0 = 0; v0 < nv; v0++)
            {
                if (island[v0] >= 0) continue;
                var list = new List<int> { v0 }; island[v0] = pieces.Count;
                for (int k = 0; k < list.Count; k++) foreach (int u in adj[list[k]]) if (island[u] < 0) { island[u] = pieces.Count; list.Add(u); }
                pieces.Add(list);
            }
            int pieceN = 0;
            foreach (var list in pieces)
            {
                if (list.Count > PieceFraction * nv) continue;
                int top = list.GroupBy(v => owner[v]).OrderByDescending(g => g.Count()).First().Key;
                foreach (int v in list) if (!locked[v]) { owner[v] = top; stiff[v] = !soft[v]; }
                pieceN++;
            }
            // fragments: connected same-owner regions under this size that are not a whole separate piece
            int minSize = Math.Max(12, nv / 400), merged = 0, fragments = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                var comp = new int[nv]; Array.Fill(comp, -1);
                var members = new List<List<int>>();
                for (int v0 = 0; v0 < nv; v0++)
                {
                    if (comp[v0] >= 0) continue;
                    var list = new List<int> { v0 }; comp[v0] = members.Count;
                    for (int k = 0; k < list.Count; k++)
                        foreach (int u in adj[list[k]]) if (comp[u] < 0 && owner[u] == owner[v0]) { comp[u] = members.Count; list.Add(u); }
                    members.Add(list);
                }
                int changed = 0;
                foreach (var list in members)
                {
                    if (list.Count >= minSize || list.Any(v => stiff[v] || locked[v])) continue;   // shells, pieces and a whole rig's answers are deliberate
                    var around = new Dictionary<int, int>();
                    foreach (int v in list) foreach (int u in adj[v]) if (owner[u] != owner[v]) around[owner[u]] = around.GetValueOrDefault(owner[u]) + 1;
                    if (around.Count == 0) continue;   // a whole small piece of its own: stays with its bone
                    int to = around.OrderByDescending(kv => kv.Value).First().Key;
                    foreach (int v in list) { owner[v] = to; stiff[v] = false; }
                    changed++; merged += list.Count;
                }
                fragments += changed;
                if (changed == 0) break;
            }
            log?.Invoke($"  rigid chunks: {shellOf.Count} of {panelVotes.Count} panels are shells ({shellV} vertices), {pieceN} separate pieces whole; " +
                $"{stiff.Count(s => s)} of {nv} vertices stiff (never bend); {fragments} stray fragments ({merged} vertices) joined the region around them");
            var chunks = new TrophyChunks { Owner = owner, Stiff = stiff };
            if (useTemplate) foreach (var (k, sw) in template!.Seams) chunks.Seams[k] = sw;
            return chunks;
        }

        // The head's PIVOT where the game's is: the head joint moved to the same relative place inside the trophy's head
        // piece (its chunk) as the game's head joint sits inside the game's head -- measured in the head bone's own frame,
        // so a head sculpted tilted is measured along its own axes. A pivot near the face (Gravity/Normal/Zero Suit sat at
        // 0.73-0.83 of the helmet's depth, the game's at 0.58) swings the helmet's back through a wide arc at every turn
        // and drags its rim across the collar. Its aim marker (".center") moves with it, so the head faces as before.
        // Only when the game's pivot lies inside its own head (Samus's: 0.46 up, 0.58 forward; a head hung below its
        // joint, like Kanden's, isn't a pivot to copy). Changes the landmarks for the next fit only.
        public static bool HeadPivotLikeGame(TrophyRig rig, TrophyChunks chunks, DsSkeleton sk, LandmarkSet lm, Action<string>? log = null)
        {
            int head = sk.IndexOf("Head_1");
            if (head < 0 || lm.MirrorX || !lm.Joints.ContainsKey("Head_1") || chunks.Owner.Length != rig.Vertices.Length) return false;
            // a head whose facing is marked ("Head_1.fwd": Retro's own helmet and pivot) keeps its own pivot -- moving the joint
            // but not the facing marks tilted Phazon's helmet 21 deg up
            if (lm.Joints.ContainsKey("Head_1.fwd")) { log?.Invoke("  head pivot: the head's own (its facing is marked) -- left as marked"); return false; }
            var inv = rig.InvBind[head];
            var game = rig.FittedDs.Where((_, i) => rig.FittedDsNode[i] == head).Select(p => Vector3.TransformPosition(p, inv)).ToList();
            var tro = Enumerable.Range(0, rig.Vertices.Length).Where(v => chunks.Owner[v] == head)
                .Select(v => Vector3.TransformPosition(Vector3.TransformPosition(rig.Vertices[v], rig.TrophyToRig), inv)).ToList();
            if (game.Count < 12 || tro.Count < 30) return false;
            var target = new float[3]; var ratios = new float[3];
            for (int k = 0; k < 3; k++)
            {
                float gmin = game.Min(p => p[k]), gmax = game.Max(p => p[k]), tmin = tro.Min(p => p[k]), tmax = tro.Max(p => p[k]);
                if (gmax - gmin < 1e-6f) return false;
                ratios[k] = -gmin / (gmax - gmin);   // the joint is the bone frame's origin
                if (ratios[k] < -0.05f || ratios[k] > 1.05f) { log?.Invoke($"  head pivot: the game's head joint lies outside its head ({ratios[k]:0.00} on axis {k}) -- left as marked"); return false; }
                target[k] = tmin + ratios[k] * (tmax - tmin);
            }
            var pivot = Vector3.TransformPosition(new Vector3(target[0], target[1], target[2]), rig.BindWorld[head]);
            var newJoint = Vector3.TransformPosition(pivot, Matrix4.Invert(rig.TrophyToRig));
            var delta = newJoint - lm.Joints["Head_1"];
            lm.Joints["Head_1"] = newJoint;
            if (lm.Joints.TryGetValue("Head_1.center", out var c)) lm.Joints["Head_1.center"] = c + delta;
            float h = rig.Vertices.Max(p => p.Y) - rig.Vertices.Min(p => p.Y);
            log?.Invoke($"  head pivot: moved to where the game's sits in its head ({ratios[0]:0.00}/{ratios[1]:0.00}/{ratios[2]:0.00} of it on its own axes), {delta.Length / h * 100:0.0}% of height");
            return true;
        }

        // Turn the head's bind so the helmet, carried into the game's Idle f0 by its bone, turns by `turn` (row-vector form)
        // about the head joint -- only the head's own vertices move (as TrophyFacing turns a part's bind).
        public static void ApplyHeadTurn(Model model, TrophyRig rig, Matrix3 turn, Action<string>? log = null)
        {
            var sk = new DsSkeleton(model);
            int head = sk.IndexOf("Head_1");
            if (head < 0) return;
            var pose = new SkeletonPose(sk.Count); var idleW = new Matrix4[sk.Count];
            DsSkeleton.Sample(model, (int)PlayerAnimation.Idle, 0, pose);
            sk.Fk(pose, idleW, 0f, rig.Lengths, rig.Offsets);
            var j = idleW[head].ExtractTranslation();
            var t4 = Matrix4.CreateTranslation(-j) * new Matrix4(turn) * Matrix4.CreateTranslation(j);
            var inv = rig.InvBind[head] * idleW[head] * t4 * Matrix4.Invert(idleW[head]);
            rig.InvBind[head] = inv; rig.BindWorld[head] = Matrix4.Invert(inv);
            float ang = MathF.Acos(Math.Clamp((turn.M11 + turn.M22 + turn.M33 - 1f) / 2f, -1f, 1f)) * 180f / MathF.PI;
            log?.Invoke($"  head turned {ang:0} deg to face the way the hand-made model's straight-ahead helmet does");
        }

        public static string StraightPathFor(string daePath) => VariantPathFor(daePath, Variants[0]);
        // a copy of a rig from before a fix ("<dae>.before.mphrig", made on the PC), only so the viewer can flip between
        // the rig as it was and as it is now
        public static string BeforePathFor(string daePath) => Path.ChangeExtension(daePath, ".before.mphrig");
        public static ulong StraightKey(string daePath) => VariantKey(daePath, Variants[0]);

        // THE OWNER'S POSE (Pose Studio): "<dae>.posed.txt" holds, per bone, a turn of that bone's statue piece in the
        // bone's own frame (K; OpenTK row vectors), laid on top of a prepared rig (the "base" variant): skin = InvBind * K *
        // world. The page poses with exactly this, so what the owner sees there is what plays -- nothing is refitted.
        // A limb turned like a doll's (the piece and everything hanging off it) also moves the joints below it: those
        // bones get a new rest OFFSET (parent-local, model units; replaces the rig's), so they stay on their pieces in
        // every clip.
        public sealed class PosedSet
        {
            public string Base = "pieces";
            public readonly Dictionary<string, Matrix4> K = new(StringComparer.Ordinal);
            public readonly Dictionary<string, Vector3> Offsets = new(StringComparer.Ordinal);
        }
        public static string PosedPathFor(string daePath) => Path.ChangeExtension(daePath, ".posed.txt");
        public static PosedSet? LoadPosed(string path)
        {
            if (!File.Exists(path)) return null;
            var ci = System.Globalization.CultureInfo.InvariantCulture; var p = new PosedSet();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                var w = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (w[0] == "base" && w.Length >= 2) p.Base = w[1];
                else if (w[0] == "bone" && w.Length >= 18)
                {
                    var f = w.Skip(2).Take(16).Select(x => float.Parse(x, ci)).ToArray();
                    p.K[w[1]] = new Matrix4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                }
                else if (w[0] == "offset" && w.Length >= 5)
                    p.Offsets[w[1]] = new Vector3(float.Parse(w[2], ci), float.Parse(w[3], ci), float.Parse(w[4], ci));
                else throw new FormatException($"{Path.GetFileName(path)}: bad line '{raw}'");
            }
            return p;
        }
        public static void SavePosed(string path, PosedSet p, string comment)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { $"# {comment}", "# per bone: the turn of its statue piece in the bone's own frame (4x4, row vectors), on top of the base rig", $"base {p.Base}" };
            foreach (var (b, m) in p.K)
                lines.Add(string.Create(ci, $"bone {b} {m.M11} {m.M12} {m.M13} {m.M14} {m.M21} {m.M22} {m.M23} {m.M24} {m.M31} {m.M32} {m.M33} {m.M34} {m.M41} {m.M42} {m.M43} {m.M44}"));
            if (p.Offsets.Count > 0) lines.Add("# per bone: its joint's rest offset from the parent joint (parent-local, model units), moved with a limb turned like a doll's");
            foreach (var (b, o) in p.Offsets) lines.Add(string.Create(ci, $"offset {b} {o.X} {o.Y} {o.Z}"));
            File.WriteAllLines(path, lines);
        }
        // a copy of the base rig with the owner's turns laid on (shared vertex data; its own bind matrices, lengths, offsets)
        public static TrophyRig WithPose(TrophyRig rig, IReadOnlyList<string> names, PosedSet p)
        {
            var r = rig.Copy();
            r.InvBind = (Matrix4[])rig.InvBind.Clone(); r.BindWorld = (Matrix4[])rig.BindWorld.Clone(); r.Lengths = (float[])rig.Lengths.Clone();
            r.Offsets = new Vector3?[Math.Max(rig.Offsets.Length, p.Offsets.Count > 0 ? names.Count : 0)];
            Array.Copy(rig.Offsets, r.Offsets, rig.Offsets.Length);
            for (int i = 0; i < names.Count && i < r.InvBind.Length; i++)
            {
                if (p.K.TryGetValue(names[i], out var k)) { r.InvBind[i] = rig.InvBind[i] * k; r.BindWorld[i] = Matrix4.Invert(r.InvBind[i]); }
                if (p.Offsets.TryGetValue(names[i], out var o) && i < r.Offsets.Length) r.Offsets[i] = o;
            }
            return r;
        }
        // the base variant's cache (must be current) + the pose file; null when either is missing
        public static TrophyRig? LoadPosedRig(string daePath, IReadOnlyList<string> names)
        {
            var p = LoadPosed(PosedPathFor(daePath));
            if (p == null) return null;
            var v = Variants.FirstOrDefault(x => x.Key == p.Base);
            var b = v != null ? Load(VariantPathFor(daePath, v), VariantKey(daePath, v)) : p.Base == "default" ? Load(Path.ChangeExtension(daePath, ".mphrig"), CacheKey(daePath)) : null;
            return b == null ? null : WithPose(b, names, p);
        }

        // fit = false: load the cache only (null when there is none) -- fitting + straightening on the device
        // would add minutes to every launch of a trophy that has no such rig prepared.
        public static TrophyRig? LoadOrFitStraight(string daePath, Model hunterModel, IReadOnlyList<Vector3> corners, bool fit, Action<string>? log = null) =>
            LoadOrFitVariant(daePath, hunterModel, corners, Variants[0], fit, log);

        public static TrophyRig? LoadOrFitVariant(string daePath, Model hunterModel, IReadOnlyList<Vector3> corners, RigVariant v, bool fit, Action<string>? log = null)
        {
            string cache = VariantPathFor(daePath, v);
            ulong hash = VariantKey(daePath, v);
            var rig = Load(cache, hash);
            if (rig != null) { log?.Invoke($"rig: loaded cache {Path.GetFileName(cache)}"); return rig; }
            if (!fit) { log?.Invoke($"rig: no valid {Path.GetFileName(cache)} -- no '{v.Key}' rig for this trophy"); return null; }
            string? wp = v.Weights != null ? Path.ChangeExtension(daePath, v.Weights) : null;
            if (wp != null && !File.Exists(wp)) { log?.Invoke($"rig: '{v.Key}' needs {Path.GetFileName(wp)}"); return null; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var opt = FitOptions(daePath, hunterModel, log);
            // a rigid-built rig's layout (its own file, else the hand-made fighter layout) -- its joints replace the
            // marked ones for this rig before anything is fitted
            FighterTemplate? layout = null;
            if (v.Rigid)
            {
                string tp = LayoutPathFor(daePath, v);
                if (v.Layout != null && !File.Exists(tp)) { log?.Invoke($"rig: '{v.Key}' needs {Path.GetFileName(tp)}"); return null; }
                if (File.Exists(tp)) layout = LoadFighterTemplate(tp, new DsSkeleton(hunterModel).Names);
                if (layout != null && layout.Joints.Count > 0)
                {
                    opt.Landmarks ??= new LandmarkSet();
                    foreach (var (k, p) in layout.Joints) opt.Landmarks.Joints[k] = p;
                    log?.Invoke($"rig: '{v.Key}' joints from its layout: {string.Join(" ", layout.Joints.Keys)}");
                }
            }
            opt.PieceClaimFraction = v.Claim;
            opt.PanelDominance = v.PanelDominance;
            opt.PanelCreaseDeg = v.Crease;
            opt.BlendScale = v.Blend;
            var fitted = TrophyRigger.Fit(hunterModel, corners, opt);
            if (wp != null) LoadWeights(wp, fitted, new DsSkeleton(hunterModel), log);
            if (v.Rigid)
            {
                // the clean fit's ownership, as chunks, refitted through the chunk path (a hand-made chunk file wins)
                var sk2 = new DsSkeleton(hunterModel);
                var template = layout;
                opt.Chunks ??= RigidChunks(fitted, corners, sk2, log, template);
                // a layout that places the head joint itself (Retro's) keeps it there
                if (opt.Landmarks != null && !(template?.Joints.ContainsKey("Head_1") ?? false)) HeadPivotLikeGame(fitted, opt.Chunks, new DsSkeleton(hunterModel), opt.Landmarks, log);
                fitted = TrophyRigger.Fit(hunterModel, corners, opt);
                if (template?.HeadTurn is Matrix3 ht) ApplyHeadTurn(hunterModel, fitted, ht, log);
            }
            rig = TrophyUnpose.Straighten(hunterModel, fitted, corners, out _, log, v.TPose, rigidHandles: v.Rigid);
            log?.Invoke($"rig: '{v.Key}' fitted + straightened in {sw.Elapsed.TotalSeconds:0.0}s; saving {Path.GetFileName(cache)}");
            try { Save(cache, rig, hash); } catch (Exception ex) { log?.Invoke("rig: cache save failed: " + ex.Message); }
            return rig;
        }
    }
}
