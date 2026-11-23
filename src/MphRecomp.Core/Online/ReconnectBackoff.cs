using System;

namespace MphRecomp.Online
{
    // How long the app waits before it tries the server again after a lost connection or a failed connect (0.10.3,
    // IDEAS_SURVEY B "hub auto-reconnect"): 1 s, 2 s, 4 s ... up to 30 s, each with jitter (half to all of it), so a few
    // hundred apps that lost a restarting server don't all knock at the same instant.
    public static class ReconnectBackoff
    {
        public const int FirstMs = 1_000;
        public const int MaxMs = 30_000;

        // attempt 0 = the first try after the drop
        public static int DelayMs(int attempt, Random rng)
        {
            double full = Math.Min(MaxMs, FirstMs * Math.Pow(2, Math.Clamp(attempt, 0, 16)));
            return (int)(full * (0.5 + 0.5 * rng.NextDouble()));
        }

        // a server restart (its notice or its closing message): it takes a few seconds to come back, so start a little later
        public const int AfterRestartAttempt = 2;
    }
}
