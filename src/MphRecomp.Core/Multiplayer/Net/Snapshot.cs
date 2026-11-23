using System;
using System.Buffers.Binary;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Multiplayer.Net
{
    // The host's view of the match after one frame, sent host -> client. Every seat's pose, view, health, weapon
    // and the buttons it held (bots included), plus the match clock and scores. About 60 bytes per seat.
    public sealed class Snapshot
    {
        public uint Tick;
        public float MatchTime;
        public byte MatchState;
        public PlayerNetState[] Seats = Array.Empty<PlayerNetState>();
        public short[] Points = Array.Empty<short>();
        public short[] Kills = Array.Empty<short>();
        public short[] Deaths = Array.Empty<short>();
        // the arena's items, every few frames and whenever they change (null = not in this snapshot)
        public NetItem[]? Items;
        // the mode's state (timers, octoliths, nodes, mode stats) in every mode but Battle (null = none)
        public NetObjective? Objective;
        // online only (SnapshotDelta, per joiner): the newest input of the receiving device's seat the host had applied when
        // it took this snapshot (-1 = none yet), and the frames it has stepped since on that input held (it hadn't come yet)
        public int InputAck = -1;
        public int InputHeld;
        // online: the host's pace byte for this device (NetMatchSession.PaceByte: serial + frames to skip or double); -1 none
        public int InputPace = -1;
        // online only: afflictions, power-ups, turrets and detail stats (SeatExtras; sent by SnapshotDelta beside the bytes
        // Write writes, so the LAN wire is unchanged)
        public SeatExtras? Extras;
        // online only (O5b): the item spawners and drop timers, for a client that becomes the host (HostHandoff)
        public HostHandoff? Handoff;
        // the Trooper's cling + drone per seat (SeatAbilities: LAN protocol 9 / online 8; null = no seat has an ability).
        // The last block of Write's bytes, so online's SnapshotDelta carries it inside the mode block (which runs to the end)
        public SeatAbilities? Abilities;
        // LAN only (14): the detail stats as the host counts them (online sends them in Extras); null = not this snapshot
        public SeatExtras.SeatStats[]? Stats;
        // the Echoes arenas' crates + breakables (NetWorld: LAN protocol 16 / online 15; null = the arena has none) and
        // Shooting Gallery's turret (NetWorld.Turret: LAN 21 / online 19)
        public NetWorld? World;

        public const int HeaderSize = 10;
        public const int SeatSize = 62;
        public int Size => HeaderSize + Seats.Length * SeatSize + 1 + (Items == null ? 0 : 1 + Items.Length * NetItem.Size)
            + 1 + (Objective?.Size ?? 0) + 1 + (Abilities?.Size ?? 0) + 1 + (Stats == null ? 0 : 1 + Stats.Length * SeatExtras.StatsSize)
            + 1 + (World?.Size ?? 0) + ThrowsSize + (Taken == null ? (HasLives ? 1 : 0) : 2 + Taken.Length * TakenSize)
            + (HasLives ? 4 + Seats.Length * LifeSize : 0)
            + (AimCams == null ? 0 : (Taken == null && !HasLives ? 1 : 0) + (HasLives ? 0 : 1) + 2 + AimCams.Length * 2)
            + (Shots == null ? 0 : (AimCams == null ? (Taken == null && !HasLives ? 1 : 0) + (HasLives ? 0 : 1) + 1 : 0)
                + 4 + Shots.SeatCount * NetHostShotBlock.SeatSize)
            + (DeathPoseCount == 0 ? 0 : (Shots == null ? (AimCams == null ? (Taken == null && !HasLives ? 1 : 0)
                + (HasLives ? 0 : 1) + 1 : 0) + 1 : 0) + 2 + Math.Min(DeathPoseCount, 255) * DeathPoseSize);

        // who took the items gone on the host lately (ItemSync.TakenList; Fruity Prime's spawner PickerSlot, 11-13): after
        // Throws, the last block (no protocol bump: an older reader stops at Throws). Flag, count, then per item its id
        // and the seat. A host that sends it always does (count 0 = none lately), so a client knows the picker is named;
        // null = an older host (ItemSync works the picker out by position)
        public (ushort Id, byte Seat)[]? Taken;
        public const int TakenSize = 3;

        // Fruity P4 lives (NetPlayerLifecycle + SessionMatchIdentity, docs/FRUITY_PORT.md, 11-13): after Taken, the last
        // block (an older reader stops at Taken; with no Taken, a 0 flag stands for it). Flag, the match id (u16), count,
        // then per seat its slot generation and life id (u16 each). Only a match host with FruitySync.LivesMark sends it
        // (NetMatchSession.SendSnapshot fills it), so LAN and phone-hosted matches never carry it
        public bool HasLives;
        public ushort MatchId;
        public const int LifeSize = 4;

        // the seats' third-person true aim (PlayerEntity.HostTrueAimCam, MphRead PlayerHostAim.cs; owner 2026-11-13), so a
        // guest's puppets aim their shots as the host's seats do: after Lives, the last block (an older reader stops at
        // Lives; with no Taken or no Lives, a 0 flag stands for each). Flag, count, then per seat its code (u16). Sent only
        // when a seat has one (LAN protocol 26; online is first person, so never)
        public ushort[]? AimCams;

        // Fruity P2 shot events, host -> guests (MphRead PlayerNetShotEvents.cs, docs/FRUITY_PORT.md, 11-13): each seat's
        // newest shots (bots' and devices' alike), so a guest's puppets fire the host's shots, not their trigger: after
        // AimCams, the last block (an older reader stops at AimCams; with no Taken, Lives or AimCams, a 0 flag stands for
        // each). Flag, seat count, the host frame (u16), then each seat's shot slots (NetHostShotBlock: a slot's bytes stay
        // put until its seat shoots again, for the XOR deltas). Only a served match host sends it, every snapshot (count 0 = none
        // lately), so a guest knows its puppets are fired by it; null = LAN, a phone host or an older host (the trigger
        // fires them)
        public NetHostShotBlock? Shots;

        // the dead seats' ragdolls (PlayerNetState.HasDeathPose, MphRead PlayerRagdollNet.cs; owner 2026-11-15 "the same
        // ragdoll on every device"): the pose the host's body started from and, once it lies still, where it rests. After
        // Shots, the last block, so no protocol bump: an older reader stops at Shots (with no Taken, Lives, AimCams or Shots,
        // a 0 flag stands for each), and an older host's snapshot has none (its guests build from their own copy). Sent
        // while a seat is dead, by every host. Flag, count, then per seat (DeathPoseSize): the seat, flags (1 rest, 2 on
        // foot, 4 rest heading along the hips, 8 no rest heading), the place (3 floats), the facing (3), the lateral X/Z (2)
        // and the body's side (3; floats, exact: what the host built from), the legs' and the upper body's clip (u8) and
        // frame (u16), the rest pelvis (3 floats) and heading (int16, 65536 a turn)
        public const int DeathPoseSize = 66;
        private int DeathPoseCount
        {
            get
            {
                int n = 0;
                foreach (PlayerNetState s in Seats) if (s.HasDeathPose) n++;
                return n;
            }
        }

        // the dead seats' ragdoll launches (PlayerNetState.HasThrow, owner 2026-11-09 "I want exact throw online"): after
        // World, the last block, so no protocol bump: an older reader stops at World, and an older host's snapshot has
        // none (its guests work the launch out themselves). Online, SnapshotDelta's mode block runs to the end and carries
        // it. Flag, count, then per seat: the seat, the speed and push at 1/2048 (int16, +-16)
        public const int ThrowSize = 1 + 6 * 2;
        private const float ThrowScale = PlayerEntity.NetThrowScale;
        private int ThrowCount
        {
            get
            {
                int n = 0;
                foreach (PlayerNetState s in Seats) if (s.HasThrow) n++;
                return n;
            }
        }
        private int ThrowsSize => ThrowCount == 0 ? 1 : 2 + ThrowCount * ThrowSize;

        public int Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest, Tick);
            BinaryPrimitives.WriteSingleLittleEndian(dest[4..], MatchTime);
            dest[8] = MatchState;
            dest[9] = (byte)Seats.Length;
            int o = HeaderSize;
            for (int i = 0; i < Seats.Length; i++)
            {
                WriteSeat(dest[o..], Seats[i], Points[i], Kills[i], Deaths[i]);
                o += SeatSize;
            }
            if (Items == null)
            {
                dest[o++] = 0;
            }
            else
            {
                int count = Math.Min(Items.Length, 255);
                dest[o++] = 1;
                dest[o++] = (byte)count;
                for (int i = 0; i < count; i++)
                {
                    Items[i].Write(dest[o..]);
                    o += NetItem.Size;
                }
            }
            if (Objective == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                o += Objective.Write(dest[o..]);
            }
            if (Abilities == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                o += Abilities.Write(dest[o..]);
            }
            if (Stats == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                dest[o++] = (byte)Stats.Length;
                for (int i = 0; i < Stats.Length; i++)
                {
                    SeatExtras.WriteStats(dest[o..], Stats[i]);
                    o += SeatExtras.StatsSize;
                }
            }
            if (World == null)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                o += World.Write(dest[o..]);
            }
            int throws = ThrowCount;
            if (throws == 0)
            {
                dest[o++] = 0;
            }
            else
            {
                dest[o++] = 1;
                dest[o++] = (byte)throws;
                for (int i = 0; i < Seats.Length; i++)
                {
                    if (!Seats[i].HasThrow) continue;
                    dest[o++] = (byte)i;
                    WriteThrowVec(dest[o..], Seats[i].ThrowSpeed);
                    WriteThrowVec(dest[(o + 6)..], Seats[i].ThrowPush);
                    o += 12;
                }
            }
            if (Taken != null)
            {
                int taken = Math.Min(Taken.Length, 255);
                dest[o++] = 1;
                dest[o++] = (byte)taken;
                for (int i = 0; i < taken; i++, o += TakenSize)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dest[o..], Taken[i].Id);
                    dest[o + 2] = Taken[i].Seat;
                }
            }
            else if (HasLives)
            {
                dest[o++] = 0;
            }
            if (HasLives)
            {
                int lives = Math.Min(Seats.Length, 255);
                dest[o++] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(dest[o..], MatchId);
                dest[o + 2] = (byte)lives;
                o += 3;
                for (int i = 0; i < lives; i++, o += LifeSize)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dest[o..], Seats[i].Generation);
                    BinaryPrimitives.WriteUInt16LittleEndian(dest[(o + 2)..], Seats[i].LifeId);
                }
            }
            if (AimCams != null)
            {
                if (Taken == null && !HasLives)
                {
                    dest[o++] = 0;
                }
                if (!HasLives)
                {
                    dest[o++] = 0;
                }
                int cams = Math.Min(AimCams.Length, 255);
                dest[o++] = 1;
                dest[o++] = (byte)cams;
                for (int i = 0; i < cams; i++, o += 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dest[o..], AimCams[i]);
                }
            }
            if (Shots != null)
            {
                if (AimCams == null)
                {
                    if (Taken == null && !HasLives)
                    {
                        dest[o++] = 0;
                    }
                    if (!HasLives)
                    {
                        dest[o++] = 0;
                    }
                    dest[o++] = 0;
                }
                int seats = Shots.SeatCount;
                dest[o++] = 1;
                dest[o++] = (byte)seats;
                BinaryPrimitives.WriteUInt16LittleEndian(dest[o..], Shots.Frame);
                o += 2;
                Shots.WriteSlots(dest[o..], seats);
                o += seats * NetHostShotBlock.SeatSize;
            }
            int deathPoses = Math.Min(DeathPoseCount, 255);
            if (deathPoses > 0)
            {
                if (Shots == null)
                {
                    if (AimCams == null)
                    {
                        if (Taken == null && !HasLives)
                        {
                            dest[o++] = 0;
                        }
                        if (!HasLives)
                        {
                            dest[o++] = 0;
                        }
                        dest[o++] = 0;
                    }
                    dest[o++] = 0;
                }
                dest[o++] = 1;
                dest[o++] = (byte)deathPoses;
                for (int i = 0, n = 0; i < Seats.Length && n < deathPoses; i++)
                {
                    if (Seats[i].HasDeathPose)
                    {
                        WriteDeathPose(dest[o..], (byte)i, Seats[i].DeathPose);
                        o += DeathPoseSize;
                        n++;
                    }
                }
            }
            return o;
        }

        public static Snapshot Read(ReadOnlySpan<byte> src)
        {
            int count = src[9];
            var snap = new Snapshot
            {
                Tick = BinaryPrimitives.ReadUInt32LittleEndian(src),
                MatchTime = BinaryPrimitives.ReadSingleLittleEndian(src[4..]),
                MatchState = src[8],
                Seats = new PlayerNetState[count],
                Points = new short[count],
                Kills = new short[count],
                Deaths = new short[count]
            };
            int o = HeaderSize;
            for (int i = 0; i < count; i++)
            {
                snap.Seats[i] = ReadSeat(src[o..], out snap.Points[i], out snap.Kills[i], out snap.Deaths[i]);
                o += SeatSize;
            }
            if (o < src.Length && src[o++] == 1)
            {
                int items = src[o++];
                snap.Items = new NetItem[items];
                for (int i = 0; i < items; i++)
                {
                    snap.Items[i] = NetItem.Read(src[o..]);
                    o += NetItem.Size;
                }
            }
            if (o < src.Length && src[o++] == 1)
            {
                snap.Objective = NetObjective.Read(src[o..], out int size);
                o += size;
            }
            if (o < src.Length && src[o++] == 1)
            {
                snap.Abilities = SeatAbilities.Read(src[o..], out int size);
                o += size;
            }
            if (o < src.Length && src[o++] == 1 && o < src.Length)
            {
                int stats = src[o++];
                if (src.Length - o >= stats * SeatExtras.StatsSize)
                {
                    snap.Stats = new SeatExtras.SeatStats[stats];
                    for (int i = 0; i < stats; i++)
                    {
                        snap.Stats[i] = SeatExtras.ReadStats(src[o..]);
                        o += SeatExtras.StatsSize;
                    }
                }
            }
            if (o < src.Length && src[o++] == 1)
            {
                snap.World = NetWorld.Read(src[o..], out int size);
                o += size;
            }
            if (o < src.Length && src[o++] == 1 && o < src.Length)
            {
                int throws = src[o++];
                for (int i = 0; i < throws && src.Length - o >= ThrowSize; i++, o += ThrowSize)
                {
                    int seat = src[o];
                    if (seat >= count) continue;
                    snap.Seats[seat].HasThrow = true;
                    snap.Seats[seat].ThrowSpeed = ReadThrowVec(src[(o + 1)..]);
                    snap.Seats[seat].ThrowPush = ReadThrowVec(src[(o + 7)..]);
                }
            }
            if (o < src.Length && src[o++] == 1 && o < src.Length)
            {
                int taken = Math.Min(src[o++], (src.Length - o) / TakenSize);
                snap.Taken = new (ushort, byte)[taken];
                for (int i = 0; i < taken; i++, o += TakenSize)
                {
                    snap.Taken[i] = (BinaryPrimitives.ReadUInt16LittleEndian(src[o..]), src[o + 2]);
                }
            }
            if (o < src.Length && src[o++] == 1 && src.Length - o >= 3)
            {
                snap.HasLives = true;
                snap.MatchId = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]);
                int lives = Math.Min(src[o + 2], (src.Length - o - 3) / LifeSize);
                o += 3;
                for (int i = 0; i < lives; i++, o += LifeSize)
                {
                    if (i >= count) continue;
                    snap.Seats[i].Generation = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]);
                    snap.Seats[i].LifeId = BinaryPrimitives.ReadUInt16LittleEndian(src[(o + 2)..]);
                }
            }
            if (o < src.Length && src[o++] == 1 && o < src.Length)
            {
                int cams = Math.Min(src[o++], (src.Length - o) / 2);
                snap.AimCams = new ushort[cams];
                for (int i = 0; i < cams; i++, o += 2)
                {
                    snap.AimCams[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]);
                }
            }
            if (o < src.Length && src[o++] == 1 && o + 3 <= src.Length)
            {
                int seats = Math.Min(src[o], (src.Length - o - 3) / NetHostShotBlock.SeatSize);
                ushort frame = BinaryPrimitives.ReadUInt16LittleEndian(src[(o + 1)..]);
                o += 3;
                snap.Shots = new NetHostShotBlock(frame, NetHostShotBlock.ReadSlots(src[o..], seats));
                o += seats * NetHostShotBlock.SeatSize;
            }
            if (o < src.Length && src[o++] == 1 && o < src.Length)
            {
                int deathPoses = Math.Min(src[o++], (src.Length - o) / DeathPoseSize);
                for (int i = 0; i < deathPoses; i++, o += DeathPoseSize)
                {
                    int seat = src[o];
                    if (seat >= count) continue;
                    // (every build: the owner checked the agreement on two devices 2026-11-22, "Check 3 passes"; the
                    // Wave 1a public gate that skipped this is gone)
                    snap.Seats[seat].HasDeathPose = true;
                    snap.Seats[seat].DeathPose = ReadDeathPose(src[o..]);
                }
            }
            return snap;
        }

        private static void WriteDeathPose(Span<byte> d, byte seat, NetDeathPose p)
        {
            d[0] = seat;
            d[1] = (byte)((p.HasRest ? 1 : 0) | (p.OnFoot ? 2 : 0) | (p.RestFromHips ? 4 : 0) | (p.RestNoHeading ? 8 : 0));
            WriteVec(d[2..], p.Position);
            WriteVec(d[14..], p.Facing);
            BinaryPrimitives.WriteSingleLittleEndian(d[26..], p.LateralX);
            BinaryPrimitives.WriteSingleLittleEndian(d[30..], p.LateralZ);
            WriteVec(d[34..], p.Gun);
            d[46] = p.LegsAnim;
            BinaryPrimitives.WriteUInt16LittleEndian(d[47..], p.LegsFrame);
            d[49] = p.TorsoAnim;
            BinaryPrimitives.WriteUInt16LittleEndian(d[50..], p.TorsoFrame);
            // (an unrested body's rest stays zero, so the bytes don't change until it rests: the XOR deltas)
            WriteVec(d[52..], p.HasRest ? p.RestPelvis : Vector3.Zero);
            BinaryPrimitives.WriteInt16LittleEndian(d[64..], p.HasRest ? p.RestHeading : (short)0);
        }

        private static NetDeathPose ReadDeathPose(ReadOnlySpan<byte> d)
        {
            byte flags = d[1];
            return new NetDeathPose
            {
                HasRest = (flags & 1) != 0,
                OnFoot = (flags & 2) != 0,
                RestFromHips = (flags & 4) != 0,
                RestNoHeading = (flags & 8) != 0,
                Position = ReadVec(d[2..]),
                Facing = ReadVec(d[14..]),
                LateralX = BinaryPrimitives.ReadSingleLittleEndian(d[26..]),
                LateralZ = BinaryPrimitives.ReadSingleLittleEndian(d[30..]),
                Gun = ReadVec(d[34..]),
                LegsAnim = d[46],
                LegsFrame = BinaryPrimitives.ReadUInt16LittleEndian(d[47..]),
                TorsoAnim = d[49],
                TorsoFrame = BinaryPrimitives.ReadUInt16LittleEndian(d[50..]),
                RestPelvis = ReadVec(d[52..]),
                RestHeading = BinaryPrimitives.ReadInt16LittleEndian(d[64..])
            };
        }

        private static void WriteThrowVec(Span<byte> d, Vector3 v)
        {
            for (int i = 0; i < 3; i++)
            {
                float f = Math.Clamp(MathF.Round(v[i] * ThrowScale), short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(d[(i * 2)..], (short)f);
            }
        }

        private static Vector3 ReadThrowVec(ReadOnlySpan<byte> d)
        {
            return new Vector3(BinaryPrimitives.ReadInt16LittleEndian(d) / ThrowScale,
                BinaryPrimitives.ReadInt16LittleEndian(d[2..]) / ThrowScale, BinaryPrimitives.ReadInt16LittleEndian(d[4..]) / ThrowScale);
        }

        // one seat's SeatSize bytes (the online delta compares these byte ranges: SnapshotDelta.SeatGroups)
        public static void WriteSeat(Span<byte> d, in PlayerNetState s, short points, short kills, short deaths)
        {
            d[0] = s.Flags;
            WriteVec(d[1..], s.Position);
            WriteVec(d[13..], s.Speed);
            WriteUnit(d[25..], s.Facing);
            WriteUnit(d[31..], s.Up);
            BinaryPrimitives.WriteInt16LittleEndian(d[37..], (short)Math.Clamp(MathF.Round(Wrap(s.Yaw) * 65536f / 360f), -32768, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(d[39..], (short)Math.Clamp(MathF.Round(s.Pitch * 100), -18000, 18000));
            d[41] = s.AimEpoch;
            BinaryPrimitives.WriteUInt16LittleEndian(d[42..], s.Health);
            d[44] = (byte)s.Weapon;
            BinaryPrimitives.WriteUInt16LittleEndian(d[45..], s.Weapons);
            BinaryPrimitives.WriteInt16LittleEndian(d[47..], s.Ammo0);
            BinaryPrimitives.WriteInt16LittleEndian(d[49..], s.Ammo1);
            BinaryPrimitives.WriteUInt16LittleEndian(d[51..], s.Controls);
            BinaryPrimitives.WriteInt16LittleEndian(d[53..], points);
            BinaryPrimitives.WriteInt16LittleEndian(d[55..], kills);
            BinaryPrimitives.WriteInt16LittleEndian(d[57..], deaths);
            d[59] = (byte)s.LastAttacker;
            d[60] = (byte)s.LastBeam;
            d[61] = s.LastFlags;
        }

        public static PlayerNetState ReadSeat(ReadOnlySpan<byte> d, out short points, out short kills, out short deaths)
        {
            points = BinaryPrimitives.ReadInt16LittleEndian(d[53..]);
            kills = BinaryPrimitives.ReadInt16LittleEndian(d[55..]);
            deaths = BinaryPrimitives.ReadInt16LittleEndian(d[57..]);
            return new PlayerNetState
            {
                Flags = d[0],
                Position = ReadVec(d[1..]),
                Speed = ReadVec(d[13..]),
                Facing = ReadUnit(d[25..]),
                Up = ReadUnit(d[31..]),
                Yaw = BinaryPrimitives.ReadInt16LittleEndian(d[37..]) * 360f / 65536f,
                Pitch = BinaryPrimitives.ReadInt16LittleEndian(d[39..]) / 100f,
                AimEpoch = d[41],
                Health = BinaryPrimitives.ReadUInt16LittleEndian(d[42..]),
                Weapon = (sbyte)d[44],
                Weapons = BinaryPrimitives.ReadUInt16LittleEndian(d[45..]),
                Ammo0 = BinaryPrimitives.ReadInt16LittleEndian(d[47..]),
                Ammo1 = BinaryPrimitives.ReadInt16LittleEndian(d[49..]),
                Controls = BinaryPrimitives.ReadUInt16LittleEndian(d[51..]),
                LastAttacker = (sbyte)d[59],
                LastBeam = (sbyte)d[60],
                LastFlags = d[61]
            };
        }

        private static void WriteVec(Span<byte> d, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(d, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[8..], v.Z);
        }

        private static Vector3 ReadVec(ReadOnlySpan<byte> d)
        {
            return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d), BinaryPrimitives.ReadSingleLittleEndian(d[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(d[8..]));
        }

        // unit vectors as three 16-bit components (~0.00003 precision)
        private static void WriteUnit(Span<byte> d, Vector3 v)
        {
            BinaryPrimitives.WriteInt16LittleEndian(d, (short)Math.Clamp(MathF.Round(v.X * 32767), -32767, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(d[2..], (short)Math.Clamp(MathF.Round(v.Y * 32767), -32767, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(d[4..], (short)Math.Clamp(MathF.Round(v.Z * 32767), -32767, 32767));
        }

        private static Vector3 ReadUnit(ReadOnlySpan<byte> d)
        {
            var v = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(d) / 32767f,
                BinaryPrimitives.ReadInt16LittleEndian(d[2..]) / 32767f, BinaryPrimitives.ReadInt16LittleEndian(d[4..]) / 32767f);
            return v.LengthSquared > 0 ? v.Normalized() : v;
        }

        private static float Wrap(float degrees) => degrees - 360 * MathF.Floor((degrees + 180) / 360);
    }
}
