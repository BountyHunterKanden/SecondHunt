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

        public const int Size = 11;
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
                AimEpoch = src[10]
            };
        }

        private static float Wrap(float degrees) => degrees - 360 * MathF.Floor((degrees + 180) / 360);
    }
}
