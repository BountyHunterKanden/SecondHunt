using System;
using System.Collections.Generic;

namespace MphRecomp.Multiplayer.Net
{
    // What MatchNetDriver needs from a match's networking, whatever carries it: LanSession (one host + one joiner on
    // a Wi-Fi network) or NetMatchSession (one host + up to 7 joiners through the online relay, docs/ONLINE_PLAY.md
    // section 9). The host takes each remote seat's newest input and sends one snapshot per frame to every device; a
    // client takes the newest snapshot and sends its own input.
    public interface IMatchNet
    {
        bool IsHost { get; }
        // "LAN" or "online" (status lines)
        string Kind { get; }

        // host: the seats played on other devices
        IReadOnlyList<int> RemoteSeats { get; }
        bool TryTakeInput(int seat, out NetInput input);
        // host: inputs received from this seat's device, and in all
        int InputsFrom(int seat);
        int InputsReceived { get; }
        // host: milliseconds since this seat's device was last heard (a BYE makes it huge); client: since the host was
        // (seat ignored)
        long SilentMs(int seat);
        void SendSnapshot(Snapshot snap);

        // client
        bool TryTakeSnapshot(out Snapshot snap);
        int SnapshotsReceived { get; }
        void SendInput(in NetInput input);

        // something was heard from the other side(s) in the last few seconds
        bool PeerAlive { get; }

        // the replay link's side channel (Replays O10: identities in, the host's file out): one reserved first byte
        // (MsgSide) that goes straight to SideReceived without parsing, so the match protocols can change around it.
        // host: to that seat's device; client: to the host (seat ignored). Payloads under the transport's MaxPayload
        // (1400 on the relay); the replay link chunks and acks itself.
        void SendSide(int seat, ReadOnlySpan<byte> payload);
        // fromSeat (client: 0), raised on the receive thread
        event Action<int, byte[]>? SideReceived;
    }

    public static class MatchNetSide
    {
        public const byte MsgSide = 0x40;
    }

    // Voice O8: voice packets ride the online match transport directly (VoiceChat on OnlineMatch.Link); the match
    // session ignores them and they're never recorded
    public static class MatchNetVoice
    {
        public const byte MsgVoice = 0x41;
    }

    // the match packets' carrier: datagrams to a slot (0 = the host's device) or to every other slot
    public delegate void DatagramHandler(int fromSlot, ReadOnlySpan<byte> payload);

    public interface IDatagramTransport : IDisposable
    {
        int LocalSlot { get; }
        int MaxPayload { get; }
        void Send(int slot, ReadOnlySpan<byte> payload);
        void SendAll(ReadOnlySpan<byte> payload);
        // raised on the transport's receive thread
        event DatagramHandler? Received;
    }
}
