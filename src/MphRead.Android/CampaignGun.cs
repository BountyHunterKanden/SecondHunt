using Android.Opengl;
using Android.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MphRecomp.Assets;
using MphRecomp.Campaign;
using MphRecomp.Render;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The first-person Prime arm cannon, drawn in place of MPH's SamusGun: the exported gun (<external files>/guns/<id>/:
// gun.bin, gun.gx.json, gun.cfg, textures -- the owner's own disc, never bundled) posed by GunViewmodel from MPH's
// gun state, through the GX pipeline of the HD trophies (MphRecomp.Render.GxShader), skinned on the GPU: each material
// with its own TEV program where ArenaGxSpec can write one, else the GX interpreter (GunPrograms).
// Lighting: the trophy viewer's light, as the HD suits use. The light materials take MPH's per-weapon colour.
internal sealed class CampaignGun
{
    public readonly HdGun Gun;
    public readonly GunViewmodel Viewmodel;
    public readonly Vector3 RestMuzzle;
    public float Scale;
    readonly string _dir;
    readonly List<GxMaterial> _mats;
    readonly GxShader.Params[] _params;
    readonly int[] _start, _count;
    readonly float[] _pal = new float[32 * 16];
    readonly float[] _m16 = new float[16], _mvp = new float[16], _viewRot = new float[9];
    int _prog, _vao, _vbo; // _prog: the GX interpreter, for a material without a program of its own
    int _depthProg;
    readonly bool _alphaTested; // a drawn material with GX's alpha test: the depth passes leave it to its colour program
    readonly List<string> _specSrc = new(); // the distinct material programs (GunPrograms.Frag), linked one per Warm step
    readonly int[] _specOf; // per material: its index in _specSrc, -1 = the interpreter
    int[] _specProg = Array.Empty<int>();
    int[] _matProg = Array.Empty<int>(); // per material: the program it draws with
    int[][]? _tex; // per material, per texture unit; set when every GL object is made
    readonly Dictionary<(int, string), int> _u = new();
    readonly Dictionary<string, int> _texByName = new();

    // The GL objects are made a step at a time (Warm: the program, the vertex buffer, then one texture per step), from
    // pixels and vertices a worker thread readies at load -- the first draw used to make them all in one frame (~0.3 s
    // at every first ship exit, 2026-10-03 recording). Draw finishes whatever is left if it comes first.
    sealed class Prep
    {
        public Java.Nio.ByteBuffer Verts = null!;
        public int VertBytes;
        public readonly List<(string Name, Java.Nio.ByteBuffer Px, int W, int H)> Tex = new();
    }
    Task<Prep>? _prep;
    int _step, _warmFrames;
    double _warmMs, _slowStepMs;
    string _slowStep = "";

    static readonly float[] LightDir = Norm3(-50f, 50f, 90f);
    static readonly float[] LightCol = { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] LightSpecCol = { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] LightSpecK = { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 };
    static float[] Norm3(float x, float y, float z)
    {
        float l = MathF.Sqrt(x * x + y * y + z * z);
        return new[] { x / l, y / l, z / l, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    public CampaignGun(string dir)
    {
        _dir = dir;
        Gun = HdGun.Load(System.IO.Path.Combine(dir, "gun.bin"));
        Viewmodel = new GunViewmodel(Gun);
        RestMuzzle = Viewmodel.RestMuzzle();
        Scale = Gun.Scale;
        _mats = GxJson.Read(System.IO.Path.Combine(dir, "gun.gx.json"));
        _params = new GxShader.Params[Gun.Materials.Length];
        _start = new int[Gun.Materials.Length];
        _count = new int[Gun.Materials.Length];
        _specOf = new int[Gun.Materials.Length];
        int at = 0;
        for (int m = 0; m < Gun.Materials.Length; m++)
        {
            _params[m] = GxShader.Pack(_mats[m]);
            _start[m] = at;
            _count[m] = Gun.Materials[m].VertexCount;
            at += _count[m];
            _alphaTested |= !_params[m].Blend && _params[m].AlphaTest[3] == 1;
            string? src = _params[m].Blend ? null : GunPrograms.Frag(Gun, m, _params[m]); // blended ones aren't drawn
            int spec = src == null ? -1 : _specSrc.IndexOf(src);
            if (src != null && spec < 0)
            {
                spec = _specSrc.Count;
                _specSrc.Add(src);
            }
            _specOf[m] = spec;
        }
        Log.Info("MPHCampaign", $"gun: {dir}: {Gun.BoneCount} bones, {Gun.Materials.Length} materials, {Gun.Anims.Count} animations, "
            + $"scale {Scale}, lights [{string.Join(",", Gun.LightMaterials)}]");
        _prep = StartPrep();
    }

    public string Name => System.IO.Path.GetFileName(_dir);

    // a new GL context: everything is made again (Warm / the next draw), from pixels read again on a worker thread
    public void ResetGl()
    {
        ForgetGl();
        _prep ??= StartPrep();
    }

    // this gun is being replaced: free its GL objects (on the GL thread; the program is CampaignGlCache's, shared)
    public void DeleteGl()
    {
        if (_vao != 0) GLES30.GlDeleteVertexArrays(1, new[] { _vao }, 0);
        if (_vbo != 0) GLES30.GlDeleteBuffers(1, new[] { _vbo }, 0);
        foreach (int t in _texByName.Values) GLES30.GlDeleteTextures(1, new[] { t }, 0);
        ForgetGl();
        _prep = null;
    }

    void ForgetGl()
    {
        _prog = 0;
        _depthProg = 0;
        _specProg = Array.Empty<int>();
        _matProg = Array.Empty<int>();
        _vao = 0;
        _vbo = 0;
        _tex = null;
        _u.Clear();
        _texByName.Clear();
        _step = 0;
        _warmFrames = 0;
        _warmMs = _slowStepMs = 0;
    }

    int U(int prog, string n) => _u.TryGetValue((prog, n), out int l) ? l : _u[(prog, n)] = GLES30.GlGetUniformLocation(prog, n);

    // a thread of its own (a pool thread can wait behind the game's own work): the vertex buffer and every texture's
    // pixels (RGBA bytes), ready for GL; the PNGs decode four at a time
    Task<Prep> StartPrep() => Task.Factory.StartNew(BuildPrep, TaskCreationOptions.LongRunning);

    Prep BuildPrep()
    {
        long t0 = Stopwatch.GetTimestamp();
        var p = new Prep();
        int total = 0;
        foreach (HdGun.Material m in Gun.Materials) total += m.Vertices.Length;
        p.VertBytes = total * 4;
        p.Verts = Java.Nio.ByteBuffer.AllocateDirect(p.VertBytes)!;
        p.Verts.Order(Java.Nio.ByteOrder.NativeOrder()!);
        Java.Nio.FloatBuffer fb = p.Verts.AsFloatBuffer()!;
        foreach (HdGun.Material m in Gun.Materials) fb.Put(m.Vertices);
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (GxShader.Params P in _params)
        {
            for (int u = 0; u < 8; u++)
            {
                string name = P.Units[u]?.Name ?? "";
                if (seen.Add(name)) names.Add(name);
            }
        }
        var read = new (Java.Nio.ByteBuffer Px, int W, int H)[names.Count];
        Parallel.For(0, names.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => read[i] = ReadPng(names[i]));
        for (int i = 0; i < names.Count; i++) p.Tex.Add((names[i], read[i].Px, read[i].W, read[i].H));
        Log.Info("MPHCampaign", $"gun: {names.Count} textures read in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
        return p;
    }

    (Java.Nio.ByteBuffer Px, int W, int H) ReadPng(string name)
    {
        int[] px = { unchecked((int)0xFFFFFFFF) };
        int w = 1, h = 1;
        string file = System.IO.Path.Combine(_dir, name + ".png");
        try
        {
            if (name != "" && System.IO.File.Exists(file))
            {
                Android.Graphics.Bitmap? bmp = Android.Graphics.BitmapFactory.DecodeFile(file);
                if (bmp != null)
                {
                    w = bmp.Width; h = bmp.Height; px = new int[w * h];
                    bmp.GetPixels(px, 0, w, 0, 0, w, h);
                    bmp.Recycle();
                    for (int i = 0; i < px.Length; i++)
                    {
                        int a = px[i];
                        px[i] = (int)((a & 0xFF00FF00u) | ((uint)(a >> 16) & 0xFF) | ((uint)(a & 0xFF) << 16)); // ARGB -> ABGR
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", $"gun: texture {name} failed: {ex.Message}");
            px = new[] { unchecked((int)0xFFFFFFFF) };
            w = h = 1;
        }
        var bb = Java.Nio.ByteBuffer.AllocateDirect(px.Length * 4)!;
        bb.Order(Java.Nio.ByteOrder.NativeOrder()!);
        bb.AsIntBuffer()!.Put(px);
        return (bb, w, h);
    }

    // a few GL steps this frame, for about budgetMs (at least one); true once the gun is ready to draw
    public bool Warm(double budgetMs)
    {
        if (_tex != null) return true;
        if (_prep == null || !_prep.IsCompleted) return false;
        long t0 = Stopwatch.GetTimestamp();
        do Step();
        while (_tex == null && Stopwatch.GetElapsedTime(t0).TotalMilliseconds < budgetMs);
        _warmFrames++;
        return _tex != null;
    }

    void EnsureGl()
    {
        if (_tex != null) return;
        _prep ??= StartPrep();
        _prep.Wait();
        _warmFrames++;
        while (_tex == null) Step();
    }

    void Step()
    {
        Prep p = _prep!.Result;
        long t0 = Stopwatch.GetTimestamp();
        string what;
        int nSpec = _specSrc.Count;
        if (_step == 0)
        {
            what = "program";
            _prog = CampaignGlCache.Program(GunPrograms.Vert, GxShader.Frag, "GX (gun)");
            _depthProg = CampaignGlCache.Program(GunPrograms.Vert, GunPrograms.DepthFrag, "GX depth (gun)");
            _specProg = new int[nSpec];
        }
        else if (_step <= nSpec)
        {
            what = "material program";
            _specProg[_step - 1] = CampaignGlCache.Program(GunPrograms.Vert, _specSrc[_step - 1], $"GX material {_step} of {nSpec} (gun)");
        }
        else if (_step == nSpec + 1)
        {
            what = "vertex buffer";
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _vao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _vbo = ids[0];
            GLES30.GlBindVertexArray(_vao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
            p.Verts.Position(0);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, p.VertBytes, p.Verts, GLES30.GlStaticDraw);
            int stride = HdGun.FloatsPerVertex * 4;
            int[] size = { 3, 3, 2, 3, 4, 4 }, off = { 0, 3, 6, 8, 11, 15 };
            for (int a = 0; a < 6; a++)
            {
                GLES30.GlEnableVertexAttribArray(a);
                GLES30.GlVertexAttribPointer(a, size[a], GLES30.GlFloat, false, stride, off[a] * 4);
            }
            GLES30.GlBindVertexArray(0);
        }
        else if (_step - nSpec - 2 < p.Tex.Count)
        {
            (string name, Java.Nio.ByteBuffer px, int w, int h) = p.Tex[_step - nSpec - 2];
            what = $"texture {name} {w}x{h}";
            _texByName[name] = Upload(px, w, h);
        }
        else
        {
            what = "materials";
            _matProg = new int[Gun.Materials.Length];
            _tex = new int[Gun.Materials.Length][];
            for (int m = 0; m < Gun.Materials.Length; m++)
            {
                int own = _specOf[m] >= 0 ? _specProg[_specOf[m]] : 0;
                _matProg[m] = own != 0 ? own : _prog;
                _tex[m] = new int[8];
                for (int u = 0; u < 8; u++)
                {
                    _tex[m][u] = _texByName[_params[m].Units[u]?.Name ?? ""];
                }
            }
            // the pixels are in GL now: let Java free the buffers
            foreach (var t in p.Tex) t.Px.Dispose();
            p.Verts.Dispose();
            _prep = null;
        }
        _step++;
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _warmMs += ms;
        if (ms > _slowStepMs) { _slowStepMs = ms; _slowStep = what; }
        if (_tex != null)
        {
            int drawn = 0, own = 0;
            for (int m = 0; m < Gun.Materials.Length; m++)
            {
                if (_params[m].Blend) continue;
                drawn++;
                if (_matProg[m] != _prog) own++;
            }
            Log.Info("MPHCampaign", $"gun: GL ready (program {_prog}, {own} of {drawn} materials on their own programs, depth "
                + $"{(_depthProg != 0 ? "first" : "with the colour")}, {_texByName.Count} textures; {_warmMs:0} ms of GL work over "
                + $"{_warmFrames} frames, slowest step {_slowStepMs:0.0} ms: {_slowStep})");
        }
    }

    static int Upload(Java.Nio.ByteBuffer px, int w, int h)
    {
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
        px.Position(0);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinearMipmapLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        return ids[0];
    }

    static void Put(float[] dst, Matrix4 m)
    {
        // OpenTK's memory layout, uploaded untransposed (row vectors), as MphRead and HdLook do
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }

    // The gun's passes in CampaignActivity.Render. Prepass: its depth alone, before the room (the caller masks colour),
    // so the room behind it and its own hidden faces skip their shading. AfterPrepass: the colour pass over that depth,
    // each gun pixel shaded once (a prepassed material's < becomes <=; GunPrograms.Vert puts it at the same depth).
    // DepthRebuild: pass 4 (colour masked by the caller), so MPH's muzzle flash and charge effects blend over the gun.
    // Full: colour and depth in one go (no prepass). Without the depth program everything falls back to Full's GX path.
    public enum GunPass { Full, Prepass, AfterPrepass, DepthRebuild }

    public bool HasDepthPass => _depthProg != 0;

    // Draw the posed gun (all of the MP1 gun's materials are opaque; blended ones are skipped). The caller restores its
    // own program.
    public void Draw(Matrix4 model, Matrix4 view, Matrix4 proj, Vector3 tint, double seconds, GunPass pass = GunPass.Full)
    {
        EnsureGl();
        if (_depthProg == 0)
        {
            if (pass == GunPass.Prepass) return;
            if (pass == GunPass.AfterPrepass) pass = GunPass.Full;
        }
        else if (pass is GunPass.Prepass or GunPass.DepthRebuild)
        {
            DrawDepth(model, view, proj);
            if (pass == GunPass.Prepass || !_alphaTested) return;
        }
        Put(_mvp, view * proj);
        Put(_m16, model);
        _viewRot[0] = view.M11; _viewRot[1] = view.M12; _viewRot[2] = view.M13;
        _viewRot[3] = view.M21; _viewRot[4] = view.M22; _viewRot[5] = view.M23;
        _viewRot[6] = view.M31; _viewRot[7] = view.M32; _viewRot[8] = view.M33;
        for (int t = 0; t < 8; t++)
        {
            GLES30.GlBindSampler(t, 0); // MPH's sampler objects would override the layers' wrap modes
        }
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlBindVertexArray(_vao);
        int cur = 0;
        for (int m = 0; m < Gun.Materials.Length; m++)
        {
            GxShader.Params P = _params[m];
            if (P.Blend)
            {
                continue; // none in the MP1 gun
            }
            bool tested = P.AlphaTest[3] == 1;
            if (pass == GunPass.DepthRebuild && !tested && _depthProg != 0) continue; // in DrawDepth
            int prog = _matProg[m];
            if (prog != cur)
            {
                GLES30.GlUseProgram(prog);
                Begin(prog);
                cur = prog;
            }
            if (prog == _prog)
            {
                // the interpreter's TEV, alpha test and normal map uniforms (a material's own program has them written in)
                GLES30.GlUniform1i(U(prog, "uNumStages"), P.NumStages);
                GlUniforms.Uniform4iv(U(prog, "uCabcd"), 8, P.Cabcd, 0); GlUniforms.Uniform4iv(U(prog, "uCmod"), 8, P.Cmod, 0);
                GlUniforms.Uniform4iv(U(prog, "uAabcd"), 8, P.Aabcd, 0); GlUniforms.Uniform4iv(U(prog, "uAmod"), 8, P.Amod, 0);
                GlUniforms.Uniform4iv(U(prog, "uDst"), 8, P.Dst, 0); GlUniforms.Uniform4iv(U(prog, "uKsel"), 8, P.Ksel, 0);
                GlUniforms.Uniform1iv(U(prog, "uRasChan"), 8, P.RasChan, 0); GlUniforms.Uniform4iv(U(prog, "uSwap"), 4, P.Swap, 0);
                GlUniforms.Uniform4iv(U(prog, "uAlphaTest"), 1, P.AlphaTest, 0); GlUniforms.Uniform2fv(U(prog, "uAlphaRef"), 1, P.AlphaRef, 0);
                GLES30.GlUniform1i(U(prog, "uBlendAlpha"), 0);
                GLES30.GlUniform1i(U(prog, "uNmap"), P.NmapUnit + 1); GLES30.GlUniform1i(U(prog, "uNmapTc"), P.NmapCoord);
                GLES30.GlUniform1i(U(prog, "uTintUnit"), Gun.GlowUnit);
            }
            GlUniforms.Uniform4fv(U(prog, "uReg"), 4, P.Reg, 0); GlUniforms.Uniform4fv(U(prog, "uKonst"), 4, P.Konst, 0);
            GlUniforms.Uniform4iv(U(prog, "uColCtrl"), 2, P.ColCtrl, 0); GlUniforms.Uniform4iv(U(prog, "uAlpCtrl"), 2, P.AlpCtrl, 0);
            GlUniforms.Uniform2iv(U(prog, "uAttn"), 2, P.Attn, 0);
            GlUniforms.Uniform4fv(U(prog, "uChanMat"), 2, P.ChanMat, 0); GlUniforms.Uniform4fv(U(prog, "uChanAmb"), 2, P.ChanAmb, 0);
            GlUniforms.Uniform1iv(U(prog, "uTgMode"), 8, P.TgMode, 0);
            GlUniforms.UniformMatrix3fv(U(prog, "uTexMtx"), 8, false, P.Scrolls ? GxShader.ScrolledTexMtx(_mats[m], P, seconds) : P.TexMtx, 0);
            GLES30.GlUniform4f(U(prog, "uTint"), tint.X, tint.Y, tint.Z, GunPrograms.TintMode(Gun, m, P));
            Viewmodel.FillPalette(m, _pal);
            GlUniforms.UniformMatrix4fv(U(prog, "uBones"), Gun.Materials[m].Palette.Length, false, _pal, 0);
            for (int u = 0; u < 8; u++)
            {
                GLES30.GlActiveTexture(GLES30.GlTexture0 + u);
                GLES30.GlBindTexture(GLES30.GlTexture2d, _tex![m][u]);
                GxLayer? L = P.Units[u];
                if (L != null)
                {
                    int W(int w) => w == 0 ? GLES30.GlClampToEdge : w == 2 ? GLES30.GlMirroredRepeat : GLES30.GlRepeat;
                    GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, W(L.WrapS));
                    GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, W(L.WrapT));
                }
            }
            int func = DepthFunc(P.DepthFunc);
            if (pass == GunPass.AfterPrepass && !tested && func == GLES30.GlLess) func = GLES30.GlLequal;
            GLES30.GlDepthFunc(func);
            GLES30.GlDrawArrays(GLES30.GlTriangles, _start[m], _count[m]);
        }
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindVertexArray(0);
        GLES30.GlEnable(GLES30.GlBlend);
    }

    // the uniforms every material of one program shares this frame (camera, model, the trophy viewer's light, samplers)
    void Begin(int prog)
    {
        GlUniforms.UniformMatrix4fv(U(prog, "uMvp"), 1, false, _mvp, 0);
        GlUniforms.UniformMatrix3fv(U(prog, "uViewRot"), 1, false, _viewRot, 0);
        GLES30.GlUniform1i(U(prog, "uSkin"), 1);
        GlUniforms.UniformMatrix4fv(U(prog, "uModel"), 1, false, _m16, 0);
        GLES30.GlUniform3f(U(prog, "uSceneAmb"), 102f / 255f, 100f / 255f, 100f / 255f);
        GLES30.GlUniform1i(U(prog, "uNumLights"), 1);
        GlUniforms.Uniform3fv(U(prog, "uLightDir"), 4, LightDir, 0);
        GlUniforms.Uniform3fv(U(prog, "uLightCol"), 4, LightCol, 0);
        GlUniforms.Uniform3fv(U(prog, "uLightSpecCol"), 4, LightSpecCol, 0);
        GlUniforms.Uniform3fv(U(prog, "uLightSpecK"), 4, LightSpecK, 0);
        GLES30.GlUniform1f(U(prog, "uExposure"), 1f);
        GLES30.GlUniform1i(U(prog, "uRawEnvNormal"), 0);
        for (int t = 0; t < 8; t++)
        {
            GLES30.GlUniform1i(U(prog, "uTex" + t), t);
        }
    }

    static int DepthFunc(int f) => f switch
    {
        0 => GLES30.GlNever, 1 => GLES30.GlLess, 2 => GLES30.GlEqual, 3 => GLES30.GlLequal,
        4 => GLES30.GlGreater, 5 => GLES30.GlNotequal, 6 => GLES30.GlGequal, _ => GLES30.GlAlways,
    };

    // The gun's depth without its shading (GunPrograms.Vert + an empty fragment program): the pass-1 prepass and the
    // pass-4 depth rebuild. Alpha-tested materials are left to their colour programs (their holes come from the TEV
    // alpha). Before it, the depth rebuild ran the GX program over every gun pixel again: MP4 Legacy's GPU 93-99% busy
    // and 107-117 fps in the VDO 2 Slench fight, MP1 Fusion's 57% and 120 (2026-10-03 capture).
    void DrawDepth(Matrix4 model, Matrix4 view, Matrix4 proj)
    {
        int prog = _depthProg;
        GLES30.GlUseProgram(prog);
        Put(_mvp, view * proj);
        GlUniforms.UniformMatrix4fv(U(prog, "uMvp"), 1, false, _mvp, 0);
        Put(_m16, model);
        GlUniforms.UniformMatrix4fv(U(prog, "uModel"), 1, false, _m16, 0);
        GLES30.GlUniform1i(U(prog, "uSkin"), 1);
        int bones = U(prog, "uBones");
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlBindVertexArray(_vao);
        for (int m = 0; m < Gun.Materials.Length; m++)
        {
            GxShader.Params P = _params[m];
            if (P.Blend || P.AlphaTest[3] == 1) continue;
            Viewmodel.FillPalette(m, _pal);
            GlUniforms.UniformMatrix4fv(bones, Gun.Materials[m].Palette.Length, false, _pal, 0);
            GLES30.GlDepthFunc(DepthFunc(P.DepthFunc));
            GLES30.GlDrawArrays(GLES30.GlTriangles, _start[m], _count[m]);
        }
        GLES30.GlBindVertexArray(0);
        GLES30.GlEnable(GLES30.GlBlend);
    }
}
