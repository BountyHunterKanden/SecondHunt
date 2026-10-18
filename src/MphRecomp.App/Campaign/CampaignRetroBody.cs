using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// A native Retro body in a match (docs/CHARACTERS_AND_SKINS.md D22: the Trooper on the Prime 1 pirate rig): the imported
// bodies/<skin>/ folder (Import/Bodies/BodyImporter; the owner's own disc, never bundled) posed by its own clips
// (NativeBody + one NativeBodyAnimator per seat, from MphRead's biped state), drawn where MphRead stands the player in
// place of its DS model and of the HD suit's body (MatchSkins). One per distinct skin, shared by every seat wearing it.
// The GL side is CampaignSuit's: each material on its own TEV program (GunPrograms.SuitFrag) where ArenaGxSpec can
// write one, else the interpreter (with uFade); the opaque parts' depth first; the suits' passes, unmorph fade and red
// damage flash; the trophy viewer's light. Made a step at a time (Warm) from vertices and pixels a worker readied.
internal sealed partial class CampaignRetroBody
{
    const string Tag = "MPHCampaign";
    const float Dt = 1f / 60f; // one MphRead step

    public readonly string Id;
    public readonly NativeBody Body;
    public readonly BodyClips Clips;
    readonly string _dir;
    readonly Dictionary<object, NativeBodyAnimator> _anims = new();
    public bool Failed { get; private set; }

    sealed class Batch
    {
        public int Material, Start, Count, Pass; // pass: 0 opaque, 1 blended, 2 additive
        public int Spec = -1, Prog;
        // a Brawl body's own pixel-engine state (her layered hair: back faces culled, blended shells still writing
        // depth); the Retro bodies draw their opaque parts unculled, blended parts culled as their material says, without depth
        public int Cull;            // GX cull mode: 0 none, 1 front, 2 back, 3 all
        public bool BlendDepth;
        public bool DepthFirst => Pass == 0 && P.AlphaTest[3] != 1;
        public GxMaterial Gx = null!;
        public GxShader.Params P = null!;
        public readonly int[] Tex = new int[8];
    }

    readonly List<Batch> _batches = new();
    float[]? _verts;
    readonly Dictionary<string, (int[] Px, int W, int H)> _pixels = new();
    readonly List<string> _texNames = new();
    readonly List<string> _specSrc = new();
    int[] _specProg = Array.Empty<int>();
    int _prog, _depthProg, _vao, _vbo, _cur, _step;
    bool _glReady, _fadeOk, _interpOnly;
    Vector4 _tint;
    static readonly Vector4 DamageTint = new(1f, 0.28f, 0.22f, 1f);
    // the seat being drawn's team / duplicate tint (LookAlikes.BodyTint; w = 0 none), set by MatchSkins around each draw:
    // the body is shared by every seat wearing it
    public Vector4 SeatTint { get; set; }
    // ... and a same-look copy's lights colour (LookAlikes.BodyLight: GxShader uTint2 with w 9+k)
    public Vector4 SeatTint2 { get; set; }
    readonly Dictionary<(int, string), int> _u = new();
    readonly Dictionary<string, int> _texIds = new();
    readonly float[] _mvp16 = new float[16], _m16 = new float[16], _viewRot = new float[9], _pal = new float[32 * 16];

    static readonly float[] LightDir = Norm3(-50f, 50f, 90f);
    static readonly float[] LightCol = { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] LightSpecCol = { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] LightSpecK = { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 };
    static float[] Norm3(float x, float y, float z)
    {
        float l = MathF.Sqrt(x * x + y * y + z * z);
        return new[] { x / l, y / l, z / l, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    public static string DirOf(string bodiesRoot, string id) => Path.Combine(bodiesRoot, id);

    // (imported and up to date: ImportedContent; a public build uses no body pushed by hand)
    public static bool Imported(string? bodiesRoot, string id) =>
        bodiesRoot != null && NativeBody.Exists(DirOf(bodiesRoot, id)) && MphRecomp.Import.ImportedContent.Usable(bodiesRoot, id);

    // a worker thread: the body, its clip map, its batches and programs (CPU side) and every texture's pixels; null when
    // it isn't there or isn't a body this app knows the clips of
    public static CampaignRetroBody? TryLoad(string bodiesRoot, string id)
    {
        string dir = DirOf(bodiesRoot, id);
        if (!Imported(bodiesRoot, id)) return null;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            NativeBody body = NativeBody.Load(dir);
            BodyClips? clips = BodyClips.For(body);
            if (clips == null)
            {
                Log.Warn(Tag, $"body {id}: no clip map for its clips -- the HD suit or MphRead's own model instead");
                return null;
            }
            var b = new CampaignRetroBody(id, dir, body, clips);
            Log.Info(Tag, $"body {id}: {body.Data.BoneCount} bones, {body.Data.Materials.Length} materials, {body.Data.Anims.Count} clips, "
                + $"height {body.Height:0.00} (scale {body.Scale:0.000}), {b._texNames.Count} textures; loaded in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
            return b;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"body {id}: load failed: {ex.Message}");
            return null;
        }
    }

    // GX colour register 1 as the game's code sets it when it draws this body (body.gx.json has only the material's own
    // registers): Prime 1's Power Trooper glow reads it, the trooper's yellow (as RetroTrophy.Reg1Colour bakes for the HD
    // suit). Only where the material leaves register 1 at 0; uReg[2] = GX register 1 (GxShader)
    static readonly Dictionary<string, float[]> _register1 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MP1PowerTrooper"] = new[] { 253 / 255f, 239 / 255f, 86 / 255f },
    };

    static void GameRegister1(string id, GxShader.Params p)
    {
        if (!_register1.TryGetValue(id, out float[]? rgb) || p.Reg[8] != 0 || p.Reg[9] != 0 || p.Reg[10] != 0) return;
        p.Reg[8] = rgb[0];
        p.Reg[9] = rgb[1];
        p.Reg[10] = rgb[2];
    }

    CampaignRetroBody(string id, string dir, NativeBody body, BodyClips clips)
    {
        Id = id;
        _dir = dir;
        Body = body;
        Clips = clips;
        List<GxMaterial> mats = Materials(dir, body.Data.Materials.Length);
        bool brawl = body.Game == "ssbb";
        int total = 0, at = 0;
        foreach (HdGun.Material m in body.Data.Materials) total += m.Vertices.Length;
        _verts = new float[total];
        for (int m = 0; m < body.Data.Materials.Length; m++)
        {
            HdGun.Material mat = body.Data.Materials[m];
            Array.Copy(mat.Vertices, 0, _verts, at, mat.Vertices.Length);
            GxMaterial gx = mats[m];
            var batch = new Batch
            {
                Material = m, Start = at / HdGun.FloatsPerVertex, Count = mat.VertexCount, Gx = gx, P = GxShader.Pack(gx),
                Pass = gx.Additive ? 2 : gx.BlendEnable ? 1 : 0,
                // a Retro body's blended parts cull as the disc says (owner 10-18: Dark Samus's shoulder glow shimmered:
                // unculled, unsorted, without depth, their far faces drew over the near ones in triangle order)
                Cull = brawl || gx.Additive || gx.BlendEnable ? gx.CullMode & 3 : 0, BlendDepth = brawl && gx.DepthWrite,
            };
            GameRegister1(id, batch.P);
            at += mat.Vertices.Length;
            string? src = GunPrograms.SuitFrag(batch.P);
            if (src != null)
            {
                batch.Spec = _specSrc.IndexOf(src);
                if (batch.Spec < 0)
                {
                    batch.Spec = _specSrc.Count;
                    _specSrc.Add(src);
                }
            }
            _batches.Add(batch);
            for (int u = 0; u < 8; u++)
            {
                string name = batch.P.Units[u]?.Name ?? "";
                if (!_texNames.Contains(name)) _texNames.Add(name);
            }
        }
        FlipV(_verts);
        AppendCannon(); // the first-person arm cannon's cut after the body (CampaignArmCannon.cs)
        foreach (string name in _texNames) _pixels[name] = ReadPng(name);
    }

    // each written material's GX program: body.gx.json's (Retro bodies), or a Brawl fighter's (Import/BrawlBody: body.cfg
    // "material <index> <name> [<file>]" = that MDL0 material out of body.brres, or the named .brres beside it)
    static List<GxMaterial> Materials(string dir, int count)
    {
        if (!File.Exists(Path.Combine(dir, "body.brres"))) return GxJson.Read(Path.Combine(dir, "body.gx.json"));
        var files = new Dictionary<string, List<GxMaterial>>(StringComparer.OrdinalIgnoreCase);
        var res = new GxMaterial[count];
        foreach (string line in File.ReadAllLines(Path.Combine(dir, "body.cfg")))
        {
            string[] t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 3 || t[0] != "material" || !int.TryParse(t[1], out int i) || i < 0 || i >= count) continue;
            string file = t.Length >= 4 ? t[3] : "body.brres";
            if (!files.TryGetValue(file, out List<GxMaterial>? mats)) files[file] = mats = Mdl0Gx.Read(Path.Combine(dir, file));
            res[i] = mats.Find(m => m.Name == t[2]) ?? throw new InvalidDataException($"{file} has no material {t[2]}");
        }
        for (int i = 0; i < count; i++) if (res[i] == null) throw new InvalidDataException($"body.cfg names no material {i}");
        return new List<GxMaterial>(res);
    }

    // body.bin keeps Retro's raw V but its PNGs are stored row-flipped (like the trophies'): sample (u, 1 - v) as the HD suits do
    static void FlipV(float[] verts)
    {
        for (int i = 7; i < verts.Length; i += HdGun.FloatsPerVertex) verts[i] = 1 - verts[i];
    }

    (int[] Px, int W, int H) ReadPng(string name)
    {
        string file = Path.Combine(_dir, name + ".png");
        try
        {
            if (name != "" && File.Exists(file) && AppPlatform.DecodeImage(file) is (int[] px, int w, int h)) return (px, w, h);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"body {Id}: texture {name} failed: {ex.Message}");
        }
        return (new[] { unchecked((int)0xFFFFFFFF) }, 1, 1);
    }

    // -------- the seats' clocks --------

    NativeBodyAnimator AnimatorOf(object who) => _anims.TryGetValue(who, out NativeBodyAnimator? a) ? a : _anims[who] = new NativeBodyAnimator(Body, Clips);

    // after each sim step: the player's clip clock, and its standing body's while a Scout Drone flies
    public void Tick(PlayerEntity p)
    {
        NativeBodyAnimator a = AnimatorOf(p);
        a.Step(p, p.Position, Dt);
        if (LogClips)
        {
            // dev (--es bodylog 1): each seat's clip changes, with its step count, to find poses in a replay
            string now = a.Playing + " | " + a.PlayingTorso;
            _steps[p] = _steps.TryGetValue(p, out int n) ? n + 1 : 1;
            if (!_logged.TryGetValue(p, out string? was) || was != now)
            {
                _logged[p] = now;
                Log.Info(Tag, $"body {Path.GetFileName(Body.Dir)}: seat {p.SlotIndex} step {_steps[p]}: {now}");
            }
        }
        if (p.Halfturret is HalfturretEntity t && p.HostScoutDrone && p.Flags2.TestFlag(PlayerFlags2.Halfturret)) AnimatorOf(t).StepStanding(Dt);
    }

    public static bool LogClips;

    // the Trooper's Scout Drone on its own body (Core DroneBody, bodies/MP1AlphaDrone | MP1GammaDrone): one clip clock per
    // seat, stepped after each sim step while that seat's drone flies
    readonly Dictionary<PlayerEntity, DroneBody.Animator> _drones = new();

    public void TickDrone(PlayerEntity p)
    {
        if (!_drones.TryGetValue(p, out DroneBody.Animator? d)) _drones[p] = d = new DroneBody.Animator(Body, Clips);
        string was = d.Playing;
        d.Step(p, Dt);
        if (LogClips && d.Playing != was) Log.Info(Tag, $"body {Id}: seat {p.SlotIndex} drone: {d.Playing}");
    }

    // the drone where MphRead flies it (PlayerScoutDrone's DroneDrawCenter / DroneDrawFacing), pitched toward the aim (the
    // laser's locked aim while it runs)
    public void DrawDrone(PlayerEntity p, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha, bool flash)
    {
        if (!_drones.TryGetValue(p, out DroneBody.Animator? d)) return;
        Draw(d.Clips, null, DroneBody.ModelMatrix(Body, p.DroneDrawCenter, p.DroneDrawFacing, p.DroneDrawAim), view, proj, pass, alpha, flash);
    }
    readonly Dictionary<object, string> _logged = new();
    readonly Dictionary<object, int> _steps = new();

    public string Playing(PlayerEntity p) => _anims.TryGetValue(p, out NativeBodyAnimator? a) ? a.Playing : "";

    // -------- GL --------

    // a few GL steps this frame, for about budgetMs (at least one); true once the body is ready to draw
    public bool Warm(double budgetMs)
    {
        if (_glReady) return true;
        if (Failed) return false;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            do Step();
            while (!_glReady && Stopwatch.GetElapsedTime(t0).TotalMilliseconds < budgetMs);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"body {Id}: GL setup failed -- the HD suit or MphRead's own model instead: {ex}");
            Failed = true;
        }
        return _glReady;
    }

    // a new GL context: everything made again (the pixels and vertices read again first)
    public void ResetGl()
    {
        _prog = _depthProg = _vao = _vbo = _cur = _step = 0;
        _specProg = Array.Empty<int>();
        _glReady = false;
        _u.Clear();
        _texIds.Clear();
        if (_verts == null)
        {
            int total = 0, at = 0;
            foreach (HdGun.Material m in Body.Data.Materials) total += m.Vertices.Length;
            _verts = new float[total];
            foreach (HdGun.Material m in Body.Data.Materials)
            {
                Array.Copy(m.Vertices, 0, _verts, at, m.Vertices.Length);
                at += m.Vertices.Length;
            }
            FlipV(_verts);
            AppendCannon(); // CampaignArmCannon.cs
            foreach (string name in _texNames) _pixels[name] = ReadPng(name);
        }
    }

    void Step()
    {
        int nSpec = _specSrc.Count;
        if (_step == 0)
        {
            _prog = CampaignGlCache.Program(GunPrograms.Vert, GunPrograms.SuitInterpFrag, "GX (body)");
            if (_prog == 0) throw new InvalidOperationException("the GX program did not link");
            _depthProg = CampaignGlCache.Program(GunPrograms.Vert, GunPrograms.DepthFrag, "GX depth (body)");
            _specProg = new int[nSpec];
        }
        else if (_step <= nSpec)
        {
            _specProg[_step - 1] = CampaignGlCache.Program(GunPrograms.Vert, _specSrc[_step - 1], $"GX material {_step} of {nSpec} (body)");
        }
        else if (_step == nSpec + 1)
        {
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _vao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _vbo = ids[0];
            GLES30.GlBindVertexArray(_vao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, _verts!.Length * 4, _verts, GLES30.GlStaticDraw);
            int stride = HdGun.FloatsPerVertex * 4;
            int[] size = { 3, 3, 2, 3, 4, 4 }, off = { 0, 3, 6, 8, 11, 15 };
            for (int a = 0; a < 6; a++)
            {
                GLES30.GlEnableVertexAttribArray(a);
                GLES30.GlVertexAttribPointer(a, size[a], GLES30.GlFloat, false, stride, off[a] * 4);
            }
            GLES30.GlBindVertexArray(0);
        }
        else if (_step - nSpec - 2 < _texNames.Count)
        {
            string name = _texNames[_step - nSpec - 2];
            (int[] px, int w, int h) = _pixels[name];
            _texIds[name] = Upload(px, w, h);
        }
        else
        {
            foreach (Batch b in _batches)
            {
                for (int u = 0; u < 8; u++) b.Tex[u] = _texIds[b.P.Units[u]?.Name ?? ""];
                int own = b.Spec >= 0 ? _specProg[b.Spec] : 0;
                b.Prog = own != 0 ? own : _prog;
            }
            _pixels.Clear();
            _verts = null;
            _fadeOk = GLES30.GlGetUniformLocation(_prog, "uFade") >= 0;
            _glReady = true;
            int own2 = 0;
            foreach (Batch b in _batches) if (b.Prog != _prog) own2++;
            Log.Info(Tag, $"body {Id}: GL ready ({own2} of {_batches.Count} materials on their own programs, {_texIds.Count} textures)");
        }
        _step++;
    }

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

    // -------- drawing --------

    // the player's body in its seat's pose where MphRead stands it; alpha / pass / flash as CampaignSuit.DrawBody (the
    // opaque parts move to the Translucent pass while it fades)
    public void DrawBody(PlayerEntity p, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha, bool flash)
    {
        if (!_anims.TryGetValue(p, out NativeBodyAnimator? a)) return;
        // on the wall: the wall clips' own frame (+Z into the wall), so his feet and blade plant on it
        Vector3 facing = a.WallFacing ?? new Vector3(p.Field70, 0, p.Field74);
        Draw(a, p, Body.ModelMatrix(p.Hunter, p.Position, facing, Clips.Lift), view, proj, pass, alpha, flash);
    }

    // the Trooper's standing body while his Scout Drone flies (HalfturretEntity's HostBodyPosition: where a player would be)
    public void DrawStanding(HalfturretEntity t, Hunter hunter, Matrix4 view, Matrix4 proj, SuitPass pass, bool flash)
    {
        if (!_anims.TryGetValue(t, out NativeBodyAnimator? a)) return;
        Draw(a, null, Body.ModelMatrix(hunter, t.HostBodyPosition, t.FacingVector, Clips.Lift), view, proj, pass, 1f, flash);
    }

    void Draw(NativeBodyAnimator a, PlayerEntity? who, Matrix4 model, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha, bool flash)
    {
        if (alpha <= 0f || Failed || !Warm(1000)) return;
        try
        {
            _interpOnly = flash || SeatTint.W > 0;
            _tint = flash ? DamageTint : SeatTint;
            bool rebuild = pass == SuitPass.DepthRebuild;
            int p = rebuild ? 0 : (int)pass;
            bool fade = alpha < 0.999f && _fadeOk;
            bool own = !(fade && p == 0);
            bool moved = fade && p == 1;
            if (!(own && Has(p)) && !(moved && Has(0))) return;
            if (!a.Pose(who)) return;
            Begin(view, proj, model);
            double s = Environment.TickCount64 / 1000.0;
            if (moved) DrawFaded(alpha, s);
            if (own && p == 0 && _depthProg != 0)
            {
                if (!rebuild) GLES30.GlColorMask(false, false, false, false);
                DrawDepth();
                if (!rebuild) GLES30.GlColorMask(true, true, true, true);
                if (!rebuild) DrawPass(0, 1f, s, lequal: true);
                else
                {
                    // pass 4: the alpha-tested parts' depth through their colour programs (the caller masks colour)
                    foreach (Batch b in _batches) if (b.Pass == 0 && !b.DepthFirst) Emit(b, 1f, s);
                }
            }
            else if (own) DrawPass(p, fade ? alpha : 1f, s);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"body {Id}: draw failed -- the HD suit or MphRead's own model from now on: {ex}");
            Failed = true;
        }
        finally
        {
            _interpOnly = false;
            _tint = Vector4.Zero;
            GLES30.GlDisable(0x0B44); // GL_CULL_FACE: as Begin left it
            GLES30.GlActiveTexture(GLES30.GlTexture0);
            GLES30.GlBindVertexArray(0);
        }
    }

    bool Has(int pass)
    {
        foreach (Batch b in _batches) if (b.Pass == pass) return true;
        return false;
    }

    void DrawPass(int pass, float fade, double seconds, bool lequal = false)
    {
        foreach (Batch b in _batches)
        {
            if (b.Pass != pass) continue;
            Emit(b, fade, seconds, lequal && b.DepthFirst);
        }
    }

    void DrawDepth()
    {
        foreach (Batch b in _batches)
        {
            if (!b.DepthFirst) continue;
            Use(_depthProg);
            Palette(_depthProg, b);
            GLES30.GlDisable(GLES30.GlBlend);
            GLES30.GlDepthMask(true);
            GLES30.GlDepthFunc(DepthFunc(b.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
    }

    // the opaque parts at alpha: depth only, then the colour where that depth is (CampaignSuit.DrawFaded)
    void DrawFaded(float alpha, double seconds)
    {
        if (_depthProg != 0)
        {
            GLES30.GlColorMask(false, false, false, false);
            DrawDepth();
            GLES30.GlColorMask(true, true, true, true);
        }
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlZero, GLES30.GlOne);
        GLES30.GlDepthMask(true);
        foreach (Batch b in _batches)
        {
            if (b.Pass != 0 || _depthProg != 0 && b.DepthFirst) continue;
            Material(b, 1f, seconds);
            GLES30.GlDepthFunc(DepthFunc(b.P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthMask(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        foreach (Batch b in _batches)
        {
            if (b.Pass != 0) continue;
            Material(b, alpha, seconds);
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
    }

    void Emit(Batch b, float fade, double seconds, bool lequal = false)
    {
        Material(b, fade, seconds);
        GxShader.Params P = b.P;
        if (P.Blend)
        {
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(SrcFactor(P.BlendSrc), DstFactor(P.BlendDst));
            GLES30.GlDepthMask(b.BlendDepth);
        }
        else
        {
            GLES30.GlDisable(GLES30.GlBlend);
            GLES30.GlDepthMask(true);
        }
        if (b.Cull != 0)
        {
            // GX's "inside" = the faces turned away (her export winds the outside counter-clockwise)
            GLES30.GlEnable(0x0B44); // GL_CULL_FACE
            GLES30.GlFrontFace(GLES30.GlCcw);
            GLES30.GlCullFace(b.Cull == 1 ? GLES30.GlFront : b.Cull == 2 ? GLES30.GlBack : 0x0408); // GL_FRONT_AND_BACK
        }
        else GLES30.GlDisable(0x0B44);
        int func = DepthFunc(P.DepthFunc);
        GLES30.GlDepthFunc(lequal && func == GLES30.GlLess ? GLES30.GlLequal : func);
        GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
    }

    int U(int prog, string n) => _u.TryGetValue((prog, n), out int l) ? l : _u[(prog, n)] = GLES30.GlGetUniformLocation(prog, n);

    static void Put(float[] dst, Matrix4 m)
    {
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }

    void Begin(Matrix4 view, Matrix4 proj, Matrix4 model)
    {
        Put(_mvp16, view * proj); // OpenTK's layout uploaded untransposed (row vectors), as CampaignGun
        _viewRot[0] = view.M11; _viewRot[1] = view.M12; _viewRot[2] = view.M13;
        _viewRot[3] = view.M21; _viewRot[4] = view.M22; _viewRot[5] = view.M23;
        _viewRot[6] = view.M31; _viewRot[7] = view.M32; _viewRot[8] = view.M33;
        Put(_m16, model);
        _cur = 0;
        for (int t = 0; t < 8; t++) GLES30.GlBindSampler(t, 0); // MPH's sampler objects would override the layers' wraps
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE: drawn unculled, as the trophies and suits
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
        for (int t = 0; t < 8; t++) GLES30.GlUniform1i(U(prog, "uTex" + t), t);
    }

    // this material's bones (its palette slots), in the pose Draw set
    void Palette(int prog, Batch b)
    {
        Body.FillPalette(b.Material, _pal);
        GlUniforms.UniformMatrix4fv(U(prog, "uBones"), Body.Data.Materials[b.Material].Palette.Length, false, _pal, 0);
    }

    void Material(Batch b, float fade, double seconds)
    {
        GxShader.Params P = b.P;
        int prog = _interpOnly ? _prog : b.Prog;
        Use(prog);
        Palette(prog, b);
        if (prog == _prog)
        {
            GLES30.GlUniform1i(U(prog, "uNumStages"), P.NumStages);
            GlUniforms.Uniform4iv(U(prog, "uCabcd"), 8, P.Cabcd, 0); GlUniforms.Uniform4iv(U(prog, "uCmod"), 8, P.Cmod, 0);
            GlUniforms.Uniform4iv(U(prog, "uAabcd"), 8, P.Aabcd, 0); GlUniforms.Uniform4iv(U(prog, "uAmod"), 8, P.Amod, 0);
            GlUniforms.Uniform4iv(U(prog, "uDst"), 8, P.Dst, 0); GlUniforms.Uniform4iv(U(prog, "uKsel"), 8, P.Ksel, 0);
            GlUniforms.Uniform1iv(U(prog, "uRasChan"), 8, P.RasChan, 0); GlUniforms.Uniform4iv(U(prog, "uSwap"), 4, P.Swap, 0);
            GlUniforms.Uniform4iv(U(prog, "uAlphaTest"), 1, P.AlphaTest, 0); GlUniforms.Uniform2fv(U(prog, "uAlphaRef"), 1, P.AlphaRef, 0);
            GLES30.GlUniform1i(U(prog, "uBlendAlpha"), P.Blend ? 1 : 0);
            GLES30.GlUniform1i(U(prog, "uNmap"), P.NmapUnit + 1); GLES30.GlUniform1i(U(prog, "uNmapTc"), P.NmapCoord);
            GLES30.GlUniform1i(U(prog, "uTintUnit"), -1);
        }
        GlUniforms.Uniform4fv(U(prog, "uReg"), 4, P.Reg, 0); GlUniforms.Uniform4fv(U(prog, "uKonst"), 4, P.Konst, 0);
        GlUniforms.Uniform4iv(U(prog, "uColCtrl"), 2, P.ColCtrl, 0); GlUniforms.Uniform4iv(U(prog, "uAlpCtrl"), 2, P.AlpCtrl, 0);
        GlUniforms.Uniform2iv(U(prog, "uAttn"), 2, P.Attn, 0);
        GlUniforms.Uniform4fv(U(prog, "uChanMat"), 2, P.ChanMat, 0); GlUniforms.Uniform4fv(U(prog, "uChanAmb"), 2, P.ChanAmb, 0);
        GlUniforms.Uniform1iv(U(prog, "uTgMode"), 8, P.TgMode, 0);
        GlUniforms.UniformMatrix3fv(U(prog, "uTexMtx"), 8, false, P.Scrolls ? GxShader.ScrolledTexMtx(b.Gx, P, seconds) : P.TexMtx, 0);
        GLES30.GlUniform1f(U(prog, "uFade"), fade);
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
}
