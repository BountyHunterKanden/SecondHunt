using System;
using System.Linq;
using System.Text;
using MphRead.Entities;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.Campaign
{
    // Per seat, how far back each remote input's view really was (2026-10-19: lots of hits missing for certain
    // players, not ping: one player one match, another the next): the rewind it needed (host frame - its ViewTick, before the
    // cap), how many went past LagCompensation.MaxRewind (those shots are tested against too-new poses), and how many came
    // with a stale aim epoch (the host ignores their aim until the client has the re-aim). Counted in SetRemoteInput; the
    // match host logs and clears them with its stats (SecondHunt.MatchHost Program.Play). Nothing here changes the match.
    public sealed partial class CampaignHost
    {
        private struct LagStat
        {
            public int Inputs, Capped, Stale, Max;
            public long Sum;
        }

        private LagStat[] _lagStats = new LagStat[16];
        // how many inputs needed each rewind, in 6-frame bins (0-5, 6-11, ... 30-35, 36+: the histogram the other forks
        // found their cap from)
        private const int LagBin = 6, LagBins = 7;
        private readonly int[,] _lagBins = new int[16, LagBins];

        private void CountLag(int seat, in NetInput input, int epoch)
        {
            if (seat < 0 || seat >= _lagStats.Length || input.ViewTick == 0)
            {
                return;
            }
            ref LagStat s = ref _lagStats[seat];
            int need = (int)Math.Max(0, Frame - input.ViewTick);
            s.Inputs++;
            s.Sum += need;
            s.Max = Math.Max(s.Max, need);
            _lagBins[seat, Math.Min(need / LagBin, LagBins - 1)]++;
            if (need > LagCompensation.MaxRewind) s.Capped++;
            if ((byte)epoch != input.AimEpoch) s.Stale++;
        }

        // "seat 2: rewind need avg 7.1 max 15 frames, capped 12%, aim stale 1%; ..." for the seats heard since the last call
        public string LagStatsLine(bool reset)
        {
            var line = new StringBuilder();
            for (int i = 0; i < _lagStats.Length; i++)
            {
                LagStat s = _lagStats[i];
                if (s.Inputs == 0) continue;
                if (line.Length > 0) line.Append("; ");
                line.Append($"seat {i}: rewind need avg {(double)s.Sum / s.Inputs:0.0} max {s.Max} frames, capped "
                    + $"{100.0 * s.Capped / s.Inputs:0}%, aim stale {100.0 * s.Stale / s.Inputs:0}%, by 6 frames ["
                    + string.Join(" ", Enumerable.Range(0, LagBins).Select(b => _lagBins[i, b])) + "]");
            }
            if (reset)
            {
                Array.Clear(_lagStats);
                Array.Clear(_lagBins);
            }
            return line.Length > 0 ? line.ToString() : "no remote views";
        }
    }
}
