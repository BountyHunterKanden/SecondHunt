using System;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.App.Platform;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// The menu background shot picker (dev only, owner 2026-11-20: "a dev entry for me to go in every map and free cam fly
// and press a button to snap shot where the camera should be"). A solo arena run with --es menushot 1 (the dev
// launcher's MENU BACKGROUND SHOT button; desktop --menushot 1) opens photo mode's free camera by itself, with no walls
// and no range limit, starting from the arena's current menu shot. SHOT saves that camera as the arena's LOOK > MENUS
// ARENA BACKGROUND view in <data>/menu_shots.json (MenuBackdropShots.SavePicked) in place of a picture. Roll isn't
// kept: the backdrop's camera is always upright.
// The shot's depth of field (owner 2026-11-21) is set here too and drawn live as it will be on the menus
// (CampaignRenderer.Dof.cs): AUTO FOCUS (pad X, key F) focuses on what's at the screen's centre (and turns BLUR on if
// it's off), FOCUS - / + (X + D-pad down / up, keys , . shown as < >) move it nearer / farther, BLUR - / + (X + D-pad left / right,
// keys 9 0) step 0 (none) to 10, and RANGE - / + (X + L1 / R1, keys 7 8; owner 11-21 "yes add range") step how far nearer
// and farther than the focus also stays sharp, 0 (only the focus) to 50 world units. SHOT saves them with the camera.
internal sealed partial class CampaignRenderer
{
    bool _menuShotPick;
    bool _menuShotOpened;
    int _menuShotWait;
    (Vector3 Eye, Vector3 Target, float Fov)? _menuShotLast;
    float _menuShotFocus = MenuBackdropShots.DefaultFocus, _menuShotBlur, _menuShotRange; // the depth of field being set (Blur 0 = none)
    bool _menuShotAutoFocus; // this frame's presses (keys, pad, pills)
    int _menuShotBlurStep, _menuShotRangeStep;
    bool _menuShotPadX, _menuShotPadXUsed, _menuShotPadLeft, _menuShotPadRight, _menuShotPadL1, _menuShotPadR1; // last frame's, for presses
    static readonly float[] MenuShotRanges = { 0, 0.5f, 1, 2, 3, 5, 8, 12, 20, 30, MenuBackdropShots.MaxRange }; // RANGE - / + steps
    const float MenuShotFocusRate = 1.2f; // FOCUS - / + held: x e^1.2 (3.3x) a second, 2.5x that with Shift
    const float MenuShotFirstBlur = 4; // AUTO FOCUS with BLUR at 0 turns this much on, so it shows

    string MenuShotArena => _matchSettings?.Arena ?? "";
    string? MenuShotDir => SettingsPath is { Length: > 0 } p ? Path.GetDirectoryName(p) : null;
    string PhotoTitle => _menuShotPick ? "MENU SHOT  " + MenuShotArena : "PHOTO MODE";

    // ConfigureSolo: --es menushot 1 (never a public build)
    void ConfigureMenuShotPick(LaunchArgs args)
    {
        _menuShotPick = args.Get("menushot") == "1" && !MphRecomp.Config.RecompSettings.PublicBuild;
        if (_menuShotPick) Log.Info("MPHMenu", $"menu shot picker in {MenuShotArena}, saving to {MenuShotDir}");
    }

    // each frame (OnDrawFrame): photo mode opens by itself once the arena is up
    void MenuShotPickFrame()
    {
        if (_menuShotPick && PhotoOpen) _menuShotLast = (_photoPos, _photoPos + PhotoFacing, _photoFov); // (BACK and back in: here again)
        if (!_menuShotPick || _menuShotOpened || _host?.Scene == null || _error != null) return;
        if (++_menuShotWait < 45) return;
        _menuShotOpened = true;
        OpenPhotoMode();
    }

    // OpenPhotoMode: the camera starts where the last one was left, else at the arena's menu shot as it is now
    void MenuShotPlace()
    {
        if (!_menuShotPick || _host == null) return;
        MenuBackdropShots.LoadPicked(MenuShotDir);
        if (_menuShotLast == null) (_menuShotFocus, _menuShotBlur, _menuShotRange) = MenuBackdropShots.DofFor(MenuShotArena);
        var (eye, target, fov) = _menuShotLast ?? MenuBackdropShots.For(MenuShotArena, _host.SpawnPoints(), new Random(0));
        Vector3 fwd = target - eye;
        if (fwd.LengthSquared < 1e-6f) fwd = Vector3.UnitZ;
        fwd.Normalize();
        _photoAnchor = _photoPos = eye;
        _photoYaw = MathF.Atan2(fwd.X, fwd.Z);
        _photoPitch = Math.Clamp(MathF.Asin(Math.Clamp(fwd.Y, -1, 1)), -1.5f, 1.5f);
        _photoRoll = 0;
        _photoFov = _photoFov0 = Math.Clamp(fov, PhotoMinFov, PhotoMaxFov);
    }

    // PhotoCapture: SHOT saves the camera instead of a picture
    void MenuShotSave()
    {
        string arena = MenuShotArena;
        var shot = (_photoPos, _photoPos + PhotoFacing, _photoFov);
        _menuShotLast = shot;
        string? dir = MenuShotDir;
        try
        {
            if (dir == null || arena.Length == 0) throw new InvalidOperationException("no data folder or arena");
            int count = MenuBackdropShots.SavePicked(dir, arena, shot.Item1, shot.Item2, shot.Item3, _menuShotFocus, _menuShotBlur,
                _menuShotRange);
            Log.Info("MPHMenu", $"menu shot saved: {arena} eye ({shot.Item1.X:0.00}, {shot.Item1.Y:0.00}, {shot.Item1.Z:0.00})"
                + $" target ({shot.Item2.X:0.00}, {shot.Item2.Y:0.00}, {shot.Item2.Z:0.00}) fov {shot.Item3:0}"
                + $" focus {_menuShotFocus:0.0} blur {_menuShotBlur:0} range {_menuShotRange:0.#}; {count} arenas in {dir}");
            _photoToast = $"MENU SHOT SAVED  {arena}  ({count} ARENAS)";
        }
        catch (Exception ex)
        {
            Log.Warn("MPHMenu", "menu shot not saved: " + ex.Message);
            _photoToast = "MENU SHOT NOT SAVED";
        }
        _photoToastAt = _clock.Elapsed.TotalSeconds;
    }

    // the help's top line: the depth of field being set
    string MenuShotDofText => _menuShotPick ? $"   FOCUS {_menuShotFocus:0.0}   RANGE {_menuShotRange:0.#}   BLUR {_menuShotBlur:0}" : "";

    // PhotoInput, the pad: while picking, X + the D-pad set the depth of field in place of zoom and RESET. X alone
    // (released with no D-pad) is AUTO FOCUS, X + left / right steps BLUR, X + L1 / R1 steps RANGE (PhotoInput doesn't roll
    // while X is held), X + up / down moves the focus farther / nearer (the return: 1, -1, 0)
    float MenuShotDofPad(bool x, ref bool up, ref bool down, ref bool side)
    {
        if (!_menuShotPick) return 0;
        PadState pad = Pad;
        bool left = pad[PadButton.DpadLeft] || pad.HatX <= -0.5f, right = pad[PadButton.DpadRight] || pad.HatX >= 0.5f;
        bool l1 = pad[PadButton.L1], r1 = pad[PadButton.R1];
        float dir = 0;
        if (x && !_menuShotPadX) _menuShotPadXUsed = false;
        if (x)
        {
            if (left && !_menuShotPadLeft) _menuShotBlurStep--;
            if (right && !_menuShotPadRight) _menuShotBlurStep++;
            if (l1 && !_menuShotPadL1) _menuShotRangeStep--;
            if (r1 && !_menuShotPadR1) _menuShotRangeStep++;
            dir = (up ? 1 : 0) - (down ? 1 : 0);
            if (left || right || up || down || l1 || r1) _menuShotPadXUsed = true;
            up = down = side = false;
        }
        else if (_menuShotPadX && !_menuShotPadXUsed)
        {
            _menuShotAutoFocus = true;
        }
        (_menuShotPadX, _menuShotPadLeft, _menuShotPadRight, _menuShotPadL1, _menuShotPadR1) = (x, left, right, l1, r1);
        return dir;
    }

    // PhotoInput, each frame: the depth of field's presses and held keys / pills / pad
    void MenuShotDofFrame(Scene scene, float dt, PhotoHold held, float padDir)
    {
        if (!_menuShotPick)
        {
            (_menuShotAutoFocus, _menuShotBlurStep, _menuShotRangeStep) = (false, 0, 0);
            return;
        }
        if (_menuShotAutoFocus)
        {
            // what the screen's centre sees, as the walls photo mode stops at (nothing within MaxFocus: MaxFocus)
            _menuShotAutoFocus = false;
            Vector3 far = _photoPos + PhotoFacing * MenuBackdropShots.MaxFocus;
            var res = new CollisionResult();
            float d = CollisionDetection.CheckBetweenPoints(_photoPos, far, TestFlags.Players, scene, ref res)
                ? (res.Position - _photoPos).Length : MenuBackdropShots.MaxFocus;
            _menuShotFocus = Math.Clamp(d, MenuBackdropShots.MinFocus, MenuBackdropShots.MaxFocus);
            if (_menuShotBlur <= 0) _menuShotBlur = MenuShotFirstBlur;
        }
        if (_menuShotBlurStep != 0)
        {
            _menuShotBlur = Math.Clamp(_menuShotBlur + _menuShotBlurStep, 0, MenuBackdropShots.MaxBlur);
            _menuShotBlurStep = 0;
        }
        if (_menuShotRangeStep != 0)
        {
            // from the step it's on (a saved range between steps: the one below it)
            int at = 0;
            while (at + 1 < MenuShotRanges.Length && MenuShotRanges[at + 1] <= _menuShotRange + 0.001f) at++;
            _menuShotRange = MenuShotRanges[Math.Clamp(at + _menuShotRangeStep, 0, MenuShotRanges.Length - 1)];
            _menuShotRangeStep = 0;
        }
        float dir = padDir + ((held & PhotoHold.FocusFar) != 0 ? 1 : 0) - ((held & PhotoHold.FocusNear) != 0 ? 1 : 0);
        if (dir != 0)
        {
            float rate = MenuShotFocusRate * ((held & PhotoHold.Fast) != 0 ? 2.5f : 1);
            _menuShotFocus = Math.Clamp(_menuShotFocus * MathF.Exp(Math.Clamp(dir, -1, 1) * rate * dt),
                MenuBackdropShots.MinFocus, MenuBackdropShots.MaxFocus);
        }
    }
}
