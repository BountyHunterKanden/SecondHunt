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
// a morph ball whole, a little from above. FrontendRenderer steps it on the GL thread, like MenuSamusRender.
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
                _load = Task.Run(() => (CampaignSuit.TryLoadStill(job.Folder, job.Name), job.Ball ? null : Head(job.Folder)));
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
            if (_model == null || !_model.Warm(4)) return;
            ImportRenderJob done = _job;
            (int w, int h) = done.Ball ? (ImportRenders.BallSize, ImportRenders.BallSize) : (ImportRenders.BodyW, ImportRenders.BodyH);
            (Matrix4 view, Matrix4 proj) = Camera(_model, done.Ball, _head, w, h);
            _recipe ??= MenuSamus.Load();
            byte[]? px = Render(_model, view, proj, w, h, _recipe.HideFor(done.Name), _recipe.OpaqueFor(done.Name));
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
            End(false);
        }
    }

    void End(bool ok)
    {
        if (_job != null) ImportRenders.Finished(_job, ok);
        _job = null;
        _load = null;
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
        float tan = MathF.Tan(FovY * MathF.PI / 360f);
        Vector3 target, eye;
        float dist;
        if (ball)
        {
            target = (min + max) / 2;
            Vector3 size = max - min;
            float r = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) / 2; // as wide as the ball (CampaignSuit's _ballWidth)
            dist = r / tan / 0.9f;
            eye = target + new Vector3(0, 0.34f, 0.94f) * dist;
        }
        else
        {
            float height = MathF.Max(max.Y - min.Y, 1e-3f);
            dist = 1.83f * height;
            float y = head is Vector3 c ? c.Y - (1 - 2 * HeadRow) * (dist - c.Z) * tan : min.Y + 0.81f * height;
            target = new Vector3(0, y, 0); // centred on the model's own axis (an arm cannon makes the box lopsided)
            eye = target + new Vector3(0, 0, dist);
        }
        Matrix4 view = Matrix4.LookAt(eye, target, Vector3.UnitY);
        Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(FovY * MathF.PI / 180f, w / (float)h, dist * 0.05f, dist * 4f);
        return (view, proj);
    }

    static byte[]? Render(CampaignSuit model, Matrix4 view, Matrix4 proj, int w, int h, string[] hide, string[] opaque)
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
            GLES30.GlClearColor(0, 0, 0, 1);
            GLES30.GlDepthMask(true);
            GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
            GLES30.GlEnable(GLES30.GlDepthTest);
            bool drawn = model.DrawStillModel(view, proj, hide, opaque);
            GLES30.GlDisable(GLES30.GlDepthTest);
            GLES30.GlDisable(GLES30.GlBlend);
            GLES30.GlDepthMask(true);
            if (!drawn) return null;

            // resolve the samples, then read the picture back
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, outFbo);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[2]);
            GLES30.GlRenderbufferStorage(GLES30.GlRenderbuffer, GLES30.GlRgba8, w, h);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[2]);
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, msFbo);
            GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, outFbo);
            GLES30.GlBlitFramebuffer(0, 0, w, h, 0, 0, w, h, GLES30.GlColorBufferBit, GLES30.GlNearest);
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, outFbo);
            byte[] raw = new byte[w * h * 4];
            GLES30.GlReadPixels(0, 0, w, h, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
            // GL's rows run bottom up; the picture is opaque
            byte[] px = new byte[raw.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * row, px, y * row, row);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return px;
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
