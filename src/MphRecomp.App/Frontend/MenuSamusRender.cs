using System;
using System.IO;
using System.Threading.Tasks;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// The menus' HD Samus backdrop on the device (Core Frontend/MenuSamus.cs; owner 2026-10-03): the recipe's first suit the
// user has (hd/<suit>, Echoes' Varia first) loaded on a worker through the campaign's own HD suit code (CampaignSuit:
// the same GX program and lights; loaded as built, straight on), its GL objects warmed a few ms a frame, then drawn once
// (face hidden, visor pane opaque, per the recipe) into a 4x multisampled framebuffer through the recipe's camera, read
// back and cached as a PNG in <external files>/cache (made from the user's own files; a changed recipe or size renders
// again, older pictures are deleted); the suit's GL objects are freed after. FrontendRenderer steps it on the GL thread
// and hands the picture to FrontendSession.SetHdBackdrop; until then, or without any suit, the DS art stays.
internal sealed class MenuSamusRender
{
    const string Tag = "MPHFrontend";
    const int Samples = 4;
    readonly string _hdRoot, _cacheDir;
    readonly int _w, _h;
    MenuSamus? _recipe;
    string _cache = "";
    Task<byte[]?>? _cached;
    Task<CampaignSuit?>? _load;
    CampaignSuit? _suit;
    readonly System.Diagnostics.Stopwatch _clock = new();

    public bool Done { get; private set; }

    // canvasWidth: the screen's long side; the picture is two DS screens tall at up to 1536 wide
    public MenuSamusRender(string hdRoot, string cacheDir, int canvasWidth)
    {
        _hdRoot = hdRoot;
        _cacheDir = cacheDir;
        _w = Math.Clamp(canvasWidth, 512, 1536) & ~3;
        _h = (int)MathF.Round(_w / MenuSamus.Aspect);
    }

    // GL thread, every frame until Done: the picture (RGBA, top row first) once it is ready, else null
    public (int W, int H, byte[] Rgba)? Step()
    {
        if (Done) return null;
        try
        {
            if (_recipe == null)
            {
                _clock.Start();
                _recipe = MenuSamus.Load();
                string? suit = _recipe.PickSuit(_hdRoot);
                if (suit == null)
                {
                    Log.Info(Tag, $"menu samus: none of the recipe's suits in {_hdRoot}: the DS art stays");
                    Done = true;
                    return null;
                }
                _cache = System.IO.Path.Combine(_cacheDir, _recipe.CacheName(suit, _w, _h));
                if (File.Exists(_cache)) _cached = Task.Run(() => ReadPng(_cache));
                else _load = Load(suit);
                Log.Info(Tag, $"menu samus: {suit} ({(_cached != null ? "cached" : "rendering")} {_w}x{_h})");
                return null;
            }
            if (_cached != null)
            {
                if (!_cached.IsCompleted) return null;
                byte[]? px = _cached.Result;
                _cached = null;
                if (px != null)
                {
                    Done = true;
                    Log.Info(Tag, $"menu samus: cached picture read in {_clock.ElapsedMilliseconds} ms");
                    return (_w, _h, px);
                }
                string suit = _recipe.PickSuit(_hdRoot)!;
                _load = Load(suit); // an unreadable cache: render it again
                return null;
            }
            if (_load != null)
            {
                if (!_load.IsCompleted) return null;
                _suit = _load.Result;
                _load = null;
                if (_suit == null || !_suit.HasBody)
                {
                    Log.Info(Tag, "menu samus: the suit has no body to draw: the DS art stays");
                    Done = true;
                    return null;
                }
            }
            if (_suit != null)
            {
                if (!_suit.Warm(4)) return null; // its program, buffers and textures, a few ms a frame
                Done = true;
                byte[]? px = Render(_suit, _recipe);
                string id = _suit.Id;
                _suit.DeleteGl(); // drawn once: its textures and buffers go (the GX program is CampaignGlCache's)
                _suit = null;
                if (px == null) return null;
                string cache = _cache;
                int w = _w, h = _h;
                Task.Run(() => WritePng(cache, px, w, h));
                Log.Info(Tag, $"menu samus: {id} rendered in {_clock.ElapsedMilliseconds} ms");
                return (_w, _h, px);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "menu samus failed (the DS art stays): " + ex);
            Done = true;
        }
        return null;
    }

    Task<CampaignSuit?> Load(string suit)
    {
        bool built = _recipe!.AsBuilt;
        return Task.Run(() => CampaignSuit.TryLoad(_hdRoot, suit, asBuilt: built));
    }

    byte[]? Render(CampaignSuit suit, MenuSamus recipe)
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
            GLES30.GlRenderbufferStorageMultisample(GLES30.GlRenderbuffer, Samples, GLES30.GlRgba8, _w, _h);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[0]);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[1]);
            GLES30.GlRenderbufferStorageMultisample(GLES30.GlRenderbuffer, Samples, GLES30.GlDepthComponent24, _w, _h);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlDepthAttachment, GLES30.GlRenderbuffer, rbs[1]);
            if (GLES30.GlCheckFramebufferStatus(GLES30.GlFramebuffer) != GLES30.GlFramebufferComplete)
            {
                Log.Warn(Tag, "menu samus: framebuffer incomplete");
                return null;
            }
            GLES30.GlViewport(0, 0, _w, _h);
            GLES30.GlClearColor(0, 0, 0, 1);
            GLES30.GlDepthMask(true);
            GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
            GLES30.GlEnable(GLES30.GlDepthTest);
            bool drawn = suit.DrawStill(recipe.AsBuilt ? -1 : recipe.Clip, recipe.Frame, recipe.View, recipe.Projection,
                recipe.HideFor(suit.Id), recipe.OpaqueFor(suit.Id));
            GLES30.GlDisable(GLES30.GlDepthTest);
            GLES30.GlDisable(GLES30.GlBlend);
            GLES30.GlDepthMask(true);
            if (!drawn) return null;

            // resolve the samples, then read the picture back
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, outFbo);
            GLES30.GlBindRenderbuffer(GLES30.GlRenderbuffer, rbs[2]);
            GLES30.GlRenderbufferStorage(GLES30.GlRenderbuffer, GLES30.GlRgba8, _w, _h);
            GLES30.GlFramebufferRenderbuffer(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlRenderbuffer, rbs[2]);
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, msFbo);
            GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, outFbo);
            GLES30.GlBlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, GLES30.GlColorBufferBit, GLES30.GlNearest);
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, outFbo);
            byte[] raw = new byte[_w * _h * 4];
            GLES30.GlReadPixels(0, 0, _w, _h, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
            // GL's rows run bottom up
            byte[] px = new byte[raw.Length];
            int row = _w * 4;
            for (int y = 0; y < _h; y++) Buffer.BlockCopy(raw, (_h - 1 - y) * row, px, y * row, row);
            return px;
        }
        finally
        {
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
            GLES30.GlDeleteRenderbuffers(3, rbs, 0);
            GLES30.GlDeleteFramebuffers(2, ids, 0);
        }
    }

    static byte[]? ReadPng(string path)
    {
        try
        {
            if (AppPlatform.DecodeImage(path) is not (int[] px, _, _)) return null;
            return System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "menu samus: cache unreadable: " + ex.Message);
            return null;
        }
    }

    static void WritePng(string path, byte[] px, int w, int h)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            if (!AppPlatform.EncodePng(tmp, px, w, h)) throw new IOException("the PNG encoder failed");
            File.Move(tmp, path, overwrite: true);
            Log.Info(Tag, $"menu samus: cached {path}");
            // pictures of an older recipe or another size are never read again
            foreach (string old in Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "menu_samus_*.png"))
            {
                if (old != path) File.Delete(old);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "menu samus: cache write failed: " + ex.Message);
        }
    }
}
