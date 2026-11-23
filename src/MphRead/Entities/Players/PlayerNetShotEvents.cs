using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // Fruity P2 shot events (docs/FRUITY_PORT.md, 2026-11-13; Fruity Prime's shot events and NetShotEventsRemote, the
    // owner-approved netcode port, THIRD_PARTY_NOTICES.md). On a served match whose host sent FruitySync.ShotsMark, a
    // device's seat on the host fires the shots the device fired, not what the host makes of its trigger (the host fired
    // presses the device's cooldown had refused, and refused shots the device fired: 46 vs 44 shots, 5 of 28 lost).
    // Device: each shot its own player fires (TryFireWeapon) is an event, numbered, with its weapon, charge, where it left
    // the gun and its direction (NetShotEventsNew); the input packets carry it until the host has applied an input made
    // after it (MphRecomp Net/NetMatchSession.Shots.cs). Host: the seat's new events are handed over before each step
    // (CampaignHost.SetRemoteShots, recorded for replays) and from the first one the seat's trigger fires nothing
    // (NetShotEventsDriven): NetFireReady fires the waiting events in order, each with its own weapon (switched to with no
    // animation, as Fruity's PrepareShot), charge, origin and direction, past the weapon's cooldown and a lowered gun (the
    // device judged those), and from a dead body too (shots its device fired before it saw the death still leave). An event
    // waits while the seat is in alt form or morphing or can't fire it (no ammo), and is dropped after FreshFrames.
    // The Shock Coil (a continuous weapon) stays on the trigger, as do bots, LAN and phone-hosted matches.
    // Part (b), client puppets: the served host's snapshot carries each seat's newest shots (Snapshot.Shots, Fruity's
    // SectionShots: the host's own numbering, the newest HostShotsPerSeat a seat from the last HostShotWindow frames, as
    // Fruity's ShotHistoryCount, so a client that went a while without a snapshot still gets the shots in between), and a
    // client's copy of another seat (its puppet) fires those, as the host's copy does its device's, not its trigger bit (a
    // trigger bit seen on another frame than the host's fired a shot the host never did, or missed one: "a shot one phone
    // never drew"), in order and at most HostShotWindow frames after the host did. A puppet's ammo is the host's after the
    // shot, so it's topped up for the shot and put back. The Omega Cannon keeps its own puppet path (PlayerNetConfirm.cs),
    // the Shock Coil its trigger; an older host sends no shots.
    public readonly record struct NetShotEvent(ushort Seq, ushort InputSeq, sbyte Weapon, ushort Charge, Vector3 Origin, Vector3 Direction)
    {
        public const int Size = 31; // u16 seq, u16 input seq, i8 weapon, u16 charge, 3 x f32 origin, 3 x f32 direction

        public void Write(Span<byte> d)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, Seq);
            BinaryPrimitives.WriteUInt16LittleEndian(d[2..], InputSeq);
            d[4] = (byte)Weapon;
            BinaryPrimitives.WriteUInt16LittleEndian(d[5..], Charge);
            BinaryPrimitives.WriteSingleLittleEndian(d[7..], Origin.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[11..], Origin.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[15..], Origin.Z);
            BinaryPrimitives.WriteSingleLittleEndian(d[19..], Direction.X);
            BinaryPrimitives.WriteSingleLittleEndian(d[23..], Direction.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d[27..], Direction.Z);
        }

        public static NetShotEvent Read(ReadOnlySpan<byte> d)
        {
            return new NetShotEvent(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]),
                (sbyte)d[4], BinaryPrimitives.ReadUInt16LittleEndian(d[5..]),
                new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[7..]), BinaryPrimitives.ReadSingleLittleEndian(d[11..]),
                    BinaryPrimitives.ReadSingleLittleEndian(d[15..])),
                new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d[19..]), BinaryPrimitives.ReadSingleLittleEndian(d[23..]),
                    BinaryPrimitives.ReadSingleLittleEndian(d[27..])));
        }
    }

    // a served host's shots in a snapshot (Snapshot.Shots): the host frame it was taken on and each seat's newest shots,
    // each event's InputSeq the frame it was fired on (both their low 16 bits). On the wire every seat up to the highest that
    // shot has HostShotsPerSeat slots, a shot in the slot of its number's low bits, so a slot's bytes change only when its
    // seat shoots again (the XOR deltas, SnapshotDelta.cs) and the block's length only when a seat first shoots
    public sealed record NetHostShotBlock(ushort Frame, (byte Seat, NetShotEvent Ev)[] Events)
    {
        // a slot: u16 number (0 = none), u16 frame, i8 weapon, u16 charge, 3 x i16 origin (1/64), 3 x i16 direction
        // (1/32767; a puppet draws it, so it needn't be exact, and an origin off the scale falls back to the puppet's muzzle)
        public const int SlotSize = 19, SeatSize = SlotSize * PlayerEntity.NetHostShotsPerSeat;
        private const float OriginScale = 64, DirectionScale = Int16.MaxValue;

        public int SeatCount
        {
            get
            {
                int seats = 0;
                foreach ((byte seat, NetShotEvent _) in Events)
                {
                    seats = Math.Max(seats, seat + 1);
                }
                return Math.Min(seats, 16);
            }
        }

        public void WriteSlots(Span<byte> d, int seats)
        {
            d[..(seats * SeatSize)].Clear();
            foreach ((byte seat, NetShotEvent ev) in Events)
            {
                if (seat >= seats || ev.Seq == 0)
                {
                    continue;
                }
                Span<byte> slot = d[(seat * SeatSize + ev.Seq % PlayerEntity.NetHostShotsPerSeat * SlotSize)..];
                BinaryPrimitives.WriteUInt16LittleEndian(slot, ev.Seq);
                BinaryPrimitives.WriteUInt16LittleEndian(slot[2..], ev.InputSeq);
                slot[4] = (byte)ev.Weapon;
                BinaryPrimitives.WriteUInt16LittleEndian(slot[5..], ev.Charge);
                WriteVec(slot[7..], ev.Origin, OriginScale);
                WriteVec(slot[13..], ev.Direction, DirectionScale);
            }
        }

        public static (byte Seat, NetShotEvent Ev)[] ReadSlots(ReadOnlySpan<byte> d, int seats)
        {
            var events = new List<(byte, NetShotEvent)>();
            for (int i = 0; i < seats * PlayerEntity.NetHostShotsPerSeat; i++)
            {
                ReadOnlySpan<byte> slot = d[(i * SlotSize)..];
                ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(slot);
                if (seq == 0)
                {
                    continue;
                }
                Vector3 direction = ReadVec(slot[13..], DirectionScale);
                if (direction.LengthSquared > 0)
                {
                    direction = direction.Normalized();
                }
                events.Add(((byte)(i / PlayerEntity.NetHostShotsPerSeat), new NetShotEvent(seq,
                    BinaryPrimitives.ReadUInt16LittleEndian(slot[2..]), (sbyte)slot[4], BinaryPrimitives.ReadUInt16LittleEndian(slot[5..]),
                    ReadVec(slot[7..], OriginScale), direction)));
            }
            return events.ToArray();
        }

        private static void WriteVec(Span<byte> d, Vector3 v, float scale)
        {
            for (int i = 0; i < 3; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(d[(i * 2)..], (short)Math.Clamp(MathF.Round(v[i] * scale), Int16.MinValue, Int16.MaxValue));
            }
        }

        private static Vector3 ReadVec(ReadOnlySpan<byte> d, float scale)
        {
            return new Vector3(BinaryPrimitives.ReadInt16LittleEndian(d) / scale, BinaryPrimitives.ReadInt16LittleEndian(d[2..]) / scale,
                BinaryPrimitives.ReadInt16LittleEndian(d[4..]) / scale);
        }
    }

    public partial class PlayerEntity
    {
        // events wait at most this many frames on the host, and at most this many at once (Fruity's FreshFrames, Capacity)
        public const int NetShotFreshFrames = 30, NetShotCapacity = 8;
        // an event's origin farther than this from the seat's own muzzle isn't taken (the muzzle's is: Fruity's MaxRayOffset)
        private const float NetShotMaxOffset = 6.0f;

        // device: this match client sends its own player's shots as events (MatchNetDriver sets it as each match starts)
        public static bool NetShotEvents { get; set; }
        // device: the events fired since the last input sent (NetMatchSession.SendInput takes them)
        public static readonly List<NetShotEvent> NetShotEventsNew = new();
        private static ushort _netShotEventSeq;

        // host (a client: its puppets), per seat: events fired, events dropped unfired (stale), this match (the PC tests' check)
        public static readonly int[] NetShotEventsFired = new int[16];
        public static readonly int[] NetShotEventsDropped = new int[16];

        // served host: its snapshots carry every seat's shots (MatchNetDriver sets it as each match starts)
        public static bool NetHostShotEvents { get; set; }
        // a seat's newest this many shots (Fruity's ShotHistoryCount) stay in the snapshots this many frames, and a puppet
        // fires one at most that late
        public const int NetHostShotWindow = 60, NetHostShotsPerSeat = 4;
        // and its slot stays as it was this many frames (past the window the puppets ignore it), so its bytes don't change
        // until the seat shoots again; then it's emptied, before its frame's low 16 bits could come round again
        private const int NetHostShotKeep = 1800;
        // (each seat's, in its own place: seat * HostShotsPerSeat + its number's low bits)
        private static readonly (NetShotEvent Ev, ulong Frame)[] _netHostShots = new (NetShotEvent, ulong)[16 * NetHostShotsPerSeat];
        private static readonly ushort[] _netHostShotSeq = new ushort[16];
        // client, per seat: the newest host shot number its puppet took (-1 = none yet), and how many it took (the PC tests)
        private static readonly int[] _netShotNewest = new int[16];
        public static readonly int[] NetShotEventsTaken = new int[16];
        public static int NetShotNewest(int seat) => seat >= 0 && seat < _netShotNewest.Length ? _netShotNewest[seat] : -1;

        public static void ResetNetShotEvents()
        {
            NetShotEventsNew.Clear();
            _netShotEventSeq = 0;
            Array.Clear(NetShotEventsFired);
            Array.Clear(NetShotEventsDropped);
            Array.Clear(_netHostShots);
            Array.Clear(_netHostShotSeq);
            Array.Fill(_netShotNewest, -1);
            Array.Clear(NetShotEventsTaken);
        }

        // host: this seat fires its device's events, not its trigger (CampaignHost.SetRemoteShots)
        public bool NetShotEventsDriven { get; set; }
        private readonly List<(NetShotEvent Ev, int Age)> _netShotQueue = new();
        private NetShotEvent? _netFiringEvent; // (TryFireWeapon: the event being fired)

        // host: a seat's new events (in the order fired); age = how long it has waited already
        public void NetQueueShotEvent(in NetShotEvent ev, int age = 0)
        {
            if (_netShotQueue.Count >= NetShotCapacity)
            {
                _netShotQueue.RemoveAt(0);
                NetCountShotDrop();
            }
            _netShotQueue.Add((ev, age));
        }

        private void NetCountShotDrop()
        {
            if (SlotIndex >= 0 && SlotIndex < NetShotEventsDropped.Length)
            {
                NetShotEventsDropped[SlotIndex]++;
            }
        }

        // ProcessInput, every frame: events waiting too long are dropped
        private void NetShotEventsAge()
        {
            for (int i = _netShotQueue.Count - 1; i >= 0; i--)
            {
                (NetShotEvent ev, int age) = _netShotQueue[i];
                if (++age > NetShotFreshFrames)
                {
                    _netShotQueue.RemoveAt(i);
                    NetCountShotDrop();
                }
                else
                {
                    _netShotQueue[i] = (ev, age);
                }
            }
        }

        // Spawn: a new life fires none of the old one's
        private void NetShotEventsSpawn()
        {
            _netShotQueue.Clear();
            _netFiringEvent = null;
        }

        // host: fires the seat's waiting events (on foot, alive or dead); how many went out (Fruity's FireReady)
        private int NetFireReady()
        {
            if (!NetShotEventsDriven || !HostRemote || _netShotQueue.Count == 0 || IsAltForm || IsMorphing)
            {
                return 0;
            }
            int fired = 0;
            while (_netShotQueue.Count > 0)
            {
                NetShotEvent ev = _netShotQueue[0].Ev;
                var weapon = (BeamType)ev.Weapon;
                if (weapon != CurrentWeapon)
                {
                    // (Fruity's PrepareShot) the device fired another weapon than this copy holds: switched to it at once
                    if (!PirateBlaster.IsPlayerBeam(weapon) || !_availableWeapons[weapon] || !TryEquipWeapon(weapon, silent: true))
                    {
                        break; // (not here yet: it waits)
                    }
                }
                if (EquipInfo.Weapon.Flags.TestFlag(WeaponFlags.Continuous))
                {
                    _netShotQueue.RemoveAt(0); // (the trigger fires it)
                    continue;
                }
                ushort charge = EquipInfo.ChargeLevel;
                EquipInfo.ChargeLevel = ev.Charge;
                // a client's puppet: its ammo is the host's after this shot, so it gets the shot's back for it
                WeaponInfo info = EquipInfo.Weapon;
                int ammo = EquipInfo.Ammo;
                if (NetClient && ammo >= 0)
                {
                    EquipInfo.Ammo = ammo + Math.Max(info.AmmoCost, Math.Max(info.MinChargeCost, info.ChargeCost));
                }
                _netFiringEvent = ev;
                bool shot = TryFireWeapon();
                _netFiringEvent = null;
                if (NetClient && ammo >= 0)
                {
                    EquipInfo.Ammo = ammo;
                }
                if (!shot)
                {
                    EquipInfo.ChargeLevel = charge;
                    break;
                }
                _netShotQueue.RemoveAt(0);
                // a charged shot spends the charge (as its release does); an uncharged one leaves this copy's own charging
                EquipInfo.ChargeLevel = ev.Charge >= EquipWeapon.MinCharge * 2 ? (ushort)0 : charge; // todo: FPS stuff
                if (SlotIndex >= 0 && SlotIndex < NetShotEventsFired.Length)
                {
                    NetShotEventsFired[SlotIndex]++;
                }
                fired++;
            }
            return fired;
        }

        // TryFireWeapon, host (and a client's puppet): false = this seat's trigger fires nothing (its events do; a puppet's
        // Omega Cannon fires on the host's word, PlayerNetConfirm.cs)
        private bool NetTriggerHeld()
        {
            return NetShotEventsDriven && HostRemote && _netFiringEvent == null && !EquipInfo.Weapon.Flags.TestFlag(WeaponFlags.Continuous)
                && !(NetClient && CurrentWeapon == BeamType.OmegaCannon);
        }

        // TryFireWeapon, host firing an event: where the shot leaves the gun and its direction, the device's
        private void NetEventShotRay(ref Vector3 origin, ref Vector3 direction)
        {
            if (_netFiringEvent is not NetShotEvent ev)
            {
                return;
            }
            if (Single.IsFinite(ev.Origin.X) && Single.IsFinite(ev.Origin.Y) && Single.IsFinite(ev.Origin.Z)
                && (ev.Origin - origin).LengthSquared <= NetShotMaxOffset * NetShotMaxOffset)
            {
                origin = ev.Origin;
            }
            float length = ev.Direction.Length;
            if (Single.IsFinite(length) && length > 0.5f && length < 2)
            {
                direction = ev.Direction / length;
            }
        }

        // TryFireWeapon, a device's own player: the shot that just went out as an event; its number (0 = none sent)
        private ushort NetLogShotEvent(WeaponInfo weapon, Vector3 origin, Vector3 direction)
        {
            if (!NetShotEvents || !NetClient || this != Main || weapon.Flags.TestFlag(WeaponFlags.Continuous))
            {
                return 0;
            }
            _netShotEventSeq = (ushort)(_netShotEventSeq == UInt16.MaxValue ? 1 : _netShotEventSeq + 1);
            NetShotEventsNew.Add(new NetShotEvent(_netShotEventSeq, 0, (sbyte)CurrentWeapon, EquipInfo.ChargeLevel, origin, direction));
            return _netShotEventSeq;
        }

        // TryFireWeapon, served host: any seat's shot that just went out, for the snapshots (its own number per seat)
        private void NetLogHostShot(WeaponInfo weapon, Vector3 origin, Vector3 direction)
        {
            if (!NetHostShotEvents || NetClient || SlotIndex < 0 || SlotIndex >= _netHostShotSeq.Length
                || weapon.Flags.TestFlag(WeaponFlags.Continuous) || CurrentWeapon == BeamType.OmegaCannon)
            {
                return;
            }
            ushort seq = _netHostShotSeq[SlotIndex] = (ushort)(_netHostShotSeq[SlotIndex] == UInt16.MaxValue ? 1 : _netHostShotSeq[SlotIndex] + 1);
            _netHostShots[SlotIndex * NetHostShotsPerSeat + seq % NetHostShotsPerSeat]
                = (new NetShotEvent(seq, 0, (sbyte)CurrentWeapon, EquipInfo.ChargeLevel, origin, direction), _scene.FrameCount);
        }

        // served host, CaptureSnapshot: each seat's newest shots (null = this host sends none)
        public static NetHostShotBlock? NetHostShots(ulong now)
        {
            if (!NetHostShotEvents)
            {
                return null;
            }
            var shots = new List<(byte, NetShotEvent)>();
            for (int i = 0; i < _netHostShots.Length; i++)
            {
                (NetShotEvent ev, ulong frame) = _netHostShots[i];
                if (ev.Seq != 0 && frame <= now && now - frame < NetHostShotKeep)
                {
                    shots.Add(((byte)(i / NetHostShotsPerSeat), ev with { InputSeq = (ushort)frame }));
                }
            }
            return new NetHostShotBlock((ushort)now, shots.ToArray());
        }

        // client, ApplySnapshot, a puppet: the host's shots for this seat it hasn't taken yet, in the order fired (null =
        // the host sends none: the puppet's trigger fires it)
        public void NetPuppetShots(NetHostShotBlock? block)
        {
            NetShotEventsDriven = block != null;
            if (block == null || SlotIndex < 0 || SlotIndex >= _netShotNewest.Length)
            {
                return;
            }
            (byte Seat, NetShotEvent Ev)[] shots = block.Events;
            int newest = _netShotNewest[SlotIndex];
            Span<int> take = stackalloc int[NetShotCapacity];
            int n = 0;
            for (int i = 0; i < shots.Length && n < take.Length; i++)
            {
                (byte seat, NetShotEvent ev) = shots[i];
                if (seat != SlotIndex || ev.Seq == 0 || (ushort)(block.Frame - ev.InputSeq) >= NetHostShotWindow
                    || newest >= 0 && (short)(ev.Seq - (ushort)newest) <= 0)
                {
                    continue;
                }
                int j = n++;
                for (; j > 0 && (short)(ev.Seq - shots[take[j - 1]].Ev.Seq) < 0; j--)
                {
                    take[j] = take[j - 1];
                }
                take[j] = i;
            }
            for (int k = 0; k < n; k++)
            {
                NetShotEvent ev = shots[take[k]].Ev;
                _netShotNewest[SlotIndex] = ev.Seq;
                NetShotEventsTaken[SlotIndex]++;
                if ((BeamType)ev.Weapon != BeamType.OmegaCannon)
                {
                    // (the queue drops an event after FreshFrames: a puppet's starts that much short of HostShotWindow,
                    // counted from the host's frame)
                    int age = (ushort)(block.Frame - ev.InputSeq);
                    NetQueueShotEvent(ev with { InputSeq = 0 }, age - (NetHostShotWindow - NetShotFreshFrames));
                }
            }
        }
    }
}
