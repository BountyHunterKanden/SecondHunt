using System;
using System.Diagnostics;
using MphRead;
using MphRecomp.Campaign;

namespace MphRecomp.Replay
{
    // Plays a replay with no drawing, as fast as the sim runs, checking every step: whether it plays exactly on this
    // build and device (docs/REPLAY_MODE.md: R2c's golden replays on a device, R2b's cross-device checks, and later R3's
    // "verify before watching"). The same CampaignHost.Step as watching it; drawing never changes a match (R1).
    public static class ReplayVerify
    {
        public readonly record struct Result(bool Exact, int Steps, int Recorded, int FailedAt, string? Refused,
            double MsPerStep)
        {
            public string Verdict => Refused != null ? "REFUSED: " + Refused
                : FailedAt >= 0 ? $"NOT EXACT from step {FailedAt} of {Recorded} ({MsPerStep:0.000} ms/step)"
                : !Exact ? $"NOT EXACT: ended at step {Steps}, the recording has {Recorded}"
                : $"exact, {Steps} steps ({MsPerStep:0.000} ms/step)";
        }

        // afterStep(step, scene): after each step, before its check (diagnosis: ReplayState.Dump)
        public static Result Run(ReplayFile file, Action<int, Scene>? afterStep = null)
        {
            string? why = ReplayPlayer.CheckBeforeStart(file);
            if (why != null)
            {
                return new Result(false, 0, file.Steps.Count, -1, why, 0);
            }
            var player = new ReplayPlayer(file);
            CampaignHost host = player.Start(collectDrawItems: false);
            try
            {
                why = player.CheckAfterStart();
                if (why != null)
                {
                    return new Result(false, 0, file.Steps.Count, -1, why, 0);
                }
                var sw = Stopwatch.StartNew();
                while (!host.Ended && player.TryNext(out CampaignInput input))
                {
                    host.Step(input);
                    afterStep?.Invoke(player.Position, host.Scene);
                    if (!player.Check(host.Scene))
                    {
                        break;
                    }
                }
                double ms = sw.Elapsed.TotalMilliseconds / Math.Max(1, host.Frame);
                bool exact = player.FailedAt < 0 && host.Frame == file.Steps.Count && host.Ended == file.Complete;
                return new Result(exact, (int)host.Frame, file.Steps.Count, player.FailedAt, null, ms);
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
