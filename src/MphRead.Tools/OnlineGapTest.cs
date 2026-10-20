using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRead
{
    // -onlinetest served=hub gap=1 (docs/FRUITY_PORT.md Step 2a, 2026-10-20): the "close on my screen, far on the other" check.
    // Every process of the PC match writes each step's player positions (GAPTRACE: Core Net/GapTrace.cs) on one clock; then,
    // for each player, every place the match host has it and every place the other joiner draws it is checked against the
    // path that player's own device really took over the last second ("path gap": how far from the nearest point of that
    // path). A sound served path only ever shows a place the player was a moment ago, so the gap stays near 0 whatever the
    // lag; one where the server's copy goes its own way (two writers) shows the owner's split. Samples within 1.5 s of a
    // death, respawn or teleport (own path jumps over 20 units) aren't counted. PASS = p99 <= 0.5 units and max <= 2.
    internal static partial class OnlineMatchTool
    {
        private const double GapWindowMs = 1000, GapSlackMs = 50, GapQuietMs = 1500;
        private const float GapP99 = 0.5f, GapMax = 2f, GapFar = 3f;

        private readonly record struct GapSample(double T, Vector3 P, bool Alive);

        private static Dictionary<(char Kind, int Seat), List<GapSample>> GapRead(string path)
        {
            var seats = new Dictionary<(char, int), List<GapSample>>();
            foreach (string line in File.ReadLines(path))
            {
                string[] f = line.Split(' ');
                if (f.Length < 9) continue; // (a line cut short as the process was stopped)
                if (f[8] == "1") continue; // (bots: nobody's own device)
                var key = (f[2][0], Int32.Parse(f[3], CultureInfo.InvariantCulture));
                if (!seats.TryGetValue(key, out List<GapSample>? list)) seats[key] = list = [];
                list.Add(new GapSample(Double.Parse(f[0], CultureInfo.InvariantCulture),
                    new Vector3(Single.Parse(f[4], CultureInfo.InvariantCulture), Single.Parse(f[5], CultureInfo.InvariantCulture),
                    Single.Parse(f[6], CultureInfo.InvariantCulture)), f[7] == "1"));
            }
            return seats;
        }

        private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len = ab.LengthSquared;
            float t = len < 1e-8f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / len, 0, 1);
            return (a + ab * t - p).Length;
        }

        // Fruity Prime's P1 movement gates (their Harness/NetFeatureCheck.cpp + tools/netcheck/compare-reports.py, ported
        // 2026-10-20 for docs/FRUITY_PORT.md P1), one player as drawn on another joiner:
        //   agreement: worst |server's place - drawn place| at the same moment <= 8 u (their WorstPositionGap);
        //   movement parity: owner walked >= 5 u -> drawn walked within max(1 u, 3%) (steps under 5 u, both present; the owner's
        //     stretch taken the pair's median lag earlier);
        //   jump parity: owner's height range >= 1.5 u -> drawn range within max(0.2 u, 5%);
        //   no pops: no drawn step over 9 u away from a death/respawn/teleport (they only print this; a gate here);
        //   liveness: drawn > 5 distinct places and > 2 u travelled.
        private const float FruityWorstGap = 8f, FruityPop = 9f, FruityStep = 5f;

        private static bool FruityChecks(int seat, string role, string viewer, List<GapSample> own, List<GapSample> seen,
            List<GapSample>? auth, List<double> events, double lag)
        {
            double from = Math.Max(own[0].T, seen[0].T), to = Math.Min(own[^1].T, seen[^1].T);
            bool Quiet(double t) => events.Any(e => Math.Abs(e - t) <= GapQuietMs);
            // (the owner's own walk is taken `lag` earlier: the drawn copy shows it that late, so both cover the same stretch;
            // both sides skip a moment near a death/teleport on either clock, or the drawn side counts what the owner's skips)
            (float Travel, float Height, int Distinct, int Pops, float Rough) Walk(List<GapSample> l, bool pops, double shift)
            {
                float travel = 0, lo = Single.MaxValue, hi = Single.MinValue, rough = 0, last = -1;
                int count = 0;
                var places = new HashSet<Vector3>();
                for (int i = 1; i < l.Count; i++)
                {
                    double t = l[i].T + shift;
                    if (t < from || t > to || !l[i].Alive || !l[i - 1].Alive || Quiet(t) || Quiet(t - lag))
                    {
                        last = -1;
                        continue;
                    }
                    float step = (l[i].P - l[i - 1].P).Length;
                    // (roughness: how much each step's length differs from the one before, per unit travelled; a steady walk
                    // ~0, one that stops and jumps (a stair) ~2)
                    if (last >= 0 && step < FruityStep && last < FruityStep) rough += MathF.Abs(step - last);
                    last = step;
                    if (step < FruityStep) travel += step;
                    if (pops && step > FruityPop) count++;
                    lo = Math.Min(lo, l[i].P.Y);
                    hi = Math.Max(hi, l[i].P.Y);
                    places.Add(l[i].P);
                }
                return (travel, hi > lo ? hi - lo : 0, places.Count, count, travel > 0 ? rough / travel : 0);
            }
            var did = Walk(own, false, lag);
            var saw = Walk(seen, true, 0);
            var srv = auth != null ? Walk(auth, false, 0) : default;
            float serverTravel = srv.Travel;
            float worst = 0;
            if (auth != null)
            {
                int a = 0;
                foreach (GapSample v in seen)
                {
                    if (!v.Alive || v.T < from || v.T > to || Quiet(v.T)) continue;
                    while (a < auth.Count - 1 && auth[a + 1].T <= v.T) a++;
                    if (auth[a].T > v.T || !auth[a].Alive) continue;
                    float g = (v.P - auth[a].P).Length;
                    worst = Single.IsFinite(g) ? Math.Max(worst, g) : Single.PositiveInfinity;
                }
            }
            var fails = new List<string>();
            if (auth == null) fails.Add("no server trace");
            else if (!(worst <= FruityWorstGap)) fails.Add($"agreement {worst:0.00} > {FruityWorstGap} u");
            if (did.Travel >= 5 && (saw.Travel < 5 || Math.Abs(saw.Travel - did.Travel) > Math.Max(1, 0.03f * did.Travel)))
                fails.Add("movement parity");
            if (did.Height >= 1.5f && Math.Abs(saw.Height - did.Height) > Math.Max(0.2f, 0.05f * did.Height)) fails.Add("jump parity");
            if (saw.Pops > 0) fails.Add($"{saw.Pops} pops");
            if (saw.Distinct <= 5 || saw.Travel <= 2) fails.Add("liveness");
            Console.WriteLine($"P1 seat {seat} ({role}) seen by {viewer}: agreement worst {worst:0.00} u; walked {did.Travel:0.0} / drawn "
                + $"{saw.Travel:0.0} u (server {serverTravel:0.0}); roughness {did.Rough:0.00} / {saw.Rough:0.00} (server {srv.Rough:0.00}); height {did.Height:0.00} / {saw.Height:0.00} u; pops {saw.Pops}"
                + (fails.Count == 0 ? "" : "  <-- FAIL " + String.Join(", ", fails)));
            return fails.Count == 0;
        }

        // the gap check over the traces in folder; one line per (player, viewer) and the verdict; true = PASS
        internal static bool GapCheck(string folder)
        {
            var traces = Directory.GetFiles(folder, "*.txt").ToDictionary(f => Path.GetFileNameWithoutExtension(f), GapRead);
            if (!traces.TryGetValue("host", out var server))
            {
                Console.WriteLine("FAIL gaptest: no match host trace (GAPTRACE didn't reach it)");
                return false;
            }
            bool pass = true;
            int checkedPairs = 0;
            foreach ((string role, var own) in traces.Where(t => t.Key != "host").OrderBy(t => t.Key))
            {
                foreach (((char kind, int seat), List<GapSample> path) in own.Where(s => s.Key.Kind == 'O'))
                {
                    // quiet times: a death/respawn or a teleport on the own path, and 1.5 s either side
                    var events = new List<double>();
                    for (int i = 1; i < path.Count; i++)
                    {
                        if (path[i].Alive != path[i - 1].Alive || !path[i].Alive
                            || (path[i].P - path[i - 1].P).Length > 20) events.Add(path[i].T);
                    }
                    var viewers = new List<(string Name, List<GapSample> Seen)>();
                    if (server.TryGetValue(('S', seat), out var s)) viewers.Add(("server", s));
                    foreach ((string other, var t) in traces.Where(t => t.Key != "host" && t.Key != role))
                    {
                        if (t.TryGetValue(('D', seat), out var d)) viewers.Add((other, d));
                    }
                    foreach ((string viewer, List<GapSample> seen) in viewers)
                    {
                        var gaps = new List<float>();
                        var raw = new List<float>();
                        var lags = new List<double>();
                        int e = 0, lo = 0;
                        foreach (GapSample v in seen)
                        {
                            if (!v.Alive || v.T < path[0].T + GapWindowMs || v.T > path[^1].T) continue;
                            while (e < events.Count && events[e] < v.T - GapQuietMs) e++;
                            if (e < events.Count && events[e] <= v.T + GapQuietMs) continue;
                            while (lo < path.Count - 1 && path[lo + 1].T < v.T - GapWindowMs) lo++;
                            float best = Single.MaxValue, now = Single.MaxValue;
                            double lag = 0;
                            for (int i = lo; i < path.Count - 1 && path[i].T <= v.T + GapSlackMs; i++)
                            {
                                float g = SegmentDistance(v.P, path[i].P, path[i + 1].P);
                                if (g < best)
                                {
                                    best = g;
                                    lag = v.T - path[i].T;
                                }
                                if (path[i].T <= v.T) now = (v.P - path[i].P).Length;
                            }
                            if (best == Single.MaxValue) continue;
                            gaps.Add(best);
                            raw.Add(now);
                            lags.Add(lag);
                        }
                        if (gaps.Count < 300)
                        {
                            Console.WriteLine($"GAP seat {seat} ({role}) seen by {viewer}: only {gaps.Count} samples");
                            pass = false;
                            continue;
                        }
                        checkedPairs++;
                        float P(List<float> l, double q) => l.OrderBy(x => x).ElementAt((int)Math.Min(l.Count - 1, q * l.Count));
                        float p99 = P(gaps, 0.99), max = gaps.Max();
                        bool ok = p99 <= GapP99 && max <= GapMax;
                        pass &= ok;
                        Console.WriteLine($"GAP seat {seat} ({role}) seen by {viewer}: {gaps.Count} samples, path gap p50 {P(gaps, 0.5):0.00} "
                            + $"p95 {P(gaps, 0.95):0.00} p99 {p99:0.00} max {max:0.00} u, over {GapFar} u {gaps.Count(g => g > GapFar) * 100.0 / gaps.Count:0.0}%; "
                            + $"same-time gap p50 {P(raw, 0.5):0.00} max {raw.Max():0.00} u; lag p50 {lags.OrderBy(x => x).ElementAt(lags.Count / 2):0} ms"
                            + (ok ? "" : "  <-- FAIL"));
                        if (viewer != "server")
                        {
                            pass &= FruityChecks(seat, role, viewer, path, seen, server.TryGetValue(('S', seat), out var auth) ? auth : null, events,
                                lags.OrderBy(x => x).ElementAt(lags.Count / 2));
                        }
                    }
                }
            }
            if (checkedPairs == 0)
            {
                Console.WriteLine("FAIL gaptest: no player traces to check");
                return false;
            }
            Console.WriteLine(pass ? $"PASS gaptest: every place shown was on the player's own path (p99 <= {GapP99} u, max <= {GapMax} u)"
                : $"FAIL gaptest: places shown off the player's own path (p99 > {GapP99} u or max > {GapMax} u)");
            return pass;
        }
    }
}
