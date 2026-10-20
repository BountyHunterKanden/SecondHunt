using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Client-reported position (Net/ClientPosition.cs): a report the host checked and took goes in here before the step and is
    // applied at its start (PlayerEntity.HostTakePosition), through ClientPositionTap, so the host's recording has it at the
    // step it was applied and a replay puts it back there (ReplaySession, NetEvents of kind Position, format 54).
    // Knockback (0.3.6, format 55): a report from a device that runs its own pushes says so (the host then hands a hit's push
    // to it, PlayerEntity.NetHandPush) and carries its speed; the host takes that speed too while a push it handed is recent
    // (PushSpeedFrames), so its copy slides as the device's does. The record holds what was decided, so a replay needn't
    // decide it again.
    public sealed partial class CampaignHost
    {
        // the recording tap for a taken position: (seat, PositionRecord's bytes)
        public Action<int, byte[]>? ClientPositionTap;

        private readonly List<(int Seat, Vector3 Position, bool Pushes, Vector3? Speed, bool Decided)> _clientPositions = new();

        public const int PositionRecordSize = 12; // 3 x f32 (format 54)
        private const int PositionRecordFlags = 13; // + flags (bit 0: the device runs its own pushes)
        private const int PositionRecordSpeed = 25; // + the speed taken with it (3 x f32)
        // after a push handed to a device, its reported speed is taken this long (frames: the slide and a round trip's margin)
        private const int PushSpeedFrames = 300;
        private const float SpeedMax = 3f; // a reported speed past this is garbage (a push slide is well under 1)
        private readonly byte[] _pushSeen = new byte[PlayerEntity.MaxPlayers];
        private readonly long[] _pushFrame = new long[PlayerEntity.MaxPlayers];

        // a report the host took (Net/ClientPosition.cs): applied at the start of the next step. pushes = its device runs its
        // own pushes; speed = its reported speed (taken only if a handed push is recent)
        // (decided: the speed is already chosen, as Net/ClientPosition.cs's Pin and Hold do)
        public void TakeClientPosition(int seat, Vector3 position, bool pushes = false, Vector3? speed = null, bool decided = false)
            => _clientPositions.Add((seat, position, pushes, speed, decided));

        // a push was handed to this seat's device lately (its reported speed is the slide it runs)
        public bool PushRecent(int seat) => seat >= 0 && seat < PlayerEntity.MaxPlayers && _pushFrame[seat] > 0 && Frame - _pushFrame[seat] <= PushSpeedFrames;

        // Net/ClientPosition.cs HostPin, after the step: the seat is put there at the next step's start (recorded; a report taken
        // for that step comes after it in the list and wins). Not moved now: the claimed hits run first at a step's start, and a
        // replay has the seat where the step left it there (moving it now made served replays NOT EXACT from step ~11, PC 10-20)
        public void PinNow(int seat, Vector3 position, Vector3? speed)
        {
            PlayerEntity p = PlayerEntity.Players[seat];
            _clientPositions.Add((seat, position, p.NetClientPushes, speed, true));
        }

        // a replay putting a recorded one back, as it was applied
        public void TakeClientPosition(int seat, byte[] record)
        {
            (Vector3 position, bool pushes, Vector3? speed) = ReadPositionRecord(record);
            _clientPositions.Add((seat, position, pushes, speed, true));
        }

        public static byte[] PositionRecord(Vector3 position, bool pushes = false, Vector3? speed = null)
        {
            byte[] b = new byte[speed.HasValue ? PositionRecordSpeed : pushes ? PositionRecordFlags : PositionRecordSize];
            BinaryPrimitives.WriteSingleLittleEndian(b, position.X);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), position.Z);
            if (b.Length > PositionRecordSize)
            {
                b[12] = (byte)(pushes ? 1 : 0);
            }
            if (speed.HasValue)
            {
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(13), speed.Value.X);
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(17), speed.Value.Y);
                BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(21), speed.Value.Z);
            }
            return b;
        }

        public static bool IsPositionRecord(byte[] b)
            => b.Length == PositionRecordSize || b.Length == PositionRecordFlags || b.Length == PositionRecordSpeed;

        public static (Vector3 Position, bool Pushes, Vector3? Speed) ReadPositionRecord(byte[] b)
        {
            var position = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(b),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(4)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(8)));
            bool pushes = b.Length > PositionRecordSize && (b[12] & 1) != 0;
            Vector3? speed = b.Length >= PositionRecordSpeed ? new Vector3(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(13)),
                BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(17)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(21))) : null;
            return (position, pushes, speed);
        }

        // positions taken (the logs), and how many of them took the device's speed after a handed push
        public int PositionsApplied { get; private set; }
        public int PushSpeedsTaken { get; private set; }
        public int PushHeldFrames { get; private set; }

        // a seat whose input hasn't come this frame (the host holds its last one): while a push handed to its device is
        // recent, the seat doesn't slide on by itself this step (its side-to-side speed is taken as zero, the place as it is).
        // Its device's report of the late input puts it where that device had it before that step, so a held slide step was
        // pulled back a frame later: a one-frame jerk back mid-slide on the other players' screens (PC test, 200 ms + 50 ms
        // jitter). Recorded like any taken position, so a replay does the same.
        public void HoldClientPushed(int seat)
        {
            if (seat < 0 || seat >= PlayerEntity.MaxPlayers || PlayerEntity.Players.Count <= seat) return;
            PlayerEntity p = PlayerEntity.Players[seat];
            if (!p.NetClientPushes || _pushFrame[seat] <= 0 || Frame - _pushFrame[seat] > PushSpeedFrames
                || !(Match?.ClientPosition ?? true))
            {
                return;
            }
            _clientPositions.Add((seat, p.Position, true, new Vector3(0, p.Speed.Y, 0), true));
            PushHeldFrames++;
        }

        // Step, before the frame runs (after the claimed hits, before lag compensation keeps the poses)
        private void ApplyClientPositions()
        {
            // the pushes handed to each seat's device since the last step (the frame of the newest)
            for (int seat = 0; seat < PlayerEntity.MaxPlayers; seat++)
            {
                PlayerEntity? p = PlayerEntity.Players.Count > seat ? PlayerEntity.Players[seat] : null;
                if (p != null && p.NetPushCount != _pushSeen[seat])
                {
                    _pushSeen[seat] = p.NetPushCount;
                    _pushFrame[seat] = Frame;
                }
            }
            if (_clientPositions.Count == 0) return;
            if (Match?.ClientPosition ?? true)
            {
                foreach ((int seat, Vector3 position, bool pushes, Vector3? reported, bool decided) in _clientPositions)
                {
                    if (seat < 0 || seat >= PlayerEntity.MaxPlayers) continue;
                    Vector3? speed = reported;
                    if (!decided)
                    {
                        speed = pushes && reported.HasValue && _pushFrame[seat] > 0 && Frame - _pushFrame[seat] <= PushSpeedFrames
                            && reported.Value.Length <= SpeedMax ? reported : null;
                    }
                    ClientPositionTap?.Invoke(seat, PositionRecord(position, pushes, speed));
                    PlayerEntity.Players[seat].HostTakePosition(position, pushes, speed);
                    PositionsApplied++;
                    if (speed.HasValue)
                    {
                        PushSpeedsTaken++;
                    }
                }
            }
            _clientPositions.Clear();
        }
    }
}
