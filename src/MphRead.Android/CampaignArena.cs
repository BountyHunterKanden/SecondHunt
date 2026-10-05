using Android.Opengl;
using Android.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRecomp.Anim;
using MphRecomp.Arenas;
using MphRecomp.Assets;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// An imported Echoes arena drawn with its own look (docs/MP2_MULTIPLAYER_IMPORT.md phase 2): the mesh, Retro
// materials and textures ArenaHdWriter put in _archives/<archive>/hd/, through the GX-TEV shader the HD suit and the
// Prime guns use (plus a second UV set: lightmaps read TEX0, the surfaces TEX1). Opaque materials draw with the opaque
// items (passes 1 and 4 of the frame), blended ones after the translucent passes; the area's sky (if it has one) after
// the opaque world at the far plane, around the camera, in pass 1. Single-mip textures (the lightmap atlases) get no mipmaps. Once it's ready the DS stand-in room
// model is hidden (RoomEntity.Hidden: collision, room parts and everything else stay MphRead's). If the bundle is
// missing or GL fails, the stand-in simply stays.
internal sealed partial class CampaignRenderer
{
    ArenaHdDraw? _arenaHd;
    Task<ArenaHdDraw?>? _arenaHdLoad;
    int _arenaHdRoom = -1;
    // the arena's CPU time on the GL thread, its share of MPHPerf's "draw": one MPHArena line every 2 s
    readonly double[] _arenaMs = new double[3];
    double _arenaFrameMs, _arenaMaxMs;
    int _arenaFrames;
    long _arenaLogAt;

    // GL thread, from the frame's passes: pass 0 = the opaque materials, 1 = the blended ones
    void DrawArena(Scene scene, int pass, int restoreDepthFunc)
    {
        if (_host == null || scene != _host.Scene) return;
        if (_arenaHdRoom != _host.RoomId)
        {
            _arenaHdRoom = _host.RoomId;
            _arenaHd = null;
            _arenaHdLoad = null;
            RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
            if (meta != null && EchoesArena.IsEchoesRoom(meta.Name) && meta.Archive is string archive)
            {
                string dir = Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
                _arenaHdLoad = Task.Run(() => ArenaHdDraw.TryLoad(dir));
            }
        }
        if (_arenaHd == null)
        {
            if (_arenaHdLoad == null || !_arenaHdLoad.IsCompleted) return;
            _arenaHd = _arenaHdLoad.Result;
            _arenaHdLoad = null;
            if (_arenaHd == null) return;
        }
        ArenaHdDraw hd = _arenaHd;
        if (!hd.Warm(pass == 0 ? 3 : 0)) return;   // GL objects a few ms per frame; the stand-in draws meanwhile
        if (hd.Failed) return;
        if (scene.Room is RoomEntity room) room.Hidden = true;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // the sky only with the frame's first opaque pass (the one restoring LESS); the depth rebuild has colour masked
        bool first = pass == 0 && restoreDepthFunc == GLES30.GlLess;
        if (first) ArenaPerfFrame();
        hd.Draw(scene.ViewMatrix, scene.PerspectiveMatrix, pass, _clock.Elapsed.TotalSeconds, sky: first);
        ArenaGl.UseProgram(_program);
        ArenaGl.BindVertexArray(0);
        ArenaGl.ActiveTexture(GLES30.GlTexture0);
        ArenaGl.Enable(GLES30.GlBlend);
        ArenaGl.BlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        ArenaGl.Disable(CullFaceCap);
        ArenaGl.DepthMask(pass == 0);
        ArenaGl.DepthFunc(restoreDepthFunc);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _arenaMs[first ? 0 : pass == 0 ? 1 : 2] += ms;
        _arenaFrameMs += ms;
    }

    // the frame's first arena call: closes the last frame, logs every 2 s
    void ArenaPerfFrame()
    {
        if (_arenaFrames > 0) _arenaMaxMs = Math.Max(_arenaMaxMs, _arenaFrameMs);
        _arenaFrameMs = 0;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_arenaLogAt == 0) _arenaLogAt = now;
        if (_arenaFrames > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_arenaLogAt, now).TotalSeconds >= 2)
        {
            int n = _arenaFrames;
            Log.Info("MPHArena", $"arena CPU {(_arenaMs[0] + _arenaMs[1] + _arenaMs[2]) / n:0.00} ms/frame (opaque {_arenaMs[0] / n:0.00} + depth rebuild "
                + $"{_arenaMs[1] / n:0.00} + blended {_arenaMs[2] / n:0.00}), max {_arenaMaxMs:0.0} ms; {n} frames");
            Array.Clear(_arenaMs);
            _arenaMaxMs = 0;
            _arenaFrames = 0;
            _arenaLogAt = now;
        }
        _arenaFrames++;
    }

    // OnSurfaceCreated: a new context, every GL name is gone
    void ArenaGlReset() => _arenaHd?.ResetGl();
}

internal sealed class ArenaHdDraw
{
    const string Tag = "MPHArena";

    sealed class Batch
    {
        public int Start, Count, Pass;
        public GxMaterial Gx = null!;
        public GxShader.Params P = null!;
        public readonly string?[] Png = new string?[8];
        public readonly int[] Tex = new int[8];
        // the fast path (most opaque Echoes surfaces): lightmap x texture (+ an added glow texture); null = GX program
        public string? FastLm, FastTex, FastGlow;
        public int TexLm, TexD, TexG;
        public bool Fast => FastLm != null;
        // every other material: its own TEV program (ArenaGxSpec) once linked; null = the GX interpreter
        public string? SpecSrc;
        public Spec? Spec;
    }

    // a linked ArenaGxSpec program and its uniforms; per-frame ones re-sent when the frame or the sky/world MVP changes,
    // the material's when another batch (or a scrolling one) uses the program
    sealed class Spec
    {
        public int Prog, Mvp, ViewRot, SceneAmb, ColCtrl, AlpCtrl, Attn, ChanMat, ChanAmb, TgMode, TexMtx, Reg, Konst;
        public int[] Units = Array.Empty<int>();
        public long Stamp = -1;
        public bool Sky;
        public Batch? Last;
    }

    readonly string _dir;
    readonly List<Batch> _batches = new();
    float[] _verts = Array.Empty<float>();
    int _floats;
    Java.Nio.ByteBuffer? _vertBuf;
    readonly Dictionary<string, (Java.Nio.ByteBuffer Px, int W, int H)?> _pixels = new();
    List<string> _texOrder = new();
    readonly Dictionary<string, int> _texIds = new();
    readonly Dictionary<string, int> _u = new();
    readonly HashSet<string> _noMips = new();   // single-mip textures on the disc (the lightmap atlases): no mipmaps
    int _prog, _vao, _vbo, _white, _step;
    bool _glReady;
    double _warmMs;
    readonly float[] _m16 = new float[16], _viewRot = new float[9], _identity = new float[16], _skyMvp = new float[16];
    int _skyTriangles;
    int _fastProg, _fastMvp, _fastGlow, _depthProg, _depthMvp;
    bool _fastOk;
    readonly List<(int Start, int Count)> _fastRanges = new();
    readonly List<string> _specOrder = new();
    readonly Dictionary<string, Spec?> _specs = new();
    long _stamp;
    int _curProg;
    // GL state this Draw call has set (MPH's passes change it between calls); wrap modes stay with the texture objects
    long _progStamp = -1;
    bool _progSky;
    int _active, _blend, _blendSrc, _blendDst, _depthMask, _depthFunc;
    readonly int[] _unitTex = new int[8];
    readonly Dictionary<int, (int S, int T)> _wrap = new();
    readonly float[] _scroll = new float[72];

    public bool Failed { get; private set; }

    static readonly string[] TexUniform = { "uTex0", "uTex1", "uTex2", "uTex3", "uTex4", "uTex5", "uTex6", "uTex7" };
    static readonly float[] NoLights = new float[12];

    // GxShader's vertex program + a second UV set (texgen mode 2 = aUv1), made here so GxShader.cs stays as it is
    static readonly string Vert = PatchVert(GxShader.Vert);

    static string PatchVert(string v)
    {
        const string attr = "layout(location=5) in vec4 aWeights;\n";
        const string src = "vec3 src = uTgMode[i] == 0 ? vec3(aUv, 1.0) :";
        if (!v.Contains(attr) || !v.Contains(src)) throw new InvalidOperationException("GxShader.Vert changed: the arena's second UV set can't be patched in");
        return v.Replace(attr, attr + "layout(location=6) in vec2 aUv1;\n")
            .Replace(src, "vec3 src = uTgMode[i] == 2 ? vec3(aUv1, 1.0) : uTgMode[i] == 0 ? vec3(aUv, 1.0) :");
    }

    // The fast path: Retro's lightmapped TEV (stage 0 REG0 = RASC + C1 x lightmap with RASC 0 and C1 white, stage 1
    // REG0 x texture, [stage 2 + glow texture]) as a small program. No discard, so the GPU's early depth test works;
    // positions invariant so the depth-only pre-pass and the colour pass produce the same depths.
    const string FastVert = "#version 300 es\n" +
        "layout(location=0) in vec3 aPos; layout(location=2) in vec2 aUv; layout(location=6) in vec2 aUv1;\n" +
        "uniform mat4 uMvp; out vec2 vUv0; out vec2 vUv1;\n" +
        "invariant gl_Position;\n" +
        "void main(){ gl_Position = uMvp * vec4(aPos, 1.0); vUv0 = aUv; vUv1 = aUv1; }\n";
    const string FastFrag = "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv0; in vec2 vUv1; uniform sampler2D uLm, uTex, uGlowTex; uniform int uGlow; out vec4 o;\n" +
        "void main(){ vec3 c = clamp(texture(uLm, vUv0).rgb, 0.0, 1.0) * texture(uTex, vUv1).rgb;\n" +
        "  if (uGlow == 1) c += texture(uGlowTex, vUv1).rgb;\n" +
        "  o = vec4(clamp(c, 0.0, 1.0), 1.0); }\n";
    const string DepthVert = "#version 300 es\n" +
        "layout(location=0) in vec3 aPos; uniform mat4 uMvp;\n" +
        "invariant gl_Position;\n" +
        "void main(){ gl_Position = uMvp * vec4(aPos, 1.0); }\n";
    const string DepthFrag = "#version 300 es\n" +
        "precision mediump float; out vec4 o; void main(){ o = vec4(0.0); }\n";

    // which materials the fast path draws as GX would: opaque, lit (no light reaches it), no alpha test, the exact
    // lightmap TEV above, lightmap on TEX0 and textures on TEX1 with no animation
    static (string Lm, string Tex, string? Glow)? FastOf(JsonElement jm)
    {
        if (jm.GetProperty("pass").GetInt32() != 0 || jm.GetProperty("alphaTest").GetBoolean() || !jm.GetProperty("lit").GetBoolean()) return null;
        var st = jm.GetProperty("stages").EnumerateArray().ToArray();
        var layers = jm.GetProperty("layers").EnumerateArray().ToArray();
        if (st.Length is not (2 or 3)) return null;
        static int[] C(JsonElement s) => s.GetProperty("c").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        static bool Plain(JsonElement s) => s.GetProperty("cop").GetInt32() == 0 && s.GetProperty("cscale").GetInt32() == 0 && s.GetProperty("cbias").GetInt32() == 0;
        if (!C(st[0]).SequenceEqual(new[] { 15, 4, 8, 10 }) || st[0].GetProperty("cdest").GetInt32() != 1 || st[0].GetProperty("ras").GetInt32() != 0) return null;
        if (!C(st[1]).SequenceEqual(new[] { 15, 2, 8, 15 }) || st[1].GetProperty("cdest").GetInt32() != 0) return null;
        if (st.Length == 3 && (!C(st[2]).SequenceEqual(new[] { 15, 8, 12, 0 }) || st[2].GetProperty("cdest").GetInt32() != 0)) return null;
        if (!st.All(Plain)) return null;
        string? Layer(JsonElement s, int uv)
        {
            int t = s.GetProperty("texmap").GetInt32();
            if (t < 0 || t >= layers.Length) return null;
            JsonElement l = layers[t];
            bool still = !l.GetProperty("env").GetBoolean() && l.GetProperty("scroll").EnumerateArray().All(e => e.GetDouble() == 0);
            return still && l.GetProperty("uv").GetInt32() == uv ? l.GetProperty("name").GetString() : null;
        }
        string? lm = Layer(st[0], 0), tex = Layer(st[1], 1), glow = st.Length == 3 ? Layer(st[2], 1) : null;
        if (lm == null || tex == null || st.Length == 3 && glow == null) return null;
        return (lm, tex, glow);
    }

    ArenaHdDraw(string dir) { _dir = dir; }

    // worker thread: the mesh, the materials and every texture decoded; null when there's no bundle (or it's broken)
    public static ArenaHdDraw? TryLoad(string dir)
    {
        string mesh = Path.Combine(dir, ArenaHdWriter.Mesh), json = Path.Combine(dir, ArenaHdWriter.Materials);
        if (!File.Exists(mesh) || !File.Exists(json))
        {
            Log.Info(Tag, $"arena: no HD bundle in {dir} (the DS stand-in stays)");
            return null;
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var d = new ArenaHdDraw(dir);
            d.Read(mesh, json);
            d.DecodeAll();
            Log.Info(Tag, $"arena: {Path.GetFileName(Path.GetDirectoryName(dir))}: {d._verts.Length / d._floats / 3} triangles ({d._skyTriangles} sky), "
                + $"{d._batches.Count} batches, {d._texOrder.Count} textures read in {sw.ElapsedMilliseconds} ms");
            return d;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"arena: HD bundle in {dir} unreadable (the DS stand-in stays): {ex}");
            return null;
        }
    }

    void Read(string mesh, string json)
    {
        List<GxMaterial> mats = GxJson.Read(json);
        // which texcoords read the second UV set, per material
        var uv1 = new List<List<int>>();
        var fast = new List<(string Lm, string Tex, string? Glow)?>();
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json)))
        {
            foreach (JsonElement jm in doc.RootElement.GetProperty("materials").EnumerateArray())
            {
                fast.Add(FastOf(jm));
                var list = new List<int>();
                foreach (JsonElement jl in jm.GetProperty("layers").EnumerateArray())
                {
                    if (jl.TryGetProperty("uv", out JsonElement u) && u.GetInt32() == 1) list.Add(jl.GetProperty("texcoord").GetInt32());
                }
                uv1.Add(list);
            }
            if (doc.RootElement.TryGetProperty("mips", out JsonElement mips))
            {
                foreach (JsonProperty t in mips.EnumerateObject()) if (t.Value.GetInt32() <= 1) _noMips.Add(Path.Combine(_dir, t.Name + ".png"));
            }
        }
        using var r = new BinaryReader(File.OpenRead(mesh));
        string magic = System.Text.Encoding.ASCII.GetString(r.ReadBytes(8));
        if (magic != "MPHARN1\0") throw new InvalidDataException($"not an arena mesh ({magic})");
        _floats = r.ReadInt32();
        int count = r.ReadInt32(), batches = r.ReadInt32();
        var seen = new HashSet<string>();
        for (int i = 0; i < batches; i++)
        {
            int mat = r.ReadInt32(), start = r.ReadInt32(), n = r.ReadInt32(), pass = r.ReadInt32();
            GxMaterial m = mats[mat];
            // Retro's TEV register 1 scales the lightmap (white: the lightmap as baked); no scene light reaches the
            // lit channels, so the lightmaps light the world as Echoes' own areas look
            m.Reg[2] = System.Numerics.Vector4.One;
            GxShader.Params p = GxShader.Pack(m);
            foreach (int tc in uv1[mat]) if (tc >= 0 && tc < 8 && p.TgMode[tc] == 0) p.TgMode[tc] = 2;
            var b = new Batch { Start = start, Count = n, Pass = pass, Gx = m, P = p };
            if (pass == ArenaHdWriter.SkyPass) _skyTriangles += n / 3;
            if (fast[mat] is { } f)
            {
                b.FastLm = Path.Combine(_dir, f.Lm + ".png");
                b.FastTex = Path.Combine(_dir, f.Tex + ".png");
                b.FastGlow = f.Glow is string g ? Path.Combine(_dir, g + ".png") : null;
            }
            else if (ArenaGxSpec.Frag(p) is string spec)
            {
                b.SpecSrc = spec;
                if (!_specOrder.Contains(spec)) _specOrder.Add(spec);
            }
            for (int u = 0; u < 8; u++)
            {
                if (p.Units[u]?.Name is string name)
                {
                    b.Png[u] = Path.Combine(_dir, name + ".png");
                    if (seen.Add(b.Png[u]!)) _texOrder.Add(b.Png[u]!);
                }
            }
            _batches.Add(b);
        }
        // the fast batches' vertex ranges, neighbours merged, for the depth-only passes
        foreach (Batch b in _batches)
        {
            if (!b.Fast) continue;
            if (_fastRanges.Count > 0 && _fastRanges[^1].Start + _fastRanges[^1].Count == b.Start) _fastRanges[^1] = (_fastRanges[^1].Start, _fastRanges[^1].Count + b.Count);
            else _fastRanges.Add((b.Start, b.Count));
        }
        _verts = new float[count * _floats];
        byte[] raw = r.ReadBytes(_verts.Length * 4);
        Buffer.BlockCopy(raw, 0, _verts, 0, raw.Length);
        _vertBuf = VertBuffer(_verts);
    }

    void DecodeAll()
    {
        var read = new (Java.Nio.ByteBuffer Px, int W, int H)?[_texOrder.Count];
        Parallel.For(0, _texOrder.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => read[i] = Decode(_texOrder[i]));
        for (int i = 0; i < _texOrder.Count; i++) _pixels[_texOrder[i]] = read[i];
    }

    static (Java.Nio.ByteBuffer Px, int W, int H)? Decode(string png)
    {
        if (!File.Exists(png)) return null;
        try
        {
            Android.Graphics.Bitmap? bmp = Android.Graphics.BitmapFactory.DecodeFile(png);
            if (bmp == null) return null;
            int w = bmp.Width, h = bmp.Height;
            var px = new int[w * h];
            bmp.GetPixels(px, 0, w, 0, 0, w, h);
            bmp.Recycle();
            for (int i = 0; i < px.Length; i++)
            {
                int a = px[i];
                px[i] = (int)((a & 0xFF00FF00u) | ((uint)(a >> 16) & 0xFF) | ((uint)(a & 0xFF) << 16)); // ARGB -> ABGR
            }
            return (IntBuffer(px), w, h);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"arena: texture {Path.GetFileName(png)} failed: {ex.Message}");
            return null;
        }
    }

    // a few GL steps for about budgetMs (at least one when budgetMs > 0); true once ready (or failed)
    public bool Warm(double budgetMs)
    {
        if (_glReady || Failed) return true;
        if (budgetMs <= 0) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            do Step();
            while (!_glReady && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds < budgetMs);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"arena: GL setup failed (the DS stand-in stays): {ex}");
            Failed = true;
        }
        _warmMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return _glReady || Failed;
    }

    // 0 the programs, 1 the vertex buffer, then one texture per step, one TEV program per step, then the batches' tables
    void Step()
    {
        if (_step == 0)
        {
            _prog = CampaignGlCache.Program(Vert, GxShader.Frag, "GX (arena)");
            if (_prog == 0) throw new InvalidOperationException("the arena's GX program did not link");
            // the uniforms no arena draw changes (uniforms are the program's own state; nothing else uses this program)
            GLES30.GlUseProgram(_curProg = _prog);
            if (_identity[0] == 0) { _identity[0] = _identity[5] = _identity[10] = _identity[15] = 1; }
            GLES30.GlUniform1i(U("uSkin"), 0);
            GLES30.GlUniformMatrix4fv(U("uModel"), 1, false, _identity, 0);
            GLES30.GlUniform1i(U("uNumLights"), 0);
            GLES30.GlUniform3fv(U("uLightDir"), 4, NoLights, 0);
            GLES30.GlUniform3fv(U("uLightCol"), 4, NoLights, 0);
            GLES30.GlUniform3fv(U("uLightSpecCol"), 4, NoLights, 0);
            GLES30.GlUniform3fv(U("uLightSpecK"), 4, NoLights, 0);
            GLES30.GlUniform1f(U("uExposure"), 1f);
            GLES30.GlUniform1i(U("uRawEnvNormal"), 0);
            GLES30.GlUniform4f(U("uTint"), 0, 0, 0, 0);
            for (int t = 0; t < 8; t++) GLES30.GlUniform1i(U(TexUniform[t]), t);
            _fastProg = CampaignGlCache.Program(FastVert, FastFrag, "arena lightmap");
            _depthProg = CampaignGlCache.Program(DepthVert, DepthFrag, "arena depth");
            _fastOk = _fastProg != 0 && _depthProg != 0;
            if (!_fastOk) Log.Warn(Tag, "arena: the fast programs did not link; every surface goes through the GX program");
            else
            {
                _fastMvp = GLES30.GlGetUniformLocation(_fastProg, "uMvp");
                _fastGlow = GLES30.GlGetUniformLocation(_fastProg, "uGlow");
                GLES30.GlUseProgram(_fastProg);
                GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fastProg, "uLm"), 0);
                GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fastProg, "uTex"), 1);
                GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fastProg, "uGlowTex"), 2);
                _depthMvp = GLES30.GlGetUniformLocation(_depthProg, "uMvp");
            }
        }
        else if (_step == 1)
        {
            if (_vertBuf == null) _vertBuf = VertBuffer(_verts);
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _vao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _vbo = ids[0];
            GLES30.GlBindVertexArray(_vao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
            _vertBuf.Position(0);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, _verts.Length * 4, _vertBuf, GLES30.GlStaticDraw);
            // pos, normal, uv0 (aUv), colour (aColor), uv1 (aUv1); no bones (uSkin 0)
            (int Loc, int Size, int Off)[] attrs = { (0, 3, 0), (1, 3, 3), (2, 2, 6), (6, 2, 8), (3, 3, 10) };
            foreach ((int loc, int size, int off) in attrs)
            {
                GLES30.GlEnableVertexAttribArray(loc);
                GLES30.GlVertexAttribPointer(loc, size, GLES30.GlFloat, false, _floats * 4, off * 4);
            }
            GLES30.GlBindVertexArray(0);
        }
        else if (_step - 2 < _texOrder.Count)
        {
            string png = _texOrder[_step - 2];
            (Java.Nio.ByteBuffer Px, int W, int H)? img = _pixels.TryGetValue(png, out var pre) ? pre : Decode(png);
            _texIds[png] = img.HasValue ? Upload(img.Value.Px, img.Value.W, img.Value.H, !_noMips.Contains(png)) : White();
        }
        else if (_step - 2 - _texOrder.Count < _specOrder.Count)
        {
            int i = _step - 2 - _texOrder.Count;
            _specs[_specOrder[i]] = LinkSpec(_specOrder[i], i);
        }
        else
        {
            foreach (Batch b in _batches)
            {
                for (int u = 0; u < 8; u++) b.Tex[u] = b.Png[u] is string png && _texIds.TryGetValue(png, out int t) ? t : White();
                int Id(string? png) => png != null && _texIds.TryGetValue(png, out int t) ? t : White();
                if (b.Fast) { b.TexLm = Id(b.FastLm); b.TexD = Id(b.FastTex); b.TexG = b.FastGlow != null ? Id(b.FastGlow) : White(); }
                b.Spec = b.SpecSrc != null && _specs.TryGetValue(b.SpecSrc, out Spec? s) ? s : null;
            }
            foreach (var px in _pixels.Values) px?.Px.Dispose();
            _pixels.Clear();
            _vertBuf?.Dispose();
            _vertBuf = null;
            _glReady = true;
            Log.Info(Tag, $"arena: GL ready (program {_prog}, {_texIds.Count} textures, {_warmMs:0} ms of GL work; "
                + $"fast path {(_fastOk ? _batches.Count(b => b.Fast) : 0)} of {_batches.Count(b => b.Pass == 0)} opaque batches; "
                + $"own TEV programs {_specs.Values.Count(s => s != null)} of {_specOrder.Count} for {_batches.Count(b => b.Spec != null)} of "
                + $"{_batches.Count(b => !(b.Fast && _fastOk))} other batches)");
        }
        _step++;
    }

    int U(string n) => _u.TryGetValue(n, out int l) ? l : _u[n] = GLES30.GlGetUniformLocation(_prog, n);

    // Every per-frame GL call goes straight to the driver (ArenaGl) and only when the state differs from what this call
    // already set: through GLES30 (JNI, arrays copied into new Java arrays) Sidehopper took ~1,000 calls a frame.
    public void Draw(Matrix4 view, Matrix4 proj, int pass, double seconds, bool sky)
    {
        _stamp++;
        HunterRig.ToArray(view * proj, _m16);
        _viewRot[0] = view.M11; _viewRot[1] = view.M12; _viewRot[2] = view.M13;
        _viewRot[3] = view.M21; _viewRot[4] = view.M22; _viewRot[5] = view.M23;
        _viewRot[6] = view.M31; _viewRot[7] = view.M32; _viewRot[8] = view.M33;
        // what MPH's passes may have changed since the last call
        _curProg = _active = _blend = _blendSrc = _blendDst = _depthMask = _depthFunc = -1;
        Array.Fill(_unitTex, -1);
        for (int t = 0; t < 8; t++) ArenaGl.BindSampler(t, 0); // MPH's sampler objects would override the layers' wrap modes
        ArenaGl.Disable(0x0B44); // GL_CULL_FACE: both faces, as the suit (Retro's winding isn't checked against GL's yet)
        ArenaGl.BindVertexArray(_vao);
        bool fast = pass == 0 && _fastOk;
        if (fast)
        {
            // depth only first (the colour pass's pre-pass, or the frame's depth rebuild), then the colour pass shades
            // each pixel once
            Use(_depthProg);
            ArenaGl.UniformMatrix4(_depthMvp, 1, _m16);
            Blend(null);
            DepthMask(true);
            DepthFunc(GLES30.GlLequal);
            if (sky) ArenaGl.ColorMask(false, false, false, false);
            foreach ((int start, int count) in _fastRanges) ArenaGl.DrawArrays(GLES30.GlTriangles, start, count);
            if (sky)
            {
                ArenaGl.ColorMask(true, true, true, true);
                Use(_fastProg);
                ArenaGl.UniformMatrix4(_fastMvp, 1, _m16);
                int glowOn = -1;
                foreach (Batch b in _batches)
                {
                    if (!b.Fast) continue;
                    Tex(0, b.TexLm);
                    Tex(1, b.TexD);
                    int on = b.FastGlow != null ? 1 : 0;
                    if (on == 1) Tex(2, b.TexG);
                    if (on != glowOn) ArenaGl.Uniform1i(_fastGlow, glowOn = on);
                    ArenaGl.DrawArrays(GLES30.GlTriangles, b.Start, b.Count);
                }
            }
        }
        foreach (Batch b in _batches)
        {
            if (pass == 0 ? b.Pass != 0 || fast && b.Fast : b.Pass is not (1 or 2)) continue;
            Bind(b, seconds, sky: false);
            Blend(b.P);
            DepthMask(!b.P.Blend);
            DepthFunc(GLES30.GlLequal);
            ArenaGl.DrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
        if (sky && _skyTriangles > 0) DrawSky(view, proj, seconds);
    }

    // Retro's sky: the area's sky model around the camera (translation only), lights off and the ambient white (its lit
    // materials show their texture as is). Drawn after the arena's opaque world at the far plane (depth range 1..1, LEQUAL,
    // no depth write), so only the pixels the world leaves open are shaded: drawn first under the world it shaded the
    // whole screen in several layers (Sidehopper's 11-texture sky: GPU 7 ms vs Pipeline's 2.9 with none, Odin 18:29).
    // Back faces culled, as Retro does: unculled, the far side of Sidehopper's planet and cloud spheres drew over their
    // near side ("really mangled", owner 18:03). All 1820 sky triangles wind counter-clockwise toward their normals.
    void DrawSky(Matrix4 view, Matrix4 proj, double seconds)
    {
        // the camera's position goes into the MVP: GxShader.Vert applies uModel to skinned vertices only
        Vector3 eye = view.Inverted().ExtractTranslation();
        HunterRig.ToArray(Matrix4.CreateTranslation(eye) * view * proj, _skyMvp);
        DepthFunc(GLES30.GlLequal);
        ArenaGl.DepthRangef(1, 1);
        DepthMask(false);
        ArenaGl.Enable(0x0B44); // GL_CULL_FACE
        ArenaGl.CullFace(0x0405); // GL_BACK
        ArenaGl.FrontFace(0x0901); // GL_CCW
        foreach (Batch b in _batches)
        {
            if (b.Pass != ArenaHdWriter.SkyPass) continue;
            Bind(b, seconds, sky: true);
            Blend(b.P);
            ArenaGl.DrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
        ArenaGl.DepthRangef(0, 1);
        ArenaGl.Disable(0x0B44);
    }

    void Use(int prog)
    {
        if (_curProg != prog) ArenaGl.UseProgram(_curProg = prog);
    }

    // a texture on a unit, unless it's already there
    void Tex(int unit, int tex)
    {
        if (_unitTex[unit] == tex) return;
        Active(unit);
        ArenaGl.BindTexture(GLES30.GlTexture2d, _unitTex[unit] = tex);
    }

    void Active(int unit)
    {
        if (_active != unit) ArenaGl.ActiveTexture(GLES30.GlTexture0 + (_active = unit));
    }

    // a layer's wrap modes on the texture just put on this unit (texture state: set again only when they change)
    void TexWrap(int unit, GxLayer L)
    {
        int tex = _unitTex[unit];
        var w = (Wrap(L.WrapS), Wrap(L.WrapT));
        if (_wrap.TryGetValue(tex, out var cur) && cur == w) return;
        Active(unit);
        ArenaGl.TexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, w.Item1);
        ArenaGl.TexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, w.Item2);
        _wrap[tex] = w;
    }

    // the material's blend (null: blending off)
    void Blend(GxShader.Params? P)
    {
        int on = P?.Blend == true ? 1 : 0;
        if (on != _blend)
        {
            if (on == 1) ArenaGl.Enable(GLES30.GlBlend);
            else ArenaGl.Disable(GLES30.GlBlend);
            _blend = on;
        }
        if (on == 0) return;
        int src = SrcFactor(P!.BlendSrc), dst = DstFactor(P.BlendDst);
        if (src != _blendSrc || dst != _blendDst) ArenaGl.BlendFunc(_blendSrc = src, _blendDst = dst);
    }

    void DepthMask(bool on)
    {
        if ((on ? 1 : 0) != _depthMask) ArenaGl.DepthMask(on);
        _depthMask = on ? 1 : 0;
    }

    void DepthFunc(int func)
    {
        if (func != _depthFunc) ArenaGl.DepthFunc(_depthFunc = func);
    }

    // the batch's program and material: its own TEV program when it has one (per-frame uniforms on its first draw of
    // the Draw call, or when it goes from world to sky; the material's when the program last drew another batch), else
    // the GX interpreter
    void Bind(Batch b, double seconds, bool sky)
    {
        GxShader.Params P = b.P;
        if (b.Spec is not Spec s)
        {
            Use(_prog);
            if (_progStamp != _stamp || _progSky != sky)
            {
                ArenaGl.UniformMatrix4(U("uMvp"), 1, sky ? _skyMvp : _m16);
                ArenaGl.UniformMatrix3(U("uViewRot"), 1, _viewRot);
                float amb = sky ? 1 : 0;
                ArenaGl.Uniform3f(U("uSceneAmb"), amb, amb, amb);
                _progStamp = _stamp;
                _progSky = sky;
            }
            Material(b, seconds);
            return;
        }
        Use(s.Prog);
        if (s.Stamp != _stamp || s.Sky != sky)
        {
            ArenaGl.UniformMatrix4(s.Mvp, 1, sky ? _skyMvp : _m16);
            ArenaGl.UniformMatrix3(s.ViewRot, 1, _viewRot);
            float amb = sky ? 1 : 0;
            ArenaGl.Uniform3f(s.SceneAmb, amb, amb, amb);
            s.Stamp = _stamp;
            s.Sky = sky;
        }
        if (s.Last != b || P.Scrolls)
        {
            ArenaGl.Uniform4iv(s.ColCtrl, 2, P.ColCtrl); ArenaGl.Uniform4iv(s.AlpCtrl, 2, P.AlpCtrl);
            ArenaGl.Uniform2iv(s.Attn, 2, P.Attn);
            ArenaGl.Uniform4fv(s.ChanMat, 2, P.ChanMat); ArenaGl.Uniform4fv(s.ChanAmb, 2, P.ChanAmb);
            ArenaGl.Uniform1iv(s.TgMode, 8, P.TgMode);
            ArenaGl.UniformMatrix3(s.TexMtx, 8, TexMtx(b, seconds));
            ArenaGl.Uniform4fv(s.Reg, 4, P.Reg); ArenaGl.Uniform4fv(s.Konst, 4, P.Konst);
            s.Last = b;
        }
        foreach (int u in s.Units)
        {
            Tex(u, b.Tex[u]);
            if (P.Units[u] is GxLayer L) TexWrap(u, L);
        }
    }

    // GxShader.ScrolledTexMtx into a reused buffer (no garbage per draw)
    float[] TexMtx(Batch b, double seconds)
    {
        GxShader.Params P = b.P;
        if (!P.Scrolls) return P.TexMtx;
        Array.Copy(P.TexMtx, _scroll, 72);
        for (int i = 0; i < 8; i++)
        {
            var L = b.Gx.Layers[i];
            if (L == null || L.ScrollPerSec == System.Numerics.Vector2.Zero) continue;
            _scroll[i * 9 + 6] += (float)(L.ScrollPerSec.X * seconds % 1.0);
            _scroll[i * 9 + 7] += (float)(L.ScrollPerSec.Y * seconds % 1.0);
        }
        return _scroll;
    }

    // one ArenaGxSpec program (linked, or its saved binary): samplers on their own units and the uniforms no arena draw
    // changes; null keeps its materials on the GX interpreter
    Spec? LinkSpec(string frag, int i)
    {
        int prog = CampaignGlCache.Program(Vert, frag, $"arena TEV {i}");
        if (prog == 0)
        {
            Log.Warn(Tag, $"arena: TEV program {i} did not link; its materials stay on the GX program");
            return null;
        }
        int L(string n) => GLES30.GlGetUniformLocation(prog, n);
        var s = new Spec
        {
            Prog = prog, Mvp = L("uMvp"), ViewRot = L("uViewRot"), SceneAmb = L("uSceneAmb"), ColCtrl = L("uColCtrl"), AlpCtrl = L("uAlpCtrl"),
            Attn = L("uAttn"), ChanMat = L("uChanMat"), ChanAmb = L("uChanAmb"), TgMode = L("uTgMode"), TexMtx = L("uTexMtx"),
            Reg = L("uReg"), Konst = L("uKonst"),
        };
        GLES30.GlUseProgram(_curProg = prog);
        var units = new List<int>();
        for (int u = 0; u < 8; u++)
        {
            int loc = L(TexUniform[u]);
            if (loc < 0) continue;
            GLES30.GlUniform1i(loc, u);
            units.Add(u);
        }
        s.Units = units.ToArray();
        if (_identity[0] == 0) { _identity[0] = _identity[5] = _identity[10] = _identity[15] = 1; }
        GLES30.GlUniform1i(L("uSkin"), 0);
        GLES30.GlUniformMatrix4fv(L("uModel"), 1, false, _identity, 0);
        GLES30.GlUniform1i(L("uNumLights"), 0);
        GLES30.GlUniform3fv(L("uLightDir"), 4, NoLights, 0);
        GLES30.GlUniform3fv(L("uLightCol"), 4, NoLights, 0);
        GLES30.GlUniform3fv(L("uLightSpecCol"), 4, NoLights, 0);
        GLES30.GlUniform3fv(L("uLightSpecK"), 4, NoLights, 0);
        GLES30.GlUniform1f(L("uExposure"), 1f);
        GLES30.GlUniform1i(L("uRawEnvNormal"), 0);
        return s;
    }

    void Material(Batch b, double seconds)
    {
        GxShader.Params P = b.P;
        ArenaGl.Uniform1i(U("uNumStages"), P.NumStages);
        ArenaGl.Uniform4iv(U("uCabcd"), 8, P.Cabcd); ArenaGl.Uniform4iv(U("uCmod"), 8, P.Cmod);
        ArenaGl.Uniform4iv(U("uAabcd"), 8, P.Aabcd); ArenaGl.Uniform4iv(U("uAmod"), 8, P.Amod);
        ArenaGl.Uniform4iv(U("uDst"), 8, P.Dst); ArenaGl.Uniform4iv(U("uKsel"), 8, P.Ksel);
        ArenaGl.Uniform1iv(U("uRasChan"), 8, P.RasChan); ArenaGl.Uniform4iv(U("uSwap"), 4, P.Swap);
        ArenaGl.Uniform4fv(U("uReg"), 4, P.Reg); ArenaGl.Uniform4fv(U("uKonst"), 4, P.Konst);
        ArenaGl.Uniform4iv(U("uColCtrl"), 2, P.ColCtrl); ArenaGl.Uniform4iv(U("uAlpCtrl"), 2, P.AlpCtrl);
        ArenaGl.Uniform2iv(U("uAttn"), 2, P.Attn);
        ArenaGl.Uniform4fv(U("uChanMat"), 2, P.ChanMat); ArenaGl.Uniform4fv(U("uChanAmb"), 2, P.ChanAmb);
        ArenaGl.Uniform1iv(U("uTgMode"), 8, P.TgMode);
        ArenaGl.UniformMatrix3(U("uTexMtx"), 8, TexMtx(b, seconds));
        ArenaGl.Uniform4iv(U("uAlphaTest"), 1, P.AlphaTest); ArenaGl.Uniform2fv(U("uAlphaRef"), 1, P.AlphaRef);
        ArenaGl.Uniform1i(U("uBlendAlpha"), P.Blend ? 1 : 0);
        ArenaGl.Uniform1i(U("uNmap"), P.NmapUnit + 1); ArenaGl.Uniform1i(U("uNmapTc"), P.NmapCoord);
        for (int u = 0; u < 8; u++)
        {
            Tex(u, b.Tex[u]);
            if (P.Units[u] is GxLayer L) TexWrap(u, L);
        }
    }

    static int Wrap(int w) => w == 0 ? GLES30.GlClampToEdge : w == 2 ? GLES30.GlMirroredRepeat : GLES30.GlRepeat;
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

    static Java.Nio.ByteBuffer VertBuffer(float[] verts)
    {
        var bb = Java.Nio.ByteBuffer.AllocateDirect(verts.Length * 4)!;
        bb.Order(Java.Nio.ByteOrder.NativeOrder()!);
        bb.AsFloatBuffer()!.Put(verts);
        return bb;
    }

    static Java.Nio.ByteBuffer IntBuffer(int[] px)
    {
        var bb = Java.Nio.ByteBuffer.AllocateDirect(px.Length * 4)!;
        bb.Order(Java.Nio.ByteOrder.NativeOrder()!);
        bb.AsIntBuffer()!.Put(px);
        return bb;
    }

    int White() => _white != 0 ? _white : _white = Upload(IntBuffer(new[] { unchecked((int)0xFFFFFFFF) }), 1, 1, false);

    static int Upload(Java.Nio.ByteBuffer px, int w, int h, bool mipmap)
    {
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
        px.Position(0);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
        if (mipmap) GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, mipmap ? GLES30.GlLinearMipmapLinear : GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        return ids[0];
    }

    // a new EGL context: every GL name is gone; the pixels are read again from the PNGs (Step falls back to Decode)
    public void ResetGl()
    {
        _prog = _vao = _vbo = _white = 0;
        _fastProg = _depthProg = _curProg = 0;
        _fastOk = false;
        _specs.Clear();
        foreach (Batch b in _batches) b.Spec = null;
        _step = 0;
        _glReady = false;
        Failed = false;
        _u.Clear();
        _texIds.Clear();
        _wrap.Clear();
        _progStamp = -1;
    }
}

// Direct calls into the GLES 3 driver for the arena's per-frame draws, as GlNative does for MPH's items: Android.Opengl.
// GLES30 goes through JNI, and every array argument is copied into a new Java array and back. These go to libGLESv3.so on
// the GL thread's current context with the arrays pinned. Setup keeps using GLES30.
internal static unsafe class ArenaGl
{
    const string Lib = "libGLESv3.so";

    [DllImport(Lib, EntryPoint = "glUseProgram")] public static extern void UseProgram(int program);
    [DllImport(Lib, EntryPoint = "glUniform1i")] public static extern void Uniform1i(int location, int v0);
    [DllImport(Lib, EntryPoint = "glUniform3f")] public static extern void Uniform3f(int location, float v0, float v1, float v2);
    [DllImport(Lib, EntryPoint = "glUniform1iv")] static extern void Uniform1iv(int location, int count, int* value);
    [DllImport(Lib, EntryPoint = "glUniform2iv")] static extern void Uniform2iv(int location, int count, int* value);
    [DllImport(Lib, EntryPoint = "glUniform4iv")] static extern void Uniform4iv(int location, int count, int* value);
    [DllImport(Lib, EntryPoint = "glUniform2fv")] static extern void Uniform2fv(int location, int count, float* value);
    [DllImport(Lib, EntryPoint = "glUniform4fv")] static extern void Uniform4fv(int location, int count, float* value);
    [DllImport(Lib, EntryPoint = "glUniformMatrix3fv")] static extern void UniformMatrix3fv(int location, int count, byte transpose, float* value);
    [DllImport(Lib, EntryPoint = "glUniformMatrix4fv")] static extern void UniformMatrix4fv(int location, int count, byte transpose, float* value);
    [DllImport(Lib, EntryPoint = "glBindVertexArray")] public static extern void BindVertexArray(int array);
    [DllImport(Lib, EntryPoint = "glDrawArrays")] public static extern void DrawArrays(int mode, int first, int count);
    [DllImport(Lib, EntryPoint = "glActiveTexture")] public static extern void ActiveTexture(int texture);
    [DllImport(Lib, EntryPoint = "glBindTexture")] public static extern void BindTexture(int target, int texture);
    [DllImport(Lib, EntryPoint = "glTexParameteri")] public static extern void TexParameteri(int target, int pname, int param);
    [DllImport(Lib, EntryPoint = "glBindSampler")] public static extern void BindSampler(int unit, int sampler);
    [DllImport(Lib, EntryPoint = "glEnable")] public static extern void Enable(int cap);
    [DllImport(Lib, EntryPoint = "glDisable")] public static extern void Disable(int cap);
    [DllImport(Lib, EntryPoint = "glBlendFunc")] public static extern void BlendFunc(int sfactor, int dfactor);
    [DllImport(Lib, EntryPoint = "glDepthFunc")] public static extern void DepthFunc(int func);
    [DllImport(Lib, EntryPoint = "glDepthMask")] static extern void DepthMask(byte flag);
    [DllImport(Lib, EntryPoint = "glColorMask")] static extern void ColorMask(byte r, byte g, byte b, byte a);
    [DllImport(Lib, EntryPoint = "glDepthRangef")] public static extern void DepthRangef(float n, float f);
    [DllImport(Lib, EntryPoint = "glCullFace")] public static extern void CullFace(int mode);
    [DllImport(Lib, EntryPoint = "glFrontFace")] public static extern void FrontFace(int mode);

    public static void DepthMask(bool flag) => DepthMask((byte)(flag ? 1 : 0));
    public static void ColorMask(bool r, bool g, bool b, bool a) => ColorMask((byte)(r ? 1 : 0), (byte)(g ? 1 : 0), (byte)(b ? 1 : 0), (byte)(a ? 1 : 0));
    public static void Uniform1iv(int location, int count, int[] v) { fixed (int* p = v) Uniform1iv(location, count, p); }
    public static void Uniform2iv(int location, int count, int[] v) { fixed (int* p = v) Uniform2iv(location, count, p); }
    public static void Uniform4iv(int location, int count, int[] v) { fixed (int* p = v) Uniform4iv(location, count, p); }
    public static void Uniform2fv(int location, int count, float[] v) { fixed (float* p = v) Uniform2fv(location, count, p); }
    public static void Uniform4fv(int location, int count, float[] v) { fixed (float* p = v) Uniform4fv(location, count, p); }
    // count matrices from the start of v (untransposed, column-major)
    public static void UniformMatrix3(int location, int count, float[] v) { fixed (float* p = v) UniformMatrix3fv(location, count, 0, p); }
    public static void UniformMatrix4(int location, int count, float[] v) { fixed (float* p = v) UniformMatrix4fv(location, count, 0, p); }
}
