using System;
using System.Buffers.Binary;

namespace MphRecomp.Online
{
    // The match relay's UDP format (docs/ONLINE_PLAY.md section 7). Every match packet goes through the relay, so no
    // player ever sees another's IP address. The relay never reads the payload.
    //   device -> relay: 'R', token (8 bytes, from the server's RelayInfo), destination slot (0-8, ToAll, or KeepAlive), payload
    //   relay -> device: 'R', source slot, payload            (a KeepAlive is answered with 'R', KeepAlive: the relay's ping)
    public static class RelayWire
    {
        public const byte Magic = (byte)'R';
        public const byte ToAll = 0xFF;
        public const byte KeepAlive = 0xFE;
        // a room's slots: up to MaxPlayers players, plus a served game's match host on slot 0 (server 0.8.2,
        // docs/DEDICATED_SERVERS.md: 8 players in a served game, on slots 1-8; devices only ever send to slot 0 or ToAll)
        public const int MaxSlots = 9;
        public const int MaxPlayers = 8;
        public const int SendHeader = 10;
        public const int ReceiveHeader = 2;
        public const int MaxPayload = 1400;

        public static void WriteHeader(Span<byte> dest, ulong token, byte to)
        {
            dest[0] = Magic;
            BinaryPrimitives.WriteUInt64LittleEndian(dest[1..], token);
            dest[9] = to;
        }

        public static ulong ParseToken(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            return bytes.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
        }

        public static string TokenText(ulong token)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, token);
            return Convert.ToBase64String(bytes);
        }
    }
}
