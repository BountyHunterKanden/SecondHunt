using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Samus's HD suit in the campaign (RECOMP SETTINGS "samus suit"): the suit's body -- an HD model on Samus's DS rig,
// hd/<Id>/ -- posed every frame from MphRead's own biped animation layers, and its morph ball, hd/<Id>Ball (HdBall).
// MphRead still poses and simulates its own model; the campaign tells it (PlayerEntity.HostOwnMainBiped/Alt) to leave
// the main player's body and ball to this class. The arm cannon in first person is CampaignGun's.
// Lifted from RenderActivity's animhd path (BuildHdTemplate / MakeHdTemplate / LoadHdModel / HdPalette / RollBall /
// DrawGx), GX-TEV program only: every material through MphRecomp.Render.GxShader, skinned on the GPU, lit by the Brawl
// trophy viewer's light. A part with no GX program (no .brres / .gx.json entry) is left out, not drawn the old way.
// Each material on its own TEV program (GunPrograms.SuitFrag) where ArenaGxSpec can write it, else the interpreter.
// DepthRebuild: CampaignActivity's pass 4 (colour masked by the caller): the opaque parts' depth only.
internal enum SuitPass { Opaque, Translucent, Additive, DepthRebuild }

internal sealed class CampaignSuit
{
    const string Tag = "MPHCampaign";
    const int Floats = 19;     // per vertex: pos3 normal3 uv2 colour3 bones4 weights4 (GxShader's attributes 0..5)
    const int MaxBones = 32;   // GxShader's uBones[32]

    public readonly string Id;
    public bool HasBody { get; private set; }
    public bool HasBall { get; private set; }
    bool _asBuilt; // the menus' still: the body's corners as the model was built, not the rig's straightened BindPos

    // one draw: a range of the vertex buffer, its GX program and the PNG each texmap unit samples
    sealed class Batch
    {
        public int Start, Count, Pass;   // pass: 0 opaque, 1 blended, 2 additive (RenderActivity's PassOf)
        public int Spec = -1, Prog;      // its program in _specSrc (-1 = the interpreter), and the one it draws with
        public bool DepthFirst => Pass == 0 && P.AlphaTest[3] != 1; // in the depth-only passes (DrawDepth)
        public GxMaterial Gx = null!;
        public GxShader.Params P = null!;
        public readonly string?[] Png = new string?[8];
        public readonly bool[] Intensity = new bool[8];
        public readonly int[] Tex = new int[8];
    }

    readonly List<float> _build = new();
    float[] _verts = Array.Empty<float>();
    readonly List<Batch> _body = new(), _ball = new();
    // decoded in TryLoad (RGBA bytes, ready for GL), handed to GL by Warm / the first draw and dropped (after a context
    // loss a worker thread reads them again: _reprep)
    readonly Dictionary<string, (int[] Px, int W, int H)?> _pixels = new();
    float[]? _vertBuf;
    Task<(Dictionary<string, (int[] Px, int W, int H)?> Pixels, float[] Verts)>? _reprep;

    // the body: the DS skeleton MphRead's clips pose, and the rig the HD mesh is bound to
    Model _dsModel = null!;
    DsSkeleton _skel = null!;
    TrophyRig _rig = null!;
    SkeletonPose _pose = null!;
    Matrix4[] _world = Array.Empty<Matrix4>();
    bool[] _legs = Array.Empty<bool>(), _torso = Array.Empty<bool>();
    readonly float[] _pal = new float[MaxBones * 16];

    // the ball: its model's own centre and width, and the roll so far
    Vector3 _ballCenter;
    float _ballWidth = 1f, _heading, _spin;
    Vector3? _ballLast;
    readonly float[] _ballPal = new float[16];

    // _prog = the interpreter (GunPrograms.SuitInterpFrag), _depthProg = depth only, _specProg = the materials' own
    // programs (_specSrc, one Warm step each). Every one on GunPrograms.Vert: the depth pass and the colour pass must
    // put a pixel at exactly the same depth (the colour pass's <=). _cur = the program bound in this draw (Use).
    int _prog, _depthProg, _vao, _vbo, _white, _cur;
    readonly List<string> _specSrc = new();
    int[] _specProg = Array.Empty<int>();
    bool _interpOnly; // DrawStill (the menus' backdrop): everything on the interpreter, as it was built and judged
    bool _glFailed, _fadeOk, _glReady;
    // GL objects are made a step at a time (Warm): the program, the vertex buffer, one texture per step, then the
    // batches' texture tables -- the first draw used to make them all in one frame (250 ms, 2026-10-03 recording)
    int _step, _warmFrames;
    double _warmMs, _slowStepMs;
    string _slowStep = "";
    List<(string Png, bool Intensity)>? _texOrder;
    readonly Dictionary<(int, string), int> _u = new();
    readonly Dictionary<string, int> _texIds = new();
    readonly float[] _mvp16 = new float[16], _m16 = new float[16], _viewRot = new float[9];
    float[] _palNow = Array.Empty<float>();
    int _bonesNow;

    static readonly string[] TexUniform = { "uTex0", "uTex1", "uTex2", "uTex3", "uTex4", "uTex5", "uTex6", "uTex7" };
    static readonly float[] LightDir = Norm3(-50f, 50f, 90f);
    static readonly float[] LightCol = { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] LightSpecCol = { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] LightSpecK = { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 };
    static float[] Norm3(float x, float y, float z)
    {
        float l = MathF.Sqrt(x * x + y * y + z * z);
        return new[] { x / l, y / l, z / l, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    CampaignSuit(string id) { Id = id; }

    // each batch's own fragment program, the distinct ones once (CPU only: TryLoad's worker thread)
    void PlanPrograms()
    {
        foreach (List<Batch> list in new[] { _body, _ball })
        {
            foreach (Batch b in list)
            {
                string? src = GunPrograms.SuitFrag(b.P);
                if (src == null) continue;
                b.Spec = _specSrc.IndexOf(src);
                if (b.Spec < 0)
                {
                    b.Spec = _specSrc.Count;
                    _specSrc.Add(src);
                }
            }
        }
    }

    // MphRead's Samus model (the DS rig the suit's body is posed on); the GL thread's lookup for TryLoad on a worker
    public static Model SamusModel() => Read.GetModelInstance(Metadata.HunterModels[Hunter.Samus][0]).Model;

    // hd/<id>/ (the body, only when that model is on Samus's rig) and hd/<id>Ball; null when neither is there, or for
    // "original" / "none". CPU work only, safe on a worker thread (GL objects are made by Warm / the first draw); samus =
    // MphRead's Samus model, looked up by the caller on the GL thread (else read here). Never fits a rig: the body comes
    // from the model's cached rig files or not at all.
    public static CampaignSuit? TryLoad(string hdRoot, string id, Model? samus = null, bool asBuilt = false)
    {
        if (string.IsNullOrEmpty(id) || id.Equals("original", StringComparison.OrdinalIgnoreCase) || id.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var suit = new CampaignSuit(id) { _asBuilt = asBuilt };
            string body, ball;
            try { body = suit.LoadBody(hdRoot, samus); }
            catch (Exception ex)
            {
                Log.Error(Tag, $"suit {id}: body failed: {ex}");
                suit._body.Clear(); suit._build.Clear(); suit._pixels.Clear(); suit.HasBody = false;
                body = "failed (" + ex.Message + ")";
            }
            int mark = suit._build.Count;
            try { ball = suit.LoadBall(hdRoot); }
            catch (Exception ex)
            {
                Log.Error(Tag, $"suit {id}: morph ball failed: {ex}");
                suit._ball.Clear(); suit._build.RemoveRange(mark, suit._build.Count - mark); suit.HasBall = false;
                ball = "failed (" + ex.Message + ")";
            }
            if (!suit.HasBody && !suit.HasBall)
            {
                Log.Info(Tag, $"suit {id}: nothing to draw (body {body}; ball {ball}) -- MphRead's own Samus stays");
                return null;
            }
            suit.PlanPrograms();
            var tex = System.Diagnostics.Stopwatch.StartNew();
            suit.DecodeAll();
            long texMs = tex.ElapsedMilliseconds;
            suit._verts = suit._build.ToArray();
            suit._build.Clear();
            suit._build.TrimExcess();
            suit._vertBuf = suit._verts;
            Log.Info(Tag, $"suit {id}: body {body}; ball {ball}; {suit._verts.Length / Floats / 3} tris, "
                + $"{suit._pixels.Count} textures (read in {texMs} ms), loaded in {sw.ElapsedMilliseconds} ms");
            return suit;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"suit {id}: load failed: {ex}");
            return null;
        }
    }

    string LoadBody(string hdRoot, Model? samus)
    {
        if (!TrophyRigs.TryHunterFor(Id, out Hunter h) || h != Hunter.Samus) return "none (not a model on Samus's rig)";
        string dir = Path.Combine(hdRoot, Id);
        string[] daes = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dae") : Array.Empty<string>();
        if (daes.Length == 0) return $"none (no .dae in hd/{Id})";
        string daePath = daes[0];
        Model rm = samus ?? SamusModel();
        if (MathF.Abs(rm.Scale.X - 1f) > 1e-6f) Log.Warn(Tag, $"suit {Id}: {rm.Name} model scale {rm.Scale.X} != 1 (rigs assume 1)");
        var sk = new DsSkeleton(rm);
        TrophyRig? rig = PickRig(daePath, rm, sk, out string label);
        if (rig == null) return $"none (no rig cached for {Path.GetFileName(daePath)}: prepare one on the PC, it is never fitted here)";
        if (sk.Count > MaxBones || rig.InvBind.Length < sk.Count || rig.Lengths.Length < sk.Count || rig.Offsets.Length < sk.Count)
        {
            return $"none (the {label} rig has {rig.InvBind.Length} bones, Samus's skeleton {sk.Count})";
        }
        // feet on the floor where the game model's are (the hip height the sculpt gave can sink them in or lift them)
        try { TrophyFeet.Settle(rig, rm, sk, m => Log.Info(Tag, $"suit {Id} [{label}]:" + m)); }
        catch (Exception ex) { Log.Warn(Tag, $"suit {Id}: feet settle failed: {ex.Message}"); }

        DaeModel dae = DaeModel.Load(daePath);
        Dictionary<string, GxMaterial> gx = ReadGx(dir, daePath, out Dictionary<string, int> formats);
        Matrix4 g = rig.TrophyToRig;
        int tris = 0, hidden = 0, missing = 0;
        foreach (DaeMesh part in dae.Meshes)
        {
            if (!gx.TryGetValue(part.Material, out GxMaterial? m))
            {
                Log.Info(Tag, $"suit {Id}: '{part.Material}' has no GX program -- its {part.Positions.Count / 3} triangles are not drawn");
                continue;
            }
            int start = _build.Count / Floats, kept = 0;
            for (int t = 0; t + 3 <= part.Positions.Count; t += 3)
            {
                // triangles on a replaced / hidden / cut part (the rig's landmarks) are not drawn
                int v0 = rig.IndexOf(part.Positions[t]), v1 = rig.IndexOf(part.Positions[t + 1]), v2 = rig.IndexOf(part.Positions[t + 2]);
                if (v0 >= 0 && v1 >= 0 && v2 >= 0 && rig.HidesTriangle(v0, v1, v2))
                {
                    hidden++;
                    continue;
                }
                for (int i = t; i < t + 3; i++)
                {
                    int vi = i == t ? v0 : i == t + 1 ? v1 : v2;
                    System.Numerics.Vector3 q = part.Positions[i];
                    Vector3 rp = Vector3.TransformPosition(new Vector3(q.X, q.Y, q.Z), g);
                    Vector3 rn = Vector3.TransformNormal(Normal(part, i), g);
                    if (rig.BindPos != null && vi >= 0 && !_asBuilt)
                    {
                        // a straightened rig: bound where the straightening put this vertex, its normal turned with it
                        rp = rig.BindPos[vi];
                        rn = Vector3.Transform(rn, rig.BindRot![vi]);
                    }
                    if (rn.LengthSquared > 1e-12f) rn.Normalize();
                    Corner(rp, rn, part, i);
                    if (vi < 0)
                    {
                        missing++; // not in the rig (it is built from these same corners): pinned to the root
                        Rigid(0);
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++) _build.Add(rig.Bones[vi * 4 + k]);
                        for (int k = 0; k < 4; k++) _build.Add(rig.Weights[vi * 4 + k]);
                    }
                    kept++;
                }
            }
            _body.Add(MakeBatch(start, kept, m, dir, formats));
            tris += kept / 3;
        }
        if (rig.Attachments.Length > 0)
        {
            // the DS model's own parts for replaced bones (RenderActivity draws them on its old shader): left out here
            int partTris = 0;
            foreach (var (_, corners) in TrophyRigger.ReplacementParts(rm, rig)) partTris += corners.Count / 3;
            Log.Info(Tag, $"suit {Id}: the rig replaces parts with the DS model's own -- those {partTris} triangles have no GX program, not drawn");
        }
        if (_body.Count == 0) return $"none (no part of {Path.GetFileName(daePath)} has a GX program: no .brres or .gx.json?)";
        _dsModel = rm;
        _skel = sk;
        _rig = rig;
        _pose = new SkeletonPose(sk.Count);
        _world = new Matrix4[sk.Count];
        _legs = new bool[sk.Count];
        _torso = new bool[sk.Count];
        for (int i = 0; i < sk.Count; i++)
        {
            _torso[i] = sk.AboveSpine[i];
            _legs[i] = !sk.AboveSpine[i];
        }
        HasBody = true;
        return $"{label} rig ({_body.Count} batches, {tris} tris, {hidden} hidden, {missing} unmapped corners)";
    }

    // The rig, from cache only, in RenderActivity's order: the owner's pose (Pose Studio, on its base rig) > PIECES >
    // RETRO > RIGID > the sculpt-bound <dae>.mphrig. LoadOrFit would fit when that last cache is stale, so it is read
    // with Load + its cache key instead.
    TrophyRig? PickRig(string dae, Model rm, DsSkeleton sk, out string label)
    {
        Action<string> log = m => Log.Info(Tag, $"suit {Id}: " + m);
        try
        {
            string posedPath = TrophyRigIO.PosedPathFor(dae);
            if (File.Exists(posedPath))
            {
                string? baseKey = TrophyRigIO.LoadPosed(posedPath)?.Base;
                TrophyRig? posed = TrophyRigIO.LoadPosedRig(dae, sk.Names);
                if (posed != null)
                {
                    label = $"posed (on {baseKey})";
                    return posed;
                }
                log($"rig: the pose file's base rig '{baseKey}' has no valid cache");
            }
        }
        catch (Exception ex) { log("rig: posed failed: " + ex.Message); }
        foreach (string key in new[] { "pieces", "retro", "rigid" })
        {
            TrophyRigIO.RigVariant? v = Array.Find(TrophyRigIO.Variants, x => x.Key == key);
            if (v == null || !File.Exists(TrophyRigIO.VariantPathFor(dae, v))) continue;
            try
            {
                TrophyRig? r = TrophyRigIO.LoadOrFitVariant(dae, rm, Array.Empty<Vector3>(), v, fit: false, log);
                if (r != null)
                {
                    label = key;
                    return r;
                }
            }
            catch (Exception ex) { log($"rig: '{key}' failed: {ex.Message}"); }
        }
        string sculpt = Path.ChangeExtension(dae, ".mphrig");
        if (File.Exists(sculpt))
        {
            try
            {
                TrophyRig? r = TrophyRigIO.Load(sculpt, TrophyRigIO.CacheKey(dae));
                if (r != null)
                {
                    label = "sculpted";
                    return r;
                }
                log($"rig: {Path.GetFileName(sculpt)} is stale (another .dae, landmarks or rigger version)");
            }
            catch (Exception ex) { log("rig: sculpted failed: " + ex.Message); }
        }
        label = "none";
        return null;
    }

    // The suit's own morph ball (HdBall): every corner on bone 0 -- the ball's whole placement is that one matrix -- plus
    // the lit core between the halves
    string LoadBall(string hdRoot)
    {
        HdBall? info = HdBall.For(hdRoot, Id);
        if (info == null) return $"none (no hd/{HdBall.IdFor(Id)})";
        DaeModel dae = DaeModel.Load(info.DaePath);
        Dictionary<string, GxMaterial> gx = ReadGx(info.Folder, info.DaePath, out Dictionary<string, int> formats);
        System.Numerics.Vector3 mn = dae.Min, mx = dae.Max;
        _ballCenter = new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
        _ballWidth = MathF.Max(mx.X - mn.X, MathF.Max(mx.Y - mn.Y, mx.Z - mn.Z));
        if (!(_ballWidth > 1e-6f)) return $"none ({info.Id} is empty)";
        int tris = 0;
        foreach (DaeMesh part in dae.Meshes)
        {
            if (!gx.TryGetValue(part.Material, out GxMaterial? m))
            {
                Log.Info(Tag, $"suit {Id}: ball '{part.Material}' has no GX program -- its {part.Positions.Count / 3} triangles are not drawn");
                continue;
            }
            int start = _build.Count / Floats;
            for (int i = 0; i < part.Positions.Count; i++)
            {
                System.Numerics.Vector3 q = part.Positions[i];
                Corner(new Vector3(q.X, q.Y, q.Z), Normal(part, i), part, i);
                Rigid(0);
            }
            _ball.Add(MakeBatch(start, part.Positions.Count, m, info.Folder, formats));
            tris += part.Positions.Count / 3;
        }
        if (_ball.Count == 0) return $"none (no part of {info.Id} has a GX program)";
        string core = "no core";
        if (info.Core is System.Numerics.Vector3 c)
        {
            int start = _build.Count / Floats, n = 0;
            var centre = new System.Numerics.Vector3(_ballCenter.X, _ballCenter.Y, _ballCenter.Z);
            foreach (var (p, d) in HdBall.CoreCorners(centre, _ballWidth))
            {
                _build.Add(p.X); _build.Add(p.Y); _build.Add(p.Z); _build.Add(d.X); _build.Add(d.Y); _build.Add(d.Z);
                _build.Add(0); _build.Add(0); _build.Add(1); _build.Add(1); _build.Add(GxShader.PackBlueAlpha(1, 1));
                Rigid(0);
                n++;
            }
            _ball.Add(MakeBatch(start, n, HdBall.CoreMaterial(c), info.Folder, formats));
            core = "lit core";
        }
        HasBall = true;
        return $"{info.Id} ({_ball.Count} batches, {tris} tris, {core})";
    }

    // the model's GX programs by material name: its .brres, else <dae>.gx.json (a Retro suit from the owner's disc)
    static Dictionary<string, GxMaterial> ReadGx(string dir, string daePath, out Dictionary<string, int> formats)
    {
        var mats = new Dictionary<string, GxMaterial>();
        formats = new Dictionary<string, int>();
        string[] brs = Directory.GetFiles(dir, "*.brres");
        if (brs.Length > 0)
        {
            foreach (GxMaterial m in Mdl0Gx.Read(brs[0])) mats.TryAdd(m.Name, m);
            formats = Mdl0Gx.TextureFormats(brs[0]);
        }
        else if (File.Exists(GxJson.PathFor(daePath)))
        {
            foreach (GxMaterial m in GxJson.Read(GxJson.PathFor(daePath))) mats.TryAdd(m.Name, m);
        }
        return mats;
    }

    static Vector3 Normal(DaeMesh part, int i)
    {
        if (i >= part.Normals.Count) return Vector3.UnitY;
        System.Numerics.Vector3 n = part.Normals[i];
        return new Vector3(n.X, n.Y, n.Z);
    }

    // position, normal, then what the GX program reads: the mesh's own UVs and vertex colour (alpha packed with blue)
    void Corner(Vector3 p, Vector3 n, DaeMesh part, int i)
    {
        _build.Add(p.X); _build.Add(p.Y); _build.Add(p.Z); _build.Add(n.X); _build.Add(n.Y); _build.Add(n.Z);
        System.Numerics.Vector2 uv = i < part.Uvs.Count ? part.Uvs[i] : default;
        System.Numerics.Vector4 c = i < part.Colors.Count ? part.Colors[i] : part.FlatColor;
        _build.Add(uv.X); _build.Add(uv.Y); _build.Add(c.X); _build.Add(c.Y); _build.Add(GxShader.PackBlueAlpha(c.Z, c.W));
    }

    void Rigid(int bone)
    {
        _build.Add(bone); _build.Add(0); _build.Add(0); _build.Add(0);
        _build.Add(1); _build.Add(0); _build.Add(0); _build.Add(0);
    }

    Batch MakeBatch(int start, int count, GxMaterial m, string dir, Dictionary<string, int> formats)
    {
        var b = new Batch { Start = start, Count = count, Gx = m, P = GxShader.Pack(m), Pass = m.Additive ? 2 : m.BlendEnable ? 1 : 0 };
        for (int u = 0; u < 8; u++)
        {
            string? name = b.P.Units[u]?.Name;
            if (string.IsNullOrEmpty(name)) continue;
            string png = Path.Combine(dir, name + ".png");
            // GX I4/I8 textures are their intensity in all four channels; the PNG kept the grey but not the alpha
            bool intensity = formats.TryGetValue(name, out int f) && Mdl0Gx.IsIntensity(f);
            b.Png[u] = png;
            b.Intensity[u] = intensity;
        }
        return b;
    }

    static string Key(string png, bool intensity) => intensity ? png + "#i" : png;

    // a PNG -> ABGR ints (GL's RGBA bytes), as GL takes them
    static (int[] Px, int W, int H)? Decode(string png, bool intensity)
    {
        if (!File.Exists(png))
        {
            Log.Warn(Tag, $"suit: missing texture {png}");
            return null;
        }
        try
        {
            if (AppPlatform.DecodeImage(png) is not (int[] px, int w, int h)) return null;
            if (intensity)
            {
                for (int i = 0; i < px.Length; i++) px[i] = (px[i] & 0x00FFFFFF) | ((px[i] & 0xFF) << 24);
            }
            return (px, w, h);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"suit: texture {Path.GetFileName(png)} failed: {ex.Message}");
            return null;
        }
    }

    // once per 60 Hz sim step, after the game's step: the ball's roll (heading and spin from the player's movement)
    public void Tick(PlayerEntity player)
    {
        if (!HasBall) return;
        if (!player.IsAltForm)
        {
            _ballLast = null; // morphing again starts from where the ball is then, not where it last rolled
            return;
        }
        Vector3 now = player.Position;
        if (_ballLast is Vector3 last)
        {
            float dx = now.X - last.X, dz = now.Z - last.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
            // (a jump of several units in one step is a teleport, not a roll)
            if (dist > 1e-4f && dist < 5f)
            {
                _heading = LerpAngle(_heading, MathF.Atan2(dx, -dz), 0.35f);   // facing = (sin, 0, -cos)
                _spin = (_spin + dist / BallRadius(player)) % (2f * MathF.PI);
            }
        }
        else
        {
            _heading = MathF.Atan2(player.Field70, -player.Field74);
        }
        _ballLast = now;
    }

    static float LerpAngle(float a, float b, float t)
    {
        float d = b - a;
        while (d > MathF.PI) d -= 2f * MathF.PI;
        while (d < -MathF.PI) d += 2f * MathF.PI;
        return a + d * t;
    }

    static float BallRadius(PlayerEntity player)
    {
        float r = Fixed.ToFloat(player.Values.AltColRadius);
        return r > 0 ? r : HdBall.GameDiameter / 2f;
    }

    // the body, posed from the player's biped layers (legs + torso clips, aim pitch) where MphRead places its own;
    // alpha = MphRead's own for the main biped (unmorph fade, cloak). While it fades (alpha < 1) the opaque parts move to
    // the Translucent pass: first into depth only, then blended at alpha over just their own front surfaces, so the
    // fading body never shows its insides; the blended and additive parts keep their passes, their alpha times the fade.
    // Opaque (pass 1) with the depth program: the opaque parts' depth first (colour masked here), then their colour at
    // <=, so a body filling the screen many layers deep (a cutscene camera passing through her, owner queue #37) shades
    // each pixel once. DepthRebuild (pass 4): their depth only; alpha-tested parts through their colour programs (their
    // holes come from the TEV alpha; the caller masks colour).
    public void DrawBody(PlayerEntity player, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha)
    {
        if (!HasBody || alpha <= 0f || !EnsureGl()) return;
        try
        {
            bool rebuild = pass == SuitPass.DepthRebuild;
            int p = rebuild ? 0 : (int)pass;
            bool fade = alpha < 0.999f && _fadeOk;
            bool own = !(fade && p == 0);
            bool moved = fade && p == 1;
            if (!(own && Has(_body, p)) && !(moved && Has(_body, 0))) return;
            Pose(player);
            Matrix4 model = HunterRig.ModelMatrix(_dsModel,
                HunterRig.Placement(Hunter.Samus, player.Position, new Vector3(player.Field70, 0, player.Field74)));
            Begin(view, proj, model, _pal, _skel.Count);
            double s = Environment.TickCount64 / 1000.0;
            if (moved) DrawFaded(alpha, s);
            if (own && p == 0 && _depthProg != 0)
            {
                if (!rebuild) GLES30.GlColorMask(false, false, false, false);
                DrawDepth(_body);
                if (!rebuild) GLES30.GlColorMask(true, true, true, true);
                DrawPass(_body, 0, 1f, s, rebuild ? DepthDone.Skip : DepthDone.Lequal);
            }
            else if (own) DrawPass(_body, p, fade ? alpha : 1f, s);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"suit {Id}: body draw failed -- MphRead draws its own body from now on: {ex}");
            HasBody = false;
        }
        finally
        {
            End();
        }
    }

    // The body in one still pose with no player (the menus' HD Samus backdrop, MenuSamusRender -- UI's, reached in
    // 2026-10-03): frame `frame` of Samus's DS clip `clip` on every node, level, in the rig's own space (model = identity:
    // Samus faces -Z, feet on y = 0), every pass. The caller binds its framebuffer and enables the depth test.
    // clip < 0: no pose (identity skin: the corners as loaded -- TryLoad asBuilt = the model as built, straight on).
    // hide: materials not drawn (the face behind the visor); opaque: blended materials drawn opaque (the visor pane).
    public bool DrawStill(int clip, int frame, Matrix4 view, Matrix4 proj, ICollection<string>? hide = null, ICollection<string>? opaque = null)
    {
        if (!HasBody || !EnsureGl()) return false;
        try
        {
            _interpOnly = true;
            if (clip < 0)
            {
                for (int i = 0; i < _skel.Count; i++) HunterRig.ToArray(Matrix4.Identity, _pal, i * 16);
            }
            else
            {
                if (clip < _dsModel.AnimationGroups.Node.Count) DsSkeleton.Sample(_dsModel, clip, frame, _pose);
                _skel.Fk(_pose, _world, 0f, _rig.Lengths, _rig.Offsets);
                for (int i = 0; i < _skel.Count; i++) HunterRig.ToArray(_rig.InvBind[i] * _world[i], _pal, i * 16);
            }
            Begin(view, proj, Matrix4.Identity, _pal, _skel.Count);
            if (hide == null && opaque == null)
            {
                for (int p = 0; p < 3; p++) if (Has(_body, p)) DrawPass(_body, p, 1f, 0);
                return true;
            }
            for (int p = 0; p < 3; p++)
            {
                for (int k = 0; k < (p == 2 ? 2 : 1); k++) // DrawPass's doubled additive sheets
                {
                    foreach (Batch b in _body)
                    {
                        if (hide != null && hide.Contains(b.Gx.Name)) continue;
                        bool solid = opaque != null && opaque.Contains(b.Gx.Name);
                        if ((solid ? 0 : b.Pass) != p || (solid && k > 0)) continue;
                        if (!solid) { Emit(b, 1f, 0); continue; }
                        Material(b, 1f, 0);
                        GLES30.GlUniform1i(U(_prog, "uBlendAlpha"), 0);
                        GLES30.GlDisable(GLES30.GlBlend);
                        GLES30.GlDepthMask(true);
                        GLES30.GlDepthFunc(DepthFunc(b.P.DepthFunc));
                        GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"suit {Id}: still draw failed: {ex}");
            return false;
        }
        finally
        {
            _interpOnly = false;
            End();
        }
    }

    // MphRead's own biped pose (PlayerDraw): the legs layer up to Spine_1, the torso layer above it, the spine tilted by
    // the aim -- on the rig's bone lengths / rest offsets; skin matrix = inverse bind * pose (RenderActivity's HdPalette)
    void Pose(PlayerEntity player)
    {
        var legs = player.HostBipedModel1.AnimInfo;
        var torso = player.BipedModel2.AnimInfo;
        Sample(legs.Index[0], legs.Frame[0], _legs);
        Sample(torso.Index[0], torso.Frame[0], _torso);
        _skel.Fk(_pose, _world, player.FacingVector.Y, _rig.Lengths, _rig.Offsets);
        for (int i = 0; i < _skel.Count; i++) HunterRig.ToArray(_rig.InvBind[i] * _world[i], _pal, i * 16);
    }

    void Sample(int clip, int frame, bool[] nodes)
    {
        if (clip >= 0 && clip < _dsModel.AnimationGroups.Node.Count) DsSkeleton.Sample(_dsModel, clip, frame, _pose, nodes);
    }

    // the morph ball where MphRead's own ball is: its centre at the player's position + AltColYPos, as wide as MPH's
    // (2 x AltColRadius). HdBall.Placement rests the ball on y = 0, so it is dropped by its radius first.
    // (small on screen: no depth prepass; pass 4 is its depth only, as the body's)
    public void DrawBall(PlayerEntity player, Matrix4 view, Matrix4 proj, SuitPass pass)
    {
        bool rebuild = pass == SuitPass.DepthRebuild;
        int p = rebuild ? 0 : (int)pass;
        if (!HasBall || !Has(_ball, p) || !EnsureGl()) return;
        try
        {
            HunterRig.ToArray(HdBall.Placement(_ballCenter, _ballWidth, _spin, _heading), _ballPal);
            float half = HdBall.GameDiameter / 2f;
            Vector3 centre = player.Position;
            centre.Y += Fixed.ToFloat(player.Values.AltColYPos);
            Matrix4 model = Matrix4.CreateTranslation(0, -half, 0) * Matrix4.CreateScale(BallRadius(player) / half)
                * Matrix4.CreateTranslation(centre);
            Begin(view, proj, model, _ballPal, 1);
            double s = Environment.TickCount64 / 1000.0;
            if (rebuild && _depthProg != 0)
            {
                DrawDepth(_ball);
                DrawPass(_ball, 0, 1f, s, DepthDone.Skip);
            }
            else DrawPass(_ball, p, 1f, s);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"suit {Id}: ball draw failed -- MphRead draws its own ball from now on: {ex}");
            HasBall = false;
        }
        finally
        {
            End();
        }
    }

    static bool Has(List<Batch> batches, int pass)
    {
        foreach (Batch b in batches) if (b.Pass == pass) return true;
        return false;
    }

    // what DrawDepth already did for the DepthFirst batches: nothing (None), their depth (Lequal: their colour at <=),
    // or all of their part (Skip: pass 4)
    enum DepthDone { None, Lequal, Skip }

    void DrawPass(List<Batch> batches, int pass, float fade, double seconds, DepthDone depth = DepthDone.None)
    {
        // the additive parts twice: the DAE export collapsed the trophies' two coincident sheets (front + back) into one,
        // and the glow is the stack of both (RenderActivity's finding, 2026-08-12)
        int times = pass == 2 ? 2 : 1;
        for (int k = 0; k < times; k++)
        {
            foreach (Batch b in batches)
            {
                if (b.Pass != pass) continue;
                if (depth == DepthDone.Skip && b.DepthFirst) continue;
                Emit(b, fade, seconds, depth == DepthDone.Lequal && b.DepthFirst);
            }
        }
    }

    // the DepthFirst batches' depth alone (GunPrograms.DepthFrag: no fragment work). It writes no colour, so the caller
    // masks colour (an unwritten output is undefined)
    void DrawDepth(List<Batch> batches)
    {
        Use(_depthProg);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlDepthMask(true);
        foreach (Batch b in batches)
        {
            if (!b.DepthFirst) continue;
            GLES30.GlDepthFunc(DepthFunc(b.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
    }

    // the opaque parts at alpha: depth only (ZERO, ONE; the depth program where it can), then the colour where that
    // depth is
    void DrawFaded(float alpha, double seconds)
    {
        if (_depthProg != 0)
        {
            GLES30.GlColorMask(false, false, false, false);
            DrawDepth(_body);
            GLES30.GlColorMask(true, true, true, true);
        }
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlZero, GLES30.GlOne);
        GLES30.GlDepthMask(true);
        foreach (Batch b in _body)
        {
            if (b.Pass != 0 || _depthProg != 0 && b.DepthFirst) continue;
            Material(b, 1f, seconds);
            GLES30.GlDepthFunc(DepthFunc(b.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthMask(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        foreach (Batch b in _body)
        {
            if (b.Pass != 0) continue;
            Material(b, alpha, seconds);
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
    }

    // one batch with the material's own blend mode and depth test (RenderActivity's DrawGx); lequal: its depth is
    // already in (DrawDepth), so its < becomes <=
    void Emit(Batch b, float fade, double seconds, bool lequal = false)
    {
        Material(b, fade, seconds);
        GxShader.Params P = b.P;
        if (P.Blend)
        {
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(SrcFactor(P.BlendSrc), DstFactor(P.BlendDst));
            GLES30.GlDepthMask(false);
        }
        else
        {
            GLES30.GlDisable(GLES30.GlBlend);
            GLES30.GlDepthMask(true);
        }
        int func = DepthFunc(P.DepthFunc);
        GLES30.GlDepthFunc(lequal && func == GLES30.GlLess ? GLES30.GlLequal : func);
        GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
    }

    int U(int prog, string n) => _u.TryGetValue((prog, n), out int l) ? l : _u[(prog, n)] = GLES30.GlGetUniformLocation(prog, n);

    // everything but the material, for this draw: the camera, the skin, the model; each program gets them (and the
    // trophy light, the texture units) the first time this draw uses it (Use)
    void Begin(Matrix4 view, Matrix4 proj, Matrix4 model, float[] palette, int bones)
    {
        HunterRig.ToArray(view * proj, _mvp16); // OpenTK's layout uploaded untransposed (row vectors), as CampaignGun
        _viewRot[0] = view.M11; _viewRot[1] = view.M12; _viewRot[2] = view.M13;
        _viewRot[3] = view.M21; _viewRot[4] = view.M22; _viewRot[5] = view.M23;
        _viewRot[6] = view.M31; _viewRot[7] = view.M32; _viewRot[8] = view.M33;
        HunterRig.ToArray(model, _m16);
        _palNow = palette;
        _bonesNow = bones;
        _cur = 0;
        for (int t = 0; t < 8; t++)
        {
            GLES30.GlBindSampler(t, 0); // MPH's sampler objects would override the layers' wrap modes
        }
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE: RenderActivity draws the trophies unculled
        GLES30.GlBindVertexArray(_vao);
    }

    void Use(int prog)
    {
        if (prog == _cur) return;
        _cur = prog;
        GLES30.GlUseProgram(prog);
        GlUniforms.UniformMatrix4fv(U(prog, "uMvp"), 1, false, _mvp16, 0);
        GlUniforms.UniformMatrix3fv(U(prog, "uViewRot"), 1, false, _viewRot, 0);
        GLES30.GlUniform1i(U(prog, "uSkin"), 1);
        GlUniforms.UniformMatrix4fv(U(prog, "uModel"), 1, false, _m16, 0);
        GlUniforms.UniformMatrix4fv(U(prog, "uBones"), _bonesNow, false, _palNow, 0);
        if (prog == _depthProg) return;
        GLES30.GlUniform3f(U(prog, "uSceneAmb"), 102f / 255f, 100f / 255f, 100f / 255f);
        GLES30.GlUniform1i(U(prog, "uNumLights"), 1);
        GlUniforms.Uniform3fv(U(prog, "uLightDir"), 4, LightDir, 0);
        GlUniforms.Uniform3fv(U(prog, "uLightCol"), 4, LightCol, 0);
        GlUniforms.Uniform3fv(U(prog, "uLightSpecCol"), 4, LightSpecCol, 0);
        GlUniforms.Uniform3fv(U(prog, "uLightSpecK"), 4, LightSpecK, 0);
        GLES30.GlUniform1f(U(prog, "uExposure"), 1f);
        GLES30.GlUniform1i(U(prog, "uRawEnvNormal"), 0);
        GLES30.GlUniform4f(U(prog, "uTint"), 0, 0, 0, 0);
        for (int t = 0; t < 8; t++)
        {
            GLES30.GlUniform1i(U(prog, TexUniform[t]), t);
        }
    }

    void End()
    {
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindVertexArray(0);
    }

    void Material(Batch b, float fade, double seconds)
    {
        GxShader.Params P = b.P;
        int prog = _interpOnly ? _prog : b.Prog;
        Use(prog);
        if (prog == _prog)
        {
            // the interpreter's TEV, alpha test and normal map uniforms (a material's own program has them written in)
            GLES30.GlUniform1i(U(prog, "uNumStages"), P.NumStages);
            GlUniforms.Uniform4iv(U(prog, "uCabcd"), 8, P.Cabcd, 0); GlUniforms.Uniform4iv(U(prog, "uCmod"), 8, P.Cmod, 0);
            GlUniforms.Uniform4iv(U(prog, "uAabcd"), 8, P.Aabcd, 0); GlUniforms.Uniform4iv(U(prog, "uAmod"), 8, P.Amod, 0);
            GlUniforms.Uniform4iv(U(prog, "uDst"), 8, P.Dst, 0); GlUniforms.Uniform4iv(U(prog, "uKsel"), 8, P.Ksel, 0);
            GlUniforms.Uniform1iv(U(prog, "uRasChan"), 8, P.RasChan, 0); GlUniforms.Uniform4iv(U(prog, "uSwap"), 4, P.Swap, 0);
            GlUniforms.Uniform4iv(U(prog, "uAlphaTest"), 1, P.AlphaTest, 0); GlUniforms.Uniform2fv(U(prog, "uAlphaRef"), 1, P.AlphaRef, 0);
            GLES30.GlUniform1i(U(prog, "uBlendAlpha"), P.Blend ? 1 : 0);
            GLES30.GlUniform1i(U(prog, "uNmap"), P.NmapUnit + 1); GLES30.GlUniform1i(U(prog, "uNmapTc"), P.NmapCoord);
        }
        GlUniforms.Uniform4fv(U(prog, "uReg"), 4, P.Reg, 0); GlUniforms.Uniform4fv(U(prog, "uKonst"), 4, P.Konst, 0);
        GlUniforms.Uniform4iv(U(prog, "uColCtrl"), 2, P.ColCtrl, 0); GlUniforms.Uniform4iv(U(prog, "uAlpCtrl"), 2, P.AlpCtrl, 0);
        GlUniforms.Uniform2iv(U(prog, "uAttn"), 2, P.Attn, 0);
        GlUniforms.Uniform4fv(U(prog, "uChanMat"), 2, P.ChanMat, 0); GlUniforms.Uniform4fv(U(prog, "uChanAmb"), 2, P.ChanAmb, 0);
        GlUniforms.Uniform1iv(U(prog, "uTgMode"), 8, P.TgMode, 0);
        GlUniforms.UniformMatrix3fv(U(prog, "uTexMtx"), 8, false, P.Scrolls ? GxShader.ScrolledTexMtx(b.Gx, P, seconds) : P.TexMtx, 0);
        GLES30.GlUniform1f(U(prog, "uFade"), fade);
        // unit u holds the layer bound to texmap u, with that layer's wrap
        for (int u = 0; u < 8; u++)
        {
            GLES30.GlActiveTexture(GLES30.GlTexture0 + u);
            GLES30.GlBindTexture(GLES30.GlTexture2d, b.Tex[u]);
            GxLayer? L = P.Units[u];
            if (L != null)
            {
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, Wrap(L.WrapS));
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, Wrap(L.WrapT));
            }
        }
    }

    static int Wrap(int w) => w == 0 ? GLES30.GlClampToEdge : w == 2 ? GLES30.GlMirroredRepeat : GLES30.GlRepeat;
    static int DepthFunc(int f) => f switch
    {
        0 => GLES30.GlNever, 1 => GLES30.GlLess, 2 => GLES30.GlEqual, 3 => GLES30.GlLequal,
        4 => GLES30.GlGreater, 5 => GLES30.GlNotequal, 6 => GLES30.GlGequal, _ => GLES30.GlAlways,
    };
    static int SrcFactor(int f) => f switch
    {
        0 => GLES30.GlZero, 1 => GLES30.GlOne, 2 => GLES30.GlDstColor, 3 => GLES30.GlOneMinusDstColor,
        4 => GLES30.GlSrcAlpha, 5 => GLES30.GlOneMinusSrcAlpha, 6 => GLES30.GlDstAlpha, _ => GLES30.GlOneMinusDstAlpha,
    };
    static int DstFactor(int f) => f switch
    {
        0 => GLES30.GlZero, 1 => GLES30.GlOne, 2 => GLES30.GlSrcColor, 3 => GLES30.GlOneMinusSrcColor,
        4 => GLES30.GlSrcAlpha, 5 => GLES30.GlOneMinusSrcAlpha, 6 => GLES30.GlDstAlpha, _ => GLES30.GlOneMinusDstAlpha,
    };

    // a few GL steps this frame, for about budgetMs (at least one); true once the suit is ready to draw (or has failed:
    // then MphRead keeps drawing its own Samus)
    public bool Warm(double budgetMs)
    {
        if (_glReady || _glFailed) return true;
        if (!TakeReprep(wait: false)) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            do Step();
            while (!_glReady && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < budgetMs);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        _warmFrames++;
        return _glReady || _glFailed;
    }

    // the draw needs it now: whatever Warm has not made yet, in this frame. A failure gives the body and ball back to
    // MphRead.
    bool EnsureGl()
    {
        if (_glReady) return true;
        if (_glFailed) return false;
        try
        {
            TakeReprep(wait: true);
            _warmFrames++;
            while (!_glReady) Step();
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    void Fail(Exception ex)
    {
        Log.Error(Tag, $"suit {Id}: GL setup failed -- MphRead draws its own Samus: {ex}");
        _glFailed = true;
        _prog = 0;
        _depthProg = 0;
        HasBody = HasBall = false;
    }

    // one GL step: 0 the interpreter + depth programs, then each material program, then the one vertex buffer (body +
    // ball), then each texture, then the batches' tables
    void Step()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        string what;
        _texOrder ??= TexKeys();
        int nSpec = _specSrc.Count;
        if (_step == 0)
        {
            what = "program";
            _prog = CampaignGlCache.Program(GunPrograms.Vert, GunPrograms.SuitInterpFrag, "GX (suit)");
            if (_prog == 0) throw new InvalidOperationException("the GX program did not link");
            _depthProg = CampaignGlCache.Program(GunPrograms.Vert, GunPrograms.DepthFrag, "GX depth (suit)");
            _specProg = new int[nSpec];
        }
        else if (_step <= nSpec)
        {
            what = "material program";
            _specProg[_step - 1] = CampaignGlCache.Program(GunPrograms.Vert, _specSrc[_step - 1], $"GX material {_step} of {nSpec} (suit)");
        }
        else if (_step == nSpec + 1)
        {
            what = "vertex buffer";
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _vao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _vbo = ids[0];
            GLES30.GlBindVertexArray(_vao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
            _vertBuf ??= _verts;
            GLES30.GlBufferData(GLES30.GlArrayBuffer, _verts.Length * 4, _vertBuf, GLES30.GlStaticDraw);
            int[] size = { 3, 3, 2, 3, 4, 4 }, off = { 0, 3, 6, 8, 11, 15 };
            for (int a = 0; a < 6; a++)
            {
                GLES30.GlEnableVertexAttribArray(a);
                GLES30.GlVertexAttribPointer(a, size[a], GLES30.GlFloat, false, Floats * 4, off[a] * 4);
            }
            GLES30.GlBindVertexArray(0);
        }
        else if (_step - nSpec - 2 < _texOrder.Count)
        {
            (string png, bool intensity) = _texOrder[_step - nSpec - 2];
            what = "texture " + Path.GetFileName(png);
            Texture(png, intensity);
        }
        else
        {
            what = "batches";
            foreach (List<Batch> list in new[] { _body, _ball })
            {
                foreach (Batch b in list)
                {
                    Textures(b);
                    int own = b.Spec >= 0 ? _specProg[b.Spec] : 0; // 0: it didn't link (logged), the interpreter
                    b.Prog = own != 0 ? own : _prog;
                }
            }
            // everything is in GL now: let the GC have the pixels and vertex copy
            _pixels.Clear();
            _vertBuf = null;
            _fadeOk = GLES30.GlGetUniformLocation(_prog, "uFade") >= 0;
            _glReady = true;
        }
        _step++;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _warmMs += ms;
        if (ms > _slowStepMs) { _slowStepMs = ms; _slowStep = what; }
        if (_glReady)
        {
            int n = 0, own = 0;
            foreach (List<Batch> list in new[] { _body, _ball })
            {
                foreach (Batch b in list)
                {
                    n++;
                    if (b.Prog != _prog) own++;
                }
            }
            Log.Info(Tag, $"suit {Id}: GL ready (program {_prog}, {own} of {n} materials on their own programs, depth "
                + $"{(_depthProg != 0 ? "first" : "with the colour")}, {_texIds.Count} textures{(_fadeOk ? "" : ", no fade: GxShader.Frag changed")}; "
                + $"{_warmMs:0} ms of GL work over {_warmFrames} frames, slowest step {_slowStepMs:0.0} ms: {_slowStep})");
        }
    }

    // every texture the batches sample, once each, in batch order
    List<(string Png, bool Intensity)> TexKeys()
    {
        var keys = new List<(string, bool)>();
        var seen = new HashSet<string>();
        foreach (List<Batch> list in new[] { _body, _ball })
        {
            foreach (Batch b in list)
            {
                for (int u = 0; u < 8; u++)
                {
                    if (b.Png[u] is string png && seen.Add(Key(png, b.Intensity[u]))) keys.Add((png, b.Intensity[u]));
                }
            }
        }
        return keys;
    }

    // every texture the batches sample, decoded four at a time (TryLoad, on its worker thread)
    void DecodeAll()
    {
        List<(string Png, bool Intensity)> keys = TexKeys();
        var read = new (int[] Px, int W, int H)?[keys.Count];
        Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => read[i] = Decode(keys[i].Png, keys[i].Intensity));
        for (int i = 0; i < keys.Count; i++) _pixels[Key(keys[i].Png, keys[i].Intensity)] = read[i];
    }

    // after a context loss: the pixels and vertices again, on a worker thread (the batches and _verts never change)
    bool TakeReprep(bool wait)
    {
        if (_reprep == null) return true;
        if (!_reprep.IsCompleted)
        {
            if (!wait) return false;
            _reprep.Wait();
        }
        var (pixels, verts) = _reprep.Result;
        foreach (var kv in pixels) _pixels[kv.Key] = kv.Value;
        _vertBuf = verts;
        _reprep = null;
        return true;
    }



    void Textures(Batch b)
    {
        for (int u = 0; u < 8; u++) b.Tex[u] = b.Png[u] is string png ? Texture(png, b.Intensity[u]) : White();
    }

    int Texture(string png, bool intensity)
    {
        string key = Key(png, intensity);
        if (_texIds.TryGetValue(key, out int id)) return id;
        (int[] Px, int W, int H)? img = _pixels.TryGetValue(key, out var pre) ? pre : Decode(png, intensity);
        id = img.HasValue ? Upload(img.Value.Px, img.Value.W, img.Value.H) : White();
        return _texIds[key] = id;
    }

    int White() => _white != 0 ? _white : _white = Upload(new[] { unchecked((int)0xFFFFFFFF) }, 1, 1);

    static int Upload(int[] px, int w, int h)
    {
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinearMipmapLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        return ids[0];
    }

    // the EGL context was recreated: forget every GL name (Warm / the next draw makes them again, from pixels a worker
    // thread reads again if they were already handed over)
    public void ResetGl()
    {
        ForgetGl();
        if (_reprep == null && (_pixels.Count == 0 || _vertBuf == null))
        {
            List<(string Png, bool Intensity)> keys = TexKeys();
            float[] verts = _verts;
            _reprep = Task.Run(() =>
            {
                var pixels = new Dictionary<string, (int[] Px, int W, int H)?>();
                foreach ((string png, bool intensity) in keys) pixels[Key(png, intensity)] = Decode(png, intensity);
                return (pixels, verts);
            });
        }
    }

    // this suit is being replaced: free its GL objects (on the GL thread; the program is CampaignGlCache's, shared)
    public void DeleteGl()
    {
        var tex = new HashSet<int>(_texIds.Values) { _white };
        foreach (int t in tex) if (t != 0) GLES30.GlDeleteTextures(1, new[] { t }, 0);
        if (_vao != 0) GLES30.GlDeleteVertexArrays(1, new[] { _vao }, 0);
        if (_vbo != 0) GLES30.GlDeleteBuffers(1, new[] { _vbo }, 0);
        ForgetGl();
    }

    void ForgetGl()
    {
        _prog = 0;
        _depthProg = 0;
        _specProg = Array.Empty<int>();
        _cur = 0;
        _vao = 0;
        _vbo = 0;
        _white = 0;
        _glFailed = false;
        _glReady = false;
        _step = 0;
        _warmFrames = 0;
        _warmMs = _slowStepMs = 0;
        _u.Clear();
        _texIds.Clear();
    }
}
