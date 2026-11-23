using System;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Import.Effects;
using MphRecomp.Holiday;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// HOLIDAY MODE's falling snow (owner 2026-11-20: "model our snow's behavior off of Phendrana Shorelines"), drawn the way
// the Shorelines snow is (DrawPrime1Snow, CampaignRenderer.Prime1Fx.cs; docs/HOLIDAY_SNOW.md) but with our own flake:
// a fixed set of flakes, each its own place and fall speed, wrapped into a 127 m box centred ahead of the camera, so the
// whole view is snowing from the first frame and nothing ever appears or loads as you turn. A flake is drawn where it is
// ahead of the camera, nearer than the fog's far edge (faded in over the last 5 m), not under a roof and not over empty
// space (HolidaySnow.Map, the room's roof grid built when it loads). One draw, additive, after the room. Draw-only: matches,
// replays and online play are the same with it on or off. Rooms with their own Shorelines snow keep only theirs.
internal sealed partial class CampaignRenderer
{
    const float HolidaySnowScale = 0.5925926f; // Prime metres to MPH units (as the Shorelines arena's Look.Scale)
    float _hsTime;      // s of sim time in this room
    long _hsTickAt;     // Stopwatch ticks at the last sim step
    Vector3 _hsDrift;   // the wind's drift so far (MPH units)
    int _hsRoom = -1;
    Vector3[]? _hsFlakes;
    float[]? _hsFall;
    int _hsTex;         // our flake (a soft round dot), a GL texture

    // TickSkins (MatchSkins.cs), after TickPrime1Fx
    void TickHolidaySnow()
    {
        if (!HolidaySnow.Enabled || _host == null || _host.Ended) return;
        if (_hsRoom != _host.RoomId)
        {
            _hsRoom = _host.RoomId;
            _hsTime = 0;
            _hsDrift = Vector3.Zero;
        }
        if (!_host.IsMenuBackdrop) HolidayGround.MatchPlaying(); // the ground snow's background build waits (HolidayGround.Prebuild)
        HolidayDevCam();
        const float dt = 1 / 60f;
        _hsTime += dt;
        _hsTickAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Vector2 wind = HolidaySnow.Wind(_hsTime);
        _hsDrift += new Vector3(wind.X, 0, wind.Y) * dt;
    }

    // dev: MPH_DEVCAM="x,y,z,yaw,pitch" (degrees) opens photo mode there after 60 frames, for PC shots inside a room
    int _hsDevCamWait;
    void HolidayDevCam()
    {
        if (_hsDevCamWait < 0 || Environment.GetEnvironmentVariable("MPH_DEVCAM") is not { } spec) return;
        if (++_hsDevCamWait < 60) return;
        _hsDevCamWait = -1;
        float[] v = Array.ConvertAll(spec.Split(','), t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        OpenPhotoMode();
        if (!PhotoOpen || v.Length < 5) return;
        _photoAnchor = _photoPos = new Vector3(v[0], v[1], v[2]);
        _photoYaw = MathHelper.DegreesToRadians(v[3]);
        _photoPitch = MathHelper.DegreesToRadians(v[4]);
        PhotoApply();
    }

    // DrawSkins (MatchSkins.cs), after DrawPrime1Fx
    void DrawHolidaySnow(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || !HolidaySnow.Enabled || _host == null || scene != _host.Scene) return;
        if (_p1Fx is { } lib && lib.Flakes.Length > 0) return; // the room's own Shorelines snow
        if (HolidaySnow.Map is not { } map || map.RoomId != _host.RoomId || _hsRoom != _host.RoomId) return;
        if (!FxGl()) return;
        if (_hsTex == 0 || !_u.ContainsKey("holidaysnow")) MakeHolidayFlake();
        if (_hsFlakes == null)
        {
            // the flakes: spread through the field (m), each its own fall speed (2-4 m/s); the same every time
            var rng = new Random(0x5e0f);
            _hsFlakes = new Vector3[P1SnowMax];
            _hsFall = new float[P1SnowMax];
            for (int i = 0; i < P1SnowMax; i++)
            {
                _hsFlakes[i] = new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * P1SnowField;
                _hsFall[i] = 2 + 2 * rng.NextSingle();
            }
        }
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var fwd = -new Vector3(view.M13, view.M23, view.M33);
        Vector3 cam = scene.CameraPosition;
        float s = HolidaySnowScale, field = P1SnowField * s;
        // the time since the last sim step, so the flakes move smoothly at any frame rate
        float between = MathF.Min(1 / 60f, (float)System.Diagnostics.Stopwatch.GetElapsedTime(_hsTickAt).TotalSeconds);
        float t = _hsTime + between;
        // the field's corner: centred ahead of the camera across the ground, on the camera up and down
        var flat = new Vector3(fwd.X, 0, fwd.Z);
        if (flat.LengthSquared > 1e-6f) flat.Normalize();
        Vector3 centre = cam + flat * (P1SnowAhead * s);
        Vector3 corner = centre - new Vector3(field / 2);
        float fogNear = P1SnowFogNear * s, fogFar = P1SnowFogFar * s, half = P1SnowFlake * s / 2;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Vector3 ax = right * half, ay = up * half;
        Vector3[] flakes = _hsFlakes;
        float[] fall = _hsFall!;
        int n = 0;
        for (int i = 0; i < flakes.Length; i++)
        {
            // the flake's place: its own, fallen, blown, wrapped into the field
            Vector3 f = flakes[i] * s + _hsDrift - new Vector3(0, fall[i] * s * t, 0) - corner;
            f.X -= MathF.Floor(f.X / field) * field;
            f.Y -= MathF.Floor(f.Y / field) * field;
            f.Z -= MathF.Floor(f.Z / field) * field;
            Vector3 at = corner + f, d = at - cam;
            if (Vector3.Dot(d, fwd) <= 0) continue;
            float dist = d.Length;
            if (dist >= fogFar) continue;
            if (!map.SeesSky(at)) continue;
            float a = MathF.Min(1, (fogFar - dist) / (fogFar - fogNear));
            Quad(ref n, vp, at - ax + ay, at + ax + ay, at - ax - ay, at + ax - ay, new Vector4(1, 1, 1, a));
        }
        if (n == 0) return;
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        // (uTex isn't set: a sampler uniform starts at unit 0, and the other effects only ever set it to 0. Asking GL where a uniform is waits for
        // the GPU to catch up: it cost ~15 ms a frame on PC, holiday mode's whole frame dip, owner 2026-11-20)
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        DrawRun(0, RetroBlend.Add, n, gl: _hsTex);
        // MphRead's own drawing state back, as DrawDroneFx leaves it
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
    }

    // our flake: a white dot, solid in the middle and soft to its edge (made again after a new GL context)
    void MakeHolidayFlake()
    {
        const int size = 32;
        var px = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float a = Math.Clamp((1 - r) / 0.6f, 0, 1);
                a *= a;
                px[y * size + x] = (int)(0x00ffffffu | (uint)(a * 255 + 0.5f) << 24);
            }
        }
        var ids = new int[1];
        GLES30.GlGenTextures(1, ids, 0);
        _hsTex = ids[0];
        GLES30.GlBindTexture(GLES30.GlTexture2d, _hsTex);
        GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, size, size, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, px);
        GLES30.GlGenerateMipmap(GLES30.GlTexture2d);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinearMipmapLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
        GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
        _u["holidaysnow"] = 1;
    }
}
