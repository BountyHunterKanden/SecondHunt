using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Effects;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Holiday
{
    // HOLIDAY MODE's breath (owner 2026-11-20: "I like the breath puff idea too but only if outside"): every few seconds a
    // small puff of the game's own smoke in front of each hunter's visor, only while the sky above them is open. A cosmetic
    // effect (Renderer.HostCosmetic.cs: its own pools and random numbers), so play, replays and online are unchanged.
    public static class HolidayBreath
    {
        static int _puff = 39; // steamSamusShip
        const float Every = 3.2f, Spread = 1.4f; // seconds between puffs: Every to Every + Spread
        const float Ahead = 0.45f, AheadMain = 0.7f, Down = 0.15f, DownMain = 0.3f;
        // steamSamusShip's steam lives a few seconds and grows into a cloud over the view; a breath plays its whole grow
        // and fade in this long instead (the particle's curves run over its Lifespan)
        static float _life = 1.2f;

        static readonly Random _random = new(); // (when puffs come: not the game's random numbers)
        static readonly Dictionary<int, float> _next = new();
        static readonly List<(EffectElementEntry Entry, float Born)> _puffs = new(); // (Born: a pool slot used again is someone else's)
        static Scene? _scene;

        public static void Frame(Scene scene, Func<Vector3, bool> roofAbove)
        {
            if (scene != _scene)
            {
                _scene = scene;
                _next.Clear();
                _puffs.Clear();
            }
            Shorten();
            float now = scene.ElapsedTime;
            IReadOnlyList<PlayerEntity> players = PlayerEntity.Players;
            for (int i = 0; i < players.Count && i < PlayerEntity.PlayerCount; i++)
            {
                PlayerEntity player = players[i];
                // (not your own: owner 2026-11-20, only other players' breath shows)
                if (player == null || !player.LoadFlags.TestFlag(LoadFlags.Active) || player.Health <= 0 || player.IsAltForm
                    || player.IsMainPlayer)
                {
                    continue;
                }
                if (!_next.TryGetValue(i, out float next) || next > now + Every + Spread)
                {
                    _next[i] = now + (float)_random.NextDouble() * Every; // (out of step with each other)
                    continue;
                }
                if (now < next)
                {
                    continue;
                }
                _next[i] = now + Every + (float)_random.NextDouble() * Spread;
                Vector3 head = player.CameraInfo.Position;
                if (roofAbove(head))
                {
                    continue;
                }
                Vector3 facing = player.CameraInfo.Facing;
                bool main = player.IsMainPlayer;
                Vector3 at = head + facing * (main ? AheadMain : Ahead) - new Vector3(0, main ? DownMain : Down, 0);
                if (scene.HostSpawnCosmeticEffect(_puff, Matrix4.CreateTranslation(at)) is EffectElementEntry puff)
                {
                    _puffs.Add((puff, puff.CreationTime));
                }
            }
        }

        static void Shorten()
        {
            for (int i = _puffs.Count - 1; i >= 0; i--)
            {
                (EffectElementEntry puff, float born) = _puffs[i];
                if (puff.CreationTime != born || (puff.Expired && puff.Particles.Count == 0))
                {
                    _puffs.RemoveAt(i);
                    continue;
                }
                foreach (EffectParticle p in puff.Particles)
                {
                    if (p.Lifespan > _life)
                    {
                        p.Lifespan = _life;
                        p.ExpirationTime = p.CreationTime + _life;
                    }
                }
            }
        }
    }
}
