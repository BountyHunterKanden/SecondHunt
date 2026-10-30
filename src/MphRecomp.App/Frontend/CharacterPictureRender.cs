using System;
using System.IO;
using System.Threading.Tasks;
using MphRead;
using MphRecomp.Frontend;
using OpenTK.Mathematics;
using HunterColours = MphRecomp.Multiplayer.HunterColours;
using LookAlikes = MphRecomp.Multiplayer.LookAlikes;
using MatchPlayer = MphRecomp.Multiplayer.MatchPlayer;
using SuitColours = MphRecomp.Multiplayer.SuitColours;
using BeyondSyluxColours = MphRecomp.Multiplayer.BeyondSyluxColours;

namespace MphRecomp.App;

// The character + skin picker's pictures (Core Frontend/CharacterPictures.cs, D9): one skin at a time, as the pages ask
// for them -- hd/<skin> loaded on a worker on its hunter's DS rig (CampaignSuit.TryLoad; the rig model looked up here on
// the GL thread), its GL objects warmed a few ms a frame, then posed with the menus' HD Samus clip and frame (MenuSamus:
// its camera's field of view, face hidden and visor pane opaque per suit) and drawn twice into a 4x multisampled
// framebuffer on a clear background: the whole body, and a close-up of the head (the mockup's framings, in the rig's own
// space: it faces -Z, feet on y = 0). Read back, written as PNGs on a worker, its GL objects freed. FrontendRenderer steps
// it on the GL thread, like ImportItemRender. A stand-in skin (no import: the trooper look-alikes, the low-poly Dark
// Samus) is drawn from its DS model on the CPU instead (StandinPictures: loaded here, drawn on a worker). A lobby colour's
// picture (CharacterPictures.ForColour, the colour picker) is the same pose in that seat colour: an HD body with the
// match's seat tints (as MatchSkins), a DS body (stand-in or vanilla hunter) in its recolour.
internal sealed class CharacterPictureRender
{
    const string Tag = "MPHFrontend";
    const int Samples = 4;
    CharacterPictureJob? _job;
    MenuSamus? _recipe;
    Task<CampaignSuit?>? _load;
    Task<(byte[] Full, byte[] Head)>? _standin;
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
                if (job.DsModel != null)
                {
                    Model? cm = ColourModel(job, out int rc);
                    if (cm == null)
                    {
                        Log.Info(Tag, $"character picture: {job.Key}: no recolour {job.Copy} on {job.DsModel}");
                        End(false);
                        return;
                    }
                    _standin = Task.Run(() => StandinPictures.DrawColour(cm, job.Rig, job.Float, rc));
                    return;
                }
                if (job.Standin)
                {
                    Model? ds = StandinPictures.Load(job.Skin);
                    if (ds == null)
                    {
                        Log.Info(Tag, $"character picture: {job.Skin}: no stand-in model");
                        End(false);
                        return;
                    }
                    _standin = Task.Run(() => StandinPictures.Draw(ds, job.Skin));
                    return;
                }
                Model rig = CampaignSuit.HunterModel(job.Rig); // (GL thread only)
                _load = Task.Run(() => CampaignSuit.TryLoad(job.HdRoot, job.Skin, rig, asBuilt: false));
                return;
            }
            if (_standin != null)
            {
                if (!_standin.IsCompleted) return;
                (byte[] sFull, byte[] sHead) = _standin.Result; // (a failed draw throws here: the rim stays)
                _standin = null;
                CharacterPictureJob drawn = _job;
                Log.Info(Tag, $"character picture: {drawn.Key} (DS model) drawn in {_clock.ElapsedMilliseconds} ms");
                _job = null;
                Task.Run(() => CharacterPictures.Finished(drawn, Write(drawn, sFull, sHead)));
                return;
            }
            if (_load != null)
            {
                if (!_load.IsCompleted) return;
                _model = _load.Result;
                _load = null;
                if (_model == null || !_model.HasBody)
                {
                    Log.Info(Tag, $"character picture: {_job.Key}: nothing to draw");
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
            if (done.Copy > 0 && done.Seat != null) ColourTints(_model, done.Seat, done.Skin, CharacterPictures.VariantOf(done.Copy));
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
            if (liftFull < 1 || liftHead < 1) Log.Info(Tag, $"character picture: {done.Key} lifted (gamma {liftFull:0.00} / {liftHead:0.00})");
            Log.Info(Tag, $"character picture: {done.Key} drawn in {_clock.ElapsedMilliseconds} ms");
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
        _standin = null;
    }

    // a colour job's DS body: a private copy of the model with that colour's recolour built on (LookAlikes' set for a
    // stand-in's look, HunterColours' 5th-8th for a vanilla hunter; MPH's own 1st-4th are its recolours 0-3), or null
    static Model? ColourModel(CharacterPictureJob job, out int recolor)
    {
        recolor = -1;
        if (job.Float && !MphRecomp.Multiplayer.DarkSamusStandin.Available) return null;
        LookAlikes.Variant v = CharacterPictures.VariantOf(job.Copy);
        if (job.DsLook != LookAlikes.Look.None) LookAlikes.Register(new[] { (job.DsLook, v) });
        else HunterColours.Install();
        Model m = Read.GetModelInstance(job.DsModel!, false, MetaDir.Models, noCache: true).Model;
        recolor = job.DsLook != LookAlikes.Look.None ? LookAlikes.RecolorFor(job.DsLook, v, job.DsModel!)
            : job.Copy < HunterColours.Copies ? job.Copy : HunterColours.RecolorFor(job.DsModel!, job.Copy - HunterColours.Copies);
        return recolor >= 0 && recolor < m.Recolors.Count ? m : null;
    }

    // an HD body's seat colour, as a match draws it (MatchSkins' SeatTint / SeatTint2 / SuitTint, mirrored): a Trooper or
    // Dark Samus body's tint and lights colour, Prime 4 Sylux's own copy colours, a Samus suit's SuitColours
    static void ColourTints(CampaignSuit s, MatchPlayer seat, string skin, LookAlikes.Variant v)
    {
        LookAlikes.Look kind = LookAlikes.For(seat);
        Vector4 tint, tint2;
        if (kind == LookAlikes.Look.None)
        {
            bool sylux = BeyondSyluxColours.Is(skin);
            tint = sylux && BeyondSyluxColours.Tint(v) is { } st ? new Vector4(st.R, st.G, st.B, st.W) : Vector4.Zero;
            var sl = sylux ? BeyondSyluxColours.Light(v) : (0f, 0f, 0f);
            tint2 = new Vector4(sl.Item1, sl.Item2, sl.Item3, sylux ? BeyondSyluxColours.LightHue(v) : 0);
        }
        else
        {
            tint = LookAlikes.BodyTint(kind == LookAlikes.Look.DarkSamus, v) is { } t ? new Vector4(t.R, t.G, t.B, t.W) : Vector4.Zero;
            var l = LookAlikes.BodyLight(kind == LookAlikes.Look.DarkSamus, v);
            tint2 = new Vector4(l.R, l.G, l.B, kind == LookAlikes.Look.DarkSamus ? 0 : -1);
        }
        if (kind == LookAlikes.Look.None && seat.Hunter == Hunter.Samus && SuitColours.IsSuit(skin) && SuitColours.For(skin, v) is { } c)
        {
            (tint, tint2) = (new Vector4(c.Tint.R, c.Tint.G, c.Tint.B, c.Tint.W), new Vector4(c.Second.R, c.Second.G, c.Second.B, 0));
            s.SeatTint3 = new Vector4(c.Lights.R, c.Lights.G, c.Lights.B, c.Lights.ChestRef);
            s.SeatHue = new Vector4(c.Hue.Main, c.Hue.Second, c.Hue.Third, c.Hue.Lights);
            s.SeatRef = new Vector4(c.Ref.Main, c.Ref.Second, c.Ref.Grey, c.Ref.Greys);
            s.SeatChest = new Vector4(c.Chest.R, c.Chest.G, c.Chest.B, c.Chest.Hue);
        }
        (s.SeatTint, s.SeatTint2) = (tint, tint2);
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
            foreach (string old in Directory.GetFiles(dir, job.Key + "_*.png"))
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
