using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Rendering;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRecomp.App;

// recomp: side-view mode P0 (owner 2026-11-12, docs/SIDE_VIEW_MODE.md): a dev run on Data Shrine seen side-on. The
// players walk the stage on the plane x = 0 (MphRead PlayerSideView.cs / SideViewStage.cs, Core SideView/DataShrineStage.cs);
// this file is the host's half: the launch (--es sideview 1), the camera that frames every player from the side (zooming
// out to the stage's camera box, with bubbles at the screen's edge for anyone outside it, each showing that hunter: owner
// 11-12, "the circle that is supposed to show the off screen bubble ... just shows on the side of the screen without
// anything in it"), the KO gap past the box's sides, the right stick's aim on the plane, the stance button (the weapon ring's, L2 by default: the ring stays shut in side view), the reticle and the
// stance's laser, and the live tuning keys (jump height, run speed, zoom, how much of the room in front of the plane is
// cut away). No HUD (owner 11-12, "there should be no HUD whatsoever"): MphRead records none in side view
// (Renderer.RecordHud) and the status text stays empty, but for the tuning line a few seconds after a tuning key
// (SideViewStatusText); the reticle and the laser are the owner's own ask (11-12, "some kind of reticle and a stand in
// place laser aiming option like in dread").
// Dev builds only: a public build never turns it on.
internal sealed partial class CampaignRenderer
{
    const float SvFovY = 40; // degrees (the player's FOV setting scales it, Scene.HostFovScale)
    const float SvPadZ = 5, SvPadDown = 2.5f, SvPadUp = 4.5f; // room kept round the players
    const float SvMinHalfH = 7; // the closest view: about 14 units tall
    const float SvEase = 0.1f;
    // the near cut sits past the hexes' front points (x -3.72 on Data Shrine): a cut through them showed them hollow
    // (owner 11-12, "the hex platforms are see through"); the owner's own live tuning since (11-12 18:2x: "ive also
    // adjusted zoom and near cut", J 4.00 run x1.00 zoom x0.95 near 13.0)
    const float SvNearMarginDefault = 13, SvZoomDefault = 0.95f;

    // tuned live; kept between matches until the exe closes (\ puts them back)
    static float _svZoom = SvZoomDefault;
    static float _svNearMargin = SvNearMarginDefault;
    // how far past the camera box's sides the KO lines sit (owner 11-12, "there should be some room between off screen
    // and actually hitting the kill zone"): a player past the main room's walls is in a bubble for that stretch first
    static float _svKoGap = MphRecomp.SideView.DataShrineStage.KoGapDefault;

    bool _svCamPlaced;
    float _svCz, _svCy, _svHalfH;
    // this step's bubbles (SideViewOverlay), each drawn with its hunter inside after the HUD (DrawSideViewBubbles)
    readonly record struct SvBubble(PlayerEntity Q, float X, float Y, float R);
    readonly List<SvBubble> _svBubbles = new();
    float _svAspect = 16 / 9f;
    int _svMaskProg, _svMaskVao, _svMaskVbo;
    readonly float[] _svMaskQuad = new float[24];
    readonly int[] _svViewport = new int[4];
    int _svTuneShow; // steps left to show the tuning line (a tuning key shows it)
    static readonly Keys[] SvKeys =
    {
        Keys.LeftBracket, Keys.RightBracket, Keys.Semicolon, Keys.Apostrophe, Keys.Minus, Keys.Equal,
        Keys.Comma, Keys.Period, Keys.Backslash, Keys.KeyPadSubtract, Keys.KeyPadAdd, Keys.D9, Keys.D0,
    };
    readonly bool[] _svKeyPrev = new bool[SvKeys.Length];
    static Scene.HostHudSprite[]? _svBubbleSprites;
    static Scene.HostHudSprite? _svReticleSprite, _svLaserSprite, _svLaserHitSprite;
    // --svstance 1 holds the stance, --svaim <degrees> holds the right stick that way (0 right, 90 up), --svz <z> puts the
    // hunter on the walk lane at z once the view is placed (past an end wall: a bubble): for screenshots
    bool _svHoldStance;
    Vector2? _svHoldStick;
    float? _svPutZ;

    // ConfigureMatch, after ConfigureSolo: --es sideview 1 or GAME MODE's dev row (MatchSettings.SideView; dev builds)
    // plays the match side-on. Data Shrine is the only stage so far (P0), so the arena is set to it; Battle, the bots idle
    // where they stand, no time limit.
    void ConfigureSideView(LaunchArgs args, MatchSettings settings)
    {
        ResetSideView();
        if ((args.Get("sideview") != "1" && !settings.SideView) || MphRecomp.Config.RecompSettings.PublicBuild) return;
        PlayerEntity.SideViewOn = true;
        PlayerEntity.SideStage = MphRecomp.SideView.DataShrineStage.Create();
        settings.Arena = MphRecomp.SideView.DataShrineStage.Room;
        settings.Mode = GameMode.Battle;
        settings.PointGoal = 99;
        settings.TimeLimitSeconds = -1;
        settings.BotStuckFix = false;
        PlayerEntity.HostIdleBots = true; // (P0: the bots are targets that stand on their spawn)
        PlayerEntity.HostSpawnAtOnce = true;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var num = System.Globalization.NumberStyles.Float;
        if (float.TryParse(args.Get("svjump"), num, inv, out float jump)) PlayerEntity.SideViewJump = Math.Clamp(jump, 1, 12);
        if (float.TryParse(args.Get("svrun"), num, inv, out float run)) PlayerEntity.SideViewRunScale = Math.Clamp(run, 0.3f, 3);
        if (float.TryParse(args.Get("svzoom"), num, inv, out float zoom)) _svZoom = Math.Clamp(zoom, 0.4f, 3);
        if (float.TryParse(args.Get("svnear"), num, inv, out float near)) _svNearMargin = Math.Clamp(near, 0, 40);
        if (float.TryParse(args.Get("svgap"), num, inv, out float gap)) _svKoGap = Math.Clamp(gap, 1, 20);
        if (PlayerEntity.SideStage is { } svStage) SideViewKoGap(svStage);
        _svHoldStance = args.Get("svstance") == "1";
        if (float.TryParse(args.Get("svaim"), num, inv, out float aim))
        {
            float a = MathHelper.DegreesToRadians(aim);
            _svHoldStick = new Vector2(MathF.Cos(a), -MathF.Sin(a));
        }
        if (float.TryParse(args.Get("svz"), num, inv, out float putZ)) _svPutZ = Math.Clamp(putZ, -40, 40);
        SetThirdPerson(true); // the hunter's own body is drawn (HostShowMainBiped)
        Log.Info("MPHMatch", $"side view: {PlayerEntity.SideStage.Name} ({settings.Arena}), {settings.Players.Count - 1} idle bots");
    }

    // ConfigureMatch, first thing: side view off for every match (menu, LAN, online, replay, dev) until ConfigureSideView
    void ResetSideView()
    {
        if (PlayerEntity.SideViewOn) PlayerEntity.HostHideReticle = false;
        PlayerEntity.SideViewOn = false;
        PlayerEntity.SideStage = null;
        PlayerEntity.SideViewKOs = 0;
        PlayerEntity.SideViewStickX = PlayerEntity.SideViewStickY = 0;
        PlayerEntity.SideViewStanceHeld = false;
        _svHoldStance = false;
        _svHoldStick = null;
        _svPutZ = null;
        Scene.HostNearClip = null;
        _svCamPlaced = false;
        _svTuneShow = 0;
        _svBubbles.Clear();
    }

    // the side KO lines, the gap past the camera box's sides
    static void SideViewKoGap(SideViewStage stage)
    {
        stage.KoLeft = stage.CamZMin - _svKoGap;
        stage.KoRight = stage.CamZMax + _svKoGap;
    }

    // UpdateThirdPerson, after each sim step: true when the side camera placed the view (Scene.HostView)
    bool UpdateSideViewCam(Scene scene, PlayerEntity p)
    {
        if (!PlayerEntity.SideViewOn || PlayerEntity.SideStage is not { } stage)
        {
            if (_svCamPlaced)
            {
                scene.HostView = null;
                Scene.HostNearClip = null;
                PlayerEntity.HostHideReticle = false;
                _svCamPlaced = false;
            }
            return false;
        }
        SideViewKeys();
        if (_svTuneShow > 0 && --_svTuneShow == 0) _statusCountdown = 0; // (the tuning line goes now)
        PadState pad = Pad;
        PlayerEntity.SideViewStickX = _svHoldStick?.X ?? pad.Rx; // the aim on the plane (PlayerSideView.SideViewAim; +y is down)
        PlayerEntity.SideViewStickY = _svHoldStick?.Y ?? pad.Ry;
        PlayerEntity.SideViewStanceHeld = _svHoldStance || _padBinds.Held(MphRecomp.Config.ControlBinds.WeaponMenu); // (the mouse's: zoom)
        if (!_thirdPerson) SetThirdPerson(true);
        if (CameraSequence.Current != null || !(MathF.Abs(p.CameraInfo.ViewMatrix.Determinant) > 1e-3f))
        {
            // (the match intro's fly-over, and a player without a camera yet, keep the game's own view)
            scene.HostView = null;
            Scene.HostNearClip = null;
            PlayerEntity.HostHideReticle = false;
            _svCamPlaced = false;
            return false;
        }
        if (_svPutZ is float putZ && p.Health > 0)
        {
            p.Position = new Vector3(0, 3.53f - Fixed.ToFloat(p.Values.MinPickupHeight) + 0.02f, putZ); // (Data Shrine's walk lane)
            p.Speed = Vector3.Zero;
            _svPutZ = null;
        }
        // every live player's body, kept to the stage's camera box (anyone outside it gets a bubble instead)
        float zMin = Single.MaxValue, zMax = Single.MinValue, yMin = Single.MaxValue, yMax = Single.MinValue;
        for (int i = 0; i < PlayerEntity.Players.Count; i++)
        {
            PlayerEntity q = PlayerEntity.Players[i];
            if (!SideViewSeen(q)) continue;
            float z = Math.Clamp(q.Position.Z, stage.CamZMin, stage.CamZMax);
            float feet = Math.Clamp(q.Position.Y + Fixed.ToFloat(q.Values.MinPickupHeight), stage.CamYMin, stage.CamYMax);
            float head = Math.Clamp(q.Position.Y + Fixed.ToFloat(q.Values.MaxPickupHeight), stage.CamYMin, stage.CamYMax);
            zMin = MathF.Min(zMin, z);
            zMax = MathF.Max(zMax, z);
            yMin = MathF.Min(yMin, feet);
            yMax = MathF.Max(yMax, head);
        }
        if (zMin > zMax)
        {
            zMin = zMax = Math.Clamp(p.Position.Z, stage.CamZMin, stage.CamZMax);
            yMin = Math.Clamp(p.Position.Y, stage.CamYMin, stage.CamYMax);
            yMax = yMin + 2;
        }
        float aspect = _height > 0 ? _width / (float)_height : 16 / 9f;
        float lo = yMin - SvPadDown, hi = yMax + SvPadUp;
        float cy = (lo + hi) / 2;
        float cz = (zMin + zMax) / 2;
        float halfH = MathF.Max(MathF.Max((hi - lo) / 2, ((zMax - zMin) / 2 + SvPadZ) / aspect), SvMinHalfH) * _svZoom;
        float boxHalfZ = (stage.CamZMax - stage.CamZMin) / 2, boxHalfY = (stage.CamYMax - stage.CamYMin) / 2;
        // the widest view is the box's width (the main room, wall to wall, owner 11-12: "you can still see everyone in the
        // whole room"), whatever the screen's shape: a narrower screen shows more above and below instead
        halfH = MathF.Min(halfH, boxHalfZ / aspect);
        if (!_svCamPlaced)
        {
            _svCz = cz;
            _svCy = cy;
            _svHalfH = halfH;
            _svCamPlaced = true;
        }
        else
        {
            _svCz += (cz - _svCz) * SvEase;
            _svCy += (cy - _svCy) * SvEase;
            _svHalfH += (halfH - _svHalfH) * SvEase;
        }
        // the view's edges stay inside the box
        float halfW = _svHalfH * aspect;
        _svCz = halfW >= boxHalfZ ? (stage.CamZMin + stage.CamZMax) / 2 : Math.Clamp(_svCz, stage.CamZMin + halfW, stage.CamZMax - halfW);
        _svCy = _svHalfH >= boxHalfY ? (stage.CamYMin + stage.CamYMax) / 2 : Math.Clamp(_svCy, stage.CamYMin + _svHalfH, stage.CamYMax - _svHalfH);
        // (Renderer: fov' = 2 atan(tan(fov / 2) * HostFovScale) when no camera sequence runs)
        float tanHalf = MathF.Tan(MathHelper.DegreesToRadians(SvFovY / 2)) * Scene.HostFovScale;
        float dist = _svHalfH / tanHalf;
        scene.HostCameraOffset = Vector3.Zero;
        scene.HostView = new HostView(new Vector3(-dist, _svCy, _svCz), new Vector3(0, _svCy, _svCz), Vector3.UnitY, SvFovY);
        // the room in front of the plane is cut away, all but the near margin (the walkway's front half, the hexes and so on)
        Scene.HostNearClip = MathF.Max(dist - _svNearMargin, 0.0625f);
        PlayerEntity.HostOutsideHead = true; // no helmet frame
        PlayerEntity.HostHideReticle = true;
        // the room's parts are drawn as seen from the view's middle on the plane, inside the main room (RoomEntity's
        // node-ref cull: from the camera's own place, a player past an end wall turned the world black), and the free
        // mouse reticle keeps to this frame (PlayerSideView)
        PlayerEntity.SideViewCullAt = new Vector3(0, _svCy, _svCz);
        PlayerEntity.SideViewFrameCentre = new Vector2(_svCz, _svCy);
        PlayerEntity.SideViewFrameHalf = new Vector2(halfW, _svHalfH);
        _svAspect = aspect;
        SideViewOverlay(scene, p, stage, halfW, aspect);
        return true;
    }

    static bool SideViewSeen(PlayerEntity q) =>
        q.LoadFlags.TestFlag(LoadFlags.Active) && q.LoadFlags.TestFlag(LoadFlags.Spawned) && q.Health > 0;

    // the overlay, HUD quads after this step's RecordHud (which records nothing else in side view): in the stance the
    // main player's laser, their reticle, and a bubble at the screen's edge for each player out of view
    void SideViewOverlay(Scene scene, PlayerEntity p, SideViewStage stage, float halfW, float aspect)
    {
        if (scene.HudItemCount > 0) return; // (no RecordHud this step: they're still in)
        _svBubbles.Clear();
        _svBubbleSprites ??= MakeBubbleSprites();
        _svReticleSprite ??= MakeReticleSprite();
        _svLaserSprite ??= MakeDotSprite(new ColorRgba(255, 60, 40, 230));
        _svLaserHitSprite ??= MakeDotSprite(new ColorRgba(255, 235, 70, 255));
        float halfH = _svHalfH;
        float NdcX(float z) => (z - _svCz) / halfW;
        float NdcY(float y) => (y - _svCy) / halfH;
        if (!p.IsBot && SideViewSeen(p))
        {
            if (p.SideViewStance)
            {
                // a dotted line from the muzzle to where the shot meets the room or a player (yellow on a player)
                Vector3 from = p.SideViewMuzzle, to = p.SideViewLaserEnd;
                Scene.HostHudSprite dot = p.SideViewLaserHit ? _svLaserHitSprite : _svLaserSprite;
                const float d = 0.004f; // half a dot, of the screen's half height
                float length = (to - from).Length, step = d * 3 * halfH;
                int n = step > 0 ? Math.Min((int)(length / step), 400) : 0;
                for (int k = 1; k <= n; k++)
                {
                    Vector3 at = from + (to - from) * (k * step / length);
                    float x = NdcX(at.Z), y = NdcY(at.Y);
                    scene.HostHudQuad(dot, x - d / aspect, x + d / aspect, y + d, y - d);
                }
                float ex = NdcX(to.Z), ey = NdcY(to.Y);
                scene.HostHudQuad(dot, ex - 2.5f * d / aspect, ex + 2.5f * d / aspect, ey + 2.5f * d, ey - 2.5f * d);
            }
            // the reticle: where the mouse has it in the frame (owner 11-12, "can we get the reticle to follow the mouse
            // freely"), else out from the muzzle along the stick's aim
            Vector2 c = p.SideViewReticle;
            float rx = NdcX(c.X), ry = NdcY(c.Y);
            const float rr = 0.035f;
            scene.HostHudQuad(_svReticleSprite, rx - rr / aspect, rx + rr / aspect, ry + rr, ry - rr);
        }
        // a bubble for each player none of whose body is on the screen, at the screen's edge nearest them (level with them
        // on a side, over them at the top or bottom), smaller the nearer they are to a KO line; DrawSideViewBubbles puts
        // the hunter in it after the HUD
        for (int i = 0; i < PlayerEntity.Players.Count; i++)
        {
            PlayerEntity q = PlayerEntity.Players[i];
            if (!SideViewSeen(q)) continue;
            float w = Fixed.ToFloat(q.Values.BipedColRadius);
            float feet = q.Position.Y + Fixed.ToFloat(q.Values.MinPickupHeight);
            float head = q.Position.Y + Fixed.ToFloat(q.Values.MaxPickupHeight);
            if (NdcX(q.Position.Z + w) >= -1 && NdcX(q.Position.Z - w) <= 1 && NdcY(head) >= -1 && NdcY(feet) <= 1) continue;
            float mid = (feet + head) / 2;
            // 0 at the screen's edge, 1 at the KO line
            float outZ = MathF.Abs(q.Position.Z - _svCz) - halfW;
            float koZ = (q.Position.Z > _svCz ? stage.KoRight - _svCz : _svCz - stage.KoLeft) - halfW;
            float outY = MathF.Abs(mid - _svCy) - halfH;
            float koY = (mid > _svCy ? stage.KoTop - _svCy : _svCy - stage.KoBottom) - halfH;
            float frac = Math.Clamp(MathF.Max(outZ / MathF.Max(koZ, 1), outY / MathF.Max(koY, 1)), 0, 1);
            float r = 0.12f - 0.05f * frac;
            float xEdge = 1 - 1.5f * r / aspect, yEdge = 1 - 1.5f * r;
            float x = Math.Clamp(NdcX(q.Position.Z), -xEdge, xEdge), y = Math.Clamp(NdcY(mid), -yEdge, yEdge);
            Scene.HostHudSprite bubble = _svBubbleSprites[q.SlotIndex % _svBubbleSprites.Length];
            scene.HostHudQuad(bubble, x - r / aspect, x + r / aspect, y + r, y - r, 0.9f);
            _svBubbles.Add(new SvBubble(q, x, y, r));
        }
    }

    // CampaignRenderer.Render, after the HUD: each bubble's hunter drawn inside its ring. The scene's own draws again for
    // that player's items (and HD body), through a projection that moves and enlarges their picture into the bubble,
    // kept to the ring's inside by the scissor and a depth mask (no second render target)
    void DrawSideViewBubbles(Scene scene)
    {
        if (_svBubbles.Count == 0 || scene != _host?.Scene || !SvMaskGl()) return;
        GLES30.GlGetIntegerv(0x0BA2, _svViewport, 0); // GL_VIEWPORT
        int vx = _svViewport[0], vy = _svViewport[1], vw = _svViewport[2], vh = _svViewport[3];
        if (vw <= 0 || vh <= 0) return;
        Matrix4 proj = scene.PerspectiveMatrix;
        IReadOnlyList<RenderItem> opaque = scene.OpaqueItems, decal = scene.DecalItems, trans = scene.TranslucentItems;
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlEnable(GLES30.GlScissorTest);
        foreach (SvBubble b in _svBubbles)
        {
            PlayerEntity q = b.Q;
            Matrix4 qv = SmoothView(q, scene);
            Vector2 Ndc(float y)
            {
                Vector4 c = new Vector4(q.Position.X, q.Position.Y + y, q.Position.Z, 1) * qv * proj;
                return MathF.Abs(c.W) > 1e-6f ? new Vector2(c.X / c.W, c.Y / c.W) : Vector2.Zero;
            }
            float lo = Fixed.ToFloat(q.Values.MinPickupHeight), hi = Fixed.ToFloat(q.Values.MaxPickupHeight);
            Vector2 mid = Ndc((lo + hi) / 2);
            float tall = MathF.Abs(Ndc(hi).Y - Ndc(lo).Y);
            if (tall < 1e-4f) continue;
            float m = 1.5f * b.R * 0.78f / tall; // the hunter about three quarters of the ring's inside
            Matrix4 crop = Matrix4.Identity;
            crop.M11 = m;
            crop.M22 = m;
            crop.M41 = b.X - m * mid.X;
            crop.M42 = b.Y - m * mid.Y;
            Matrix4 bubbleProj = proj * crop;
            // the bubble's square, in pixels
            float hw = b.R / _svAspect;
            int px = vx + (int)MathF.Floor((b.X - hw + 1) / 2 * vw), py = vy + (int)MathF.Floor((b.Y - b.R + 1) / 2 * vh);
            int pw = (int)MathF.Ceiling(hw * vw) + 1, ph = (int)MathF.Ceiling(b.R * vh) + 1;
            GLES30.GlScissor(px, py, pw, ph);
            // the depth: cleared inside the square, then the nearest outside the ring's inside (the mask draws no colour)
            GLES30.GlDepthMask(true);
            GLES30.GlClear(GLES30.GlDepthBufferBit);
            GLES30.GlColorMask(false, false, false, false);
            GLES30.GlDepthFunc(GLES30.GlAlways);
            float[] v = _svMaskQuad;
            float x0 = b.X - hw, x1 = b.X + hw, y0 = b.Y - b.R, y1 = b.Y + b.R;
            SvMaskVertex(v, 0, x0, y0, -1, -1);
            SvMaskVertex(v, 1, x1, y0, 1, -1);
            SvMaskVertex(v, 2, x0, y1, -1, 1);
            SvMaskVertex(v, 3, x1, y1, 1, 1);
            GLES30.GlUseProgram(_svMaskProg);
            GLES30.GlBindVertexArray(_svMaskVao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _svMaskVbo);
            GLES30.GlBufferData(GLES30.GlArrayBuffer, v.Length * 4, v, GLES30.GlDynamicDraw);
            GLES30.GlDrawArrays(GLES30.GlTriangleStrip, 0, 4);
            GLES30.GlColorMask(true, true, true, true);
            // the hunter: the scene's program with the bubble's projection, unfogged
            GLES30.GlUseProgram(_program);
            SetMatrix(U("u_projection"), bubbleProj);
            SetMatrix(U("u_view"), scene.ViewMatrix);
            _sfBound = SmoothNone;
            GLES30.GlUniform1i(U("u_fogOn"), 0);
            scene.HostPerspective = bubbleProj; // (the HD bodies' passes read the scene's)
            bool main = q == PlayerEntity.Main;
            GLES30.GlDepthFunc(GLES30.GlLess);
            _layerGl.SetAlphaFilter(LayerAlphaFilter.SolidOnly);
            foreach (RenderItem item in opaque)
            {
                if (item.HostOwner == q) DrawItem(item, scene);
            }
            if (main) DrawSuit(scene, SuitPass.Opaque);
            DrawSkins(scene, SuitPass.Opaque);
            GLES30.GlUseProgram(_program);
            GLES30.GlDepthFunc(GLES30.GlLequal);
            _layerGl.SetPolygonOffsetFill(true);
            _layerGl.SetPolygonOffset(-1, -1);
            foreach (RenderItem item in decal)
            {
                if (item.HostOwner == q) DrawItem(item, scene);
            }
            _layerGl.SetPolygonOffsetFill(false);
            GLES30.GlEnable(GLES30.GlBlend);
            GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
            GLES30.GlDepthMask(false);
            _layerGl.SetAlphaFilter(LayerAlphaFilter.SeeThroughOnly);
            foreach (RenderItem item in trans)
            {
                if (item.HostOwner == q) DrawItem(item, scene);
            }
            if (main)
            {
                DrawSuit(scene, SuitPass.Translucent);
                DrawSuit(scene, SuitPass.Additive);
            }
            DrawSkins(scene, SuitPass.Translucent);
            DrawSkins(scene, SuitPass.Additive);
            GLES30.GlUseProgram(_program);
            _layerGl.SetAlphaFilter(LayerAlphaFilter.Off);
            scene.HostPerspective = null;
        }
        // the state DrawHud left
        GLES30.GlDisable(GLES30.GlScissorTest);
        GLES30.GlDepthMask(true);
        GLES30.GlDepthFunc(GLES30.GlLess);
        GLES30.GlUseProgram(_program);
        SetMatrix(U("u_projection"), scene.PerspectiveMatrix);
        SetMatrix(U("u_view"), scene.ViewMatrix);
        _sfBound = SmoothNone;
        GLES30.GlUniform1i(U("u_fogOn"), scene.FogEnabled ? 1 : 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }

    static void SvMaskVertex(float[] v, int i, float x, float y, float u, float t)
    {
        int o = i * 6;
        v[o] = x;
        v[o + 1] = y;
        v[o + 2] = -1; // the nearest depth
        v[o + 3] = 1;
        v[o + 4] = u;
        v[o + 5] = t;
    }

    const string SvMaskVert = "#version 300 es\nlayout(location = 0) in vec4 aPos;\nlayout(location = 1) in vec2 aUv;\n"
        + "out vec2 vUv;\nvoid main() { gl_Position = aPos; vUv = aUv; }\n";
    // the ring's inside (MakeRingSprite's 12.5 of 16) is left alone
    const string SvMaskFrag = "#version 300 es\nprecision mediump float;\nin vec2 vUv;\nout vec4 fragColor;\n"
        + "void main() { if (dot(vUv, vUv) < 0.6104) discard; fragColor = vec4(0.0); }\n";

    // the mask's program and buffer, made again after a new GL context (it empties _u: a key of ours marks ours alive)
    bool SvMaskGl()
    {
        if (!_u.ContainsKey("sideviewbubble"))
        {
            _svMaskProg = _svMaskVao = _svMaskVbo = 0;
            _u["sideviewbubble"] = 1;
        }
        if (_svMaskProg == 0)
        {
            _svMaskProg = CampaignGlCache.Program(SvMaskVert, SvMaskFrag, "side view bubble mask");
            if (_svMaskProg == 0) return false;
            var ids = new int[1];
            GLES30.GlGenVertexArrays(1, ids, 0); _svMaskVao = ids[0];
            GLES30.GlGenBuffers(1, ids, 0); _svMaskVbo = ids[0];
            GLES30.GlBindVertexArray(_svMaskVao);
            GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _svMaskVbo);
            GLES30.GlEnableVertexAttribArray(0);
            GLES30.GlVertexAttribPointer(0, 4, GLES30.GlFloat, false, 24, 0);
            GLES30.GlEnableVertexAttribArray(1);
            GLES30.GlVertexAttribPointer(1, 2, GLES30.GlFloat, false, 24, 16);
            GLES30.GlBindVertexArray(0);
        }
        return true;
    }

    // a white ring with a dot in the middle, a dark edge round both (it reads on the bright walls and the dark sky)
    static Scene.HostHudSprite MakeReticleSprite()
    {
        const int size = 32;
        var pixels = new ColorRgba[size * size];
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                pixels[y * size + x] = d < 2.2f || d >= 10 && d < 12.5f ? new ColorRgba(255, 255, 255, 240)
                    : d < 3.4f || d >= 8.8f && d < 13.7f ? new ColorRgba(0, 0, 0, 170)
                    : new ColorRgba(0, 0, 0, 0);
            }
        }
        return new Scene.HostHudSprite { Width = size, Height = size, Pixels = pixels };
    }

    static Scene.HostHudSprite MakeDotSprite(ColorRgba colour)
    {
        const int size = 4;
        var pixels = new ColorRgba[size * size];
        Array.Fill(pixels, colour);
        return new Scene.HostHudSprite { Width = size, Height = size, Pixels = pixels };
    }

    static Scene.HostHudSprite[] MakeBubbleSprites()
    {
        ColorRgba[] colours =
        {
            new(255, 200, 40, 255), new(235, 70, 60, 255), new(70, 210, 100, 255), new(80, 150, 255, 255),
            new(240, 120, 230, 255), new(90, 225, 225, 255), new(250, 150, 60, 255), new(200, 200, 200, 255),
        };
        var sprites = new Scene.HostHudSprite[colours.Length];
        for (int i = 0; i < colours.Length; i++)
        {
            sprites[i] = MakeRingSprite(colours[i], 200, 128, 12.5f, 15); // (big: a bubble is up to a quarter of the screen tall)
        }
        return sprites;
    }

    // a ring of the colour round a dark middle (insideAlpha: 0 = clear), size pixels square
    static Scene.HostHudSprite MakeRingSprite(ColorRgba colour, byte insideAlpha, int size, float inner, float outer)
    {
        var pixels = new ColorRgba[size * size];
        float c = (size - 1) / 2f, scale = size / 32f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / scale;
                pixels[y * size + x] = d > outer ? new ColorRgba(0, 0, 0, 0)
                    : d >= inner ? colour
                    : new ColorRgba(0, 0, 0, insideAlpha);
            }
        }
        return new Scene.HostHudSprite { Width = size, Height = size, Pixels = pixels };
    }

    // the live tuning keys (a PC keyboard; one step a press): [ ] jump height, ; ' run speed, - = zoom, , . the near
    // margin, 9 0 the KO gap, \ all back to their defaults
    void SideViewKeys()
    {
        KeyboardMouseInput? km = KeyboardMouse;
        if (km == null) return;
        for (int i = 0; i < SvKeys.Length; i++)
        {
            bool down = km.KeyDown(SvKeys[i]);
            bool press = down && !_svKeyPrev[i];
            _svKeyPrev[i] = down;
            if (!press) continue;
            switch (SvKeys[i])
            {
                case Keys.LeftBracket: PlayerEntity.SideViewJump = MathF.Max(PlayerEntity.SideViewJump - 0.25f, 1); break;
                case Keys.RightBracket: PlayerEntity.SideViewJump = MathF.Min(PlayerEntity.SideViewJump + 0.25f, 12); break;
                case Keys.Semicolon: PlayerEntity.SideViewRunScale = MathF.Max(PlayerEntity.SideViewRunScale - 0.05f, 0.3f); break;
                case Keys.Apostrophe: PlayerEntity.SideViewRunScale = MathF.Min(PlayerEntity.SideViewRunScale + 0.05f, 3); break;
                case Keys.Minus or Keys.KeyPadSubtract: _svZoom = MathF.Min(_svZoom + 0.05f, 3); break; // out
                case Keys.Equal or Keys.KeyPadAdd: _svZoom = MathF.Max(_svZoom - 0.05f, 0.4f); break; // in
                case Keys.Comma: _svNearMargin = MathF.Max(_svNearMargin - 0.5f, 0); break;
                case Keys.Period: _svNearMargin = MathF.Min(_svNearMargin + 0.5f, 40); break;
                case Keys.D9: _svKoGap = MathF.Max(_svKoGap - 1, 1); break;
                case Keys.D0: _svKoGap = MathF.Min(_svKoGap + 1, 20); break;
                case Keys.Backslash:
                    PlayerEntity.SideViewJump = PlayerEntity.SideViewJumpDefault;
                    PlayerEntity.SideViewRunScale = 1;
                    _svZoom = SvZoomDefault;
                    _svNearMargin = SvNearMarginDefault;
                    _svKoGap = MphRecomp.SideView.DataShrineStage.KoGapDefault;
                    break;
            }
            if (PlayerEntity.SideStage is { } stage) SideViewKoGap(stage);
            _svTuneShow = 180; // three seconds
            _statusCountdown = 0;
            Log.Info("MPHMatch", $"side view tuning: J {PlayerEntity.SideViewJump:0.00}  run x{PlayerEntity.SideViewRunScale:0.00}"
                + $"  zoom x{_svZoom:0.00}  near {_svNearMargin:0.0}  KO gap {_svKoGap:0}");
        }
    }

    // MatchStatusText in side view: nothing over the stage but the gun-adjust pause's lines, and the tuning values for a
    // few seconds after a tuning key
    string SideViewStatusText() => _paused ? PauseText()
        : _svTuneShow == 0 ? ""
        : $"SIDE VIEW  J {PlayerEntity.SideViewJump:0.00} ([ ])  run x{PlayerEntity.SideViewRunScale:0.00} (; ')"
          + $"  zoom x{_svZoom:0.00} (- =)  near {_svNearMargin:0.0} (, .)  KO gap {_svKoGap:0} (9 0)  \\ resets  KOs {PlayerEntity.SideViewKOs}";
}
