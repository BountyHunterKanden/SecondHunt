using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Arenas;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// HOLIDAY MODE's presents (owner 2026-11-20): Echoes' weapon crates and Main Quarry's breakable crates draw as wrapped
// presents (ArenaHdDraw.DrawPresents) and burst into paper and ribbon bits when they break. Draw only: the crates' sim
// (collision, hits, break timing, drops, the Echoes crates' own death effect) is untouched, and the bits fly on their
// own Random and the wall clock, so matches, replays and netcode are the same with it on or off.
internal sealed partial class CampaignRenderer
{
    readonly List<ArenaHdDraw.Present> _presents = new();
    readonly Dictionary<EchoesCrateEntity, int> _presentBreaks = new();
    readonly Dictionary<Vector3, float> _presentSizes = new();   // each present's smaller side, for its bits
    readonly List<PresentBit> _presentBits = new();
    readonly Random _presentRandom = new();
    double _presentBitsAt = -1;

    sealed class PresentBit
    {
        public Vector3 At, Vel, Spin, Turn;
        public Vector3 Half;
        public float Age, Life, Fall;
        public int Style;
        public bool Ribbon;
    }

    const string MainQuarryKey = "P1_MainQuarry";   // MainQuarry.Key (Mining Plaza's sand crates are plants, not presents)

    bool IsMainQuarry() => _host != null && EchoesArena.All.FirstOrDefault(d => d.Id == _host.RoomId)?.Key == MainQuarryKey;

    // a present in the crate's place: its pass's model box, through the crate's matrix; its wrapping picked by where it
    // stands (the same present every match)
    void AddPresent(ArenaHdDraw hd, Matrix4 model, int pass, bool flash, CrateLight? light, float fade, Vector3 at)
    {
        (Vector3 lo, Vector3 hi) = hd.PassBox(pass);
        // (and by its pass: one Main Quarry crate is a stack of pieces, each its own present)
        int style = (int)(((uint)MathF.Round(at.X * 7) * 73856093u) ^ ((uint)MathF.Round(at.Z * 7) * 19349663u) ^ ((uint)MathF.Round(at.Y * 7) * 83492791u)
            ^ ((uint)pass * 2654435761u));
        _presents.Add(new ArenaHdDraw.Present(model, lo, hi, flash, light, fade, style));
        Vector3 size = Vector3.TransformVector(hi - lo, model);
        _presentSizes[at] = MathF.Max(0.2f, MathF.Min(MathF.Abs(size.X), MathF.Abs(size.Z)));
    }

    // a present breaking: paper squares and ribbon strips thrown up and out, tumbling, drifting down, fading
    void BurstPresent(Vector3 at)
    {
        float s = _presentSizes.TryGetValue(at, out float k) ? k : 1f;
        int style = _presentRandom.Next(ArenaHdDraw.Styles.Length);
        foreach ((Vector3 key, float size) in _presentSizes)
        {
            if ((key - at).LengthSquared < 0.01f) { s = size; break; }
        }
        float R(float lo, float hi) => lo + (float)_presentRandom.NextDouble() * (hi - lo);
        for (int i = 0; i < 24; i++)
        {
            bool ribbon = i >= 18;
            float a = R(0, MathF.Tau), out_ = R(0.6f, 1.6f) * s, up = R(1.2f, 3.0f) * s;
            float side = ribbon ? s * 0.07f : s * R(0.07f, 0.12f);
            _presentBits.Add(new PresentBit
            {
                At = at + new Vector3(MathF.Cos(a), R(-0.3f, 0.3f), MathF.Sin(a)) * s * 0.3f,
                Vel = new Vector3(MathF.Cos(a) * out_, up, MathF.Sin(a) * out_),
                Spin = new Vector3(R(-9, 9), R(-9, 9), R(-9, 9)),
                Turn = new Vector3(R(0, MathF.Tau), R(0, MathF.Tau), R(0, MathF.Tau)),
                Half = ribbon ? new Vector3(side * 3.5f, side * 0.08f, side) : new Vector3(side, side * 0.08f, side * R(0.7f, 1f)),
                Life = R(1.2f, 2.0f),
                Fall = s * R(2.0f, 3.0f),
                Style = style,
                Ribbon = ribbon,
            });
        }
    }

    // Echoes' crates breaking (their Breaks count, synced online; one first seen already broken bursts nothing), then
    // the bits' flight on the wall clock (DrawCrates runs for both opaque passes; the second sees no time pass)
    void AddPresentBits(Scene scene, List<ArenaHdDraw.Present> list)
    {
        foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities())
        {
            if (e is not EchoesCrateEntity c) continue;
            if (_presentBreaks.TryGetValue(c, out int seen) && c.Breaks > seen) BurstPresent(c.Position);
            _presentBreaks[c] = c.Breaks;
        }
        double now = _clock.Elapsed.TotalSeconds;
        float dt = _presentBitsAt < 0 ? 0 : (float)Math.Clamp(now - _presentBitsAt, 0, 0.1);
        _presentBitsAt = now;
        for (int i = _presentBits.Count - 1; i >= 0; i--)
        {
            PresentBit b = _presentBits[i];
            b.Age += dt;
            if (b.Age >= b.Life) { _presentBits.RemoveAt(i); continue; }
            // paper flutters: strong drag, light gravity, a little sideways sway
            float drag = MathF.Exp(-2.2f * dt);
            b.Vel = b.Vel * drag + new Vector3(MathF.Sin(b.Turn.X * 3), -b.Fall, MathF.Cos(b.Turn.Z * 3)) * dt;
            b.At += b.Vel * dt;
            b.Turn += b.Spin * dt;
            float fade = 1 - MathF.Max(0, (b.Age - b.Life * 0.6f) / (b.Life * 0.4f));
            Matrix4 m = Matrix4.CreateRotationX(b.Turn.X) * Matrix4.CreateRotationY(b.Turn.Y) * Matrix4.CreateRotationZ(b.Turn.Z)
                * Matrix4.CreateTranslation(b.At);
            list.Add(new ArenaHdDraw.Present(m, -b.Half, b.Half, false, null, fade >= 1 ? 1 : -fade, b.Style, bit: true, ribbonBit: b.Ribbon));
        }
    }
}
