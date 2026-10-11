using System;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Replay
{
    // One step's game state folded into hashes, for replays (docs/REPLAY_MODE.md, "Exact or refused"). Taken after
    // each CampaignHost.Step: a replay stores Checksum per step, and playback compares it before drawing that step.
    // The parts say WHAT diverged when a check fails (the harness reports them; Tools -replaytest / -drawinvariance).
    //   Checksum: everything that decides a match -- Rng2 (all gameplay randomness: bots, spread, items, camera shake),
    //   the match (state, clock, scores), every seat, every gameplay entity (projectiles, bombs, items, Halfturret,
    //   octolith, flag bases, nodes, jump pads...).
    //   Not in it: Rng1 (effects, lightning arcs, sound picks only) and the cosmetic entities (beam effects, objects,
    //   lights, camera sequences) -- they're kept as parts to report, but what's on screen may move them.
    // No allocation: it runs every step of a match.
    public struct ReplayStateParts
    {
        public uint Rng1;
        public uint Rng2;
        public ulong Match;
        public ulong Seat0, Seat1, Seat2, Seat3;
        public ulong Seats4To7; // P8: seats 5-8 and their scores, only in a match of 5-8 seats (0 otherwise)
        public ulong Entities;
        public ulong Cosmetic;
        public uint Checksum;

        public readonly ulong Seat(int index) => index switch { 0 => Seat0, 1 => Seat1, 2 => Seat2, _ => Seat3 };

        // the parts that differ from another step's, e.g. "rng2 seat1 entities" (empty = identical)
        public readonly string Diff(in ReplayStateParts other)
        {
            string s = "";
            if (Rng2 != other.Rng2) s += " rng2";
            if (Match != other.Match) s += " match";
            for (int i = 0; i < 4; i++)
            {
                if (Seat(i) != other.Seat(i)) s += $" seat{i}";
            }
            if (Seats4To7 != other.Seats4To7) s += " seats4-7";
            if (Entities != other.Entities) s += " entities";
            if (Rng1 != other.Rng1) s += " (rng1)";
            if (Cosmetic != other.Cosmetic) s += " (cosmetic)";
            return s.Trim();
        }
    }

    public static class ReplayState
    {
        // entity types that only decorate: what's drawn may create, move or skip them without changing the match
        public static bool IsCosmetic(EntityType type) => type is EntityType.BeamEffect or EntityType.Object
            or EntityType.LightSource or EntityType.CameraSequence;

        // PlayerFlags2 bits set by drawing (level of detail, drawn in third person): not game state
        private const PlayerFlags2 DrawFlags2 = PlayerFlags2.Lod1 | PlayerFlags2.DrawnThirdPerson;

        public const int TypeSlots = 32; // EntityType values below 32 (all of MPH's); First Hunt types share the last

        public static ReplayStateParts Capture(Scene scene)
        {
            var parts = new ReplayStateParts
            {
                Rng1 = Rng.Rng1,
                Rng2 = Rng.Rng2
            };
            var h = Hash.Start();
            h.Add((uint)GameState.MatchState);
            h.Add(GameState.MatchTime);
            for (int i = 0; i < 4; i++)
            {
                h.Add(GameState.Points[i]);
                h.Add(GameState.TeamPoints[i]);
                h.Add(GameState.Kills[i]);
                h.Add(GameState.Deaths[i]);
            }
            parts.Match = h.Finish();
            // only this match's seats: the static player array keeps an earlier, bigger match's extra players
            for (int i = 0; i < 4; i++)
            {
                ulong seat = i < PlayerEntity.PlayerCount && PlayerEntity.Players[i] is PlayerEntity p ? SeatHash(p) : 0;
                switch (i)
                {
                case 0: parts.Seat0 = seat; break;
                case 1: parts.Seat1 = seat; break;
                case 2: parts.Seat2 = seat; break;
                default: parts.Seat3 = seat; break;
                }
            }
            if (PlayerEntity.MaxPlayers > PlayerEntity.VanillaMaxPlayers)
            {
                // P8 (5-8 seats): the extra seats in one more part, so a 1-4 player match hashes exactly as before
                var high = Hash.Start();
                for (int i = PlayerEntity.VanillaMaxPlayers; i < PlayerEntity.MaxPlayers; i++)
                {
                    high.Add(GameState.Points[i]);
                    high.Add(GameState.TeamPoints[i]);
                    high.Add(GameState.Kills[i]);
                    high.Add(GameState.Deaths[i]);
                    high.Add(i < PlayerEntity.PlayerCount && PlayerEntity.Players[i] is PlayerEntity p ? SeatHash(p) : 0);
                }
                parts.Seats4To7 = high.Finish();
            }
            var game = Hash.Start();
            var cosmetic = Hash.Start();
            foreach (EntityBase entity in scene.Entities)
            {
                if (entity.Type == EntityType.Player || entity.Type == EntityType.Room)
                {
                    continue;
                }
                if (IsCosmetic(entity.Type))
                {
                    AddEntity(ref cosmetic, entity);
                }
                else
                {
                    AddEntity(ref game, entity);
                }
            }
            parts.Entities = game.Finish();
            parts.Cosmetic = cosmetic.Finish();
            var all = Hash.Start();
            all.Add(parts.Rng2);
            all.Add(parts.Match);
            all.Add(parts.Seat0);
            all.Add(parts.Seat1);
            all.Add(parts.Seat2);
            all.Add(parts.Seat3);
            if (PlayerEntity.MaxPlayers > PlayerEntity.VanillaMaxPlayers)
            {
                all.Add(parts.Seats4To7);
            }
            all.Add(parts.Entities);
            ulong total = all.Finish();
            parts.Checksum = (uint)(total ^ (total >> 32));
            return parts;
        }

        // R2b diagnosis: one step's raw game state as text, floats as their bit patterns, in the same format on every
        // device: two devices' dumps of the same replay diff line by line to the first value that differs (Tools
        // -replayplay dump=a-b; the app's --es replaydump a-b). Lines: "step", one per seat, "types" (a hash per entity
        // type). Allocates; diagnosis only.
        public static string Dump(Scene scene, int step)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"step {step} rng1 {Rng.Rng1:x8} rng2 {Rng.Rng2:x8} state {(uint)GameState.MatchState} "
                + $"time {Bits(GameState.MatchTime)} entities {scene.Entities.Count}\n");
            for (int i = 0; i < PlayerEntity.PlayerCount; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                PlayerNetState s = p.NetCapture();
                CameraInfo cam = p.CameraInfo;
                sb.Append($"  seat{i} pos {V(s.Position)} spd {V(s.Speed)} fac {V(s.Facing)} up {V(s.Up)} "
                    + $"yaw {Bits(s.Yaw)} pitch {Bits(s.Pitch)} hp {s.Health} f1 {(uint)p.Flags1:x8} "
                    + $"f2 {(uint)(p.Flags2 & ~DrawFlags2):x8} ctl {s.Controls:x8} w {(byte)s.Weapon} "
                    + $"cam {V(cam.Position)} tgt {V(cam.Target)} cup {V(cam.UpVector)} fov {Bits(cam.Fov)}\n");
            }
            Span<ulong> byType = stackalloc ulong[TypeSlots];
            CaptureByType(scene, byType);
            sb.Append("  types");
            for (int i = 0; i < TypeSlots; i++)
            {
                if (byType[i] != Hash.Start().Finish() && i != (int)EntityType.Player)
                {
                    sb.Append($" {(EntityType)i}:{byType[i] & 0xFFFFFFFF:x8}");
                }
            }
            return sb.ToString();

            static string Bits(float f) => $"{BitConverter.SingleToInt32Bits(f):x8}";
            static string V(OpenTK.Mathematics.Vector3 v) => $"{Bits(v.X)},{Bits(v.Y)},{Bits(v.Z)}";
        }

        // diagnostics: one hash per entity type (index = (int)type, First Hunt types in the last slot)
        public static void CaptureByType(Scene scene, Span<ulong> byType)
        {
            Span<Hash> hashes = stackalloc Hash[TypeSlots];
            for (int i = 0; i < TypeSlots; i++)
            {
                hashes[i] = Hash.Start();
            }
            foreach (EntityBase entity in scene.Entities)
            {
                int slot = Math.Min((int)entity.Type, TypeSlots - 1);
                AddEntity(ref hashes[slot], entity);
            }
            for (int i = 0; i < TypeSlots && i < byType.Length; i++)
            {
                byType[i] = hashes[i].Finish();
            }
        }

        private static ulong SeatHash(PlayerEntity p)
        {
            PlayerNetState s = p.NetCapture();
            var h = Hash.Start();
            h.Add(s.Flags);
            h.Add(s.Position.X); h.Add(s.Position.Y); h.Add(s.Position.Z);
            h.Add(s.Speed.X); h.Add(s.Speed.Y); h.Add(s.Speed.Z);
            h.Add(s.Facing.X); h.Add(s.Facing.Y); h.Add(s.Facing.Z);
            h.Add(s.Up.X); h.Add(s.Up.Y); h.Add(s.Up.Z);
            h.Add(s.Yaw); h.Add(s.Pitch);
            h.Add(s.Health);
            h.Add((uint)(byte)s.Weapon);
            h.Add(s.Weapons);
            h.Add((uint)(ushort)s.Ammo0);
            h.Add((uint)(ushort)s.Ammo1);
            h.Add(s.Controls);
            h.Add((uint)p.Flags1);
            h.Add((uint)(p.Flags2 & ~DrawFlags2));
            // the player's own camera (sim state: the morph ball's camera angle steers the roll, camera shake uses
            // Rng2), and it's what a replay shows from that seat
            CameraInfo cam = p.CameraInfo;
            h.Add(cam.Position.X); h.Add(cam.Position.Y); h.Add(cam.Position.Z);
            h.Add(cam.Target.X); h.Add(cam.Target.Y); h.Add(cam.Target.Z);
            h.Add(cam.UpVector.X); h.Add(cam.UpVector.Y); h.Add(cam.UpVector.Z);
            h.Add(cam.Fov);
            return h.Finish();
        }

        // type, active and position only: ids of spawned entities (projectiles, bombs, effects) come from shared
        // counters, so a cosmetic spawn would shift every later id without changing the match
        private static void AddEntity(ref Hash h, EntityBase entity)
        {
            h.Add((uint)entity.Type | (entity.Active ? 0x10000u : 0) | (entity.Initialized ? 0x20000u : 0));
            OpenTK.Mathematics.Vector3 pos = entity.Position;
            h.Add(pos.X);
            h.Add(pos.Y);
            h.Add(pos.Z);
        }

        // FNV-style mix over 32-bit words + a SplitMix64 finish: exact bit patterns (floats by their bits), cheap
        private struct Hash
        {
            private ulong _h;

            public static Hash Start() => new Hash { _h = 14695981039346656037UL };

            public void Add(uint value)
            {
                _h = (_h ^ value) * 1099511628211UL;
            }

            public void Add(int value) => Add((uint)value);

            public void Add(float value) => Add((uint)BitConverter.SingleToInt32Bits(value));

            public void Add(ulong value)
            {
                Add((uint)value);
                Add((uint)(value >> 32));
            }

            public readonly ulong Finish()
            {
                ulong z = _h;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
