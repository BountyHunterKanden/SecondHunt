using System;
using System.Buffers.Binary;
using MphRead;
using MphRecomp.Campaign;

namespace MphRecomp.Multiplayer.Net
{
    // One simulation frame of a remote player's intent, as sent client -> host. Movement is digital like the DS
    // d-pad (the host's CampaignInput has the same dead zone); the view is ABSOLUTE (PlayerEntity.NetYaw/NetPitch),
    // so a lost packet never leaves the aim off. AimEpoch echoes the host's PlayerEntity.NetAimEpoch (low byte): after
    // a spawn or teleport the host ignores older view targets until the client has taken the new view.
    public struct NetInput
    {
        public ushort Seq;
        public CampaignButtons Buttons;
        public BeamType SelectWeapon; // held to pick a weapon directly; None = no change (the wheel resolves here)
        public sbyte MoveX, MoveY; // -1, 0, 1: strafe (+right), forward (+fwd)
        public float Yaw; // degrees
        public float Pitch; // degrees (the game clamps it)
        public byte AimEpoch;
        // online (O5 lag compensation): the host frame this client's view showed when it made this input (the newest
        // snapshot's tick + the frames it stepped since); the host rewinds the other players to it around this seat's
        // beam hits. 0 = none (LAN, old inputs)
        public uint ViewTick;
        // T11 (touch controls, 2026-10-10): the stylus on the touch controls' touch area this frame (PlayerTouch.cs:
        // vanilla's touch roll, flick boost and double-tap jump), DS pixels moved since the last frame; 0 / false for a pad
        public bool Touching;
        public sbyte TouchDx, TouchDy;
        // online (0.3.6, client-reported position, Net/ClientPosition.cs): where this device had its own player at the start
        // of this input's step (Report), in alt form or not, and the newest host snapshot its prediction had taken in by then
        // (ReportBasis; 0 = no report). Not in Write / WriteRecord: it rides after the inputs of an online input packet
        // (NetMatchSession.SendInput); the host's replay records what it took as its own event (ReplayNetKind.Position)
        public uint ReportBasis;
        public OpenTK.Mathematics.Vector3 Report;
        public bool ReportAlt;
        // (knockback, 0.3.6): this device runs the host's pushes on its own player itself (the snapshot's push section,
        // SeatExtras.Pushes) and its speed at the start of the step, which the host takes for a while after a push
        public bool ReportPushes;
        public OpenTK.Mathematics.Vector3 ReportSpeed;

        // the LAN wire form (Write/Read, LanSession) is unchanged; the online match and the host's replay record carry
        // every field (WriteRecord/ReadRecord)
        public const int Size = 14; // 11 before LAN protocol 13 / online 12 (the touch bytes)
        public const int RecordSize = Size + 4;
        private const float YawScale = 65536f / 360f;
        private const float PitchScale = 100f;

        public readonly void Write(Span<byte> dest)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, Seq);
            BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)Buttons);
            dest[4] = (byte)(sbyte)SelectWeapon;
            dest[5] = (byte)((MoveX + 1) | ((MoveY + 1) << 2));
            BinaryPrimitives.WriteInt16LittleEndian(dest[6..], (short)Math.Clamp(MathF.Round(Wrap(Yaw) * YawScale), -32768, 32767));
            BinaryPrimitives.WriteInt16LittleEndian(dest[8..], (short)Math.Clamp(MathF.Round(Pitch * PitchScale), -18000, 18000));
            dest[10] = AimEpoch;
            dest[11] = (byte)TouchDx;
            dest[12] = (byte)TouchDy;
            dest[13] = (byte)(Touching ? 1 : 0);
        }

        public readonly void WriteRecord(Span<byte> dest)
        {
            Write(dest);
            BinaryPrimitives.WriteUInt32LittleEndian(dest[Size..], ViewTick);
        }

        public static NetInput ReadRecord(ReadOnlySpan<byte> src)
        {
            NetInput input = Read(src);
            input.ViewTick = BinaryPrimitives.ReadUInt32LittleEndian(src[Size..]);
            return input;
        }

        public static NetInput Read(ReadOnlySpan<byte> src)
        {
            return new NetInput
            {
                Seq = BinaryPrimitives.ReadUInt16LittleEndian(src),
                Buttons = (CampaignButtons)BinaryPrimitives.ReadUInt16LittleEndian(src[2..]),
                SelectWeapon = (BeamType)(sbyte)src[4],
                MoveX = (sbyte)((src[5] & 3) - 1),
                MoveY = (sbyte)(((src[5] >> 2) & 3) - 1),
                Yaw = BinaryPrimitives.ReadInt16LittleEndian(src[6..]) / YawScale,
                Pitch = BinaryPrimitives.ReadInt16LittleEndian(src[8..]) / PitchScale,
                AimEpoch = src[10],
                TouchDx = (sbyte)src[11],
                TouchDy = (sbyte)src[12],
                Touching = (src[13] & 1) != 0
            };
        }

        // a record from before the touch bytes (replay format < 24: 11 + 4 bytes): the same with the stylus up
        public const int OldRecordSize = 11 + 4;

        public static byte[] UpgradeOldRecord(byte[] old)
        {
            var record = new byte[RecordSize];
            old.AsSpan(0, 11).CopyTo(record);
            old.AsSpan(11, 4).CopyTo(record.AsSpan(Size));
            return record;
        }

        private static float Wrap(float degrees) => degrees - 360 * MathF.Floor((degrees + 180) / 360);
    }
}
