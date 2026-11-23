using System;
using System.Buffers.Binary;
using MphRead.Entities;

namespace MphRecomp.Multiplayer.Net
{
    // Fruity P4 lives (docs/FRUITY_PORT.md, 2026-11-13; Fruity Prime's NetPlayerLifecycle, ServerSlotIdentity and
    // SessionMatchIdentity, as its ApplyIntent uses them). Each seat's life is three numbers: the match (bumps at each
    // SendStart), the slot's generation (bumps when its device rejoins the running match) and the life id
    // (PlayerEntity.NetLifeId, bumps at each spawn). A match host with FruitySync.LivesMark sends them in every snapshot
    // (Snapshot's lives block); its devices stamp each input packet with the life they last saw for their own seat (a 7-byte
    // 'L' trailer after ClientPosition's 'P' one), and the host drops a packet stamped with a life the seat no longer has.
    // So inputs a device made while it still saw its old life (it saw itself dead, or in the match before a rejoin) never
    // steer the new one: the seat stands at its spawn for about a round trip, then follows the device again (Fruity drops
    // such intents the same way). At a life change the host clears the seat's queued inputs and, if nothing new is there the
    // next frame, gives it one neutral input (MatchRemote.HoldRemoteForLife): no move or stick, with the buttons kept.
    // That last part is ours, not Fruity's (its ApplyIntent clears every button): a button held through the respawn stays
    // held, not pressed again, so vanilla's NoShotsFired still keeps a held trigger from firing (PlayerInput.cs).
    // Claims and reported positions stay fenced by the aim epoch as before (it also bumps at every spawn).
    public sealed partial class NetMatchSession
    {
        private const byte LivesTrailerMark = 0x4C; // 'L'
        private const int LivesTrailerSize = 7;

        // host: this match's id (never 0: a device's stamp of 0 means it hasn't seen a snapshot of this match yet)
        private ushort _matchId;

        // client: the life last seen for this device's own seat (game thread; reset with each match's state)
        private ushort _stampMatch, _stampGen, _stampLife;

        private static bool HostLivesOn => FruitySync.On;

        // host, SendStart (under the lock): a new match id for every joiner
        private void LivesStart()
        {
            _matchId = (ushort)(_matchId == UInt16.MaxValue ? 1 : _matchId + 1);
            foreach (Joiner j in _joiners)
            {
                j.Life = 0;
                j.LifeReset = false;
                j.Stamping = false;
                j.LifeDrops = 0;
            }
        }

        // host, OnJoin (under the lock): a device back in the running match is a new generation of its slot, so whatever its
        // last connection still has on the way is stale
        private static void LivesRejoin(Joiner j)
        {
            j.Generation = (ushort)(j.Generation == UInt16.MaxValue ? 1 : j.Generation + 1);
            j.LifeReset = false;
        }

        // host, SendSnapshot (under the lock, every frame): stamps the lives into the snapshot and notes each joiner's life
        // change (its queued inputs were made for the old life)
        private void LivesStamp(Snapshot snap)
        {
            if (!HostLivesOn || _matchId == 0)
            {
                return;
            }
            snap.HasLives = true;
            snap.MatchId = _matchId;
            foreach (Joiner j in _joiners)
            {
                if (j.Left || j.Seat < 0 || j.Seat >= snap.Seats.Length)
                {
                    continue;
                }
                snap.Seats[j.Seat].Generation = j.Generation;
                ushort life = snap.Seats[j.Seat].LifeId;
                if (life == j.Life)
                {
                    continue;
                }
                bool first = j.Life == 0;
                j.Life = life;
                if (j.Stamping && !first)
                {
                    j.Queue.Clear();
                    j.LifeReset = true;
                }
            }
        }

        // host, HostReceive (under the lock): false = the packet is for the seat's current life (or carries no stamp: an
        // older device, which is never fenced). `rest` = the bytes after the input records
        private bool LivesStale(Joiner j, ReadOnlySpan<byte> rest)
        {
            rest = rest[ClientPosition.TrailerLength(rest)..];
            if (rest.Length < LivesTrailerSize || rest[0] != LivesTrailerMark)
            {
                return false;
            }
            j.Stamping = true;
            ushort match = BinaryPrimitives.ReadUInt16LittleEndian(rest[1..]);
            ushort gen = BinaryPrimitives.ReadUInt16LittleEndian(rest[3..]);
            ushort life = BinaryPrimitives.ReadUInt16LittleEndian(rest[5..]);
            if (match == _matchId && gen == j.Generation && life == j.Life)
            {
                return false;
            }
            j.LifeDrops++;
            return true;
        }

        // host, once per frame per remote seat when TryTakeInput found nothing: true once after the seat's life changed
        // (the driver then gives it the neutral input, MatchRemote.HoldRemoteForLife)
        public bool TakeLifeReset(int seat)
        {
            lock (_lock)
            {
                foreach (Joiner j in _joiners)
                {
                    if (j.Seat == seat && !j.Left && j.LifeReset)
                    {
                        j.LifeReset = false;
                        return true;
                    }
                }
                return false;
            }
        }

        // host: a new input came for the seat (its new life's, or no stamps): no neutral input owed
        private static void LivesInputTaken(Joiner j)
        {
            j.LifeReset = false;
        }

        // host: input packets dropped as stale, all joiners this match (the PC tests' check)
        public int LifeDrops
        {
            get
            {
                lock (_lock)
                {
                    int sum = 0;
                    foreach (Joiner j in _joiners)
                    {
                        sum += j.LifeDrops;
                    }
                    return sum;
                }
            }
        }

        // client, TryTakeSnapshot (game thread): the life the newest snapshot gives this device's seat; a new one empties
        // the repeat ring (the inputs before it belong to the old life, and the new stamp would pass them)
        private void LivesSeen(Snapshot snap)
        {
            int seat = Settings?.LocalSeat ?? -1;
            if (!snap.HasLives || seat < 0 || seat >= snap.Seats.Length)
            {
                return;
            }
            ushort gen = snap.Seats[seat].Generation, life = snap.Seats[seat].LifeId;
            if (snap.MatchId == _stampMatch && gen == _stampGen && life == _stampLife)
            {
                return;
            }
            _stampMatch = snap.MatchId;
            _stampGen = gen;
            _stampLife = life;
            _recentCount = 0;
            ShotsNewLife(); // (and the old life's shot events: Fruity P2, NetMatchSession.Shots.cs)
        }

        private void LivesClientReset()
        {
            _stampMatch = _stampGen = _stampLife = 0;
        }

        // client, SendInput: the life trailer (a host without LivesMark gets none)
        private int WriteLives(Span<byte> d)
        {
            if (!FruitySync.On || !FruitySync.HostLives || d.Length < LivesTrailerSize)
            {
                return 0;
            }
            d[0] = LivesTrailerMark;
            BinaryPrimitives.WriteUInt16LittleEndian(d[1..], _stampMatch);
            BinaryPrimitives.WriteUInt16LittleEndian(d[3..], _stampGen);
            BinaryPrimitives.WriteUInt16LittleEndian(d[5..], _stampLife);
            return LivesTrailerSize;
        }
    }
}
