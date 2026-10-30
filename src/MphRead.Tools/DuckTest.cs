using System;
using MphRecomp.Online.Voice;

namespace MphRead
{
    // -ducktest: drives the music ducking envelope (DuckEnvelope) without a clock and checks it (Thread C, C3, 2026-10-23):
    // a short burst doesn't duck, speech ducks smoothly to the duck gain, gaps between words don't pump it, it comes back after
    // the hold, and the toggle off keeps the music up.
    internal static class DuckTest
    {
        private static int s_fail;

        public static void Run()
        {
            const int step = 20;
            long t = 1000;

            // 1. a short burst (what VoiceChat reports for ~100 ms of sound: Speaking for 350 ms) never moves the gain
            var e = new DuckEnvelope();
            float min = 1;
            for (int i = 0; i < 400 / step; i++) { min = Math.Min(min, e.Step(t, i * step < DuckEnvelope.OnsetMs - step, true)); t += step; }
            for (int i = 0; i < 3000 / step; i++) { min = Math.Min(min, e.Step(t, false, true)); t += step; }
            Check("short burst: no duck", min == 1, $"min gain {min:F2}");

            // 2. steady speech: down to the duck gain, monotonic, no faster than the attack
            e = new DuckEnvelope();
            float prev = 1;
            bool mono = true, smooth = true;
            for (int i = 0; i < 2000 / step; i++)
            {
                float g = e.Step(t, true, true);
                t += step;
                mono &= g <= prev + 1e-6f;
                smooth &= prev - g <= (1 - DuckEnvelope.DuckGain) * step / DuckEnvelope.AttackMs + 1e-4f;
                prev = g;
            }
            Check("speech ducks to the duck gain", Math.Abs(prev - DuckEnvelope.DuckGain) < 1e-4f, $"gain {prev:F3}");
            Check("fade down is monotonic and smooth", mono && smooth, "");

            // 3. gaps between words (500 ms of silence) don't bring the music back
            float maxInGap = 0;
            for (int word = 0; word < 6; word++)
            {
                for (int i = 0; i < 500 / step; i++) { maxInGap = Math.Max(maxInGap, e.Step(t, false, true)); t += step; }
                for (int i = 0; i < 600 / step; i++) { e.Step(t, true, true); t += step; }
            }
            Check("gaps between words don't pump", maxInGap <= DuckEnvelope.DuckGain + 1e-4f, $"max gain in a gap {maxInGap:F2}");

            // 4. after the speech ends: held, then released slowly back to 1
            long end = t;
            float atHold = 0;
            while (t - end < DuckEnvelope.HoldMs - step) { atHold = e.Step(t, false, true); t += step; }
            Check("held after the speech", Math.Abs(atHold - DuckEnvelope.DuckGain) < 1e-4f, $"gain {atHold:F2} at {t - end} ms");
            prev = atHold;
            bool up = true, slow = true;
            for (int i = 0; i < 3000 / step; i++)
            {
                float g = e.Step(t, false, true);
                t += step;
                up &= g >= prev - 1e-6f;
                slow &= g - prev <= (1 - DuckEnvelope.DuckGain) * step / DuckEnvelope.ReleaseMs + 1e-4f;
                prev = g;
            }
            Check("comes back to full", prev == 1, $"gain {prev:F3}");
            Check("fade up is monotonic and slow", up && slow, "");

            // 5. toggle off: speech never ducks, and switching it off mid-duck brings the music back
            e = new DuckEnvelope();
            min = 1;
            for (int i = 0; i < 3000 / step; i++) { min = Math.Min(min, e.Step(t, true, false)); t += step; }
            Check("toggle off: no duck", min == 1, $"min gain {min:F2}");
            for (int i = 0; i < 1000 / step; i++) { e.Step(t, true, true); t += step; }
            float ducked = e.Gain;
            for (int i = 0; i < 1500 / step; i++) { e.Step(t, true, false); t += step; }
            Check("toggle off mid-duck: back to full", ducked < 1 && e.Gain == 1, $"ducked {ducked:F2} then {e.Gain:F2}");

            // 6. a long stall between two steps (a paused app) doesn't jump the gain
            e = new DuckEnvelope();
            e.Step(t, true, true);
            float after = e.Step(t + 60_000, true, true);
            Check("a stalled clock doesn't jump", after > 0.5f, $"gain {after:F2}");

            // 7. the speaking icon (VoiceGlyph, C4): drawn at the sizes the lobby / pop-up use, vertically symmetric, not empty or solid
            foreach (int n in new[] { 12, 24, 32 })
            {
                byte[] px = VoiceGlyph.Render(n);
                int lit = 0, asym = 0;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        int a = px[(y * n + x) * 4 + 3], b = px[((n - 1 - y) * n + x) * 4 + 3];
                        if (a > 127) lit++;
                        if (Math.Abs(a - b) > 40) asym++;
                    }
                }
                Check($"speaker icon {n}px", lit > n * n / 8 && lit < n * n / 2 && asym <= n / 2, $"{lit} lit of {n * n}, {asym} asymmetric");
                if (n == 24)
                {
                    for (int y = 0; y < n; y++)
                    {
                        var line = new System.Text.StringBuilder("    ");
                        for (int x = 0; x < n; x++) line.Append(px[(y * n + x) * 4 + 3] > 127 ? '#' : px[(y * n + x) * 4 + 3] > 40 ? '+' : '.');
                        Console.WriteLine(line);
                    }
                }
            }

            Console.WriteLine(s_fail == 0 ? "ducktest: all passed" : $"ducktest: {s_fail} FAILED");
            if (s_fail != 0) Environment.ExitCode = 1;
        }

        private static void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
            if (!ok) s_fail++;
        }
    }
}
