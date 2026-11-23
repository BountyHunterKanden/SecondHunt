using System;
using System.Collections.Generic;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // Fruity P2 shot events (docs/FRUITY_PORT.md, 2026-11-13; Fruity Prime's shot events, as its intents carry them). A
    // match host with FruitySync.ShotsMark fires a device's seat from the shots the device fired (MphRead
    // PlayerNetShotEvents.cs), not from its trigger. The device's input packets carry them in an 'S' trailer after the
    // 'L' one: [0x53][n][n x NetShotEvent], its unacked events, each stamped with the input it was first sent with. An event
    // leaves the device's list once the host has applied that input or a later one (Snapshot.InputAck: the packet that
    // brought that input carried the event), or after FreshFrames inputs; at most Capacity ride along. The host keeps each
    // event once (by its number, a joiner at a time), drops those in a stale-life packet (NetMatchSession.Lives.cs) and hands
    // the new ones to the match before each step (TakeShotEvents, MatchNetDriver). A device whose packets carry the trailer
    // (even an empty one) has its seat driven by events from then on; an older device sends none and stays on its trigger.
    public sealed partial class NetMatchSession
    {
        private const byte ShotsTrailerMark = 0x53; // 'S'
        private const int ShotsTrailerMax = 2 + PlayerEntity.NetShotCapacity * NetShotEvent.Size;

        // client (game thread): the events not known to have reached the host, and the host's newest applied input
        private readonly List<NetShotEvent> _shotHistory = new();
        private int _shotAck = -1;

        // client, TryTakeSnapshot: the newest input the host applied for this seat
        private void ShotsSeen(Snapshot snap)
        {
            if (snap.InputAck >= 0)
            {
                _shotAck = snap.InputAck;
            }
        }

        // client, LivesSeen: a new life sends none of the old one's events
        private void ShotsNewLife()
        {
            _shotHistory.Clear();
            PlayerEntity.NetShotEventsNew.Clear();
        }

        private void ShotsClientReset()
        {
            _shotHistory.Clear();
            _shotAck = -1;
        }

        // client, SendInput: the shot trailer, stamped with this input's sequence (a host without ShotsMark gets none)
        private int WriteShots(Span<byte> d, ushort inputSeq)
        {
            if (!FruitySync.On || !FruitySync.HostShots || d.Length < 2)
            {
                PlayerEntity.NetShotEventsNew.Clear();
                return 0;
            }
            foreach (NetShotEvent ev in PlayerEntity.NetShotEventsNew)
            {
                _shotHistory.Add(ev with { InputSeq = inputSeq });
            }
            PlayerEntity.NetShotEventsNew.Clear();
            int ack = _shotAck;
            _shotHistory.RemoveAll(ev => ack >= 0 && (short)(ev.InputSeq - (ushort)ack) <= 0
                || (short)(inputSeq - ev.InputSeq) > PlayerEntity.NetShotFreshFrames);
            if (_shotHistory.Count > PlayerEntity.NetShotCapacity)
            {
                _shotHistory.RemoveRange(0, _shotHistory.Count - PlayerEntity.NetShotCapacity);
            }
            int n = Math.Min(_shotHistory.Count, (d.Length - 2) / NetShotEvent.Size);
            int first = _shotHistory.Count - n;
            d[0] = ShotsTrailerMark;
            d[1] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                _shotHistory[first + i].Write(d[(2 + i * NetShotEvent.Size)..]);
            }
            return 2 + n * NetShotEvent.Size;
        }

        // host, HostReceive (under the lock): the packet's new events into the joiner's pending list (none from a stale
        // life's packet). `rest` = the bytes after the input records
        private static void ShotsReceive(Joiner j, ReadOnlySpan<byte> rest, bool stale)
        {
            rest = rest[ClientPosition.TrailerLength(rest)..];
            if (rest.Length >= LivesTrailerSize && rest[0] == LivesTrailerMark)
            {
                rest = rest[LivesTrailerSize..];
            }
            if (rest.Length < 2 || rest[0] != ShotsTrailerMark)
            {
                return;
            }
            j.ShotsDriven = true;
            if (stale)
            {
                return;
            }
            int n = Math.Min(rest[1], (rest.Length - 2) / NetShotEvent.Size);
            for (int i = 0; i < n; i++)
            {
                NetShotEvent ev = NetShotEvent.Read(rest[(2 + i * NetShotEvent.Size)..]);
                if (ev.Seq == 0)
                {
                    continue;
                }
                // the 16-bit number unwrapped against the newest seen
                int seq = j.ShotNewest < 0 ? ev.Seq : j.ShotNewest + (short)(ev.Seq - (ushort)j.ShotNewest);
                if (seq <= j.ShotNewest - j.ShotSeen.Length / 2 || j.ShotSeen[seq & (j.ShotSeen.Length - 1)] == seq)
                {
                    continue;
                }
                j.ShotSeen[seq & (j.ShotSeen.Length - 1)] = seq;
                j.ShotNewest = Math.Max(j.ShotNewest, seq);
                j.ShotsPending.Add(ev);
                j.ShotEvents++;
            }
        }

        // a joiner's per-match event state (ResetMatchState: each match, and a device back in the running match)
        private static void ShotsReset(Joiner j)
        {
            j.ShotsDriven = false;
            Array.Fill(j.ShotSeen, Int32.MinValue);
            j.ShotNewest = -1;
            j.ShotsPending.Clear();
        }

        // host, once per frame per remote seat (MatchNetDriver): the seat's new events, in order; true = its device sends
        // events (the seat fires them, not its trigger)
        public bool TakeShotEvents(int seat, List<NetShotEvent> into)
        {
            into.Clear();
            lock (_lock)
            {
                foreach (Joiner j in _joiners)
                {
                    if (j.Seat == seat && !j.Left)
                    {
                        into.AddRange(j.ShotsPending);
                        j.ShotsPending.Clear();
                        return j.ShotsDriven;
                    }
                }
                return false;
            }
        }

        // host: events received, all joiners (the PC tests' check)
        public int ShotEventsReceived
        {
            get
            {
                lock (_lock)
                {
                    int sum = 0;
                    foreach (Joiner j in _joiners)
                    {
                        sum += j.ShotEvents;
                    }
                    return sum;
                }
            }
        }
    }
}
