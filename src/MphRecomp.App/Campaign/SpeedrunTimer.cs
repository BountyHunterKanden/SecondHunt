using System;
using System.Collections.Generic;
using System.Globalization;
using MphRecomp.Campaign;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRead;

namespace MphRecomp.App;

// The campaign's speedrun timer (owner 2026-11-16): RECOMP SETTINGS > CAMPAIGN > SPEEDRUN TIMER off / igt / rta / both,
// SPEEDRUN SPLITS on / off. A run starts on a new file (the file select's empty slot, or game over's NEW GAME) and ends
// when the credits come (CampaignEnd.Ending), as speedrun.com/mph times it; the runs, splits and the slot's bests live in
// speedrun<slot>.json (Campaign/SpeedrunSplits.cs). IGT = the game's 60 Hz steps outside door loads, plus the ship's
// screens and movies at the frame rate; the pause menu, Select's map and game over's menu stop it. RTA = the wall clock
// from the start, pauses included (only time the app spent suspended is left out). A HUD element ("speedrun") the HUD
// editor moves, sizes, fades and hides like the game's own. Display only: nothing in the game reads any of it, so replays,
// goldens and matches play the same (it is off in matches and replays).
internal sealed partial class CampaignRenderer
{
    const string SpeedrunId = "speedrun";
    const double SpeedrunSaveEvery = 15, SpeedrunSplitShow = 6;

    readonly UiTextureCache _speedrunTextures = new();
    readonly UiGlDrawer _speedrunDrawer = new();
    MenuFont? _speedrunFont;
    UiDrawList? _speedrunList;
    readonly List<string> _speedrunNew = new();
    SpeedrunFile? _speedrunFile;
    int _speedrunSlot = -1;
    string _speedrunMode = "";       // "" = settings not read yet
    bool _speedrunSplitsOn = true;
    bool _speedrunMenuWas, _speedrunShipWas;
    double _speedrunSinceSave;
    SpeedrunMark? _speedrunLast;
    double _speedrunLastAge;

    bool SpeedrunOn => _speedrunMode is "igt" or "rta" or "both" && _matchSettings == null && !ReplayPlaying;
    SpeedrunRun? SpeedrunRunning => SpeedrunOn && _speedrunFile?.Current is SpeedrunRun run && !run.Finished ? run : null;
    string? SpeedrunPath => _saves == null ? null : SpeedrunFile.PathFor(_saves.Directory, _saveSlot);

    void SpeedrunGlReset()
    {
        _speedrunDrawer.Init();
        _speedrunFont = null;
    }

    void SpeedrunReadSettings()
    {
        try
        {
            RecompSettings s = RecompSettings.Load(SettingsPath);
            _speedrunMode = s.SpeedrunTimer ?? "off";
            _speedrunSplitsOn = s.SpeedrunSplits;
        }
        catch (Exception ex)
        {
            _speedrunMode = "off";
            Log.Warn("MPHSpeedrun", "settings: " + ex.Message);
        }
    }

    // this slot's runs (loaded on first use and when the slot changes)
    SpeedrunFile? SpeedrunSlotFile()
    {
        if (_saves == null) return null;
        if (_speedrunFile == null || _speedrunSlot != _saveSlot)
        {
            _speedrunSlot = _saveSlot;
            _speedrunFile = SpeedrunFile.Load(SpeedrunPath!);
            _speedrunLast = null;
        }
        return _speedrunFile;
    }

    void SpeedrunFlush()
    {
        _speedrunSinceSave = 0;
        if (_speedrunFile == null || SpeedrunPath is not string path) return;
        try
        {
            _speedrunFile.Save(path);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHSpeedrun", "save: " + ex.Message);
        }
    }

    // a new file in this slot: a new run (the slot's bests stay). archives: game over's NEW GAME lands at Celestial
    // Archives straight away, without the planet select's landing split. With the timer off the old run is dropped so it
    // never shows up later as this file's time.
    void SpeedrunStart(bool archives)
    {
        if (_speedrunMode == "") SpeedrunReadSettings();
        SpeedrunFile? file = SpeedrunSlotFile();
        if (file == null || _matchSettings != null) return;
        if (!SpeedrunOn)
        {
            if (file.Current == null) return;
            file.Current = null;
            SpeedrunFlush();
            return;
        }
        SpeedrunRun run = file.Start(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        if (archives) SpeedrunSplits.Land(run, Planet.CelestialArchives);
        _speedrunLast = null;
        Log.Info("MPHSpeedrun", $"run started in slot {_saveSlot}");
        SpeedrunFlush();
    }

    void SpeedrunNote(SpeedrunMark mark)
    {
        _speedrunLast = mark;
        _speedrunLastAge = 0;
        string delta = mark.Delta is double d ? " " + SpeedrunSplits.Delta(d) : "";
        Log.Info("MPHSpeedrun", $"split {mark.Name} {SpeedrunSplits.Clock(mark.Igt)}{delta}{(mark.BestSegment ? " (best segment)" : "")}");
    }

    // the planet select's LAND: the planet's first landing this run is a split
    void SpeedrunLanded(Planet planet)
    {
        if (SpeedrunRunning is not SpeedrunRun run || !SpeedrunSplits.Land(run, planet)) return;
        SpeedrunNote(_speedrunFile!.Split(SpeedrunSplits.LandingSplit(planet)));
        SpeedrunFlush();
    }

    // CampaignHost.Ended: the ending's credits stop the clock; anything else just saves where the run is
    void SpeedrunHostEnded(CampaignEnd end)
    {
        if (SpeedrunRunning is not SpeedrunRun run) return;
        SpeedrunPollBosses(run);
        if (end == CampaignEnd.Ending)
        {
            SpeedrunMark mark = _speedrunFile!.Finish();
            SpeedrunNote(mark);
            Log.Info("MPHSpeedrun", $"run finished: IGT {SpeedrunSplits.Clock(run.Igt)}, RTA {SpeedrunSplits.Clock(run.Rta)}"
                + (_speedrunFile.Best?.Started == run.Started ? " (personal best)" : ""));
        }
        SpeedrunFlush();
    }

    // one 60 Hz game step: game time, unless the step is a door's room load (TransitionState.Process: the next room
    // loading on the worker thread, the player held in the doorway)
    void SpeedrunStep()
    {
        if (SpeedrunRunning is not SpeedrunRun run) return;
        if (MphRead.GameState.TransitionState != TransitionState.Process) run.Igt += 1 / 60.0;
    }

    // once a frame, after the steps: the ship's screens and movies as game time (dt: the frame, capped at 0.1 s, so a
    // long load's hitch adds next to nothing), the wall clock, the octolith splits, the settings when a menu closes
    void SpeedrunFrame(double frameInterval, double dt, bool menuOpen)
    {
        bool menuClosed = (_speedrunMenuWas && !menuOpen) || (_speedrunShipWas && !ShipOpen);
        _speedrunMenuWas = menuOpen;
        _speedrunShipWas = ShipOpen;
        if (_speedrunMode == "" || menuClosed) SpeedrunReadSettings();
        if (_speedrunLast != null) _speedrunLastAge += Math.Min(frameInterval, 1);
        if (SpeedrunRunning is not SpeedrunRun run) return;
        // an app left in the background (the Odin asleep, the window minimized) leaves a long frame: not counted
        run.Rta += Math.Min(frameInterval, 1);
        if (!menuOpen && !_paused && !MapOpen)
        {
            if (ShipOpen && _shipPage != ShipPage.GameOver) run.Igt += dt;
            else if (_host != null && MovieHoldsSim) run.Igt += dt;
        }
        if (_host != null) SpeedrunPollBosses(run);
        if ((_speedrunSinceSave += Math.Min(frameInterval, 1)) >= SpeedrunSaveEvery) SpeedrunFlush();
    }

    void SpeedrunPollBosses(SpeedrunRun run)
    {
        if (MphRead.GameState.StorySave is not StorySave story) return;
        _speedrunNew.Clear();
        SpeedrunSplits.BossSplits(run, (int)story.BossFlags, _speedrunNew);
        if (_speedrunNew.Count == 0) return;
        foreach (string name in _speedrunNew) SpeedrunNote(_speedrunFile!.Split(name));
        SpeedrunFlush();
    }

    // over the scene and the ship, under the pause menu (the HUD editor's rings draw over it)
    void SpeedrunDraw(int width, int height)
    {
        if (!SpeedrunOn || (_host == null && !ShipOpen) || width <= 0 || height <= 0) return;
        try
        {
            _speedrunFont ??= new MenuFont(_speedrunTextures);
            _speedrunList ??= new UiDrawList(_speedrunTextures);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHSpeedrun", "font unavailable: " + ex.Message);
            _speedrunMode = "off";
            return;
        }
        SpeedrunRun? run = SpeedrunSlotFile()?.Current;
        MenuFont font = _speedrunFont;
        UiDrawList list = _speedrunList;
        list.Clear();
        float u = height / 360f;
        float big = 8f * u / MenuFont.Cell, small = 5.5f * u / MenuFont.Cell;
        float pad = 3f * u, gap = 1.5f * u;
        float bigH = MenuFont.Cell * big, smallH = MenuFont.Cell * small;

        bool both = _speedrunMode == "both";
        bool rtaMain = _speedrunMode == "rta";
        string main = run == null ? "--:--" : SpeedrunSplits.Clock(rtaMain ? run.Rta : run.Igt);
        string? sub = run != null && both ? "RTA " + SpeedrunSplits.Clock(run.Rta) : null;
        string? split = null;
        (float R, float G, float B) splitTint = (1, 1, 1);
        if (run != null && run.Finished)
        {
            split = (_speedrunFile!.Best?.Started == run.Started ? "PERSONAL BEST" : "FINAL") + (both ? "" : rtaMain ? " RTA" : " IGT");
            splitTint = (1, 0.84f, 0.3f);
        }
        else if (_speedrunSplitsOn && _speedrunLast is SpeedrunMark mark && _speedrunLastAge < SpeedrunSplitShow)
        {
            split = mark.Name + (mark.Delta is double d ? "  " + SpeedrunSplits.Delta(d) : "");
            splitTint = mark.BestSegment && mark.Delta != null ? (1, 0.84f, 0.3f)
                : mark.Delta is double dd ? (dd <= 0 ? (0.35f, 1f, 0.45f) : (1f, 0.4f, 0.35f)) : (0.85f, 0.9f, 1f);
        }

        // the home box: a fixed size (the widest clock and two small lines) so the editor's ring doesn't jump as the
        // text changes; top centre, under the top edge
        float boxW = MathF.Ceiling(MathF.Max(font.MeasureCanvas("0:00:00.0", big), font.MeasureCanvas("ARCTERRA 2 OCTOLITH  +0:00:00.0", small)) + pad * 2);
        float boxH = MathF.Ceiling(pad + bigH + 2 * (gap + smallH) + pad);
        var home = new HudLayout.Box { Left = (width - boxW) / 2, Top = 4 * u, Right = (width + boxW) / 2, Bottom = 4 * u + boxH };
        float scale = 1, alpha = 1, cx = home.CentreX, cy = home.CentreY;
        HudLayout layout = _hudLayout;
        if (Scene.HostHudElementsOn)
        {
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
            if (layout.Width != width || layout.Height != height)
            {
                // HudLayoutPrepare clears the homes on a resize; on the ship nothing else has drawn yet
                layout.Width = width;
                layout.Height = height;
            }
            layout.Homes[SpeedrunId] = home;
            if (!layout.Order.Contains(SpeedrunId)) layout.Order.Add(SpeedrunId);
            layout.Live.Add(SpeedrunId);
            if (layout.Edits.TryGetValue(SpeedrunId, out LayoutEdit? edit))
            {
                scale = LayoutEdit.ClampScale(edit.Scale);
                alpha = Math.Clamp(edit.Opacity, 0, 1);
                if (edit.Hidden)
                {
                    if (!layout.Editing) return;
                    alpha = 0.25f;
                }
                (cx, cy) = layout.Centre(SpeedrunId, home, edit);
            }
        }
        if (alpha <= 0.01f) return;

        // the plate fits what shows now, centred on the element's centre, top-aligned in its box
        float mainW = font.MeasureCanvas(main, big);
        float textW = mainW;
        if (sub != null) textW = MathF.Max(textW, font.MeasureCanvas(sub, small));
        if (split != null) textW = MathF.Max(textW, font.MeasureCanvas(split, small));
        int lines = (sub != null ? 1 : 0) + (split != null ? 1 : 0);
        float w = (textW + pad * 2) * scale, h = (pad + bigH + lines * (gap + smallH) + pad) * scale;
        float x0 = MathF.Round(cx - w / 2), y0 = MathF.Round(cy - boxH * scale / 2);
        float e = u * 0.6f;
        list.Quad(-1, x0 - e, y0 - e, x0 + w + e, y0 + h + e, 0, 0, 0, 0, 0.9f, 0.39f, 0.03f, 0.55f * alpha);
        list.Quad(-1, x0, y0, x0 + w, y0 + h, 0, 0, 0, 0, 0.04f, 0.05f, 0.06f, 0.72f * alpha);
        float y = y0 + pad * scale;
        (float R, float G, float B) clockTint = run == null ? (0.6f, 0.6f, 0.6f) : run.Finished ? (1, 0.84f, 0.3f) : (1, 1, 1);
        font.EmitCanvas(main, MathF.Round(cx), MathF.Round(y), 2, big * scale, clockTint.R, clockTint.G, clockTint.B, alpha, list);
        y += (bigH + gap) * scale;
        if (sub != null)
        {
            font.EmitCanvas(sub, MathF.Round(cx), MathF.Round(y), 2, small * scale, 0.75f, 0.8f, 0.85f, alpha, list);
            y += (smallH + gap) * scale;
        }
        if (split != null)
        {
            font.EmitCanvas(split, MathF.Round(cx), MathF.Round(y), 2, small * scale, splitTint.R, splitTint.G, splitTint.B, alpha, list);
        }
        _speedrunDrawer.Draw(list, width, height, smooth: false); // crisp: the font atlas bleeds its neighbours when filtered
    }
}
