using System;
using System.Collections.Generic;
using MphRecomp.Anim;
using MphRecomp.Arenas;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// HOLIDAY MODE's presents (owner 2026-11-20: "weapon crates in echoes would be replaced with presents. so would breakable
// crates like in main quarry"). Draw only: a crate keeps its collision, hits, break timing and drops; only its model is
// swapped for a wrapped box sized to the crate's own model box (its pass's _passBox), the paper patterned and a ribbon
// band crossing every face, a bow on top. Our own art, built here (no textures): a small lit program of its own, lit by
// the crate's CrateLight as DrawProps lights the crate. The paper and ribbon bits a present breaks into draw here too
// (CampaignRenderer.HolidayPresents.cs flies them).
internal sealed partial class ArenaHdDraw
{
    public readonly struct Present
    {
        public readonly Matrix4 Model;      // the crate's model matrix (its pass's space to the world)
        public readonly Vector3 Lo, Hi;     // the box in that space
        public readonly bool Flash;
        public readonly CrateLight? Light;
        public readonly float Fade;         // < 0: see-through at -Fade (DrawProps' rule)
        public readonly int Style;          // the wrapping (Styles)
        public readonly bool Bit;           // a flying piece: Lo/Hi its slab, one plain colour (Style's paper or ribbon)
        public readonly bool RibbonBit;

        public Present(Matrix4 model, Vector3 lo, Vector3 hi, bool flash, CrateLight? light, float fade, int style, bool bit = false, bool ribbonBit = false)
        {
            Model = model; Lo = lo; Hi = hi; Flash = flash; Light = light; Fade = fade; Style = style; Bit = bit; RibbonBit = ribbonBit;
        }
    }

    // paper, ribbon, pattern colour, pattern (0 dots, 1 stripes)
    public static readonly (Vector3 Paper, Vector3 Ribbon, Vector3 Mark, int Pattern)[] Styles =
    {
        (new(0.72f, 0.07f, 0.08f), new(0.96f, 0.76f, 0.22f), new(0.95f, 0.88f, 0.80f), 0),  // red, gold ribbon
        (new(0.05f, 0.42f, 0.16f), new(0.82f, 0.09f, 0.10f), new(0.95f, 0.80f, 0.30f), 1),  // green, red ribbon
        (new(0.10f, 0.20f, 0.62f), new(0.92f, 0.92f, 0.96f), new(0.55f, 0.75f, 0.98f), 0),  // blue, white ribbon
        (new(0.93f, 0.92f, 0.88f), new(0.80f, 0.08f, 0.10f), new(0.82f, 0.12f, 0.14f), 1),  // white, red stripes
        (new(0.42f, 0.13f, 0.55f), new(0.96f, 0.76f, 0.22f), new(0.90f, 0.80f, 0.95f), 0),  // purple, gold ribbon
    };

    public bool HasPresentBox(int pass) => _passBox.ContainsKey(pass);
    public (Vector3 Lo, Vector3 Hi) PassBox(int pass) => _passBox.TryGetValue(pass, out var b) ? b : (new Vector3(-0.5f), new Vector3(0.5f));

    const string PresentVert = "#version 300 es\n" +
        "layout(location=0) in vec3 aPos; layout(location=1) in vec3 aNrm;\n" +
        "uniform mat4 uMvp; uniform mat3 uNrm; out vec3 vPos; out vec3 vLn; out vec3 vN;\n" +
        "void main(){ gl_Position = uMvp * vec4(aPos, 1.0); vPos = aPos; vLn = aNrm; vN = normalize(uNrm * aNrm); }\n";
    // uMode 0: the box (paper + its pattern, the ribbon bands), 1: plain uRibbon (the bow), 2: plain uPaper (a paper bit)
    const string PresentFrag = "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec3 vPos; in vec3 vLn; in vec3 vN; out vec4 o;\n" +
        "uniform int uMode, uPattern, uNum; uniform vec3 uSize, uPaper, uRibbon, uMark, uAmb; uniform vec3 uLDir[4]; uniform vec3 uLCol[4];\n" +
        "uniform float uFlash, uAlpha;\n" +
        "void main(){\n" +
        "  vec3 base = uMode == 1 ? uRibbon : uPaper;\n" +
        "  if (uMode == 0) {\n" +
        "    vec3 q = vPos * uSize; vec3 a = abs(vLn);\n" +
        "    vec2 uv = a.x > 0.5 ? q.zy : a.y > 0.5 ? q.xz : q.xy;\n" +
        "    float s = 0.16 * min(uSize.x, uSize.z);\n" +
        "    float mark = uPattern == 0 ? step(length(fract(uv / s) - 0.5), 0.2) : step(fract((uv.x + uv.y) / (1.6 * s)), 0.3);\n" +
        "    base = mix(uPaper, uMark, mark);\n" +
        "    float w = 0.075 * min(uSize.x, uSize.z);\n" +
        "    float r = min(a.x > 0.5 ? 9.0 : abs(q.x), a.z > 0.5 ? 9.0 : abs(q.z));\n" +
        "    if (r < w) base = uRibbon * (r > 0.8 * w ? 0.72 : 1.0);\n" +
        "  }\n" +
        "  vec3 N = normalize(vN); vec3 lit = uAmb;\n" +
        "  for (int i = 0; i < 4; i++) { if (i >= uNum) break; lit += uLCol[i] * max(dot(N, uLDir[i]), 0.0); }\n" +
        // a little sheen off the ribbon and paper toward the camera, so a present reads in a dark corner
        "  float rim = pow(max(N.z, 0.0), 8.0) * 0.12;\n" +
        "  vec3 c = clamp(base * lit + rim, 0.0, 1.0);\n" +
        "  c = mix(c, vec3(1.0, 0.25, 0.2), uFlash * 0.5);\n" +
        "  o = vec4(c, uAlpha); }\n";

    int _presentProg, _presentVao, _presentVbo, _presentBoxCount, _presentBowStart, _presentBowCount;
    readonly Dictionary<string, int> _pu = new();
    readonly float[] _pMvp = new float[16], _pNrm = new float[9], _pDir = new float[12], _pCol = new float[12];
    public int PresentsDrawn { get; private set; }

    int PU(string name)
    {
        if (!_pu.TryGetValue(name, out int l)) _pu[name] = l = GLES30.GlGetUniformLocation(_presentProg, name);
        return l;
    }

    void ResetPresentsGl()
    {
        _presentProg = _presentVao = _presentVbo = 0;
        _pu.Clear();
    }

    // the unit box ([-0.5, 0.5], 36 vertices) then the bow (in units of the box's smaller side, sitting on y = 0)
    static float[] PresentMesh(out int boxCount, out int bowCount)
    {
        var v = new List<float>();
        AddBox(v, Matrix4.Identity);
        boxCount = v.Count / 6;
        // the bow: two loops (an ellipse of 8 flat segments each, leaning out), a knot, two tails on the lid
        for (int side = -1; side <= 1; side += 2)
        {
            const int n = 8;
            const float rx = 0.17f, ry = 0.10f, thick = 0.022f, width = 0.13f;
            Matrix4 lean = Matrix4.CreateRotationZ(side * 0.28f) * Matrix4.CreateTranslation(side * 0.16f, 0.085f, 0);
            for (int i = 0; i < n; i++)
            {
                float a0 = MathF.Tau * i / n, a1 = MathF.Tau * (i + 1) / n;
                Vector2 p0 = new(rx * MathF.Cos(a0), ry * MathF.Sin(a0)), p1 = new(rx * MathF.Cos(a1), ry * MathF.Sin(a1));
                Vector2 d = p1 - p0, mid = (p0 + p1) / 2;
                float len = d.Length + thick;
                Matrix4 seg = Matrix4.CreateScale(len, thick, width) * Matrix4.CreateRotationZ(MathF.Atan2(d.Y, d.X))
                    * Matrix4.CreateTranslation(mid.X, mid.Y, 0) * lean;
                AddBox(v, seg);
            }
            // a tail lying on the lid, out from the knot
            AddBox(v, Matrix4.CreateScale(0.30f, 0.012f, 0.09f) * Matrix4.CreateRotationY(side * 0.5f)
                * Matrix4.CreateTranslation(side * 0.14f, 0.006f, side * 0.08f));
        }
        AddBox(v, Matrix4.CreateScale(0.10f, 0.085f, 0.14f) * Matrix4.CreateTranslation(0, 0.045f, 0));
        bowCount = v.Count / 6 - boxCount;
        return v.ToArray();
    }

    // a unit cube's 12 triangles (counter-clockwise outward), through m (normals by its inverse transpose)
    static void AddBox(List<float> v, Matrix4 m)
    {
        var nm = new Matrix3(m);
        nm.Invert();
        nm.Transpose();
        for (int f = 0; f < 6; f++)
        {
            int ax = f / 2;
            float sgn = (f & 1) == 0 ? 1 : -1;
            Vector3 n = Vector3.Zero;
            n[ax] = sgn;
            Vector3 u = Vector3.Zero, w = Vector3.Zero;
            u[(ax + 1) % 3] = 1;
            w[(ax + 2) % 3] = 1;
            if (sgn < 0) (u, w) = (w, u);
            Vector3 c = n * 0.5f;
            Vector3[] q = { c - u * 0.5f - w * 0.5f, c + u * 0.5f - w * 0.5f, c + u * 0.5f + w * 0.5f, c - u * 0.5f + w * 0.5f };
            Vector3 wn = (n * nm).Normalized();
            foreach (int k in stackalloc[] { 0, 1, 2, 0, 2, 3 })
            {
                Vector3 p = Vector3.TransformPosition(q[k], m);
                v.Add(p.X); v.Add(p.Y); v.Add(p.Z);
                v.Add(wn.X); v.Add(wn.Y); v.Add(wn.Z);
            }
        }
    }

    bool PresentsReady()
    {
        if (_presentProg != 0) return true;
        if (_presentProg < 0) return false;
        _presentProg = CampaignGlCache.Program(PresentVert, PresentFrag, "holiday present");
        if (_presentProg == 0)
        {
            Log.Warn(Tag, "holiday present: the program did not link; crates draw as crates");
            _presentProg = -1;
            return false;
        }
        float[] mesh = PresentMesh(out _presentBoxCount, out _presentBowCount);
        _presentBowStart = _presentBoxCount;
        var ids = new int[1];
        GLES30.GlGenVertexArrays(1, ids, 0); _presentVao = ids[0];
        GLES30.GlGenBuffers(1, ids, 0); _presentVbo = ids[0];
        GLES30.GlBindVertexArray(_presentVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _presentVbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, mesh.Length * 4, mesh, GLES30.GlStaticDraw);
        GLES30.GlEnableVertexAttribArray(0);
        GLES30.GlVertexAttribPointer(0, 3, GLES30.GlFloat, false, 24, 0);
        GLES30.GlEnableVertexAttribArray(1);
        GLES30.GlVertexAttribPointer(1, 3, GLES30.GlFloat, false, 24, 12);
        GLES30.GlBindVertexArray(0);
        Log.Info(Tag, $"holiday present: mesh {_presentBoxCount / 3} box + {_presentBowCount / 3} bow triangles");
        return true;
    }

    // the presents, after DrawProps (opaque world drawn): depth tested and written, back faces culled; see-through ones
    // blended at their alpha without writing depth
    public void DrawPresents(Matrix4 view, Matrix4 proj, List<Present> presents)
    {
        PresentsDrawn = 0;
        if (presents.Count == 0 || !_glReady || Failed || !PresentsReady()) return;
        GLES30.GlUseProgram(_presentProg);
        GLES30.GlBindVertexArray(_presentVao);
        ArenaGl.Enable(0x0B44); // GL_CULL_FACE
        ArenaGl.CullFace(0x0405);
        ArenaGl.FrontFace(0x0901);
        ArenaGl.DepthFunc(GLES30.GlLequal);
        foreach (Present p in presents)
        {
            var st = Styles[(int)((uint)p.Style % Styles.Length)];
            Vector3 size = Vector3.ComponentMax(p.Hi - p.Lo, new Vector3(1e-3f)), mid = (p.Lo + p.Hi) / 2;
            Vector3 amb = p.Light?.Ambient ?? Vector3.One;
            int n = p.Light == null ? 0 : Math.Min(4, p.Light.Dirs.Length);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = Vector3.TransformVector(p.Light!.Dirs[i], view).Normalized(), c = p.Light.Colors[i];
                _pDir[3 * i] = d.X; _pDir[3 * i + 1] = d.Y; _pDir[3 * i + 2] = d.Z;
                _pCol[3 * i] = c.X; _pCol[3 * i + 1] = c.Y; _pCol[3 * i + 2] = c.Z;
            }
            GLES30.GlUniform3f(PU("uAmb"), amb.X, amb.Y, amb.Z);
            GLES30.GlUniform1i(PU("uNum"), n);
            if (n > 0)
            {
                ArenaGl.Uniform3fv(PU("uLDir"), 4, _pDir);
                ArenaGl.Uniform3fv(PU("uLCol"), 4, _pCol);
            }
            GLES30.GlUniform3f(PU("uPaper"), st.Paper.X, st.Paper.Y, st.Paper.Z);
            GLES30.GlUniform3f(PU("uRibbon"), st.Ribbon.X, st.Ribbon.Y, st.Ribbon.Z);
            GLES30.GlUniform3f(PU("uMark"), st.Mark.X, st.Mark.Y, st.Mark.Z);
            GLES30.GlUniform1i(PU("uPattern"), st.Pattern);
            GLES30.GlUniform3f(PU("uSize"), size.X, size.Y, size.Z);
            GLES30.GlUniform1f(PU("uFlash"), p.Flash ? 1 : 0);
            bool see = p.Fade < 1;
            float alpha = p.Fade < 0 ? -p.Fade : p.Fade;
            GLES30.GlUniform1f(PU("uAlpha"), alpha);
            if (see)
            {
                ArenaGl.Enable(0x0BE2); // GL_BLEND
                ArenaGl.BlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
                ArenaGl.DepthMask(false);
            }
            else
            {
                ArenaGl.Disable(0x0BE2);
                ArenaGl.DepthMask(true);
            }
            Matrix4 box = Matrix4.CreateScale(size) * Matrix4.CreateTranslation(mid) * p.Model;
            if (p.Bit)
            {
                GLES30.GlUniform1i(PU("uMode"), p.RibbonBit ? 1 : 2);
                DrawPresentPart(box, view, proj, 0, _presentBoxCount);
            }
            else
            {
                GLES30.GlUniform1i(PU("uMode"), 0);
                DrawPresentPart(box, view, proj, 0, _presentBoxCount);
                float s = MathF.Min(size.X, size.Z);
                Matrix4 bow = Matrix4.CreateScale(s) * Matrix4.CreateTranslation(mid.X, p.Hi.Y, mid.Z) * p.Model;
                GLES30.GlUniform1i(PU("uMode"), 1);
                DrawPresentPart(bow, view, proj, _presentBowStart, _presentBowCount);
            }
            PresentsDrawn++;
        }
        ArenaGl.Disable(0x0BE2);
        ArenaGl.DepthMask(true);
        ArenaGl.Disable(0x0B44);
        GLES30.GlBindVertexArray(0);
        // the arena's own draws find nothing of theirs bound
        _curProg = _active = _blend = _blendSrc = _blendDst = _depthMask = _depthFunc = -1;
        _progStamp = -1;
    }

    void DrawPresentPart(Matrix4 model, Matrix4 view, Matrix4 proj, int start, int count)
    {
        Matrix4 mv = model * view;
        HunterRig.ToArray(mv * proj, _pMvp);
        var nm = new Matrix3(mv);
        nm.Invert();
        nm.Transpose();
        _pNrm[0] = nm.M11; _pNrm[1] = nm.M12; _pNrm[2] = nm.M13;
        _pNrm[3] = nm.M21; _pNrm[4] = nm.M22; _pNrm[5] = nm.M23;
        _pNrm[6] = nm.M31; _pNrm[7] = nm.M32; _pNrm[8] = nm.M33;
        ArenaGl.UniformMatrix4(PU("uMvp"), 1, _pMvp);
        ArenaGl.UniformMatrix3(PU("uNrm"), 1, _pNrm);
        ArenaGl.DrawArrays(GLES30.GlTriangles, start, count);
    }
}
