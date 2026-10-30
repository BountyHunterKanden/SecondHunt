using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// IMPORT GAMES' pictures of imported models (Core Frontend/ImportRenders.cs; owner 2026-10-08: "do the disc banners and
// renders"): one job at a time, as the pages ask for them -- the model's folder loaded on a worker as it was built
// (CampaignSuit.TryLoadStill: no rig, every corner on bone 0, the campaign suit's GX programs and light), its GL objects
// warmed a few ms a frame, then drawn once into a 4x multisampled framebuffer, read back and written as a PNG on a worker;
// its GL objects are freed after. A body is framed head to waist from the front (a tall picture the tiles and cards crop),
// a morph ball whole, a little from above. A Scout Drone (job.Native: bodies/<name>, a native body.bin and no .dae) is
// loaded as a match draws it (CampaignRetroBody), held in its idle clip's first frame and framed whole like a ball.
// FrontendRenderer steps it on the GL thread, like MenuSamusRender.
internal sealed class ImportItemRender
{
    const string Tag = "MPHFrontend";
    const int Samples = 4;
    const float FovY = 22f;
    // a body's head (the middle of it) this far down its picture: the tiles show rows 78-236 of 576, so a helmet sits at
    // the top of the tile with the shoulders under it (owner 2026-10-08: the busts sat too low, and the pirates' heads,
    // well under their spikes and crests, fell out of the tile)
    const float HeadRow = 160f / 576;
    ImportRenderJob? _job;
    MenuSamus? _recipe; // the menus' HD Samus recipe: per suit, its face (hidden) and visor pane (opaque)
    Task<(CampaignSuit? Model, Vector3? Head)>? _load;
    CampaignSuit? _model;
    Vector3? _head;
    Task<(CampaignRetroBody? Body, (Vector3 Min, Vector3 Max)? Box)>? _nativeLoad;
    CampaignRetroBody? _native;
    (Vector3 Min, Vector3 Max) _nativeBox;
    readonly System.Diagnostics.Stopwatch _clock = new();

    // GL thread, every frame the menus are up
    public void Step(int screenW, int screenH)
    {
        try
        {
            if (_job == null)
            {
                if (!ImportRenders.TryTake(out ImportRenderJob job)) return;
                _job = job;
                _clock.Restart();
                if (job.Native)
                {
                    _nativeLoad = Task.Run(() => LoadNative(job));
                    return;
                }
                _load = Task.Run(() => (CampaignSuit.TryLoadStill(job.Folder, job.Name, job.Ball), job.Ball ? null : Head(job.Folder)));
                return;
            }
            if (_load != null)
            {
                if (!_load.IsCompleted) return;
                (_model, _head) = _load.Result;
                _load = null;
                if (_model == null)
                {
                    Log.Info(Tag, $"import render: {_job.Name}: nothing to draw");
                    End(false);
                    return;
                }
            }
            if (_nativeLoad != null || _native != null)
            {
                StepNative(screenW, screenH);
                return;
            }
            if (_model == null || !_model.Warm(4)) return;
            ImportRenderJob done = _job;
            (int w, int h) = done.Ball ? (ImportRenders.BallSize, ImportRenders.BallSize) : (ImportRenders.BodyW, ImportRenders.BodyH);
            (Matrix4 view, Matrix4 proj) = Camera(_model, done.Ball, _head, w, h);
            _recipe ??= MenuSamus.Load();
            CampaignSuit model = _model;
            byte[]? px = Render(() => model.DrawStillModel(view, proj, _recipe.HideFor(done.Name), _recipe.OpaqueFor(done.Name)), w, h);
            _model.DeleteGl();
            _model = null;
            GLES30.GlViewport(0, 0, screenW, screenH);
            if (px == null)
            {
                End(false);
                return;
            }
            Log.Info(Tag, $"import render: {done.Name} drawn in {_clock.ElapsedMilliseconds} ms");
            _job = null;
            Task.Run(() => ImportRenders.Finished(done, WritePng(done, px, w, h)));
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "import render failed (the pictogram stays): " + ex);
            _model?.DeleteGl();
            _model = null;
            _native?.DeleteGl();
            _native = null;
            End(false);
        }
    }

    void End(bool ok)
    {
        if (_job != null) ImportRenders.Finished(_job, ok);
        _job = null;
        _load = null;
        _nativeLoad = null;
    }

    // a worker: a Scout Drone's body (bodies/<name>) and its idle pose's box; null body = nothing to draw
    static (CampaignRetroBody?, (Vector3, Vector3)?) LoadNative(ImportRenderJob job)
    {
        CampaignRetroBody? body = CampaignRetroBody.TryLoad(Path.GetDirectoryName(job.Folder)!, job.Name);
        return body == null ? (null, null) : (body, body.StillBox());
    }

    // GL thread: a native body's job, as Step does a suit's (loaded, warmed a few ms a frame, drawn, freed)
    void StepNative(int screenW, int screenH)
    {
        ImportRenderJob done = _job!;
        if (_nativeLoad != null)
        {
            if (!_nativeLoad.IsCompleted) return;
            (CampaignRetroBody? body, (Vector3 Min, Vector3 Max)? box) = _nativeLoad.Result;
            _nativeLoad = null;
            if (body == null || box == null)
            {
                Log.Info(Tag, $"import render: {done.Name}: nothing to draw");
                End(false);
                return;
            }
            (_native, _nativeBox) = (body, box.Value);
        }
        CampaignRetroBody native = _native!;
        if (native.Failed)
        {
            _native = null;
            End(false);
            return;
        }
        if (!native.Warm(4)) return;
        int size = ImportRenders.BallSize;
        (Matrix4 view, Matrix4 proj) = Whole(_nativeBox.Min, _nativeBox.Max, size, size, 0.98f);
        byte[]? px = Render(() => native.DrawStill(view, proj), size, size);
        // the posed box is looser than what is drawn (owner 2026-10-26: "still too small"): measure the drawn picture and
        // frame it again, centred and filling the picture
        if (px != null && Drawn(px, size) is (int X0, int Y0, int X1, int Y1))
        {
            float tan = MathF.Tan(FovY * MathF.PI / 360f);
            Matrix4 cam = Matrix4.Invert(view);
            Vector3 right = cam.Row0.Xyz, up = cam.Row1.Xyz, eye = cam.Row3.Xyz;
            Vector3 centre = (_nativeBox.Min + _nativeBox.Max) / 2;
            float dist = (eye - centre).Length, unit = 2 * dist * tan / size; // world units per picture pixel at the model
            Vector3 shift = right * (((X0 + X1) / 2f - size / 2f) * unit) - up * (((Y0 + Y1) / 2f - size / 2f) * unit);
            float scale = MathF.Max(X1 - X0, Y1 - Y0) / (size * 0.94f);
            (view, proj) = Whole(_nativeBox.Min, _nativeBox.Max, size, size, 0.98f, 0f, shift, scale);
            byte[]? again = Render(() => native.DrawStill(view, proj), size, size);
            if (again != null) px = again;
        }
        native.DeleteGl();
        _native = null;
        GLES30.GlViewport(0, 0, screenW, screenH);
        if (px == null)
        {
            End(false);
            return;
        }
        Log.Info(Tag, $"import render: {done.Name} drawn in {_clock.ElapsedMilliseconds} ms");
        _job = null;
        Task.Run(() => ImportRenders.Finished(done, WritePng(done, px, size, size)));
    }

    // the middle of a body's head in DAE space: Head_1.center (else Head_1) of the .joints.txt the importer writes beside
    // its .dae (TrophyLandmarks); null without one
    static Vector3? Head(string folder)
    {
        try
        {
            string? dae = Directory.EnumerateFiles(folder, "*.dae").FirstOrDefault();
            string? file = dae == null ? null : MphRecomp.Anim.TrophyLandmarks.PathFor(dae);
            if (file == null || !File.Exists(file)) return null;
            var joints = MphRecomp.Anim.TrophyLandmarks.Load(file).Joints;
            if (joints.TryGetValue("Head_1.center", out Vector3 c) || joints.TryGetValue("Head_1", out c)) return c;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.Warn(Tag, $"import render: {folder}: no head ({ex.Message})");
        }
        return null;
    }

    // the model faces +Z, Y up, feet on y = 0 (DAE space). A body: the mockups' bust framing (-femenusamus --eye 0,1.42,-3.2
    // --fov 22 on her rig: 1.83 heights away, on her axis, level), head and shoulders at the top of a 2:3 picture, down to the
    // waist -- the eye at the height that puts its head's middle HeadRow down the picture (without a head: 0.81 of its height,
    // the mockups' eye). A ball: all of it, nine tenths of the picture, seen from in front and a little above.
    static (Matrix4 View, Matrix4 Proj) Camera(CampaignSuit m, bool ball, Vector3? head, int w, int h)
    {
        Vector3 min = m.StillMin, max = m.StillMax;
        if (ball) return Whole(min, max, w, h, 0.9f, BallYaw);
        float tan = MathF.Tan(FovY * MathF.PI / 360f);
        float height = MathF.Max(max.Y - min.Y, 1e-3f);
        float dist = 1.83f * height;
        float y = head is Vector3 c ? c.Y - (1 - 2 * HeadRow) * (dist - c.Z) * tan : min.Y + 0.81f * height;
        Vector3 target = new Vector3(0, y, 0); // centred on the model's own axis (an arm cannon makes the box lopsided)
        Vector3 eye = target + new Vector3(0, 0, dist);
        Matrix4 view = Matrix4.LookAt(eye, target, Vector3.UnitY);
        Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(FovY * MathF.PI / 180f, w / (float)h, dist * 0.05f, dist * 4f);
        return (view, proj);
    }

    // a ball seen turned about its up axis, as the placeholder pictogram draws it (its near half's outer plate foreshortened to
    // one side, the gap between the halves slanting, the core glowing in it), not square on (owner 2026-10-26)
    const float BallYaw = 0.55f;

    // a whole model in its box (a ball, a drone): nine tenths of the picture, from in front (+Z) and a little above
    static (Matrix4 View, Matrix4 Proj) Whole(Vector3 min, Vector3 max, int w, int h, float fill = 0.9f, float yaw = 0f, Vector3 shift = default, float scale = 1f)
    {
        float tan = MathF.Tan(FovY * MathF.PI / 360f);
        Vector3 target = (min + max) / 2 + shift;
        Vector3 size = max - min;
        float r = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 2; // as wide as the ball (CampaignSuit's _ballWidth)
        float dist = MathF.Max(r, 1e-3f) / tan / fill * scale;
        Vector3 eye = target + Vector3.TransformVector(new Vector3(0, 0.34f, 0.94f), Matrix4.CreateRotationY(yaw)) * dist;
        Matrix4 view = Matrix4.LookAt(eye, target, Vector3.UnitY);
        Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(FovY * MathF.PI / 180f, w / (float)h, dist * 0.05f, dist * 4f);
        return (view, proj);
    }

    // the box of the pixels a picture has drawn (anything not see-through); null when it is empty
    static (int X0, int Y0, int X1, int Y1)? Drawn(byte[] px, int size)
    {
        int x0 = size, y0 = size, x1 = -1, y1 = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                if (px[i + 3] <= 12) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        return x1 < x0 ? null : (x0, y0, x1 + 1, y1 + 1);
    }

    // draw: the model's still into the bound framebuffer (depth test on), false when it failed
    static byte[]? Render(Func<bool> draw, int w, int h)
    {
        var ids = new int[2];
        GLES30.GlGenFramebuffers(2, ids, 0);
        int msFbo = ids[0], outFbo = ids[1];
        var rbs = new int[3];
        GLES30.GlGenRenderbuffers(3, rbs, 0);
        try
        {
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, msFbo);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[0]);
            GLES30.GlRenderbufferStorageMultisample(GLES30.GlRenderbuffer, Samples, GLES30.GlRgba8, w, h);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[0]);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[1]);
            GLES30.GlRenderbufferStorageMultisample(GLES30.GlRenderbuffer, Samples, GLES30.GlDepthComponent24, w, h);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlDepthAttachment, GLES30.GlRenderbuffer, rbs[1]);
            if (GLES30.GlCheckFramebufferStatus(GLES30.GlFramebuffer) != GLES30.GlFramebufferComplete)
            {
                Log.Warn(Tag, "import render: framebuffer incomplete");
                return null;
            }
            GLES30.GlViewport(0, 0, w, h);
            // drawn twice, on white and on black: what differs between the two is the ground showing through, so the
            // picture keeps the model only, see-through elsewhere (owner 2026-10-30: the square pictures sat in black
            // boxes on the lighter IMPORT GAMES tiles)
            bool resolveReady = false, premultiplied = OperatingSystem.IsAndroid();
            byte[]? onWhite = Pass(1), onBlack = onWhite != null ? Pass(0) : null;
            if (onWhite == null || onBlack == null) return null;
            // GL's rows run bottom up
            byte[] px = new byte[onBlack.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++)
            {
                int src = (h - 1 - y) * row, dst = y * row;
                for (int x = 0; x < row; x += 4)
                {
                    int ground = 0;
                    for (int c = 0; c < 3; c++) ground = Math.Max(ground, onWhite[src + x + c] - onBlack[src + x + c]);
                    int a = Math.Clamp(255 - ground, 0, 255);
                    if (a == 0) continue;
                    // the colour over black is the model's times its cover: Android's PNG encoder takes it so (a Bitmap
                    // holds premultiplied pixels), the desktop's wants the colour itself
                    for (int c = 0; c < 3; c++)
                    {
                        int v = Math.Min(onBlack[src + x + c], a);
                        px[dst + x + c] = (byte)(premultiplied ? v : v * 255 / a);
                    }
                    px[dst + x + 3] = (byte)a;
                }
            }
            return px;

            byte[]? Pass(float ground)
            {
                GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, msFbo);
                GLES30.GlClearColor(ground, ground, ground, 1);
                GLES30.GlDepthMask(true);
                GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
                GLES30.GlEnable(GLES30.GlDepthTest);
                bool drawn = draw();
                GLES30.GlDisable(GLES30.GlDepthTest);
                GLES30.GlDisable(GLES30.GlBlend);
                GLES30.GlDepthMask(true);
                if (!drawn) return null;

                // resolve the samples, then read the picture back
                GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, outFbo);
                if (!resolveReady)
                {
                    GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[2]);
                    GLES30.GlRenderbufferStorage(GLES30.GlRenderbuffer, GLES30.GlRgba8, w, h);
                    GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[2]);
                    resolveReady = true;
                }
                GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, msFbo);
                GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, outFbo);
                GLES30.GlBlitFramebuffer(0, 0, w, h, 0, 0, w, h, GLES30.GlColorBufferBit, GLES30.GlNearest);
                GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, outFbo);
                byte[] raw = new byte[w * h * 4];
                GLES30.GlReadPixels(0, 0, w, h, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
                return raw;
            }
        }
        finally
        {
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
            GLES30.GlDeleteRenderbuffers(3, rbs, 0);
            GLES30.GlDeleteFramebuffers(2, ids, 0);
        }
    }

    // the picture, then the model's older pictures (another stamp) deleted
    static bool WritePng(ImportRenderJob job, byte[] px, int w, int h)
    {
        try
        {
            string dir = Path.GetDirectoryName(job.Png)!;
            Directory.CreateDirectory(dir);
            string tmp = job.Png + ".tmp";
            if (!AppPlatform.EncodePng(tmp, px, w, h)) throw new IOException("the PNG encoder failed");
            File.Move(tmp, job.Png, overwrite: true);
            foreach (string old in Directory.GetFiles(dir, job.Name + "_*.png"))
            {
                if (!string.Equals(old, job.Png, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
            }
            Log.Info(Tag, $"import render: cached {job.Png}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "import render: cache write failed: " + ex.Message);
            return false;
        }
    }
}
