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
// Multiplayer skins (docs/CHARACTERS_AND_SKINS.md, MatchSkins.cs): the same class draws any hunter's rig (Samus's suits,
// the troopers on Weavel's) on any player -- one loaded skin serves every seat wearing it (each seat's ball roll is its
// own), with MphRead's red damage flash.
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
    IReadOnlyDictionary<string, Vector3>? _stand; // ... and its stance there (MenuSamus.StandFor: offsets per bone, rig units)

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
        public bool Cull;                // front faces only (a vein ball's vents: HdBall.IsVent)
    }

    readonly List<float> _build = new();
    float[] _verts = Array.Empty<float>();
    readonly List<Batch> _body = new(), _ball = new();
    // decoded in TryLoad (RGBA bytes, ready for GL), handed to GL by Warm / the first draw and dropped (after a context
    // loss a worker thread reads them again: _reprep)
    readonly Dictionary<string, (int[] Px, int W, int H)?> _pixels = new();
    // the DS model's own textures for its attached parts ("dspart<n>", see LoadBody): no PNG behind them, kept for a reprep
    readonly Dictionary<string, (int[] Px, int W, int H)> _raw = new();
    float[]? _vertBuf;
    Task<(Dictionary<string, (int[] Px, int W, int H)?> Pixels, float[] Verts)>? _reprep;

    // the body: the DS skeleton MphRead's clips pose, and the rig the HD mesh is bound to; _hunter = whose rig (Samus for
    // her suits, Weavel for the Space Pirates); _offArm = the pirates' blade arm, which keeps the legs layer's clip while
    // the torso fires (TrophyRigs.OffHandStaysDown), null for everyone else
    Hunter _hunter = Hunter.Samus;
    bool[]? _offArm;
    // a body converted from its own Retro model with its sheets as the disc has them (the final form Dark Samus, owner
    // 10-18: "when she jumps she's way brighter"): its additive parts once, its blended ones culled as the disc says --
    // CampaignRetroBody's look. The trophies keep the doubled sheets (DrawPass)
    bool _oneSheet;
    Model _dsModel = null!;
    DsSkeleton _skel = null!;
    TrophyRig _rig = null!;
    SuitFlourish? _flourish;
    PlayerEntity? _posing; // DrawBody's player while it poses (the flourish clocks are per seat)
    SkeletonPose _pose = null!;
    Matrix4[] _world = Array.Empty<Matrix4>();
    bool[] _legs = Array.Empty<bool>(), _torso = Array.Empty<bool>();
    readonly float[] _pal = new float[MaxBones * 16];

    // the ball: its model's own centre and width, and each player's roll so far
    Vector3 _ballCenter;
    float _ballWidth = 1f;
    sealed class Roll
    {
        public float Heading, Spin;
        public Vector3? Last;
    }
    readonly Dictionary<PlayerEntity, Roll> _rolls = new();
    Roll RollOf(PlayerEntity p) => _rolls.TryGetValue(p, out Roll? r) ? r : _rolls[p] = new Roll();
    readonly float[] _ballPal = new float[32];   // bone 0: the ball; bone 1: its beating heart (HdBall.Beat), else the same
    bool _ballBeat;                               // the ball has a heart (a vein ball's <stem>_heart0)
    bool _cullOn;
    // GxShader's uTint for this draw (the interpreter only): w = 1 recolours it (MphRead's red damage flash)
    Vector4 _tint;
    static readonly Vector4 DamageTint = new(1f, 0.28f, 0.22f, 1f);
    // the seat being drawn's team / duplicate tint (LookAlikes.BodyTint, through the interpreter as the flash; w = 0
    // none), set by MatchSkins around each draw: a skin is shared by every seat wearing it
    public Vector4 SeatTint { get; set; }
    // ... and a same-look copy's lights colour (LookAlikes.BodyLight: GxShader uTint2 with w 9+k)
    public Vector4 SeatTint2 { get; set; }

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
    // any hunter's (the rig a skin is on: RigHunter); GL thread only, as SamusModel
    public static Model HunterModel(Hunter h) => Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
    // the hunter whose rig hd/<id> is on (Samus when it isn't one: a morph ball alone)
    public static Hunter RigHunter(string id) => TrophyRigs.TryHunterFor(id, out Hunter h) ? h : Hunter.Samus;

    // hd/<id>/ (the body, on its hunter's rig: RigHunter) and hd/<id>Ball; null when neither is there, or for
    // "original" / "none". CPU work only, safe on a worker thread (GL objects are made by Warm / the first draw); samus =
    // that hunter's MphRead model (Samus's for her suits, despite the name), looked up by the caller on the GL thread
    // (HunterModel; else read here). Never fits a rig: the body comes from the model's cached rig files or not at all.
    public static CampaignSuit? TryLoad(string hdRoot, string id, Model? samus = null, bool asBuilt = false,
        IReadOnlyDictionary<string, Vector3>? stand = null)
    {
        if (string.IsNullOrEmpty(id) || id.Equals("original", StringComparison.OrdinalIgnoreCase) || id.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // only imported, up-to-date models (ImportedContent: a stale import waits for IMPORT AGAIN; a public build wears
        // nothing hand-dropped, e.g. no Brawl trophies)
        if (!MphRecomp.Import.ImportedContent.Usable(hdRoot, id))
        {
            Log.Info(Tag, $"suit {id}: not imported (or out of date) here -- MPH's own Samus");
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var suit = new CampaignSuit(id) { _asBuilt = asBuilt, _stand = asBuilt ? stand : null };
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
        if (!TrophyRigs.TryHunterFor(Id, out Hunter h)) return "none (not a model on a hunter's rig)";
        string dir = Path.Combine(hdRoot, Id);
        string[] daes = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.dae") : Array.Empty<string>();
        if (daes.Length == 0) return $"none (no .dae in hd/{Id})";
        string daePath = daes[0];
        _hunter = h;
        string want = Metadata.HunterModels[h][0];
        Model rm = samus != null && samus.Name == want ? samus : HunterModel(h);
        if (MathF.Abs(rm.Scale.X - 1f) > 1e-6f) Log.Warn(Tag, $"suit {Id}: {rm.Name} model scale {rm.Scale.X} != 1 (rigs assume 1)");
        var sk = new DsSkeleton(rm);
        if (TrophyRigs.OffHandStaysDown(Id)) _offArm = sk.Subtree("L_shoulder");
        TrophyRig? rig = PickRig(daePath, rm, sk, out string label);
        if (rig == null) return $"none (no rig cached for {Path.GetFileName(daePath)}: prepare one on the PC, it is never fitted here)";
        if (sk.Count > MaxBones || rig.InvBind.Length < sk.Count || rig.Lengths.Length < sk.Count || rig.Offsets.Length < sk.Count)
        {
            return $"none (the {label} rig has {rig.InvBind.Length} bones, {h}'s skeleton {sk.Count})";
        }
        // feet on the floor where the game model's are (the hip height the sculpt gave can sink them in or lift them)
        try { TrophyFeet.Settle(rig, rm, sk, m => Log.Info(Tag, $"suit {Id} [{label}]:" + m)); }
        catch (Exception ex) { Log.Warn(Tag, $"suit {Id}: feet settle failed: {ex.Message}"); }

        DaeModel dae = DaeModel.Load(daePath);
        Dictionary<string, GxMaterial> gx = ReadGx(dir, daePath, out Dictionary<string, int> formats);
        Matrix4 g = rig.TrophyToRig;
        Vector3[]? stand = MphRecomp.Frontend.MenuSamus.StandOffsets(_stand, sk.Names);
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
                    if (stand != null && vi >= 0) rp = MphRecomp.Frontend.MenuSamus.Stand(rp, stand, rig.Bones, rig.Weights, vi);
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
            // the DS model's own parts the rig attaches (a held gun: Zero Suit Samus holds Weavel's, HdRecipe "hold"), each
            // through a one-stage GX program (texture x lit vertex colour), its pixels straight from the DS model
            int partTris = 0, n = 0;
            GxMaterial? like = System.Linq.Enumerable.FirstOrDefault(gx.Values, x => x.Lit) ?? System.Linq.Enumerable.FirstOrDefault(gx.Values);
            foreach (var (src, corners) in TrophyRigger.ReplacementParts(rm, rig))
            {
                if (corners.Count == 0) continue;
                string key = $"dspart{n++}";
                if (src.Pixels != null) _raw[key] = (src.Pixels, src.W, src.H);
                int start = _build.Count / Floats;
                foreach (TrophyRigger.ReplacementCorner c in corners)
                {
                    _build.Add(c.Pos.X); _build.Add(c.Pos.Y); _build.Add(c.Pos.Z); _build.Add(c.Normal.X); _build.Add(c.Normal.Y); _build.Add(c.Normal.Z);
                    _build.Add(c.U); _build.Add(c.V); _build.Add(c.R); _build.Add(c.G); _build.Add(GxShader.PackBlueAlpha(c.B, 1f));
                    Rigid(c.Node);
                }
                var b = new Batch { Start = start, Count = corners.Count, Gx = DsPartMaterial(key, like, src.Pixels != null) };
                b.P = GxShader.Pack(b.Gx);
                if (src.Pixels != null) b.Png[0] = key;
                _body.Add(b);
                partTris += corners.Count / 3;
            }
            tris += partTris;
            Log.Info(Tag, $"suit {Id}: {partTris} triangles of {rm.Name}'s own parts attached (held)");
        }
        if (_body.Count == 0) return $"none (no part of {Path.GetFileName(daePath)} has a GX program: no .brres or .gx.json?)";
        _oneSheet = Id.Equals("MP2DarkSamusFinal", StringComparison.OrdinalIgnoreCase);
        if (_oneSheet) foreach (Batch b in _body) b.Cull = b.Pass != 0 && (b.Gx.CullMode & 3) == 2;
        _dsModel = rm;
        _skel = sk;
        _rig = rig;
        // its own game's idle flourish, when the import made one (SuitFlourish: Beyond Sylux)
        _flourish = SuitFlourish.Load(daePath, sk, m => Log.Info(Tag, $"suit {Id}: {m}"));
        _pose = new SkeletonPose(sk.Count);
        _world = new Matrix4[sk.Count];
        _legs = new bool[sk.Count];
        _torso = new bool[sk.Count];
        for (int i = 0; i < sk.Count; i++)
        {
            _torso[i] = sk.AboveSpine[i];
            _legs[i] = !sk.AboveSpine[i];
        }
        SetupUpright();
        HasBody = true;
        return $"{label} rig ({_body.Count} batches, {tris} tris, {hidden} hidden, {missing} unmapped corners)";
    }

    // The rig, from cache only, in RenderActivity's order: the owner's pose (Pose Studio, on its base rig) > PIECES >
    // RETRO > RIGID > the sculpt-bound <dae>.mphrig. LoadOrFit would fit when that last cache is stale, so it is read
    // with Load + its cache key instead.
    TrophyRig? PickRig(string dae, Model rm, DsSkeleton sk, out string label)
    {
        Action<string> log = m => Log.Info(Tag, $"suit {Id}: " + m);
        // a public build plays the rigs the import makes (retro, rigid) only: the pose file and the PIECES rig are
        // hand-made on the PC (Pose Studio, rig work) and never come from an import
        bool handMade = !MphRecomp.Config.RecompSettings.PublicBuild;
        try
        {
            string posedPath = TrophyRigIO.PosedPathFor(dae);
            if (handMade && File.Exists(posedPath))
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
        foreach (string key in handMade ? new[] { "pieces", "retro", "rigid" } : new[] { "retro", "rigid" })
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
        if (!MphRecomp.Import.ImportedContent.Usable(hdRoot, info.Id)) return $"none ({info.Id} isn't imported)"; // ship-safe audit
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
            bool heart = HdBall.IsHeart(part.Material);
            _ballBeat |= heart;
            for (int i = 0; i < part.Positions.Count; i++)
            {
                System.Numerics.Vector3 q = part.Positions[i];
                Corner(new Vector3(q.X, q.Y, q.Z), Normal(part, i), part, i);
                Rigid(heart ? 1 : 0);
            }
            Batch nb = MakeBatch(start, part.Positions.Count, m, info.Folder, formats);
            nb.Cull = HdBall.IsVent(part.Material);
            _ball.Add(nb);
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

    // a DS part's program: one stage, its texture (unit 0, the mesh's UVs) x the body's lit light channel 0, alpha tested
    static GxMaterial DsPartMaterial(string tex, GxMaterial? like, bool textured)
    {
        var m = new GxMaterial { Name = tex, NumTevStage = 1, NumTexGen = textured ? 1 : 0, NumChans = 1, CullMode = 0 };
        m.Stages = new[]
        {
            textured
                ? new GxTevStage { Ca = 15, Cb = 8, Cc = 10, Cd = 15, Cclamp = 1, Aa = 7, Ab = 4, Ac = 5, Ad = 7, Aclamp = 1, TexMap = 0, TexCoord = 0, RasChan = 0 }
                : new GxTevStage { Ca = 15, Cb = 15, Cc = 15, Cd = 10, Cclamp = 1, Aa = 7, Ab = 7, Ac = 7, Ad = 5, Aclamp = 1, TexMap = -1, RasChan = 0 },
        };
        if (like != null)
        {
            m.Chan[0] = like.Chan[0];
            m.ColorControl = like.ColorControl;
            m.MatColor = like.MatColor;
            m.AmbColor = like.AmbColor;
        }
        m.Chan[0].Mat = System.Numerics.Vector4.One;
        if (textured)
        {
            m.TexGenSrc[0] = 5;
            m.Layers[0] = new GxLayer { Name = tex, TexMapId = 0 };
            m.TexNames[0] = tex;
            m.AlphaTest = true;
            m.AlphaComp0 = 6; // GEQUAL
            m.AlphaRef0 = 128;
            m.AlphaComp1 = 7;
        }
        return m;
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

    // a texture's pixels: a DS part's own (_raw), else its PNG
    (int[] Px, int W, int H)? PixelsOf(string png, bool intensity) => _raw.TryGetValue(png, out var raw) ? raw : Decode(png, intensity);

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
        _flourish?.Tick(player);
        if (!HasBall) return;
        Roll r = RollOf(player);
        if (!player.IsAltForm)
        {
            r.Last = null; // morphing again starts from where the ball is then, not where it last rolled
            return;
        }
        Vector3 now = player.Position;
        if (r.Last is Vector3 last)
        {
            float dx = now.X - last.X, dz = now.Z - last.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
            // (a jump of several units in one step is a teleport, not a roll)
            if (dist > 1e-4f && dist < 5f)
            {
                r.Heading = LerpAngle(r.Heading, MathF.Atan2(dx, -dz), 0.35f);   // facing = (sin, 0, -cos)
                r.Spin = (r.Spin + dist / BallRadius(player)) % (2f * MathF.PI);
            }
        }
        else
        {
            r.Heading = MathF.Atan2(player.Field70, -player.Field74);
        }
        r.Last = now;
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
    // lift: drawn this far (world units) above the player (a native body's hover kept while this suit stands in for it)
    public void DrawBody(PlayerEntity player, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha, bool flash = false, float lift = 0)
    {
        _posing = player;
        try
        {
            DrawBodyAt(player.HostBipedModel1.AnimInfo, player.BipedModel2.AnimInfo, player.FacingVector.Y, player.Position + new Vector3(0, lift, 0),
                new Vector3(player.Field70, 0, player.Field74), view, proj, pass, alpha, flash);
        }
        finally { _posing = null; }
        if (pass == SuitPass.Opaque) GunFlashOnTip(player);
    }

    // a body with no player of its own (the Trooper's standing body while his Scout Drone flies, HalfturretEntity's
    // HostBodyModel): one clip on both layers, level, where a player at `position` facing `facing` would stand
    public void DrawStanding(AnimationInfo anim, Vector3 position, Vector3 facing, Matrix4 view, Matrix4 proj, SuitPass pass,
        bool flash = false) => DrawBodyAt(anim, anim, 0f, position, facing, view, proj, pass, 1f, flash);

    void DrawBodyAt(AnimationInfo legs, AnimationInfo torso, float pitch, Vector3 position, Vector3 facing, Matrix4 view,
        Matrix4 proj, SuitPass pass, float alpha, bool flash)
    {
        if (!HasBody || alpha <= 0f || !EnsureGl()) return;
        try
        {
            Flash(flash);
            bool rebuild = pass == SuitPass.DepthRebuild;
            int p = rebuild ? 0 : (int)pass;
            bool fade = alpha < 0.999f && _fadeOk;
            bool own = !(fade && p == 0);
            bool moved = fade && p == 1;
            if (!(own && Has(_body, p)) && !(moved && Has(_body, 0))) return;
            Matrix4 model = HunterRig.ModelMatrix(_dsModel, HunterRig.Placement(_hunter, position, facing));
            if (_upright != 0) AimInModel(model, facing, pitch);
            Pose(legs, torso, pitch);
            _lastModel = model;
            Begin(view, proj, model, _pal, _skel.Count);
            double s = Environment.TickCount64 / 1000.0;
            if (moved) DrawFaded(_body, alpha, s);
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
            Flash(false);
            End();
        }
    }

    // MphRead's damage flash (PlayerEntity.HostDamageFlash: it draws its own model in its red palette): the whole draw
    // through the interpreter, recoloured red (the materials' own programs have no tint)
    void Flash(bool on, bool seatTint = true)
    {
        _interpOnly = on || seatTint && SeatTint.W > 0;
        _tint = on ? DamageTint : seatTint ? SeatTint : Vector4.Zero;
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
                for (int k = 0; k < (p == 2 && !_oneSheet ? 2 : 1); k++) // DrawPass's doubled additive sheets
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

    // DrawStill(clip, frame)'s head: its middle, facing and size in the rig's space, from the skin's head landmarks (the
    // character pictures' head framing, CharacterPictureRender -- reached in 2026-10-09); null without a head joint
    public (Vector3 Center, Vector3 Facing, float Radius)? StillHead(int clip, int frame, (Vector3 Center, Vector3 Joint, Vector3? Ahead) head)
    {
        if (!HasBody) return null;
        Matrix4[] world = _rig.BindWorld; // clip < 0: no pose (DrawStill's identity skin)
        if (clip >= 0)
        {
            if (clip < _dsModel.AnimationGroups.Node.Count) DsSkeleton.Sample(_dsModel, clip, frame, _pose);
            _skel.Fk(_pose, _world, 0f, _rig.Lengths, _rig.Offsets);
            world = _world;
        }
        return MphRecomp.Frontend.CharacterPictures.PosedHead(_rig, _skel, world, head);
    }

    // MphRead's own biped pose (PlayerDraw): the legs layer up to Spine_1, the torso layer above it, the spine tilted by
    // the aim -- on the rig's bone lengths / rest offsets; skin matrix = inverse bind * pose (RenderActivity's HdPalette)
    void Pose(AnimationInfo legs, AnimationInfo torso, float pitch)
    {
        Sample(legs.Index[0], legs.Frame[0], _legs);
        Sample(torso.Index[0], torso.Frame[0], _torso);
        // a Space Pirate fires one-handed: the blade arm stays on the legs layer's clip (RenderActivity's custom players)
        if (_offArm != null && TrophyRigs.IsFiringClip(torso.Index[0])) Sample(legs.Index[0], legs.Frame[0], _offArm);
        // Zero Suit: her gun arm also keeps the legs layer's (idle / walk) bent arm while firing -- Weavel's firing clips
        // fold it into the middle of her chest; HoldGunArm then turns just the forearm onto the aim
        if (_upright != 0 && _armShoulder != null && TrophyRigs.IsFiringClip(torso.Index[0])) Sample(legs.Index[0], legs.Frame[0], _armShoulder);
        _skel.Fk(_pose, _world, pitch, _rig.Lengths, _rig.Offsets);
        Straighten();
        if (_posing != null) _flourish?.Apply(_posing, _rig, _world);
        for (int i = 0; i < _skel.Count; i++) HunterRig.ToArray(_rig.InvBind[i] * _world[i], _pal, i * 16);
    }

    // Zero Suit Samus on Weavel's rig (owner 2026-10-12: "go back to putting her on a vanilla rig but just updating her
    // posture to be a little more upright"): an armoured hunter's clips hunch the chest forward, so her upper body is tilted
    // back about the waist (Spine_1, about its own side axis -- the aim's), then each shoulder and the head are turned back
    // by the same amount about their own joints, so the gun stays on the aim and her eyes stay level. Dev: --es zsupright <deg>
    public static float ZeroSuitUprightDeg = 25f;
    float _upright; // radians, 0 = none (every other skin)
    bool[]? _uprightAll, _uprightL, _uprightR, _uprightHead;

    void SetupUpright()
    {
        _upright = 0;
        if (!Id.Contains("ZeroSuit", StringComparison.Ordinal) || _hunter != Hunter.Weavel) return;
        int sp = _skel.IndexOf("Spine_1");
        if (sp < 0) return;
        _upright = MathHelper.DegreesToRadians(ZeroSuitUprightDeg);
        _uprightAll = _skel.Subtree("Spine_1");
        _uprightL = _skel.Subtree("L_shoulder");
        _uprightR = _skel.Subtree("R_shoulder");
        _uprightHead = _skel.Subtree("Head_1");
        Log.Info(Tag, $"suit {Id}: upright {ZeroSuitUprightDeg:0.#} deg (shoulders {_skel.IndexOf("L_shoulder")}/{_skel.IndexOf("R_shoulder")}, head {_skel.IndexOf("Head_1")})");
    }

    void Straighten()
    {
        if (_upright == 0 || _uprightAll == null) return;
        int sp = _skel.IndexOf("Spine_1");
        Vector3 axis = _world[sp].Row2.Xyz;
        if (axis.LengthSquared < 1e-8f) return;
        axis.Normalize();
        Turn(_uprightAll, sp, axis, _upright);
        foreach ((bool[]? part, string root) in new[] { (_uprightL, "L_shoulder"), (_uprightR, "R_shoulder"), (_uprightHead, "Head_1") })
        {
            int r = _skel.IndexOf(root);
            if (part != null && r >= 0) Turn(part, r, axis, -_upright);
        }
        HoldGunArm();
    }

    // her gun arm held straight out along the aim from her right shoulder, where a hunter's arm cannon is (owner 2026-10-12:
    // Weavel's firing clips fold the arm into the middle of her chest, away from the muzzle flash): the elbow straightened,
    // then the whole arm turned onto the aim. The flash then goes on the gun's own tip (GunFlashOnTip). Dev: zsupright 0 = off
    Vector3 _aimModel; // the aim in the rig's model space (DrawBodyAt)
    Matrix4 _lastModel = Matrix4.Identity;
    bool[]? _armShoulder, _armElbow;
    bool _armHeld;

    void AimInModel(Matrix4 model, Vector3 facing, float pitch)
    {
        var flat = new Vector3(facing.X, 0, facing.Z);
        if (flat.LengthSquared < 1e-12f) return;
        flat.Normalize();
        float y = Math.Clamp(pitch, -1f, 1f), h = MathF.Sqrt(1 - y * y);
        Vector3 aim = Vector3.TransformVector(new Vector3(flat.X * h, y, flat.Z * h), model.Inverted());
        if (aim.LengthSquared > 1e-12f) _aimModel = aim.Normalized();
    }

    void HoldGunArm()
    {
        _armHeld = false;
        // Weavel's gun arm ends at R_elbow (his cannon is the forearm: no R_wrist); her forearm, hand and the held gun ride it
        int sh = _skel.IndexOf("R_shoulder"), el = _skel.IndexOf("R_elbow");
        if (sh < 0 || el < 0 || _aimModel.LengthSquared < 0.5f)
        {
            if (!_armLogged) Log.Warn(Tag, $"suit {Id}: gun arm NOT held (shoulder {sh}, elbow {el}, aim {_aimModel})");
            _armLogged = true;
            return;
        }
        _armShoulder ??= _skel.Subtree("R_shoulder");
        _armElbow ??= _skel.Subtree("R_elbow");
        // the upper arm as the clip bends it (a straight arm out on the aim looked rigid and stretched her shoulder skin,
        // owner 2026-10-12); just the forearm's barrel line (elbow -> MPH's muzzle point on R_elbow) onto the aim, so the
        // gun and its flash stay on the aim
        TurnOnto(_armElbow, el, Muzzle() - _world[el].Row3.Xyz, _aimModel);
        _armHeld = true;
        if (!_armLogged)
        {
            _armLogged = true;
            Vector3 got = (Muzzle() - _world[el].Row3.Xyz).Normalized();
            Log.Info(Tag, $"suit {Id}: gun arm held: aim (model) {_aimModel.X:0.00},{_aimModel.Y:0.00},{_aimModel.Z:0.00}, barrel "
                + $"{got.X:0.00},{got.Y:0.00},{got.Z:0.00}");
        }
    }

    // MPH's muzzle point (its shoot node R_elbow + Weavel's muzzle offset) in the rig's model space, on her posed arm
    Vector3 Muzzle() => MphRead.Matrix.Vec3MultMtx4(MphRead.Metadata.MuzzleOffests[(int)Hunter.Weavel], _world[_skel.IndexOf("R_elbow")]);
    bool _armLogged;

    // every marked node turned about node `pivot`'s joint by the shortest turn taking direction `from` onto `to`
    void TurnOnto(bool[] nodes, int pivot, Vector3 from, Vector3 to)
    {
        if (from.LengthSquared < 1e-10f || to.LengthSquared < 1e-10f) return;
        from.Normalize(); to.Normalize();
        float d = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        Vector3 axis = Vector3.Cross(from, to);
        if (d > 0.99999f || axis.LengthSquared < 1e-12f) return;
        axis.Normalize();
        float angle = MathF.Acos(d);
        // OpenTK's axis-angle sense in row-vector use: take whichever sign lands `from` on `to`
        if (Vector3.Dot(Vector3.TransformVector(from, Matrix4.CreateFromAxisAngle(axis, angle)), to) < d) angle = -angle;
        Turn(nodes, pivot, axis, angle);
    }

    // third person: her muzzle flash / charge glow on the held gun's tip -- MPH's own muzzle offset on R_elbow (its shoot
    // node, PlayerEntity) in her posed arm -- as PlayerDraw's HostFxShootOffset (left, up, forward of her position by her
    // facing; MatchArmCannon.CannonFx3P clears it each frame before the skins draw, so it is set again every frame)
    void GunFlashOnTip(PlayerEntity player)
    {
        if (!_armHeld) return;
        int el = _skel.IndexOf("R_elbow");
        var ahead = new Vector3(player.Field70, 0, player.Field74);
        if (el < 0 || ahead.LengthSquared < 1e-12f) return;
        ahead.Normalize();
        Vector3 tip = Vector3.TransformPosition(Muzzle(), _lastModel);
        Vector3 d = tip - player.Position;
        player.HostFxShootOffset = new Vector3(Vector3.Dot(d, Vector3.Cross(Vector3.UnitY, ahead)), d.Y, Vector3.Dot(d, ahead));
    }

    // every marked node's model-space matrix turned by `angle` about `axis` through node `pivot`'s joint
    void Turn(bool[] nodes, int pivot, Vector3 axis, float angle)
    {
        Vector3 at = _world[pivot].Row3.Xyz;
        Matrix4 r = Matrix4.CreateTranslation(-at) * Matrix4.CreateFromAxisAngle(axis, angle) * Matrix4.CreateTranslation(at);
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i]) _world[i] *= r;
        }
    }

    void Sample(int clip, int frame, bool[] nodes)
    {
        if (clip >= 0 && clip < _dsModel.AnimationGroups.Node.Count) DsSkeleton.Sample(_dsModel, clip, frame, _pose, nodes);
    }

    // the morph ball where MphRead's own ball is: its centre at the player's position + AltColYPos, as wide as MPH's
    // (2 x AltColRadius). HdBall.Placement rests the ball on y = 0, so it is dropped by its radius first.
    // (small on screen: no depth prepass; pass 4 is its depth only, as the body's)
    // alpha = MphRead's own for the alt model (PlayerEntity.CurAlpha: the death fade, cloak); while it fades the opaque
    // parts move to the Translucent pass, as the body's (DrawBodyAt)
    public void DrawBall(PlayerEntity player, Matrix4 view, Matrix4 proj, SuitPass pass, bool flash = false, float alpha = 1f)
    {
        bool rebuild = pass == SuitPass.DepthRebuild;
        int p = rebuild ? 0 : (int)pass;
        bool fade = alpha < 0.999f && _fadeOk;
        bool own = !(fade && p == 0);
        bool moved = fade && p == 1;
        if (!HasBall || alpha <= 0f || !(own && Has(_ball, p)) && !(moved && Has(_ball, 0)) || !EnsureGl()) return;
        try
        {
            Flash(flash && !rebuild, !rebuild);
            Roll r = RollOf(player);
            Matrix4 place = HdBall.Placement(_ballCenter, _ballWidth, r.Spin, r.Heading);
            HunterRig.ToArray(place, _ballPal);
            HunterRig.ToArray(_ballBeat ? HdBall.BeatAbout(_ballCenter, Environment.TickCount64 / 1000.0) * place : place, _ballPal, 16);
            float half = HdBall.GameDiameter / 2f;
            Vector3 centre = player.Position;
            centre.Y += Fixed.ToFloat(player.Values.AltColYPos);
            Matrix4 model = Matrix4.CreateTranslation(0, -half, 0) * Matrix4.CreateScale(BallRadius(player) / half)
                * Matrix4.CreateTranslation(centre);
            Begin(view, proj, model, _ballPal, 2);
            double s = Environment.TickCount64 / 1000.0;
            if (moved) DrawFaded(_ball, alpha, s);
            if (own && rebuild && _depthProg != 0)
            {
                DrawDepth(_ball);
                DrawPass(_ball, 0, 1f, s, DepthDone.Skip);
            }
            else if (own) DrawPass(_ball, p, fade ? alpha : 1f, s);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"suit {Id}: ball draw failed -- MphRead draws its own ball from now on: {ex}");
            HasBall = false;
        }
        finally
        {
            Flash(false);
            End();
        }
    }

    // An imported model drawn still, as it was built (IMPORT GAMES' tile and card pictures, ImportItemRender -- Importer
    // menu visual design's, reached in 2026-10-08): any hd/<name> folder (a suit, a morph ball, a character), loaded the way
    // LoadBall loads a ball -- every corner on bone 0, no rig needed -- into the ball's batches. StillMin / StillMax: the
    // model's own box (DAE space: Y up, feet on y = 0, facing +Z). CPU work only, safe on a worker thread.
    public Vector3 StillMin { get; private set; }
    public Vector3 StillMax { get; private set; }

    public static CampaignSuit? TryLoadStill(string folder, string id)
    {
        try
        {
            string[] daes = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.dae") : Array.Empty<string>();
            if (daes.Length == 0) return null;
            var suit = new CampaignSuit(id) { _asBuilt = true };
            DaeModel dae = DaeModel.Load(daes[0]);
            Dictionary<string, GxMaterial> gx = ReadGx(folder, daes[0], out Dictionary<string, int> formats);
            suit.StillMin = new Vector3(dae.Min.X, dae.Min.Y, dae.Min.Z);
            suit.StillMax = new Vector3(dae.Max.X, dae.Max.Y, dae.Max.Z);
            foreach (DaeMesh part in dae.Meshes)
            {
                if (!gx.TryGetValue(part.Material, out GxMaterial? m)) continue;
                int start = suit._build.Count / Floats;
                for (int i = 0; i < part.Positions.Count; i++)
                {
                    System.Numerics.Vector3 q = part.Positions[i];
                    suit.Corner(new Vector3(q.X, q.Y, q.Z), Normal(part, i), part, i);
                    suit.Rigid(0);
                }
                suit._ball.Add(suit.MakeBatch(start, part.Positions.Count, m, folder, formats));
            }
            if (suit._ball.Count == 0) return null;
            suit.HasBall = true;
            suit.PlanPrograms();
            suit.DecodeAll();
            suit._verts = suit._build.ToArray();
            suit._build.Clear();
            suit._build.TrimExcess();
            suit._vertBuf = suit._verts;
            return suit;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"still {id}: load failed: {ex}");
            return null;
        }
    }

    // TryLoadStill's model, every pass, model = identity, on the interpreter (as DrawStill); hide / opaque as DrawStill's
    // (MenuSamus' per-suit face and visor pane). The caller binds its framebuffer and enables the depth test.
    public bool DrawStillModel(Matrix4 view, Matrix4 proj, ICollection<string>? hide = null, ICollection<string>? opaque = null)
    {
        if (!HasBall || !EnsureGl()) return false;
        try
        {
            _interpOnly = true;
            HunterRig.ToArray(Matrix4.Identity, _ballPal);
            HunterRig.ToArray(Matrix4.Identity, _ballPal, 16);
            Begin(view, proj, Matrix4.Identity, _ballPal, 2);
            for (int p = 0; p < 3; p++)
            {
                for (int k = 0; k < (p == 2 ? 2 : 1); k++) // DrawPass's doubled additive sheets
                {
                    foreach (Batch b in _ball)
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
            Log.Error(Tag, $"still {Id}: draw failed: {ex}");
            return false;
        }
        finally
        {
            _interpOnly = false;
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
        int times = pass == 2 && !(_oneSheet && batches == _body) ? 2 : 1;
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
    void DrawFaded(List<Batch> batches, float alpha, double seconds)
    {
        if (_depthProg != 0)
        {
            GLES30.GlColorMask(false, false, false, false);
            DrawDepth(batches);
            GLES30.GlColorMask(true, true, true, true);
        }
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlZero, GLES30.GlOne);
        GLES30.GlDepthMask(true);
        foreach (Batch b in batches)
        {
            if (b.Pass != 0 || _depthProg != 0 && b.DepthFirst) continue;
            Material(b, 1f, seconds);
            GLES30.GlDepthFunc(DepthFunc(b.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthMask(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        foreach (Batch b in batches)
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
        if (b.Cull != _cullOn)
        {
            _cullOn = b.Cull;
            if (_cullOn)
            {
                GLES30.GlEnable(0x0B44);   // GL_CULL_FACE, the back (GL_BACK; counter-clockwise faces the camera)
                GLES30.GlCullFace(0x0405);
            }
            else GLES30.GlDisable(0x0B44);
        }
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
        _cullOn = false;
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
        GLES30.GlUniform4f(U(prog, "uTint"), _tint.X, _tint.Y, _tint.Z, _tint.W);
        GLES30.GlUniform4f(U(prog, "uTint2"), SeatTint2.X, SeatTint2.Y, SeatTint2.Z, 0);
        for (int t = 0; t < 8; t++)
        {
            GLES30.GlUniform1i(U(prog, TexUniform[t]), t);
        }
    }

    void End()
    {
        if (_cullOn) { GLES30.GlDisable(0x0B44); _cullOn = false; } // a culled batch (the vein ball's vents) never outlives the draw
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
        Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => read[i] = PixelsOf(keys[i].Png, keys[i].Intensity));
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
        (int[] Px, int W, int H)? img = _pixels.TryGetValue(key, out var pre) ? pre : PixelsOf(png, intensity);
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
                foreach ((string png, bool intensity) in keys) pixels[Key(png, intensity)] = PixelsOf(png, intensity);
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
