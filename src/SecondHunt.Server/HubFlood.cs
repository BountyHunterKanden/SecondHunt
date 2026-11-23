using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MphRecomp.Online;

namespace SecondHunt.Server
{
    // The request flood limit (server 0.10.3; IDEAS_SURVEY B 1.7 "general request flood limit"): every connection has a
    // token bucket (FloodBurst tokens, FloodPerSecond back each second) and each request costs a little (Cost: the
    // database-heavy ones more, a head-to-head report less). An empty bucket answers ErrorMsg "slow_down" without doing
    // the request; each refusal adds a strike (strikes fade at FloodPerSecond too), and past FloodDropAfter strikes the
    // connection is dropped and logged. Chat has its own limit on top (HubChat.cs).
    // Sized from measured app traffic (docs/ONLINE_PLAY.md section 13, 0.10.3): an app sends ~0.4 requests a second on the
    // hub (the 10 s refresh) and short bursts of tens at a login or a page change, so a real app never gets near it; a
    // client sending hundreds a second is cut off within a second or two. SECONDHUNT_FLOODSTATS=1 logs every connection's
    // counts when it closes (the measuring); a connection that was refused anything is always logged.
    public sealed partial class Hub
    {
        public static double FloodBurst = 100;
        public static double FloodPerSecond = 10;
        public static double FloodDropAfter = 200;
        public const string SlowDownCode = "slow_down";

        private static readonly bool s_floodStats = Environment.GetEnvironmentVariable("SECONDHUNT_FLOODSTATS") == "1";

        // what one request costs (1 = an ordinary one)
        public static double FloodCost(OnlineMsg msg) => msg switch
        {
            MatchResultReq => 0.25, // (an app sends every waiting report at login, one after another: never refuse those)
            PingReq or ListGamesReq or RosterReq or RecentReq => 1,
            SearchReq or ReplaySendReq or CloudPutReq => 3, // (a name search, a replay upload ticket, a cloud save of up to 40 KB)
            LeadersReq or StatsReq or HubReq or ReplayFetchReq or ReportReq or TurnCredsReq => 2,
            _ => 1,
        };

        private sealed class FloodMeter
        {
            private readonly long _start = Stopwatch.GetTimestamp();
            private long _last = Stopwatch.GetTimestamp();
            private double _tokens = FloodBurst;
            private double _strikes;
            private readonly Dictionary<string, int> _kinds = new();
            private long _second = -1, _tenth = -1;
            private int _inSecond, _inTen;

            public int Total { get; private set; }
            public int Refused { get; private set; }
            public int PeakSecond { get; private set; }
            public int PeakTen { get; private set; }
            public double Lowest { get; private set; } = FloodBurst;

            // false = refuse it ("slow_down"); drop = too many refusals: close the connection
            public bool Take(OnlineMsg msg, out bool drop)
            {
                long now = Stopwatch.GetTimestamp();
                double dt = (now - _last) / (double)Stopwatch.Frequency;
                _last = now;
                _tokens = Math.Min(FloodBurst, _tokens + dt * FloodPerSecond);
                _strikes = Math.Max(0, _strikes - dt * FloodPerSecond);
                Count(msg, now);
                double cost = FloodCost(msg);
                drop = false;
                if (_tokens >= cost)
                {
                    _tokens -= cost;
                    Lowest = Math.Min(Lowest, _tokens);
                    return true;
                }
                Lowest = 0;
                Refused++;
                _strikes += 1;
                drop = _strikes > FloodDropAfter;
                return false;
            }

            private void Count(OnlineMsg msg, long now)
            {
                Total++;
                string kind = msg.GetType().Name;
                _kinds[kind] = _kinds.GetValueOrDefault(kind) + 1;
                long ms = (now - _start) * 1000 / Stopwatch.Frequency;
                if (ms / 1000 != _second)
                {
                    _second = ms / 1000;
                    _inSecond = 0;
                }
                if (ms / 10_000 != _tenth)
                {
                    _tenth = ms / 10_000;
                    _inTen = 0;
                }
                PeakSecond = Math.Max(PeakSecond, ++_inSecond);
                PeakTen = Math.Max(PeakTen, ++_inTen);
            }

            public string Summary()
            {
                double secs = (Stopwatch.GetTimestamp() - _start) / (double)Stopwatch.Frequency;
                string top = string.Join(" ", _kinds.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key.Replace("Req", "")}={k.Value}"));
                return FormattableString.Invariant($"{Total} requests in {secs:0} s ({Total / Math.Max(1, secs):0.00}/s), ")
                    + FormattableString.Invariant($"peak {PeakSecond} in 1 s, {PeakTen} in 10 s, lowest bucket {Lowest:0}/{FloodBurst:0}, ")
                    + $"refused {Refused} [{top}]";
            }
        }

        // the Run loop: null = go ahead, else the refusal to send (drop: then close the connection)
        private static ErrorMsg? FloodCheck(FloodMeter meter, string me, OnlineMsg msg, out bool drop)
        {
            if (meter.Take(msg, out drop))
            {
                return null;
            }
            if (meter.Refused == 1)
            {
                Console.WriteLine($"flood: {me} is over the request limit ({msg.GetType().Name}): slow_down");
            }
            if (drop)
            {
                Console.WriteLine($"flood: dropping {me}: kept sending over the limit: {meter.Summary()}");
            }
            return new ErrorMsg { Code = SlowDownCode, Text = "too many requests: slow down." };
        }

        private static void FloodClosed(FloodMeter meter, string me)
        {
            if (s_floodStats || meter.Refused > 0)
            {
                Console.WriteLine($"flood stats: {me}: {meter.Summary()}");
            }
        }
    }
}
