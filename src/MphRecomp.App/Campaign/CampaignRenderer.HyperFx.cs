using System;
using System.Collections.Generic;
using System.IO;
using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Sound;
using MphRecomp.Campaign;
using MphRecomp.Import.Effects;
using MphRecomp.Import.Retro;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Dark Samus's Hyper Ball in Corruption's own look and sound (owner 2026-10-12: "let's get the real beams and ball
// effect"; docs/DARK_SAMUS_HYPER.md H4): her body's hyper/ folder (Core Import/Effects/HyperFx.cs, from the player's own
// Corruption disc, Trilogy or standalone) gives PhazonBallSpikes (the blue lightning: arcs from the ball out to the
// floor and walls around it, as many as there is surface near it; owner 10-12: "all of the arcs hit the ground and just
// occasional larger ones reach further"), SpikesAI (the red lightning to each target in reach as it is hurt; owner
// 10-12: "The red version is what actually arcs out and locks on to an enemy in the area of effect"; an import from
// before it was a role reaches them with the blue), PhazonBallContactPoints (a glow where each arc lands),
// PhazonBallParams (the arcs' odds by distance), BallInnerGlow (a Corruption particle system, Core
// Campaign/RetroParticles.cs) inside it, and its sound: the Hyper Ball hum while it runs (Corruption's code plays only that; its
// HyperballImpact is loaded but never played). Where the arcs go is Corruption's player code, read from the game (SurfaceArcs,
// RedArc; docs/MP3_HYPER_EFFECTS_FORMATS.md section 3).
// MphRead's stand-in lightning and sounds are then not drawn or played (PlayerEntity.HostOwnHyperBallLook / Sfx).
// Drawing and sound only: the Hyper Ball's reach, targets and damage are the simulation's (PlayerHyperBall.cs).
internal sealed partial class CampaignRenderer
{
    const float HyperSurfaceBox = 2;       // the blue arcs' surfaces: triangles touching a box this many units around the ball
    const float HyperRedCooldown = 0.032f; // a target's wait between red arcs, seconds (Corruption's TweakBall +0x270)
    const int HyperSfxRange = 1;           // MPH's sound range for another player
    const float HyperSfxVolume = 1f;

    sealed class HyperLib
    {
        public RetroParticles Parts = null!;
        public Elsc? Spikes, Bolts;
        public float[]? Odds;                  // PhazonBallParams: the blue arcs' odds by distance
        public HyperFx.Config Cfg = null!;
        public readonly Dictionary<string, List<(short[] Pcm, int Rate, float Volume)>> Sounds = new(StringComparer.Ordinal);
    }

    sealed class HyperSeat
    {
        public RetroElectric? Arcs, Reds;      // the blue to the surfaces, the red to the targets
        public RetroParticleSystem? Glow, Contacts;
        public object? Hum;
        public float Area;
        public bool Was;
        public readonly float[] RedWait = new float[16]; // per target slot: seconds to its next red arc
        public readonly SoundSource Sound = new();
    }

    readonly Dictionary<CampaignRetroBody, HyperLib?> _hyperLibs = new();
    readonly Dictionary<PlayerEntity, HyperSeat> _hyperSeats = new();
    readonly List<RetroElectric.Line> _hyperLines = new();
    readonly List<RetroBillboard> _hyperBoards = new();
    readonly Random _hyperRand = new(7);
    readonly HashSet<(MphCollisionInfo, int)> _hyperSeenTris = new();

    HyperLib? HyperOf(CampaignRetroBody body)
    {
        if (_hyperLibs.TryGetValue(body, out HyperLib? lib)) return lib;
        lib = null;
        try
        {
            if (HyperFx.Config.Load(body.Body.Dir) is HyperFx.Config cfg)
            {
                string dir = Path.Combine(body.Body.Dir, HyperFx.Folder);
                lib = new HyperLib { Cfg = cfg, Parts = RetroParticles.Load(dir, mp3: true) };
                if (cfg.Roles.TryGetValue("ballspikes", out var sp) && File.Exists(Path.Combine(dir, $"{sp.Id:x8}.elsc")))
                {
                    lib.Spikes = Elsc.Load(File.ReadAllBytes(Path.Combine(dir, $"{sp.Id:x8}.elsc")), mp3: true);
                }
                if (cfg.Roles.TryGetValue("ballbolts", out var bo) && File.Exists(Path.Combine(dir, $"{bo.Id:x8}.elsc")))
                {
                    lib.Bolts = Elsc.Load(File.ReadAllBytes(Path.Combine(dir, $"{bo.Id:x8}.elsc")), mp3: true);
                }
                if (cfg.Roles.TryGetValue("ballparams", out var pa) && File.Exists(Path.Combine(dir, $"{pa.Id:x8}.usrc")))
                {
                    lib.Odds = HyperFx.Curve(File.ReadAllBytes(Path.Combine(dir, $"{pa.Id:x8}.usrc")));
                }
                foreach ((string role, var list) in cfg.Sounds)
                {
                    var l = new List<(short[], int, float)>();
                    foreach ((int n, float vol, bool _) in list)
                    {
                        if (MusyX.ReadWav(Path.Combine(dir, HyperFx.SoundFile(role, n)), out int rate) is short[] pcm && pcm.Length > 0) l.Add((pcm, rate, vol));
                    }
                    if (l.Count > 0) lib.Sounds[role] = l;
                }
                foreach (string png in Directory.GetFiles(dir, "*.png"))
                {
                    if (UInt32.TryParse(Path.GetFileNameWithoutExtension(png), System.Globalization.NumberStyles.HexNumber, null, out uint t)) _fxTexFile.TryAdd(t, png);
                }
                Log.Info("MPHCampaign", $"body {body.Id}: Corruption Hyper effects: {lib.Parts.Count} particle systems, spikes {(lib.Spikes != null ? "yes" : "no")}, lock-on {(lib.Bolts != null ? "yes" : "no")}, arc odds {(lib.Odds != null ? "yes" : "no")}, {lib.Sounds.Count} sounds");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("MPHCampaign", $"body {body.Id}: its Hyper effects failed to load, MPH's stand-in drawn: {ex.Message}");
            lib = null;
        }
        _hyperLibs[body] = lib;
        return lib;
    }

    // TickSkins (MatchSkins.cs), after each sim step
    void TickHyperFx()
    {
        foreach (PlayerEntity p in PlayerEntity.Players)
        {
            if (p == null || !p.HostHyperBall) continue;
            HyperLib? lib = BodyOf(p) is CampaignRetroBody body ? HyperOf(body) : null;
            p.HostOwnHyperBallLook = lib?.Spikes != null;
            p.HostOwnHyperBallSfx = lib != null && _sfx != null && lib.Sounds.ContainsKey("ballloop");
            if (lib == null)
            {
                EndHyper(p);
                continue;
            }
            if (!_hyperSeats.TryGetValue(p, out HyperSeat? seat)) _hyperSeats[p] = seat = new HyperSeat();
            bool active = p.IsAltForm && p.HyperBallActive;
            Vector3 center = p.Volume.SpherePosition;
            Matrix4 t = PrimeToModel * Matrix4.CreateScale(NativeBodyScale(p)) * Matrix4.CreateTranslation(center);
            seat.Sound.Update(center, HyperSfxRange);
            // the arcs' own emitters stay off: the game's code adds every arc (below)
            if (lib.Spikes != null) seat.Arcs ??= new RetroElectric(lib.Spikes, p.SlotIndex * 7919 + 1) { Emitting = false };
            if ((lib.Bolts ?? lib.Spikes) is Elsc red) seat.Reds ??= new RetroElectric(red, p.SlotIndex * 7919 + 2) { Emitting = false };
            if (active && !seat.Was)
            {
                uint glow = lib.Cfg.Id("ballglow");
                if (glow != 0 && lib.Parts.Get(glow) != null) seat.Glow = new RetroParticleSystem(lib.Parts, glow, t, follow: true, seed: p.SlotIndex + 11);
                uint contacts = lib.Cfg.Id("contacts");
                if (contacts != 0 && lib.Parts.Get(contacts) != null)
                {
                    seat.Contacts ??= new RetroParticleSystem(lib.Parts, contacts, t, follow: false, seed: p.SlotIndex + 13);
                }
                if (p.HostOwnHyperBallSfx && lib.Sounds.TryGetValue("ballloop", out var hum))
                {
                    seat.Hum = _sfx!.PlayPcmLoop(hum[0].Pcm, hum[0].Rate, HyperSfxVolume * hum[0].Volume, seat.Sound);
                }
                seat.Area = 0;
                Array.Clear(seat.RedWait);
            }
            else if (!active && seat.Was)
            {
                StopHyper(seat);
            }
            seat.Was = active;
            foreach (RetroElectric? e in new[] { seat.Arcs, seat.Reds })
            {
                if (e != null) e.Transform = t;
            }
            if (active)
            {
                if (p.HyperBallSimFx)
                {
                    // the simulation ran Corruption's rules here (PlayerHyperBall.cs): its arcs and its zap, as they are
                    if (seat.Arcs != null) SimArcs(seat, p, t);
                    if (p.HyperBallZapped is PlayerEntity zapped && seat.Reds != null) RedArc(seat, center, zapped, t);
                }
                else
                {
                    // another player's ball online, or older rules: the same rules, drawn here (no kick, no damage)
                    if (seat.Arcs != null && _host?.Scene is Scene scene) SurfaceArcs(seat, lib, scene, center, t);
                    // Corruption's red: each target zapped waits HyperRedCooldown (TweakBall, seconds, less a 60 Hz
                    // frame's each frame), and one red arc a frame goes to the nearest target that isn't waiting: about
                    // one every 2 frames on a lone target, so ~5 live on it at once
                    for (int s = 0; s < seat.RedWait.Length; s++) seat.RedWait[s] -= 1 / 60f;
                    for (int i = 0; i < 2; i++)
                    {
                        if (p.HyperBallTarget(i) is not PlayerEntity target) continue;
                        ref float wait = ref seat.RedWait[target.SlotIndex % seat.RedWait.Length];
                        if (wait <= 0 && seat.Reds != null)
                        {
                            RedArc(seat, center, target, t);
                            wait = HyperRedCooldown;
                            break;
                        }
                    }
                }
                if (seat.Hum != null) _sfx?.MovePcm(seat.Hum, HyperSfxVolume * lib.Sounds["ballloop"][0].Volume, seat.Sound);
            }
            seat.Arcs?.Step();
            seat.Reds?.Step();
            if (seat.Contacts != null)
            {
                seat.Contacts.Transform = t;
                seat.Contacts.Step();
            }
            if (seat.Glow != null)
            {
                seat.Glow.Transform = t;
                seat.Glow.Step();
                if (seat.Glow.Done) seat.Glow = null;
            }
        }
        // seats that left
        List<PlayerEntity>? gone = null;
        foreach (PlayerEntity p in _hyperSeats.Keys)
        {
            if (!System.Linq.Enumerable.Contains(PlayerEntity.Players, p) || !p.HostHyperBall) (gone ??= new()).Add(p);
        }
        if (gone != null) foreach (PlayerEntity p in gone) EndHyper(p);
    }

    float NativeBodyScale(PlayerEntity p) => BodyOf(p)?.Body.Scale ?? 1;

    // Corruption's rule for the blue arcs (its player code, read from the game; docs/MP3_HYPER_EFFECTS_FORMATS.md
    // section 3): every surface triangle touching a box 2 units around the ball adds its area to a running sum; each
    // 1/(GRAT dt) of it buys a try at an arc to a random point on that triangle, kept with PhazonBallParams' odds for its
    // distance (sure from 0.3 to 2.9 units, then fading out by 5). An arc starts on the ball's shell (IEMT), and lands
    // on the point heading in along the surface's normal (FEMT's cone about it). The live arcs at SCNT: the sum is
    // dropped. So more surface near the ball = more arcs, nearly all to the floor and walls right around it.
    void SurfaceArcs(HyperSeat seat, HyperLib lib, Scene scene, Vector3 center, Matrix4 t)
    {
        RetroElectric arcs = seat.Arcs!;
        float unit = Vector3.TransformVector(Vector3.UnitX, t).Length;
        if (unit < 1e-6f) return;
        float per = 1 / (Math.Max(1e-3f, arcs.Rate) / 60f);   // area a try costs (dt = a 60 Hz frame)
        var box = new Vector3(HyperSurfaceBox * unit);
        Vector3 lo = center - box, hi = center + box;
        IReadOnlyList<CollisionCandidate> cands = CollisionDetection.GetCandidatesForLimits(null, Vector3.Zero, 0, lo, hi,
            includeEntities: false, scene);
        Matrix4 toEffect = t.Inverted();
        _hyperSeenTris.Clear();
        foreach (CollisionCandidate cand in cands)
        {
            if (cand.Collision.Info is not MphCollisionInfo info || cand.EntityCollision != null) continue;
            Vector3 off = cand.Collision.Translation;
            for (int j = 0; j < cand.Entry.DataCount; j++)
            {
                int index = info.DataIndices[cand.Entry.DataStartIndex + j];
                if (!_hyperSeenTris.Add((info, index))) continue;
                CollisionData data = info.Data[index];
                Vector3 normal = info.Planes[data.PlaneIndex].Xyz;
                Vector3 a = info.Points[info.PointIndices[data.PointStartIndex]] + off;
                for (int k = 1; k + 1 < data.PointIndexCount; k++)
                {
                    Vector3 b = info.Points[info.PointIndices[data.PointStartIndex + k]] + off;
                    Vector3 c = info.Points[info.PointIndices[data.PointStartIndex + k + 1]] + off;
                    if (Vector3.ComponentMax(Vector3.ComponentMax(a, b), c).X < lo.X || Vector3.ComponentMin(Vector3.ComponentMin(a, b), c).X > hi.X
                        || Vector3.ComponentMax(Vector3.ComponentMax(a, b), c).Y < lo.Y || Vector3.ComponentMin(Vector3.ComponentMin(a, b), c).Y > hi.Y
                        || Vector3.ComponentMax(Vector3.ComponentMax(a, b), c).Z < lo.Z || Vector3.ComponentMin(Vector3.ComponentMin(a, b), c).Z > hi.Z)
                    {
                        continue;
                    }
                    seat.Area += Vector3.Cross(b - a, c - a).Length / 2 / (unit * unit);
                    int n = (int)(_hyperRand.NextSingle() * 2 * seat.Area / per);
                    for (int m = 0; m < n; m++)
                    {
                        if (arcs.Live >= arcs.Most)
                        {
                            seat.Area = 0;
                            return;
                        }
                        seat.Area -= per;
                        float u = _hyperRand.NextSingle(), v = _hyperRand.NextSingle();
                        Vector3 at = a * u + b * ((1 - u) * v) + c * ((1 - u) * (1 - v));
                        float dist = (at - center).Length / unit;
                        if (_hyperRand.NextSingle() > HyperArcOdds(lib, dist)) continue;
                        Vector3 nrm = Vector3.TransformVector(normal, toEffect).Normalized();
                        (Vector3 p0, Vector3 v0) = arcs.Start();
                        Vector3 v1 = FromUp(arcs.End().Vel, nrm);
                        Vector3 fixedAt = at;
                        int life = arcs.Spawn(p0, v0, () => fixedAt, v1);
                        if (life > 1) seat.Contacts?.EmitAt(at + normal * (0.01f * unit), life - 1);
                    }
                }
            }
        }
    }

    // the arcs the simulation added this frame (Corruption's rule run there, so the arcs that kick the ball are the ones
    // drawn): each from the ball's shell (IEMT) with its start velocity, held on its point, ending along the surface's
    // normal (FEMT's cone about it), with a glow where it lands
    void SimArcs(HyperSeat seat, PlayerEntity p, Matrix4 t)
    {
        RetroElectric arcs = seat.Arcs!;
        float unit = Vector3.TransformVector(Vector3.UnitX, t).Length;
        Matrix4 toEffect = t.Inverted();
        for (int i = 0; i < p.HyperBallNewArcs; i++)
        {
            PlayerEntity.HyperArc arc = p.HyperBallNewArc(i);
            (Vector3 p0, _) = arcs.Start();
            Vector3 v0 = Vector3.TransformVector(arc.Vel * unit, toEffect);
            Vector3 nrm = Vector3.TransformNormal(arc.Normal, toEffect).Normalized();
            Vector3 v1 = FromUp(arcs.End().Vel, nrm);
            Vector3 at = arc.End;
            int life = arcs.Spawn(p0, v0, () => at, v1, arc.Life);
            if (life > 1) seat.Contacts?.EmitAt(at + arc.Normal * (0.01f * unit), life - 1);
        }
    }

    // PhazonBallParams: an arc's odds by its distance, a key a 0.1 unit (an import from before it was a role: sure to 3)
    static float HyperArcOdds(HyperLib lib, float dist)
    {
        if (lib.Odds == null) return dist >= 0.3f && dist < 3 ? 1 : 0;
        int i = (int)(dist * 10);
        return i >= 0 && i < lib.Odds.Length ? lib.Odds[i] : 0;
    }

    // a red arc as Corruption adds one (MP3 main.dol 0x80101e6c): from the ball's shell to the centre of the target's
    // bounds, held on it as it moves, its end velocity FEMT's cone about ball -> target (so it runs past the target and
    // curls back in); no glow where it lands (the red system has no ContactPoints)
    void RedArc(HyperSeat seat, Vector3 center, PlayerEntity target, Matrix4 t)
    {
        RetroElectric reds = seat.Reds!;
        Vector3 toward = target.Volume.SpherePosition - center;
        Vector3 hitDir = toward.LengthSquared > 1e-8f ? toward.Normalized() : Vector3.UnitY;
        (Vector3 p0, Vector3 v0) = reds.Start();
        Vector3 on = Vector3.TransformVector(hitDir, t.Inverted());
        Vector3 v1 = FromUp(reds.End().Vel, on.LengthSquared > 1e-12f ? on.Normalized() : Vector3.UnitZ);
        reds.Spawn(p0, v0, () => target.Volume.SpherePosition, v1);
    }

    // a vector given about +Z turned to sit the same way about a normal (effect space)
    static Vector3 FromUp(Vector3 v, Vector3 normal)
    {
        Vector3 axis = Vector3.Cross(Vector3.UnitZ, normal);
        float dot = Math.Clamp(normal.Z, -1, 1);
        if (axis.LengthSquared < 1e-10f) return dot >= 0 ? v : -v;
        return Vector3.Transform(v, Quaternion.FromAxisAngle(axis.Normalized(), MathF.Acos(dot)));
    }

    void StopHyper(HyperSeat seat)
    {
        if (seat.Glow != null) seat.Glow.Emitting = false;
        _sfx?.StopPcm(seat.Hum);
        seat.Hum = null;
    }

    void EndHyper(PlayerEntity p)
    {
        if (_hyperSeats.Remove(p, out HyperSeat? seat)) StopHyper(seat);
    }

    void ResetHyperFx()
    {
        foreach (HyperSeat seat in _hyperSeats.Values) StopHyper(seat);
        _hyperSeats.Clear();
        _hyperLibs.Clear();
    }

    // DrawSkins (MatchSkins.cs), additive pass: the lightning's lines, the glow's particles
    void DrawHyperFx(Scene scene, SuitPass pass)
    {
        if (pass != SuitPass.Additive || _host == null || scene != _host.Scene || _hyperSeats.Count == 0) return;
        _hyperLines.Clear();
        _hyperBoards.Clear();
        foreach (HyperSeat seat in _hyperSeats.Values)
        {
            seat.Arcs?.Collect(_hyperLines);
            seat.Reds?.Collect(_hyperLines);
            seat.Glow?.Collect(_hyperBoards);
            seat.Contacts?.Collect(_hyperBoards);
        }
        if (_hyperLines.Count == 0 && _hyperBoards.Count == 0) return;
        if (!FxGl()) return;
        Vector3 cam = scene.CameraPosition;
        Matrix4 vp = scene.ViewMatrix * scene.PerspectiveMatrix;
        Matrix4 view = scene.ViewMatrix;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        float pxToWorld = 2f / 528f / MathF.Max(1e-3f, scene.PerspectiveMatrix.M22); // a 528-line pixel at distance 1
        GLES30.GlUseProgram(_fxProg);
        GLES30.GlBindVertexArray(_fxVao);
        GLES30.GlBindBuffer(GLES30.GlArrayBuffer, _fxVbo);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlUniform1i(GLES30.GlGetUniformLocation(_fxProg, "uTex"), 0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlEnable(GLES30.GlDepthTest);
        GLES30.GlDepthMask(false);
        GLES30.GlDisable(CullFaceCap);
        // the glow's particles, grouped by texture
        _hyperBoards.Sort((a, b) => a.Additive != b.Additive ? (a.Additive ? 1 : -1) : a.Texture.CompareTo(b.Texture));
        int i = 0;
        while (i < _hyperBoards.Count)
        {
            RetroBillboard first = _hyperBoards[i];
            int n = 0, j = i;
            for (; j < _hyperBoards.Count && _hyperBoards[j].Additive == first.Additive && _hyperBoards[j].Texture == first.Texture; j++)
            {
                RetroBillboard bb = _hyperBoards[j];
                float near = bb.Size > 1e-4f ? ((bb.Position - cam).Length / bb.Size - FxNearGone) / (FxNearWhole - FxNearGone) : 1;
                Vector4 col = bb.Color;
                col.W *= Math.Clamp(near, 0, 1);
                if (col.W <= 0.002f) continue;
                float rad = MathHelper.DegreesToRadians(bb.Rotation);
                float c = MathF.Cos(rad) * bb.Size / 2, s = MathF.Sin(rad) * bb.Size / 2;
                Vector3 ax = right * c + up * s, ay = -right * s + up * c;
                Quad(ref n, vp, bb.Position - ax + ay, bb.Position + ax + ay, bb.Position - ax - ay, bb.Position + ax - ay, col, uv: bb.Uv);
            }
            DrawRun(first.Texture, first.Additive, n);
            i = j;
        }
        // the lightning as Corruption draws it (MP3 main.dol RenderLines 0x803a2a14, BLDM 1): hard-edged lines LWDn pixels
        // wide on its 528-line framebuffer, each LCLn's colour added whole (One/One: no alpha scaling, all three lines at
        // full strength); a TEXR runs along the line only (the same texel across its width), times the colour
        _hyperLines.Sort((a, b) => a.Texture.CompareTo(b.Texture));
        for (int li = 0; li < _hyperLines.Count;)
        {
            uint tex = _hyperLines[li].Texture;
            int n = 0;
            for (; li < _hyperLines.Count && _hyperLines[li].Texture == tex; li++)
            {
                RetroElectric.Line line = _hyperLines[li];
                var col = new Vector4(line.Color.Xyz, 1);
                Vector3[] pts = line.Points;
                float du = 1f / Math.Max(1, pts.Length - 1);
                // each point's side from its neighbours, so the segments join at the bends
                Vector3 Side(int k)
                {
                    Vector3 at = pts[k], dir = pts[Math.Min(k + 1, pts.Length - 1)] - pts[Math.Max(k - 1, 0)];
                    Vector3 sd = Vector3.Cross(dir, cam - at);
                    float half = line.WidthPx * 0.5f * pxToWorld * (cam - at).Length;
                    return (sd.LengthSquared > 1e-10f ? sd.Normalized() : right) * half;
                }
                Vector3 sa = Side(0);
                for (int k = 0; k + 1 < pts.Length; k++)
                {
                    Vector3 a = pts[k], b = pts[k + 1], sb = Side(k + 1);
                    // u and v both run 0 -> 1 along the line (the game's UV rect start to end), not across it
                    float u0 = k * du, u1 = (k + 1) * du;
                    if ((n + 6) * 10 > _fxVerts.Length) Array.Resize(ref _fxVerts, _fxVerts.Length * 2);
                    Vert(ref n, vp, a + sa, (u0, u0), col); Vert(ref n, vp, b + sb, (u1, u1), col); Vert(ref n, vp, a - sa, (u0, u0), col);
                    Vert(ref n, vp, a - sa, (u0, u0), col); Vert(ref n, vp, b + sb, (u1, u1), col); Vert(ref n, vp, b - sb, (u1, u1), col);
                    sa = sb;
                }
            }
            if (n > 0) DrawRun(tex, additive: true, n, white: tex == 0 || FxTexture(tex) is not int id || id == 0);
        }
        // MphRead's own drawing state back, as DrawSuit leaves it
        GLES30.GlDepthMask(true);
        GLES30.GlUseProgram(_program);
        GLES30.GlBindVertexArray(0);
        GLES30.GlActiveTexture(GLES30.GlTexture0);
        GLES30.GlEnable(GLES30.GlBlend);
        GLES30.GlBlendFunc(GLES30.GlSrcAlpha, GLES30.GlOneMinusSrcAlpha);
        GLES30.GlDisable(CullFaceCap);
    }
}
