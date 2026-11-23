using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using MphRecomp.Config;

namespace MphRecomp.App;

// The custom HUD (owner 2026-11-15, wave 2): DrawHud's items moved, sized, faded or hidden per HUD element
// (Hud/HudLayout.cs holds the edits, Hud/HudEditor.cs edits them over the paused game). MphRead tags which items belong to
// which element (Scene.HostHudTag, recorded while Scene.HostHudElementsOn); here each element gets one transform a frame:
// size about its shipped centre, then that centre moved to its stored place. The reticle only sizes, about its own
// centre, and the aim view still moves it after (LookReticleShift). Display only: nothing in the game reads any of it, so
// replays and matches play the same. Dev builds only for now (RecompSettings.PublicBuild: no tags, no edits).
internal sealed partial class CampaignRenderer
{
    readonly HudLayout _hudLayout = new();
    bool _hudLayoutLoaded, _hudLayoutOn;
    Hunter? _hudHomeHunter;
    bool _hudHomePrime;
    int[] _hudItemElement = new int[256]; // HUD item -> element index this frame (-1: none)
    int _hudItemElements;
    readonly List<string> _hudFrameIds = new();
    readonly List<HudLayout.Box> _hudFrameBoxes = new();
    // per element index: scale, the centre it scales about, where that centre goes (pixels), opacity, hidden
    float[] _hudElScale = new float[32], _hudElHx = new float[32], _hudElHy = new float[32], _hudElTx = new float[32],
        _hudElTy = new float[32], _hudElAlpha = new float[32];
    bool[] _hudElHidden = new bool[32];

    // DrawHud's start, its HUD program bound: this frame's element transforms (false: nothing to change, draw as recorded)
    bool HudLayoutPrepare(Scene scene)
    {
        Scene.HostHudElementsOn = !RecompSettings.PublicBuild;
        _hudLayoutOn = false;
        if (!Scene.HostHudElementsOn) return false;
        HudLayout layout = _hudLayout;
        if (PauseMenu != null) PauseMenu.HudHost ??= layout;
        if (!_hudLayoutLoaded)
        {
            _hudLayoutLoaded = true;
            try
            {
                layout.Load(RecompSettings.Load(SettingsPath));
            }
            catch (Exception ex)
            {
                Log.Warn("MPHHud", "layout: " + ex.Message);
            }
        }
        Hunter? hunter = _host?.Player?.Hunter;
        if (layout.Width != _width || layout.Height != _height || hunter != _hudHomeHunter || PlayerEntity.HostPrimeHud != _hudHomePrime)
        {
            layout.Homes.Clear();
            layout.Order.Clear();
            layout.Width = _width;
            layout.Height = _height;
            _hudHomeHunter = hunter;
            _hudHomePrime = PlayerEntity.HostPrimeHud;
        }
        int items = scene.HudItemCount;
        if (_hudItemElement.Length < items) Array.Resize(ref _hudItemElement, Math.Max(items, _hudItemElement.Length * 2));
        Array.Fill(_hudItemElement, -1, 0, items);
        _hudItemElements = items;
        _hudFrameIds.Clear();
        _hudFrameBoxes.Clear();
        layout.Live.Clear();
        float w = _width, h = _height;
        int ranges = scene.HudElementRangeCount;
        for (int r = 0; r < ranges; r++)
        {
            Scene.HostHudElementRange range = scene.GetHudElementRange(r);
            int e = _hudFrameIds.IndexOf(range.Id);
            if (e < 0)
            {
                e = _hudFrameIds.Count;
                _hudFrameIds.Add(range.Id);
                _hudFrameBoxes.Add(new HudLayout.Box { Left = float.MaxValue, Top = float.MaxValue, Right = float.MinValue, Bottom = float.MinValue });
            }
            HudLayout.Box box = _hudFrameBoxes[e];
            int end = Math.Min(range.End, items);
            for (int i = range.Start; i < end; i++)
            {
                _hudItemElement[i] = e;
                Scene.HostHudItem item = scene.GetHudItem(i);
                // NDC (Y up) to pixels (Y down)
                float l = (item.Left + 1) / 2 * w, rt = (item.Right + 1) / 2 * w;
                float t = (1 - item.Top) / 2 * h, b = (1 - item.Bottom) / 2 * h;
                box.Left = MathF.Min(box.Left, MathF.Min(l, rt));
                box.Right = MathF.Max(box.Right, MathF.Max(l, rt));
                box.Top = MathF.Min(box.Top, MathF.Min(t, b));
                box.Bottom = MathF.Max(box.Bottom, MathF.Max(t, b));
            }
            _hudFrameBoxes[e] = box;
        }
        int elements = _hudFrameIds.Count;
        if (_hudElScale.Length < elements)
        {
            int n = elements * 2;
            Array.Resize(ref _hudElScale, n); Array.Resize(ref _hudElHx, n); Array.Resize(ref _hudElHy, n);
            Array.Resize(ref _hudElTx, n); Array.Resize(ref _hudElTy, n); Array.Resize(ref _hudElAlpha, n);
            Array.Resize(ref _hudElHidden, n);
        }
        bool any = layout.Editing;
        for (int e = 0; e < elements; e++)
        {
            string id = _hudFrameIds[e];
            HudLayout.Box raw = _hudFrameBoxes[e];
            _hudElScale[e] = 1;
            _hudElAlpha[e] = 1;
            _hudElHidden[e] = false;
            _hudElHx[e] = _hudElTx[e] = 0;
            _hudElHy[e] = _hudElTy[e] = 0;
            if (raw.Right < raw.Left) continue; // (no items in its ranges)
            layout.Live.Add(id);
            if (layout.Homes.TryGetValue(id, out HudLayout.Box home))
            {
                home.Left = MathF.Min(home.Left, raw.Left);
                home.Top = MathF.Min(home.Top, raw.Top);
                home.Right = MathF.Max(home.Right, raw.Right);
                home.Bottom = MathF.Max(home.Bottom, raw.Bottom);
            }
            else
            {
                home = raw;
                layout.Order.Add(id);
            }
            layout.Homes[id] = home;
            _hudElHx[e] = _hudElTx[e] = home.CentreX;
            _hudElHy[e] = _hudElTy[e] = home.CentreY;
            if (!layout.Edits.TryGetValue(id, out LayoutEdit? edit) || edit.IsDefault) continue;
            any = true;
            _hudElScale[e] = LayoutEdit.ClampScale(edit.Scale);
            _hudElAlpha[e] = Math.Clamp(edit.Opacity, 0, 1);
            _hudElHidden[e] = edit.Hidden;
            if (HudLayout.Movable(id))
            {
                (_hudElTx[e], _hudElTy[e]) = layout.Centre(id, home, edit);
            }
            else
            {
                // the reticle: about its own centre this frame (it stays on the aim)
                _hudElHx[e] = _hudElTx[e] = raw.CentreX;
                _hudElHy[e] = _hudElTy[e] = raw.CentreY;
            }
        }
        _hudLayoutOn = any;
        if (layout.Editing)
        {
            // the editor: the world dimmed under the HUD so its parts stand out (the touch layout editor's 40%)
            GlNative.Uniform1i(HU("use_mask"), 0);
            GLES30.GlUniform4f(HU("fade_color"), 0, 0, 0, 0.4f);
            DrawHudQuad(-1, 1, 1, -1);
            GLES30.GlUniform4f(HU("fade_color"), 0, 0, 0, 0);
        }
        return any;
    }

    // one HUD item with its element's edit: false = not drawn (hidden; the editor shows it faintly)
    bool HudLayoutItem(int i, ref float left, ref float right, ref float top, ref float bottom, ref float alpha)
    {
        if (!_hudLayoutOn || i >= _hudItemElements) return true;
        int e = _hudItemElement[i];
        if (e < 0) return true;
        if (_hudElHidden[e])
        {
            if (!_hudLayout.Editing) return false;
            alpha *= 0.25f;
        }
        alpha *= _hudElAlpha[e];
        float s = _hudElScale[e];
        float hx = _hudElHx[e], hy = _hudElHy[e], tx = _hudElTx[e], ty = _hudElTy[e];
        if (s == 1 && hx == tx && hy == ty) return true;
        float w = _width, h = _height;
        left = (tx + ((left + 1) / 2 * w - hx) * s) / w * 2 - 1;
        right = (tx + ((right + 1) / 2 * w - hx) * s) / w * 2 - 1;
        top = 1 - (ty + ((1 - top) / 2 * h - hy) * s) / h * 2;
        bottom = 1 - (ty + ((1 - bottom) / 2 * h - hy) * s) / h * 2;
        return true;
    }
}
