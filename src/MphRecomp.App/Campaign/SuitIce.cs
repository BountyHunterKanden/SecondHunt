using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Render;
using MphRecomp.Anim;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// recomp: fitted ice on a body the host draws itself (an HD campaign suit, or a native body: Trooper, Prime 4 Sylux), the
// GL side of PlayerFittedIce.cs (owner 2026-11-09, the Death look thread's option B). Vanilla's own ice shell, fitted to
// the body (FittedIce.cs: its chunky pieces where vanilla puts them, at vanilla's gap, on the body's own bones; IceShell
// below), in vanilla's ice material: samus_ice's texture through MPH's normal texgen (the camera-space
// normal, scale 1 / translate 0: uv = 0.5 + 0.5 n.xy, v flipped), lit as MPH lights it (per light: colour x (diffuse x
// max(0, -L.n) + ambient), clamped), translucent, back faces dropped (vanilla culls them; tested per pixel so the
// mesh's winding doesn't matter). FACETED = each triangle's flat normal instead of the smooth one. Look only.
internal sealed class IceLook
{
    public int Tex;               // samus_ice / nox_ice's texture (GL name; 0 = no ice)
    public float Push;            // how far out along the normals, world units (0: the shell sits where the fit put it)
    public FittedIce.IceReference? Ref; // the shell and its reference body (SuitIce.Reference), for the body's fit
    public bool Facets;
    public Vector3 L1, C1, L2, C2; // the player's two lights (world space directions, 0-1 colours)
    public Vector3 Dif = Vector3.One, Amb = new(23f / 31f);
}

internal static class SuitIce
{
    const string Tag = "MPHIce";

    const string Vs =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "layout(location=1) in vec3 aNormal;\n" +
        "layout(location=4) in vec4 aBones;\n" +
        "layout(location=5) in vec4 aWeights;\n" +
        "uniform mat4 uMvp; uniform mat4 uView; uniform mat4 uModel; uniform mat4 uBones[32]; uniform float uPush;\n" +
        "out vec3 vPv; out vec3 vNv; out vec2 vUv;\n" +
        "void main(){\n" +
        "  mat4 B = uBones[int(aBones.x + 0.5)] * aWeights.x + uBones[int(aBones.y + 0.5)] * aWeights.y\n" +
        "         + uBones[int(aBones.z + 0.5)] * aWeights.z + uBones[int(aBones.w + 0.5)] * aWeights.w;\n" +
        "  vec3 p = (uModel * (B * vec4(aPos, 1.0))).xyz;\n" +
        "  vec3 sn = mat3(uModel) * (mat3(B) * aNormal);\n" +
        "  vec3 n = dot(sn, sn) > 1e-12 ? normalize(sn) : vec3(0.0);\n" +
        "  p += n * uPush;\n" +
        "  gl_Position = uMvp * vec4(p, 1.0);\n" +
        "  vPv = (uView * vec4(p, 1.0)).xyz;\n" +
        "  vNv = mat3(uView) * n;\n" +
        "  vec3 ne = dot(vNv, vNv) > 1e-12 ? normalize(vNv) : vec3(0.0, 0.0, 1.0);\n" +
        "  vUv = vec2(0.5 + 0.5 * ne.x, 0.5 - 0.5 * ne.y);\n" +
        "}\n";

    const string Fs =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec3 vPv; in vec3 vNv; in vec2 vUv;\n" +
        "uniform sampler2D uTex; uniform int uFacets;\n" +
        "uniform vec3 uL1; uniform vec3 uC1; uniform vec3 uL2; uniform vec3 uC2; uniform vec3 uDif; uniform vec3 uAmb;\n" +
        "out vec4 o;\n" +
        "void main(){\n" +
        "  vec3 ns = dot(vNv, vNv) > 1e-12 ? normalize(vNv) : vec3(0.0, 0.0, 1.0);\n" +
        "  vec3 cr = cross(dFdx(vPv), dFdy(vPv));\n" +
        "  vec3 nf = dot(cr, cr) > 1e-20 ? normalize(cr) : ns;\n" +
        "  if (dot(nf, ns) < 0.0) nf = -nf;\n" +
        "  if (dot(nf, -vPv) < 0.0) discard;\n" + // a back face (vanilla's Back cull)
        "  vec3 n = uFacets == 1 ? nf : ns;\n" +
        "  vec2 uv = uFacets == 1 ? vec2(0.5 + 0.5 * n.x, 0.5 - 0.5 * n.y) : vUv;\n" +
        "  vec4 t = texture(uTex, uv);\n" +
        "  vec3 c = uC1 * (uDif * max(0.0, -dot(uL1, n)) + uAmb) + uC2 * (uDif * max(0.0, -dot(uL2, n)) + uAmb);\n" +
        "  o = vec4(t.rgb * min(c, vec3(1.0)), t.a);\n" +
        "}\n";

    static readonly float[] _view16 = new float[16];
    static readonly Dictionary<(int, string), int> _u = new();
    static readonly HashSet<string> _logged = new();
    static int _prog;

    // a new GL context (CampaignRenderer.OnSurfaceCreated): the shells upload again
    public static int Generation;
    public static void Reset() => Generation++;

    static readonly Dictionary<string, FittedIce.IceReference?> _refs = new();

    // the thread that reads models (the draw thread: the sim steps there): a shell's reference, once
    public static FittedIce.IceReference? Reference(string rig)
    {
        string ice = FittedIce.IceFor(rig);
        if (_refs.TryGetValue(ice, out FittedIce.IceReference? r)) return r;
        try { r = FittedIce.ReferenceOf(rig); }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"no {ice} to fit: {ex.Message}");
            r = null;
        }
        return _refs[ice] = r;
    }

    // a body's shell, fitted on a worker the first time it's asked for (null until it's ready, or when there's none)
    public static IceShell? Fit(ref Task<IceShell?>? job, string who, Func<IceShell?> fit)
    {
        if (job == null)
        {
            job = Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    IceShell? s = fit();
                    Log.Info(Tag, s == null ? $"{who}: no fitted shell (not a biped's skeleton)" : $"{who}: shell fitted in {sw.ElapsedMilliseconds} ms");
                    return s;
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"{who}: shell fit failed: {ex}");
                    return null;
                }
            });
        }
        return job.IsCompletedSuccessfully ? job.Result : null;
    }

    static int U(string name)
    {
        if (!_u.TryGetValue((_prog, name), out int loc)) _u[(_prog, name)] = loc = GLES30.GlGetUniformLocation(_prog, name);
        return loc;
    }

    // GL thread, the body's VAO bound: the ice program and state for this body (mvp / model as the body's own draw
    // uploads them); false = no ice
    public static bool Begin(IceLook look, float[] mvp16, Matrix4 view, float[] model16)
    {
        if (look.Tex == 0) return false;
        _prog = CampaignGlCache.Program(Vs, Fs, "fitted ice");
        if (_prog == 0) return false;
        GLES30.GlUseProgram(_prog);
        GlUniforms.UniformMatrix4fv(U("uMvp"), 1, false, mvp16, 0);
        HunterRig.ToArray(view, _view16);
        GlUniforms.UniformMatrix4fv(U("uView"), 1, false, _view16, 0);
        GlUniforms.UniformMatrix4fv(U("uModel"), 1, false, model16, 0);
        GLES30.GlUniform1f(U("uPush"), look.Push);
        GLES30.GlUniform1i(U("uFacets"), look.Facets ? 1 : 0);
        GLES30.GlUniform1i(U("uTex"), 0);
        // the lights into camera space, where the normals are
        Vector3 l1 = Vector3.TransformVector(look.L1, view), l2 = Vector3.TransformVector(look.L2, view);
        GLES30.GlUniform3f(U("uL1"), l1.X, l1.Y, l1.Z);
        GLES30.GlUniform3f(U("uL2"), l2.X, l2.Y, l2.Z);
        GLES30.GlUniform3f(U("uC1"), look.C1.X, look.C1.Y, look.C1.Z);
        GLES30.GlUniform3f(U("uC2"), look.C2.X, look.C2.Y, look.C2.Z);
        GLES30.GlUniform3f(U("uDif"), look.Dif.X, look.Dif.Y, look.Dif.Z);
        GLES30.GlUniform3f(U("uAmb"), look.Amb.X, look.Amb.Y, look.Amb.Z);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, look.Tex);
        // its own filtering, as MPH's sampler objects would give it (the body's draw unbinds them; without one the texture's
        // default mipmapped filter leaves it incomplete: solid black)
        GLES30.GlBindSampler(0, 0);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE: the back faces go in the fragment shader
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDepthMask(false);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        return true;
    }

    // a batch's bone palette (the body's own, as its draw uploads it)
    public static void Bones(float[] palette, int count) => GlUniforms.UniformMatrix4fv(U("uBones"), count, false, palette, 0);

    public static void Draw(int start, int count) => GLES30.GlDrawArrays(GLES30.GlTriangles, start, count);

    // the draw check: once per body kind, in logcat
    public static void End(string who)
    {
        GLES30.GlDepthMask(true);
        if (_logged.Add(who)) Log.Info(Tag, $"fitted ice drew on {who}");
    }
}

// a body's fitted shell as the host draws it (FittedIce.ShellOnSkeleton / ShellOnModel): vanilla's shell fitted to the
// body's bind pose, each corner on one of the body's bones, skinned by the palette the draw fills: boneOf given, Bones
// lists the palette's slots (slot -> bone); none, the corners' nodes are the palette's slots (a suit's whole skeleton).
// Fitted on a worker (SuitIce.Fit), uploaded on the GL thread.
internal sealed class IceShell
{
    const int Floats = 14; // pos3, normal3, bones4, weights4: attributes 0, 1, 4, 5, as the bodies' own
    public readonly int[] Bones;
    readonly float[] _verts;
    readonly int[] _ids = new int[2], _was = new int[1];
    int _gen = -1;

    public IceShell(FittedIce.IceCorner[] corners, Func<int, int>? boneOf = null)
    {
        var bones = new List<int>();
        _verts = new float[corners.Length * Floats];
        for (int i = 0; i < corners.Length; i++)
        {
            int slot = corners[i].Node;
            if (boneOf != null)
            {
                int b = boneOf(slot);
                slot = bones.IndexOf(b);
                if (slot < 0 && bones.Count < 32) { slot = bones.Count; bones.Add(b); }
                if (slot < 0) slot = 0; // (more than 32 bones: can't happen with 19 nodes)
            }
            int o = i * Floats;
            FittedIce.IceCorner c = corners[i];
            (_verts[o], _verts[o + 1], _verts[o + 2]) = (c.Pos.X, c.Pos.Y, c.Pos.Z);
            (_verts[o + 3], _verts[o + 4], _verts[o + 5]) = (c.Normal.X, c.Normal.Y, c.Normal.Z);
            _verts[o + 6] = slot;
            _verts[o + 10] = 1;
        }
        Bones = bones.ToArray();
    }

    // between SuitIce.Begin / End, the palette uploaded (SuitIce.Bones)
    public void Draw()
    {
        GLES30.GlGetIntegerv(0x85B5, _was, 0); // GL_VERTEX_ARRAY_BINDING: the body's, back after
        if (_gen != SuitIce.Generation)
        {
            _gen = SuitIce.Generation;
            GLES30.GlGenVertexArrays(1, _ids, 0);
            GLES30.GlGenBuffers(1, _ids, 1);
            GLES30.GlBindVertexArray(_ids[0]);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _ids[1]);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, _verts.Length * 4, _verts, GLES30.GlStaticDraw);
            int[] at = { 0, 1, 4, 5 }, size = { 3, 3, 4, 4 }, off = { 0, 3, 6, 10 };
            for (int a = 0; a < 4; a++)
            {
                GLES30.GlEnableVertexAttribArray(at[a]);
                GLES30.GlVertexAttribPointer(at[a], size[a], GLES30.GlFloat, false, Floats * 4, off[a] * 4);
            }
        }
        else GLES30.GlBindVertexArray(_ids[0]);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, _verts.Length / Floats);
        GLES30.GlBindVertexArray(_was[0]);
    }
}
