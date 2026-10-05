using Android.App;
using Android.Content;
using Android.Opengl;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Javax.Microedition.Khronos.Opengles;
using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities; // PlayerAnimation -- the game's biped clip indices
using MphRecomp.Game;   // GameSession, ICollision, PlayerController -- the live sim
using MphRecomp.Input;  // RawInput / RawAxis -- gamepad -> sim input
using MphRecomp.Sim;    // RenderSnapshot -- the per-frame render list + camera
using MphRecomp.Render; // GeometryBaker -- DS display lists -> GL batches (world-baked or skinned)
using MphRecomp.Anim;   // BipedAnimator / HunterRig -- the game's per-frame hunter animation
using Matrix = Android.Opengl.Matrix;

namespace MphRecomp.App;

// M1 increment 2 (textured): bake real MPH geometry AND its textures.
// GeometryBaker batches triangles by texture, captures UVs from the DS TEXCOORD
// commands, and hands back each batch's pixels; the renderer uploads a GL texture
// per batch and draws it. Untextured materials get a 1x1 white texture so the same
// shader (texture x vertex-colour x light) covers both.
[Activity(Name = "com.mphrecomp.app.RenderActivity", Label = "mph-recomp render",
    Exported = BuildFlags.ExportDevActivities, ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape,
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize)]
public class RenderActivity : Activity
{
    SceneRenderer _renderer = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string? model = Intent?.GetStringExtra("model");
        string room = Intent?.GetStringExtra("room") ?? "MP1 SANCTORUS";
        // HD model (user-supplied Brawl trophy, exported to a .dae the user dropped in the app's
        // external files dir -- never bundled). --es hd DolKanden loads external/hd/DolKanden.dae.
        string? hd = Intent?.GetStringExtra("hd");
        // --es hd DolKanden --es hdroom "MP1 SANCTORUS": drop the HD model INTO a room (in-game preview).
        string? hdRoom = Intent?.GetStringExtra("hdroom");
        string hdDir = GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        // --es gxeval 1: render HD trophies through the NEW data-driven GX-TEV interpreter (Phase 2)
        // instead of the hand-tuned shader paths. Gated + off by default so the proven renderer is intact
        // -- brought up ALONGSIDE for A/B validation before the eventual switch-over (Phase 4).
        // --es gxcompare 1: drop TWO copies of the HD model in the room (needs --es hdroom) -- LEFT via
        // the old hand-tuned shader, RIGHT via the interpreter -- to walk around both under one light.
        bool gxCompare = Intent?.GetStringExtra("gxcompare") == "1";
        bool gxEval = Intent?.GetStringExtra("gxeval") == "1" || gxCompare;
        // --es jiggle 1: demo procedural spring-jiggle on hair parts (left stick swings it).
        bool jiggle = Intent?.GetStringExtra("jiggle") == "1";
        // --es nobloom 1: disable the bloom post-process -- to see the raw data-driven render (does the
        // additive/emissive material read as energy on its own, without the added glow?).
        bool noBloom = Intent?.GetStringExtra("nobloom") == "1";
        // --es bloom 1: keep the bloom glow on even with an HD trophy on screen (off by default there: it washed the
        // bright suits out -- owner's call, 2026-09-26 -- and neither Brawl nor BrawlCrate has it)
        bool forceBloom = Intent?.GetStringExtra("bloom") == "1";
        // --es hudHunter Samus: which hunter's gameplay-HUD styling to preview (normal-room mode only,
        // placeholder data -- see docs/HUD_APPROACH.md). D-pad left/right cycles it live on-device too.
        string hudHunter = Intent?.GetStringExtra("hudHunter") ?? "Samus";
        // --es hudDump 1: one-off diagnostic, raw-decodes every hunter's HUD frame layers to
        // <externalFilesDir>/hud_debug/*.png for offline inspection (see DumpAllHunterLayers).
        bool hudDump = Intent?.GetStringExtra("hudDump") == "1";
        // --es animview Kanden: third-person hunter animation viewer in the room -- the hunter is driven
        // by the game's own biped movement + animation logic (see MphRecomp.Anim). Normal rooms also get
        // live-animated hunters at their PlayerSpawns; --es statichunters 1 restores the old baked
        // idle statues (kept for A/B regression comparison against the GPU-skinned path).
        string? animView = Intent?.GetStringExtra("animview");
        bool staticHunters = Intent?.GetStringExtra("statichunters") == "1";
        // --es animdemo 1 (with animview): loop a scripted input sequence instead of the controller,
        // restarting from the spawn each loop; 2 = the same script WITHOUT the restart (a wandering
        // soak test for collision -- the hunter must never leave the map); 3 = the auto combat demo
        // (charge/shoot on the upper body while the legs stand and walk; Y in drive mode starts it too)
        int animDemo = int.TryParse(Intent?.GetStringExtra("animdemo"), out int ad) ? ad : 0;
        // --es animhd Kanden (with animview): the viewer's hunter wears that Brawl trophy from hd/<name>/,
        // auto-rigged to its DS skeleton (fit cached as <dae>.mphrig) and animated by the game's clips.
        // Add --es gxeval 1 to shade it with the GX-TEV interpreter instead of the old hand-tuned shader.
        string? animHd = Intent?.GetStringExtra("animhd");
        // --es animrig straight|clean|tpose|hinge|rigid (with animhd; default rigid, else clean): start on that alternative rig (<dae>.<key>.mphrig, prepared
        // on the PC, see TrophyRigIO.Variants) instead of the rig bound as sculpted; L2 / Select steps through every
        // rig the trophy has, live
        string? animRig = Intent?.GetStringExtra("animrig");
        _renderer = new SceneRenderer(FilesDir!.AbsolutePath, model, room, hd, hdDir, hdRoom, gxEval, gxCompare, jiggle, noBloom, hudHunter, hudDump, animView, staticHunters, animDemo, animHd, animRig, forceBloom);
        // --es animorbit 180: start the viewer camera orbited this many degrees around the hunter
        // (180 = looking at its front) -- for captures, since adb can't hold L1/R1
        if (float.TryParse(Intent?.GetStringExtra("animorbit"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float orbitDeg))
            _renderer.SetOrbitDegrees(orbitDeg);
        // --es animclip 2: start the viewer in clip mode on that PlayerAnimation index (captures/review)
        if (int.TryParse(Intent?.GetStringExtra("animclip"), out int startClip) && startClip >= 0)
            _renderer.AnimCycleClip(startClip + 1);   // from drive mode, +1 lands on clip 0, +n+1 on clip n
        // --es animframe 7 (with animclip): hold that clip on frame 7 -- identical poses for DS/HD comparisons
        if (int.TryParse(Intent?.GetStringExtra("animframe"), out int holdFrame) && holdFrame >= 0)
            _renderer.HoldFrame = holdFrame;
        // --es animcompare 0 (with animhd): start without the side-by-side (trophy sculpt | rigged | game model)
        _renderer.SetCompare(animHd != null && Intent?.GetStringExtra("animcompare") != "0");
        _renderer.StatusSink = s => RunOnUiThread(() => { _animStatus = s; UpdateHud(); });
        var view = new GLSurfaceView(this);
        view.SetEGLContextClientVersion(3);
        // 24-bit depth (the default config gave 16): layered trophy detail sits a few thousandths of a unit apart --
        // Dark Samus's phazon orbs under her shoulder shell, eyelashes over eyes -- and at the close-up near planes
        // (0.02-0.07) 16 bits can't tell those surfaces apart, so the layer underneath punched through
        view.SetEGLConfigChooser(8, 8, 8, 8, 24, 0);
        view.SetRenderer(_renderer);

        // On-screen HUD of the live-tunable values (reflection/exposure/blend/etc), so values can be
        // read off the screen and reported back instead of guessing or digging through logcat.
        var root = new FrameLayout(this);
        root.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _hud = new TextView(this) { TextSize = 13f };
        _hud.SetTextColor(Android.Graphics.Color.White);
        _hud.SetShadowLayer(3f, 1f, 1f, Android.Graphics.Color.Black);
        _hud.SetPadding(24, 16, 24, 16);
        root.AddView(_hud, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Top | GravityFlags.Left });
        if (animView != null)
        {
            // viewer camera buttons (touch): free camera on/off, and back to the default view. Not focusable, so the
            // controller's buttons and D-pad keep going to the viewer.
            var bar = new LinearLayout(this) { Orientation = Orientation.Vertical };
            Android.Widget.Button Btn(string text, Action onClick)
            {
                var b = new Android.Widget.Button(this) { Text = text, Focusable = false, FocusableInTouchMode = false, TextSize = 14f };
                b.SetAllCaps(false);
                b.Alpha = 0.8f;
                b.Click += (_, _) => onClick();
                bar.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
                return b;
            }
            Btn("Free camera", () => _renderer.ToggleFreeCam());
            Btn("Reset view", () => _renderer.AnimResetPosition());
            root.AddView(bar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
            { Gravity = GravityFlags.Top | GravityFlags.Right, TopMargin = 220, RightMargin = 16 });
        }
        SetContentView(root);
        HideSystemUi();
        UpdateHud();
    }

    TextView _hud = null!;
    string _animStatus = "";
    void UpdateHud() => _hud.Text = _renderer.IsAnimView ? _animStatus :
        $"refl={_renderer.ReflIntensity:0.00}  exposure={_renderer.Exposure:0.00}  lerp={_renderer.Lerp:0.00}\n" +
        $"swap={_renderer.Swap}  debug={_renderer.Debug}  walk={_renderer.Walk}" +
        (_renderer.IsHdView ? "" : $"\nhud-hunter={_renderer.CurrentHunterName} (D-pad L/R to cycle)");

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemUi();
    }

    void HideSystemUi() => Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(
        SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen |
        SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);

    static float Dead(float v) => Math.Abs(v) < 0.15f ? 0f : v;

    float _hatX, _hatY; // edge-detect state for the D-pad-as-HAT-axis workaround below
    float _r2Axis;       // edge-detect state for the R2-as-analog-trigger reset fallback below
    float _l2Axis;       // edge-detect state for L2-as-analog-trigger (animation viewer: flip the trophy's rig)

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e != null && e.Source.HasFlag(InputSourceType.Joystick) && e.Action == MotionEventActions.Move)
        {
            _renderer.Lx = Dead(e.GetAxisValue(Axis.X));
            _renderer.Ly = Dead(e.GetAxisValue(Axis.Y));
            _renderer.Rx = Dead(e.GetAxisValue(Axis.Z));
            _renderer.Ry = Dead(e.GetAxisValue(Axis.Rz));

            // Many controllers (this device included, apparently) report the D-pad as a HAT axis
            // over the joystick source, not as KeyEvent DPAD_* -- so OnKeyDown's HD-tuning binds
            // (reflection gloss / exposure) never fired here. Edge-detect the hat here instead so
            // a held press fires once, same as a KeyEvent with RepeatCount==0 would.
            float hx = e.GetAxisValue(Axis.HatX), hy = e.GetAxisValue(Axis.HatY);
            // Same story for R2: on many controllers it's an analog trigger axis, not a KeyEvent,
            // so the OnKeyDown ButtonR2 reset bind below may never fire. Cover both.
            float r2 = e.GetAxisValue(Axis.Rtrigger);
            if (_renderer.IsAnimView)
            {
                // Animation viewer: D-pad L/R = switch hunter, U/D = browse clips (manual mode),
                // R2 (analog trigger on this device) = shoot.
                if (hx <= -0.5f && _hatX > -0.5f) _renderer.AnimCycleHunter(-1);
                if (hx >= 0.5f && _hatX < 0.5f) _renderer.AnimCycleHunter(+1);
                if (hy <= -0.5f && _hatY > -0.5f) _renderer.AnimCycleClip(-1);
                if (hy >= 0.5f && _hatY < 0.5f) _renderer.AnimCycleClip(+1);
                _r2Trigger = r2 >= 0.5f;
                _renderer.FireHeld = _r2Trigger || _r2Key;
                // L2 (an analog trigger here too) = flip the HD trophy between its two rigs
                float l2 = e.GetAxisValue(Axis.Ltrigger);
                if (l2 >= 0.5f && _l2Axis < 0.5f) _renderer.FlipHdRig();
                _l2Axis = l2;
            }
            else if (_renderer.IsHdView)
            {
                bool changed = false;
                if (hx <= -0.5f && _hatX > -0.5f) { _renderer.AdjustRefl(-0.1f); changed = true; }
                if (hx >= 0.5f && _hatX < 0.5f) { _renderer.AdjustRefl(+0.1f); changed = true; }
                if (hy <= -0.5f && _hatY > -0.5f) { _renderer.AdjustExposure(+0.08f); changed = true; }
                if (hy >= 0.5f && _hatY < 0.5f) { _renderer.AdjustExposure(-0.08f); changed = true; }
                if (r2 >= 0.5f && _r2Axis < 0.5f) { _renderer.ResetTuning(); changed = true; }
                if (changed) UpdateHud();
            }
            else
            {
                // Gameplay-HUD preview (normal room, not an HD-trophy view): D-pad left/right cycles
                // which hunter's styling is shown. Same hat-axis edge-detect as above -- this exact
                // Odin unit reports its D-pad as a HAT axis, not KEYCODE_DPAD_*, confirmed earlier.
                if (hx <= -0.5f && _hatX > -0.5f) { _renderer.CycleHunter(-1); UpdateHud(); }
                if (hx >= 0.5f && _hatX < 0.5f) { _renderer.CycleHunter(+1); UpdateHud(); }
            }
            _hatX = hx; _hatY = hy; _r2Axis = r2;
            return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    bool _r2Trigger, _r2Key; // R2 may arrive as an analog axis or a key, depending on the controller
    bool _l3, _r3;            // stick clicks held (both together = free camera)

    // Animation viewer, touchscreen: pinch = zoom, one-finger drag = orbit (sideways) / look higher or lower (up-down)
    ScaleGestureDetector? _pinch;
    float _dragX, _dragY; bool _dragging;
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null || !_renderer.IsAnimView) return base.OnTouchEvent(e);
        _pinch ??= new ScaleGestureDetector(this, new PinchZoom(_renderer));
        _pinch.OnTouchEvent(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down: _dragX = e.GetX(); _dragY = e.GetY(); _dragging = true; break;
            case MotionEventActions.PointerDown: _dragging = false; break;   // a second finger: pinch only
            case MotionEventActions.Move:
                if (_dragging && e.PointerCount == 1 && !_pinch.IsInProgress)
                {
                    float dx = e.GetX() - _dragX, dy = e.GetY() - _dragY;
                    if (_renderer.FreeCam) _renderer.FreeLook(dx * 0.003f, dy * 0.003f);
                    else { _renderer.OrbitBy(dx * 0.003f); _renderer.LiftBy(-dy * 0.002f); }
                    _dragX = e.GetX(); _dragY = e.GetY();
                }
                break;
            case MotionEventActions.Up: case MotionEventActions.Cancel: _dragging = false; break;
        }
        return true;
    }
    sealed class PinchZoom : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        readonly SceneRenderer _r;
        public PinchZoom(SceneRenderer r) { _r = r; }
        public override bool OnScale(ScaleGestureDetector? d)
        {
            if (d != null) { if (_r.FreeCam) _r.FreeDolly(d.ScaleFactor); else _r.ZoomBy(d.ScaleFactor); }
            return true;
        }
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (_renderer.IsAnimView)
        {
            bool first = e?.RepeatCount == 0;
            switch (keyCode)
            {
                case Keycode.ButtonA: if (first) _renderer.PressJump(); return true;
                case Keycode.ButtonY: if (first) _renderer.AnimDriveMode(); return true;
                // X = morph ball (as in campaign mode); L1 + R1 together = back to the spawn (X's old job)
                case Keycode.ButtonX: if (first) _renderer.AnimMorph(); return true;
                case Keycode.ButtonR2: _r2Key = true; _renderer.FireHeld = true; return true;
                case Keycode.ButtonL1: if (first && _renderer.OrbitRight) _renderer.AnimResetPosition(); _renderer.OrbitLeft = true; return true;
                case Keycode.ButtonR1: if (first && _renderer.OrbitLeft) _renderer.AnimResetPosition(); _renderer.OrbitRight = true; return true;
                case Keycode.DpadLeft: if (first) _renderer.AnimCycleHunter(-1); return true;
                case Keycode.DpadRight: if (first) _renderer.AnimCycleHunter(+1); return true;
                case Keycode.DpadUp: if (first) _renderer.AnimCycleClip(-1); return true;
                case Keycode.DpadDown: if (first) _renderer.AnimCycleClip(+1); return true;
                case Keycode.ButtonL2:
                case Keycode.ButtonSelect: if (first) _renderer.FlipHdRig(); return true;
                case Keycode.ButtonStart: if (first) _renderer.ToggleCompare(); return true;
                // L3 = zoom in, R3 = zoom out; BOTH together = free camera on/off
                case Keycode.ButtonThumbl:
                    if (first) { _l3 = true; if (_r3) _renderer.ToggleFreeCam(); }
                    if (!_r3) _renderer.ZoomBy(1.2f);
                    return true;
                case Keycode.ButtonThumbr:
                    if (first) { _r3 = true; if (_l3) _renderer.ToggleFreeCam(); }
                    if (!_l3) _renderer.ZoomBy(1f / 1.2f);
                    return true;
            }
            // B / Back fall through to the shared exit-to-picker handling below
        }
        // HD trophy viewer live tuning: X/Y = blend toward C0/C1 (duochrome metal);
        // Dpad Left/Right = reflection gloss down/up; Dpad Up/Down = brighter/dimmer; L3/R3 = swap C0/C1;
        // R2 = reset all tuning to defaults.
        if (_renderer.IsHdView && e?.RepeatCount == 0)
        {
            if (keyCode == Keycode.ButtonX) { _renderer.AdjustLerp(-0.05f); UpdateHud(); return true; }
            if (keyCode == Keycode.ButtonY) { _renderer.AdjustLerp(+0.05f); UpdateHud(); return true; }
            if (keyCode == Keycode.DpadLeft) { _renderer.AdjustRefl(-0.1f); UpdateHud(); return true; }
            if (keyCode == Keycode.DpadRight) { _renderer.AdjustRefl(+0.1f); UpdateHud(); return true; }
            if (keyCode == Keycode.DpadUp) { _renderer.AdjustExposure(+0.08f); UpdateHud(); return true; }
            if (keyCode == Keycode.DpadDown) { _renderer.AdjustExposure(-0.08f); UpdateHud(); return true; }
            if (keyCode == Keycode.ButtonThumbl || keyCode == Keycode.ButtonThumbr) { _renderer.ToggleSwap(); UpdateHud(); return true; }
            if (keyCode == Keycode.ButtonSelect || keyCode == Keycode.ButtonStart) { _renderer.ToggleDebug(); UpdateHud(); return true; }
            if (keyCode == Keycode.ButtonR2) { _renderer.ResetTuning(); UpdateHud(); return true; }
        }
        else if (e?.RepeatCount == 0)
        {
            // Gameplay-HUD preview: D-pad left/right cycles the previewed hunter's styling (mirrors
            // the hat-axis handling in OnGenericMotionEvent for controllers whose D-pad reports as
            // real KeyEvents instead).
            if (keyCode == Keycode.DpadLeft) { _renderer.CycleHunter(-1); UpdateHud(); return true; }
            if (keyCode == Keycode.DpadRight) { _renderer.CycleHunter(+1); UpdateHud(); return true; }
        }
        if (keyCode == Keycode.ButtonR1) { _renderer.Up = true; return true; }
        if (keyCode == Keycode.ButtonL1) { _renderer.Down = true; return true; }
        if (keyCode == Keycode.ButtonA && e?.RepeatCount == 0) { _renderer.Walk = !_renderer.Walk; UpdateHud(); return true; }
        if ((keyCode == Keycode.Back || keyCode == Keycode.ButtonB) && e?.RepeatCount == 0)
        {
            // return to the room picker instead of exiting the app
            StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
            Finish();
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (_renderer.IsAnimView)
        {
            if (keyCode == Keycode.ButtonL1) { _renderer.OrbitLeft = false; return true; }
            if (keyCode == Keycode.ButtonR1) { _renderer.OrbitRight = false; return true; }
            if (keyCode == Keycode.ButtonR2) { _r2Key = false; _renderer.FireHeld = _r2Trigger; return true; }
            if (keyCode == Keycode.ButtonThumbl) { _l3 = false; return true; }
            if (keyCode == Keycode.ButtonThumbr) { _r3 = false; return true; }
        }
        if (keyCode == Keycode.ButtonR1) { _renderer.Up = false; return true; }
        if (keyCode == Keycode.ButtonL1) { _renderer.Down = false; return true; }
        return base.OnKeyUp(keyCode, e);
    }
}

internal sealed class DrawBatch { public DrawBatch At(int start, int count) { var c = (DrawBatch)MemberwiseClone(); c.Start = start; c.Count = count; return c; } public int Tex; public int Start; public int Count; public bool Texgen; public bool Metallic; public bool Unlit; public bool Textured; public bool MultiTex; public int MatKind; public bool CombineSwap; public float LerpScale = 2f; public float ReflScale = 2f; public float UnlitScale = 1f; public float[]? ColB; public float[] Ambient = { 1f, 1f, 1f }; public int IncandTex = -1, SpecTex = -1, ReflTex = -1; public bool AlphaCutout; public int OpacityTex = -1; public bool Additive; public bool Translucent; public MphRecomp.Assets.GxMaterial? Gx; public int[]? GxTex; public int[]? GxTexGen; public MphRecomp.Render.GxShader.Params? GxP; public bool Jiggle; public float JigTop, JigBot; public float[]? SkinPalette; public int SkinCount; public float[]? SkinModel; }

internal sealed class SceneRenderer : Java.Lang.Object, GLSurfaceView.IRenderer
{
    const int Stride = 11 * sizeof(float); // pos3 + normal3 + uv2 + colour3

    readonly string _filesDir;
    readonly string? _model;
    readonly string _room;
    readonly string? _hd;       // HD model name (Brawl trophy .dae), null otherwise
    readonly string _hdDir;     // external files dir the user drops HD models into
    int _program, _vao, _uMvp, _uTex, _uViewRot, _uTexgen, _uMetallic, _uUnlit, _uColB, _uLerp, _uExposure, _uSwap, _uDebug, _uMatId, _uLerpScale, _uReflScale, _uTextured, _uMultiTex, _uIncand, _uSpec, _uReflTex, _uUnlitScale, _uAmbient, _uCutout, _uOpacity, _uTranslucent;
    int _uJigOn, _uJigD, _uJigTop, _uJigBot;   // procedural hair-jiggle uniforms (old shader)
    float _jigDx, _jigDz, _jigVx, _jigVz;       // spring state (lateral world-space displacement + velocity)
    // PHASE 2 GX-TEV interpreter program + uniform locations (only built/used when _gxEval).
    int _gxProgram, _gxMvp, _gxViewRot;
    readonly int[] _gxTexUnits = new int[8];   // uTex0..uTex7 sampler locations
    readonly Dictionary<string, int> _gxU = new();   // GX program uniform locations by name
    int _passMode;   // the pass being drawn: 0 opaque, 1 alpha-blended, 2 additive (DrawGx restores its state)

    // ---- Gameplay HUD (presentation layer, v1: visuals + all 7 hunters, placeholder data) ---------
    int _hudProgram, _hudVao, _hudUPos, _hudUSize, _hudURot, _hudUFlip, _hudUColor, _hudUTex, _hudUUvScale, _hudUUvOffset;
    int _hudHunterIdx;
    readonly int[] _hudCornerTex = new int[7];
    readonly int[] _hudReticleTex = new int[7];
    int _hudRingTex, _hudSweepTex;
    readonly Dictionary<string, (int Tex, float Aspect)> _hudTextCache = new();
    readonly Dictionary<string, (int Tex, int W, int H)> _hudFrameCache = new(); // real per-hunter visor-frame art
    public void CycleHunter(int d)
    {
        _hudHunterIdx = ((_hudHunterIdx + d) % HunterStyles.Length + HunterStyles.Length) % HunterStyles.Length;
        Log.Info("MPHRender", $"HUD hunter -> {HunterStyles[_hudHunterIdx].Name}");
    }
    public string CurrentHunterName => HunterStyles[_hudHunterIdx].Name;

    // Duochrome (HD trophies): the lit metal is the model's real TEV program -- it blends between the
    // two real register colours (C0,C1) by the lighting, times the reflection. Fully data-driven; the
    // only free knob is a small blend offset + exposure (scene lighting isn't stored in the model).
    public volatile float Lerp = 0.10f;     // blend offset added to the computed lighting factor
    public volatile float Exposure = 1.0f;  // global brightness
    public volatile float ReflIntensity = 1.0f; // HD interpreter: env-map reflection/gloss scale (1 = full)
    public volatile bool Swap = false;      // flip C0/C1 order (armour lerps the opposite direction)
    public volatile bool Debug = false;     // flat-colour each material by id (region check)
    public bool IsHdView => _hd != null;
    public void ToggleDebug() { Debug = !Debug; Log.Info("MPHRender", $"FLIP debug-regions={Debug} (orange=green-mat, blue=vioret-mat, green=ports)"); }
    public void AdjustLerp(float d)
    {
        Lerp = Math.Clamp(Lerp + d, 0f, 1f);
        Log.Info("MPHRender", $"FLIP lerp={Lerp:0.00} exposure={Exposure:0.00} swap={Swap}");
    }
    public void AdjustExposure(float d)
    {
        Exposure = Math.Clamp(Exposure + d, 0.3f, 2.5f);
        Log.Info("MPHRender", $"FLIP lerp={Lerp:0.00} exposure={Exposure:0.00} swap={Swap}");
    }
    public void AdjustRefl(float d)
    {
        ReflIntensity = Math.Clamp(ReflIntensity + d, 0f, 2f);
        Log.Info("MPHRender", $"FLIP reflIntensity={ReflIntensity:0.00} exposure={Exposure:0.00}");
    }
    public void ToggleSwap()
    {
        Swap = !Swap;
        Log.Info("MPHRender", $"FLIP lerp={Lerp:0.00} exposure={Exposure:0.00} swap={Swap}");
    }
    public void ResetTuning()
    {
        var (refl, exposure) = (_hd != null && HdTuningDefaults.TryGetValue(_hd, out var def)) ? def : (1.0f, 1.0f);
        Lerp = 0.10f; Exposure = exposure; ReflIntensity = refl; Swap = false; Debug = false;
        Log.Info("MPHRender", $"FLIP reset -> defaults (refl={refl:0.00} exposure={exposure:0.00})");
    }
    readonly float[] _viewRot = new float[9]; // upper-3x3 of _view for texgen
    readonly List<DrawBatch> _batches = new();
    readonly float[] _center = new float[3];
    float _radius = 10f;         // playable radius (rooms: from collision) -> camera speed/eye/spawn
    float _renderRadius = 10f;   // full rendered extent incl. skybox -> far clip plane
    float _hdFrameR = 1f;        // HD-in-room: the placed hunter's height, drives camera framing
    float _cmpHalfW = 3f;        // gxcompare: orthographic half-width to frame both copies head-on
    float[]? _hdBladeC;          // HD-in-room: world centroid of the additive (blade) batches, for close-ups
    float _hdSpawnX, _hdSpawnY, _hdSpawnZ, _hdSpawnYaw; bool _hdHasSpawn; // gxcompare: floor-level spawn for the real walk/collision session (distinct from the close-up framing camera below)
    long _last;
    readonly float[] _proj = new float[16], _view = new float[16], _mvp = new float[16];
    // BLOOM post-process (HD view only): the Brawl trophy renderer glows bright pixels so the energy
    // blade shimmers and its hot core reads yellow-white. That's a scene effect, not model data, so we
    // reproduce it here. Render the scene to an offscreen buffer, extract + blur the bright parts at
    // half res, then add that glow back. Gated to IsHdView so the real game render is untouched.
    int _vw, _vh;                                   // current viewport size
    int _fbScene, _texScene, _rbDepth;              // full-res scene target (colour + depth)
    int _fbBloomA, _texBloomA, _fbBloomB, _texBloomB; // half-res ping-pong for extract/blur
    int _fsQuad;                                    // fullscreen-triangle VAO for the post passes
    int _progBright, _progBlur, _progComp;          // extract, separable blur, composite programs
    int _uBrightTex, _uBrightThresh, _uBlurTex, _uBlurDir, _uCompScene, _uCompBloom, _uCompIntensity;
    bool _bloomOk;                                  // false if FBO setup failed -> render straight to screen
    float _bloomThreshold = 0.55f;                  // bright-pass cutoff; raised for the light-backdrop view

    // free-fly camera (fed by the gamepad from the activity, integrated each frame)
    readonly float[] _pos = new float[3];
    float _yaw, _pitch = -0.1f;
    public volatile float Lx, Ly, Rx, Ry;
    public volatile bool Up, Down;
    public volatile bool Walk = true;   // rooms: true = live sim play; false = free-fly inspect (A toggles). models: always free-fly
    float[] _tri = System.Array.Empty<float>(); // CPU triangle positions (3 floats/vert) for floor raycasts

    // The live game: for a room, the built GameSession runs the sim (PlayerController walks the
    // collision, mods tick, entities update) and we render its snapshot. Null for the model viewer.
    GameSession? _session;
    readonly RawInput _raw = new();
    long _lastSimLog;

    // Adapts the renderer's CPU collision raycasts to the sim's ICollision (nested -> can reach the
    // enclosing renderer's private _tri helpers). Sim positions are in the same world units as _tri.
    sealed class TriCollision : ICollision
    {
        readonly SceneRenderer _r;
        public TriCollision(SceneRenderer r) => _r = r;
        public float FloorBelow(System.Numerics.Vector3 pos)
        {
            float f = _r.FloorBelow(pos.X, pos.Y, pos.Z);
            return f <= -1e8f ? float.NegativeInfinity : f;
        }
        public bool Blocked(System.Numerics.Vector3 pos, System.Numerics.Vector3 dir, float dist)
            => _r.BlockedHoriz(pos.X, pos.Y, pos.Z, dir.X, dir.Z, dist);
    }

    readonly string? _hdRoom;   // when set with _hd: render the HD model inside this room (in-game preview)
    readonly bool _gxEval;     // Phase 2: run HD trophies through the GX-TEV interpreter program (gated)
    readonly bool _gxCompare;  // Phase 2: A/B two copies in-room (old shader vs interpreter)
    readonly bool _jiggle;     // demo: procedural spring-jiggle on hair parts
    readonly bool _noBloom;    // disable bloom post-process (see the raw data-driven render)
    readonly bool _forceBloom; // --es bloom 1: bloom even with an HD trophy shown

    // Per-model tuning defaults for the GX-TEV interpreter, found by eye via the on-screen HUD + gxCompare
    // (R2 resets to these, not a flat 1.0/1.0). Most hunters read correctly at the neutral default; add
    // an entry here only once a model has actually been dialed in and confirmed against the old shader.
    static readonly Dictionary<string, (float Refl, float Exposure)> HdTuningDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // SamusR1 interpreter read washed-out at neutral: her material sums two env-reflection
            // layers into the color BEFORE the TEV program's own x4 lighting multiply, so any reflection
            // over-brightness gets quadrupled. refl=0.50/exposure=0.68 confirmed by eye on-device.
            ["SamusR1"] = (0.50f, 0.68f),
        };

    readonly bool _hudDump;

    // ---- Live hunter animation (MphRecomp.Anim: the game's biped animation, GPU-skinned) ----------
    // Hunters are baked ONCE per model into bone-local "skinned" batches (stride 12: + matrix-stack
    // slot) in their own VBO; every frame each hunter's two clip layers are posed on the CPU into a
    // bone palette (HunterRig.Pose) and the vertex shader skins with it. Replaces the old frozen idle
    // statues (still available with --es statichunters 1 for A/B comparison).
    static readonly Hunter[] AnimHunters = { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel };
    // GPU skinned vertex: pos3 normal3 uv2 colour3 + 4 bone indices + 4 weights (DS and HD share it)
    const int GpuSkinFloats = 19, SkinStride = GpuSkinFloats * sizeof(float);
    const float TickSeconds = 1f / 60f;
    sealed class SkinnedTemplate { public Model Model = null!; public Hunter Hunter; public readonly List<DrawBatch> Batches = new(); }
    // A Brawl trophy rigged to a DS hunter skeleton (TrophyRigger), drawn in place of that hunter's DS mesh.
    sealed class HdTemplate
    {
        public string Id = ""; public Hunter Hunter; public Model Model = null!; public DsSkeleton Skeleton = null!;
        public TrophyRig Rig = null!; public readonly List<DrawBatch> Batches = new();
        public SkeletonPose Pose = null!; public OpenTK.Mathematics.Matrix4[] World = null!;
        public string Label = "";
        public bool[]? OffArm;                      // (pirates) the blade arm's nodes, re-sampled from the legs layer while firing
    }
    HdTemplate? _hdTemplate;
    HdTemplate? _hdSculpt;                        // the sculpt-bound rig (the compare view's statue at rest), even when not offered
    // the trophy's other rig for side-by-side judging (L2 / Select flips): bound as sculpted <-> straightened
    readonly List<HdTemplate> _hdRigs = new();   // every rig prepared for the trophy: sculpt-bound first, then the variants
    readonly string? _animRig;                    // --es animrig <variant key>: start on that one
    int _pendingRigFlip;
    long _lastRigFlip;
    public void FlipHdRig()
    {
        // one press may arrive as both a key and a trigger axis: a second flip within 300 ms is the same press
        long now = System.Environment.TickCount64;
        if (now - _lastRigFlip < 300) return;
        _lastRigFlip = now;
        System.Threading.Interlocked.Exchange(ref _pendingRigFlip, 1);
    }
    readonly string? _animHdId;   // --es animhd <trophy>: the viewer's hunter wears this rigged trophy
    readonly Dictionary<Hunter, SkinnedTemplate> _skinTemplates = new();
    readonly List<(Hunter Hunter, OpenTK.Mathematics.Vector3 Pos, OpenTK.Mathematics.Vector3 Facing)> _spawnHunters = new();
    readonly List<HunterDummy> _actors = new();   // every animated hunter in the scene
    HunterDummy? _driven;                          // animview: the controllable one (also in _actors)
    int _skinVao;
    int _uSkin, _uModel, _uBones;
    int _gxSkin = -1, _gxModel = -1, _gxBones = -1;
    readonly float[] _palette = new float[16 * 32], _modelMtx = new float[16];
    float _animAcc;
    readonly bool _staticHunters;
    Hunter? _animHunter;                           // non-null = animation viewer mode
    int _animClip = -1;                            // animview: manual clip index, -1 = drive mode
    float _orbit;                                  // animview: camera orbit offset around the hunter (radians)
    long _lastStatus;
    public bool IsAnimView => _animHunter != null;
    public volatile bool FireHeld, OrbitLeft, OrbitRight;
    int _pendingJump;
    public void PressJump() => System.Threading.Interlocked.Exchange(ref _pendingJump, 1);
    public Action<string>? StatusSink;
    // UI-thread requests, consumed on the GL thread (Interlocked so no press is lost between threads)
    int _pendingHunterStep, _pendingClipStep, _pendingDrive, _pendingReset;
    public void AnimCycleHunter(int d) => System.Threading.Interlocked.Add(ref _pendingHunterStep, d);
    public void AnimCycleClip(int d) => System.Threading.Interlocked.Add(ref _pendingClipStep, d);
    public void AnimDriveMode() => System.Threading.Interlocked.Exchange(ref _pendingDrive, 1);
    public void AnimResetPosition() => System.Threading.Interlocked.Exchange(ref _pendingReset, 1);
    // MORPH BALL (X): the HD suit's own ball ("<suit>Ball", HdBall) rolls where the hunter walks -- the whole ball as one
    // piece, turned to face the way it rolls and spun by the distance covered; press again to stand up
    int _pendingMorph;
    public void AnimMorph() => System.Threading.Interlocked.Exchange(ref _pendingMorph, 1);
    sealed class BallTemplate { public string Id = ""; public List<DrawBatch> Batches = new(); public OpenTK.Mathematics.Vector3 Center; public float Diameter = 1f; }
    BallTemplate? _ball;
    bool _morphed;
    float _ballHeading, _ballSpin;                 // radians: the way it rolls (facing convention), the spin so far
    OpenTK.Mathematics.Vector3? _ballLast;         // the hunter's position at the last roll step
    public void SetOrbitDegrees(float deg) => _orbit = deg * (float)Math.PI / 180f;
    // viewer camera zoom (pinch / L3 / R3) and look height (one-finger vertical drag), in hunter heights; X resets
    public volatile float Zoom = 1f, ViewLift = 0f;
    public void ZoomBy(float f) => Zoom = Math.Clamp(Zoom * f, 0.5f, 6f);
    public void LiftBy(float d) => ViewLift = Math.Clamp(ViewLift + d, -1.0f, 0.9f);
    public void OrbitBy(float radians) => _orbit += radians;
    // FREE CAMERA (L3+R3 together, or the on-screen button): detached from the hunter, who keeps animating
    public volatile bool FreeCam;
    int _pendingFree;
    OpenTK.Mathematics.Vector3 _freePos, _lastLook = -OpenTK.Mathematics.Vector3.UnitZ;
    float _freeYaw, _freePitch;
    public void ToggleFreeCam() => System.Threading.Interlocked.Exchange(ref _pendingFree, 1);
    // touch in free camera: drag = look around, pinch = move along the view
    public void FreeLook(float dx, float dy) { _freeYaw += dx; _freePitch = Math.Clamp(_freePitch - dy, -1.5f, 1.5f); }
    public void FreeDolly(float f)
    {
        var look = new OpenTK.Mathematics.Vector3(MathF.Sin(_freeYaw) * MathF.Cos(_freePitch), MathF.Sin(_freePitch), -MathF.Cos(_freeYaw) * MathF.Cos(_freePitch));
        _freePos += look * ((f - 1f) * 4f * (_driven != null ? Metadata.HunterScales[_driven.Hunter] : 1f));
    }
    public volatile int HoldFrame = -1;   // clip mode: pin both layers to this frame (paused)

    public SceneRenderer(string filesDir, string? model, string room, string? hd = null, string hdDir = "", string? hdRoom = null, bool gxEval = false, bool gxCompare = false, bool jiggle = false, bool noBloom = false, string hudHunter = "Samus", bool hudDump = false, string? animView = null, bool staticHunters = false, int animDemo = 0, string? animHd = null, string? animRig = null, bool forceBloom = false)
    {
        _filesDir = filesDir; _model = model; _room = room; _hd = hd; _hdDir = hdDir; _hdRoom = hdRoom; _gxEval = gxEval; _gxCompare = gxCompare; _jiggle = jiggle; _noBloom = noBloom; _forceBloom = forceBloom; _hudDump = hudDump;
        _staticHunters = staticHunters; _animDemo = animDemo; _animHdId = animHd; _animRig = animRig;
        if (animView != null)
        {
            if (Enum.TryParse(animView, ignoreCase: true, out Hunter vh) && Array.IndexOf(AnimHunters, vh) >= 0) _animHunter = vh;
            else { Log.Warn("MPHRender", $"animview: unknown hunter '{animView}', using Samus"); _animHunter = Hunter.Samus; }
        }
        // Rooms default to walk; a lone HD model defaults to free-fly for framing. gxCompare places static
        // HD copies IN a room as normal scenery, so it should default like any other room (walk), not the
        // HD-viewer default -- the A-button toggle still switches either way regardless of the starting mode.
        Walk = (model == null && hd == null) || gxCompare;
        if (hd != null && HdTuningDefaults.TryGetValue(hd, out var def)) { ReflIntensity = def.Refl; Exposure = def.Exposure; }
        int hi = Array.FindIndex(HunterStyles, h => string.Equals(h.Name, hudHunter, StringComparison.OrdinalIgnoreCase));
        _hudHunterIdx = Math.Max(0, hi);
    }

    const string VertSrc =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "layout(location=1) in vec3 aNormal;\n" +
        "layout(location=2) in vec2 aUv;\n" +
        "layout(location=3) in vec3 aColor;\n" +
        "layout(location=4) in vec4 aBones;\n" +   // skinned: up to 4 bone indices (DS: stack slot; HD: node)
        "layout(location=5) in vec4 aWeights;\n" + //          their weights (DS: 1,0,0,0 -- rigid, as the DS draws it)
        "uniform mat4 uMvp;\n" +
        "uniform highp mat3 uViewRot;\n" +  // view rotation, for texgen env-mapping (highp: also used in frag)
        "uniform highp int uTexgen;\n" +    // 1 = generate UVs from the view-space normal
        // Procedural jiggle: a world-space spring displacement uJigD applied to a dangly part (hair),
        // weighted by height so the scalp (uJigTop) stays put and the tip (uJigBot) sways most.
        "uniform int uJigOn; uniform vec3 uJigD; uniform float uJigTop; uniform float uJigBot;\n" +
        // GPU skinning (uSkin==1): world = uModel * (sum w_i * uBones[b_i]) * pos. DS hunters: pos is
        // bone-LOCAL, one bone at weight 1, uBones = the frame's matrix stack (HunterRig.Pose) -- the same
        // math the CPU baker used to freeze statues, verified vertex-exact headlessly (-animtest). HD
        // trophies: pos is in rig space, uBones = inverse-bind * pose (retargeted), blended joints.
        "uniform int uSkin; uniform mat4 uModel; uniform mat4 uBones[32];\n" + SkinFn +
        "out vec3 vN; out vec2 vUv; out vec3 vC;\n" +
        "void main(){\n" +
        "  vec3 p = aPos; vec3 nrm = aNormal;\n" +
        "  if (uSkin==1) skin(p, nrm);\n" +
        "  else if (uJigOn==1) { float w = clamp((aPos.y - uJigBot)/(uJigTop - uJigBot + 0.0001),0.0,1.0); p += uJigD*(w*w); }\n" +
        "  gl_Position = uMvp*vec4(p,1.0); vN=nrm; vC=aColor;\n" +
        "  if (uTexgen == 1) { vec3 n = normalize(uViewRot * nrm); vUv = n.xy*0.5 + 0.5; }\n" +
        "  else vUv = aUv;\n" +
        "}\n";

    // Shared vertex-stage skinning (main + GX interpreter programs). Needs aPos/aNormal/aBones/aWeights
    // and uModel/uBones declared by the including shader.
    const string SkinFn =
        "void skin(inout vec3 p, inout vec3 nrm){\n" +
        "  mat4 B = uBones[int(aBones.x + 0.5)] * aWeights.x + uBones[int(aBones.y + 0.5)] * aWeights.y\n" +
        "         + uBones[int(aBones.z + 0.5)] * aWeights.z + uBones[int(aBones.w + 0.5)] * aWeights.w;\n" +
        "  p = (uModel * (B * vec4(aPos, 1.0))).xyz;\n" +
        "  vec3 sn = mat3(uModel) * (mat3(B) * aNormal);\n" +
        "  nrm = dot(sn, sn) > 1e-12 ? normalize(sn) : sn;\n" +
        "}\n";

    const string FragSrc =
        "#version 300 es\n" +
        "precision mediump float;\n" +
        "in vec3 vN; in vec2 vUv; in vec3 vC; out vec4 o;\n" +
        "uniform sampler2D uTex;\n" +
        "uniform highp int uTexgen;\n" +
        "uniform highp int uMetallic;\n" +   // 1 = Brawl HD trophy: warm metallic (env-map dominates)
        "uniform highp int uUnlit;\n" +      // 1 = flat emissive material colour, no lighting/reflection
        "uniform vec3 uColB;\n" +            // second register colour C1 (vC carries C0) -- duochrome pair
        "uniform float uLerp;\n" +           // blend offset added to the lighting-driven factor
        "uniform float uExposure;\n" +       // global brightness
        "uniform highp int uSwap;\n" +       // 1 = blend C1<->C0 (armour lerps the opposite direction)
        "uniform float uLerpScale;\n" +      // GX scale on the colour-blend stage (per material: Kanden 2, Sylux 1)
        "uniform float uReflScale;\n" +      // GX scale on the reflection stage (per material: Kanden 2, Sylux 4/2)
        "uniform highp int uDebug;\n" +      // 1 = flat-colour each material by id (region check)
        "uniform highp int uMatId;\n" +      // 0 = first lit material, 1 = second, -1 = unlit (ports)
        "uniform highp int uTextured;\n" +   // 1 = real diffuse texture (Weavel body/hair), sampled by UV
        "uniform highp int uMultiTex;\n" +   // 1 = body's real 4-texture combine (diffuse+incand+spec+refl)
        "uniform sampler2D uIncand;\n" +     // incandescence (additive glow)
        "uniform sampler2D uSpec;\n" +       // spec map (masks the reflection)
        "uniform sampler2D uReflTex;\n" +    // reflection env sphere-map
        "uniform highp mat3 uViewRot;\n" +   // view rotation (highp to match the vertex stage)
        "uniform float uUnlitScale;\n" +     // GX output scale on an unlit flat material (blade = 4)
        "uniform vec3 uAmbient;\n" +         // light-channel ambient colour (white=Kanden, olive=Weavel body)
        "uniform highp int uCutout;\n" +     // 1 = hard alpha cutout from the opacity mask (hair)
        "uniform sampler2D uOpacity;\n" +    // opacity/cutout mask (weavel_hair_o), sampled by the diffuse UV
        "uniform highp int uTranslucent;\n" +// 1 = alpha-blend see-through energy (Noxus glow lines) -> emissive-bright
        "void main(){\n" +
        "  if (uDebug == 1) {\n" +           // region debug: green material=orange, vioret=blue, ports=green
        "    vec3 dc = (uMatId==0) ? vec3(1.0,0.55,0.0) : (uMatId==1) ? vec3(0.1,0.45,1.0) : vec3(0.15,1.0,0.2);\n" +
        "    o = vec4(dc, 1.0); return;\n" +
        "  }\n" +
        "  vec3 N = normalize(vN);\n" +
        "  vec3 L1 = normalize(vec3(0.4,0.85,0.5));\n" +
        "  vec3 L2 = normalize(vec3(-0.5,0.35,-0.6));\n" +
        "  float d = max(dot(N,L1),0.0) + 0.4*max(dot(N,L2),0.0);\n" +
        // Textured materials (e.g. Weavel's body/hair) take their colour from a real diffuse texture
        // sampled by the model's own UVs -- the register colours are black precisely because the colour
        // lives in the texture. Lit parts get diffuse lighting; unlit parts are flat; alpha cuts out hair.
        "  if (uTextured == 1) {\n" +
        "    vec4 tx = texture(uTex, vUv);\n" +
        // Hair is a hard alpha CUTOUT: the diffuse's own alpha is opaque (black gaps between strands),
        // so cut on the separate opacity mask (weavel_hair_o) at 0.5 -> real negative space. Other
        // textured parts keep the diffuse-alpha cutout.
        "    if (uCutout == 1) { if (texture(uOpacity, vUv).r < 0.5) discard; }\n" +
        "    else if (tx.a < 0.5) discard;\n" +
        // RASC = matColour * clamp(ambient + lights). Weavel's body chan-ctrl (0x703) enables lighting
        // but selects NO lights, so the light sum is ZERO -> RASC is just the ambient colour, flat. That
        // ambient is olive (0.4,0.4,0), which tints the whole body green (white for Kanden = neutral).
        // Adding a fake directional term is what washed it out; instead modulate the olive slightly by
        // 'd' (multiplicative, so the olive stays saturated) purely to give a little shape.
        // RASC = matColour(vertex) * clamp(ambient + lights). We REPRODUCE the Brawl trophy scene: a
        // strong directional KEY light (L1) gives the dark-recess contrast, a small fill floor keeps
        // shadows from going black. Faces toward the key saturate -> the diffuse's own colour shows;
        // faces away fall to the olive ambient -> dark olive. That contrast is the trophy depth.
        "    float keyL = max(dot(N, L1), 0.0);\n" +
        "    vec3 rasc = vC * clamp(uAmbient + vec3(0.12 + 1.15*keyL), 0.0, 1.0);\n" +
        "    vec3 rgb;\n" +
        "    if (uMultiTex == 1) {\n" +
        // The body's REAL GX combine, decoded from the .brres TEV:
        //   2*diffuse*RASC + incandescence + reflection*spec
        // Additive incand = the visor/port glow; additive reflection(env-map) masked by spec = the
        // metallic specular highlights (what made ours look soft/grey when missing).
        "      vec3 inc = texture(uIncand, vUv).rgb;\n" +
        "      vec3 spc = texture(uSpec, vUv).rgb;\n" +
        "      vec2 eUv = normalize(uViewRot * N).xy * 0.5 + 0.5;\n" +
        "      vec3 rfl = texture(uReflTex, eUv).rgb;\n" +
        // TODO(scene): uRefBoost + the 'd' lighting term below are SCENE stand-ins, not from the model
        // (the baked sphere-map is dark + Brawl's trophy lights aren't stored). Revisit if we ever
        // capture the real trophy-scene lighting. Everything else here is read from the .brres.
        // Ease the reflection (was washing the plates grey) and give the olive base more presence.
        "      rgb = clamp(1.15*tx.rgb*rasc + inc + 1.1*rfl*spc, 0.0, 1.0);\n" +
        "    } else {\n" +
        "      rgb = (uUnlit==1) ? tx.rgb : clamp(2.0*tx.rgb*rasc, 0.0, 1.0);\n" +   // diffuse-only (hair/c02)
        "    }\n" +
        // Translucent energy (Noxus's cyan glow lines/spots): emissive-bright so bloom blooms it, and
        // see-through alpha -- alpha-blended in its own pass. Data-driven (material CMODE0 dst=INVSRCA).
        "    if (uTranslucent==1) { o = vec4(clamp(rgb*1.7,0.0,1.0)*uExposure, 0.66); return; }\n" +
        "    o = vec4(rgb * uExposure, 1.0); return;\n" +
        "  }\n" +
        // Unlit materials (e.g. Kanden's ports, chan-ctrl lighting bit off) render as flat, full-
        // brightness material colour -- no diffuse, no reflection. This is stored per-material in the
        // model, and is what makes the ports read as bright glowing yellow next to the lit metal.
        "  if (uUnlit == 1) { o = vec4(clamp(vC * uUnlitScale, 0.0, 1.0), 1.0); return; }\n" +
        "  vec4 t = texture(uTex, vUv);\n" +
        "  if (uTexgen == 0 && t.a < 0.3) discard;\n" +  // cutout only for normal (uv) materials
        "  if (uMetallic == 1) {\n" +
        // The REAL Brawl combine, read from the material's TEV program (not a guessed tint):
        //   base = clamp(2 * mix(C0, C1, L));   out = clamp(2 * TEXC * base)
        // The material is DUOCHROME -- it blends between two register colours by the lighting L, then
        // multiplies by the reflection map TEXC. L varies across the surface (approximated from the
        // reflection luminance + uLerp) to give the two-tone metallic shift. uSwap flips C0/C1 order.
        "    float L = clamp(uLerp + 0.35*d, 0.0, 1.0);\n" +          // RASC ~ the diffuse lighting (computed, not a knob)
        "    vec3 cA = (uSwap==1) ? uColB : vC;\n" +
        "    vec3 cB = (uSwap==1) ? vC : uColB;\n" +
        "    vec3 base = clamp(uLerpScale * mix(cA, cB, L), 0.0, 1.0);\n" +          // stage 0: scale * lerp(C0,C1,RASC)
        "    o = vec4(clamp(uReflScale * t.rgb * base, 0.0, 1.0) * uExposure, 1.0);\n" + // stage 1: scale * TEXC * base
        "  } else {\n" +
        "    o = vec4(t.rgb * vC * (0.55 + 0.6*d), 1.0);\n" +
        "  }\n" +
        "}\n";

    // Fullscreen-triangle vertex shader for the post passes: no VBO -- positions/UVs come from gl_VertexID.
    const string PostVert =
        "#version 300 es\n" +
        "out vec2 vUv;\n" +
        "void main(){ vec2 p = vec2(float((gl_VertexID<<1)&2), float(gl_VertexID&2));\n" +
        "  vUv = p; gl_Position = vec4(p*2.0-1.0, 0.0, 1.0); }\n";
    // Bright extract: keep only the excess over a threshold -- a NEUTRAL bloom that glows whatever colour
    // is already there (no white-tinting: the blade's own colour has no blue, and faking it would be
    // reproducing the trophy viewer's white BACKGROUND, an environment effect, not the model).
    const string BrightFrag =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv; out vec4 o;\n" +
        "uniform sampler2D uTex; uniform float uThreshold;\n" +
        "void main(){ vec3 c = texture(uTex, vUv).rgb; float b = max(max(c.r,c.g),c.b);\n" +
        "  o = vec4(c * max(0.0, b - uThreshold) / max(b, 1e-4), 1.0); }\n";
    // Separable 9-tap Gaussian; uDir is a one-texel step in the blur direction.
    const string BlurFrag =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv; out vec4 o;\n" +
        "uniform sampler2D uTex; uniform vec2 uDir;\n" +
        "void main(){ float w0=0.227027,w1=0.1945946,w2=0.1216216,w3=0.054054,w4=0.016216;\n" +
        "  vec3 s = texture(uTex,vUv).rgb*w0;\n" +
        "  s += (texture(uTex,vUv+uDir).rgb+texture(uTex,vUv-uDir).rgb)*w1;\n" +
        "  s += (texture(uTex,vUv+uDir*2.0).rgb+texture(uTex,vUv-uDir*2.0).rgb)*w2;\n" +
        "  s += (texture(uTex,vUv+uDir*3.0).rgb+texture(uTex,vUv-uDir*3.0).rgb)*w3;\n" +
        "  s += (texture(uTex,vUv+uDir*4.0).rgb+texture(uTex,vUv-uDir*4.0).rgb)*w4;\n" +
        "  o = vec4(s,1.0); }\n";
    // Composite: original scene + the blurred glow.
    const string CompFrag =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv; out vec4 o;\n" +
        "uniform sampler2D uScene; uniform sampler2D uBloom; uniform float uIntensity;\n" +
        "void main(){ o = vec4(texture(uScene,vUv).rgb + texture(uBloom,vUv).rgb*uIntensity, 1.0); }\n";

    // ---- Gameplay HUD (Phase: presentation-layer v1, docs/HUD_APPROACH.md) ------------------------
    // Minimal ortho 2D pass: one unit quad, positioned/sized per draw via uniforms (screen-fraction
    // space, y-down to match screen convention), textured from a bank of textures baked ONCE at init
    // (Canvas/Path-drawn shapes + Canvas-drawn text -- same "pre-baked bitmap, not a live rasterizer"
    // technique the real DS game's own HUD renderer uses -- see src/MphRead/Renderer.cs DrawHudObject),
    // tinted per-hunter via a color uniform so one shape texture serves every hunter that uses it.
    const string HudVertSrc =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aPos;\n" +   // unit quad corner, 0..1
        "uniform vec2 uPos; uniform vec2 uSize; uniform float uRot; uniform vec2 uFlip;\n" +
        "uniform vec2 uUvScale; uniform vec2 uUvOffset;\n" +
        "out vec2 vUv;\n" +
        "void main(){\n" +
        "  vUv = aPos * uUvScale + uUvOffset;\n" +
        "  vec2 c = (aPos - 0.5) * uFlip;\n" +
        "  float s = sin(uRot), co = cos(uRot);\n" +
        "  vec2 r = vec2(c.x*co - c.y*s, c.x*s + c.y*co);\n" +
        "  vec2 frac = uPos + uSize*0.5 + r*uSize;\n" +
        "  gl_Position = vec4(frac.x*2.0-1.0, 1.0-frac.y*2.0, 0.0, 1.0);\n" +
        "}\n";
    const string HudFragSrc =
        "#version 300 es\n" +
        "precision mediump float;\n" +
        "in vec2 vUv; out vec4 o;\n" +
        "uniform sampler2D uTex; uniform vec4 uColor;\n" +
        "void main(){ o = texture(uTex, vUv) * uColor; }\n";

    // Corner-bracket / reticle shape ids, indexing the baked-shape texture arrays (see BakeHudAssets).
    const int C_CANOPY = 0, C_ROUND = 1, C_BLADE = 2, C_ANGULAR = 3, C_ARROW = 4, C_ROCK = 5, C_LIGHTS = 6;
    const int R_CIRCLE = 0, R_DIAMONDR = 1, R_CROSS = 2, R_SQUARE = 3, R_ARROW = 4, R_DIAMOND = 5, R_HEX = 6;

    struct HunterHudStyle
    {
        public string Name;
        public float[] Accent;    // primary frame/bar color, RGB 0..1
        public float[] Accent2;   // secondary/readout color, RGB 0..1 (numerals, reticle)
        public int BarMode;       // 0 = top horizontal (Samus only); 1 = left vertical; 2 = both sides vertical
        public int Corner, Reticle;
        public string Weapon, AltForm;
        public int Energy, AmmoCur, AmmoMax; // placeholder demo values -- no real sim data exists yet
    }
    static float[] Rgb(int r, int g, int b) => new[] { r / 255f, g / 255f, b / 255f };

    // Per-hunter table. Bar position/orientation corrected against the real reference screenshots
    // (the owner's MPH Hunter HUDs *.webp set, top-screen only) -- the earlier concept mockup
    // kept every hunter's bar top-center, but the real game varies POSITION, not just segment style:
    // Samus alone gets the horizontal top bar; Trace/Sylux get twin bars both sides; the rest mount a
    // single bar on the left. Colors/corner/reticle shapes are from the concept mockup ("mph-recomp --
    // HUD lab" artifact), which matched the real screenshots well for everything except bar position.
    static readonly HunterHudStyle[] HunterStyles =
    {
        new() { Name = "Samus",  Accent = Rgb(0x46,0xe0,0x6a), Accent2 = Rgb(0x46,0xe0,0x6a), BarMode = 0, Corner = C_CANOPY,  Reticle = R_CIRCLE,   Weapon = "Power Beam",   AltForm = "Morph Ball", Energy = 99, AmmoCur = 87,  AmmoMax = 110 },
        new() { Name = "Kanden", Accent = Rgb(0xf5,0xd0,0x22), Accent2 = Rgb(0xff,0x3d,0x94), BarMode = 1, Corner = C_ROUND,   Reticle = R_DIAMONDR, Weapon = "Volt Driver",  AltForm = "Stinglarva", Energy = 63, AmmoCur = 1,   AmmoMax = 60  },
        new() { Name = "Trace",  Accent = Rgb(0xff,0x3b,0x3b), Accent2 = Rgb(0xff,0x5a,0x4a), BarMode = 2, Corner = C_BLADE,   Reticle = R_CROSS,    Weapon = "Imperialist",  AltForm = "Triskelion", Energy = 48, AmmoCur = 199, AmmoMax = 199 },
        new() { Name = "Sylux",  Accent = Rgb(0x5a,0x8c,0xff), Accent2 = Rgb(0x46,0xe0,0x6a), BarMode = 2, Corner = C_ANGULAR, Reticle = R_SQUARE,   Weapon = "Shock Coil",   AltForm = "Lockjaw",    Energy = 71, AmmoCur = 120, AmmoMax = 160 },
        new() { Name = "Noxus",  Accent = Rgb(0x8f,0xe3,0xff), Accent2 = Rgb(0xbf,0xef,0xff), BarMode = 1, Corner = C_ARROW,   Reticle = R_ARROW,    Weapon = "Judicator",    AltForm = "Vhoscythe",  Energy = 55, AmmoCur = 30,  AmmoMax = 40  },
        new() { Name = "Spire",  Accent = Rgb(0xff,0x8a,0x3d), Accent2 = Rgb(0xff,0xa6,0x61), BarMode = 1, Corner = C_ROCK,    Reticle = R_DIAMOND,  Weapon = "Magmaul",      AltForm = "Dialanche",  Energy = 95, AmmoCur = 24,  AmmoMax = 30  },
        new() { Name = "Weavel", Accent = Rgb(0xf2,0xb2,0x1e), Accent2 = Rgb(0x6c,0xb8,0xff), BarMode = 1, Corner = C_LIGHTS,  Reticle = R_HEX,      Weapon = "Battlehammer", AltForm = "Halfturret", Energy = 66, AmmoCur = 195, AmmoMax = 240 },
    };

    public void OnSurfaceCreated(IGL10? gl, Javax.Microedition.Khronos.Egl.EGLConfig? config)
    {
        GLES30.GlClearColor(0.03f, 0.04f, 0.07f, 1f);
        GLES30.GlEnable(GLES30.GlDepthTest);
        _program = LinkProgram(VertSrc, FragSrc);
        // Post-process programs (bloom). Built once; the FBOs come in OnSurfaceChanged (need the size).
        _progBright = LinkProgram(PostVert, BrightFrag);
        _uBrightTex = GLES30.GlGetUniformLocation(_progBright, "uTex");
        _uBrightThresh = GLES30.GlGetUniformLocation(_progBright, "uThreshold");
        _progBlur = LinkProgram(PostVert, BlurFrag);
        _uBlurTex = GLES30.GlGetUniformLocation(_progBlur, "uTex");
        _uBlurDir = GLES30.GlGetUniformLocation(_progBlur, "uDir");
        _progComp = LinkProgram(PostVert, CompFrag);
        _uCompScene = GLES30.GlGetUniformLocation(_progComp, "uScene");
        _uCompBloom = GLES30.GlGetUniformLocation(_progComp, "uBloom");
        _uCompIntensity = GLES30.GlGetUniformLocation(_progComp, "uIntensity");
        int[] vao = new int[1]; GLES30.GlGenVertexArrays(1, vao, 0); _fsQuad = vao[0];
        _uMvp = GLES30.GlGetUniformLocation(_program, "uMvp");
        _uTex = GLES30.GlGetUniformLocation(_program, "uTex");
        _uViewRot = GLES30.GlGetUniformLocation(_program, "uViewRot");
        _uTexgen = GLES30.GlGetUniformLocation(_program, "uTexgen");
        _uMetallic = GLES30.GlGetUniformLocation(_program, "uMetallic");
        _uUnlit = GLES30.GlGetUniformLocation(_program, "uUnlit");
        _uColB = GLES30.GlGetUniformLocation(_program, "uColB");
        _uLerp = GLES30.GlGetUniformLocation(_program, "uLerp");
        _uExposure = GLES30.GlGetUniformLocation(_program, "uExposure");
        _uSwap = GLES30.GlGetUniformLocation(_program, "uSwap");
        _uDebug = GLES30.GlGetUniformLocation(_program, "uDebug");
        _uMatId = GLES30.GlGetUniformLocation(_program, "uMatId");
        _uLerpScale = GLES30.GlGetUniformLocation(_program, "uLerpScale");
        _uReflScale = GLES30.GlGetUniformLocation(_program, "uReflScale");
        _uTextured = GLES30.GlGetUniformLocation(_program, "uTextured");
        _uMultiTex = GLES30.GlGetUniformLocation(_program, "uMultiTex");
        _uIncand = GLES30.GlGetUniformLocation(_program, "uIncand");
        _uSpec = GLES30.GlGetUniformLocation(_program, "uSpec");
        _uReflTex = GLES30.GlGetUniformLocation(_program, "uReflTex");
        _uUnlitScale = GLES30.GlGetUniformLocation(_program, "uUnlitScale");
        _uAmbient = GLES30.GlGetUniformLocation(_program, "uAmbient");
        _uCutout = GLES30.GlGetUniformLocation(_program, "uCutout");
        _uOpacity = GLES30.GlGetUniformLocation(_program, "uOpacity");
        _uTranslucent = GLES30.GlGetUniformLocation(_program, "uTranslucent");
        _uJigOn = GLES30.GlGetUniformLocation(_program, "uJigOn");
        _uJigD = GLES30.GlGetUniformLocation(_program, "uJigD");
        _uJigTop = GLES30.GlGetUniformLocation(_program, "uJigTop");
        _uJigBot = GLES30.GlGetUniformLocation(_program, "uJigBot");
        _uSkin = GLES30.GlGetUniformLocation(_program, "uSkin");
        _uModel = GLES30.GlGetUniformLocation(_program, "uModel");
        _uBones = GLES30.GlGetUniformLocation(_program, "uBones");
        Log.Info("MPHRender", $"skinning uniforms: uSkin={_uSkin} uModel={_uModel} uBones={_uBones}");
        if (_gxEval)
        {
            // the SAME GX program the PC comparison tool runs (MphRead.Tools -hdlook), proven against BrawlCrate's own
            // renders of all 13 trophies: every material input from the .brres, only the scene light from us
            _gxProgram = LinkProgram(MphRecomp.Render.GxShader.Vert, MphRecomp.Render.GxShader.Frag);
            _gxMvp = GLES30.GlGetUniformLocation(_gxProgram, "uMvp");
            _gxViewRot = GLES30.GlGetUniformLocation(_gxProgram, "uViewRot");
            for (int t = 0; t < 8; t++) _gxTexUnits[t] = GLES30.GlGetUniformLocation(_gxProgram, "uTex" + t);
            foreach (var un in new[] { "uNumStages", "uCabcd", "uCmod", "uAabcd", "uAmod", "uDst", "uKsel", "uRasChan", "uSwap", "uReg", "uKonst",
                "uColCtrl", "uAlpCtrl", "uAttn", "uChanMat", "uChanAmb", "uSceneAmb", "uNumLights", "uLightDir", "uLightCol", "uLightSpecCol", "uLightSpecK",
                "uTgMode", "uTexMtx", "uAlphaTest", "uAlphaRef", "uBlendAlpha", "uExposure", "uRawEnvNormal", "uNmap", "uNmapTc" })
                _gxU[un] = GLES30.GlGetUniformLocation(_gxProgram, un);
            _gxSkin = GLES30.GlGetUniformLocation(_gxProgram, "uSkin");
            _gxModel = GLES30.GlGetUniformLocation(_gxProgram, "uModel");
            _gxBones = GLES30.GlGetUniformLocation(_gxProgram, "uBones");
            Log.Info("MPHRender", $"gxeval: interpreter program linked ({_gxProgram})");
        }

        // Gameplay HUD: built unconditionally (not gated behind _gxEval) -- it's shown only in normal
        // room mode (see OnDrawFrame), independent of the HD-trophy shader work entirely.
        _hudProgram = LinkProgram(HudVertSrc, HudFragSrc);
        _hudUPos = GLES30.GlGetUniformLocation(_hudProgram, "uPos");
        _hudUSize = GLES30.GlGetUniformLocation(_hudProgram, "uSize");
        _hudURot = GLES30.GlGetUniformLocation(_hudProgram, "uRot");
        _hudUFlip = GLES30.GlGetUniformLocation(_hudProgram, "uFlip");
        _hudUColor = GLES30.GlGetUniformLocation(_hudProgram, "uColor");
        _hudUTex = GLES30.GlGetUniformLocation(_hudProgram, "uTex");
        _hudUUvScale = GLES30.GlGetUniformLocation(_hudProgram, "uUvScale");
        _hudUUvOffset = GLES30.GlGetUniformLocation(_hudProgram, "uUvOffset");
        SetupHudQuad();
        BakeHudAssets();
        Log.Info("MPHRender", $"gameplay HUD: baked assets, program linked ({_hudProgram})");

        List<GeometryBaker.Batch> baked;
        try
        {
            System.IO.Directory.SetCurrentDirectory(_filesDir);
            Paths.UpdatePaths(); Paths.ChooseMphPath();
            if (_hudDump) DumpAllHunterLayers();
            if (_hd != null && _hdRoom != null)
            {
                // IN-GAME PREVIEW: drop the HD hunter into a real arena, lit by the room the same way
                // the game would. Bake the HD model (model space), then bake the room and scale/place
                // the hunter on the room floor at ~hunter height. Camera frames the hunter.
                var hdBatches = LoadHdModel();               // sets _center/_radius to the HD model bounds
                float hcx = _center[0], hcy = _center[1], hcz = _center[2], hr = _radius;
                ModelInstance rinst = Read.GetRoomModelInstance(_hdRoom);
                rinst.Model.FilterNodes(RoomLayerMask(Metadata.RoomMetadata[_hdRoom]));
                var roomBatches = GeometryBaker.Bake(rinst.Model, _center, out _radius, useAnimation: false, useMatrixStack: false, skipDisabledNodes: true);
                _tri = LoadCollisionTris(_hdRoom, _center, out float croom);   // room centre + play radius
                if (croom > 1f) _radius = croom;
                float targetH = 2.2f;                        // a hunter is ~human height, NOT room-radius scaled
                float s = targetH / Math.Max(1f, hr * 1.46f);// hr is a half-diagonal; *1.46 -> model height
                // Stand the hunter on an actual PlayerSpawn (open floor) -- the room centre is inside the
                // central structure. gxCompare wants the MOST CENTRAL spawn specifically (not just the
                // first one in the entity list, which can land anywhere around the ring). Fall back to the
                // collision centre if the room has no spawns.
                float placeX, placeZ, floorY, spawnYaw;
                bool foundSpawn = _gxCompare
                    ? FindCentralPlayerSpawn(_hdRoom, _center, out OpenTK.Mathematics.Vector3 spn, out spawnYaw)
                    : FindPlayerSpawn(_hdRoom, out spn, out spawnYaw);
                if (foundSpawn)
                { placeX = spn.X; placeZ = spn.Z; floorY = spn.Y; }
                else
                { placeX = _center[0]; placeZ = _center[2]; floorY = FloorAt(placeX, placeZ); if (floorY > 1e8f) floorY = _center[1] - _radius * 0.35f; spawnYaw = 0f; }
                if (_gxCompare)
                { _hdHasSpawn = foundSpawn; _hdSpawnX = placeX; _hdSpawnZ = placeZ; _hdSpawnY = floorY + _radius * 0.06f; _hdSpawnYaw = spawnYaw; }
                float placeY = floorY + targetH * 0.5f;      // centre at half-height -> feet on the floor
                if (_gxCompare)
                {
                    // A/B in ONE arena, identical lighting: LEFT copy = old hand-tuned shader (Gx cleared
                    // -> DrawOne), RIGHT copy = GX-TEV interpreter (Gx kept -> DrawGx). Walk around both.
                    float gap = Math.Max(2.4f, targetH * 1.15f);   // clear channel between the two copies
                    baked = roomBatches;
                    foreach (var b in hdBatches)
                    {
                        var lb = CloneBatch(b, keepGx: false);   // old shader
                        var rb = CloneBatch(b, keepGx: true);    // interpreter
                        for (int i = 0; i + 11 <= b.Verts.Count; i += 11)
                        {
                            float x = (b.Verts[i] - hcx) * s, y = (b.Verts[i + 1] - hcy) * s + placeY, z = (b.Verts[i + 2] - hcz) * s + placeZ;
                            lb.Verts[i] = x + placeX - gap; lb.Verts[i + 1] = y; lb.Verts[i + 2] = z;
                            rb.Verts[i] = x + placeX + gap; rb.Verts[i + 1] = y; rb.Verts[i + 2] = z;
                        }
                        baked.Add(lb); baked.Add(rb);
                    }
                    _center[0] = placeX; _center[1] = placeY; _center[2] = placeZ;
                    _hdFrameR = targetH + gap;         // camera pull-back distance
                    _cmpHalfW = gap + targetH * 0.7f;  // ortho half-width: frame both copies with margin
                    Log.Info("MPHRender", $"HD-COMPARE '{_hd}': OLD (-{gap:0.0}x) vs INTERPRETER (+{gap:0.0}x) at ({placeX:0.0},{placeY:0.0},{placeZ:0.0})");
                }
                else
                {
                    double bx = 0, by = 0, bz = 0; long bn = 0;   // accumulate the blade (additive) centroid
                    foreach (var b in hdBatches)
                        for (int i = 0; i + 11 <= b.Verts.Count; i += 11)
                        {
                            b.Verts[i] = (b.Verts[i] - hcx) * s + placeX;
                            b.Verts[i + 1] = (b.Verts[i + 1] - hcy) * s + placeY;
                            b.Verts[i + 2] = (b.Verts[i + 2] - hcz) * s + placeZ;
                            if (b.Additive) { bx += b.Verts[i]; by += b.Verts[i + 1]; bz += b.Verts[i + 2]; bn++; }
                        }
                    if (bn > 0) _hdBladeC = new[] { (float)(bx / bn), (float)(by / bn), (float)(bz / bn) };
                    baked = roomBatches; baked.AddRange(hdBatches);
                    // Camera looks at the hunter; keep _radius = the room extent so the far plane still
                    // covers the whole arena. _hdFrameR (hunter height) drives the framing distance.
                    _center[0] = placeX; _center[1] = placeY; _center[2] = placeZ;
                    _hdFrameR = targetH;
                }
                Log.Info("MPHRender", $"HD-in-room '{_hdRoom}': hunter scale={s:0.000} at ({placeX:0.0},{placeY:0.0},{placeZ:0.0}) floorY={floorY:0.0}");
            }
            else if (_hd != null)
            {
                baked = LoadHdModel(); // user-supplied HD trophy (.dae), baked with its real part colors
            }
            else
            {
                ModelInstance inst = _model != null ? Read.GetModelInstance(_model) : Read.GetRoomModelInstance(_room);
                // Multiplayer room models PACK MULTIPLE LAYERS in one file: the single-player campaign
                // walls/door-blockers/tube(tunnel)-blockers live on _s0x-tagged nodes, the open MP arena
                // on _mpu/_ml0 nodes. Without filtering we'd draw the campaign walls over the MP arena
                // (sectioned tunnel, closed corner doorways). Apply the game's layer mask (FilterNodes)
                // so only the current layer's nodes stay Enabled; the baker then skips disabled nodes.
                if (_model == null) inst.Model.FilterNodes(RoomLayerMask(Metadata.RoomMetadata[_room]));
                // Hunters carry a full skeleton whose BIND pose is a collapsed rest state; pose them
                // with Idle before baking (see ApplyIdle). Non-animated models bake in bind pose.
                bool animated = _model != null && ApplyIdle(inst);
                // rooms bake per-node (their matrix stack is intentionally unused); a single model
                // (viewer) is a non-room model, so drive it through the DS matrix stack.
                baked = GeometryBaker.Bake(inst.Model, _center, out _radius, useAnimation: animated,
                    useMatrixStack: _model != null, skipDisabledNodes: _model == null);
                Log.Info("MPHRender", $"model={_model} animated={animated}");
            }
        }
        catch (Exception ex) { Log.Error("MPHRender", "load/bake failed: " + ex); baked = new(); }

        // place the room's entities (item pickups + visible props: doors/jump-pads/platforms/
        // force-fields) at their authored positions, baked straight into the scene VBO.
        // Render-only: NOT added to the collision triangles (_tri). Shared model cache.
        if (_model == null && _hd == null)
        {
            var entCache = new Dictionary<string, List<GeometryBaker.Batch>>();
            try { baked.AddRange(LoadPickupBatches(_room, entCache)); }
            catch (Exception ex) { Log.Error("MPHRender", "pickups failed: " + ex); }
            try { baked.AddRange(LoadPropBatches(_room, entCache)); }
            catch (Exception ex) { Log.Error("MPHRender", "props failed: " + ex); }
            if (_staticHunters)
            {
                try { baked.AddRange(LoadHunterBatches(_room, entCache)); }
                catch (Exception ex) { Log.Error("MPHRender", "hunters failed: " + ex); }
            }
            else
            {
                try { CollectSpawnHunters(_room); }   // animated + GPU-skinned; built after collision loads
                catch (Exception ex) { Log.Error("MPHRender", "spawn hunters failed: " + ex); }
            }
        }

        // concat all batch verts into one VBO; upload a texture per batch
        var all = new List<float>();
        foreach (var b in baked)
        {
            int start = all.Count / 11;
            if (_gxEval && b.Gx != null && b.GxAux != null)
                for (int i = 0, v = 0; i + 11 <= b.Verts.Count; i += 11, v += 5)
                {
                    for (int k = 0; k < 6; k++) all.Add(b.Verts[i + k]);
                    for (int k = 0; k < 5; k++) all.Add(b.GxAux[v + k]);
                }
            else all.AddRange(b.Verts);
            _batches.Add(ToDrawBatch(b, start, b.Verts.Count / 11));
        }
        int totalVerts = all.Count / 11;
        Log.Info("MPHRender", $"batches={_batches.Count} verts={totalVerts} tris={totalVerts / 3} " +
            $"center=({_center[0]:0.0},{_center[1]:0.0},{_center[2]:0.0}) r={_radius:0.0}");
        if (totalVerts == 0) return;

        _tri = new float[totalVerts * 3]; // keep positions CPU-side for floor raycasts
        for (int i = 0; i < totalVerts; i++)
        {
            _tri[i * 3] = all[i * 11]; _tri[i * 3 + 1] = all[i * 11 + 1]; _tri[i * 3 + 2] = all[i * 11 + 2];
        }

        var vaos = new int[1]; GLES30.GlGenVertexArrays(1, vaos, 0); _vao = vaos[0];
        var vbos = new int[1]; GLES30.GlGenBuffers(1, vbos, 0);
        GLES30.GlBindVertexArray(_vao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, vbos[0]);
        float[] arr = all.ToArray();
        var bb = Java.Nio.ByteBuffer.AllocateDirect(arr.Length * sizeof(float));
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        var fb = bb.AsFloatBuffer()!; fb.Put(arr); fb.Position(0);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, arr.Length * sizeof(float), fb, GLES30.GlStaticDraw);
        GLES30.GlVertexAttribPointer(0, 3, GLES30.GlFloat, false, Stride, 0);
        GLES30.GlEnableVertexAttribArray(0);
        GLES30.GlVertexAttribPointer(1, 3, GLES30.GlFloat, false, Stride, 3 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(1);
        GLES30.GlVertexAttribPointer(2, 2, GLES30.GlFloat, false, Stride, 6 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(2);
        GLES30.GlVertexAttribPointer(3, 3, GLES30.GlFloat, false, Stride, 8 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(3);

        // gxcompare wants a real walk/collision session (see below); it needs the CLEAN collision
        // mesh, same as a plain room does, not the render-derived one reset just below.
        bool hdWalkable = _gxCompare && _hdRoom != null && _hdHasSpawn;

        // The rendered geometry includes the skybox shell (huge, e.g. +/-600), which is
        // fine to SEE but must not drive camera scale/spawn/collision. For rooms, the true
        // playable space is the collision mesh (skyboxes have no collision), and it lives in
        // the SAME world units as the baked geometry. So: keep the render extent for the far
        // plane, but take centre/radius and the CPU raycast triangles from collision.
        _renderRadius = _radius;
        if ((_model == null && _hd == null) || hdWalkable)
        {
            try
            {
                // LoadCollisionTris WRITES the room's true collision-mesh center into its centerOut
                // param -- fine for plain rooms, but for hdWalkable that would clobber _center (which
                // holds the hunter placement point) with the room's raw center, which can sit inside an
                // unwalkable central structure (e.g. Sanctorus's column) and break the OnDrawFrame
                // fell-out-of-world recenter. Give it a scratch array instead when hdWalkable.
                float[] ctri = LoadCollisionTris(hdWalkable ? _hdRoom! : _room, hdWalkable ? new float[3] : _center, out float cradius);
                if (ctri.Length >= 9) { _tri = ctri; if (!hdWalkable) _radius = cradius; } // hd-in-room already scaled _radius to croom earlier; don't reclobber it here
                else Log.Warn("MPHRender", "collision empty; falling back to render bounds");
            }
            catch (Exception ex) { Log.Error("MPHRender", "collision load failed: " + ex); }
            Log.Info("MPHRender", $"playRadius={_radius:0.0} renderRadius={_renderRadius:0.0} colTris={_tri.Length / 9}");
        }

        // live hunters need the collision triangles (floor snap / the viewer's walking), so after that
        if (_model == null && _hd == null && !_staticHunters)
        {
            try { SetupAnimatedHunters(); }
            catch (Exception ex) { Log.Error("MPHRender", "animated hunters failed: " + ex); _actors.Clear(); _driven = null; }
        }

        // start the camera just inside the room, at eye height, looking toward its centre
        float eye = _radius * 0.06f;
        if (_model == null && _hd == null)
        {
            // spawn at an actual PlayerSpawn entity (the game's guaranteed-valid spots) so we
            // never start outside the map -- vertical arenas break the "lowest floor under the
            // centre" heuristic. Fall back to that heuristic only if there are no spawn points.
            if (FindPlayerSpawn(_room, out OpenTK.Mathematics.Vector3 sp, out float syaw))
            {
                _pos[0] = sp.X; _pos[1] = sp.Y + eye; _pos[2] = sp.Z;
                _yaw = syaw; _pitch = 0f;
            }
            else
            {
                float floorY = FloorAt(_center[0], _center[2]);
                if (floorY <= -1e8f) { floorY = 1e9f; for (int i = 1; i < _tri.Length; i += 3) if (_tri[i] < floorY) floorY = _tri[i]; }
                _pos[0] = _center[0]; _pos[1] = floorY + eye; _pos[2] = _center[2];
                _yaw = 0f; _pitch = 0f;
            }
        }
        else if (_hd != null && _hdRoom != null)
        {
            // HD-in-room: tight close-up centred on the BLADE (additive-batch centroid) so we can judge
            // the energy blend up close. Falls back to a body-centred view if there's no blade.
            if (_hdBladeC != null)
            { _pos[0] = _hdBladeC[0]; _pos[1] = _hdBladeC[1]; _pos[2] = _hdBladeC[2] + _hdFrameR * 0.42f; }
            else   // bladeless: full-body view (works for any hunter/suit body shape)
            { _pos[0] = _center[0]; _pos[1] = _center[1]; _pos[2] = _center[2] + _hdFrameR * 1.6f; }
            _yaw = 0f; _pitch = 0f;
        }
        else
        {
            // single model: close head-and-shoulders framing (matching the BrawlCrate preview), shifted
            // slightly right. The free-fly stick moves freely.
            _pos[0] = _center[0] + _radius * 0.15f; _pos[1] = _center[1] + _radius * 0.36f; _pos[2] = _center[2] + _radius * 0.55f;
            _yaw = 0f; _pitch = 0f;
        }

        // Bring the room to life: hand the sim our collision + spawn, and scale the player's feel to
        // the room (same numbers the old inline walk used, so play mode feels identical -- but now
        // it's the real GameSession running: PlayerController + fixed-tick world + mod hooks).
        // Also true for HD-in-room gxCompare: Walk defaults true there (see the Walk ctor assignment)
        // so it needs the same real session, not just stick-driven free-fly with no collision/gravity --
        // that was the actual gxCompare "walk does nothing" bug (Walk was true but no session existed).
        if (((_model == null && _hd == null) || hdWalkable) && _animHunter == null) // the anim viewer drives its own hunter + chase camera instead
        {
            try
            {
                string roomName = hdWalkable ? _hdRoom! : _room;
                System.Numerics.Vector3 spawnPos = hdWalkable
                    ? new System.Numerics.Vector3(_hdSpawnX, _hdSpawnY, _hdSpawnZ)
                    : new System.Numerics.Vector3(_pos[0], _pos[1], _pos[2]);
                float spawnYaw = hdWalkable ? _hdSpawnYaw : _yaw;
                _session = new GameSession();
                PlayerController p = _session.EnterRoom(roomName, new TriCollision(this), spawnPos, spawnYaw);
                p.MoveSpeed = _radius * 0.5f;
                p.Gravity = _radius * 2.0f;
                // eye at hunter eye-level: the biped models stand ~1.6 units tall (feet at 0), so a
                // fixed ~1.5 puts the camera just below the head -- NOT radius-scaled (that made the
                // player ~2 hunters tall). Player is Samus-scale; world units are consistent per room.
                p.EyeHeight = 1.5f;
                p.LookSpeed = 2.2f;
                _session.Start();
                Log.Info("MPHRender", $"sim live: room='{roomName}' spawn=({spawnPos.X:0.0},{spawnPos.Y:0.0},{spawnPos.Z:0.0}) yaw={spawnYaw:0.00} hd={hdWalkable}");
            }
            catch (Exception ex) { Log.Error("MPHRender", "sim init failed (falling back to inline walk): " + ex); _session = null; }
        }
        _last = SystemClock.UptimeMillis(); _fpsSince = _last;
    }

    public void OnSurfaceChanged(IGL10? gl, int width, int height)
    {
        GLES30.GlViewport(0, 0, width, height);
        _vw = width; _vh = height;
        SetupBloomFbos(width, height);   // bloom in every mode -- energy glows in-game too, not just the viewer
        float aspect = height == 0 ? 1f : (float)width / height;
        // near tied to the playable scale; far reaches the skybox shell so it stays visible.
        // HD-in-room: the camera hugs a ~2-unit hunter for close-ups, so tie near to his scale
        // (not the 47-unit room) or his face/chest would be clipped by the near plane.
        float near = (_hd != null && _hdRoom != null)
            ? Math.Max(0.03f, _hdFrameR * 0.03f)
            : IsAnimView ? 0.02f   // the viewer zooms / free-flies right up to a ~2-unit hunter
            : Math.Max(0.1f, _radius * 0.01f);
        float far = _renderRadius * 2.5f + 100f;
        Matrix.PerspectiveM(_proj, 0, 62f, aspect, near, far);
    }

    // GL enums this binding doesn't surface as un-shadowed constants.
    const int GL_FRAMEBUFFER = 0x8D40, GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_DEPTH_ATTACHMENT = 0x8D00,
              GL_RENDERBUFFER = 0x8D41, GL_DEPTH_COMPONENT16 = 0x81A5, GL_DEPTH_COMPONENT24 = 0x81A6, GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

    // Build the offscreen targets for the bloom passes: a full-res scene buffer (colour+depth) and two
    // half-res buffers to ping-pong the extract/blur through. Recreated whenever the surface resizes.
    void SetupBloomFbos(int w, int h)
    {
        _bloomOk = false;
        DeletePost();
        if (w <= 0 || h <= 0) return;
        int bw = Math.Max(1, w / 2), bh = Math.Max(1, h / 2);
        _texScene = MakeColorTex(w, h);
        _texBloomA = MakeColorTex(bw, bh);
        _texBloomB = MakeColorTex(bw, bh);
        int[] rb = new int[1]; GLES30.GlGenRenderbuffers(1, rb, 0); _rbDepth = rb[0];
        GLES30.GlBindRenderbuffer(GL_RENDERBUFFER, _rbDepth);
        GLES30.GlRenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, w, h);   // 24-bit: see the EGL config
        _fbScene = MakeFbo(_texScene, _rbDepth);
        _fbBloomA = MakeFbo(_texBloomA, 0);
        _fbBloomB = MakeFbo(_texBloomB, 0);
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        _bloomOk = _fbScene != 0 && _fbBloomA != 0 && _fbBloomB != 0;
        Log.Info("MPHRender", $"bloom fbos {(_bloomOk ? "ready" : "FAILED")} scene={w}x{h} bloom={bw}x{bh}");
    }

    int MakeColorTex(int w, int h)
    {
        int[] t = new int[1]; GLES30.GlGenTextures(1, t, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, t[0]);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (Java.Nio.Buffer?)null);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        return t[0];
    }

    int MakeFbo(int colorTex, int depthRb)
    {
        int[] f = new int[1]; GLES30.GlGenFramebuffers(1, f, 0);
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, f[0]);
        GLES30.GlFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GLES30.GlTexture2d, colorTex, 0);
        if (depthRb != 0) GLES30.GlFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, depthRb);
        int st = GLES30.GlCheckFramebufferStatus(GL_FRAMEBUFFER);
        if (st != GL_FRAMEBUFFER_COMPLETE) { Log.Warn("MPHRender", $"FBO incomplete 0x{st:X}"); return 0; }
        return f[0];
    }

    void DeletePost()
    {
        void DT(ref int t) { if (t != 0) { GLES30.GlDeleteTextures(1, new[] { t }, 0); t = 0; } }
        void DF(ref int f) { if (f != 0) { GLES30.GlDeleteFramebuffers(1, new[] { f }, 0); f = 0; } }
        DF(ref _fbScene); DF(ref _fbBloomA); DF(ref _fbBloomB);
        DT(ref _texScene); DT(ref _texBloomA); DT(ref _texBloomB);
        if (_rbDepth != 0) { GLES30.GlDeleteRenderbuffers(1, new[] { _rbDepth }, 0); _rbDepth = 0; }
    }

    // The bloom post-process, run after the scene is drawn into _fbScene: extract the bright pixels at
    // half res, separable-blur them, then composite scene + glow to the screen. This is what gives the
    // energy blade its shimmer and a yellow-white hot core -- reproducing the Brawl trophy renderer.
    void DrawBloom()
    {
        int bw = Math.Max(1, _vw / 2), bh = Math.Max(1, _vh / 2);
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlBindVertexArray(_fsQuad);
        // 1) bright extract: scene -> bloomA
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _fbBloomA);
        GLES30.GlViewport(0, 0, bw, bh);
        GLES30.GlUseProgram(_progBright);
        GLES30.GlActiveTexture(GLES30.GlTexture0); GLES30.GlBindTexture(GLES30.GlTexture2d, _texScene);
        GLES30.GlUniform1i(_uBrightTex, 0); GLES30.GlUniform1f(_uBrightThresh, _bloomThreshold);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        // 2) blur horizontally: bloomA -> bloomB
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _fbBloomB);
        GLES30.GlUseProgram(_progBlur);
        GLES30.GlActiveTexture(GLES30.GlTexture0); GLES30.GlBindTexture(GLES30.GlTexture2d, _texBloomA);
        GLES30.GlUniform1i(_uBlurTex, 0); GLES30.GlUniform2f(_uBlurDir, 1.4f / bw, 0f);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        // 3) blur vertically: bloomB -> bloomA
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _fbBloomA);
        GLES30.GlBindTexture(GLES30.GlTexture2d, _texBloomB);
        GLES30.GlUniform2f(_uBlurDir, 0f, 1.4f / bh);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        // 4) composite scene + glow -> the real screen
        GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0);
        GLES30.GlViewport(0, 0, _vw, _vh);
        GLES30.GlUseProgram(_progComp);
        GLES30.GlActiveTexture(GLES30.GlTexture0); GLES30.GlBindTexture(GLES30.GlTexture2d, _texScene); GLES30.GlUniform1i(_uCompScene, 0);
        GLES30.GlActiveTexture(GLES30.GlTexture1); GLES30.GlBindTexture(GLES30.GlTexture2d, _texBloomA); GLES30.GlUniform1i(_uCompBloom, 1);
        GLES30.GlUniform1f(_uCompIntensity, 1.3f);
        GLES30.GlDrawArrays(GLES30.GlTriangles, 0, 3);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }

    public void OnDrawFrame(IGL10? gl)
    {
        // Standalone HD viewer (no room) = a BrawlCrate-style light backdrop, so the additive blade is
        // composited over near-white just like the trophy view -- proves the paleness is the background,
        // not the model. Raise the bloom threshold above the backdrop so only the model glows, not the bg.
        bool lightBg = IsHdView && _hdRoom == null;
        if (lightBg) { GLES30.GlClearColor(0.82f, 0.82f, 0.84f, 1f); _bloomThreshold = 0.92f; }
        else { GLES30.GlClearColor(0.03f, 0.04f, 0.07f, 1f); _bloomThreshold = 0.55f; }
        // Render the scene into the offscreen buffer so we can bloom it afterwards. Bloom runs in EVERY
        // mode (incl. play), so emissive/additive geometry -- the blade, ports, beams -- glows in-game.
        // no bloom while an HD trophy is shown (the trophy viewer, a trophy in a room, a trophy as the live hunter)
        bool bloom = _bloomOk && !_noBloom && (_forceBloom || (_hd == null && _animHdId == null));
        if (bloom) { GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, _fbScene); GLES30.GlViewport(0, 0, _vw, _vh); }
        GLES30.GlClear(GLES30.GlColorBufferBit | GLES30.GlDepthBufferBit);
        if (_batches.Count == 0) { if (bloom) GLES30.GlBindFramebuffer(GL_FRAMEBUFFER, 0); return; }
        long now = SystemClock.UptimeMillis();
        float dt = Math.Min(0.05f, (now - _last) / 1000f); _last = now;

        // frame-rate probe (logged every 2 s in every mode) -- the cost of live hunters vs statues
        _fpsFrames++;
        if (now - _fpsSince >= 2000)
        {
            _fps = _fpsFrames * 1000f / (now - _fpsSince);
            Log.Info("MPHRender", $"frame stats: fps={_fps:0.0} liveHunters={_actors.Count} staticHunters={_staticHunters}");
            _fpsFrames = 0; _fpsSince = now;
        }
        if (_actors.Count > 0) UpdateAnimation(dt, now);

        float fx, fy, fz;
        if (System.Threading.Interlocked.Exchange(ref _pendingFree, 0) == 1 && _driven != null)
        {
            FreeCam = !FreeCam;
            if (FreeCam)
            {
                // start exactly where the chase camera is, looking the same way
                _freePos = new OpenTK.Mathematics.Vector3(_pos[0], _pos[1], _pos[2]);
                _freeYaw = MathF.Atan2(_lastLook.X, -_lastLook.Z);
                _freePitch = MathF.Asin(Math.Clamp(_lastLook.Y, -1f, 1f));
            }
            Log.Info("MPHRender", $"animview free camera -> {(FreeCam ? "on" : "off")}");
        }
        if (_driven != null && FreeCam)
        {
            // FREE CAMERA: the hunter keeps animating; the sticks fly the camera instead of driving him
            float s = Metadata.HunterScales[_driven.Hunter];
            _freeYaw += Rx * 2.0f * dt; _freePitch = Math.Clamp(_freePitch - Ry * 1.6f * dt, -1.5f, 1.5f);
            var look = new OpenTK.Mathematics.Vector3(MathF.Sin(_freeYaw) * MathF.Cos(_freePitch), MathF.Sin(_freePitch), -MathF.Cos(_freeYaw) * MathF.Cos(_freePitch));
            var rightV = new OpenTK.Mathematics.Vector3(MathF.Cos(_freeYaw), 0, MathF.Sin(_freeYaw));
            float speed = 2.2f * s * dt;
            _freePos += look * (-Ly * speed) + rightV * (Lx * speed) + new OpenTK.Mathematics.Vector3(0, ((OrbitRight ? 1 : 0) - (OrbitLeft ? 1 : 0)) * speed, 0);
            _pos[0] = _freePos.X; _pos[1] = _freePos.Y; _pos[2] = _freePos.Z;
            fx = look.X; fy = look.Y; fz = look.Z;
        }
        else if (_driven != null)
        {
            // ANIMATION VIEWER: third-person chase camera behind the driven hunter (L1/R1 orbit it).
            if (OrbitLeft) _orbit -= 1.8f * dt;
            if (OrbitRight) _orbit += 1.8f * dt;
            float a = Math.Clamp(_animAcc / TickSeconds, 0f, 1f);
            var feet = OpenTK.Mathematics.Vector3.Lerp(_driven.PrevPosition, _driven.Position, a)
                - new OpenTK.Mathematics.Vector3(0, _driven.Position.Y - _driven.Feet.Y, 0);
            float yaw = LerpAngle(_driven.PrevYaw, _driven.Yaw, a) + _orbit;
            float scale = Metadata.HunterScales[_driven.Hunter];
            var target = feet + new OpenTK.Mathematics.Vector3(0, (1.05f + ViewLift) * scale, 0);
            var back = new OpenTK.Mathematics.Vector3(-(float)Math.Sin(yaw), 0, (float)Math.Cos(yaw));
            // Keep a wall/ceiling from coming between the camera and the hunter: try the normal boom,
            // then progressively higher (over-the-shoulder -> overhead) booms, and take the first with
            // enough room; failing that, the one with the most room -- but never closer than ~a body
            // length, which would put the camera inside the hunter.
            OpenTK.Mathematics.Vector3 cam = target; float bestClear = -1f;
            foreach (float up in CamBoomHeights)
            {
                var arm = back * ((_compare && _hdTemplate != null ? 6.2f : 3.4f) / Zoom) + new OpenTK.Mathematics.Vector3(0, up / Zoom, 0);
                float armLen = arm.Length; var armDir = arm / armLen;
                float clear = RayNearest(target.X, target.Y, target.Z, armDir.X, armDir.Y, armDir.Z, armLen + 0.25f) - 0.25f;
                if (clear > bestClear)
                {
                    bestClear = clear;
                    cam = target + armDir * Math.Max(Math.Min(1.3f, armLen), Math.Min(armLen, clear));
                }
                if (clear >= armLen * 0.8f) break;
            }
            var look = (target - cam).Normalized();
            _lastLook = look;
            _pos[0] = cam.X; _pos[1] = cam.Y; _pos[2] = cam.Z;
            fx = look.X; fy = look.Y; fz = look.Z;
        }
        else if (_session != null && Walk)
        {
            // LIVE SIM (play mode): feed the gamepad to the session; its PlayerController walks the
            // collision and drives the camera, the world ticks, and mods fire. Render its snapshot.
            _raw.Clear();
            _raw.Set(RawAxis.LeftX, Lx); _raw.Set(RawAxis.LeftY, Ly);
            _raw.Set(RawAxis.RightX, Rx); _raw.Set(RawAxis.RightY, Ry);
            RenderSnapshot snap = _session.Update(dt, _raw);
            System.Numerics.Vector3 cpos = snap.CameraPosition, cfac = snap.CameraFacing;
            if (cpos.Y < _center[1] - _radius && _session.Player != null) // fell out of the world -> recenter
            {
                _session.Player.Position = new System.Numerics.Vector3(_center[0], _center[1], _center[2]);
                cpos = _session.Player.Position;
            }
            _pos[0] = cpos.X; _pos[1] = cpos.Y; _pos[2] = cpos.Z;
            fx = cfac.X; fy = cfac.Y; fz = cfac.Z;
            if (_session.Player != null) { _yaw = _session.Player.Yaw; _pitch = _session.Player.Pitch; }
            if (now - _lastSimLog > 2000) { _lastSimLog = now; Log.Info("MPHRender", $"sim tick={_session.World.TickCount} cam=({_pos[0]:0.0},{_pos[1]:0.0},{_pos[2]:0.0})"); }
        }
        else
        {
            // FREE-FLY: the model viewer, or room inspection (A toggled play off). Integrate here.
            _yaw += Rx * 2.2f * dt;
            _pitch -= Ry * 2.2f * dt;
            if (_pitch > 1.5f) _pitch = 1.5f; else if (_pitch < -1.5f) _pitch = -1.5f;
            float cy = (float)Math.Cos(_yaw), sy = (float)Math.Sin(_yaw);
            float cp = (float)Math.Cos(_pitch), sp = (float)Math.Sin(_pitch);
            fx = sy * cp; fy = sp; fz = -cy * cp;
            float rgx = cy, rgz = sy;
            float mv = _radius * 0.75f * dt;
            _pos[0] += (fx * -Ly + rgx * Lx) * mv;
            _pos[1] += fy * -Ly * mv + (Up ? mv : 0f) - (Down ? mv : 0f);
            _pos[2] += (fz * -Ly + rgz * Lx) * mv;
            if (_session?.Player != null) // park the sim player here so returning to play resumes smoothly
            {
                _session.Player.Position = new System.Numerics.Vector3(_pos[0], _pos[1], _pos[2]);
                _session.Player.Yaw = _yaw; _session.Player.Pitch = _pitch;
            }
        }

        Matrix.SetLookAtM(_view, 0, _pos[0], _pos[1], _pos[2],
            _pos[0] + fx, _pos[1] + fy, _pos[2] + fz, 0f, 1f, 0f);
        Matrix.MultiplyMM(_mvp, 0, _proj, 0, _view, 0);
        // upper-3x3 of the view matrix (column-major) for texgen env-mapping
        _viewRot[0] = _view[0]; _viewRot[1] = _view[1]; _viewRot[2] = _view[2];
        _viewRot[3] = _view[4]; _viewRot[4] = _view[5]; _viewRot[5] = _view[6];
        _viewRot[6] = _view[8]; _viewRot[7] = _view[9]; _viewRot[8] = _view[10];
        // Procedural hair-jiggle spring (demo): a gentle idle sway + left-stick force drive an underdamped
        // spring; the lateral displacement _jigD is fed to the vertex shader, weighted by hair height.
        if (_jiggle)
        {
            float amp = _radius * 0.10f, tt = now / 1000f, k = 45f, c = 5f;
            float driveX = amp * k * (float)Math.Sin(tt * 2.4f) + Lx * amp * k * 3f;
            float driveZ = amp * k * (float)Math.Sin(tt * 1.7f + 1.3f) + (-Ly) * amp * k * 3f;
            _jigVx += (driveX - k * _jigDx - c * _jigVx) * dt;
            _jigVz += (driveZ - k * _jigDz - c * _jigVz) * dt;
            _jigDx += _jigVx * dt; _jigDz += _jigVz * dt;
        }
        GLES30.GlUseProgram(_program);
        GLES30.GlUniformMatrix4fv(_uMvp, 1, false, _mvp, 0);
        GLES30.GlUniformMatrix3fv(_uViewRot, 1, false, _viewRot, 0);
        GLES30.GlUniform1i(_uTex, 0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindVertexArray(_vao);
        void DrawOne(DrawBatch b)
        {
            SetSkin(_uSkin, _uModel, _uBones, b);
            bool jig = _jiggle && b.Jiggle;
            GLES30.GlUniform1i(_uJigOn, jig ? 1 : 0);
            if (jig) { GLES30.GlUniform3f(_uJigD, _jigDx, 0f, _jigDz); GLES30.GlUniform1f(_uJigTop, b.JigTop); GLES30.GlUniform1f(_uJigBot, b.JigBot); }
            GLES30.GlUniform1i(_uTexgen, b.Texgen ? 1 : 0);
            GLES30.GlUniform1i(_uMetallic, b.Metallic ? 1 : 0);
            GLES30.GlUniform1i(_uUnlit, b.Unlit ? 1 : 0);
            if (b.Metallic && b.ColB != null)
                GLES30.GlUniform3f(_uColB, b.ColB[0], b.ColB[1], b.ColB[2]);
            GLES30.GlUniform1f(_uLerpScale, b.LerpScale);
            GLES30.GlUniform1f(_uReflScale, b.ReflScale);
            GLES30.GlUniform1i(_uTextured, b.Textured ? 1 : 0);
            // Armour (MatKind 1) blends C1<->C0 (opposite direction) per its TEV program; the user's
            // Swap toggle flips this globally while we calibrate.
            GLES30.GlUniform1i(_uSwap, b.CombineSwap ^ Swap ? 1 : 0);
            GLES30.GlUniform1f(_uLerp, Lerp);
            GLES30.GlUniform1f(_uExposure, Exposure);
            GLES30.GlUniform1i(_uDebug, Debug ? 1 : 0);
            GLES30.GlUniform1i(_uMatId, b.MatKind);
            GLES30.GlUniform1i(_uMultiTex, b.MultiTex ? 1 : 0);
            GLES30.GlUniform1f(_uUnlitScale, b.UnlitScale);
            GLES30.GlUniform3f(_uAmbient, b.Ambient[0], b.Ambient[1], b.Ambient[2]);
            // Hair cutout: sample the opacity mask on unit 4 and discard below 0.5 (see shader).
            GLES30.GlUniform1i(_uCutout, b.AlphaCutout ? 1 : 0);
            GLES30.GlUniform1i(_uTranslucent, b.Translucent ? 1 : 0);
            if (b.AlphaCutout)
            {
                GLES30.GlActiveTexture(GLES30.GlTexture4); GLES30.GlBindTexture(GLES30.GlTexture2d, b.OpacityTex); GLES30.GlUniform1i(_uOpacity, 4);
                GLES30.GlActiveTexture(GLES30.GlTexture0);
            }
            // Body's real TEV needs 4 textures: diffuse(unit0) + incand(1) + spec(2) + reflection(3).
            if (b.MultiTex)
            {
                GLES30.GlActiveTexture(GLES30.GlTexture1); GLES30.GlBindTexture(GLES30.GlTexture2d, b.IncandTex); GLES30.GlUniform1i(_uIncand, 1);
                GLES30.GlActiveTexture(GLES30.GlTexture2); GLES30.GlBindTexture(GLES30.GlTexture2d, b.SpecTex); GLES30.GlUniform1i(_uSpec, 2);
                GLES30.GlActiveTexture(GLES30.GlTexture3); GLES30.GlBindTexture(GLES30.GlTexture2d, b.ReflTex); GLES30.GlUniform1i(_uReflTex, 3);
                GLES30.GlActiveTexture(GLES30.GlTexture0);
            }
            GLES30.GlBindTexture(GLES30.GlTexture2d, b.Tex);
            GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        }
        // Dispatch: HD batches with a parsed GX program go through the interpreter (when --es gxeval 1);
        // everything else stays on the proven shader. Restore the main program when switching back.
        bool gxBound = false;
        void Emit(DrawBatch b)
        {
            if (_gxEval && b.Gx != null) { DrawGx(b); gxBound = true; return; }
            if (gxBound)
            {
                GLES30.GlUseProgram(_program);
                GLES30.GlUniformMatrix4fv(_uMvp, 1, false, _mvp, 0);
                GLES30.GlUniformMatrix3fv(_uViewRot, 1, false, _viewRot, 0);
                GLES30.GlUniform1i(_uTex, 0);
                GLES30.GlActiveTexture(GLES30.GlTexture0);
                gxBound = false;
            }
            DrawOne(b);
        }
        // Opaque pass first (writes depth normally); transparent passes (additive/translucent) come after.
        _passMode = 0;
        foreach (var b in _batches) if (!b.Additive && !b.Translucent) Emit(b);
        // Live hunters (opaque, GPU-skinned from this frame's pose). Their uniforms belong to the main
        // program, so make sure it's the one bound (the GX interpreter may have been last).
        if (_actors.Count > 0) DrawSkinnedActors(Emit, 0);
        // Translucent alpha-blend pass -- see-through energy (Noxus's cyan glow lines, CMODE0 dst=INVSRCA),
        // depth-tested but no z-write, drawn over the opaque geometry.
        bool anyTrans = AnySkinned(1); foreach (var b in _batches) if (b.Translucent) { anyTrans = true; break; }
        if (anyTrans)
        {
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
            GLES30.GlDepthMask(false);
            _passMode = 1;
            foreach (var b in _batches) if (b.Translucent) Emit(b);
            if (_actors.Count > 0) DrawSkinnedActors(Emit, 1);
            GLES30.GlDepthMask(true);
            GLES30.GlDisable(GLES30.GlBlend);
        }
        // Then the additive/translucent pass -- the energy blade GLOWS over the scene (CMODE0 src=SRCA
        // dst=ONE) and doesn't write depth (ZMODE write=off), read from the material. Drawn last so it
        // composites over everything opaque behind it.
        bool anyAdditive = AnySkinned(2); foreach (var b in _batches) if (b.Additive) { anyAdditive = true; break; }
        if (anyAdditive)
        {
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOne);
            GLES30.GlDepthMask(false);
            _passMode = 2;
            // cull=None means the real blade draws front AND back faces. Verified (2026-08-12): toggling GL
            // culling on our single-sheet DAE export produced byte-identical frames, proving the export
            // collapsed the source's two coincident sheets into one -- so we draw the additive pass TWICE to
            // rebuild the front+back stack the real geometry would produce. Do not "simplify" this away.
            for (int pass = 0; pass < 2; pass++) foreach (var b in _batches) if (b.Additive) Emit(b);
            // the rigged trophy's blade comes from the same collapsed DAE export -> same double draw
            if (_actors.Count > 0) for (int pass = 0; pass < 2; pass++) DrawSkinnedActors(Emit, 2);
            GLES30.GlDepthMask(true);
            GLES30.GlDisable(GLES30.GlBlend);
        }
        // Bloom the offscreen scene onto the screen (HD viewer only) -- the trophy glow/shimmer.
        if (bloom) DrawBloom();
        // Gameplay HUD: normal room mode only -- skip it for the HD-trophy/model-viewer debug paths so
        // it doesn't visually stack with the shader-tuning debug HUD (_hud TextView).
        if (_model == null && _hd == null && _animHunter == null) DrawGameplayHud(); // first-person HUD: not in the third-person viewer
    }

    // Draw one HD batch through the GX program (MphRecomp.Render.GxShader): the material's own light channels,
    // texture coordinates, TEV stages, alpha test and blending, all read from the .brres. The scene light is BRAWL'S
    // OWN trophy-viewer light -- the light set every trophy material names (index 20, "C_lightSet" in the disc's
    // menu/collection/FigureDisp2_bg0_en.brres SCN0): ambient (102,100,100), one directional light of 102 grey from
    // (-50, 50, 90) (upper left, in front), white specular, shininess 128. Checked against the owner's Dolphin
    // screenshots of Normal Suit / Samus R1 / Samus: suit colours within a few levels (BrawlCrate's own lamp runs
    // 15-45 levels darker). Camera-relative, like the trophy viewer. Exposure is left at 1: no tuning.
    static readonly float[] GxLightDir = Norm3(-50f, 50f, 90f);
    static float[] Norm3(float x, float y, float z) { float l = MathF.Sqrt(x * x + y * y + z * z); return new[] { x / l, y / l, z / l, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; }
    static readonly float[] GxLightCol = { 0.4f, 0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] GxLightSpecCol = { 1f, 1f, 1f, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    static readonly float[] GxLightSpecK = { 64f, 0f, -63f, 1, 0, 0, 1, 0, 0, 1, 0, 0 };   // shininess 128: (s/2, 0, 1 - s/2)
    void DrawGx(DrawBatch b)
    {
        var P = b.GxP!;
        GLES30.GlUseProgram(_gxProgram);
        GLES30.GlUniformMatrix4fv(_gxMvp, 1, false, _mvp, 0);
        GLES30.GlUniformMatrix3fv(_gxViewRot, 1, false, _viewRot, 0);
        SetSkin(_gxSkin, _gxModel, _gxBones, b);
        int U(string n) => _gxU[n];
        GLES30.GlUniform1i(U("uNumStages"), P.NumStages);
        GLES30.GlUniform4iv(U("uCabcd"), 8, P.Cabcd, 0); GLES30.GlUniform4iv(U("uCmod"), 8, P.Cmod, 0);
        GLES30.GlUniform4iv(U("uAabcd"), 8, P.Aabcd, 0); GLES30.GlUniform4iv(U("uAmod"), 8, P.Amod, 0);
        GLES30.GlUniform4iv(U("uDst"), 8, P.Dst, 0); GLES30.GlUniform4iv(U("uKsel"), 8, P.Ksel, 0);
        GLES30.GlUniform1iv(U("uRasChan"), 8, P.RasChan, 0); GLES30.GlUniform4iv(U("uSwap"), 4, P.Swap, 0);
        GLES30.GlUniform4fv(U("uReg"), 4, P.Reg, 0); GLES30.GlUniform4fv(U("uKonst"), 4, P.Konst, 0);
        GLES30.GlUniform4iv(U("uColCtrl"), 2, P.ColCtrl, 0); GLES30.GlUniform4iv(U("uAlpCtrl"), 2, P.AlpCtrl, 0);
        GLES30.GlUniform2iv(U("uAttn"), 2, P.Attn, 0);
        GLES30.GlUniform4fv(U("uChanMat"), 2, P.ChanMat, 0); GLES30.GlUniform4fv(U("uChanAmb"), 2, P.ChanAmb, 0);
        GLES30.GlUniform3f(U("uSceneAmb"), 102f / 255f, 100f / 255f, 100f / 255f);
        GLES30.GlUniform1i(U("uNumLights"), 1);
        GLES30.GlUniform3fv(U("uLightDir"), 4, GxLightDir, 0); GLES30.GlUniform3fv(U("uLightCol"), 4, GxLightCol, 0);
        GLES30.GlUniform3fv(U("uLightSpecCol"), 4, GxLightSpecCol, 0); GLES30.GlUniform3fv(U("uLightSpecK"), 4, GxLightSpecK, 0);
        GLES30.GlUniform1iv(U("uTgMode"), 8, P.TgMode, 0);
        GLES30.GlUniformMatrix3fv(U("uTexMtx"), 8, false, P.Scrolls ? MphRecomp.Render.GxShader.ScrolledTexMtx(b.Gx!, P, SystemClock.UptimeMillis() / 1000.0) : P.TexMtx, 0);
        GLES30.GlUniform4iv(U("uAlphaTest"), 1, P.AlphaTest, 0); GLES30.GlUniform2fv(U("uAlphaRef"), 1, P.AlphaRef, 0);
        GLES30.GlUniform1i(U("uBlendAlpha"), P.Blend ? 1 : 0);
        GLES30.GlUniform1f(U("uExposure"), 1f);
        GLES30.GlUniform1i(U("uRawEnvNormal"), 0);
        GLES30.GlUniform1i(U("uNmap"), P.NmapUnit + 1); GLES30.GlUniform1i(U("uNmapTc"), P.NmapCoord);
        // texture units: unit u holds the layer bound to texmap u, with that layer's wrap and filter
        for (int u = 0; u < 8; u++)
        {
            GLES30.GlActiveTexture(GLES30.GlTexture0 + u);
            var L = P.Units[u];
            int li = L == null ? -1 : Array.IndexOf(b.Gx!.Layers, L);
            GLES30.GlBindTexture(GLES30.GlTexture2d, li >= 0 && b.GxTex != null ? b.GxTex[li] : WhiteTexture());
            if (L != null)
            {
                int W(int w) => w == 0 ? GLES30.GlClampToEdge : w == 2 ? GLES30.GlMirroredRepeat : GLES30.GlRepeat;
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, W(L.WrapS));
                GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, W(L.WrapT));
            }
            GLES30.GlUniform1i(_gxTexUnits[u], u);
        }
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        // the material's own blend mode (Samus's visor adds by its colour, the blade adds, glass blends)
        if (P.Blend)
        {
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(GxSrcFactor(P.BlendSrc), GxDstFactor(P.BlendDst));
            GLES30.GlDepthMask(false);
        }
        // the material's own depth test: trophies ask for <=, so a layer drawn onto the same surface (Dark Samus's
        // shoulder shell over her phazon) lands on top -- GL's default < dropped it
        GLES30.GlDepthFunc(GxDepthFunc(P.DepthFunc));
        GLES30.GlDrawArrays(GLES30.GlTriangles, b.Start, b.Count);
        GLES30.GlDepthFunc(GLES30.GlLess);
        // back to the state this pass set up
        if (_passMode == 0) { GLES30.GlDisable(GLES30.GlBlend); GLES30.GlDepthMask(true); }
        else { GLES30.GlEnable(GLES30.GlBlend); GLES30.GlBlendFunc(GLES30.GlSrcAlpha, _passMode == 2 ? GLES30.GlOne : GLES30.GlOneMinusSrcAlpha); GLES30.GlDepthMask(false); }
    }
    static int GxDepthFunc(int f) => f switch
    {
        0 => GLES30.GlNever, 1 => GLES30.GlLess, 2 => GLES30.GlEqual, 3 => GLES30.GlLequal,
        4 => GLES30.GlGreater, 5 => GLES30.GlNotequal, 6 => GLES30.GlGequal, _ => GLES30.GlAlways,
    };
    static int GxSrcFactor(int f) => f switch
    {
        0 => GLES30.GlZero, 1 => GLES30.GlOne, 2 => GLES30.GlDstColor, 3 => GLES30.GlOneMinusDstColor,
        4 => GLES30.GlSrcAlpha, 5 => GLES30.GlOneMinusSrcAlpha, 6 => GLES30.GlDstAlpha, _ => GLES30.GlOneMinusDstAlpha,
    };
    static int GxDstFactor(int f) => f switch
    {
        0 => GLES30.GlZero, 1 => GLES30.GlOne, 2 => GLES30.GlSrcColor, 3 => GLES30.GlOneMinusSrcColor,
        4 => GLES30.GlSrcAlpha, 5 => GLES30.GlOneMinusSrcAlpha, 6 => GLES30.GlDstAlpha, _ => GLES30.GlOneMinusDstAlpha,
    };
    static readonly int[] _gxTexGenZero = new int[8];

    // Upload a baked batch's textures and describe it for drawing (material flags carried through).
    DrawBatch ToDrawBatch(GeometryBaker.Batch b, int start, int count)
    {
        int tex = b.Pixels != null ? UploadTexture(b.Pixels, b.W, b.H) : WhiteTexture();
        var db = new DrawBatch { Tex = tex, Start = start, Count = count, Texgen = b.Texgen, Metallic = b.Metallic, Unlit = b.Unlit, Textured = b.Textured, MultiTex = b.MultiTex, MatKind = b.MatKind, CombineSwap = b.CombineSwap, LerpScale = b.LerpScale, ReflScale = b.ReflScale, UnlitScale = b.UnlitScale, Ambient = b.Ambient, ColB = b.ColB, Additive = b.Additive, Translucent = b.Translucent, AlphaCutout = b.AlphaCutout, Gx = b.Gx, Jiggle = b.Jiggle, JigTop = b.JigTop, JigBot = b.JigBot };
        if (b.MultiTex)
        {
            if (b.IncandPixels != null) db.IncandTex = UploadTexture(b.IncandPixels, b.IncandW, b.IncandH);
            if (b.SpecPixels != null) db.SpecTex = UploadTexture(b.SpecPixels, b.SpecW, b.SpecH);
            if (b.ReflPixels != null) db.ReflTex = UploadTexture(b.ReflPixels, b.ReflW, b.ReflH);
        }
        if (b.AlphaCutout && b.OpacityPixels != null) db.OpacityTex = UploadTexture(b.OpacityPixels, b.OpacityW, b.OpacityH);
        if (_gxEval && b.Gx != null)
        {
            db.GxP = MphRecomp.Render.GxShader.Pack(b.Gx);
            db.Additive = b.Gx.Additive; db.Translucent = b.Gx.BlendEnable && !b.Gx.Additive;
        }
        if (b.GxPixels != null)
        {
            db.GxTex = new int[8]; db.GxTexGen = b.GxTexGen;
            for (int t = 0; t < 8; t++)
                db.GxTex[t] = b.GxPixels[t] != null ? UploadTexture(b.GxPixels[t]!, b.GxW![t], b.GxH![t]) : WhiteTexture();
        }
        return db;
    }

    // Skinned batches carry the current actor's palette + model matrix (set right before its draw);
    // everything else must draw with skinning off -- uniforms persist per program.
    static void SetSkin(int uSkin, int uModel, int uBones, DrawBatch b)
    {
        if (b.SkinPalette == null) { GLES30.GlUniform1i(uSkin, 0); return; }
        GLES30.GlUniform1i(uSkin, 1);
        GLES30.GlUniformMatrix4fv(uModel, 1, false, b.SkinModel!, 0);
        GLES30.GlUniformMatrix4fv(uBones, b.SkinCount, false, b.SkinPalette, 0);
    }

    // Bake a model once (cached by name+recolor) into template batches in model-local world.
    static List<GeometryBaker.Batch> Template(Dictionary<string, List<GeometryBaker.Batch>> cache, string name, int recolor)
    {
        string key = name + "#" + recolor;
        if (!cache.TryGetValue(key, out List<GeometryBaker.Batch>? tmpl))
        {
            var scratch = new float[3];
            try { tmpl = GeometryBaker.Bake(Read.GetModelInstance(name).Model, scratch, out _, useAnimation: false, recolor: recolor, useMatrixStack: true); }
            catch (Exception ex) { Log.Warn("MPHRender", $"model '{name}' failed: " + ex.Message); tmpl = new(); }
            cache[key] = tmpl;
        }
        return tmpl;
    }

    // Instance a cached template into `dest` under a world transform (positions at floats
    // [0..2], normals at [3..5] of each 11-float vertex; texture/uv/colour copied through).
    static void AppendInstance(List<GeometryBaker.Batch> dest, List<GeometryBaker.Batch> tmpl, OpenTK.Mathematics.Matrix4 m)
    {
        var t = new OpenTK.Mathematics.Vector3(m.M41, m.M42, m.M43);
        foreach (var tb in tmpl)
        {
            var nb = new GeometryBaker.Batch { Pixels = tb.Pixels, W = tb.W, H = tb.H, Texgen = tb.Texgen };
            var src = tb.Verts;
            for (int i = 0; i + 10 < src.Count; i += 11)
            {
                var p = OpenTK.Mathematics.Vector3.TransformPosition(new OpenTK.Mathematics.Vector3(src[i], src[i + 1], src[i + 2]), m);
                var n = OpenTK.Mathematics.Vector3.TransformPosition(new OpenTK.Mathematics.Vector3(src[i + 3], src[i + 4], src[i + 5]), m) - t;
                if (n.LengthSquared > 1e-12f) n = n.Normalized();
                nb.Verts.Add(p.X); nb.Verts.Add(p.Y); nb.Verts.Add(p.Z);
                nb.Verts.Add(n.X); nb.Verts.Add(n.Y); nb.Verts.Add(n.Z);
                nb.Verts.Add(src[i + 6]); nb.Verts.Add(src[i + 7]);
                nb.Verts.Add(src[i + 8]); nb.Verts.Add(src[i + 9]); nb.Verts.Add(src[i + 10]);
            }
            dest.Add(nb);
        }
    }

    // Entity placement matrix: matches EntityBase.GetTransformMatrix (rows = right, up,
    // facing) with an optional per-instance scale folded in, translation in the last row.
    static OpenTK.Mathematics.Matrix4 EntityMatrix(OpenTK.Mathematics.Vector3 facing,
        OpenTK.Mathematics.Vector3 up, OpenTK.Mathematics.Vector3 scale, OpenTK.Mathematics.Vector3 pos)
    {
        var right = OpenTK.Mathematics.Vector3.Cross(up, facing);
        right = right.LengthSquared > 1e-12f ? right.Normalized() : OpenTK.Mathematics.Vector3.UnitX;
        up = OpenTK.Mathematics.Vector3.Cross(facing, right);
        return new OpenTK.Mathematics.Matrix4(
            right.X * scale.X, right.Y * scale.X, right.Z * scale.X, 0f,
            up.X * scale.Y, up.Y * scale.Y, up.Z * scale.Y, 0f,
            facing.X * scale.Z, facing.Y * scale.Z, facing.Z * scale.Z, 0f,
            pos.X, pos.Y, pos.Z, 1f);
    }

    // Item pickups (health/ammo/weapons) at their authored ItemSpawn positions. Upright
    // (pickups spin about Y in-game), so translation only. Render-only, not collision.
    static List<GeometryBaker.Batch> LoadPickupBatches(string room, Dictionary<string, List<GeometryBaker.Batch>> cache)
    {
        var result = new List<GeometryBaker.Batch>();
        RoomMetadata meta = Metadata.RoomMetadata[room];
        if (meta.EntityPath == null) return result;
        IReadOnlyList<MphRead.Entity> entities;
        try { entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt, allowHook: true); }
        catch (Exception ex) { Log.Error("MPHRender", "GetEntities(pickups) failed: " + ex); return result; }
        int placed = 0;
        foreach (MphRead.Entity e in entities)
        {
            if (e.Type != EntityType.ItemSpawn) continue;
            int it = (int)((MphRead.Entity<ItemSpawnEntityData>)e).Data.ItemType;
            if (it < 0 || it >= Metadata.Items.Count) continue;
            var tmpl = Template(cache, Metadata.Items[it], 0);
            if (tmpl.Count == 0) continue;
            AppendInstance(result, tmpl, OpenTK.Mathematics.Matrix4.CreateTranslation(e.Position));
            placed++;
        }
        Log.Info("MPHRender", $"pickups placed={placed} batches={result.Count}");
        return result;
    }

    // Visible prop entities: doors, jump pads, platforms, force fields. Each is placed at
    // its authored position and oriented by its facing/up basis (matching the game). Doors
    // and force fields carry a palette recolor; force fields scale by Width/Height. Skips
    // locks/beams/animation/logic-only entities. Render-only, not added to collision.
    static List<GeometryBaker.Batch> LoadPropBatches(string room, Dictionary<string, List<GeometryBaker.Batch>> cache)
    {
        var result = new List<GeometryBaker.Batch>();
        RoomMetadata meta = Metadata.RoomMetadata[room];
        if (meta.EntityPath == null) return result;
        IReadOnlyList<MphRead.Entity> entities;
        try { entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt, allowHook: true); }
        catch (Exception ex) { Log.Error("MPHRender", "GetEntities(props) failed: " + ex); return result; }
        int placed = 0;
        foreach (MphRead.Entity e in entities)
        {
            // NodeDefense (KOTH/Nodes capture point) = two models: the data terminal at the
            // node, plus the capture ring scaled to the volume's cylinder radius and raised
            // 0.7 (matching NodeDefenseEntity.GetModelTransform; its spin is animated -> static).
            if (e.Type == EntityType.NodeDefense)
            {
                try
                {
                    var d = ((MphRead.Entity<NodeDefenseEntityData>)e).Data;
                    float cyl = d.Volume.CylinderRadius.FloatValue;
                    if (!(cyl > 0.01f && cyl < 100f)) cyl = 1f; // sanity clamp
                    var term = Template(cache, "koth_data_flow", 0);
                    if (term.Count > 0) AppendInstance(result, term, EntityMatrix(e.FacingVector, e.UpVector, OpenTK.Mathematics.Vector3.One, e.Position));
                    var ring = Template(cache, "koth_terminal", 0);
                    if (ring.Count > 0) AppendInstance(result, ring, EntityMatrix(e.FacingVector, e.UpVector,
                        new OpenTK.Mathematics.Vector3(cyl, cyl, cyl), e.Position + new OpenTK.Mathematics.Vector3(0f, 0.7f, 0f)));
                    placed++;
                }
                catch (Exception ex) { Log.Warn("MPHRender", "NodeDefense failed: " + ex.Message); }
                continue;
            }
            string? name = null; int recolor = 0;
            var scale = OpenTK.Mathematics.Vector3.One;
            try
            {
                switch (e.Type)
                {
                    case EntityType.Door:
                    {
                        var d = ((MphRead.Entity<DoorEntityData>)e).Data;
                        name = Metadata.Doors[(int)d.DoorType].Name;
                        if (d.DoorType == DoorType.Standard || d.DoorType == DoorType.Thin)
                            recolor = Metadata.DoorPalettes[(int)d.PaletteId];
                        break;
                    }
                    case EntityType.JumpPad:
                        name = Metadata.JumpPads[(int)((MphRead.Entity<JumpPadEntityData>)e).Data.ModelId];
                        break;
                    case EntityType.Platform:
                        name = Metadata.GetPlatformById((int)((MphRead.Entity<PlatformEntityData>)e).Data.ModelId)?.Name;
                        break;
                    case EntityType.ForceField:
                    {
                        var d = ((MphRead.Entity<ForceFieldEntityData>)e).Data;
                        name = "ForceField";
                        recolor = Metadata.DoorPalettes[(int)d.Type];
                        scale = new OpenTK.Mathematics.Vector3(d.Width.FloatValue, d.Height.FloatValue, 1f);
                        break;
                    }
                    case EntityType.OctolithFlag: name = "octolith_ctf"; break; // CTF/Bounty octolith
                    case EntityType.FlagBase: name = "flagbase_cap"; break;      // capture base marker
                }
            }
            catch (Exception ex) { Log.Warn("MPHRender", $"prop {e.Type} resolve failed: " + ex.Message); continue; }
            if (string.IsNullOrEmpty(name)) continue;
            var tmpl = Template(cache, name!, recolor);
            if (tmpl.Count == 0) continue;
            AppendInstance(result, tmpl, EntityMatrix(e.FacingVector, e.UpVector, scale, e.Position));
            placed++;
        }
        Log.Info("MPHRender", $"props placed={placed} batches={result.Count}");
        return result;
    }

    // Pose a model with its Idle animation (PlayerAnimation.Idle=8) applied to the whole
    // skeleton. Hunters need this -- their bind pose is a collapsed rest state. Returns true
    // if posed (node.Animation populated); false if the model has no such animation.
    static bool ApplyIdle(ModelInstance inst)
    {
        const int idle = 8;
        if (inst.Model.AnimationGroups.Any && idle < inst.Model.AnimationGroups.Node.Count)
        {
            inst.SetAnimation(idle);
            if (inst.AnimInfo.Node.Group != null && inst.AnimInfo.Node.Group.Count > 0)
            {
                inst.Model.AnimateNodes(0, false, OpenTK.Mathematics.Matrix4.Identity, OpenTK.Mathematics.Vector3.One, inst.AnimInfo);
                return true;
            }
        }
        return false;
    }

    // Bake a model once (cached), Idle-posed if it's animated (hunters), else bind pose.
    static List<GeometryBaker.Batch> PosedTemplate(Dictionary<string, List<GeometryBaker.Batch>> cache, string name)
    {
        string key = name + "#idle";
        if (!cache.TryGetValue(key, out List<GeometryBaker.Batch>? tmpl))
        {
            var scratch = new float[3];
            try
            {
                ModelInstance inst = Read.GetModelInstance(name);
                bool an = ApplyIdle(inst);
                tmpl = GeometryBaker.Bake(inst.Model, scratch, out _, useAnimation: an, useMatrixStack: true);
            }
            catch (Exception ex) { Log.Warn("MPHRender", $"hunter '{name}' failed: " + ex.Message); tmpl = new(); }
            cache[key] = tmpl;
        }
        return tmpl;
    }

    static readonly string[] HunterLods =
        { "Samus_lod0", "Kanden_lod0", "Trace_lod0", "Sylux_lod0", "Nox_lod0", "Spire_lod0", "Weavel_lod0" };

    // Place an Idle-posed hunter standing at each PlayerSpawn, oriented by the spawn's
    // facing/up basis (same as the game's SetTransform). Hunters cycle so the seven are all
    // represented. Render-only. Positions are the game's, but pose/scale/orientation are
    // PENDING on-device visual confirmation.
    static List<GeometryBaker.Batch> LoadHunterBatches(string room, Dictionary<string, List<GeometryBaker.Batch>> cache)
    {
        var result = new List<GeometryBaker.Batch>();
        RoomMetadata meta = Metadata.RoomMetadata[room];
        if (meta.EntityPath == null) return result;
        IReadOnlyList<MphRead.Entity> entities;
        try { entities = Read.GetEntities(meta.EntityPath, layerId: MphRecomp.World.RoomCollision.EntityLayerId(meta), meta.FirstHunt, allowHook: true); }
        catch (Exception ex) { Log.Error("MPHRender", "GetEntities(hunters) failed: " + ex); return result; }
        int placed = 0;
        foreach (MphRead.Entity e in entities)
        {
            if (e.Type != EntityType.PlayerSpawn) continue;
            var tmpl = PosedTemplate(cache, HunterLods[placed % HunterLods.Length]);
            if (tmpl.Count == 0) continue;
            AppendInstance(result, tmpl, EntityMatrix(e.FacingVector, e.UpVector, OpenTK.Mathematics.Vector3.One, e.Position));
            placed++;
        }
        Log.Info("MPHRender", $"hunters placed={placed} batches={result.Count}");
        return result;
    }

    // ---- live animated hunters ----------------------------------------------------------------------

    // Same spawn walk + hunter cycling as LoadHunterBatches, but only RECORDS who stands where; the
    // animated actors are built in SetupAnimatedHunters once the collision mesh is loaded. Uses the
    // game's single entity layer for this room setup (Battle, 2 players) -- all-layers loading put a
    // second hunter inside the first at Sanctorus's Defender-only spawn copies.
    void CollectSpawnHunters(string room)
    {
        RoomMetadata meta = Metadata.RoomMetadata[room];
        if (meta.EntityPath == null) return;
        IReadOnlyList<MphRead.Entity> entities = Read.GetEntities(meta.EntityPath, layerId: MphRecomp.World.RoomCollision.EntityLayerId(meta), meta.FirstHunt, allowHook: true);
        int placed = 0;
        foreach (MphRead.Entity e in entities)
        {
            if (e.Type != EntityType.PlayerSpawn) continue;
            _spawnHunters.Add((AnimHunters[placed % AnimHunters.Length], e.Position, e.FacingVector));
            placed++;
        }
        Log.Info("MPHRender", $"spawn hunters recorded={placed}");
    }

    static float YawOf(OpenTK.Mathematics.Vector3 f) =>
        (Math.Abs(f.X) > 1e-4f || Math.Abs(f.Z) > 1e-4f) ? (float)Math.Atan2(f.X, -f.Z) : 0f;

    static float LerpAngle(float a, float b, float t)
    {
        float d = b - a;
        while (d > Math.PI) d -= 2f * (float)Math.PI;
        while (d < -Math.PI) d += 2f * (float)Math.PI;
        return a + d * t;
    }

    // Bake each needed hunter once as a skinned template (shared by every actor of that hunter), upload
    // them into one VBO (stride 12: + bone slot at attribute 4), then create the actors.
    void SetupAnimatedHunters()
    {
        var needed = new List<Hunter>();
        foreach (var s in _spawnHunters) if (!needed.Contains(s.Hunter)) needed.Add(s.Hunter);
        if (_animHunter != null) foreach (Hunter h in AnimHunters) if (!needed.Contains(h)) needed.Add(h); // viewer can switch
        if (needed.Count == 0) return;
        static void AddDsVertex(List<float> dst, List<float> v, int i) // 12-float DS skinned vertex -> shared 19-float layout
        {
            for (int k = 0; k < 11; k++) dst.Add(v[i + k]);
            dst.Add(v[i + 11]); dst.Add(0); dst.Add(0); dst.Add(0);   // bones: its matrix-stack slot
            dst.Add(1); dst.Add(0); dst.Add(0); dst.Add(0);           // weights: rigid, as the DS draws it
        }

        var all = new List<float>();
        foreach (Hunter h in needed)
        {
            Model m = Read.GetModelInstance(Metadata.HunterModels[h][0]).Model;
            // Bake-time pose = Idle frame 0 (only used for bounds + the few face-normal-fallback
            // triangles' normals); the vertices themselves stay bone-local.
            var poser = new BipedAnimator(m);
            poser.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            HunterRig.Pose(poser, 0f, _palette);
            var batches = GeometryBaker.Bake(m, new float[3], out _, useAnimation: true, useMatrixStack: true, skinned: true);
            var t = new SkinnedTemplate { Model = m, Hunter = h };
            foreach (var b in batches)
            {
                int start = all.Count / GpuSkinFloats;
                for (int i = 0; i + GeometryBaker.StrideSkinned <= b.Verts.Count; i += GeometryBaker.StrideSkinned) AddDsVertex(all, b.Verts, i);
                int tex = b.Pixels != null ? UploadTexture(b.Pixels, b.W, b.H) : WhiteTexture();
                t.Batches.Add(new DrawBatch { Tex = tex, Start = start, Count = b.Verts.Count / GeometryBaker.StrideSkinned, Texgen = b.Texgen });
            }
            _skinTemplates[h] = t;
            Log.Info("MPHRender", $"skinned template {h}: batches={t.Batches.Count} bones={m.NodeMatrixIds.Count} " +
                $"faceNormalFallbackTris={GeometryBaker.FaceNormalFallbacks} normalSlotMismatches={GeometryBaker.NormalSlotMismatches}");
        }

        if (_animHdId != null)
        {
            try { BuildHdTemplate(_animHdId, all); }
            catch (Exception ex) { Log.Error("MPHRender", $"animhd '{_animHdId}' failed: " + ex); _hdTemplate = null; _hdRigs.Clear(); }
            try { BuildBallTemplate(_animHdId, all); }
            catch (Exception ex) { Log.Error("MPHRender", $"animhd '{_animHdId}' morph ball failed: " + ex); _ball = null; }
        }

        var vaos = new int[1]; GLES30.GlGenVertexArrays(1, vaos, 0); _skinVao = vaos[0];
        var vbos = new int[1]; GLES30.GlGenBuffers(1, vbos, 0);
        GLES30.GlBindVertexArray(_skinVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, vbos[0]);
        float[] arr = all.ToArray();
        var bb = Java.Nio.ByteBuffer.AllocateDirect(arr.Length * sizeof(float));
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        var fb = bb.AsFloatBuffer()!; fb.Put(arr); fb.Position(0);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, arr.Length * sizeof(float), fb, GLES30.GlStaticDraw);
        GLES30.GlVertexAttribPointer(0, 3, GLES30.GlFloat, false, SkinStride, 0);
        GLES30.GlEnableVertexAttribArray(0);
        GLES30.GlVertexAttribPointer(1, 3, GLES30.GlFloat, false, SkinStride, 3 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(1);
        GLES30.GlVertexAttribPointer(2, 2, GLES30.GlFloat, false, SkinStride, 6 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(2);
        GLES30.GlVertexAttribPointer(3, 3, GLES30.GlFloat, false, SkinStride, 8 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(3);
        GLES30.GlVertexAttribPointer(4, 4, GLES30.GlFloat, false, SkinStride, 11 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(4);
        GLES30.GlVertexAttribPointer(5, 4, GLES30.GlFloat, false, SkinStride, 15 * sizeof(float));
        GLES30.GlEnableVertexAttribArray(5);
        GLES30.GlBindVertexArray(_vao);

        var col = new TriCollision(this);
        _worldMinY = float.MaxValue;
        for (int i = 1; i < _tri.Length; i += 3) if (_tri[i] < _worldMinY) _worldMinY = _tri[i];
        // viewer: the driven hunter takes the spawn with the most open space around it (the smallest
        // wall distance over 16 horizontal directions at chest height, maximised) so the chase camera
        // has room to orbit; the rest keep idling at theirs
        int drivenSpawn = -1;
        if (_animHunter != null && _spawnHunters.Count > 0)
        {
            float best = -1f;
            for (int i = 0; i < _spawnHunters.Count; i++)
            {
                var p = _spawnHunters[i].Pos;
                float floor = FloorBelow(p.X, p.Y + 1f, p.Z);
                float y = (floor > -1e8f ? floor : p.Y) + 1.2f;
                float minClear = float.MaxValue;
                for (int k = 0; k < 16; k++)
                {
                    float a = k * (float)Math.PI / 8f;
                    minClear = Math.Min(minClear, RayNearest(p.X, y, p.Z, (float)Math.Sin(a), 0f, -(float)Math.Cos(a), 20f));
                }
                if (minClear > best) { best = minClear; drivenSpawn = i; }
            }
            Log.Info("MPHRender", $"animview: spawn #{drivenSpawn} at {_spawnHunters[drivenSpawn].Pos} has the most room (clearance {best:0.0})");
        }
        for (int i = 0; i < _spawnHunters.Count; i++)
        {
            var (h, pos, facing) = _spawnHunters[i];
            bool driven = i == drivenSpawn;
            Hunter who = driven ? _animHunter!.Value : h;
            var d = new HunterDummy(who, _skinTemplates[who].Model, pos, YawOf(facing)) { Collision = col };
            d.SnapToFloor();
            if (driven) _driven = d;
            else
            {
                // separate players never spawn on the same tick; stagger the idlers so they don't
                // breathe in lockstep (each still runs the game's exact Idle/Flourish logic)
                int offset = (i * 37) % 98;
                for (int k = 0; k < offset; k++) d.Tick(default);
            }
            _actors.Add(d);
        }
        if (_animHunter != null && _driven == null)
        {
            // room without spawns: drop the viewer hunter at the collision centre
            var d = new HunterDummy(_animHunter.Value, _skinTemplates[_animHunter.Value].Model,
                new OpenTK.Mathematics.Vector3(_center[0], _center[1] + 5f, _center[2]), 0f) { Collision = col };
            d.SnapToFloor();
            _driven = d; _actors.Add(d);
        }
        _driverSpawnPos = _driven?.Position; _driverSpawnYaw = _driven?.Yaw ?? 0f;
        Log.Info("MPHRender", $"animated hunters: actors={_actors.Count} templates={_skinTemplates.Count} skinnedVerts={arr.Length / GpuSkinFloats} viewer={_animHunter?.ToString() ?? "off"} hd={_hdTemplate?.Id ?? "none"}");
    }

    static readonly float[] CamBoomHeights = { 0.55f, 1.4f, 2.4f, 3.4f };
    // Load the trophy exactly as the HD viewer does (materials, GX programs, textures), rig it to its DS
    // hunter (cached fit, TrophyRigIO), and append its vertices -- moved into rig space, each carrying up
    // to 4 bones + weights -- to the shared skinned vertex list.
    void BuildHdTemplate(string id, List<float> all)
    {
        if (!TrophyRigs.TryHunterFor(id, out Hunter rh)) { Log.Warn("MPHRender", $"animhd: no DS rig known for trophy '{id}'"); return; }
        var hdBatches = LoadHdModel(id, setBounds: false, out string daePath, out var dae);
        Model rm = Read.GetModelInstance(Metadata.HunterModels[rh][0]).Model;
        if (Math.Abs(rm.Scale.X - 1f) > 1e-6f) Log.Warn("MPHRender", $"animhd: {rm.Name} model scale {rm.Scale.X} != 1 (rig assumes 1)");
        var corners = new List<OpenTK.Mathematics.Vector3>();
        foreach (var mesh in dae.Meshes) foreach (var q in mesh.Positions) corners.Add(new OpenTK.Mathematics.Vector3(q.X, q.Y, q.Z));
        var rig = TrophyRigIO.LoadOrFit(daePath, rm, corners, msg => Log.Info("MPHRender", msg));
        var sculpted = MakeHdTemplate(id, rh, rm, rig, hdBatches, all, null, "OLD (bound as sculpted)");
        _hdRigs.Clear(); _hdRigs.Add(sculpted); _hdTemplate = sculpted; _hdSculpt = sculpted;
        // the alternative rigs prepared on the PC (-hdrig <trophy> --savevariants) -- never fitted here: that would
        // add minutes to every launch of a trophy without them. Only the ones worth flipping between are offered: the
        // best rig prepared (PIECES, else RETRO) and RIGID to compare against; the older experiments (NEW, CLEAN,
        // CLEAN + T-POSE, HINGE, MIA), the sculpt-bound OLD and the BEFORE snapshot stay on the PC (the owner's call,
        // 2026-09-28). An explicit animrig extra still loads the rig it names.
        var offered = new HashSet<string>(StringComparer.Ordinal) { "rigid", "retro", "pieces" };
        if (_animRig != null) offered.Add(_animRig);
        foreach (var v in TrophyRigIO.Variants)
        {
            if (!offered.Contains(v.Key)) continue;
            TrophyRig? alt = null;
            try { alt = TrophyRigIO.LoadOrFitVariant(daePath, rm, corners, v, fit: false, msg => Log.Info("MPHRender", msg)); }
            catch (Exception ex) { Log.Warn("MPHRender", $"animhd: rig '{v.Key}' failed to load: " + ex.Message); }
            if (alt == null) continue;
            var t = MakeHdTemplate(id, rh, rm, alt, hdBatches, all, sculpted, v.Label);
            _hdRigs.Add(t);
            // start on PIECES (the statue's own pieces) or RETRO (Retro's own rig copied) when the trophy has one, else RIGID
            // (solid pieces, like the game's models), else CLEAN -- later variants win: rigid, then retro, then pieces
            if (v.Key == (_animRig ?? "clean") || (_animRig == null && v.Key is "rigid" or "retro" or "pieces")) _hdTemplate = t;
        }
        // PIECES supersedes RETRO where a trophy has both; the sculpt-bound rig stays only as the texture share (and as
        // the rig itself when nothing else was prepared)
        if (_animRig == null && _hdRigs.Any(t => t.Label.StartsWith("PIECES")))
            _hdRigs.RemoveAll(t => t.Label.StartsWith("RETRO"));
        if (_hdRigs.Count > 1 && _animRig != "sculpted") _hdRigs.Remove(sculpted);
        if (!_hdRigs.Contains(_hdTemplate)) _hdTemplate = _hdRigs[^1];
        // the owner's own pose (Pose Studio, "<dae>.posed.txt"): its base rig + the turns the owner gave the pieces; when
        // present it is the one shown first
        try
        {
            var posed = TrophyRigIO.LoadPosedRig(daePath, new DsSkeleton(rm).Names);
            if (posed != null && posed.Vertices.Length == rig.Vertices.Length)
            {
                var pt = MakeHdTemplate(id, rh, rm, posed, hdBatches, all, sculpted, "POSED (your pose from Pose Studio)");
                _hdRigs.Add(pt);
                if (_animRig == null || _animRig == "posed") _hdTemplate = pt;
            }
        }
        catch (Exception ex) { Log.Warn("MPHRender", "animhd: posed rig failed to load: " + ex.Message); }
        // the rig as it was before the latest fix (<dae>.before.mphrig): only when asked for (animrig before)
        var before = _animRig == "before" ? TrophyRigIO.Load(TrophyRigIO.BeforePathFor(daePath), 0, anyKey: true) : null;
        if (before != null && before.Vertices.Length == rig.Vertices.Length)
            _hdRigs.Add(MakeHdTemplate(id, rh, rm, before, hdBatches, all, sculpted, "BEFORE (the previous rig, to compare)"));
    }

    // One rig's worth of trophy vertices (rig space, up to 4 bones + weights each) appended to the shared
    // skinned list. `share` = a template of the same trophy whose textures these batches reuse.
    HdTemplate MakeHdTemplate(string id, Hunter rh, Model rm, TrophyRig rig, List<GeometryBaker.Batch> hdBatches, List<float> all, HdTemplate? share, string label)
    {
        var t = new HdTemplate { Id = id, Hunter = rh, Model = rm, Skeleton = new DsSkeleton(rm), Rig = rig, Label = label };
        t.Pose = new SkeletonPose(t.Skeleton.Count); t.World = new OpenTK.Mathematics.Matrix4[t.Skeleton.Count];
        // feet on the floor where the game model's are (the hip height the sculpt gave can sink them in or lift them)
        try { TrophyFeet.Settle(rig, rm, t.Skeleton, msg => Log.Info("MPHRender", $"animhd {id} [{label}]:" + msg)); }
        catch (Exception ex) { Log.Warn("MPHRender", "animhd: feet settle failed: " + ex.Message); }
        var g = rig.TrophyToRig;
        int missing = 0, hidden = 0;
        for (int bi = 0; bi < hdBatches.Count; bi++)
        {
            var b = hdBatches[bi];
            int start = all.Count / GpuSkinFloats, kept = 0;
            for (int tri = 0; tri + 33 <= b.Verts.Count; tri += 33)
            {
                // triangles on a replaced part (landmark "replace") are not drawn: the game model's part is
                int v0 = rig.IndexOf(new System.Numerics.Vector3(b.Verts[tri], b.Verts[tri + 1], b.Verts[tri + 2]));
                int v1 = rig.IndexOf(new System.Numerics.Vector3(b.Verts[tri + 11], b.Verts[tri + 12], b.Verts[tri + 13]));
                int v2 = rig.IndexOf(new System.Numerics.Vector3(b.Verts[tri + 22], b.Verts[tri + 23], b.Verts[tri + 24]));
                if (v0 >= 0 && v1 >= 0 && v2 >= 0 && rig.HidesTriangle(v0, v1, v2)) { hidden++; continue; }
                for (int i = tri; i < tri + 33; i += 11)
                {
                    var pos = new System.Numerics.Vector3(b.Verts[i], b.Verts[i + 1], b.Verts[i + 2]);
                    int vi = rig.IndexOf(pos);
                    var rp = OpenTK.Mathematics.Vector3.TransformPosition(new OpenTK.Mathematics.Vector3(pos.X, pos.Y, pos.Z), g);
                    var rn = OpenTK.Mathematics.Vector3.TransformNormal(new OpenTK.Mathematics.Vector3(b.Verts[i + 3], b.Verts[i + 4], b.Verts[i + 5]), g);
                    if (rig.BindPos != null && vi >= 0)
                    {
                        // straightened rig: bound where the straightening put this vertex, its normal turned with it
                        rp = rig.BindPos[vi];
                        rn = OpenTK.Mathematics.Vector3.Transform(rn, rig.BindRot![vi]);
                    }
                    if (rn.LengthSquared > 1e-12f) rn.Normalize();
                    all.Add(rp.X); all.Add(rp.Y); all.Add(rp.Z); all.Add(rn.X); all.Add(rn.Y); all.Add(rn.Z);
                    if (_gxEval && b.Gx != null && b.GxAux != null) for (int k = 0; k < 5; k++) all.Add(b.GxAux[i / 11 * 5 + k]);
                    else for (int k = 6; k < 11; k++) all.Add(b.Verts[i + k]);
                    kept++;
                    if (vi < 0)
                    {
                        // not in the rig (should not happen: the rig is built from these same corners) -> pin to the root
                        missing++;
                        all.Add(0); all.Add(0); all.Add(0); all.Add(0); all.Add(0); all.Add(0); all.Add(0); all.Add(0);
                        continue;
                    }
                    for (int k = 0; k < 4; k++) all.Add(rig.Bones[vi * 4 + k]);
                    for (int k = 0; k < 4; k++) all.Add(rig.Weights[vi * 4 + k]);
                }
            }
            t.Batches.Add(share != null ? share.Batches[bi].At(start, kept) : ToDrawBatch(b, start, kept));
        }
        // the game model's own parts for replaced bones (e.g. Weavel's gun forearm), riding those bones: drawn
        // with the game's texture on the old hand-tuned path, like the DS hunters themselves
        int partTris = 0, part = 0;
        foreach (var (src, partCorners) in TrophyRigger.ReplacementParts(rm, rig))
        {
            int start = all.Count / GpuSkinFloats;
            foreach (var c in partCorners)
            {
                all.Add(c.Pos.X); all.Add(c.Pos.Y); all.Add(c.Pos.Z); all.Add(c.Normal.X); all.Add(c.Normal.Y); all.Add(c.Normal.Z);
                all.Add(c.U); all.Add(c.V); all.Add(c.R); all.Add(c.G); all.Add(c.B);
                all.Add(c.Node); all.Add(0); all.Add(0); all.Add(0);
                all.Add(1); all.Add(0); all.Add(0); all.Add(0);
            }
            int shared = hdBatches.Count + part++;
            int tex = share != null && shared < share.Batches.Count ? share.Batches[shared].Tex
                : src.Pixels != null ? UploadTexture(src.Pixels, src.W, src.H) : WhiteTexture();
            t.Batches.Add(new DrawBatch { Tex = tex, Start = start, Count = partCorners.Count, Texgen = src.Texgen });
            partTris += partCorners.Count / 3;
        }
        Log.Info("MPHRender", $"animhd '{id}' on the {rh} rig, {label}: batches={t.Batches.Count} verts={rig.Vertices.Length} fit {rig.ChamferStart:0.000}->{rig.ChamferFinal:0.000} " +
            $"(ref {rig.RefPose}) unmapped corners={missing} hidden tris={hidden} game-part tris={partTris} shader={(_gxEval ? "GX-TEV interpreter" : "old hand-tuned")}");
        return t;
    }

    // The suit's own morph ball (HdBall: hd/<suit>Ball, a static import) appended to the shared skinned list, every corner
    // on bone 0 at weight 1 -- the ball's whole placement (centre, size, heading, spin, lift off the floor) is that one
    // matrix -- plus a small lit core inside it that shows through the gap between the halves, as the games' glow does.
    void BuildBallTemplate(string suitId, List<float> all)
    {
        var info = MphRecomp.Assets.HdBall.For(System.IO.Path.Combine(_hdDir, "hd"), suitId);
        if (info == null) { Log.Info("MPHRender", $"animhd '{suitId}': no morph ball (hd/{MphRecomp.Assets.HdBall.IdFor(suitId)})"); return; }
        var batches = LoadHdModel(info.Id, setBounds: false, out _, out var dae);
        var mn = dae.Min; var mx = dae.Max;
        var ball = new BallTemplate
        {
            Id = info.Id, Center = new OpenTK.Mathematics.Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2),
            Diameter = Math.Max(mx.X - mn.X, Math.Max(mx.Y - mn.Y, mx.Z - mn.Z)),
        };
        foreach (var b in batches)
        {
            int start = all.Count / GpuSkinFloats, kept = 0;
            for (int i = 0; i + 11 <= b.Verts.Count; i += 11)
            {
                for (int k = 0; k < 6; k++) all.Add(b.Verts[i + k]);
                if (_gxEval && b.Gx != null && b.GxAux != null) for (int k = 0; k < 5; k++) all.Add(b.GxAux[i / 11 * 5 + k]);
                else for (int k = 6; k < 11; k++) all.Add(b.Verts[i + k]);
                all.Add(0); all.Add(0); all.Add(0); all.Add(0);
                all.Add(1); all.Add(0); all.Add(0); all.Add(0);
                kept++;
            }
            ball.Batches.Add(ToDrawBatch(b, start, kept));
        }
        if (info.Core is System.Numerics.Vector3 core && _gxEval) ball.Batches.Add(BallCoreBatch(ball, core, all));
        _ball = ball;
        Log.Info("MPHRender", $"animhd '{suitId}': morph ball {info.Id} ({ball.Batches.Count} batches, {ball.Diameter:0.0} across, core {(info.Core != null ? "lit" : "none")}) -- X to morph");
    }

    // The glow between the halves (HdBall.CoreMaterial / CoreCorners: one unlit GX stage on a sphere inside the shell)
    DrawBatch BallCoreBatch(BallTemplate ball, System.Numerics.Vector3 core, List<float> all)
    {
        var m = MphRecomp.Assets.HdBall.CoreMaterial(core);
        int start = all.Count / GpuSkinFloats, n = 0;
        foreach (var (p, d) in MphRecomp.Assets.HdBall.CoreCorners(new System.Numerics.Vector3(ball.Center.X, ball.Center.Y, ball.Center.Z), ball.Diameter))
        {
            all.Add(p.X); all.Add(p.Y); all.Add(p.Z); all.Add(d.X); all.Add(d.Y); all.Add(d.Z);
            all.Add(0); all.Add(0); all.Add(1); all.Add(1); all.Add(MphRecomp.Render.GxShader.PackBlueAlpha(1, 1));
            all.Add(0); all.Add(0); all.Add(0); all.Add(0); all.Add(1); all.Add(0); all.Add(0); all.Add(0);
            n++;
        }
        return new DrawBatch { Tex = WhiteTexture(), Start = start, Count = n, Gx = m, GxP = MphRecomp.Render.GxShader.Pack(m) };
    }

    // The ball's one matrix (HdBall.Placement): centred, sized to MPH's morph ball, spun, turned, resting on the floor
    void BallPalette() => HunterRig.ToArray(MphRecomp.Assets.HdBall.Placement(_ball!.Center, _ball.Diameter, _ballSpin, _ballHeading), _palette, 0);

    // One 60 Hz step of the roll: the heading eases toward the way the ball moved, the spin grows by distance / radius
    void RollBall(HunterDummy d)
    {
        var now = d.Position;
        if (_ballLast is OpenTK.Mathematics.Vector3 last)
        {
            var step = new OpenTK.Mathematics.Vector3(now.X - last.X, 0f, now.Z - last.Z);
            float dist = step.Length;
            if (dist > 1e-4f)
            {
                float want = (float)Math.Atan2(step.X, -step.Z);    // facing = (sin, 0, -cos)
                _ballHeading = LerpAngle(_ballHeading, want, 0.35f);
                _ballSpin = (_ballSpin + dist / (MphRecomp.Assets.HdBall.GameDiameter / 2f)) % (2f * (float)Math.PI);
            }
        }
        _ballLast = now;
    }

    // Retargeted pose for the trophy: the SAME two clip layers + aim the game picked for this hunter,
    // on the trophy's fitted bone lengths; skin matrix = inverse bind * pose.
    int HdPalette(HunterDummy d)
    {
        var t = _hdTemplate!;
        t.Skeleton.SampleBiped(t.Model, d.Anim, t.Pose);
        // a Space Pirate fires one-handed: while the torso layer plays a firing clip, its blade arm keeps what the legs
        // are doing (standing, walking) instead of reaching over to the gun as the hunter's clip has it
        if (TrophyRigs.OffHandStaysDown(t.Id) && TrophyRigs.IsFiringClip(d.Anim.Torso.AnimInfo.Index[0]))
            t.Skeleton.SampleFromLegs(t.Model, d.Anim, t.Pose, t.OffArm ??= t.Skeleton.Subtree("L_shoulder"));
        t.Skeleton.Fk(t.Pose, t.World, d.AimFacingY, t.Rig.Lengths, t.Rig.Offsets);
        for (int i = 0; i < t.Skeleton.Count; i++)
            HunterRig.ToArray(t.Rig.InvBind[i] * t.World[i], _palette, i * 16);
        return t.Skeleton.Count;
    }

    OpenTK.Mathematics.Vector3? _driverSpawnPos; float _driverSpawnYaw;
    bool _fireWasHeld;
    int _animDemo, _demoTick; float _worldMinY = -1e9f;
    int _fpsFrames; long _fpsSince; float _fps;

    // --es animdemo 1: a fixed, looping 12 s input script (adb can't drive the analog sticks), so every
    // biped behaviour can be captured and checked on-device: walk, strafe both ways, back-pedal, a
    // running jump, a standing jump, hard turns both ways, and a burst of shots while walking.
    static DummyInput DemoInput(int tick)
    {
        float t = (tick % 720) / 60f;
        var i = new DummyInput();
        if (t < 2.0f) i.MoveY = 1f;                                  // walk forward
        else if (t < 3.0f) i.MoveX = 1f;                             // strafe right
        else if (t < 4.0f) i.MoveX = -1f;                            // strafe left
        else if (t < 5.0f) i.MoveY = -1f;                            // back-pedal
        else if (t < 5.5f) { }                                       // settle
        else if (t < 6.5f) { i.MoveY = 1f; i.Jump = tick % 720 == 330; } // running jump at 5.5 s
        else if (t < 7.5f) { }                                       // land + settle
        else if (t < 7.7f) i.TurnX = 1f;                             // hard turn right
        else if (t < 8.2f) { }
        else if (t < 8.4f) i.TurnX = -1f;                            // hard turn left
        else if (t < 9.0f) { }
        else if (t < 10.5f) { i.MoveY = 1f; i.Fire = (tick % 720) is 540 or 570 or 600; } // shoot while walking
        else if (t < 11.0f) i.Jump = tick % 720 == 630;              // standing jump at 10.5 s
        return i;
    }

    // --es animdemo 3, or Y from drive mode: the combat clips the way the game plays them -- on the upper
    // body only, through the game's own biped logic -- while the legs stand, walk forward, then back-pedal.
    // A 10 s loop: standing, charge 1.5 s and release (Shoot, Charge, ChargeShoot), then three quick shots;
    // walking forward, the same charged shot; back-pedalling, three quick shots; then stand.
    const int CombatDemoTicks = 600;
    static DummyInput CombatDemoInput(int tick)
    {
        static bool Held(int k) => k is >= 30 and < 120 or >= 180 and < 186 or >= 204 and < 210 or >= 228 and < 234
            or >= 270 and < 360 or >= 420 and < 426 or >= 444 and < 450 or >= 468 and < 474;
        int k = tick % CombatDemoTicks;
        var i = new DummyInput { FireHeld = Held(k) };
        i.Fire = i.FireHeld && !Held(k - 1);
        if (k is >= 240 and < 390) i.MoveY = 1f;        // walk forward (4.0-6.5 s)
        else if (k is >= 390 and < 540) i.MoveY = -1f;  // back-pedal (6.5-9.0 s)
        return i;
    }

    // Advance every live hunter at the game's fixed 60 Hz; the driven one reads the controller.
    void UpdateAnimation(float dt, long now)
    {
        if (_driven != null) ApplyViewerRequests();
        _animAcc += dt;
        int ticks = 0;
        while (_animAcc >= TickSeconds && ticks < 6)
        {
            DummyInput drive = default;
            if (_driven != null && _animClip < 0 && _animDemo > 0)
            {
                // every loop restarts from the spawn so each pass (and each capture) is identical
                if ((_animDemo == 1 && _demoTick % 720 == 0 || _animDemo == 3 && _demoTick % CombatDemoTicks == 0) && _driverSpawnPos != null)
                {
                    _driven.Position = _driven.PrevPosition = _driverSpawnPos.Value;
                    _driven.Yaw = _driven.PrevYaw = _driverSpawnYaw;
                    _driven.Speed = OpenTK.Mathematics.Vector3.Zero;
                }
                drive = _animDemo == 3 ? CombatDemoInput(_demoTick++) : DemoInput(_demoTick++);
            }
            else if (_driven != null && _animClip < 0 && !FreeCam)   // in free camera the sticks fly the camera
            {
                bool fire = FireHeld;
                drive = new DummyInput
                {
                    MoveX = Lx, MoveY = -Ly, TurnX = Rx, AimY = -Ry,
                    Jump = System.Threading.Interlocked.Exchange(ref _pendingJump, 0) == 1,
                    Fire = fire && !_fireWasHeld, // Power Beam: one shot per press
                    FireHeld = fire,              // hold to charge, let go for the charged shot
                };
                _fireWasHeld = fire;
            }
            foreach (var d in _actors)
            {
                d.Tick(d == _driven ? drive : default);
                if (d.Feet.Y < _worldMinY - 3f)
                {
                    // left the playable space (should never happen): report exactly where, then respawn
                    Log.Warn("MPHRender", $"OUT OF WORLD: {d.Hunter} feet y={d.Feet.Y:0.00} < collision min {_worldMinY:0.00}; " +
                        $"last stood at ({d.LastStanding.X:0.00},{d.LastStanding.Y:0.00},{d.LastStanding.Z:0.00}), " +
                        $"no floor below from ({d.NoFloorSince?.X:0.00},{d.NoFloorSince?.Y:0.00},{d.NoFloorSince?.Z:0.00}) -- respawning");
                    d.Respawn();
                }
            }
            if (_morphed && _driven != null) RollBall(_driven);
            _animAcc -= TickSeconds;
            ticks++;
        }
        if (ticks == 6) _animAcc = 0f; // hitch: drop the backlog instead of spiralling
        if (_driven != null && StatusSink != null && now - _lastStatus > 150)
        {
            _lastStatus = now;
            var an = _driven.Anim;
            string mode = _animClip >= 0 ? $"CLIP {_animClip}: {(PlayerAnimation)_animClip} (whole body -- viewer only, not how the game plays it)"
                : _animDemo == 3 ? "AUTO COMBAT DEMO (game biped logic: charge/shoot on the upper body, legs stand/walk)   [Y = drive]"
                : _animDemo > 0 ? $"DEMO {_animDemo} (game biped logic)   [Y = drive]"
                : "DRIVE (game biped logic)   [Y = auto combat demo]";
            if (_hdTemplate != null && _driven.Hunter == _hdTemplate.Hunter)
                mode += $"\nHD trophy '{_hdTemplate.Id}' (auto-rig fit {_hdTemplate.Rig.ChamferFinal:0.000}) -- shader: {(_gxEval ? "GX-TEV interpreter" : "old hand-tuned")}" +
                    $"\nRIG {_hdRigs.IndexOf(_hdTemplate) + 1}/{_hdRigs.Count}: {_hdTemplate.Label}   " + (_hdRigs.Count > 1 ? "[L2 / Select = next rig]" : "(no other rigs prepared)") +
                    (_compare ? "\nSIDE BY SIDE (seen from behind): left = trophy as sculpted | middle = rigged | right = game model   [Start = off]" : "   [Start = side by side]");
            StatusSink($"{_driven.Hunter}  --  {mode}   {_fps:0} fps\n" +
                $"legs  {an.LegsAnim,-12} {an.LegsFrame,2}/{an.LegsFrameCount}\n" +
                $"torso {an.TorsoAnim,-12} {an.TorsoFrame,2}/{an.TorsoFrameCount}   aimY={_driven.AimFacingY:0.00}\n" +
                $"grounded={_driven.Grounded}  speed={Math.Sqrt(_driven.Speed.X * _driven.Speed.X + _driven.Speed.Z * _driven.Speed.Z) * 30:0.0} u/s  " +
                $"pos=({_driven.Position.X:0.00},{_driven.Position.Y:0.00},{_driven.Position.Z:0.00})\n" +
                $"sticks L=({Lx:0.00},{Ly:0.00}) R=({Rx:0.00},{Ry:0.00})\n" +
                "L-stick move  R-stick turn/aim  A jump  R2 tap = shoot, hold = charge, let go = charged shot\n" +
                "D-pad U/D clips  L/R hunter  Y drive / auto combat demo  " + (_ball != null ? (_morphed ? "X stand up" : "X morph ball") : "(no morph ball)") + "  L1/R1 orbit (both = reset)  B exit\n" +
                (FreeCam ? "FREE CAMERA: L-stick fly  R-stick look  L1/R1 down/up  drag = look  pinch = move  (L3+R3 together or the button = back)"
                         : $"ZOOM x{Zoom:0.0}: pinch or L3/R3 -- drag a finger to orbit / look up-down -- L3+R3 together = free camera -- L1+R1 resets"));
        }
    }

    void ApplyViewerRequests()
    {
        if (System.Threading.Interlocked.Exchange(ref _pendingCompare, 0) == 1) { _compare = !_compare; Log.Info("MPHRender", $"animview side-by-side -> {(_compare ? "on" : "off")}"); }
        if (System.Threading.Interlocked.Exchange(ref _pendingRigFlip, 0) == 1 && _hdTemplate != null && _hdRigs.Count > 1)
        {
            _hdTemplate = _hdRigs[(_hdRigs.IndexOf(_hdTemplate) + 1) % _hdRigs.Count];
            Log.Info("MPHRender", $"animhd rig -> {_hdRigs.IndexOf(_hdTemplate) + 1}/{_hdRigs.Count} {_hdTemplate.Label}");
        }
        int hs = System.Threading.Interlocked.Exchange(ref _pendingHunterStep, 0);
        if (hs != 0 && _driven != null)
        {
            int idx = Array.IndexOf(AnimHunters, _driven.Hunter);
            Hunter next = AnimHunters[((idx + hs) % AnimHunters.Length + AnimHunters.Length) % AnimHunters.Length];
            var d = new HunterDummy(next, _skinTemplates[next].Model, _driven.Position, _driven.Yaw) { Collision = _driven.Collision };
            d.SnapToFloor();
            if (_animClip >= 0) d.Anim.PlayManual((PlayerAnimation)_animClip);
            _actors[_actors.IndexOf(_driven)] = d;
            _driven = d;
            Log.Info("MPHRender", $"animview hunter -> {next}");
        }
        int cs = System.Threading.Interlocked.Exchange(ref _pendingClipStep, 0);
        if (cs != 0 && _driven != null)
        {
            int count = _driven.Anim.Model.AnimationGroups.Node.Count;
            _animClip = _animClip < 0 ? (cs > 0 ? Math.Min(cs - 1, count - 1) : Math.Max(0, count + cs)) : ((_animClip + cs) % count + count) % count;
            _driven.Speed = OpenTK.Mathematics.Vector3.Zero;
            _driven.Anim.PlayManual((PlayerAnimation)_animClip);
            if (HoldFrame >= 0)
            {
                int f = Math.Min(HoldFrame, _driven.Anim.LegsFrameCount - 1);
                _driven.Anim.Legs.AnimInfo.Frame[0] = f; _driven.Anim.Torso.AnimInfo.Frame[0] = f;
                _driven.Anim.Legs.AnimInfo.Flags[0] |= AnimFlags.Paused; _driven.Anim.Torso.AnimInfo.Flags[0] |= AnimFlags.Paused;
            }
            Log.Info("MPHRender", $"animview clip -> {_animClip} {(PlayerAnimation)_animClip} ({_driven.Anim.LegsFrameCount} frames){(HoldFrame >= 0 ? $" held at frame {HoldFrame}" : "")}");
        }
        // Y: clip mode or a demo -> drive mode; drive mode -> the auto combat demo
        if (System.Threading.Interlocked.Exchange(ref _pendingDrive, 0) == 1 && _driven != null)
        {
            bool fromDrive = _animClip < 0 && _animDemo == 0;
            _animClip = -1;
            _driven.Anim.Manual = false;
            _driven.Anim.SetBoth(PlayerAnimation.Idle, AnimFlags.None);
            _animDemo = fromDrive ? 3 : 0;
            _demoTick = 0;
            Log.Info("MPHRender", fromDrive ? "animview -> auto combat demo" : "animview -> drive mode");
        }
        if (System.Threading.Interlocked.Exchange(ref _pendingMorph, 0) == 1 && _driven != null)
        {
            // only the hunter wearing the HD suit that has a ball morphs (the game's own models keep walking)
            bool can = _ball != null && _hdTemplate != null && _driven.Hunter == _hdTemplate.Hunter;
            _morphed = can && !_morphed;
            _ballLast = _driven.Position; _ballHeading = _driven.Yaw;
            Log.Info("MPHRender", can ? $"animview morph ball -> {(_morphed ? "on" : "off")}" : "animview morph ball: this suit has none");
        }
        if (System.Threading.Interlocked.Exchange(ref _pendingReset, 0) == 1 && _driven != null && _driverSpawnPos != null)
        {
            _driven.Position = _driven.PrevPosition = _driverSpawnPos.Value;
            _driven.Yaw = _driven.PrevYaw = _driverSpawnYaw;
            _driven.Speed = OpenTK.Mathematics.Vector3.Zero;
            _orbit = 0f; Zoom = 1f; ViewLift = 0f; FreeCam = false;
        }
    }

    // Pose + draw every live hunter's batches for one blend pass (0 opaque, 1 translucent, 2 additive)
    // through the normal dispatch (old shader or GX interpreter per batch). The skin state rides on the
    // batch (SetSkin uploads it to whichever program draws it). The viewer's hunter wears the rigged
    // Brawl trophy when --es animhd names one for its skeleton.
    void DrawSkinnedActors(Action<DrawBatch> draw, int pass)
    {
        GLES30.GlBindVertexArray(_skinVao);
        float a = Math.Clamp(_animAcc / TickSeconds, 0f, 1f); // smooth placement between 60 Hz ticks
        foreach (var d in _actors)
        {
            if (_morphed && _ball != null && d == _driven)
            {
                // the morph ball instead of the suit: resting on the floor under the hunter, one matrix for all of it
                bool anyBall = false;
                foreach (var b in _ball.Batches) if (PassOf(b) == pass) { anyBall = true; break; }
                if (!anyBall) continue;
                BallPalette();
                var bp = OpenTK.Mathematics.Vector3.Lerp(d.PrevPosition, d.Position, a) - (d.Position - d.Feet);
                HunterRig.ToArray(OpenTK.Mathematics.Matrix4.CreateTranslation(bp), _modelMtx);
                foreach (var b in _ball.Batches)
                {
                    if (PassOf(b) != pass) continue;
                    b.SkinPalette = _palette; b.SkinCount = 1; b.SkinModel = _modelMtx;
                    draw(b);
                    b.SkinPalette = null;
                }
                continue;
            }
            bool hd = _hdTemplate != null && d == _driven && d.Hunter == _hdTemplate.Hunter;
            SkinnedTemplate t = _skinTemplates[d.Hunter];
            var batches = hd ? _hdTemplate!.Batches : t.Batches;
            bool any = false;
            foreach (var b in batches) if (PassOf(b) == pass) { any = true; break; }
            if (!any) continue;
            int count;
            if (hd) count = HdPalette(d);
            else { HunterRig.Pose(d.Anim, d.AimFacingY, _palette); count = t.Model.NodeMatrixIds.Count; }
            var pos = OpenTK.Mathematics.Vector3.Lerp(d.PrevPosition, d.Position, a);
            float yaw = LerpAngle(d.PrevYaw, d.Yaw, a);
            var facing = new OpenTK.Mathematics.Vector3((float)Math.Sin(yaw), 0, -(float)Math.Cos(yaw));
            HunterRig.ToArray(HunterRig.ModelMatrix(t.Model, HunterRig.Placement(d.Hunter, pos, facing)), _modelMtx);
            foreach (var b in batches)
            {
                if (PassOf(b) != pass) continue;
                b.SkinPalette = _palette; b.SkinCount = count; b.SkinModel = _modelMtx;
                draw(b);
                b.SkinPalette = null;
            }
        }
        if (_compare && _driven != null && _hdTemplate != null && _driven.Hunter == _hdTemplate.Hunter) DrawCompareCompanions(draw, pass, a);
        GLES30.GlBindVertexArray(_vao);
    }

    // SIDE-BY-SIDE (on by default with a trophy; Start toggles): beside the driven, rigged trophy -- on its left
    // (seen from behind) the trophy exactly as sculpted, on its right the game's own model on its own rig playing
    // the same clip at the same frame. Same facing, same floor.
    bool _compare;
    const float CompareGap = 1.25f;   // x the hunter's scale
    public void ToggleCompare() => System.Threading.Interlocked.Exchange(ref _pendingCompare, 1);
    public void SetCompare(bool on) => _compare = on;
    int _pendingCompare;
    void DrawCompareCompanions(Action<DrawBatch> draw, int pass, float a)
    {
        var d = _driven!;
        SkinnedTemplate ds = _skinTemplates[d.Hunter];
        var pos = OpenTK.Mathematics.Vector3.Lerp(d.PrevPosition, d.Position, a);
        float yaw = LerpAngle(d.PrevYaw, d.Yaw, a);
        var facing = new OpenTK.Mathematics.Vector3((float)Math.Sin(yaw), 0, -(float)Math.Cos(yaw));
        var right = new OpenTK.Mathematics.Vector3((float)Math.Cos(yaw), 0, (float)Math.Sin(yaw));
        float gap = CompareGap * Metadata.HunterScales[d.Hunter];
        void Draw(List<DrawBatch> batches, int count, OpenTK.Mathematics.Vector3 at)
        {
            HunterRig.ToArray(HunterRig.ModelMatrix(ds.Model, HunterRig.Placement(d.Hunter, at, facing)), _modelMtx);
            foreach (var b in batches)
            {
                if (PassOf(b) != pass) continue;
                b.SkinPalette = _palette; b.SkinCount = count; b.SkinModel = _modelMtx;
                draw(b);
                b.SkinPalette = null;
            }
        }
        // the sculpt: the sculpt-bound rig at rest -- every skin matrix identity
        var sculpt = _hdSculpt ?? _hdRigs[0];
        for (int i = 0; i < sculpt.Skeleton.Count; i++) HunterRig.ToArray(OpenTK.Mathematics.Matrix4.Identity, _palette, i * 16);
        Draw(sculpt.Batches, sculpt.Skeleton.Count, pos - right * gap);
        // the game's model, posed by the same animation state as the driven hunter
        HunterRig.Pose(d.Anim, d.AimFacingY, _palette);
        Draw(ds.Batches, ds.Model.NodeMatrixIds.Count, pos + right * gap);
    }
    static int PassOf(DrawBatch b) => b.Additive ? 2 : b.Translucent ? 1 : 0;
    bool AnySkinned(int pass) => _hdTemplate != null && _driven != null && _driven.Hunter == _hdTemplate.Hunter
        && (_morphed && _ball != null ? _ball.Batches : _hdTemplate.Batches).Exists(b => PassOf(b) == pass);

    // First PlayerSpawn entity's position + facing (yaw). These are the game's authored,
    // guaranteed-in-bounds spawn points -- used to place the walk-mode camera on start.
    static bool FindPlayerSpawn(string room, out OpenTK.Mathematics.Vector3 pos, out float yaw)
    {
        pos = OpenTK.Mathematics.Vector3.Zero; yaw = 0f;
        RoomMetadata meta = Metadata.RoomMetadata[room];
        if (meta.EntityPath == null) return false;
        IReadOnlyList<MphRead.Entity> entities;
        try { entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt, allowHook: true); }
        catch { return false; }
        foreach (MphRead.Entity e in entities)
        {
            if (e.Type != EntityType.PlayerSpawn) continue;
            pos = e.Position;
            var f = e.FacingVector;
            if (Math.Abs(f.X) > 1e-4f || Math.Abs(f.Z) > 1e-4f) yaw = (float)Math.Atan2(f.X, -f.Z);
            return true;
        }
        return false;
    }

    // Same as FindPlayerSpawn but picks the spawn CLOSEST (XZ) to a target point, instead of just the
    // first one in the entity list -- for placing something "in the middle" of a room whose spawns are
    // scattered around a ring, not clustered at one point.
    static bool FindCentralPlayerSpawn(string room, float[] center, out OpenTK.Mathematics.Vector3 pos, out float yaw)
    {
        pos = OpenTK.Mathematics.Vector3.Zero; yaw = 0f;
        RoomMetadata meta = Metadata.RoomMetadata[room];
        if (meta.EntityPath == null) return false;
        IReadOnlyList<MphRead.Entity> entities;
        try { entities = Read.GetEntities(meta.EntityPath, layerId: -1, meta.FirstHunt, allowHook: true); }
        catch { return false; }
        bool found = false; float bestD = float.MaxValue;
        foreach (MphRead.Entity e in entities)
        {
            if (e.Type != EntityType.PlayerSpawn) continue;
            float dx = e.Position.X - center[0], dz = e.Position.Z - center[2];
            float d = dx * dx + dz * dz;
            if (d < bestD)
            {
                bestD = d; found = true; pos = e.Position;
                var f = e.FacingVector;
                yaw = (Math.Abs(f.X) > 1e-4f || Math.Abs(f.Z) > 1e-4f) ? (float)Math.Atan2(f.X, -f.Z) : 0f;
            }
        }
        return found;
    }

    // Fan-triangulate the room's collision mesh into world-space triangles (9 floats/tri)
    // for the floor/wall raycasts, and report its playable centre + radius. The collision
    // points are already in the same world units as the baked render geometry, so no scale
    // is applied. Skyboxes have no collision, so this is exactly the reachable space.
    // The game's node/collision layer mask for a room: for multiplayer arenas the standard
    // battle layout (2 players); for single-player rooms the room's own authored layer. This
    // is what selects the open MP arena over the campaign walls (see FilterNodes at load).
    static int RoomLayerMask(RoomMetadata meta) => MphRecomp.World.RoomCollision.RoomLayerMask(meta);

    // Load a user-supplied HD trophy (Brawl model the user exported to a .dae and dropped in the
    // app's external files dir) into render batches via our own DaeModel reader. Each part gets its
    // real flat colour (recovered from the .brres GX shader registers) and is vertex-lit. Nothing is
    // bundled -- the .dae is the user's own asset, read at runtime. Sets _center/_radius for framing.
    List<GeometryBaker.Batch> LoadHdModel() => LoadHdModel(_hd!, setBounds: true, out _, out _);

    // id = the trophy's hd/<id>/ folder. setBounds=false leaves the scene centre/radius alone (a trophy
    // loaded as a live hunter inside a room). daePath/dae = the parsed source, for rigging.
    List<GeometryBaker.Batch> LoadHdModel(string id, bool setBounds, out string daePath, out MphRecomp.Assets.DaeModel daeModel)
    {
        // ONE FOLDER PER MODEL: hd/<id>/ holds this model's .dae + .brres + all its textures, isolated so
        // generic texture names (Samus "Spe", the suits' shared "gun_reflected"/"polySurface521") can't
        // collide across models. The .dae/.brres are found by glob inside the folder (exact filename
        // irrelevant). Falls back to the old flat hd/<id>.dae layout if no per-model folder exists.
        string modelDir = System.IO.Path.Combine(_hdDir, "hd", id);
        bool foldered = System.IO.Directory.Exists(modelDir);
        string texDir = foldered ? modelDir : System.IO.Path.Combine(_hdDir, "hd");
        string[] daes = foldered ? System.IO.Directory.GetFiles(modelDir, "*.dae") : System.Array.Empty<string>();
        string dae = daes.Length > 0 ? daes[0] : System.IO.Path.Combine(texDir, id + ".dae");
        var m = MphRecomp.Assets.DaeModel.Load(dae);
        daePath = dae; daeModel = m;
        // TRUE per-part colours read from the model's own .brres (the DAE drops them). Matched to the
        // DAE parts by material name. Register colour when the part has one, else the light-channel
        // material colour (where the yellow ports live). Falls back to HdColor if no .brres present.
        // Per material: its true base colour AND whether it is LIT. Unlit materials (Kanden's ports)
        // render flat -- no metallic reflection -- which is the per-material difference the model stores.
        // The lit materials render DUOCHROME: the model's TEV program blends between TWO register
        // colours (C0 and C1) by the lighting, then multiplies by the reflection map. So capture BOTH
        // registers per material (colA=C0, colB=C1). Unlit materials (ports) render flat from base.
        var trueColors = new Dictionary<string, System.Numerics.Vector3>();
        var colB = new Dictionary<string, System.Numerics.Vector3>();
        var unlit = new Dictionary<string, bool>();
        var swap = new Dictionary<string, bool>();
        var lerpScale = new Dictionary<string, float>();
        var reflScale = new Dictionary<string, float>();
        var ambient = new Dictionary<string, System.Numerics.Vector3>();
        var unlitScaleD = new Dictionary<string, float>();
        var additiveD = new Dictionary<string, bool>();   // energy blade: additive blend + no z-write
        var cutoutD = new Dictionary<string, bool>();      // hair: hard alpha cutout from the _o mask
        var translucentD = new Dictionary<string, bool>(); // energy crystal / glass: alpha-blend see-through
        string[] brs = foldered ? System.IO.Directory.GetFiles(modelDir, "*.brres") : System.Array.Empty<string>();
        string brres = brs.Length > 0 ? brs[0] : System.IO.Path.Combine(texDir, id + ".brres");
        try
        {
            if (System.IO.File.Exists(brres))
                foreach (var mm in MphRecomp.Assets.Mdl0Colors.Read(brres))
                {
                    // colA = BaseColor (== C0 for lit parts; the yellow material colour for unlit ports).
                    trueColors[mm.Name] = mm.BaseColor;
                    // colB = C1, the second register the duochrome blend needs (only used by lit parts).
                    colB[mm.Name] = mm.Register[1] ?? mm.BaseColor;
                    unlit[mm.Name] = !mm.Lit;
                    // Lerp direction + per-stage scales read from THIS material's TEV (all per-model:
                    // Kanden's armour swaps at 2/2, Sylux's don't at 1/4 & 1/2).
                    swap[mm.Name] = mm.CombineSwap;
                    lerpScale[mm.Name] = mm.LerpScale;
                    reflScale[mm.Name] = mm.ReflScale;
                    // The light-channel AMBIENT colour tints the lighting (RASC). White for Kanden
                    // (no tint), OLIVE (102,102,0) for Weavel's body -- which is what makes it green.
                    ambient[mm.Name] = mm.AmbientColor ?? System.Numerics.Vector3.One;
                    unlitScaleD[mm.Name] = mm.UnlitScale;   // flat unlit output scale (blade = 4)
                    // Transparency, read from the material's pixel-engine mode (not a name guess):
                    // additive = the energy blade (glows over the scene); cutout = the hair opacity mask.
                    additiveD[mm.Name] = mm.Additive;
                    cutoutD[mm.Name] = mm.AlphaCutout;
                    translucentD[mm.Name] = mm.Translucent;
                }
        }
        catch (Exception ex) { Log.Warn("MPHRender", "brres colours failed: " + ex.Message); }
        // PHASE 2 (GX-TEV interpreter): parse each material's FULL GX program from the same .brres. The
        // renderer runs these stages verbatim (no hand-tuned paths) when --es gxeval 1. Keyed by material
        // name to match the DAE parts. Only read under the flag so the default path is byte-identical.
        var gxMats = new Dictionary<string, MphRecomp.Assets.GxMaterial>();
        var texFormats = new Dictionary<string, int>();
        if (_gxEval)
        {
            try
            {
                if (System.IO.File.Exists(brres))
                {
                    foreach (var gm in MphRecomp.Assets.Mdl0Gx.Read(brres))
                        gxMats.TryAdd(gm.Name, gm);
                    texFormats = MphRecomp.Assets.Mdl0Gx.TextureFormats(brres);
                }
                // a model with no .brres (a Retro suit from the owner's Prime disc) brings its GX programs as <dae>.gx.json
                else if (System.IO.File.Exists(MphRecomp.Assets.GxJson.PathFor(dae)))
                {
                    foreach (var gm in MphRecomp.Assets.GxJson.Read(MphRecomp.Assets.GxJson.PathFor(dae)))
                        gxMats.TryAdd(gm.Name, gm);
                    brres = MphRecomp.Assets.GxJson.PathFor(dae);
                }
                Log.Info("MPHRender", $"gxeval: parsed {gxMats.Count} GX materials from {System.IO.Path.GetFileName(brres)}");
            }
            catch (Exception ex) { Log.Warn("MPHRender", "gxeval parse failed: " + ex.Message); }
        }
        // Load a PNG from the HD folder -> ABGR int pixels (GL order). Shared by the reflection map and
        // the per-material diffuse textures (Weavel). Cached so a texture shared by parts loads once.
        var texCache = new Dictionary<string, (int[] px, int w, int h)?>();
        (int[] px, int w, int h)? LoadPng(string? file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (texCache.TryGetValue(file!, out var cached)) return cached;
            (int[] px, int w, int h)? result = null;
            try
            {
                var bmp = Android.Graphics.BitmapFactory.DecodeFile(System.IO.Path.Combine(texDir, file));
                if (bmp != null)
                {
                    int w = bmp.Width, h = bmp.Height; var px = new int[w * h];
                    bmp.GetPixels(px, 0, w, 0, 0, w, h);
                    for (int i = 0; i < px.Length; i++) { int a = px[i]; px[i] = (int)((a & 0xFF00FF00u) | ((uint)(a >> 16) & 0xFF) | ((uint)(a & 0xFF) << 16)); } // ARGB->ABGR
                    bmp.Recycle();
                    result = (px, w, h);
                }
            }
            catch (Exception ex) { Log.Warn("MPHRender", $"texture '{file}' load failed: " + ex.Message); }
            texCache[file!] = result;
            return result;
        }

        // Metallic trophies (Kanden/Sylux) share one reflection/env sphere-map (applied via texgen).
        var reflTex = LoadPng(m.ReflectionFile);
        int[]? refl = reflTex?.px; int rw = reflTex?.w ?? 0, rh = reflTex?.h ?? 0;

        var batches = new List<GeometryBaker.Batch>();
        int litIdx = 0;
        foreach (var part in m.Meshes)
        {
            // A material whose DAE references a real diffuse texture (not the reflection map) is TEXTURED
            // (Weavel's body/hair): colour comes from that texture sampled by the model's own UVs. The
            // registers are black precisely because the colour lives in the texture, not the combine.
            bool textured = part.TextureFile != null && part.TextureFile != m.ReflectionFile;
            // colA = the material colour: for TEXTURED parts (matSrc=vertex) it's the DAE flat vertex
            // colour (Weavel's body is 0.5 grey -> RASC = 0.5*ambient, so the base is subtle and the
            // metallic reflection dominates). For the duochrome metal it's the C0 register.
            System.Numerics.Vector3 col = textured
                ? new System.Numerics.Vector3(part.FlatColor.X, part.FlatColor.Y, part.FlatColor.Z)
                : (trueColors.TryGetValue(part.Material, out var bc) ? bc : HdColor(part.Material));
            System.Numerics.Vector3 cB = colB.TryGetValue(part.Material, out var b2) ? b2 : col;                            // colB = C1
            bool isUnlit = unlit.TryGetValue(part.Material, out var u) && u;
            var diff = textured ? LoadPng(part.TextureFile) : null;
            if (textured && diff == null) textured = false;   // texture missing -> flat fallback
            // The body's REAL TEV combine (decoded from the .brres, which the DAE drops):
            //   final = 2*diffuse*lighting + incand + reflection*spec
            // So carry the incand (additive glow) + spec (masks the reflection) maps separately; the
            // reflection is the shared env sphere-map. Materials that only have a diffuse (hair/c02)
            // render 2*diffuse*lighting; the register-metal path (Kanden/Sylux) is untouched.
            var incand = textured ? LoadPng(part.IncandFile) : null;
            var spec = textured ? LoadPng(part.SpecFile) : null;
            // Body combine (diffuse + spec-masked reflection + optional incand glow). Weavel has all
            // three maps; Samus has spec + reflection but no incand -- so incand is optional (falls back
            // to black, contributing nothing) and spec+reflection are what matter for her glossy suit.
            bool multiTex = textured && spec != null && refl != null;
            int matKind = isUnlit ? -1 : (litIdx++ == 0 ? 0 : 1);
            bool matSwap = swap.TryGetValue(part.Material, out var sw) && sw;
            float lS = lerpScale.TryGetValue(part.Material, out var ls) ? ls : 2f;
            float rS = reflScale.TryGetValue(part.Material, out var rs) ? rs : 2f;
            bool metal = refl != null && !isUnlit && !textured;   // duochrome metal (Kanden/Sylux lit)
            var amb = ambient.TryGetValue(part.Material, out var av) ? new[] { av.X, av.Y, av.Z } : new[] { 1f, 1f, 1f };
            float uS = unlitScaleD.TryGetValue(part.Material, out var us2) ? us2 : 1f;
            // Transparency (from the material's PE mode): additive glow blade, hair alpha cutout.
            bool additive = additiveD.TryGetValue(part.Material, out var ad) && ad;
            bool translucent = translucentD.TryGetValue(part.Material, out var tl) && tl;
            bool cutout = textured && cutoutD.TryGetValue(part.Material, out var co) && co && part.OpacityFile != null;
            var opac = cutout ? LoadPng(part.OpacityFile) : null;
            if (cutout && opac == null) cutout = false;   // no mask -> fall back to opaque
            var batch = new GeometryBaker.Batch
            {
                Textured = textured, MultiTex = multiTex, Texgen = metal && !translucent, Metallic = metal && !translucent, Unlit = isUnlit,
                MatKind = matKind, CombineSwap = matSwap, LerpScale = lS, ReflScale = rS, UnlitScale = uS, Ambient = amb, ColB = new[] { cB.X, cB.Y, cB.Z },
                Additive = additive, Translucent = translucent, AlphaCutout = cutout,
                Gx = gxMats.TryGetValue(part.Material, out var gmv) ? gmv : null,
                Pixels = textured ? diff!.Value.px : (isUnlit ? null : refl),
                W = textured ? diff!.Value.w : (isUnlit ? 0 : rw),
                H = textured ? diff!.Value.h : (isUnlit ? 0 : rh),
            };
            // Interpreter: load this material's REAL texmaps (TexName -> <TexName>.png) so the GX program
            // samples exactly what the TEV texorder references -- not DAE role-guessing. Reflection/env maps
            // (name contains "ref") get normal-env texgen; everything else samples by the model's UVs.
            if (gmv != null)
            {
                var gpx = new int[8][]; var gw = new int[8]; var gh = new int[8]; var gtg = new int[8];
                for (int t = 0; t < 8; t++)
                {
                    var tn = gmv.TexNames[t];
                    if (tn == null) continue;
                    var tex = LoadPng(tn + ".png");
                    if (tex != null)
                    {
                        // GX I4/I8 textures are their intensity in all four channels; the PNG kept the grey but not the
                        // alpha. Decided by the texture's real format from the .brres (a copy: the PNG is shared)
                        var px = tex.Value.px;
                        if (texFormats.TryGetValue(tn, out int fmt) && MphRecomp.Assets.Mdl0Gx.IsIntensity(fmt))
                        {
                            px = (int[])px.Clone();
                            for (int i = 0; i < px.Length; i++) px[i] = (px[i] & 0x00FFFFFF) | ((px[i] & 0xFF) << 24);
                        }
                        gpx[t] = px; gw[t] = tex.Value.w; gh[t] = tex.Value.h;
                    }
                    // texgen source is DATA-DRIVEN: find the stage that samples this texmap and take that
                    // stage's texcoord source (UV vs normal-env), read from the model's XF TEXMTXINFO.
                    // NOT guessed from the name -- a *reflectivity* MASK contains "ref" but is UV-mapped,
                    // and the old name-guess wrongly env-mapped it (the swirly-shoulder bug).
                    int src = 0;
                    foreach (var stg in gmv.Stages)
                        if (stg != null && stg.TexMap == t) { src = gmv.TexCoordSrc[stg.TexCoord & 7]; break; }
                    gtg[t] = src;
                }
                batch.GxPixels = gpx; batch.GxW = gw; batch.GxH = gh; batch.GxTexGen = gtg;
                // the GX program's vertex data: the mesh's own UVs and vertex colours (alpha packed with blue)
                var aux = new float[part.Positions.Count * 5];
                for (int i = 0; i < part.Positions.Count; i++)
                {
                    var uv = part.Uvs[i]; var vc = i < part.Colors.Count ? part.Colors[i] : part.FlatColor;
                    aux[i * 5] = uv.X; aux[i * 5 + 1] = uv.Y; aux[i * 5 + 2] = vc.X; aux[i * 5 + 3] = vc.Y;
                    aux[i * 5 + 4] = MphRecomp.Render.GxShader.PackBlueAlpha(vc.Z, vc.W);
                }
                batch.GxAux = aux;
            }
            // DEMO jiggle: flag dangly hair parts + capture their vertical extent (scalp = top, tip = bottom).
            string ml = part.Material.ToLowerInvariant();
            if (ml.Contains("hair") || ml.Contains("pony") || ml.Contains("tail"))
            {
                float miny = 1e9f, maxy = -1e9f;
                foreach (var pp in part.Positions) { if (pp.Y < miny) miny = pp.Y; if (pp.Y > maxy) maxy = pp.Y; }
                batch.Jiggle = true; batch.JigTop = maxy; batch.JigBot = miny;
                Log.Info("MPHRender", $"jiggle part '{part.Material}' y=[{miny:0.0}..{maxy:0.0}]");
            }
            if (multiTex)
            {
                // incand is optional -> a 1x1 black texel when the model has no incandescence map (Samus)
                batch.IncandPixels = incand?.px ?? new[] { 0 }; batch.IncandW = incand?.w ?? 1; batch.IncandH = incand?.h ?? 1;
                batch.SpecPixels = spec!.Value.px; batch.SpecW = spec.Value.w; batch.SpecH = spec.Value.h;
                batch.ReflPixels = refl; batch.ReflW = rw; batch.ReflH = rh;
            }
            if (cutout) { batch.OpacityPixels = opac!.Value.px; batch.OpacityW = opac.Value.w; batch.OpacityH = opac.Value.h; }
            for (int i = 0; i < part.Positions.Count; i++)
            {
                var p = part.Positions[i]; var n = part.Normals[i]; var uv = part.Uvs[i];
                batch.Verts.Add(p.X); batch.Verts.Add(p.Y); batch.Verts.Add(p.Z);
                batch.Verts.Add(n.X); batch.Verts.Add(n.Y); batch.Verts.Add(n.Z);
                if (textured) { batch.Verts.Add(uv.X); batch.Verts.Add(uv.Y); }   // real UVs for diffuse sampling
                else { batch.Verts.Add(0f); batch.Verts.Add(0f); }
                batch.Verts.Add(col.X); batch.Verts.Add(col.Y); batch.Verts.Add(col.Z);
            }
            batches.Add(batch);
        }
        var mid = (m.Min + m.Max) * 0.5f; var size = m.Max - m.Min;
        if (setBounds)
        {
            _center[0] = mid.X; _center[1] = mid.Y; _center[2] = mid.Z;
            _radius = size.Length() * 0.5f; if (_radius < 1f) _radius = 1f;
        }
        Log.Info("MPHRender", $"hd '{id}' parts={m.Meshes.Count} tris={m.TriangleCount} " +
            $"size=({size.X:0.0},{size.Y:0.0},{size.Z:0.0}) DUOCHROME lerp={Lerp:0.00} exposure={Exposure:0.00} swap={Swap} (X/Y=blend, Dpad up/down=exposure, L3=swap)");
        return batches;
    }

    // Per-part BRAWL tint, applied UNDER the metallic reflection map to match the trophy look:
    // gold Enoema muscle, gunmetal armour, bright-yellow ring ports. Matched to the owner's Brawl
    // reference (the flat register colours in the file are DS-palette carryovers, not this look).
    // Keyed by DAE material name; per-hunter for now.
    static System.Numerics.Vector3 HdColor(string mat)
    {
        mat = mat.ToLowerInvariant();
        if (mat.Contains("green")) return new(1.00f, 0.82f, 0.42f); // Enoema muscle -> gold
        if (mat.Contains("vioret")) return new(0.30f, 0.31f, 0.36f); // armour -> gunmetal
        if (mat.Contains("yellow")) return new(1.00f, 0.88f, 0.15f); // ring ports -> yellow
        return new(0.7f, 0.7f, 0.7f);
    }

    // GX intensity textures (I4/I8) fill R, G, B AND alpha with the one intensity value. A PNG export stores
    // the intensity in RGB and leaves alpha opaque (255), dropping the alpha meaning. If a texture is grayscale
    // (R==G==B within tolerance) with fully-opaque alpha -- the intensity signature -- restore alpha=luminance,
    // so a material's TEV alpha (TEXA) reads the real mask (e.g. the hair opacity cutout). Colour textures and
    // textures that already carry a real alpha channel are left untouched.
    static void ReconstructIntensityAlpha(int[] px)   // px are ABGR ints (A<<24 | B<<16 | G<<8 | R)
    {
        for (int i = 0; i < px.Length; i++)
        {
            int p = px[i];
            if (((p >> 24) & 0xFF) != 255) return;                    // has a real alpha channel -> leave it
            int r = p & 0xFF, g = (p >> 8) & 0xFF, b = (p >> 16) & 0xFF;
            if (Math.Abs(r - g) > 8 || Math.Abs(g - b) > 8) return;   // colour, not grayscale intensity
        }
        for (int i = 0; i < px.Length; i++)
        {
            int p = px[i], lum = ((p & 0xFF) + ((p >> 8) & 0xFF) + ((p >> 16) & 0xFF)) / 3;
            px[i] = (p & 0x00FFFFFF) | (lum << 24);                   // alpha = luminance
        }
    }

    // Deep-copy a baked batch with its OWN Verts list (so the copy can be re-placed independently); the
    // read-only pixel arrays are shared. keepGx=false clears the parsed GX material so the copy renders
    // through the OLD hand-tuned shader -- used by --es gxcompare to stand old vs interpreter side by side.
    static GeometryBaker.Batch CloneBatch(GeometryBaker.Batch s, bool keepGx)
    {
        var c = new GeometryBaker.Batch
        {
            Pixels = s.Pixels, W = s.W, H = s.H, Texgen = s.Texgen, Metallic = s.Metallic, Unlit = s.Unlit,
            Textured = s.Textured, MultiTex = s.MultiTex, MatKind = s.MatKind, CombineSwap = s.CombineSwap,
            LerpScale = s.LerpScale, ReflScale = s.ReflScale, UnlitScale = s.UnlitScale, ColB = s.ColB, Ambient = s.Ambient,
            IncandPixels = s.IncandPixels, IncandW = s.IncandW, IncandH = s.IncandH,
            SpecPixels = s.SpecPixels, SpecW = s.SpecW, SpecH = s.SpecH,
            ReflPixels = s.ReflPixels, ReflW = s.ReflW, ReflH = s.ReflH,
            AlphaCutout = s.AlphaCutout, OpacityPixels = s.OpacityPixels, OpacityW = s.OpacityW, OpacityH = s.OpacityH,
            Additive = s.Additive, Translucent = s.Translucent,
            Gx = keepGx ? s.Gx : null, GxPixels = keepGx ? s.GxPixels : null,
            GxW = keepGx ? s.GxW : null, GxH = keepGx ? s.GxH : null, GxTexGen = keepGx ? s.GxTexGen : null, GxAux = keepGx ? s.GxAux : null,
        };
        c.Verts.AddRange(s.Verts);
        return c;
    }

    static float[] LoadCollisionTris(string room, float[] centerOut, out float radius)
        => MphRecomp.World.RoomCollision.LoadTriangles(room, centerOut, out radius);

    // Highest triangle surface directly under (ox,oz) at or below oy (a straight-down
    // ray via XZ barycentric test). Returns -1e9 if nothing is below.
    float FloorBelow(float ox, float oy, float oz)
    {
        float best = -1e9f;
        float[] t = _tri;
        for (int b = 0; b + 8 < t.Length; b += 9)
        {
            float ax = t[b], ay = t[b + 1], az = t[b + 2];
            float bx = t[b + 3], by = t[b + 4], bz = t[b + 5];
            float cx = t[b + 6], cy = t[b + 7], cz = t[b + 8];
            float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (d > -1e-6f && d < 1e-6f) continue;
            float w1 = ((bz - cz) * (ox - cx) + (cx - bx) * (oz - cz)) / d;
            float w2 = ((cz - az) * (ox - cx) + (ax - cx) * (oz - cz)) / d;
            float w3 = 1f - w1 - w2;
            if (w1 < -0.02f || w2 < -0.02f || w3 < -0.02f) continue;
            float y = w1 * ay + w2 * by + w3 * cy;
            if (y <= oy && y > best) best = y;
        }
        return best;
    }

    // Lowest triangle surface under (ox,oz) over the whole model -> the interior floor
    // (as opposed to the roof/outer shell above it). -1e9 if XZ hits no triangle.
    float FloorAt(float ox, float oz)
    {
        float best = 1e9f; bool found = false;
        float[] t = _tri;
        for (int b = 0; b + 8 < t.Length; b += 9)
        {
            float ax = t[b], az = t[b + 2], bx = t[b + 3], bz = t[b + 5], cx = t[b + 6], cz = t[b + 8];
            float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (d > -1e-6f && d < 1e-6f) continue;
            float w1 = ((bz - cz) * (ox - cx) + (cx - bx) * (oz - cz)) / d;
            float w2 = ((cz - az) * (ox - cx) + (ax - cx) * (oz - cz)) / d;
            float w3 = 1f - w1 - w2;
            if (w1 < -0.02f || w2 < -0.02f || w3 < -0.02f) continue;
            float y = w1 * t[b + 1] + w2 * t[b + 4] + w3 * t[b + 7];
            if (y < best) { best = y; found = true; }
        }
        return found ? best : -1e9f;
    }

    // Is there a wall within `dist` in the (dirx,dirz) direction at eye height? (horizontal ray)
    bool BlockedHoriz(float ox, float oy, float oz, float dirx, float dirz, float dist)
    {
        float len = (float)Math.Sqrt(dirx * dirx + dirz * dirz);
        if (len < 1e-6f) return false;
        float nx = dirx / len, nz = dirz / len;
        float[] t = _tri;
        for (int b = 0; b + 8 < t.Length; b += 9)
        {
            float hit = RayTri(ox, oy, oz, nx, 0f, nz, t, b);
            if (hit >= 0f && hit <= dist) return true;
        }
        return false;
    }

    // Nearest collision-mesh hit along a unit ray within maxDist, or maxDist if the path is clear.
    // (Chase camera: keeps the viewer's camera on the hunter's side of walls/ceilings.)
    float RayNearest(float ox, float oy, float oz, float dx, float dy, float dz, float maxDist)
    {
        float best = maxDist;
        float[] t = _tri;
        for (int b = 0; b + 8 < t.Length; b += 9)
        {
            float hit = RayTri(ox, oy, oz, dx, dy, dz, t, b);
            if (hit >= 0f && hit < best) best = hit;
        }
        return best;
    }

    // Möller-Trumbore ray/triangle intersection; returns distance along the ray or -1.
    static float RayTri(float ox, float oy, float oz, float dx, float dy, float dz, float[] t, int b)
    {
        float e1x = t[b + 3] - t[b], e1y = t[b + 4] - t[b + 1], e1z = t[b + 5] - t[b + 2];
        float e2x = t[b + 6] - t[b], e2y = t[b + 7] - t[b + 1], e2z = t[b + 8] - t[b + 2];
        float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
        float det = e1x * px + e1y * py + e1z * pz;
        if (det > -1e-6f && det < 1e-6f) return -1f;
        float inv = 1f / det;
        float sx = ox - t[b], sy = oy - t[b + 1], sz = oz - t[b + 2];
        float u = (sx * px + sy * py + sz * pz) * inv;
        if (u < 0f || u > 1f) return -1f;
        float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        float v = (dx * qx + dy * qy + dz * qz) * inv;
        if (v < 0f || u + v > 1f) return -1f;
        float tt = (e2x * qx + e2y * qy + e2z * qz) * inv;
        return tt > 1e-3f ? tt : -1f;
    }

    static int UploadTexture(int[] pixels, int w, int h)
    {
        var ids = new int[1]; GLES30.GlGenTextures(1, ids, 0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, ids[0]);
        var bb = Java.Nio.ByteBuffer.AllocateDirect(pixels.Length * 4);
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        var ib = bb.AsIntBuffer()!; ib.Put(pixels); bb.Position(0);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, bb);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlRepeat);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlRepeat);
        return ids[0];
    }

    static int _white = -1;
    static int WhiteTexture()
    {
        if (_white != -1) return _white;
        _white = UploadTexture(new[] { unchecked((int)0xFFFFFFFF) }, 1, 1);
        return _white;
    }

    static int LinkProgram(string vs, string fs)
    {
        int v = Compile(GLES30.GlVertexShader, vs), f = Compile(GLES30.GlFragmentShader, fs);
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, v); GLES30.GlAttachShader(p, f); GLES30.GlLinkProgram(p);
        var st = new int[1]; GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] == 0) Log.Error("MPHRender", "link: " + GLES30.GlGetProgramInfoLog(p));
        return p;
    }
    static int Compile(int type, string src)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src); GLES30.GlCompileShader(s);
        var st = new int[1]; GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] == 0) Log.Error("MPHRender", "compile: " + GLES30.GlGetShaderInfoLog(s));
        return s;
    }

    // ---- Gameplay HUD implementation ---------------------------------------------------------------

    void SetupHudQuad()
    {
        float[] verts = { 0, 0, 1, 0, 0, 1, 1, 1 }; // unit quad, drawn as a TRIANGLE_STRIP
        var vaoArr = new int[1]; GLES30.GlGenVertexArrays(1, vaoArr, 0); _hudVao = vaoArr[0];
        GLES30.GlBindVertexArray(_hudVao);
        var vboArr = new int[1]; GLES30.GlGenBuffers(1, vboArr, 0);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, vboArr[0]);
        var bb = Java.Nio.ByteBuffer.AllocateDirect(verts.Length * sizeof(float));
        bb.Order(Java.Nio.ByteOrder.NativeOrder());
        var fb = bb.AsFloatBuffer()!; fb.Put(verts); fb.Position(0);
        GLES30.GlBufferData(GLES30.GlArrayBuffer, verts.Length * sizeof(float), fb, GLES30.GlStaticDraw);
        GLES30.GlVertexAttribPointer(0, 2, GLES30.GlFloat, false, 2 * sizeof(float), 0);
        GLES30.GlEnableVertexAttribArray(0);
        GLES30.GlBindVertexArray(0);
    }

    // Android's Bitmap.GetPixels returns ARGB-packed ints (A<<24|R<<16|G<<8|B); the existing
    // UploadTexture helper (below) expects little-endian RGBA-packed ints (R|G<<8|B<<16|A<<24) since
    // it hands the raw bytes straight to GL_RGBA -- repack per pixel before reusing it.
    static int UploadBitmap(Android.Graphics.Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var argb = new int[w * h];
        bmp.GetPixels(argb, 0, w, 0, 0, w, h);
        var rgba = new int[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            int p = argb[i];
            int a = (p >> 24) & 0xFF, r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
            rgba[i] = r | (g << 8) | (b << 16) | (a << 24);
        }
        return UploadTexture(rgba, w, h);
    }

    static int BakeShapeBitmap(Action<Android.Graphics.Canvas, Android.Graphics.Paint, float> draw, int size = 128)
    {
        var bmp = Android.Graphics.Bitmap.CreateBitmap(size, size, Android.Graphics.Bitmap.Config.Argb8888!)!;
        var canvas = new Android.Graphics.Canvas(bmp);
        var paint = new Android.Graphics.Paint(Android.Graphics.PaintFlags.AntiAlias)
        { Color = Android.Graphics.Color.White, StrokeWidth = size * 0.06f };
        paint.SetStyle(Android.Graphics.Paint.Style.Stroke);
        paint.StrokeCap = Android.Graphics.Paint.Cap.Round;
        paint.StrokeJoin = Android.Graphics.Paint.Join.Round;
        draw(canvas, paint, size);
        int tex = UploadBitmap(bmp);
        bmp.Recycle();
        return tex;
    }

    (int Tex, float Aspect) GetOrBakeTextSized(string text)
    {
        if (_hudTextCache.TryGetValue(text, out var cached)) return cached;
        var paint = new Android.Graphics.Paint(Android.Graphics.PaintFlags.AntiAlias)
        { Color = Android.Graphics.Color.White, TextSize = 64f };
        paint.SetTypeface(Android.Graphics.Typeface.Create(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Bold));
        var bounds = new Android.Graphics.Rect();
        paint.GetTextBounds(text, 0, text.Length, bounds);
        int w = Math.Max(1, bounds.Width() + 12), h = Math.Max(1, (int)(paint.TextSize * 1.3f));
        var bmp = Android.Graphics.Bitmap.CreateBitmap(w, h, Android.Graphics.Bitmap.Config.Argb8888!)!;
        var canvas = new Android.Graphics.Canvas(bmp);
        canvas.DrawText(text, 6f - bounds.Left, h * 0.78f, paint);
        int tex = UploadBitmap(bmp);
        bmp.Recycle();
        var result = (tex, (float)w / h);
        _hudTextCache[text] = result;
        return result;
    }

    // Each shape is baked ONCE per id (white stroke on transparent) representing the TOP-LEFT corner;
    // the other 3 corners reuse the same texture, mirrored via the uFlip uniform at draw time.
    static void DrawCornerShape(Android.Graphics.Canvas c, Android.Graphics.Paint p, float s, int id)
    {
        var path = new Android.Graphics.Path();
        switch (id)
        {
            case C_CANOPY: // Samus -- smooth curved corner
                path.MoveTo(s * 0.02f, s * 0.98f); path.LineTo(s * 0.02f, s * 0.35f);
                path.QuadTo(s * 0.02f, s * 0.02f, s * 0.35f, s * 0.02f); path.LineTo(s * 0.98f, s * 0.02f);
                break;
            case C_ROUND: // Kanden -- gentler/thinner curve
                p.StrokeWidth = s * 0.045f;
                path.MoveTo(s * 0.02f, s * 0.9f); path.LineTo(s * 0.02f, s * 0.5f);
                path.QuadTo(s * 0.02f, s * 0.02f, s * 0.5f, s * 0.02f); path.LineTo(s * 0.9f, s * 0.02f);
                break;
            case C_BLADE: // Trace -- 45 degree chamfer
                path.MoveTo(s * 0.02f, s * 0.98f); path.LineTo(s * 0.02f, s * 0.30f);
                path.LineTo(s * 0.30f, s * 0.02f); path.LineTo(s * 0.98f, s * 0.02f);
                break;
            case C_ANGULAR: // Sylux -- sharp double bracket
                path.MoveTo(s * 0.02f, s * 0.98f); path.LineTo(s * 0.02f, s * 0.02f); path.LineTo(s * 0.98f, s * 0.02f);
                c.DrawPath(path, p);
                var inner = new Android.Graphics.Path();
                inner.MoveTo(s * 0.16f, s * 0.7f); inner.LineTo(s * 0.16f, s * 0.16f); inner.LineTo(s * 0.7f, s * 0.16f);
                c.DrawPath(inner, p);
                return;
            case C_ARROW: // Noxus -- L bracket + arrow tick
                path.MoveTo(s * 0.02f, s * 0.9f); path.LineTo(s * 0.02f, s * 0.02f); path.LineTo(s * 0.9f, s * 0.02f);
                c.DrawPath(path, p);
                var tick = new Android.Graphics.Path();
                tick.MoveTo(0, s * 0.28f); tick.LineTo(s * 0.22f, s * 0.02f); tick.LineTo(s * 0.44f, s * 0.28f);
                c.DrawPath(tick, p);
                return;
            case C_ROCK: // Spire -- thick, heavier
                p.StrokeWidth = s * 0.09f;
                path.MoveTo(s * 0.04f, s * 0.96f); path.LineTo(s * 0.04f, s * 0.04f); path.LineTo(s * 0.96f, s * 0.04f);
                break;
            default: // Weavel (C_LIGHTS) -- plain L; the accent light-segments are a future refinement
                path.MoveTo(s * 0.02f, s * 0.85f); path.LineTo(s * 0.02f, s * 0.02f); path.LineTo(s * 0.85f, s * 0.02f);
                break;
        }
        c.DrawPath(path, p);
    }

    static void DrawReticleShape(Android.Graphics.Canvas c, Android.Graphics.Paint p, float s, int id)
    {
        float cx = s / 2f, cy = s / 2f;
        switch (id)
        {
            case R_CIRCLE: // Samus
                c.DrawCircle(cx, cy, s * 0.28f, p);
                break;
            case R_DIAMONDR: // Kanden
                DrawDiamond(c, p, cx, cy, s * 0.32f, false);
                break;
            case R_CROSS: // Trace
                c.DrawLine(cx, cy - s * 0.4f, cx, cy - s * 0.12f, p); c.DrawLine(cx, cy + s * 0.12f, cx, cy + s * 0.4f, p);
                c.DrawLine(cx - s * 0.4f, cy, cx - s * 0.12f, cy, p); c.DrawLine(cx + s * 0.12f, cy, cx + s * 0.4f, cy, p);
                p.SetStyle(Android.Graphics.Paint.Style.Fill); c.DrawCircle(cx, cy, s * 0.025f, p); p.SetStyle(Android.Graphics.Paint.Style.Stroke);
                break;
            case R_SQUARE: // Sylux -- 4 open corner brackets
                DrawBracketCorner(c, p, s * 0.15f, s * 0.15f, s * 0.18f, 1, 1);
                DrawBracketCorner(c, p, s * 0.85f, s * 0.15f, s * 0.18f, -1, 1);
                DrawBracketCorner(c, p, s * 0.15f, s * 0.85f, s * 0.18f, 1, -1);
                DrawBracketCorner(c, p, s * 0.85f, s * 0.85f, s * 0.18f, -1, -1);
                break;
            case R_ARROW: // Noxus -- circle + 4 inward ticks
                c.DrawCircle(cx, cy, s * 0.22f, p);
                DrawTick(c, p, cx, cy - s * 0.42f); DrawTick(c, p, cx, cy + s * 0.42f);
                DrawTick(c, p, cx - s * 0.42f, cy); DrawTick(c, p, cx + s * 0.42f, cy);
                break;
            case R_DIAMOND: // Spire
                DrawDiamond(c, p, cx, cy, s * 0.36f, true);
                break;
            default: // Weavel (R_HEX)
                var hex = new Android.Graphics.Path();
                for (int i = 0; i < 6; i++)
                {
                    float ang = (float)(Math.PI / 3 * i - Math.PI / 2);
                    float px = cx + s * 0.32f * (float)Math.Cos(ang), py = cy + s * 0.32f * (float)Math.Sin(ang);
                    if (i == 0) hex.MoveTo(px, py); else hex.LineTo(px, py);
                }
                hex.Close(); c.DrawPath(hex, p);
                break;
        }
    }
    static void DrawDiamond(Android.Graphics.Canvas c, Android.Graphics.Paint p, float cx, float cy, float r, bool dot)
    {
        var path = new Android.Graphics.Path();
        path.MoveTo(cx, cy - r); path.LineTo(cx + r, cy); path.LineTo(cx, cy + r); path.LineTo(cx - r, cy);
        path.Close(); c.DrawPath(path, p);
        if (dot) { p.SetStyle(Android.Graphics.Paint.Style.Fill); c.DrawCircle(cx, cy, r * 0.08f, p); p.SetStyle(Android.Graphics.Paint.Style.Stroke); }
    }
    static void DrawBracketCorner(Android.Graphics.Canvas c, Android.Graphics.Paint p, float x, float y, float len, int dx, int dy)
    {
        var path = new Android.Graphics.Path();
        path.MoveTo(x, y + len * dy); path.LineTo(x, y); path.LineTo(x + len * dx, y);
        c.DrawPath(path, p);
    }
    static void DrawTick(Android.Graphics.Canvas c, Android.Graphics.Paint p, float x, float y)
    {
        p.SetStyle(Android.Graphics.Paint.Style.Fill);
        c.DrawCircle(x, y, 5f, p);
        p.SetStyle(Android.Graphics.Paint.Style.Stroke);
    }

    // Real per-hunter visor-frame background layer, decoded from the user's OWN extracted ROM at
    // runtime (never bundled -- clean-room). This is a DS BG tile layer: a 12-byte header, 4bpp indexed
    // tile data, an RGB555 palette, then a tilemap. Ported from the independently-verified, already-
    // working desktop decoder at src/MphRead/HUD/HudInfo.cs (see TestLayers there) -- same file format,
    // same math, just re-expressed to build a GLES texture here instead of a desktop export PNG.
    // Screen-centered 256x192 (DS top-screen) crop window, as UV scale+offset, for a layer baked at a
    // larger canvas size. If the canvas is already <= 256x192 on an axis, no crop on that axis.
    static (float ScaleX, float ScaleY, float OffX, float OffY) CenteredCropUv(int w, int h)
    {
        float sx = w > 256 ? 256f / w : 1f, sy = h > 192 ? 192f / h : 1f;
        float ox = w > 256 ? (w - 256) / 2f / w : 0f, oy = h > 192 ? (h - 192) / 2f / h : 0f;
        return (sx, sy, ox, oy);
    }

    (int Tex, int W, int H) GetHunterFrameTex(string hunter) => GetHunterLayerTex(hunter, "bg_top");
    (int Tex, int W, int H) GetHunterOvlTex(string hunter) => GetHunterLayerTex(hunter, "bg_top_ovl");

    // The ROM's real per-hunter archive folder names don't all match the display/style-table name --
    // confirmed via src/MphRead/HUD/HudInfo.cs's own file list (Noxus's archive is "Nox", abbreviated).
    static readonly Dictionary<string, string> HunterArchiveName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Noxus"] = "Nox",
    };

    (int Tex, int W, int H) GetHunterLayerTex(string hunter, string layer)
    {
        string cacheKey = hunter + "/" + layer;
        if (_hudFrameCache.TryGetValue(cacheKey, out var cached)) return cached;
        (int Tex, int W, int H) result = (0, 0, 0);
        try
        {
            string archiveName = HunterArchiveName.TryGetValue(hunter, out string? mapped) ? mapped : hunter;
            string path = MphRead.Paths.Combine(MphRead.Paths.FileSystem, $"_archives/local{archiveName}/{layer}.bin");
            if (System.IO.File.Exists(path))
            {
                var (w, h, pixels) = DecodeBgLayer(System.IO.File.ReadAllBytes(path));
                // Smooth the low-res DS source art for display: edge-directed 2x upscale (Scale2x/
                // AdvMAME2x, applied twice for 4x total), keeping every original color exactly -- no
                // invented pixels, just rounded diagonal/curved edges. Matters at the large on-screen
                // sizes these elements need (Kanden's ring alone spans most of a 1080p-class screen).
                // W/H returned below stay the PRE-upscale dimensions -- callers only use them for the
                // centered-crop UV fraction, which is resolution-independent.
                var (p1, w1, h1) = Scale2x(pixels, w, h);
                var (p2, w2, h2) = Scale2x(p1, w1, h1);
                // Scale2x alone still produces HARD binary-color edges -- just a smarter/finer staircase.
                // On a large shallow curve (a ring spanning most of the screen) a fine hard zigzag still
                // reads as squiggly no matter how fine, because there's no actual sub-pixel blending
                // softening the transition. A real fix needs genuine anti-aliasing (soft, partial-alpha
                // edges), not just more staircase resolution -- so blur the alpha-weighted result.
                var p3 = SoftenEdges(p2, w2, h2, radius: 1);
                var rgba = new int[p3.Length];
                for (int i = 0; i < p3.Length; i++) rgba[i] = (int)p3[i].ToUint();
                result = (UploadTexture(rgba, w2, h2), w, h);
                Log.Info("MPHRender", $"HUD frame: decoded {hunter} {layer}.bin ({w}x{h} -> {w2}x{h2} upscaled)");
            }
            else Log.Warn("MPHRender", $"HUD frame: not found, falling back to drawn brackets: {path}");
        }
        catch (System.Exception ex) { Log.Warn("MPHRender", $"HUD frame: {hunter} {layer} decode failed, falling back to drawn brackets: {ex}"); }
        _hudFrameCache[cacheKey] = result;
        return result;
    }

    // Alpha-weighted (premultiplied-style) box blur: sums RGB weighted by each sample's own alpha, sums
    // alpha separately, and unpremultiplies by the alpha SUM (not the sample count) -- fully-transparent
    // source pixels contribute zero RGB weight, so they can't bleed black into nearby opaque pixels the
    // way a naive unweighted blur would. This is what actually turns Scale2x's hard staircase into a
    // genuinely soft, anti-aliased edge.
    static ColorRgba[] SoftenEdges(ColorRgba[] src, int w, int h, int radius)
    {
        var dst = new ColorRgba[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0, aSum = 0; int n = 0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int sy = y + dy; if (sy < 0 || sy >= h) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x + dx; if (sx < 0 || sx >= w) continue;
                        ColorRgba p = src[sy * w + sx];
                        float pa = p.Alpha / 255f;
                        r += p.Red * pa; g += p.Green * pa; b += p.Blue * pa; aSum += pa;
                        n++;
                    }
                }
                byte outA = (byte)Math.Clamp(aSum / n * 255f, 0, 255);
                if (aSum > 0.0001f)
                {
                    byte outR = (byte)Math.Clamp(r / aSum, 0, 255);
                    byte outG = (byte)Math.Clamp(g / aSum, 0, 255);
                    byte outB = (byte)Math.Clamp(b / aSum, 0, 255);
                    dst[y * w + x] = new ColorRgba(outR, outG, outB, outA);
                }
                else dst[y * w + x] = default;
            }
        }
        return dst;
    }

    // One-off diagnostic (triggered by --es hudDump 1): raw-decode every hunter's bg_top/bg_top_ovl
    // (no crop/upscale/soften -- want the true, full, unmodified canvas) and save as PNGs to external
    // storage, so all of them can be inspected in one adb pull instead of a rebuild per hunter.
    public void DumpAllHunterLayers()
    {
        string dir = System.IO.Path.Combine(_hdDir, "hud_debug");
        System.IO.Directory.CreateDirectory(dir);
        string[] hunters = { "Samus", "Kanden", "Trace", "Sylux", "Noxus", "Spire", "Weavel" };
        foreach (string hunter in hunters)
        {
            string archiveName = HunterArchiveName.TryGetValue(hunter, out string? mapped) ? mapped : hunter;
            foreach (string layer in new[] { "bg_top", "bg_top_ovl", "bg_top_drop" })
            {
                try
                {
                    string path = MphRead.Paths.Combine(MphRead.Paths.FileSystem, $"_archives/local{archiveName}/{layer}.bin");
                    if (!System.IO.File.Exists(path)) { Log.Warn("MPHRender", $"dump: missing {path}"); continue; }
                    var (w, h, pixels) = DecodeBgLayer(System.IO.File.ReadAllBytes(path));
                    SavePng(pixels, w, h, System.IO.Path.Combine(dir, $"{hunter}_{layer}_{w}x{h}.png"));
                    Log.Info("MPHRender", $"dump: saved {hunter}/{layer} ({w}x{h})");
                }
                catch (System.Exception ex) { Log.Warn("MPHRender", $"dump: {hunter}/{layer} failed: {ex.Message}"); }
            }
        }
        Log.Info("MPHRender", $"dump: done, wrote to {dir}");
    }

    static void SavePng(ColorRgba[] pixels, int w, int h, string path)
    {
        var bmp = Android.Graphics.Bitmap.CreateBitmap(w, h, Android.Graphics.Bitmap.Config.Argb8888!)!;
        var argb = new int[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            ColorRgba p = pixels[i];
            argb[i] = (p.Alpha << 24) | (p.Red << 16) | (p.Green << 8) | p.Blue;
        }
        bmp.SetPixels(argb, 0, w, 0, 0, w, h);
        using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create))
        {
            bmp.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, fs);
        }
        bmp.Recycle();
    }

    static (ColorRgba[] Pixels, int W, int H) Scale2x(ColorRgba[] src, int w, int h)
    {
        int outW = w * 2, outH = h * 2;
        var dst = new ColorRgba[outW * outH];
        for (int y = 0; y < h; y++)
        {
            int yu = Math.Max(0, y - 1), yd = Math.Min(h - 1, y + 1);
            for (int x = 0; x < w; x++)
            {
                int xl = Math.Max(0, x - 1), xr = Math.Min(w - 1, x + 1);
                ColorRgba e = src[y * w + x];
                ColorRgba b = src[yu * w + x], hh = src[yd * w + x];
                ColorRgba d = src[y * w + xl], f = src[y * w + xr];
                bool gate = b != hh && d != f;
                int ox = x * 2, oy = y * 2;
                dst[oy * outW + ox] = gate && d == b ? d : e;
                dst[oy * outW + ox + 1] = gate && b == f ? f : e;
                dst[(oy + 1) * outW + ox] = gate && d == hh ? d : e;
                dst[(oy + 1) * outW + ox + 1] = gate && hh == f ? f : e;
            }
        }
        return (dst, outW, outH);
    }

    static (int Width, int Height, ColorRgba[] Pixels) DecodeBgLayer(byte[] bytes)
    {
        int charDataSize = BitConverter.ToInt32(bytes, 4);
        int palDataSize = BitConverter.ToInt32(bytes, 8);
        int offset = 12;
        var charData = new byte[charDataSize];
        Array.Copy(bytes, offset, charData, 0, charDataSize);
        offset += charDataSize;
        int palCount = palDataSize / 2;
        var palette = new ushort[palCount];
        for (int i = 0; i < palCount; i++) palette[i] = BitConverter.ToUInt16(bytes, offset + i * 2);
        offset += palDataSize;
        ushort charsX = BitConverter.ToUInt16(bytes, offset);
        ushort charsY = BitConverter.ToUInt16(bytes, offset + 2);
        int scrDataSize = BitConverter.ToInt32(bytes, offset + 4);
        offset += 8;
        int screenCount = scrDataSize / 2;
        var screen = new ushort[screenCount];
        for (int i = 0; i < screenCount; i++) screen[i] = BitConverter.ToUInt16(bytes, offset + i * 2);

        // Each 8x8 tile is 32 bytes (4bpp = 2 pixels/byte); palette index 0 = transparent.
        int tileCount = charDataSize / 32;
        var tiles = new ColorRgba[tileCount][];
        for (int t = 0; t < tileCount; t++)
        {
            var tile = new ColorRgba[64];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 4; x++)
                {
                    byte b = charData[t * 32 + y * 4 + x];
                    int i1 = b & 0xF, i2 = (b >> 4) & 0xF;
                    tile[y * 8 + x * 2] = i1 == 0 ? default : new ColorRgba(palette[i1]);
                    tile[y * 8 + x * 2 + 1] = i2 == 0 ? default : new ColorRgba(palette[i2]);
                }
            tiles[t] = tile;
        }

        int w = charsX * 8, h = charsY * 8;
        var texture = new ColorRgba[w * h];
        for (int cy = 0; cy < charsY; cy++)
            for (int cx = 0; cx < charsX; cx++)
            {
                int idx = cy * (charsX > 32 ? charsX / 2 : charsX) + (cx / 32 == 1 ? 0x400 + (cx - 32) : cx);
                ushort sd = screen[idx];
                var tile = tiles[sd & 0x3FF];
                bool flipH = (sd & 0x400) != 0, flipV = (sd & 0x800) != 0;
                int start = cy * charsX * 64 + cx * 8;
                for (int py = 0; py < 8; py++)
                {
                    int iy = flipV ? 7 - py : py;
                    for (int px = 0; px < 8; px++)
                    {
                        int ix = flipH ? 7 - px : px;
                        texture[start + py * charsX * 8 + px] = tile[iy * 8 + ix];
                    }
                }
            }
        return (w, h, texture);
    }

    void BakeHudAssets()
    {
        for (int i = 0; i < 7; i++) { int id = i; _hudCornerTex[i] = BakeShapeBitmap((c, p, s) => DrawCornerShape(c, p, s, id)); }
        for (int i = 0; i < 7; i++) { int id = i; _hudReticleTex[i] = BakeShapeBitmap((c, p, s) => DrawReticleShape(c, p, s, id)); }
        _hudRingTex = BakeShapeBitmap((c, p, s) => c.DrawCircle(s / 2f, s / 2f, s * 0.42f, p));
        _hudSweepTex = BakeShapeBitmap((c, p, s) =>
        {
            p.SetStyle(Android.Graphics.Paint.Style.Fill);
            var rf = new Android.Graphics.RectF(s * 0.08f, s * 0.08f, s * 0.92f, s * 0.92f);
            c.DrawArc(rf, 0, 50, true, p);
        });
    }

    // ---- Frame pieces: vector-redrawn, traced from the real decoded ROM textures + reference photos ---
    // The real bg_top/bg_top_ovl ROM art (DecodeBgLayer et al. above) is genuinely useful as ground
    // truth for shape/position, but two problems rule it out for the actual frame render: (1) it's a
    // small DS-native raster source, and a large shallow curve (Kanden's ring) can't be both crisp AND
    // smooth from that no matter how it's upscaled -- confirmed empirically this session, not assumed;
    // (2) the real art was laid out for a 256x192 4:3 screen, and its pieces are NOT one continuous
    // shape in every case -- some hunters (Noxus, Weavel) have genuinely separate corner/edge pieces,
    // which should sit at the TRUE 16:9 screen corners on a widescreen display, not clustered in a
    // narrow centered 4:3 window (docs/HUD_APPROACH.md: "widescreen is exactly where this layout has
    // room to breathe"). Vector pieces solve both: infinitely crisp at any size, and freely repositioned
    // to real screen edges without any raster seam/crop issues.
    struct FramePiece { public int Shape; public float AnchorX, AnchorY, MarginX, MarginY, W, H; public bool Accent2; public float FlipX, FlipY; public float Alpha; }
    const int FP_RING = 0, FP_TOPSTRIP = 1, FP_CORNERANGLE = 2, FP_TOPARC = 3, FP_SIDEBLADE = 4, FP_CHEVRON = 5, FP_CORNERCURVE = 6, FP_ARCSEG = 7, FP_SEGBAR = 8, FP_ARCRING = 9;

    // The TRANSLUCENT visor-frame overlay (from each hunter's bg_top_ovl layer) is VECTOR-redrawn for the
    // clean geometric curves -- a big flat-colored ring/arc can't be both crisp AND smooth upscaled from
    // the tiny raster ROM source (confirmed empirically), and vector gives crisp anti-aliased curves at
    // any size (owner-directed: "the translucent part is what we redraw to be smooth and higher res").
    // Shapes matched to each hunter's REAL decoded ovl: Kanden = near-full ring, Weavel = circle with 4
    // gaps. Hunters whose ovl is a complex/sparse shape (Spire U-bracket, Noxus/Sylux ticks) use the real
    // translucent ovl texture instead (HunterTexPieces Layer=1) to avoid mis-tracing. The OPAQUE corner
    // "visor port" blades stay real bg_top textures either way -- only the translucent accent is redrawn.
    static readonly Dictionary<string, FramePiece[]> HunterFrame = new()
    {
        ["Kanden"] = new[] {
            new FramePiece { Shape = FP_RING, AnchorX = 0, AnchorY = 0, MarginX = 0, MarginY = 0, W = 0.66f, H = 0.82f, Alpha = 0.8f },
        },
        ["Weavel"] = new[] {
            // Circle with 4 gaps (top/bottom/left/right) -- the real bg_top_ovl arc arrangement, NOT a
            // solid ring. Translucent gold, drawn smooth as vector per owner direction.
            new FramePiece { Shape = FP_ARCRING, AnchorX = 0, AnchorY = 0, MarginX = 0, MarginY = 0, W = 0.62f, H = 0.78f, Alpha = 0.7f },
        },
    };

    // Real ROM texture pieces (bg_top, decoded+upscaled+softened by GetHunterFrameTex/DecodeBgLayer
    // above), cropped to each distinct piece via UV rect and repositioned to the true screen corner/
    // edge it belongs at. UV rects measured directly off the dumped raw decode PNGs (DumpAllHunterLayers)
    // at 512x256 -- e.g. Samus's top strip and her two lower corner wedges turned out to already be
    // cleanly disconnected in the source art, not one continuous shape, so no seam issue splitting them.
    // Layer: 0 = bg_top (opaque corner/edge frame art); 1 = bg_top_ovl (the translucent gold accent
    // arcs -- Weavel's circular visor-frame segments etc.). Alpha: 0 defaults to fully opaque (0.95);
    // set a lower value for translucent overlay elements you can see the arena through.
    struct TexturePiece { public float U0, V0, U1, V1; public float AnchorX, AnchorY, MarginX, MarginY, W, H; public float FlipX, FlipY; public int Layer; public float Alpha; }
    static readonly Dictionary<string, TexturePiece[]> HunterTexPieces = new()
    {
        ["Samus"] = new[] {
            new TexturePiece { U0 = 0.146f, V0 = 0f, U1 = 0.850f, V1 = 0.254f, AnchorX = 0, AnchorY = -1, MarginX = 0, MarginY = 0.005f, W = 0.85f, H = 0.16f },
            new TexturePiece { U0 = 0.186f, V0 = 0.605f, U1 = 0.430f, V1 = 1f, AnchorX = -1, AnchorY = 1, MarginX = 0.005f, MarginY = 0.02f, W = 0.24f, H = 0.28f },
            new TexturePiece { U0 = 0.566f, V0 = 0.605f, U1 = 0.810f, V1 = 1f, AnchorX = 1, AnchorY = 1, MarginX = 0.005f, MarginY = 0.02f, W = 0.24f, H = 0.28f },
        },
        ["Trace"] = new[] {
            new TexturePiece { U0 = 0.1f, V0 = 0f, U1 = 0.9f, V1 = 0.3f, AnchorX = 0, AnchorY = -1, MarginX = 0, MarginY = 0.005f, W = 0.85f, H = 0.14f },
        },
        ["Sylux"] = new[] {
            // Note: the bold upper-corner chevrons visible in the real reference photo aren't in EITHER
            // decoded layer (bg_top is just the 2 side blades; bg_top_ovl is small measurement-tick
            // marks, confirmed by direct inspection) -- they likely live in the small-icon
            // HudObjectInstance format, which isn't ported. Left out rather than faked with vector guesses.
            new TexturePiece { U0 = 0.17f, V0 = 0f, U1 = 0.27f, V1 = 1f, AnchorX = -1, AnchorY = 0, MarginX = 0.005f, MarginY = 0, W = 0.05f, H = 0.85f },
            new TexturePiece { U0 = 0.73f, V0 = 0f, U1 = 0.83f, V1 = 1f, AnchorX = 1, AnchorY = 0, MarginX = 0.005f, MarginY = 0, W = 0.05f, H = 0.85f },
        },
        ["Noxus"] = new[] {
            // The "chevron" is one continuous V (both wings meeting near center, confirmed off the raw
            // dump) -- crop its FULL wingspan and display it wide (like Samus/Trace's top strip) rather
            // than a narrow centered slice, so the wingtips actually reach toward the true corners.
            new TexturePiece { U0 = 0.11f, V0 = 0f, U1 = 0.70f, V1 = 0.25f, AnchorX = 0, AnchorY = -1, MarginX = 0, MarginY = 0.005f, W = 0.8f, H = 0.14f },
            new TexturePiece { U0 = 0.05f, V0 = 0.6f, U1 = 0.25f, V1 = 0.9f, AnchorX = -1, AnchorY = 1, MarginX = 0.005f, MarginY = 0.02f, W = 0.24f, H = 0.28f },
            new TexturePiece { U0 = 0.75f, V0 = 0.6f, U1 = 0.95f, V1 = 0.9f, AnchorX = 1, AnchorY = 1, MarginX = 0.005f, MarginY = 0.02f, W = 0.24f, H = 0.28f },
        },
        ["Spire"] = new[] {
            new TexturePiece { U0 = 0.3f, V0 = 0f, U1 = 0.7f, V1 = 0.3f, AnchorX = 0, AnchorY = -1, MarginX = 0, MarginY = 0.005f, W = 0.5f, H = 0.16f },
            new TexturePiece { U0 = 0.05f, V0 = 0.55f, U1 = 0.3f, V1 = 0.9f, AnchorX = -1, AnchorY = 1, MarginX = 0.005f, MarginY = 0.02f, W = 0.24f, H = 0.28f },
            new TexturePiece { U0 = 0.7f, V0 = 0.55f, U1 = 0.95f, V1 = 0.9f, AnchorX = 1, AnchorY = 1, MarginX = 0.005f, MarginY = 0.02f, W = 0.24f, H = 0.28f },
        },
        ["Weavel"] = new[] {
            // Dark-steel corner blades (bg_top) at the very corners...
            new TexturePiece { U0 = 0.127f, V0 = 0f, U1 = 0.303f, V1 = 0.293f, AnchorX = -1, AnchorY = -1, MarginX = 0f, MarginY = 0f, W = 0.27f, H = 0.30f },
            new TexturePiece { U0 = 0.693f, V0 = 0f, U1 = 0.869f, V1 = 0.293f, AnchorX = 1, AnchorY = -1, MarginX = 0f, MarginY = 0f, W = 0.27f, H = 0.30f },
            new TexturePiece { U0 = 0.137f, V0 = 0.605f, U1 = 0.381f, V1 = 1f, AnchorX = -1, AnchorY = 1, MarginX = 0f, MarginY = 0f, W = 0.27f, H = 0.32f },
            new TexturePiece { U0 = 0.615f, V0 = 0.605f, U1 = 0.859f, V1 = 1f, AnchorX = 1, AnchorY = 1, MarginX = 0f, MarginY = 0f, W = 0.27f, H = 0.32f },
            // (Weavel's translucent gold visor-frame arcs are now VECTOR-redrawn -- see HunterFrame
            // FP_ARCRING -- for a crisp smooth curve, per owner direction.)
        },
    };

    // Hunters whose translucent bg_top_ovl overlay is a complex/sparse shape (not a clean ring/arc worth
    // vector-tracing) -- draw the REAL ovl texture, centered + translucent + accent-tinted. It's already
    // upscaled+softened by GetHunterOvlTex, and faithful to the real shape with zero mis-tracing risk.
    // Samus (ovl = a damage/flash animation strip) and Trace (ovl = a scan/lock effect) are intentionally
    // ABSENT: their ovl isn't a visor-frame overlay at all, so adding it would pull a non-frame element in.
    static readonly Dictionary<string, TexturePiece> HunterOvlOverlay = new()
    {
        ["Spire"] = new TexturePiece { U0 = 0f, V0 = 0f, U1 = 1f, V1 = 1f, AnchorX = 0, AnchorY = 0, W = 1.0f, H = 1.0f, Layer = 1, Alpha = 0.7f },
        ["Noxus"] = new TexturePiece { U0 = 0f, V0 = 0f, U1 = 1f, V1 = 1f, AnchorX = 0, AnchorY = 0, W = 1.0f, H = 1.0f, Layer = 1, Alpha = 0.7f },
        ["Sylux"] = new TexturePiece { U0 = 0f, V0 = 0f, U1 = 1f, V1 = 1f, AnchorX = 0, AnchorY = 0, W = 1.0f, H = 1.0f, Layer = 1, Alpha = 0.7f },
    };

    void DrawTexturePieces(HunterHudStyle st, float ar)
    {
        int topTex = GetHunterFrameTex(st.Name).Tex;
        int ovlTex = GetHunterOvlTex(st.Name).Tex;
        // The complex/sparse translucent overlay (real ovl texture) draws UNDER the opaque corner blades.
        if (HunterOvlOverlay.TryGetValue(st.Name, out TexturePiece ovlPiece))
            DrawOneTexPiece(st, ar, ovlPiece, topTex, ovlTex);
        if (HunterTexPieces.TryGetValue(st.Name, out TexturePiece[]? pieces))
            foreach (TexturePiece tp in pieces) DrawOneTexPiece(st, ar, tp, topTex, ovlTex);
    }

    void DrawOneTexPiece(HunterHudStyle st, float ar, TexturePiece tp, int topTex, int ovlTex)
    {
        int tex = tp.Layer == 1 ? ovlTex : topTex;
        if (tex == 0) return;
        float wx = tp.W * ar;
        float cx = tp.AnchorX < 0 ? tp.MarginX + wx / 2 : tp.AnchorX > 0 ? 1f - tp.MarginX - wx / 2 : 0.5f;
        float cy = tp.AnchorY < 0 ? tp.MarginY + tp.H / 2 : tp.AnchorY > 0 ? 1f - tp.MarginY - tp.H / 2 : 0.5f;
        float flipX = tp.FlipX == 0 ? 1f : tp.FlipX, flipY = tp.FlipY == 0 ? 1f : tp.FlipY;
        float alpha = tp.Alpha == 0 ? 0.95f : tp.Alpha;
        // Tinted with the hunter's Accent color, not raw white: the source art decodes as a neutral
        // grey/dark template (confirmed directly off the raw dumps), but the real in-game screenshots
        // show these pieces in the hunter's own color (a shared template the game recolors per hunter).
        DrawHudQuad(cx - wx / 2, cy - tp.H / 2, wx, tp.H, tex, st.Accent[0], st.Accent[1], st.Accent[2], alpha, 0f, flipX, flipY,
            uvScaleX: tp.U1 - tp.U0, uvScaleY: tp.V1 - tp.V0, uvOffX: tp.U0, uvOffY: tp.V0);
    }

    readonly Dictionary<int, int> _frameShapeTex = new();
    void DrawFramePieces(HunterHudStyle st, float ar)
    {
        if (!HunterFrame.TryGetValue(st.Name, out FramePiece[]? pieces)) return;
        foreach (FramePiece fp in pieces)
        {
            if (!_frameShapeTex.TryGetValue(fp.Shape, out int tex))
            {
                tex = BakeShapeBitmap((c, p, s) => DrawFrameShape(c, p, s, fp.Shape));
                _frameShapeTex[fp.Shape] = tex;
            }
            float wx = fp.W * ar;
            float cx = fp.AnchorX < 0 ? fp.MarginX + wx / 2 : fp.AnchorX > 0 ? 1f - fp.MarginX - wx / 2 : 0.5f;
            float cy = fp.AnchorY < 0 ? fp.MarginY + fp.H / 2 : fp.AnchorY > 0 ? 1f - fp.MarginY - fp.H / 2 : 0.5f;
            var color = fp.Accent2 ? st.Accent2 : st.Accent;
            float flipX = fp.FlipX == 0 ? 1f : fp.FlipX, flipY = fp.FlipY == 0 ? 1f : fp.FlipY;
            float alpha = fp.Alpha == 0 ? 0.9f : fp.Alpha;
            DrawHudQuad(cx - wx / 2, cy - fp.H / 2, wx, fp.H, tex, color[0], color[1], color[2], alpha, 0f, flipX, flipY);
        }
    }

    // Each shape drawn once into a square [0,1]x[0,1]-mapped canvas, then stretched to its piece's
    // actual on-screen aspect at draw time (DrawHudQuad already supports independent W/H) -- fine for
    // thin line-art accents, which tolerate mild stretch without looking wrong.
    static void DrawFrameShape(Android.Graphics.Canvas c, Android.Graphics.Paint p, float s, int shape)
    {
        p.StrokeWidth = s * 0.045f;
        switch (shape)
        {
            case FP_RING:
                c.DrawOval(new Android.Graphics.RectF(s * 0.06f, s * 0.02f, s * 0.94f, s * 0.98f), p);
                break;
            case FP_ARCRING: // Weavel -- circle with 4 gaps (top/bottom/left/right): 4 diagonal arcs, one
                             // toward each corner. Matches the real bg_top_ovl arc arrangement.
            {
                p.StrokeWidth = s * 0.05f;
                var oval = new Android.Graphics.RectF(s * 0.06f, s * 0.06f, s * 0.94f, s * 0.94f);
                c.DrawArc(oval, 20, 50, false, p);   // down-right
                c.DrawArc(oval, 110, 50, false, p);  // down-left
                c.DrawArc(oval, 200, 50, false, p);  // up-left
                c.DrawArc(oval, 290, 50, false, p);  // up-right
                break;
            }
            case FP_SEGBAR: // Weavel -- the "arc of blue light-segments across the top": a row of small
                            // filled bars stepping down-and-out (drawn for the top-LEFT; FlipX mirrors it
                            // for the right). Solid-color repeated segments, so a legit vector redraw.
            {
                p.SetStyle(Android.Graphics.Paint.Style.Fill);
                int seg = 5;
                for (int i = 0; i < seg; i++)
                {
                    float t = i / (float)(seg - 1);
                    float cx = s * (0.1f + t * 0.78f);           // left -> right across the piece
                    float cy = s * (0.32f + t * 0.28f);          // gentle downward step toward the outer edge
                    float bw = s * 0.13f, bh = s * 0.3f;         // wider trapezoid-ish segments, tighter cluster
                    var r = new Android.Graphics.RectF(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2);
                    c.DrawRoundRect(r, s * 0.03f, s * 0.03f, p);
                }
                p.SetStyle(Android.Graphics.Paint.Style.Stroke);
                break;
            }
            case FP_TOPSTRIP: // Samus -- shallow faceted line, mountain-range silhouette
            {
                var path = new Android.Graphics.Path();
                path.MoveTo(0, s * 0.7f);
                path.LineTo(s * 0.12f, s * 0.35f); path.LineTo(s * 0.32f, s * 0.5f);
                path.LineTo(s * 0.5f, s * 0.3f); path.LineTo(s * 0.68f, s * 0.5f);
                path.LineTo(s * 0.88f, s * 0.35f); path.LineTo(s, s * 0.7f);
                c.DrawPath(path, p);
                break;
            }
            case FP_CORNERANGLE: // short, thick chamfered wedge accent toward the bottom-left corner
            {
                p.StrokeWidth = s * 0.09f;
                var path = new Android.Graphics.Path();
                path.MoveTo(s * 0.08f, s * 0.85f); path.LineTo(s * 0.5f, s * 0.35f);
                c.DrawPath(path, p);
                break;
            }
            case FP_TOPARC: // Trace -- smooth shallow arc, quad-bezier (predictable, unlike DrawArc's
                             // oval-bbox+angle math which kept rendering off-canvas in testing)
            {
                var path = new Android.Graphics.Path();
                path.MoveTo(0, s * 0.65f); path.QuadTo(s * 0.5f, -s * 0.1f, s, s * 0.65f);
                c.DrawPath(path, p);
                break;
            }
            case FP_SIDEBLADE: // Sylux -- vertical line with a diamond notch mid-span
            {
                var path = new Android.Graphics.Path();
                path.MoveTo(s * 0.7f, 0); path.LineTo(s * 0.7f, s);
                c.DrawPath(path, p);
                var notch = new Android.Graphics.Path();
                notch.MoveTo(s * 0.2f, s * 0.42f); notch.LineTo(s * 0.7f, s * 0.32f);
                notch.LineTo(s * 0.95f, s * 0.47f); notch.LineTo(s * 0.7f, s * 0.62f);
                notch.LineTo(s * 0.2f, s * 0.52f); notch.Close();
                c.DrawPath(notch, p);
                break;
            }
            case FP_CHEVRON: // Noxus/Spire -- shallow angular V across the top
            {
                var path = new Android.Graphics.Path();
                path.MoveTo(0, s * 0.15f); path.LineTo(s * 0.42f, s * 0.5f);
                path.LineTo(s * 0.5f, s * 0.75f); path.LineTo(s * 0.58f, s * 0.5f); path.LineTo(s, s * 0.15f);
                c.DrawPath(path, p);
                break;
            }
            case FP_CORNERCURVE: // Noxus -- quarter-circle-ish corner accent, bulging toward the corner
            {
                p.StrokeWidth = s * 0.08f;
                var path = new Android.Graphics.Path();
                path.MoveTo(0, s * 0.9f); path.QuadTo(0, 0, s * 0.9f, 0);
                c.DrawPath(path, p);
                break;
            }
            default: // FP_ARCSEG (Weavel) -- short curved segment hugging the corner, same technique
            {
                p.StrokeWidth = s * 0.09f;
                var path = new Android.Graphics.Path();
                path.MoveTo(s * 0.1f, s * 0.95f); path.QuadTo(s * 0.1f, s * 0.1f, s * 0.95f, s * 0.1f);
                c.DrawPath(path, p);
                break;
            }
        }
    }

    void DrawHudQuad(float x, float y, float w, float h, int tex, float r, float g, float b, float a, float rot = 0f, float flipX = 1f, float flipY = 1f, float uvScaleX = 1f, float uvScaleY = 1f, float uvOffX = 0f, float uvOffY = 0f)
    {
        GLES30.GlUniform2f(_hudUPos, x, y);
        GLES30.GlUniform2f(_hudUSize, w, h);
        GLES30.GlUniform1f(_hudURot, rot);
        GLES30.GlUniform2f(_hudUFlip, flipX, flipY);
        GLES30.GlUniform2f(_hudUUvScale, uvScaleX, uvScaleY);
        GLES30.GlUniform2f(_hudUUvOffset, uvOffX, uvOffY);
        GLES30.GlUniform4f(_hudUColor, r, g, b, a);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlBindTexture(GLES30.GlTexture2d, tex);
        GLES30.GlUniform1i(_hudUTex, 0);
        GLES30.GlDrawArrays(GLES30.GlTriangleStrip, 0, 4);
    }

    void DrawEnergyBar(HunterHudStyle st, float ar)
    {
        var (numTex, numAspect) = GetOrBakeTextSized(st.Energy.ToString());
        float segW = 0.012f, segWx = segW * ar;
        if (st.BarMode == 0) // Samus: horizontal, top-center
        {
            const int n = 16;
            int on = (int)MathF.Round(st.Energy / 99f * n);
            float gapX = 0.004f * ar, totalW = n * (segWx + gapX) - gapX, x0 = 0.5f - totalW / 2f, y = 0.03f;
            for (int i = 0; i < n; i++)
                DrawHudQuad(x0 + i * (segWx + gapX), y, segWx, 0.018f, WhiteTexture(), st.Accent2[0], st.Accent2[1], st.Accent2[2], i < on ? 0.95f : 0.25f);
            float numH = 0.045f, numW = numH * numAspect * ar;
            DrawHudQuad(x0 - numW - 0.015f, y - 0.008f, numW, numH, numTex, st.Accent2[0], st.Accent2[1], st.Accent2[2], 0.95f);
        }
        else // vertical bar(s), left and/or (Trace/Sylux) both sides
        {
            const int n = 12;
            int on = (int)MathF.Round(st.Energy / 99f * n);
            float x = 0.02f, y0 = 0.16f, segH = 0.028f, gapY = 0.006f, barW = segWx * 1.4f;
            for (int i = 0; i < n; i++)
                DrawHudQuad(x, y0 + i * (segH + gapY), barW, segH, WhiteTexture(), st.Accent2[0], st.Accent2[1], st.Accent2[2], i < on ? 0.95f : 0.25f);
            if (st.BarMode == 2)
            {
                float xr = 1f - 0.02f - barW;
                for (int i = 0; i < n; i++)
                    DrawHudQuad(xr, y0 + i * (segH + gapY), barW, segH, WhiteTexture(), st.Accent2[0], st.Accent2[1], st.Accent2[2], i < on ? 0.95f : 0.25f);
            }
        }
    }

    void DrawAmmoAndBeams(HunterHudStyle st, float ar)
    {
        var (ammoTex, ammoAsp) = GetOrBakeTextSized($"{st.AmmoCur:000} / {st.AmmoMax:000}");
        var (wepTex, wepAsp) = GetOrBakeTextSized(st.Weapon);
        float boxW = 0.18f, boxH = 0.10f, x = 1f - 0.03f - boxW, y = 0.42f;
        DrawHudQuad(x, y, boxW, boxH, WhiteTexture(), st.Accent[0], st.Accent[1], st.Accent[2], 0.15f);
        float numH = 0.04f, numW = numH * ammoAsp * ar;
        DrawHudQuad(x + boxW - numW - 0.01f, y + 0.008f, numW, numH, ammoTex, st.Accent2[0], st.Accent2[1], st.Accent2[2], 0.95f);
        float wepH = 0.024f, wepW = wepH * wepAsp * ar;
        DrawHudQuad(x + boxW - wepW - 0.01f, y + numH + 0.02f, wepW, wepH, wepTex, 0.85f, 0.9f, 0.95f, 0.85f);
        float fillFrac = st.AmmoMax > 0 ? (float)st.AmmoCur / st.AmmoMax : 0f;
        DrawHudQuad(x + 0.01f, y + boxH - 0.018f, (boxW - 0.02f) * fillFrac, 0.008f, WhiteTexture(), st.Accent2[0], st.Accent2[1], st.Accent2[2], 0.9f);

        // Below the ammo box, right side -- bottom-left/right are now occupied by the real corner
        // texture pieces pushed to the true screen corners, so this is clear space instead.
        float bsz = 0.045f, bszX = bsz * ar, gap = 0.012f * ar;
        float bx = x + boxW - 3 * bszX - 2 * gap, by = y + boxH + 0.03f;
        for (int i = 0; i < 3; i++)
            DrawHudQuad(bx + i * (bszX + gap), by, bszX, bsz, WhiteTexture(), st.Accent[0], st.Accent[1], st.Accent[2], i == 0 ? 0.85f : 0.25f);
    }

    void DrawRadar(HunterHudStyle st, float ar)
    {
        float sz = 0.16f, szX = sz * ar, x = 0.02f, y = 0.16f;
        float cx = x + szX / 2, cy = y + sz / 2;
        DrawHudQuad(x, y, szX, sz, _hudRingTex, st.Accent[0], st.Accent[1], st.Accent[2], 0.5f);
        float innerSz = sz * 0.55f, innerSzX = innerSz * ar;
        DrawHudQuad(cx - innerSzX / 2, cy - innerSz / 2, innerSzX, innerSz, _hudRingTex, st.Accent[0], st.Accent[1], st.Accent[2], 0.4f);
        // Rotating sweep: a filled wedge texture rotated by elapsed wall-clock time (no per-frame sim
        // tick to hang this off yet -- purely a decorative placeholder like the rest of this pass).
        float sweepAngle = (float)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 3400 / 3400.0 * Math.PI * 2);
        DrawHudQuad(cx - szX / 2, cy - sz / 2, szX, sz, _hudSweepTex, st.Accent[0], st.Accent[1], st.Accent[2], 0.35f, sweepAngle);
        float bs = 0.012f, bsX = bs * ar;
        DrawHudQuad(cx + szX * 0.10f - bsX / 2, cy - sz * 0.18f - bs / 2, bsX, bs, WhiteTexture(), st.Accent[0], st.Accent[1], st.Accent[2], 0.9f);
        DrawHudQuad(cx - szX * 0.10f - bsX / 2, cy + sz * 0.13f - bs / 2, bsX, bs, WhiteTexture(), st.Accent[0], st.Accent[1], st.Accent[2], 0.9f);
    }

    void DrawMinimap(HunterHudStyle st, float ar)
    {
        float w = 0.19f, h = 0.16f, x = 1f - 0.02f - w, y = 0.16f;
        DrawHudQuad(x, y, w, h, WhiteTexture(), st.Accent[0], st.Accent[1], st.Accent[2], 0.12f);
        void Room(float rx, float ry, float rw, float rh) =>
            DrawHudQuad(x + rx * w, y + ry * h, rw * w, rh * h, WhiteTexture(), st.Accent[0], st.Accent[1], st.Accent[2], 0.35f);
        Room(0.08f, 0.22f, 0.34f, 0.30f); Room(0.46f, 0.10f, 0.26f, 0.54f);
        Room(0.30f, 0.60f, 0.50f, 0.30f); Room(0.74f, 0.42f, 0.20f, 0.22f);
        var (labTex, labAsp) = GetOrBakeTextSized(_room);
        float labH = 0.022f, labW = labH * labAsp * ar;
        DrawHudQuad(x + 0.006f, y + 0.006f, labW, labH, labTex, st.Accent2[0], st.Accent2[1], st.Accent2[2], 0.9f);
    }

    void DrawGameplayHud()
    {
        if (_hudProgram == 0) return;
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlUseProgram(_hudProgram);
        GLES30.GlBindVertexArray(_hudVao);
        var st = HunterStyles[_hudHunterIdx];
        float ar = _vw > 0 ? (float)_vh / _vw : 1f; // converts a height-fraction size into a width-fraction, for square/circular shapes

        float rsz = 0.06f, rszX = rsz * ar;
        DrawHudQuad(0.5f - rszX / 2, 0.5f - rsz / 2, rszX, rsz, _hudReticleTex[st.Reticle], st.Accent2[0], st.Accent2[1], st.Accent2[2], 0.85f);

        DrawEnergyBar(st, ar);
        DrawAmmoAndBeams(st, ar);
        DrawRadar(st, ar);
        DrawMinimap(st, ar);

        // Corner/edge visor-frame pieces LAST -- they are the outermost overlay, so they must draw ON TOP
        // of radar/minimap/etc. Earlier they drew first and the radar (top-left) + minimap (top-right)
        // painted over the two TOP corner frame pieces, hiding exactly the elements the owner circled.
        DrawTexturePieces(st, ar);
        DrawFramePieces(st, ar);

        var (tagTex, tagAspect) = GetOrBakeTextSized(st.Name.ToUpperInvariant());
        float tagH = 0.03f, tagW = tagH * tagAspect * ar;
        DrawHudQuad(0.5f - tagW / 2, 0.94f, tagW, tagH, tagTex, st.Accent2[0], st.Accent2[1], st.Accent2[2], 0.85f);

        GLES30.GlBindVertexArray(0);
        GLES30.GlDisable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
    }
}
