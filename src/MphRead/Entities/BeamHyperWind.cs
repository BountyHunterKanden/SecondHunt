using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: a charged Hyper Beam shot (the release and its stream, Metadata/HyperBeam.cs) flies as Corruption's PhazonBurst
    // does (WPSC 019855c26606c969, SamGunFx.pak; owner 11-14 "curl the sim too": its damage goes where it's drawn). Each
    // frame (the sim's 60 Hz is Corruption's) its local velocity eases toward the WIND's: vel += (wind - vel) * 0.125, wind =
    // (RAND(-0.5, 0.5) taken afresh each frame, KEYP_Y, KEYP_Z at the percent of its life), in metres a frame; it starts at
    // IVEC (0, 1.1, 0). Local +Y is the shot's line, +Z up, both rolled about the line by IORN RAND(0, 360); its life is PSLT
    // RAND(50, 80) frames, then it bursts (Process' Lifespan branch). Its own seeded random (HyperReleaseSeed, HyperStreamSeed), never Rng, so
    // replays and P2 puppets fly it the same. No ricochet: the weapon has none, and any hit ends it as MPH's beams do
    public partial class BeamProjectileEntity
    {
        // Corruption's metres to MPH units (MphRecomp.Arenas.EchoesArena.DefaultScale, which MphRead can't see)
        public const float HyperWindScale = 1.6f / 2.7f;
        public const float HyperWindFactor = 0.125f;
        public const float HyperWindStartSpeed = 1.1f;
        public const int HyperWindLifeMin = 50, HyperWindLifeMax = 80;

        // the WIND's KEYP keys (percent of life, 101 each), as the WPSC has them
        public static readonly float[] HyperWindKeysY =
        {
            0f, 0.044f, 0.168f, 0.365f, 0.624f, 0.937f, 1.296f, 1.691f, 2.112f, 2.552f, 3f, 3.449f, 3.888f, 4.31f, 4.704f, 5.063f,
            5.376f, 5.635f, 5.832f, 5.956f, 6f, 6.002f, 6.007f, 6.016f, 6.029f, 6.045f, 6.064f, 6.087f, 6.112f, 6.14f, 6.172f,
            6.206f, 6.243f, 6.283f, 6.325f, 6.369f, 6.416f, 6.465f, 6.516f, 6.57f, 6.625f, 6.682f, 6.741f, 6.802f, 6.864f, 6.928f,
            6.993f, 7.059f, 7.127f, 7.196f, 7.266f, 7.336f, 7.408f, 7.48f, 7.553f, 7.627f, 7.701f, 7.775f, 7.85f, 7.925f, 8f,
            8.075f, 8.15f, 8.225f, 8.299f, 8.373f, 8.447f, 8.52f, 8.592f, 8.664f, 8.734f, 8.804f, 8.873f, 8.941f, 9.007f, 9.072f,
            9.136f, 9.198f, 9.259f, 9.318f, 9.375f, 9.43f, 9.484f, 9.535f, 9.584f, 9.631f, 9.675f, 9.717f, 9.757f, 9.794f, 9.828f,
            9.86f, 9.888f, 9.913f, 9.936f, 9.955f, 9.971f, 9.984f, 9.993f, 9.998f, 10f
        };
        public static readonly float[] HyperWindKeysZ = BuildKeysZ();

        private static float[] BuildKeysZ()
        {
            var keys = new float[101]; // (past the first ten, all 0)
            float[] head = { 0.5f, 0.448f, 0.324f, 0.176f, 0.052f, 0f, -0.211f, -0.538f, -0.538f, -0.211f };
            head.CopyTo(keys, 0);
            return keys;
        }

        // the flight: on, its seeded random, the frames flown and its life, its local velocity (metres a frame), its axes
        public bool HyperWind { get; private set; }
        public uint HyperWindSeed { get; private set; }
        private uint _hyperWindRand;
        private int _hyperWindFrame;
        private int _hyperWindLife;
        private Vector3 _hyperWindVel;
        private Vector3 _hyperWindRight, _hyperWindLine, _hyperWindUp;

        // Corruption's KEYP at a percent of the life: the key below, lerped toward the next
        public static float HyperWindKey(float[] keys, float pct)
        {
            int i = Math.Clamp((int)pct, 0, keys.Length - 1);
            return i + 1 < keys.Length ? keys[i] + (keys[i + 1] - keys[i]) * (pct - i) : keys[i];
        }

        // a release's seed: its owner's slot, its life (the seat's deaths: the host's count, which every copy's snapshots
        // carry, MatchNet) and which release it is in that life (owner 11-14 "send the seed with each shot": numbers every
        // copy of the seat already agrees on, so the device, the host, each client's copy and a replay fly it the same, on
        // every path: phone-hosted and LAN matches fire it from the trigger, a served one from its shot event)
        public static uint HyperReleaseSeed(int slot, int life, int release)
        {
            uint h = HyperWindMix(HyperWindMix(HyperWindMix(0x811c9dc5, slot), life), release);
            return h == 0 ? 1 : h;
        }

        // a stream shot's seed: its release's and its place in the stream (every copy runs the stream from the release)
        public static uint HyperStreamSeed(uint release, int streamShot)
        {
            uint h = HyperWindMix(HyperWindMix(0x811c9dc5, (int)release), streamShot);
            return h == 0 ? 1 : h;
        }

        private static uint HyperWindMix(uint h, int v)
        {
            h ^= (uint)v;
            h *= 0x01000193;
            h ^= h >> 15;
            return h;
        }

        private float HyperWindRandom()
        {
            // xorshift32: [0, 1)
            uint x = _hyperWindRand;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _hyperWindRand = x;
            return (x >> 8) / 16777216f;
        }

        // Spawn: a charged Hyper Beam shot starts its flight; Lifespan becomes its PSLT life
        private void StartHyperWind(uint seed)
        {
            HyperWind = true;
            HyperWindSeed = seed;
            _hyperWindRand = seed == 0 ? 1 : seed;
            _hyperWindFrame = 0;
            _hyperWindLife = HyperWindLifeMin + (int)(HyperWindRandom() * (HyperWindLifeMax - HyperWindLifeMin + 1));
            float roll = MathHelper.DegreesToRadians(HyperWindRandom() * 360);
            Vector3 line = Direction.Normalized();
            Vector3 right = Vector3.Cross(line, Vector3.UnitY);
            right = right.LengthSquared > 1e-6f ? right.Normalized() : Vector3.UnitX;
            Vector3 up = Vector3.Cross(right, line).Normalized();
            float c = MathF.Cos(roll), s = MathF.Sin(roll);
            _hyperWindLine = line;
            _hyperWindRight = right * c - up * s;
            _hyperWindUp = up * c + right * s;
            _hyperWindVel = new Vector3(0, HyperWindStartSpeed, 0);
            Lifespan = (_hyperWindLife + 1) / 60f; // its frame on the muzzle, then PSLT moves; it bursts on the last's frame
            Velocity = _hyperWindLine * (HyperWindStartSpeed * HyperWindScale);
            Acceleration = Vector3.Zero;
            Flags &= ~BeamFlags.Ricochet;
            RicochetWeapon = null;
        }

        // Process: this frame's move (Corruption's: the WIND, then the move)
        private void StepHyperWind()
        {
            float pct = _hyperWindFrame * 100f / _hyperWindLife;
            var wind = new Vector3(-0.5f + HyperWindRandom(), HyperWindKey(HyperWindKeysY, pct), HyperWindKey(HyperWindKeysZ, pct));
            _hyperWindVel += (wind - _hyperWindVel) * HyperWindFactor;
            _hyperWindFrame++;
            Velocity = (_hyperWindRight * _hyperWindVel.X + _hyperWindLine * _hyperWindVel.Y + _hyperWindUp * _hyperWindVel.Z)
                * HyperWindScale;
            Position += Velocity;
        }
    }
}
