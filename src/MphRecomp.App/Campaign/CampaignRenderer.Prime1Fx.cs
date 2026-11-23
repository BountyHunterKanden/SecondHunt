using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Sound;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Prime 1 rooms' own weather and water in Prime 1's look and sound (Phendrana Shorelines first; any Prime 1 room the
// arena import gave hd/prime1_fx.json: Prime1Fx.Import, the player's own disc's PARTs, textures and sounds):
//   splashes: Water's SplashParticle1 + sfx 463 where a player (local, bot or online) crosses a water box's top, entering
//     at scale 2.8, leaving at 1.9, at least 0.2 s apart per player (CScriptWater's player splash)
//   visor runoff: the own player leaving the water unmorphed (6ed6486e) or unmorphing in it (52e2d0fb), on the camera,
//     with sfx 2499
//   snow: AreaAttributes' FXType snow, CEnvFxManager's field: 127 m of 8x8 wrapping cells centred ahead of the camera,
//     7320 x density flakes falling 2-4 m/s with DUMB_SnowForces' wind (one step per 2 frames), additive 0.2 m quads of
//     TXTR_SnowFlake, gone in the black fog from 52 to 57 m, none under a roof (the import's roof grid standing in for
//     TXTR_EnvGradient), half bright with the camera under water
//   underwater: the Water's InsideFogColor over the view, then a multiply filter in it (DrawPrime1Underwater, called
//     from DrawArenaUnderwater)
// The splash and runoff systems go in the drone effects' live list (_fxLive: stepped by TickDroneFx, drawn by DrawDroneFx).
internal sealed partial class CampaignRenderer
{
    const float P1SfxRange = 80f;
    const float P1SplashGap = 0.2f;          // s between one player's splashes
    const float P1SnowField = 127f, P1SnowAhead = 23.8f, P1SnowFlake = 0.2f;
    const float P1SnowFogNear = 52f, P1SnowFogFar = 57f;
    const int P1SnowMax = 7320;
    // how far toward Prime's in-water colour the multiply filter darkens the view (1 = the full InsideFogColor)
    const float P1MultiplyStrength = 0.5f;

    sealed class Prime1FxLib
    {
        public Prime1Fx.Look Look = null!;
        public RetroParticles Parts = null!;
        public readonly Dictionary<string, uint> Fx = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (short[] Pcm, int Rate)> Sounds = new(StringComparer.Ordinal);
        public uint FlakeTex;
        // the crates' break (CampaignRenderer.SandCrateFx.cs flies the pieces, plays crate_break): its sound only, the
        // flash is Prime1Once's (Prime's scale)
        public PillarFxLib? Crates;
        // the flakes: a place in the field (m) and a fall speed (m/s)
        public Vector3[] Flakes = Array.Empty<Vector3>();
        public float[] Fall = Array.Empty<float>();
    }

    sealed class Prime1Seat
    {
        public bool Wet, Alt;
        public Vector3 At;
        public float LastSplash = -10;
        public readonly SoundSource Sound = new() { ReferenceDistance = 0, MaxDistance = P1SfxRange };
    }

    Prime1FxLib? _p1Fx;
    int _p1FxRoom = -1;
    float _p1Time;                           // s of sim time in this room
    long _p1TickAt;                          // Stopwatch ticks at the last sim step (the draw's in-between time)
    int _p1Step;
    Vector3 _p1Drift;                        // the wind's drift so far (MPH units)
    readonly Dictionary<PlayerEntity, Prime1Seat> _p1Seats = new();
    readonly List<(DroneFxLive Fx, float Until)> _p1Live = new();
    DroneFxLive? _p1Runoff;

    // TickSkins (MatchSkins.cs), before TickDroneFx (which steps the systems in _fxLive)
    void TickPrime1Fx()
    {
        if (_host == null || _host.Ended)
        {
            StopPrime1Fx();
            return;
        }
        if (_p1FxRoom != _host.RoomId)
        {
            StopPrime1Fx();
            _p1FxRoom = _host.RoomId;
            _p1Fx = LoadPrime1Fx();
            _p1Time = 0;
            _p1Step = 0;
            _p1Drift = Vector3.Zero;
        }
        if (_p1Fx is not { } lib) return;
        const float dt = 1 / 60f;
        _p1Time += dt;
        _p1TickAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lib.Look.Snow is { Forces.Length: >= 2 } snow)
        {
            // one wind step per 2 frames (CEnvFxManager), Prime's (x, y) across the ground = MPH (x, -z)
            int n = snow.Forces.Length / 2, k = _p1Step / 2 % n;
            _p1Drift += new Vector3(snow.Forces[k * 2], 0, -snow.Forces[k * 2 + 1]) * (lib.Look.Scale * dt);
        }
        _p1Step++;
        for (int i = _p1Live.Count - 1; i >= 0; i--)
        {
            // (a system that never ends of itself stops emitting after a while)
            if (_p1Time >= _p1Live[i].Until) { _p1Live[i].Fx.Sys.Emitting = false; _p1Live.RemoveAt(i); }
        }
        // the crates (TickPillarFx leaves them alone: a Prime room has no pillar look)
        if (lib.Crates is { } crates && _pillarFx == null)
        {
            bool first = true;
            foreach (EntityBase e in _host.Scene.Entities)
            {
                if (e is not MiningPlazaCrateEntity c) continue;
                if (first) Prime1CrateTestStep(c);
                first = false;
                int before = _sandCrates.TryGetValue(c, out var had) ? had.Breaks : Int32.MaxValue;
                TickSandCrate(crates, c);
                if (c.Breaks > before)
                {
                    Vector3 mid = c.Spot.Center.Length == 3 ? MiningPlazaCrates.V(c.Spot.Center) : c.Position;
                    if (_p1Test) Log.Info("MPHArena", $"prime1 fx test: crate {c.Spot.Id:x8} broke at {mid} ({c.Spot.DebrisCount} of {c.Spot.Debris.Count} pieces)");
                    Prime1Once(lib, "crate_flash", mid, Prime1Fx.CrateFlashScale);
                }
            }
        }
        if (PlayerEntity.HostWater is not { } water) return;
        Prime1FxTestStep(water);
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health <= 0)
            {
                if (p != null) _p1Seats.Remove(p);
                continue;
            }
            bool wet = p.EchoesSubmerged, alt = p.IsAltForm;
            Vector3 at = p.Position;
            if (!_p1Seats.TryGetValue(p, out Prime1Seat? st))
            {
                // first seen (spawned, joined): what it already is stays quiet
                _p1Seats[p] = new Prime1Seat { Wet = wet, Alt = alt, At = at };
                continue;
            }
            bool jumped = (at - st.At).LengthSquared > 25; // a respawn, a teleport, a replay's seek: no splash
            bool mine = p.IsMainPlayer && !p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
            if (wet != st.Wet && !jumped && _p1Time - st.LastSplash >= P1SplashGap
                && WaterTop(water, at) is float top)
            {
                st.LastSplash = _p1Time;
                if (_p1Test) Log.Info("MPHArena", $"prime1 fx test: {(wet ? "in" : "out")} splash at {at} (player {p.SlotIndex}){(!wet && mine && !alt ? " + runoff" : "")}");
                var spot = new Vector3(at.X, top, at.Z);
                Prime1Once(lib, "splash", spot, wet ? lib.Look.SplashEnterScale : lib.Look.SplashExitScale);
                st.Sound.Position = spot;
                st.Sound.Self = p.IsMainPlayer;
                Prime1Sound(lib, "splash", st.Sound);
                // out of the water on foot: the visor's runoff
                if (!wet && mine && !alt) Prime1Runoff(lib, "runoff", st.Sound);
            }
            // unmorphing in the water: the visor's other runoff
            if (st.Alt && !alt && wet && mine && !jumped) Prime1Runoff(lib, "unmorph", st.Sound);
            st.Wet = wet;
            st.Alt = alt;
            st.At = at;
        }
    }

    // dev check (MPH_P1FXTEST=1, never in public builds): the own player is dropped over the biggest water box 1 s after
    // spawning, put under its top at 4 s (in splash) and lifted just out at 5 s (out splash, + runoff in first person).
    // The unmorph runoff isn't scripted: morph and unmorph in the water by hand.
    readonly bool _p1Test = !MphRecomp.Config.RecompSettings.PublicBuild && Environment.GetEnvironmentVariable("MPH_P1FXTEST") == "1";
    int _p1TestAt = -1;

    void Prime1FxTestStep((Vector3 Min, Vector3 Max)[] water)
    {
        if (!_p1Test || _host?.Player is not PlayerEntity p || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health <= 0 || water.Length == 0) return;
        if (_p1TestAt < 0) _p1TestAt = _p1Step;
        int k = _p1Step - _p1TestAt;
        (Vector3 Min, Vector3 Max) box = water[0];
        foreach ((Vector3 Min, Vector3 Max) w in water)
        {
            if ((w.Max.X - w.Min.X) * (w.Max.Z - w.Min.Z) > (box.Max.X - box.Min.X) * (box.Max.Z - box.Min.Z)) box = w;
        }
        var mid = new Vector3((box.Min.X + box.Max.X) / 2, box.Max.Y, (box.Min.Z + box.Max.Z) / 2);
        Vector3 at = k == 60 ? mid + new Vector3(0, 1.5f, 0) : k == 240 ? mid - new Vector3(0, 0.8f, 0) : k == 300 ? mid + new Vector3(0, 0.4f, 0) : default;
        if (at == default) return;
        p.Teleport(at, new Vector3(0, 0, -1), _host.Scene.GetNodeRefContaining(at));
        p.PrevPosition = at;
        Log.Info("MPHArena", $"prime1 fx test: moved to {at} (water top {box.Max.Y})");
    }

    // (the dev check in a room with crates: beside the first crate from 2 s to 5 s, facing it; broken at 3 s)
    void Prime1CrateTestStep(MiningPlazaCrateEntity c)
    {
        if (!_p1Test || _host?.Player is not PlayerEntity p || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health <= 0) return;
        if (_p1TestAt < 0) _p1TestAt = _p1Step;
        int k = _p1Step - _p1TestAt;
        Vector3 mid = c.Spot.Center.Length == 3 ? MiningPlazaCrates.V(c.Spot.Center) : c.Position;
        if (k >= 120 && k <= 300)
        {
            // (held there, looking down at it, until the pieces have landed)
            Vector3 at = mid + new Vector3(0, 1.5f, 4);
            p.Teleport(at, Vector3.Normalize(new Vector3(0, -0.35f, -1)), _host.Scene.GetNodeRefContaining(at));
            p.PrevPosition = at;
            if (k == 120) Log.Info("MPHArena", $"prime1 fx test: moved to {at} (crate {c.Spot.Id:x8} at {mid})");
        }
        if (k == 180) { Log.Info("MPHArena", $"prime1 fx test: breaking crate {c.Spot.Id:x8} (up {c.Up}, breaks {c.Breaks})"); c.NetApply(false, false, c.Breaks + 1); }
    }

    static float? WaterTop((Vector3 Min, Vector3 Max)[] water, Vector3 at)
    {
        foreach ((Vector3 min, Vector3 max) in water)
        {
            if (at.X >= min.X && at.X <= max.X && at.Z >= min.Z && at.Z <= max.Z && at.Y >= min.Y - 2 && at.Y < max.Y + 2) return max.Y;
        }
        return null;
    }

    void Prime1Sound(Prime1FxLib lib, string role, SoundSource? src)
    {
        if (_sfx != null && lib.Sounds.TryGetValue(role, out var snd)) _sfx.PlayPcm(snd.Pcm, snd.Rate, 1, src);
    }

    // a system at `at`, Prime's axes as the room maps them (X, Y = -Z, Z = up), `scale` times Prime's size
    void Prime1Once(Prime1FxLib lib, string role, Vector3 at, float scale)
    {
        if (!lib.Fx.TryGetValue(role, out uint id)) return;
        var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, id, GrappleMatrix(at, -Vector3.UnitZ * scale, Vector3.UnitX * scale, Vector3.UnitY * scale), follow: true, seed: Environment.TickCount ^ (int)id) };
        _fxLive.Add(fx);
        _p1Live.Add((fx, _p1Time + 3));
    }

    // the runoff: on the camera (Prime's view space: x right, y ahead, z up), placed each frame by PlacePrime1Fx
    void Prime1Runoff(Prime1FxLib lib, string role, SoundSource src)
    {
        if (!lib.Fx.TryGetValue(role, out uint id)) return;
        StopFx(ref _p1Runoff);
        var fx = new DroneFxLive { Sys = new RetroParticleSystem(lib.Parts, id, GrappleMatrix(Vector3.Zero, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ), follow: true, seed: Environment.TickCount ^ (int)id), Whole = true, Ride = Matrix4.Identity };
        _fxLive.Add(fx);
        _p1Live.Add((fx, _p1Time + 3));
        _p1Runoff = fx;
        src.Self = true;
        Prime1Sound(lib, "runoff", src);
    }

    void StopPrime1Fx()
    {
        foreach ((DroneFxLive fx, _) in _p1Live) fx.Sys.Emitting = false;
        _p1Live.Clear();
        _p1Seats.Clear();
        _p1Runoff = null;
        if (_host == null || _host.Ended)
        {
            _p1FxRoom = -1;
            _p1Fx = null;
        }
    }

    // DrawSkins (MatchSkins.cs), before DrawDroneFx: the runoff put on this frame's camera, then the snow
    void DrawPrime1Fx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _p1Fx is not { } lib) return;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        var fwd = -new Vector3(view.M13, view.M23, view.M33);
        Vector3 cam = scene.CameraPosition;
        if (_p1Runoff != null)
        {
            if (_p1Runoff.Sys.Done && !_p1Runoff.Sys.Emitting) _p1Runoff = null;
            else _p1Runoff.Ride = new Matrix4(new Vector4(right, 0), new Vector4(fwd, 0), new Vector4(up, 0), new Vector4(cam, 1));
        }
        if (lib.Look.Snow is { } snow && lib.Flakes.Length > 0 && lib.FlakeTex != 0) DrawPrime1Snow(scene, lib, snow, cam, right, up, fwd);
    }

    void DrawPrime1Snow(Scene scene, Prime1FxLib lib, Prime1Fx.Snow snow, Vector3 cam, Vector3 right, Vector3 up, Vector3 fwd)
    {
        if (!FxGl()) return;
        float s = lib.Look.Scale, field = P1SnowField * s;
        // the time since the last sim step, so the flakes move smoothly at any frame rate
        float between = MathF.Min(1 / 60f, (float)System.Diagnostics.Stopwatch.GetElapsedTime(_p1TickAt).TotalSeconds);
        float t = _p1Time + between;
        // the field's corner: centred ahead of the camera across the ground, on the camera up and down
        var flat = new Vector3(fwd.X, 0, fwd.Z);
        if (flat.LengthSquared > 1e-6f) flat.Normalize();
        Vector3 centre = cam + flat * (P1SnowAhead * s);
        Vector3 corner = centre - new Vector3(field / 2);
        bool under = PlayerEntity.HostWater is { } water && InWater(water, cam);
        float bright = under ? 0.5f : 1;
        float fogNear = P1SnowFogNear * s, fogFar = P1SnowFogFar * s, half = P1SnowFlake * s / 2;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Vector3 ax = right * half, ay = up * half;
        int n = 0;
        for (int i = 0; i < lib.Flakes.Length; i++)
        {
            // the flake's place: its own, fallen, blown, wrapped into the field
            Vector3 f = lib.Flakes[i] * s + _p1Drift - new Vector3(0, lib.Fall[i] * s * t, 0) - corner;
            f.X -= MathF.Floor(f.X / field) * field;
            f.Y -= MathF.Floor(f.Y / field) * field;
            f.Z -= MathF.Floor(f.Z / field) * field;
            Vector3 at = corner + f, d = at - cam;
            float ahead = Vector3.Dot(d, fwd);
            if (ahead <= 0) continue;
            float dist = d.Length;
            if (dist >= fogFar) continue;
            if (Roofed(snow, at)) continue;
            float a = bright * MathF.Min(1, (fogFar - dist) / (fogFar - fogNear));
            Quad(ref n, vp, at - ax + ay, at + ax + ay, at - ax - ay, at + ax - ay, new Vector4(1, 1, 1, a));
        }
        if (n == 0) return;
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        DrawRun(lib.FlakeTex, additive: true, n);
        // MphRead's own drawing state back, as DrawDroneFx leaves it
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
    }

    static bool InWater((Vector3 Min, Vector3 Max)[] water, Vector3 c)
    {
        foreach ((Vector3 min, Vector3 max) in water)
        {
            if (c.X >= min.X && c.X <= max.X && c.Z >= min.Z && c.Z <= max.Z && c.Y >= min.Y && c.Y < max.Y) return true;
        }
        return false;
    }

    // under something: below the first surface straight above the ground (the import's roof grid, heights x 8)
    static bool Roofed(Prime1Fx.Snow snow, Vector3 at)
    {
        if (snow.Roof.Length == 0) return false;
        int x = (int)MathF.Floor((at.X - snow.RoofX0) / snow.RoofCellSize), z = (int)MathF.Floor((at.Z - snow.RoofZ0) / snow.RoofCellSize);
        if (x < 0 || z < 0 || x >= snow.RoofW || z >= snow.RoofH) return false;
        short h = snow.Roof[z * snow.RoofW + x];
        return h != Prime1Fx.NoRoof && at.Y * 8 < h;
    }

    // DrawArenaUnderwater (CampaignArena.cs), the camera under water in a Prime 1 room: its InsideFogColor over the view
    // and the multiply filter; false: not a Prime 1 room (Echoes' own tint then)
    bool DrawPrime1Underwater()
    {
        if (_p1Fx is not { } lib || lib.Look.Fog is not { Length: >= 3 } fog) return false;
        GLES30.GlDisable(GLES30.GlDepthTest);
        GLES30.GlUseProgram(_hudProgram);
        GLES30.GlBindVertexArray(_hudVao);
        GLES30.GlUniform1f(HU("view_width"), _width);
        GLES30.GlUniform1f(HU("view_height"), _height);
        GLES30.GlUniform4f(HU("fade_color"), fog[0], fog[1], fog[2], UnderwaterTint);
        DrawHudQuad(-1, 1, 1, -1);
        // the multiply: the view times the water's colour (toward white by P1MultiplyStrength)
        float k = P1MultiplyStrength;
        GLES30.GlBlendFunc(GLES30.GlDstColor, GLES30.GlZero);
        GLES30.GlUniform4f(HU("fade_color"), 1 - k + k * fog[0], 1 - k + k * fog[1], 1 - k + k * fog[2], 1);
        DrawHudQuad(-1, 1, 1, -1);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlUniform4f(HU("fade_color"), 0, 0, 0, 0);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlEnable(GLES30.GlDepthTest);
        return true;
    }

    // the room's Prime 1 look (hd/prime1_fx.json, hd/prime1_fx/), null without it (every room but the Prime 1 ones)
    Prime1FxLib? LoadPrime1Fx()
    {
        if (_host == null) return null;
        RoomMetadata? meta = Metadata.GetRoomById(_host.RoomId, noThrow: true);
        if (meta == null || !EchoesArena.IsEchoesRoom(meta.Name) || meta.Archive is not string archive) return null;
        string dir = EchoesArena.HdDir(meta.Name) ?? Path.Combine(Paths.FileSystem, "_archives", archive, ArenaHdWriter.Dir);
        if (Prime1Fx.LoadLook(dir) is not { } look) return null;
        string fxDir = Path.Combine(dir, Prime1Fx.FxDir);
        if (!Directory.Exists(fxDir)) return null;
        var lib = new Prime1FxLib { Look = look };
        try
        {
            lib.Parts = RetroParticles.Load(fxDir);
            lib.Parts.Prime1Rules = true;
        }
        catch (Exception x)
        {
            Log.Warn("MPHArena", $"prime1 fx: effects not read ({x.Message})");
            return null;
        }
        foreach (string png in Directory.GetFiles(fxDir, "*.png"))
        {
            if (!UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) continue;
            _fxTexFile.TryAdd(t, png);
            _fxTexRepeat.Add(t);
        }
        foreach ((string role, uint id) in look.Fx)
        {
            if (lib.Parts.Get(id) != null) lib.Fx[role] = id;
        }
        foreach (Prime1Fx.Sound snd in look.Sounds)
        {
            string wav = Path.Combine(fxDir, Prime1Fx.SoundFile(snd.Role));
            if (File.Exists(wav) && MusyX.ReadWav(wav, out int rate) is short[] pcm && pcm.Length > 0) lib.Sounds[snd.Role] = (pcm, rate);
        }
        if (lib.Sounds.TryGetValue("crate_break", out var brk) || lib.Fx.ContainsKey("crate_flash"))
        {
            lib.Crates = new PillarFxLib { Parts = lib.Parts };
            if (brk.Pcm != null) lib.Crates.Sounds["crate_break"] = (brk.Pcm, brk.Rate, 1f);
        }
        if (look.Snow is { FxType: 1 } snow && snow.Flake != ""
            && UInt32.TryParse(Path.GetFileNameWithoutExtension(snow.Flake), System.Globalization.NumberStyles.HexNumber, null, out uint flake))
        {
            lib.FlakeTex = flake;
            // the flakes: spread through the field (m), each its own fall speed (2-4 m/s); the same every time
            int count = Math.Clamp((int)(P1SnowMax * snow.Density), 0, P1SnowMax);
            var rng = new Random(0x5e0f);
            lib.Flakes = new Vector3[count];
            lib.Fall = new float[count];
            for (int i = 0; i < count; i++)
            {
                lib.Flakes[i] = new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * P1SnowField;
                lib.Fall[i] = 2 + 2 * rng.NextSingle();
            }
        }
        Log.Info("MPHArena", $"prime1 fx: effects ({string.Join(", ", lib.Fx.Keys)}), sounds ({string.Join(", ", lib.Sounds.Keys)}), "
            + (lib.Flakes.Length > 0 ? $"snow ({lib.Flakes.Length} flakes)" : "no snow"));
        return lib;
    }
}
