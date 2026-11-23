using System.Collections.Concurrent;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.App.Gl;
using MphRecomp.App.Platform;
using MphRecomp.Frontend;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Photo mode (owner GO 2026-11-16, docs/IDEAS_SURVEY.md): PAUSED > PHOTO MODE (campaign and offline matches, and a replay's
// paused frame; never online, where the game can't wait). The game stays held; a free camera flies round it -- a
// render-only HostView, as a replay's free cam, the picture worked out again each frame with nothing stepped
// (HostRefreshView) -- within PhotoRange of where it started and never through a wall (collision slide), with the HUD,
// gun and reticle hidden, its own field of view and roll. A shot saves the picture at the screen's full size as a PNG
// (AppPlatform.SaveScreenshot: Windows <data>/Screenshots, Android Pictures/SecondHunt). Back leaves to the pause menu
// again, the view as it was. A screenshot outside photo mode (F12 on a PC: TakeScreenshot) saves the picture with its
// HUD, no menus.
//
// Controls: pad LS move, RS look, L2 / R2 down / up, L1 / R1 roll, D-pad up / down zoom, left / right reset, A shot,
// Y hide the help, B / Start back. Keys: WASD move, mouse look, Space / Ctrl up / down, Q / E roll, wheel or - / = zoom,
// Shift fast, R reset, H hide, Enter / F12 / left click shot, Esc back (DesktopInput). Touch: the left half a move stick,
// the right half drag to look, the pills along the bottom (PhotoTouch).
internal sealed partial class CampaignRenderer
{
    public bool PhotoOpen { get; private set; }

    const float PhotoRange = 12, PhotoMargin = 0.5f, PhotoSpeed = 6, PhotoLookSpeed = 2.2f, PhotoMouseLook = 0.0025f,
        PhotoMinFov = 20, PhotoMaxFov = 110, PhotoRollSpeed = 1.2f, PhotoFovSpeed = 40;

    Vector3 _photoAnchor, _photoPos;
    float _photoYaw, _photoPitch, _photoRoll, _photoFov, _photoFov0;
    bool _photoHelpHidden;

    // the view state photo mode overrides, put back on leaving
    HostView? _photoSavedView;
    Vector3 _photoSavedOffset;
    Action? _photoSavedBefore;
    bool _photoSavedNoViewer, _photoSavedSpectated, _photoSavedHideReticle, _photoSavedOutside;
    PlayerEntity? _photoSavedHudSeat;
    int _photoSavedFpSeat, _photoSavedDeathSeat;

    // input from the shells' threads: held keys (PhotoKeys), presses, touches
    volatile int _photoHeld;
    readonly ConcurrentQueue<PhotoPress> _photoPresses = new();
    readonly ConcurrentQueue<(float X, float Y, int Phase, int Id)> _photoTouches = new();
    bool _photoPadA, _photoPadB, _photoPadY, _photoPadSide; // last frame's, for presses

    [Flags]
    public enum PhotoHold
    {
        None = 0, Forward = 1, Back = 2, Left = 4, Right = 8, Up = 16, Down = 32, RollLeft = 64, RollRight = 128,
        Fast = 256, ZoomIn = 512, ZoomOut = 1024,
        FocusNear = 2048, FocusFar = 4096, // the menu shot picker's depth of field (CampaignRenderer.MenuShotPick.cs)
    }

    public enum PhotoPress { Shot, Reset, Hide, Back, WheelIn, WheelOut, AutoFocus, BlurDown, BlurUp, RangeDown, RangeUp }

    // the desktop's held keys, once a frame (DesktopInput.Poll), and its presses / wheel
    public void PhotoKeys(PhotoHold held) => _photoHeld = (int)held;
    public void PhotoKeyPress(PhotoPress press) => _photoPresses.Enqueue(press);
    public void PhotoWheel(float notches) => _photoPresses.Enqueue(notches > 0 ? PhotoPress.WheelIn : PhotoPress.WheelOut);
    // a touch's down / move / up (0 / 1 / 2), in pixels (CampaignActivity)
    public void PhotoTouch(float x, float y, int phase, int id = 0) => _photoTouches.Enqueue((x, y, phase, id));

    // F12 outside photo mode: the next picture with its HUD (no menus) is saved
    volatile bool _shotPending;
    public void TakeScreenshot()
    {
        if (_host != null) _shotPending = true;
    }

    // PAUSED > PHOTO MODE (PauseOverlay.PhotoModeRequested; GL thread, the menu just closed)
    public void OpenPhotoMode()
    {
        Scene? scene = _host?.Scene;
        if (scene == null || _error != null || ShipOpen || MapOpen || _seekTarget >= 0 || PhotoOpen)
        {
            if (scene != null && !PhotoOpen) _openPauseMenu = true;
            return;
        }
        HostView? raw = scene.HostView;
        if (raw != null && raw == PlayerEntity.Main.DeathFollowView) raw = null; // (the getter's fallback, not a set view)
        _photoSavedView = raw;
        _photoSavedOffset = scene.HostCameraOffset;
        _photoSavedBefore = scene.HostBeforeCamera;
        _photoSavedNoViewer = PlayerEntity.HostNoViewer;
        _photoSavedSpectated = PlayerEntity.HostMainSpectated;
        _photoSavedHideReticle = PlayerEntity.HostHideReticle;
        _photoSavedOutside = PlayerEntity.HostOutsideHead;
        _photoSavedHudSeat = PlayerEntity.HostHudSeat;
        _photoSavedFpSeat = PlayerEntity.HostFirstPersonSeat;
        _photoSavedDeathSeat = PlayerEntity.HostDeathFollowSeat;

        // from the picture as it is: the camera's place and facing
        Matrix4 cam = scene.ViewMatrix.Inverted();
        Vector3 eye = cam.Row3.Xyz, fwd = -cam.Row2.Xyz;
        if (fwd.LengthSquared < 1e-6f) fwd = Vector3.UnitZ;
        fwd.Normalize();
        _photoAnchor = _photoPos = eye;
        _photoYaw = MathF.Atan2(fwd.X, fwd.Z);
        _photoPitch = Math.Clamp(MathF.Asin(Math.Clamp(fwd.Y, -1, 1)), -1.5f, 1.5f);
        _photoRoll = 0;
        float fov = scene.HostView is { FovY: > 0 } v ? v.FovY
            : PlayerEntity.Main.CameraInfo.Fov > 0 ? PlayerEntity.Main.CameraInfo.Fov : 78;
        _photoFov = _photoFov0 = Math.Clamp(fov, PhotoMinFov, PhotoMaxFov);
        _photoHelpHidden = false;
        _photoHeld = 0;
        while (_photoPresses.TryDequeue(out _)) { }
        while (_photoTouches.TryDequeue(out _)) { }
        (_photoMoveId, _photoLookId) = (-1, -1);
        Array.Fill(_photoPillHeld, -1);
        // a button still down from the menu's press is no press here
        (_photoPadA, _photoPadB, _photoPadY, _photoPadSide) = (Pad[PadButton.A], Pad[PadButton.B], Pad[PadButton.Y], PadSide);
        Pad.TakeAim();
        PhotoOpen = true;
        Log.Info("MPHPhoto", $"photo mode at ({eye.X:0.00}, {eye.Y:0.00}, {eye.Z:0.00}) fov {_photoFov:0}");
        MenuShotPlace(); // the menu background shot picker's camera (CampaignRenderer.MenuShotPick.cs)
        PhotoApply();
    }

    // leaving: the view as it was, and the pause menu again (reopenMenu)
    void ClosePhoto(bool reopenMenu)
    {
        if (!PhotoOpen) return;
        PhotoOpen = false;
        PlayerEntity.HostNoViewer = _photoSavedNoViewer;
        PlayerEntity.HostMainSpectated = _photoSavedSpectated;
        PlayerEntity.HostHideReticle = _photoSavedHideReticle;
        PlayerEntity.HostOutsideHead = _photoSavedOutside;
        PlayerEntity.HostHudSeat = _photoSavedHudSeat;
        PlayerEntity.HostFirstPersonSeat = _photoSavedFpSeat;
        PlayerEntity.HostDeathFollowSeat = _photoSavedDeathSeat;
        Scene? scene = _host?.Scene;
        if (scene != null)
        {
            scene.HostView = _photoSavedView;
            scene.HostCameraOffset = _photoSavedOffset;
            scene.HostBeforeCamera = _photoSavedBefore;
            RefreshPhotoView(scene);
        }
        _photoSavedView = null;
        _photoSavedBefore = null;
        _photoSavedHudSeat = null;
        Array.Fill(_photoPillRects, default);
        Log.Info("MPHPhoto", "photo mode closed");
        if (reopenMenu) _openPauseMenu = true;
    }

    void RefreshPhotoView(Scene scene)
    {
        SetPlayerHooks(true);
        try
        {
            scene.HostRefreshView();
        }
        finally
        {
            SetPlayerHooks(false);
        }
    }

    bool PadUp => Pad[PadButton.DpadUp] || Pad.HatY <= -0.5f;
    bool PadDown => Pad[PadButton.DpadDown] || Pad.HatY >= 0.5f;
    bool PadSide => Pad[PadButton.DpadLeft] || Pad[PadButton.DpadRight] || MathF.Abs(Pad.HatX) >= 0.5f;

    Vector3 PhotoFacing => new(MathF.Sin(_photoYaw) * MathF.Cos(_photoPitch), MathF.Sin(_photoPitch),
        MathF.Cos(_photoYaw) * MathF.Cos(_photoPitch));

    // once a frame in real time (OnDrawFrame, in place of stepping)
    void PhotoInput(float dt)
    {
        Scene? scene = _host?.Scene;
        if (scene == null || _error != null)
        {
            ClosePhoto(reopenMenu: false);
            return;
        }
        dt = Math.Clamp(dt, 0, 0.1f);
        PadState pad = Pad;
        var held = (PhotoHold)_photoHeld;
        bool shot = false, reset = false, hide = false, back = false;
        float wheel = 0;
        while (_photoPresses.TryDequeue(out PhotoPress p))
        {
            switch (p)
            {
            case PhotoPress.Shot: shot = true; break;
            case PhotoPress.Reset: reset = true; break;
            case PhotoPress.Hide: hide = true; break;
            case PhotoPress.Back: back = true; break;
            case PhotoPress.WheelIn: wheel += 1; break;
            case PhotoPress.WheelOut: wheel -= 1; break;
            case PhotoPress.AutoFocus: _menuShotAutoFocus = true; break;
            case PhotoPress.BlurDown: _menuShotBlurStep--; break;
            case PhotoPress.BlurUp: _menuShotBlurStep++; break;
            case PhotoPress.RangeDown: _menuShotRangeStep--; break;
            case PhotoPress.RangeUp: _menuShotRangeStep++; break;
            }
        }
        // pad presses (edges)
        bool a = pad[PadButton.A], b = pad[PadButton.B], y = pad[PadButton.Y], up = PadUp, down = PadDown, side = PadSide;
        if (a && !_photoPadA) shot = true;
        if (b && !_photoPadB) back = true;
        if (y && !_photoPadY) hide = true;
        float focusPad = MenuShotDofPad(pad[PadButton.X], ref up, ref down, ref side); // the picker's X + D-pad (.MenuShotPick.cs)
        if (side && !_photoPadSide) reset = true;
        (_photoPadA, _photoPadB, _photoPadY, _photoPadSide) = (a, b, y, side);
        if (a || b || y || up || down || side || pad[PadButton.X] || MathF.Abs(pad.Lx) > 0.3f || MathF.Abs(pad.Rx) > 0.3f) KeyboardHints = false;
        PhotoTouchFrame(ref shot, ref reset, ref hide, ref back, out PhotoHold touchHeld);
        held |= touchHeld;
        MenuShotDofFrame(scene, dt, held, focusPad); // the picker's depth of field
        if (back)
        {
            ClosePhoto(reopenMenu: true);
            return;
        }
        if (hide) _photoHelpHidden = !_photoHelpHidden;
        if (reset)
        {
            _photoFov = _photoFov0;
            _photoRoll = 0;
        }

        // look: the right stick (the aim invert), the mouse (captured: DesktopInput), the touch look finger
        System.Numerics.Vector2 mouse = pad.TakeAim();
        float touchLook = _height > 0 ? FreeCamTouchLook / _height : 0;
        float zoomLook = _photoFov / 78f; // (slower when zoomed in)
        _photoYaw -= (pad.Rx * (_aimInvertX ? -1 : 1) * PhotoLookSpeed * dt + mouse.X * PhotoMouseLook * (_mouseInvertX ? -1 : 1)
            + _photoLookDX * touchLook * (_touchInvertX ? -1 : 1)) * zoomLook;
        _photoPitch -= (pad.Ry * (_aimInvertY ? -1 : 1) * PhotoLookSpeed * dt + mouse.Y * PhotoMouseLook * (_mouseInvertY ? -1 : 1)
            + _photoLookDY * touchLook * (_touchInvertY ? -1 : 1)) * zoomLook;
        _photoPitch = Math.Clamp(_photoPitch, -1.5f, 1.5f);
        (_photoLookDX, _photoLookDY) = (0, 0);

        // move: the left stick, WASD, the touch move finger; straight up / down: R2 / L2, Space / Ctrl, the UP / DOWN pills
        float lx = pad.Lx, ly = pad.Ly;
        if ((held & PhotoHold.Forward) != 0) ly -= 1;
        if ((held & PhotoHold.Back) != 0) ly += 1;
        if ((held & PhotoHold.Left) != 0) lx -= 1;
        if ((held & PhotoHold.Right) != 0) lx += 1;
        if (_photoMoveId >= 0 && _height > 0)
        {
            float r = FreeCamTouchStick * _height / 360f;
            float sx = (_photoMoveX - _photoMoveX0) / r, sy = (_photoMoveY - _photoMoveY0) / r;
            float len = MathF.Sqrt(sx * sx + sy * sy);
            if (len > 1) (sx, sy) = (sx / len, sy / len);
            (lx, ly) = (lx + sx, ly + sy);
        }
        (lx, ly) = (Math.Clamp(lx, -1, 1), Math.Clamp(ly, -1, 1));
        float vert = (pad[PadButton.R2] || pad.R2 >= 0.5f ? 1 : 0) - (pad[PadButton.L2] || pad.L2 >= 0.5f ? 1 : 0);
        if ((held & PhotoHold.Up) != 0) vert += 1;
        if ((held & PhotoHold.Down) != 0) vert -= 1;
        vert = Math.Clamp(vert, -1, 1);
        float speed = PhotoSpeed * ((held & PhotoHold.Fast) != 0 ? 2.5f : 1);
        Vector3 facing = PhotoFacing;
        var right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
        PhotoMove(scene, (facing * -ly + right * lx + Vector3.UnitY * vert) * speed * dt);

        // roll and zoom
        float roll = _menuShotPick && pad[PadButton.X] ? 0 : (pad[PadButton.R1] ? 1 : 0) - (pad[PadButton.L1] ? 1 : 0); // (X + L1 / R1: the picker's RANGE)
        if ((held & PhotoHold.RollRight) != 0) roll += 1;
        if ((held & PhotoHold.RollLeft) != 0) roll -= 1;
        _photoRoll = Math.Clamp(_photoRoll + Math.Clamp(roll, -1, 1) * PhotoRollSpeed * dt, -MathF.PI, MathF.PI);
        float zoom = (up ? 1 : 0) - (down ? 1 : 0);
        if ((held & PhotoHold.ZoomIn) != 0) zoom += 1;
        if ((held & PhotoHold.ZoomOut) != 0) zoom -= 1;
        _photoFov = Math.Clamp(_photoFov - Math.Clamp(zoom, -1, 1) * PhotoFovSpeed * dt - wheel * 4, PhotoMinFov, PhotoMaxFov);

        if (shot) _shotPending = true;
        PhotoApply();
    }

    // the move an axis at a time, so a wall stops that axis and the others slide on (never through it), then held
    // within PhotoRange of the start
    void PhotoMove(Scene scene, Vector3 move)
    {
        if (_menuShotPick) { _photoPos += move; return; } // the menu shot picker flies anywhere (CampaignRenderer.MenuShotPick.cs)
        for (int axis = 0; axis < 3; axis++)
        {
            float d = move[axis];
            if (MathF.Abs(d) < 1e-6f) continue;
            Vector3 step = Vector3.Zero, probe = Vector3.Zero;
            step[axis] = d;
            probe[axis] = MathF.Sign(d) * PhotoMargin;
            var res = new CollisionResult();
            if (CollisionDetection.CheckBetweenPoints(_photoPos, _photoPos + step + probe, TestFlags.Players, scene, ref res))
            {
                // up to the wall less the margin, never backwards
                float room = MathF.Max(0, MathF.Abs(res.Position[axis] - _photoPos[axis]) - PhotoMargin);
                step[axis] = MathF.Sign(d) * MathF.Min(MathF.Abs(d), room);
            }
            _photoPos += step;
        }
        Vector3 off = _photoPos - _photoAnchor;
        if (off.Length > PhotoRange) _photoPos = _photoAnchor + off.Normalized() * PhotoRange;
    }

    // the photo camera onto the scene, and the picture worked out again with nothing stepped
    void PhotoApply()
    {
        Scene? scene = _host?.Scene;
        if (scene == null) return;
        Vector3 facing = PhotoFacing;
        var right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
        Vector3 camUp = Vector3.Cross(right, facing).Normalized();
        Vector3 upv = camUp * MathF.Cos(_photoRoll) + right * MathF.Sin(_photoRoll);
        // nobody's eyes: no HUD, helmet, gun or reticle; the main player's body drawn
        PlayerEntity.HostNoViewer = true;
        PlayerEntity.HostMainSpectated = !_menuShotPick; // (the menu shot picker: no player, as the menu's arena)
        PlayerEntity.HostHideReticle = true;
        PlayerEntity.HostOutsideHead = true;
        PlayerEntity.HostHudSeat = null;
        PlayerEntity.HostFirstPersonSeat = -1;
        PlayerEntity.HostDeathFollowSeat = -1;
        scene.HostBeforeCamera = null;
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(_photoPos, _photoPos + facing, upv, _photoFov);
        RefreshPhotoView(scene);
    }

    // ---- touch: the left half a move stick, the right half drag to look, the pills along the bottom (held or tapped)
    int _photoMoveId = -1, _photoLookId = -1;
    float _photoMoveX0, _photoMoveY0, _photoMoveX, _photoMoveY, _photoLookX, _photoLookY, _photoLookDX, _photoLookDY;
    float _photoTapX, _photoTapY;
    bool _photoTapMoved;

    // (the second row, FocusNear on: the menu shot picker's depth of field, shown only there)
    enum PhotoPill { Shot, Back, ZoomOut, ZoomIn, RollLeft, RollRight, Down, Up, Reset, Hide, FocusNear, FocusFar, AutoFocus, RangeDown, RangeUp, BlurDown, BlurUp, Count }
    static readonly string[] PhotoPillText = { "SHOT", "BACK", "ZOOM -", "ZOOM +", "ROLL <", "ROLL >", "DOWN", "UP", "RESET", "HIDE",
        "FOCUS -", "FOCUS +", "AUTO FOCUS", "RANGE -", "RANGE +", "BLUR -", "BLUR +" };
    readonly (float X0, float Y0, float X1, float Y1)[] _photoPillRects = new (float, float, float, float)[(int)PhotoPill.Count];
    readonly int[] _photoPillHeld = Enumerable.Repeat(-1, (int)PhotoPill.Count).ToArray(); // the touch holding it, else -1

    int PhotoPillAt(float x, float y)
    {
        if (_photoHelpHidden) return -1;
        for (int i = 0; i < _photoPillRects.Length; i++)
        {
            var r = _photoPillRects[i];
            if (r.X1 > r.X0 && x >= r.X0 && x <= r.X1 && y >= r.Y0 && y <= r.Y1) return i;
        }
        return -1;
    }

    void PhotoTouchFrame(ref bool shot, ref bool reset, ref bool hide, ref bool back, out PhotoHold held)
    {
        while (_photoTouches.TryDequeue(out var t))
        {
            (float x, float y, int phase, int id) = t;
            if (phase == 0)
            {
                KeyboardHints = false;
                int pill = PhotoPillAt(x, y);
                if (pill >= 0)
                {
                    _photoPillHeld[pill] = id;
                    switch ((PhotoPill)pill)
                    {
                    case PhotoPill.Shot: shot = true; break;
                    case PhotoPill.Back: back = true; break;
                    case PhotoPill.Reset: reset = true; break;
                    case PhotoPill.Hide: hide = true; break;
                    case PhotoPill.AutoFocus: _menuShotAutoFocus = true; break;
                    case PhotoPill.BlurDown: _menuShotBlurStep--; break;
                    case PhotoPill.BlurUp: _menuShotBlurStep++; break;
                    case PhotoPill.RangeDown: _menuShotRangeStep--; break;
                    case PhotoPill.RangeUp: _menuShotRangeStep++; break;
                    }
                    continue;
                }
                if (_photoMoveId < 0 && _photoLookId < 0) (_photoTapX, _photoTapY, _photoTapMoved) = (x, y, false);
                else _photoTapMoved = true;
                if (x < _width / 2f && _photoMoveId < 0) (_photoMoveId, _photoMoveX0, _photoMoveY0, _photoMoveX, _photoMoveY) = (id, x, y, x, y);
                else if (_photoLookId < 0) (_photoLookId, _photoLookX, _photoLookY) = (id, x, y);
                continue;
            }
            if (phase == 2)
            {
                for (int i = 0; i < _photoPillHeld.Length; i++)
                {
                    if (_photoPillHeld[i] == id) _photoPillHeld[i] = -1;
                }
            }
            if (id != _photoMoveId && id != _photoLookId) continue;
            float u = _height / 360f;
            if (MathF.Abs(x - _photoTapX) > FreeCamTouchSlop * u || MathF.Abs(y - _photoTapY) > FreeCamTouchSlop * u) _photoTapMoved = true;
            if (id == _photoMoveId) (_photoMoveX, _photoMoveY) = (x, y);
            else
            {
                _photoLookDX += x - _photoLookX;
                _photoLookDY += y - _photoLookY;
                (_photoLookX, _photoLookY) = (x, y);
            }
            if (phase == 2)
            {
                if (id == _photoMoveId) _photoMoveId = -1;
                else _photoLookId = -1;
                // a tap with the help hidden brings it back
                if (_photoMoveId < 0 && _photoLookId < 0 && !_photoTapMoved && _photoHelpHidden) hide = true;
            }
        }
        held = PhotoHold.None;
        if (_photoPillHeld[(int)PhotoPill.ZoomIn] >= 0) held |= PhotoHold.ZoomIn;
        if (_photoPillHeld[(int)PhotoPill.ZoomOut] >= 0) held |= PhotoHold.ZoomOut;
        if (_photoPillHeld[(int)PhotoPill.RollLeft] >= 0) held |= PhotoHold.RollLeft;
        if (_photoPillHeld[(int)PhotoPill.RollRight] >= 0) held |= PhotoHold.RollRight;
        if (_photoPillHeld[(int)PhotoPill.Up] >= 0) held |= PhotoHold.Up;
        if (_photoPillHeld[(int)PhotoPill.Down] >= 0) held |= PhotoHold.Down;
        if (_photoPillHeld[(int)PhotoPill.FocusNear] >= 0) held |= PhotoHold.FocusNear;
        if (_photoPillHeld[(int)PhotoPill.FocusFar] >= 0) held |= PhotoHold.FocusFar;
    }

    // ---- the capture: right after the picture is drawn (OnDrawFrame), before any overlay or menu
    void PhotoCapture()
    {
        if (!_shotPending) return;
        _shotPending = false;
        if (_menuShotPick && PhotoOpen) { MenuShotSave(); return; } // SHOT saves the camera (CampaignRenderer.MenuShotPick.cs)
        int w = _width, h = _height;
        if (w <= 0 || h <= 0) return;
        try
        {
            var raw = new byte[w * h * 4];
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
            GLES30.GlPixelStorei(GLES30.GlPackAlignment, 4);
            GLES30.GlReadPixels(0, 0, w, h, GLES30.GlRgba, GLES30.GlUnsignedByte, raw);
            string name = $"SecondHunt_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            Task.Run(() =>
            {
                string? where = null;
                try
                {
                    var rgba = new byte[raw.Length];
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, (h - 1 - y) * w * 4, rgba, y * w * 4, w * 4);
                    for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255; // (the frame's alpha isn't always 1)
                    where = AppPlatform.SaveScreenshot(rgba, w, h, name);
                }
                catch (Exception ex)
                {
                    Log.Warn("MPHPhoto", "screenshot save failed: " + ex.Message);
                }
                Log.Info("MPHPhoto", where != null ? $"screenshot {w}x{h} saved: {where}" : $"screenshot {w}x{h} not saved");
                _photoToastAt = _clock.Elapsed.TotalSeconds;
                _photoToast = where != null ? "SAVED  " + Path.GetFileName(where) : "SCREENSHOT NOT SAVED";
            });
        }
        catch (Exception ex)
        {
            Log.Warn("MPHPhoto", "screenshot failed: " + ex.Message);
        }
    }

    volatile string? _photoToast;
    double _photoToastAt;
    const double PhotoToastSeconds = 2.5;

    // ---- the help along the bottom (photo mode) and the saved note (any time); never in the saved picture
    void PhotoHudDraw(int width, int height)
    {
        string? toast = _photoToast;
        if (toast != null && _clock.Elapsed.TotalSeconds - _photoToastAt > PhotoToastSeconds) toast = _photoToast = null;
        bool help = PhotoOpen && !_photoHelpHidden;
        if (!help) Array.Fill(_photoPillRects, default);
        if (!help && toast == null) return;
        try
        {
            _replayHudFont ??= new MenuFont(_replayHudTextures);
            _replayHudList ??= new UiDrawList(_replayHudTextures);
        }
        catch (Exception ex)
        {
            Log.Warn("MPHPhoto", "hud font: " + ex.Message);
            return;
        }
        MenuFont font = _replayHudFont;
        UiDrawList list = _replayHudList;
        list.Clear();
        float u = height / 360f;
        float text = 6.5f * u / MenuFont.Cell, small = 4.4f * u / MenuFont.Cell;
        float hintH = 8 * u;
        if (help)
        {
            string[] keys = KeyboardHints
                ? new[] { "ENTER", "ESC", "-", "=", "Q", "E", "CTRL", "SPACE", "R", "H", "<", ">", "F", "7", "8", "9", "0" }
                : new[] { "A", "B", "DOWN", "UP", "L1", "R1", "L2", "R2", "LEFT", "Y", "X+DOWN", "X+UP", "X", "X+L1", "X+R1", "X+LEFT", "X+RIGHT" };
            float pillH = 16 * u, edge = 6 * u;
            float top = height - edge - hintH - 2 * u - pillH;
            PhotoPillRow(font, list, keys, 0, (int)PhotoPill.FocusNear, top, width, u, text, small, hintH);
            if (_menuShotPick) PhotoPillRow(font, list, keys, (int)PhotoPill.FocusNear, (int)PhotoPill.Count, top - pillH - hintH - 6 * u, width, u, text, small, hintH);
            else Array.Fill(_photoPillRects, default, (int)PhotoPill.FocusNear, (int)PhotoPill.Count - (int)PhotoPill.FocusNear);
            string line = KeyboardHints
                ? $"{PhotoTitle}   WASD MOVE   MOUSE LOOK   SHIFT FAST   WHEEL ZOOM   FOV {_photoFov:0}{MenuShotDofText}"
                : OperatingSystem.IsAndroid() && Pad.Lx == 0 && Pad.Rx == 0
                    ? $"{PhotoTitle}   LEFT SIDE MOVE   RIGHT SIDE LOOK   FOV {_photoFov:0}{MenuShotDofText}"
                    : $"{PhotoTitle}   LEFT STICK MOVE   RIGHT STICK LOOK   FOV {_photoFov:0}{MenuShotDofText}";
            float lw = font.MeasureCanvas(line, small) + 10 * u;
            Rect(list, width / 2f - lw / 2, 6 * u, width / 2f + lw / 2, 6 * u + hintH + 2 * u, (0, 0, 0), 0.55f);
            Label(font, list, line, width / 2f, 6 * u, hintH + 2 * u, small, HudPale, 0.95f);
        }
        if (toast != null)
        {
            float th = 12 * u, tw = font.MeasureCanvas(toast, small) + 12 * u;
            float ty = help ? 20 * u : 8 * u;
            Pill(list, width / 2f - tw / 2, ty, width / 2f + tw / 2, ty + th, u, true);
            Label(font, list, toast, width / 2f, ty, th, small, HudWhite, 1);
        }
        _replayHudDrawer.Draw(list, width, height, smooth: false);
    }

    // one row of pills [from, to) centred along the bottom at top, each one's key under it
    void PhotoPillRow(MenuFont font, UiDrawList list, string[] keys, int from, int to, float top, int width, float u, float text,
        float small, float hintH)
    {
        float pillH = 16 * u, gap = 3 * u, edge = 6 * u;
        Span<float> widths = stackalloc float[to - from];
        float total = 0;
        for (int i = from; i < to; i++)
        {
            widths[i - from] = MathF.Max(font.MeasureCanvas(PhotoPillText[i], text), font.MeasureCanvas(keys[i], small)) + 10 * u;
            total += widths[i - from] + (i > from ? gap : 0);
        }
        float scale = total > width - 2 * edge ? (width - 2 * edge) / total : 1;
        float x = (width - total * scale) / 2;
        for (int i = from; i < to; i++)
        {
            float pw = widths[i - from] * scale;
            Pill(list, x, top, x + pw, top + pillH, u, _photoPillHeld[i] >= 0);
            Label(font, list, PhotoPillText[i], x + pw / 2, top, pillH, text * MathF.Min(1, scale * 1.1f), i == 0 ? HudPale : HudOrange, 1);
            Hint(font, list, keys[i], x + pw / 2, top + pillH + 2 * u, hintH, small, u);
            _photoPillRects[i] = (x, top, x + pw, top + pillH);
            x += pw + gap * scale;
        }
    }
}
