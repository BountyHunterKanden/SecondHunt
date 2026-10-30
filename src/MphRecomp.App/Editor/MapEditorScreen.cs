using System.Diagnostics;
using MphRecomp.Arenas.Editor;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// MAP MAKER (docs/MAP_EDITOR.md milestones E0-E1): the block-editing screen. Fly around, point at a face, wheel
// pulls blocks out of it or pushes them in; Ctrl+wheel pushes the nearest corner, Shift+wheel the whole face; [ ] grid.
// E1: blocks carry MPH textures (left click paints the aimed face, Shift+click the whole block, , . pick the texture,
// middle click copies the aimed one) and the map is a .shmap document (MapDocument).
// The host feeds input each frame (EditorInput) and starts PLAY with CompileForPlay. Plain GLES 3, one textured program.
public sealed class MapEditorScreen : IGlScreen
{
    public struct EditorInput
    {
        public bool Forward, Back, Left, Right, Up, Down, Fast;
        public bool Looking;            // right mouse held: the mouse turns the camera, aim from the screen centre
        public float LookDx, LookDy;    // mouse motion, pixels
        public float MouseX, MouseY;    // cursor, pixels from the top left
        public int Wheel;               // notches: + = toward the camera (pull out), - = push in
        public bool Ctrl, Shift;
        public int GridStep;            // +1 = bigger grid (]), -1 = smaller ([)
        public bool Home, Save;
        public bool Paint;              // left click (or P): paint the aimed face; with Shift the whole block
        public bool Eyedrop;            // middle click (or I): the aimed face's texture becomes the paint texture
        public int TextureStep;         // +1 = next texture (.), -1 = previous (,)
        public bool TestField;          // F9: add a patch of test pillars (to try a big map)
    }

    static readonly int[] Grids = { 1, 2, 4, 8, 16, 32, 64 };   // grid steps of 0.25: 0.25 .. 16 units

    readonly string _mapPath;
    MapDocument _doc = new();
    BlockWorld _world => _doc.World;
    readonly TextureLibrary? _textures;
    int _paintTex = 1;                                            // 0 = auto (floor / wall / ceiling by direction)
    List<BlockMesher.Tri> _tris = new();                          // one per leaf face triangle: what the aim picks
    List<BlockMesher.Tri> _drawTris = new();                      // the same with flat faces merged: what is drawn, counted and compiled
    // the uploaded triangles in runs of one texture (0 = flat colours): texture id, first vertex, vertex count
    readonly List<(int Tex, int First, int Count)> _batches = new();
    readonly Dictionary<int, int> _glTextures = new();
    int _savedVersion = -1;                                       // the world version on disk (the first load counts as saved)
    int _meshedVersion = -1, _uploadedVersion = -1, _vertexCount;
    int _gridIndex = 2;                                           // 1 unit (Hunters survey, docs/MAP_EDITOR.md 5b)
    Vector3 _cam;
    float _yaw, _pitch;
    int _width = 1, _height = 1;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTime;
    EditorInput _in;

    // the face under the aim: grid cell (steps), its size, the face, the hit point
    bool _hasSel;
    Vector3i _selCell, _selLeaf;
    int _selAxis, _selSide, _selLeafSize, _selTex;
    Vector3 _hitPoint;
    string _message = "";
    double _messageUntil;

    const int Stride = 8;   // floats per vertex: position, colour, texture coordinate
    int _program, _vao, _vbo, _lineVao, _lineVbo, _uMvp, _uTex, _uTextured, _uScroll;

    public MapEditorScreen(string mapPath)
    {
        _mapPath = mapPath;
        try
        {
            _textures = TextureLibrary.Load();
        }
        catch (Exception ex)
        {
            Log.Warn("MapMaker", "textures unavailable: " + ex.Message);
        }
        if (_textures == null) _paintTex = 0;
        ResetCamera();
        LoadDocument();
        _savedVersion = _world.Version;
    }

    // the .shmap, or E0's map.smm beside it (upgraded on the next save); a file that can't be read is kept as .bad
    void LoadDocument()
    {
        string legacy = Path.Combine(Path.GetDirectoryName(_mapPath) ?? "", "map.smm");
        string source = File.Exists(_mapPath) ? _mapPath : legacy;
        try
        {
            MapDocument? doc = MapDocument.Load(source);
            if (doc == null) return;
            _doc = doc;
            _cam = new Vector3(doc.CameraPosition[0], doc.CameraPosition[1], doc.CameraPosition[2]);
            _yaw = doc.CameraYaw;
            _pitch = doc.CameraPitch;
            _gridIndex = Math.Clamp(doc.GridIndex, 0, Grids.Length - 1);
            if (source == legacy) Say("upgraded map.smm to .shmap (saved on Ctrl+S)", 6);
        }
        catch (Exception ex)
        {
            Log.Error("MapMaker", $"can't read {source}: {ex}");
            try { File.Move(source, source + ".bad", overwrite: true); } catch (Exception) { }
            Say($"couldn't read the saved map ({ex.Message}); kept as {Path.GetFileName(source)}.bad, starting fresh", 8);
        }
    }

    string PaintName => _paintTex == 0 ? "auto" : _textures?.Get(_paintTex)?.Name ?? "?";

    public string Status
    {
        get
        {
            float g = Grids[_gridIndex] * BlockWorld.Unit;
            string mode = _in.Ctrl ? "CORNER" : _in.Shift ? "FACE" : "BLOCKS";
            string msg = _clock.Elapsed.TotalSeconds < _messageUntil ? "   " + _message : "";
            string paint = _textures == null ? "flat colours (no import)" : $"paint: {PaintName} ({_paintTex})";
            bool over = _drawTris.Count > MapCompiler.TriangleSoftLimit;
            string tris = $"{_drawTris.Count} / {MapCompiler.TriangleHardLimit} triangles" + (_drawTris.Count > MapCompiler.TriangleHardLimit ? " - TOO MANY: the collision format holds at most that many, F5 will refuse" : over ? " - heads-up: a very big map, weak devices may stutter" : "");
            string unsaved = _world.Version != _savedVersion ? "   * UNSAVED (Ctrl+S)" : "";
            return $"MAP MAKER E1   {tris}{unsaved}   grid {g:0.##}   wheel: {mode}   {paint}   F5 play   Ctrl+S save   F9 test pillars{msg}";
        }
    }

    void Say(string text, double seconds = 3)
    {
        _message = text;
        _messageUntil = _clock.Elapsed.TotalSeconds + seconds;
    }

    void ResetCamera()
    {
        _cam = new Vector3(0, 8, 20);
        _yaw = 0;
        _pitch = -0.35f;
    }

    Vector3 Forward => new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), -MathF.Cos(_yaw) * MathF.Cos(_pitch));

    public void Feed(EditorInput input) => _in = input;

    public void Save()
    {
        try
        {
            _doc.CameraPosition = new[] { _cam.X, _cam.Y, _cam.Z };
            _doc.CameraYaw = _yaw;
            _doc.CameraPitch = _pitch;
            _doc.GridIndex = _gridIndex;
            _doc.Save(_mapPath);
            _savedVersion = _world.Version;
        }
        catch (Exception ex)
        {
            Say("save failed: " + ex.Message);
        }
    }

    // writes the arena and returns its room name (null: nothing to play, the reason in the status line)
    public string? CompileForPlay(string folder)
    {
        Vector3 spawn = _hasSel && _selAxis == 1 && _selSide == 1 ? _hitPoint + new Vector3(0, 0.05f, 0) : _cam;
        try
        {
            return MapCompiler.Compile(_world, folder, spawn, _yaw, _textures);
        }
        catch (Exception ex)
        {
            Say("can't play: " + ex.Message, 5);
            Log.Error("MapMaker", ex.ToString());
            return null;
        }
    }

    // ---- per frame ----

    void Update()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - _lastTime);
        _lastTime = now;
        EditorInput i = _in;
        _in.Wheel = 0; _in.GridStep = 0; _in.Home = false; _in.Save = false; _in.LookDx = 0; _in.LookDy = 0;
        _in.Paint = false; _in.Eyedrop = false; _in.TextureStep = 0; _in.TestField = false;

        if (i.Looking)
        {
            _yaw += i.LookDx * 0.004f;
            _pitch = Math.Clamp(_pitch - i.LookDy * 0.004f, -1.55f, 1.55f);
        }
        var fwd = Forward;
        var flat = new Vector3(MathF.Sin(_yaw), 0, -MathF.Cos(_yaw));
        var right = new Vector3(-flat.Z, 0, flat.X);
        var move = Vector3.Zero;
        if (i.Forward) move += fwd;
        if (i.Back) move -= fwd;
        if (i.Right) move += right;
        if (i.Left) move -= right;
        if (i.Up) move += Vector3.UnitY;
        if (i.Down) move -= Vector3.UnitY;
        if (move.LengthSquared > 0) _cam += move.Normalized() * (i.Fast ? 36 : 12) * dt;
        if (i.Home) ResetCamera();
        if (i.GridStep != 0) _gridIndex = Math.Clamp(_gridIndex + i.GridStep, 0, Grids.Length - 1);
        if (i.Save)
        {
            Save();
            Say("saved");
        }
        if (i.TextureStep != 0 && _textures != null)
        {
            int count = _textures.Entries.Count + 1;
            _paintTex = ((_paintTex + i.TextureStep) % count + count) % count;
        }

        if (i.TestField) AddTestField();
        Remesh();
        Pick(i);
        if (i.Eyedrop && _hasSel && _textures != null) _paintTex = _selTex;
        if (i.Paint && _hasSel)
        {
            int pg = Grids[_gridIndex];
            bool ok = i.Shift ? _world.PaintCell(_selCell, pg, -1, 0, _paintTex) : _world.PaintCell(_selCell, pg, _selAxis, _selSide, _paintTex);
            if (ok)
            {
                Remesh();
                Pick(i);
            }
        }
        if (i.Wheel != 0 && _hasSel)
        {
            for (int n = 0; n < Math.Abs(i.Wheel); n++)
            {
                Edit(i, i.Wheel > 0);
                Remesh();
                Pick(i);
                if (!_hasSel) break;
            }
        }
    }

    // F9: 100 one-unit pillars (10 x 10, 3 units apart) on one quarter of the starter floor; four presses fill it
    int _fieldPatch;

    void AddTestField()
    {
        if (_fieldPatch >= 4)
        {
            Say("the test field is full (four patches)");
            return;
        }
        int px = _fieldPatch % 2, pz = _fieldPatch / 2;
        int y = (int)((0 - BlockWorld.Origin.Y) / BlockWorld.Unit);
        for (int a = 0; a < 10; a++)
        {
            for (int b = 0; b < 10; b++)
            {
                float wx = -30 + px * 30 + a * 3 + 0.5f, wz = -30 + pz * 30 + b * 3 + 0.5f;
                int sx = (int)((wx - BlockWorld.Origin.X) / BlockWorld.Unit), sz = (int)((wz - BlockWorld.Origin.Z) / BlockWorld.Unit);
                _world.SetSolid(new Vector3i(sx, y, sz), 4, true, merge: false);
            }
        }
        _world.MergeAll();
        _fieldPatch++;
        Remesh();
        Say($"added test pillars (patch {_fieldPatch} of 4): {_drawTris.Count} triangles");
    }

    void Remesh()
    {
        if (_meshedVersion == _world.Version && _tris.Count > 0) return;
        _tris = BlockMesher.Build(_world, _textures);
        _drawTris = BlockMesher.Build(_world, _textures, merge: true);
        _meshedVersion = _world.Version;
    }

    void Edit(EditorInput i, bool outward)
    {
        int g = Grids[_gridIndex];
        if (i.Ctrl)
        {
            if (!_world.PushCorner(_selCell, g, _selAxis, _selSide, NearestCorner(), !outward)) Say("corners: aim at one block of this grid size");
        }
        else if (i.Shift)
        {
            if (!_world.PushFace(_selCell, g, _selAxis, _selSide, !outward)) Say("faces: aim at one block of this grid size");
        }
        else if (outward)
        {
            Vector3i next = _selCell;
            next[_selAxis] += _selSide == 1 ? g : -g;
            _world.SetSolid(next, g, true);
        }
        else
        {
            _world.SetSolid(_selCell, g, false);
        }
    }

    // the selected face's corner (bits x | y << 1 | z << 2) nearest the hit point
    int NearestCorner()
    {
        int g = Grids[_gridIndex];
        int best = 0;
        float bestD = float.MaxValue;
        foreach (int corner in BlockMesher.FaceCorners(_selAxis, _selSide))
        {
            var c = BlockWorld.ToWorld(_selCell + new Vector3i(corner & 1, (corner >> 1) & 1, corner >> 2) * g);
            float d = (c - _hitPoint).LengthSquared;
            if (d < bestD) { bestD = d; best = corner; }
        }
        return best;
    }

    (Vector3 Origin, Vector3 Dir) AimRay(EditorInput i)
    {
        float x = i.Looking ? 0 : i.MouseX / _width * 2 - 1;
        float y = i.Looking ? 0 : 1 - i.MouseY / _height * 2;
        Matrix4 inv = (View() * Projection()).Inverted();
        Vector4 near = new Vector4(x, y, -1, 1) * inv, far = new Vector4(x, y, 1, 1) * inv;
        Vector3 a = near.Xyz / near.W, b = far.Xyz / far.W;
        return (a, (b - a).Normalized());
    }

    void Pick(EditorInput i)
    {
        (Vector3 o, Vector3 d) = AimRay(i);
        float best = float.MaxValue;
        int hit = -1;
        for (int t = 0; t < _tris.Count; t++)
        {
            BlockMesher.Tri tri = _tris[t];
            if (Vector3.Dot(tri.Normal, d) >= 0) continue;    // back faces
            float dist = Intersect(o, d, tri.A, tri.B, tri.C);
            if (dist > 0 && dist < best) { best = dist; hit = t; }
        }
        _hasSel = hit >= 0;
        if (!_hasSel) return;
        BlockMesher.Tri h = _tris[hit];
        _hitPoint = o + d * best;
        _selAxis = h.Axis;
        _selSide = h.Side;
        _selLeaf = h.Min;
        _selLeafSize = h.Size;
        _selTex = h.Tex;
        int g = Grids[_gridIndex];
        // the grid cell inside the block, just behind the hit point
        Vector3 steps = (_hitPoint - BlockWorld.Origin) / BlockWorld.Unit;
        steps[_selAxis] += _selSide == 1 ? -0.01f : 0.01f;
        // a face pushed in from its cube's side still belongs to that cube: keep the cell in the leaf's grid cell
        Vector3 leafMin = h.Min, leafMax = (Vector3)h.Min + new Vector3(h.Size);
        steps = Vector3.Clamp(steps, leafMin + new Vector3(0.01f), leafMax - new Vector3(0.01f));
        _selCell = new Vector3i((int)MathF.Floor(steps.X / g) * g, (int)MathF.Floor(steps.Y / g) * g, (int)MathF.Floor(steps.Z / g) * g);
    }

    static float Intersect(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return -1;
        float inv = 1 / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return -1;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return -1;
        return Vector3.Dot(e2, q) * inv;
    }

    Matrix4 View() => Matrix4.LookAt(_cam, _cam + Forward, Vector3.UnitY);
    Matrix4 Projection() => Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70), _width / (float)_height, 0.1f, 1000);

    // ---- GL ----

    const string VertexShader = @"#version 300 es
precision highp float;
layout(location = 0) in vec3 a_pos;
layout(location = 1) in vec3 a_col;
layout(location = 2) in vec2 a_uv;
uniform mat4 u_mvp;
uniform vec2 u_scroll;
out vec3 v_col;
out vec2 v_uv;
void main()
{
    gl_Position = u_mvp * vec4(a_pos, 1.0);
    v_col = a_col;
    v_uv = a_uv + u_scroll;
}";

    const string FragmentShader = @"#version 300 es
precision highp float;
in vec3 v_col;
in vec2 v_uv;
uniform sampler2D u_tex;
uniform float u_textured;
out vec4 o_col;
void main()
{
    vec4 t = u_textured > 0.5 ? texture(u_tex, v_uv) : vec4(1.0);
    if (t.a < 0.5) discard;
    o_col = vec4(v_col * t.rgb, 1.0);
}";

    public void OnSurfaceCreated()
    {
        _program = Link(VertexShader, FragmentShader);
        _uMvp = GLES30.GlGetUniformLocation(_program, "u_mvp");
        _uTex = GLES30.GlGetUniformLocation(_program, "u_tex");
        _uTextured = GLES30.GlGetUniformLocation(_program, "u_textured");
        _uScroll = GLES30.GlGetUniformLocation(_program, "u_scroll");
        _glTextures.Clear();                                      // a new context (back from PLAY): the old texture ids are gone
        var ids = new int[1];
        GLES30.GlGenVertexArrays(1, ids, 0); _vao = ids[0];
        GLES30.GlGenBuffers(1, ids, 0); _vbo = ids[0];
        GLES30.GlGenVertexArrays(1, ids, 0); _lineVao = ids[0];
        GLES30.GlGenBuffers(1, ids, 0); _lineVbo = ids[0];
        foreach ((int vao, int vbo) in new[] { (_vao, _vbo), (_lineVao, _lineVbo) })
        {
            GLES30.GlBindVertexArray(vao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, vbo);
            GLES30.GlEnableVertexAttribArray(0);
            GLES30.GlVertexAttribPointer(0, 3, GLES30.GlFloat, false, Stride * 4, 0);
            GLES30.GlEnableVertexAttribArray(1);
            GLES30.GlVertexAttribPointer(1, 3, GLES30.GlFloat, false, Stride * 4, 12);
            GLES30.GlEnableVertexAttribArray(2);
            GLES30.GlVertexAttribPointer(2, 2, GLES30.GlFloat, false, Stride * 4, 24);
        }
        GLES30.GlBindVertexArray(0);
        _uploadedVersion = -1;                                    // a new context (back from PLAY): upload again
        _lastTime = _clock.Elapsed.TotalSeconds;
    }

    public void OnSurfaceChanged(int width, int height)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
    }

    public void OnDrawFrame()
    {
        Update();
        if (_uploadedVersion != _meshedVersion) Upload();

        GLES30.GlViewport(0, 0, _width, _height);
        GLES30.GlClearColor(0.16f, 0.20f, 0.27f, 1);
        GLES30.GlDepthMask(true);
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthFunc(GLES30.GlLequal);
        GLES30.GlDisable(0x0B44); // GL_CULL_FACE
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlUseProgram(_program);
        float[] mvp = Floats(View() * Projection());
        GLES30.GlUniformMatrix4fv(_uMvp, 1, false, mvp, 0);

        GLES30.GlEnable(GLES30.GlPolygonOffsetFill);
        GLES30.GlPolygonOffset(1, 1);
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(_uTex, 0);
        // the lava / water slide the way the game plays them: whole repeats over 8 s (TextureLibrary.Entry.ScrollS / T)
        float slide = (float)(_clock.Elapsed.TotalSeconds % 8.0) / 8f;
        foreach ((int tex, int first, int count) in _batches)
        {
            TextureLibrary.Entry? e = tex > 0 ? _textures?.Get(tex) : null;
            GLES30.GlUniform1f(_uTextured, e != null ? 1 : 0);
            GLES30.GlUniform2f(_uScroll, e != null ? e.ScrollS * slide : 0, e != null ? e.ScrollT * slide : 0);
            if (e != null) GLES30.GlBindTexture(GLES30.GlTexture2d, GlTexture(e));
            GLES30.GlDrawArrays(GLES30.GlTriangles, first, count);
        }
        GLES30.GlUniform1f(_uTextured, 0);
        GLES30.GlUniform2f(_uScroll, 0, 0);
        GLES30.GlDisable(GLES30.GlPolygonOffsetFill);

        var lines = new List<float>();
        if (_hasSel) SelectionLines(lines);
        if (lines.Count > 0)
        {
            DrawLines(lines);
        }
        // the aim point while looking
        if (_in.Looking)
        {
            float sx = 10f / _width, sy = 10f / _height;
            var cross = new List<float>();
            Line(cross, new Vector3(-sx, 0, 0), new Vector3(sx, 0, 0), Vector3.One);
            Line(cross, new Vector3(0, -sy, 0), new Vector3(0, sy, 0), Vector3.One);
            GLES30.GlDisable(GLES30.GlDepthTest);
            GLES30.GlUniformMatrix4fv(_uMvp, 1, false, Floats(Matrix4.Identity), 0);
            DrawLines(cross);
        }
        DrawSwatch();
        GLES30.GlBindVertexArray(0);
    }

    // the paint texture, a small square at the bottom left
    void DrawSwatch()
    {
        TextureLibrary.Entry? e = _paintTex > 0 ? _textures?.Get(_paintTex) : null;
        if (e == null) return;
        float w = 96f / _width * 2, h = 96f / _height * 2, x0 = -1 + 16f / _width * 2, y0 = -1 + 16f / _height * 2;
        float[] q =
        {
            x0, y0, 0, 1, 1, 1, 0, 1,   x0 + w, y0, 0, 1, 1, 1, 1, 1,   x0 + w, y0 + h, 0, 1, 1, 1, 1, 0,
            x0, y0, 0, 1, 1, 1, 0, 1,   x0 + w, y0 + h, 0, 1, 1, 1, 1, 0,   x0, y0 + h, 0, 1, 1, 1, 0, 0,
        };
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlUniformMatrix4fv(_uMvp, 1, false, Floats(Matrix4.Identity), 0);
        GLES30.GlUniform1f(_uTextured, 1);
        GLES30.GlBindTexture(GLES30.GlTexture2d, GlTexture(e));
        GLES30.GlBindVertexArray(_lineVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _lineVbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, q.Length * 4, q, GLES30.GlDynamicDraw);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 6);
        GLES30.GlUniform1f(_uTextured, 0);
    }

    int GlTexture(TextureLibrary.Entry e)
    {
        if (_glTextures.TryGetValue(e.Id, out int id)) return id;
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        id = ids[0];
        GLES30.GlBindTexture(GLES30.GlTexture2d, id);
        GLES30.GlPixelStorei(GLES30.GlUnpackAlignment, 1);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, e.Width, e.Height, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, e.Rgba);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlRepeat);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlRepeat);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlNearest);   // the DS look
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlNearest);
        _glTextures[e.Id] = id;
        return id;
    }

    void DrawLines(List<float> lines)
    {
        float[] data = lines.ToArray();
        GLES30.GlBindVertexArray(_lineVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _lineVbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, data.Length * 4, data, GLES30.GlDynamicDraw);
        GLES30.GlDrawArrays(GLES30.GlLines, 0, data.Length / Stride);
    }

    // the selected grid cell's face outlined, and in corner mode the corner that the wheel moves
    void SelectionLines(List<float> lines)
    {
        int g = Grids[_gridIndex];
        int[] k = BlockMesher.FaceCorners(_selAxis, _selSide);
        var n = Vector3.Zero;
        n[_selAxis] = _selSide == 1 ? 0.02f : -0.02f;
        Vector3 P(int corner) => BlockWorld.ToWorld(_selCell + new Vector3i(corner & 1, (corner >> 1) & 1, corner >> 2) * g) + n;
        var col = _in.Ctrl ? new Vector3(1, 0.6f, 0.2f) : _in.Shift ? new Vector3(0.4f, 1, 0.5f) : new Vector3(1, 1, 0.3f);
        for (int e = 0; e < 4; e++) Line(lines, P(k[e]), P(k[(e + 1) % 4]), col);
        if (_in.Ctrl)
        {
            Vector3 c = P(NearestCorner());
            float r = g * BlockWorld.Unit * 0.2f;
            Line(lines, c - new Vector3(r, 0, 0), c + new Vector3(r, 0, 0), Vector3.One);
            Line(lines, c - new Vector3(0, r, 0), c + new Vector3(0, r, 0), Vector3.One);
            Line(lines, c - new Vector3(0, 0, r), c + new Vector3(0, 0, r), Vector3.One);
        }
    }

    static void Line(List<float> f, Vector3 a, Vector3 b, Vector3 c)
    {
        f.Add(a.X); f.Add(a.Y); f.Add(a.Z); f.Add(c.X); f.Add(c.Y); f.Add(c.Z); f.Add(0); f.Add(0);
        f.Add(b.X); f.Add(b.Y); f.Add(b.Z); f.Add(c.X); f.Add(c.Y); f.Add(c.Z); f.Add(0); f.Add(0);
    }

    void Upload()
    {
        // flat-coloured triangles first, then one run per texture
        var order = new List<int>(_drawTris.Count);
        for (int t = 0; t < _drawTris.Count; t++) order.Add(t);
        order.Sort((x, y) => _drawTris[x].Tex != _drawTris[y].Tex ? _drawTris[x].Tex.CompareTo(_drawTris[y].Tex) : x.CompareTo(y));
        var data = new float[_drawTris.Count * 3 * Stride];
        int o = 0;
        _batches.Clear();
        foreach (int index in order)
        {
            BlockMesher.Tri t = _drawTris[index];
            if (_batches.Count == 0 || _batches[^1].Tex != t.Tex) _batches.Add((t.Tex, o / Stride, 0));
            _batches[^1] = (t.Tex, _batches[^1].First, _batches[^1].Count + 3);
            Vector3[] ps = { t.A, t.B, t.C };
            Vector2[] uvs = { t.UvA, t.UvB, t.UvC };
            for (int k = 0; k < 3; k++)
            {
                data[o++] = ps[k].X; data[o++] = ps[k].Y; data[o++] = ps[k].Z;
                data[o++] = t.Color.X; data[o++] = t.Color.Y; data[o++] = t.Color.Z;
                data[o++] = uvs[k].X; data[o++] = uvs[k].Y;
            }
        }
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _vbo);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, Math.Max(4, data.Length * 4), data, GLES30.GlStaticDraw);
        _vertexCount = _drawTris.Count * 3;
        _uploadedVersion = _meshedVersion;
    }

    static float[] Floats(Matrix4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    static int Link(string vs, string fs)
    {
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, Compile(GLES30.GlVertexShader, vs));
        GLES30.GlAttachShader(p, Compile(GLES30.GlFragmentShader, fs));
        GLES30.GlLinkProgram(p);
        var st = new int[1];
        GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] == 0) Log.Error("MapMaker", "link: " + GLES30.GlGetProgramInfoLog(p));
        return p;
    }

    static int Compile(int type, string src)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src);
        GLES30.GlCompileShader(s);
        var st = new int[1];
        GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] == 0) Log.Error("MapMaker", "compile: " + GLES30.GlGetShaderInfoLog(s));
        return s;
    }
}
