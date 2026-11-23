using System;
using System.Collections.Generic;
using MphRead.Effects;
using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: host cosmetic effects (holiday mode's snow, MphRecomp.Holiday). They run through MphRead's own effect code
    // (ProcessEffects, the particle draw) but take their elements and particles from their own pools and their random
    // numbers from their own Rng1/Rng2 states. The game's pools and random numbers, so the simulation, replays and online
    // play, are the same with or without them. Draw-only; nothing here is in a replay, a snapshot or the netcode.
    public partial class Scene
    {
        private const int _cosmeticElementMax = 32;
        private const int _cosmeticParticleMax = 1800;
        private readonly Queue<EffectElementEntry> _cosmeticElements = new Queue<EffectElementEntry>();
        private readonly List<EffectElementEntry> _cosmeticActive = new List<EffectElementEntry>();
        private readonly Queue<EffectParticle> _cosmeticParticles = new Queue<EffectParticle>();
        private bool _cosmeticPass = false;
        private bool _cosmeticAllocated = false;
        private uint _cosmeticRng1 = 0x1D3C5B7AU;
        private uint _cosmeticRng2 = 0x6E8F0A2BU;
        private uint _gameRng1 = 0;
        private uint _gameRng2 = 0;

        // called once per processed frame (not while paused), before the cosmetic effects run: the host spawns, moves and
        // trims its effects here
        public static Action<Scene>? HostCosmeticFrame { get; set; }
        // called after the cosmetic effects have run, before they're drawn: the host sets what it owns (a flake's alpha and
        // size) over what the effect's own functions set
        public static Action<Scene>? HostCosmeticAfter { get; set; }

        public IReadOnlyList<EffectElementEntry> HostCosmeticElements => _cosmeticActive;
        public int HostCosmeticFreeParticles => _cosmeticAllocated ? _cosmeticParticles.Count : _cosmeticParticleMax;

        private void BeginCosmetic()
        {
            _gameRng1 = Rng.Rng1;
            _gameRng2 = Rng.Rng2;
            Rng.SetRng1(_cosmeticRng1);
            Rng.SetRng2(_cosmeticRng2);
            _cosmeticPass = true;
        }

        private void EndCosmetic()
        {
            _cosmeticPass = false;
            _cosmeticRng1 = Rng.Rng1;
            _cosmeticRng2 = Rng.Rng2;
            Rng.SetRng1(_gameRng1);
            Rng.SetRng2(_gameRng2);
        }

        // spawns a cosmetic effect (loaded on first use, kept loaded) and returns its last element, or null if the
        // cosmetic pool is full. Its elements are kept alive until their particles are gone, so a finished emitter's
        // flakes still land instead of vanishing with it
        public EffectElementEntry? HostSpawnCosmeticEffect(int effectId, Matrix4 transform)
        {
            if (!_cosmeticAllocated)
            {
                _cosmeticAllocated = true;
                for (int i = 0; i < _cosmeticElementMax; i++)
                {
                    _cosmeticElements.Enqueue(new EffectElementEntry());
                }
                for (int i = 0; i < _cosmeticParticleMax; i++)
                {
                    _cosmeticParticles.Enqueue(new EffectParticle());
                }
            }
            if (Read.GetEffect(effectId) == null)
            {
                LoadEffect(effectId, persistent: true);
            }
            int before = _cosmeticActive.Count;
            BeginCosmetic();
            try
            {
                SpawnEffect(effectId, transform, child: false, entry: null, entCol: null);
            }
            finally
            {
                EndCosmetic();
            }
            for (int i = before; i < _cosmeticActive.Count; i++)
            {
                _cosmeticActive[i].Flags |= EffElemFlags.KeepAlive;
            }
            return _cosmeticActive.Count > before ? _cosmeticActive[^1] : null;
        }

        private void ProcessCosmeticEffects()
        {
            HostCosmeticFrame?.Invoke(this);
            if (_cosmeticActive.Count == 0)
            {
                return;
            }
            BeginCosmetic();
            try
            {
                ProcessEffects(_cosmeticActive);
            }
            finally
            {
                EndCosmetic();
            }
            HostCosmeticAfter?.Invoke(this);
        }

        public void ClearCosmeticEffects()
        {
            while (_cosmeticActive.Count > 0)
            {
                UnlinkEffectElement(_cosmeticActive[^1]);
            }
        }
    }
}
