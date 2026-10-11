using System;
using System.IO;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The character + skin picker's pictures (Core Frontend/CharacterPictures.cs, D9): one skin at a time, as the pages ask
// for them -- hd/<skin> loaded on a worker on its hunter's DS rig (CampaignSuit.TryLoad; the rig model looked up here on
// the GL thread), its GL objects warmed a few ms a frame, then posed with the menus' HD Samus clip and frame (MenuSamus:
// its camera's field of view, face hidden and visor pane opaque per suit) and drawn twice into a 4x multisampled
// framebuffer on a clear background: the whole body, and a close-up of the head (the mockup's framings, in the rig's own
// space: it faces -Z, feet on y = 0). Read back, written as PNGs on a worker, its GL objects freed. FrontendRenderer steps
// it on the GL thread, like ImportItemRender.
internal sealed class CharacterPictureRender
{
    const string Tag = "MPHFrontend";
    const int Samples = 4;
    CharacterPictureJob? _job;
    MenuSamus? _recipe;
    Task<CampaignSuit?>? _load;
    CampaignSuit? _model;
    readonly System.Diagnostics.Stopwatch _clock = new();

    // GL thread, every frame the menus are up
    public void Step(int screenW, int screenH)
    {
        try
        {
            if (_job == null)
            {
                if (!CharacterPictures.TryTake(out CharacterPictureJob job)) return;
                _job = job;
                _clock.Restart();
                Model rig = CampaignSuit.HunterModel(job.Rig); // (GL thread only)
                _load = Task.Run(() => CampaignSuit.TryLoad(job.HdRoot, job.Skin, rig, asBuilt: false));
                return;
            }
            if (_load != null)
            {
                if (!_load.IsCompleted) return;
                _model = _load.Result;
                _load = null;
                if (_model == null || !_model.HasBody)
                {
                    Log.Info(Tag, $"character picture: {_job.Skin}: nothing to draw");
                    _model?.DeleteGl();
                    _model = null;
                    End(false);
                    return;
                }
            }
            if (_model == null || !_model.Warm(4)) return;
            CharacterPictureJob done = _job;
            _recipe ??= MenuSamus.Load();
            string[] hide = _recipe.HideFor(done.Skin), opaque = _recipe.OpaqueFor(done.Skin);
            (Vector3 eye, Vector3 target) = HeadCamera(done, _model, _recipe);
            byte[]? full = Render(_model, _recipe, new Vector3(-1.6f, 0.86f, -5.4f), new Vector3(0, 0.84f, 0),
                CharacterPictures.FullW, CharacterPictures.FullH, hide, opaque);
            byte[]? head = full == null ? null : Render(_model, _recipe, eye, target, CharacterPictures.HeadW, CharacterPictures.HeadH, hide, opaque);
            _model.DeleteGl();
            _model = null;
            GLES30.GlViewport(0, 0, screenW, screenH);
            if (full == null || head == null)
            {
                End(false);
                return;
            }
            // a dark skin (Echoes' Dark Samus) lifted, each picture on its own
            float liftFull = CharacterPictures.Lift(full), liftHead = CharacterPictures.Lift(head);
            if (liftFull < 1 || liftHead < 1) Log.Info(Tag, $"character picture: {done.Skin} lifted (gamma {liftFull:0.00} / {liftHead:0.00})");
            Log.Info(Tag, $"character picture: {done.Skin} drawn in {_clock.ElapsedMilliseconds} ms");
            _job = null;
            Task.Run(() => CharacterPictures.Finished(done, Write(done, full, head)));
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "character picture failed (the icon's rim stays): " + ex);
            _model?.DeleteGl();
            _model = null;
            End(false);
        }
    }

    void End(bool ok)
    {
        if (_job != null) CharacterPictures.Finished(_job, ok);
        _job = null;
        _load = null;
    }

    // the head close-up's camera (v2): on the posed head's own middle, facing and size (CharacterPictures.HeadCamera, from
    // the skin's .joints.txt); a skin without head landmarks keeps the mockup's framings: Samus's rig (her suits, Echoes'
    // Dark Samus) from her left, a little above; Corruption's Dark Samus from the front; the troopers on Weavel's rig
    static (Vector3 Eye, Vector3 Target) HeadCamera(CharacterPictureJob job, CampaignSuit model, MenuSamus recipe)
    {
        if (CharacterPictures.HeadLandmarks(Path.Combine(job.HdRoot, job.Skin)) is { } marks
            && model.StillHead(recipe.Clip, recipe.Frame, marks) is { } head && head.Radius > 0)
        {
            return CharacterPictures.HeadCamera(head.Center, head.Facing, head.Radius, recipe.FovY);
        }
        Log.Info(Tag, $"character picture: {job.Skin}: no head landmarks, the mockup's framing");
        if (job.Rig == Hunter.Weavel) return (new Vector3(-0.3f, 1.52f, -1.9f), new Vector3(0.02f, 1.46f, 0));
        if (job.Skin.Equals("MP3DarkSamus", StringComparison.OrdinalIgnoreCase)) return (new Vector3(0, 1.5f, -2.2f), new Vector3(0, 1.45f, 0));
        return (new Vector3(-0.45f, 1.45f, -1.35f), new Vector3(0, 1.42f, 0));
    }

    static byte[]? Render(CampaignSuit model, MenuSamus recipe, Vector3 eye, Vector3 target, int w, int h, string[] hide, string[] opaque)
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
                Log.Warn(Tag, "character picture: framebuffer incomplete");
                return null;
            }
            GLES30.GlViewport(0, 0, w, h);
            GLES30.GlClearColor(0, 0, 0, 0);
            GLES30.GlDepthMask(true);
            GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
            GLES30.GlEnable(GLES30.GlDepthTest);
            Matrix4 view = Matrix4.LookAt(eye, target, Vector3.UnitY);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(recipe.FovY * MathF.PI / 180f, w / (float)h, 0.05f, 100f);
            bool drawn = model.DrawStill(recipe.Clip, recipe.Frame, view, proj, hide, opaque);
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
            // GL's rows run bottom up; the body's own pixels opaque (its blended sheets leave their alpha low), the rest clear
            byte[] px = new byte[raw.Length];
            int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * row, px, y * row, row);
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] == 0 && (px[i] | px[i + 1] | px[i + 2]) == 0) continue;
                px[i + 3] = (byte)Math.Max(px[i + 3], (int)Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])));
            }
            return px;
        }
        finally
        {
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
            GLES30.GlDeleteRenderbuffers(3, rbs, 0);
            GLES30.GlDeleteFramebuffers(2, ids, 0);
        }
    }

    // both pictures, then the skin's older pictures (another stamp or look) deleted
    static bool Write(CharacterPictureJob job, byte[] full, byte[] head)
    {
        try
        {
            string dir = Path.GetDirectoryName(job.Full)!;
            Directory.CreateDirectory(dir);
            foreach ((string path, byte[] px, int w, int h) in new[]
            {
                (job.Full, full, CharacterPictures.FullW, CharacterPictures.FullH), (job.Head, head, CharacterPictures.HeadW, CharacterPictures.HeadH)
            })
            {
                string tmp = path + ".tmp";
                if (!AppPlatform.EncodePng(tmp, px, w, h)) throw new IOException("the PNG encoder failed");
                File.Move(tmp, path, overwrite: true);
            }
            foreach (string old in Directory.GetFiles(dir, job.Skin + "_*.png"))
            {
                if (!string.Equals(old, job.Full, StringComparison.OrdinalIgnoreCase) && !string.Equals(old, job.Head, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(old);
                }
            }
            Log.Info(Tag, $"character picture: cached {job.Full}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "character picture: cache write failed: " + ex.Message);
            return false;
        }
    }
}
