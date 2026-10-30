using System;
using System.Globalization;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace SecondHunt.MatchHost
{
    // The 0.3.6 netcode switches this host takes from the server that started it (SecondHunt.Server HubServed.cs passes its
    // --served-* values on), so a served match can be set back without a new app:
    //   --snapshot-hz <60|30|20>      snapshots a second to a joiner on a good link (NetMatchSession.SnapshotIntervalNormal;
    //                                 a struggling joiner still drops to 30 or 20 by itself); default 60
    //   --client-position <on|off>    take the guests' own positions (MatchSettings.ClientPosition, Net/ClientPosition.cs);
    //                                 default on
    //   --fruity-pin <on|off>         pin a guest's seat to its reports before and after the step, speed from the gaps (ClientPosition.Pin); off
    //   --fruity-hold <on|off>        hold a seat at its last pinned place on a frame with no input (ClientPosition.Hold); off
    //   --latest-input <on|off>       run only the newest waiting input of a seat (NetMatchSession.LatestInputOnly); off (empties the pacing buffer)
    //   --catchup-cap <steps>         most steps run to catch up in a pass, a gap over 0.25 s not caught up (0 = off); off
    //   --fruity-p1 <on|off>          Fruity Prime's movement sync for the whole match, host and joiners (MatchSettings.FruitySync,
    //                                 Net/FruitySync.cs; docs/FRUITY_PORT.md P1): replaces the four above and their loop; on
    //   --position-limit <units>      the furthest a guest's report may be from this host's copy (ClientPosition.Limit); 4
    internal static partial class Program
    {
        private static int _snapshotInterval = 1;
        private static bool _clientPosition = true;
        private static int _catchUpCap = 0; // off 10-20 with the other Fruity pieces
        private static bool _fruityP1 = true; // on 10-20; --fruity-p1 off for the old path
        private const int FruityCatchUp = 5; // (their server loop: at most 5 steps a pass, the rest dropped)

        // Parse: one of the options above (false = not one of them)
        private static bool ParseNetcode(string key, Func<string> next)
        {
            switch (key)
            {
            case "--snapshot-hz":
                int hz = Int32.Parse(next(), CultureInfo.InvariantCulture);
                if (hz is not (60 or 30 or 20)) throw new ArgumentException("--snapshot-hz is 60, 30 or 20");
                _snapshotInterval = 60 / hz;
                return true;
            case "--client-position":
                string on = next();
                if (on is not ("on" or "off")) throw new ArgumentException("--client-position is on or off");
                _clientPosition = on == "on";
                return true;
            case "--fruity-pin": ClientPosition.Pin = OnOff(key, next()); return true;
            case "--fruity-hold": ClientPosition.Hold = OnOff(key, next()); return true;
            case "--fruity-p1": _fruityP1 = OnOff(key, next()); return true;
            case "--latest-input": NetMatchSession.LatestInputOnly = OnOff(key, next()); return true;
            case "--catchup-cap":
                _catchUpCap = Int32.Parse(next(), CultureInfo.InvariantCulture); // (0 = off: catch up without limit)
                if (_catchUpCap < 0 || _catchUpCap > 60) throw new ArgumentException("--catchup-cap is 0 (off) to 60 steps");
                return true;
            case "--position-limit":
                float limit = Single.Parse(next(), CultureInfo.InvariantCulture);
                if (!(limit > 0 && limit <= 50)) throw new ArgumentException("--position-limit is above 0, at most 50 units");
                ClientPosition.Limit = limit;
                return true;
            }
            return false;
        }

        private static bool OnOff(string key, string value)
        {
            if (value is not ("on" or "off")) throw new ArgumentException(key + " is on or off");
            return value == "on";
        }

        // a new session (standalone or --control): its snapshot rate, before any START
        internal static void Netcode(NetMatchSession net)
        {
            net.SnapshotIntervalNormal = _snapshotInterval;
            Log($"netcode: snapshots {60 / _snapshotInterval} Hz, client position {(_clientPosition ? $"on (limit {ClientPosition.Limit:0.##})" : "off")}, "
                + $"pin {(ClientPosition.Pin ? "on" : "off")}, hold {(ClientPosition.Hold ? "on" : "off")}, latest input {(NetMatchSession.LatestInputOnly ? "on" : "off")}, "
                + $"catch-up cap {(_catchUpCap > 0 ? _catchUpCap.ToString() : "off")}, fruity P1 {(_fruityP1 ? "on" : "off")}");
        }

        // Play, each match: the rule as the server set it (only this host checks reports: nothing goes to the players)
        private static void Netcode(MatchSettings settings)
        {
            settings.ClientPosition = _clientPosition ? null : false;
        }
    }
}
