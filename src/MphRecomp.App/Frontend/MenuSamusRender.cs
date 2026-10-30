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
// and hands the picture to FrontendSession.SetHdBackdrop; until then, or without any suit, the DS art stays. A DS body
// picked as the menu art (the low-poly Dark Samus, "ds:<hunter>": StandinPictures.MenuArtOf; owner 2026-10-29) is drawn
// from the game's own DS model on the CPU instead (StandinPictures.MenuArt: loaded here, drawn on a worker) and cached
// the same way.
internal sealed class MenuSamusRender
{
    const string Tag = "MPHFrontend";
    const int Samples = 4;
    readonly string _hdRoot, _cacheDir;
    readonly string? _want; // RECOMP SETTINGS "menu art"'s suit (RecompSettings.MenuArtSuit: "", "shuffle" or an id)
    readonly int _w, _h;
    MenuSamus? _recipe;
    string _cache = "";
    Task<byte[]?>? _cached;
    Task<CampaignSuit?>? _load;
    Task<byte[]?>? _ds;
    // a DS body's picture is drawn at most this wide (its CPU raster's buffers) and stretched to the picture's size
    const int DsMaxW = 768;
    CampaignSuit? _suit;
    string? _suitId;
    readonly System.Diagnostics.Stopwatch _clock = new();

    public bool Done { get; private set; }

    // canvasWidth: the screen's long side; the picture is two DS screens tall at up to 1536 wide
    public MenuSamusRender(string hdRoot, string cacheDir, int canvasWidth, string? want = null)
    {
        _hdRoot = hdRoot;
        _cacheDir = cacheDir;
        _want = want;
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
                string lastFile = System.IO.Path.Combine(_cacheDir, "menu_samus_last.txt");
                string? last = null;
                try { if (File.Exists(lastFile)) last = File.ReadAllText(lastFile).Trim(); } catch (IOException) { }
                string? suit = _recipe.PickSuit(_hdRoot, _want, last);
                _suitId = suit;
                if (suit != null && suit != last)
                {
                    try { Directory.CreateDirectory(_cacheDir); File.WriteAllText(lastFile, suit); } catch (IOException) { }
                }
                if (suit == null)
                {
                    Log.Info(Tag, $"menu samus: none of the recipe's suits in {_hdRoot}: the DS art stays");
                    Done = true;
                    return null;
                }
                _cache = System.IO.Path.Combine(_cacheDir, _recipe.CacheName(CacheId(suit), _w, _h));
                if (File.Exists(_cache)) _cached = Task.Run(() => ReadPng(_cache));
                else if (!Start(suit)) return null;
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
                Start(_suitId!); // an unreadable cache: render it again
                return null;
            }
            if (_ds != null)
            {
                if (!_ds.IsCompleted) return null;
                byte[]? px = _ds.Result;
                _ds = null;
                Done = true;
                if (px == null)
                {
                    Log.Warn(Tag, $"menu samus: {_suitId} (DS body) drew nothing: the DS art stays");
                    return null;
                }
                string cache = _cache;
                int w = _w, h = _h;
                Task.Run(() => WritePng(cache, px, w, h));
                Log.Info(Tag, $"menu samus: {_suitId} (DS body) drawn in {_clock.ElapsedMilliseconds} ms");
                return (_w, _h, px);
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
                if (Blank(px))
                {
                    // nothing drawn (a broken GL state): not cached, so the next start draws it again
                    Log.Warn(Tag, $"menu samus: {id} drew nothing: the DS art stays, not cached");
                    return null;
                }
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

    // a DS body's id keeps its framing's stamp in the cache name (and no ':' in a file name)
    static string CacheId(string suit) => StandinPictures.MenuArtOf(suit) != null
        ? suit.Replace(':', '_') + "-" + StandinPictures.MenuArtStamp : suit;

    // GL thread: start drawing `suit` (an HD suit on a worker, or a DS body: its model loaded here, MphRead's loader being
    // this thread's, drawn on a worker); false when it can't be (Done: the DS art stays)
    bool Start(string suit)
    {
        if (StandinPictures.MenuArtOf(suit) == null)
        {
            _load = Load(suit);
            return true;
        }
        MphRead.Model? model = StandinPictures.LoadMenuArt(suit);
        if (model == null)
        {
            Log.Info(Tag, $"menu samus: {suit}: no DS model: the DS art stays");
            Done = true;
            return false;
        }
        int w = _w, h = _h;
        _ds = Task.Run(() => DrawDs(model, suit, w, h));
        return true;
    }

    static byte[]? DrawDs(MphRead.Model model, string id, int w, int h)
    {
        try
        {
            int dw = Math.Min(w, DsMaxW) & ~3, dh = (int)MathF.Round(dw / MenuSamus.Aspect);
            byte[] px = StandinPictures.MenuArt(model, id, dw, dh);
            return dw == w && dh == h ? px : Stretch(px, dw, dh, w, h);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"menu samus: {id} (DS body) failed: " + ex);
            return null;
        }
    }

    // bilinear, RGBA
    static byte[] Stretch(byte[] src, int sw, int sh, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            float fy = Math.Clamp((y + 0.5f) * sh / h - 0.5f, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            float ty = fy - y0;
            for (int x = 0; x < w; x++)
            {
                float fx = Math.Clamp((x + 0.5f) * sw / w - 0.5f, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                float tx = fx - x0;
                int a = (y0 * sw + x0) * 4, b = (y0 * sw + x1) * 4, c = (y1 * sw + x0) * 4, d = (y1 * sw + x1) * 4, o = (y * w + x) * 4;
                for (int k = 0; k < 4; k++)
                {
                    float top = src[a + k] + (src[b + k] - src[a + k]) * tx, bottom = src[c + k] + (src[d + k] - src[c + k]) * tx;
                    dst[o + k] = (byte)(top + (bottom - top) * ty + 0.5f);
                }
            }
        }
        return dst;
    }

    Task<CampaignSuit?> Load(string suit)
    {
        bool built = _recipe!.AsBuilt;
        var stand = _recipe.StandFor(suit);
        return Task.Run(() => CampaignSuit.TryLoad(_hdRoot, suit, asBuilt: built, stand: stand));
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
            bool drawn = suit.DrawStill(recipe.AsBuilt ? -1 : recipe.Clip, recipe.Frame, recipe.ViewFor(suit.Id), recipe.Projection,
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

    // under 1 in 1000 pixels lit on the black clear: no suit there
    static bool Blank(byte[] px)
    {
        int lit = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i] + px[i + 1] + px[i + 2] > 24) lit++;
        return lit < px.Length / 4 / 1000;
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
            // pictures of an older recipe or another size are never read again (other suits' stay: the "menu art" picker
            // and its shuffle switch between them)
            string keep = path[(path.LastIndexOf('_', path.LastIndexOf('_') - 1))..];
            foreach (string old in Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "menu_samus_*.png"))
            {
                if (!old.EndsWith(keep, StringComparison.Ordinal)) File.Delete(old);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "menu samus: cache write failed: " + ex.Message);
        }
    }
}
