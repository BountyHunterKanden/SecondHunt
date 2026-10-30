using System;
using System.Linq;

namespace MphRecomp.Multiplayer.Net
{
    public sealed partial class NetMatchSession
    {
        // host: each seat's round trip as this host measures it (its snapshots to the joiner's ack), -1 = not a joiner's
        // seat or not measured yet (SeatPings.cs)
        public int[] SeatRtts(int seats)
        {
            int[] rtts = new int[seats];
            Array.Fill(rtts, -1);
            lock (_lock)
            {
                foreach (Joiner j in _joiners.Where(j => !j.Left && j.Seat >= 0 && j.Seat < seats))
                {
                    rtts[j.Seat] = j.RttMs;
                }
            }
            return rtts;
        }
    }
}
