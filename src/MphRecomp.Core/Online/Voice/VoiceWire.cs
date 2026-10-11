using System;
using System.Buffers.Binary;
using System.Text;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.Online.Voice
{
    // Voice packets on the match transport (OnlineMatch.Link: our relay or Cloudflare TURN), their own channel: the first
    // byte is MatchNetVoice.MsgVoice, which NetMatchSession skips (no match parsing, no replay tap: voice is never kept).
    //   audio: MsgVoice, 1, seq (u16 LE, the first frame's), count | 0x80 at the end of a talk spurt, then per frame:
    //          length (u8), Opus bytes. Frames are consecutive seqs.
    //   hello: MsgVoice, 2, length (u8), this device's friend code (ASCII): who a relay slot is, when the server's member
    //          cards don't say (servers before 0.5.0 leave PlayerCard.Slot out)
    public static class VoiceWire
    {
        public const byte Audio = 1, Hello = 2;
        public const byte EndFlag = 0x80;
        public const int AudioHeader = 5;

        public static int WriteHello(Span<byte> packet, string code)
        {
            packet[0] = MatchNetVoice.MsgVoice;
            packet[1] = Hello;
            int n = Encoding.ASCII.GetBytes(code.AsSpan(0, Math.Min(code.Length, 32)), packet[3..]);
            packet[2] = (byte)n;
            return 3 + n;
        }

        public static string? ReadHello(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 3 || packet[1] != Hello || packet[2] == 0 || packet.Length < 3 + packet[2])
            {
                return null;
            }
            return Encoding.ASCII.GetString(packet.Slice(3, packet[2]));
        }

        public static void WriteAudioHeader(Span<byte> packet, ushort seq, int count, bool end)
        {
            packet[0] = MatchNetVoice.MsgVoice;
            packet[1] = Audio;
            BinaryPrimitives.WriteUInt16LittleEndian(packet[2..], seq);
            packet[4] = (byte)(count | (end ? EndFlag : 0));
        }

        // each frame of an audio packet: (seq, opus bytes, last frame of the packet, the packet ends a spurt)
        public delegate void FrameSink(ushort seq, ReadOnlySpan<byte> frame, bool last, bool end);

        // false when the packet is malformed; endAfter: an empty packet with the end flag (the spurt's last frames had already gone): the seq before its own
        public static bool ReadAudio(ReadOnlySpan<byte> packet, FrameSink sink, Action<ushort>? endAfter = null)
        {
            if (packet.Length < AudioHeader || packet[1] != Audio)
            {
                return false;
            }
            ushort seq = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
            int count = packet[4] & 0x7F;
            bool end = (packet[4] & EndFlag) != 0;
            if (count > 8)
            {
                return false;
            }
            if (count == 0)
            {
                if (end)
                {
                    endAfter?.Invoke((ushort)(seq - 1));
                }
                return end;
            }
            int at = AudioHeader;
            for (int i = 0; i < count; i++)
            {
                if (at >= packet.Length)
                {
                    return false;
                }
                int len = packet[at++];
                if (len > VoiceFormat.MaxFrameBytes || at + len > packet.Length)
                {
                    return false;
                }
                sink((ushort)(seq + i), packet.Slice(at, len), i == count - 1, end);
                at += len;
            }
            return true;
        }

        // seq a minus seq b, wrapping (-32768..32767)
        public static int Diff(ushort a, ushort b) => (short)(a - b);
    }
}
