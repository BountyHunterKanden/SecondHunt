using System;
using System.Collections.Generic;
using MphRecomp.Frontend;
using MphRecomp.Online.Voice;

namespace MphRecomp.App;

// The speaking pop-up in a match (Thread C, C4, 2026-10-23): while someone is heard over voice chat their name and a
// speaker icon show in a small stack on the right edge, between the HUD's top-right block (octolith, prime hunter) and the
// ammo counter / weapon icon below it, so no HUD element sits under it. Several speakers stack downward (at most
// MaxRows, then "+N"); each fades in, stays a moment past the last sound so a pause between words doesn't flicker, and
// fades out. Drawn by the shared GL drawer over the scene and under the pause menu, scaled with the screen height
// (u = height / 360) so it reads the same on the Odin, a phone and a PC. Online games only (VoiceChat exists only there).
internal sealed partial class CampaignRenderer
{
    const int VoiceHudMaxRows = 4, VoiceHudNameChars = 14;
    const float VoiceHudFadeInMs = 120, VoiceHudLingerMs = 500, VoiceHudFadeOutMs = 300;

    sealed class VoiceHudRow
    {
        public string Code = "";
        public string Name = "";
        public bool Me;
        public long LastHeard;
        public long Since;      // when this row appeared
        public long GoneAt;     // 0 = still heard; else when it was released (the fade-out starts after the linger)
    }

    readonly UiTextureCache _voiceHudTextures = new();
    readonly UiGlDrawer _voiceHudDrawer = new();
    readonly List<VoiceHudRow> _voiceHudRows = new();
    readonly List<(string Code, string Name, bool Me)> _voiceHudHeard = new();
    MenuFont? _voiceHudFont;
    UiDrawList? _voiceHudList;

    void VoiceHudGlReset()
    {
        _voiceHudDrawer.Init();
        _voiceHudFont = null; // lazily rebuilt: Extract/Paths may not be ready yet at surface creation
    }

    void VoiceHudDraw(int width, int height)
    {
        VoiceChat? voice = VoiceChat.Current;
        if (voice == null)
        {
            _voiceHudRows.Clear();
            return;
        }
        long now = Environment.TickCount64;
        voice.Heard(_voiceHudHeard);
        UpdateVoiceHudRows(now);
        if (_voiceHudRows.Count == 0) return;
        try
        {
            _voiceHudFont ??= new MenuFont(_voiceHudTextures);
            _voiceHudList ??= new UiDrawList(_voiceHudTextures);
        }
        catch (Exception ex)
        {
            Log.Warn("VoiceHud", "font unavailable: " + ex.Message);
            return;
        }
        MenuFont font = _voiceHudFont;
        UiDrawList list = _voiceHudList;
        list.Clear();
        float u = height / 360f;
        float scale = 6f * u / MenuFont.Cell;
        float rowH = 12.5f * u, pitch = rowH + 2.5f * u, icon = MathF.Round(9.5f * u), pad = 3.5f * u;
        float right = width - 5 * u;
        // the free band on the right edge: below the octolith / prime hunter block (DS y 28-42 of 192) and above the ammo
        // (y 137), i.e. 0.30 .. 0.68 of the height
        float top = height * 0.30f;
        int icon_n = Math.Max(8, (int)icon);
        UiTexture glyph = _voiceHudTextures.GetOrAdd("voice glyph " + icon_n, () => (icon_n, icon_n, VoiceGlyph.Render(icon_n)));
        int shown = Math.Min(VoiceHudMaxRows, _voiceHudRows.Count);
        for (int i = 0; i < shown; i++)
        {
            VoiceHudRow row = _voiceHudRows[i];
            float a = VoiceHudAlpha(row, now);
            if (a <= 0.01f) continue;
            string full = MphRecomp.Config.StreamerPrivacy.Name(row.Name, row.Me); // (STREAMING: SHOW NAME off)
            string name = full.Length > VoiceHudNameChars ? full[..VoiceHudNameChars] : full;
            float textW = font.MeasureCanvas(name, scale);
            float w = pad + icon + pad + textW + pad;
            float x0 = MathF.Round(right - w), y0 = MathF.Round(top + i * pitch);
            // a dark plate with a thin menu-orange edge
            list.Quad(-1, x0 - u * 0.6f, y0 - u * 0.6f, x0 + w + u * 0.6f, y0 + rowH + u * 0.6f, 0, 0, 0, 0, 0.9f, 0.39f, 0.03f, 0.55f * a);
            list.Quad(-1, x0, y0, x0 + w, y0 + rowH, 0, 0, 0, 0, 0.04f, 0.05f, 0.06f, 0.72f * a);
            float ix = MathF.Round(x0 + pad), iy = MathF.Round(y0 + (rowH - icon_n) / 2);
            (float R, float G, float B) tint = row.Me ? (0.55f, 0.85f, 1f) : (0.35f, 1f, 0.45f);
            list.Quad(glyph.Id, ix, iy, ix + icon_n, iy + icon_n, 0, 0, 1, 1, tint.R, tint.G, tint.B, a);
            font.EmitCanvas(name, x0 + pad + icon + pad, y0 + (rowH - MenuFont.Cell * scale) / 2, 0, scale,
                1, 1, 1, a, list);
        }
        if (_voiceHudRows.Count > shown)
        {
            float y0 = MathF.Round(top + shown * pitch);
            font.EmitCanvas("+" + (_voiceHudRows.Count - shown), right, y0, 1, scale, 1, 0.97f, 0.61f, 0.85f, list);
        }
        _voiceHudDrawer.Draw(list, width, height, smooth: false); // crisp: the font atlas bleeds its neighbours when filtered
    }

    // fold this frame's heard list into the rows: new speakers get a row, heard ones refresh, the rest age out
    void UpdateVoiceHudRows(long now)
    {
        foreach (VoiceHudRow row in _voiceHudRows)
        {
            bool heard = false;
            foreach ((string code, string name, bool me) in _voiceHudHeard)
            {
                if (code == row.Code)
                {
                    heard = true;
                    row.Name = name;
                    row.Me = me;
                    break;
                }
            }
            if (heard)
            {
                row.LastHeard = now;
                row.GoneAt = 0;
            }
            else if (row.GoneAt == 0)
            {
                row.GoneAt = now;
            }
        }
        foreach ((string code, string name, bool me) in _voiceHudHeard)
        {
            bool have = false;
            foreach (VoiceHudRow row in _voiceHudRows)
            {
                if (row.Code == code)
                {
                    have = true;
                    break;
                }
            }
            if (!have) _voiceHudRows.Add(new VoiceHudRow { Code = code, Name = name, Me = me, LastHeard = now, Since = now });
        }
        _voiceHudRows.RemoveAll(r => r.GoneAt != 0 && now - r.GoneAt > VoiceHudLingerMs + VoiceHudFadeOutMs);
    }

    static float VoiceHudAlpha(VoiceHudRow row, long now)
    {
        float a = Math.Clamp((now - row.Since) / VoiceHudFadeInMs, 0, 1);
        if (row.GoneAt != 0)
        {
            float gone = now - row.GoneAt - VoiceHudLingerMs;
            if (gone > 0) a *= 1 - Math.Clamp(gone / VoiceHudFadeOutMs, 0, 1);
        }
        return a;
    }
}
