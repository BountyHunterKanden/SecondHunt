using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using OpenTK.Mathematics;

namespace MphRead
{
    // -tplan [arena=Combat Hall] [time=30]: third-person true aim (PlayerHostAim.cs) in a real LAN match (owner 2026-11-13:
    // "true aim for third person for local matches too"). This process hosts (Samus + 1 bot) and starts a second one that
    // joins (-tplanjoin, Kanden); both play in third person, the shoulders in turn, as CampaignRenderer sends the codes.
    // Checks: the host applies the codes the guest sent (LAN protocol 26's input trailer), the guest's puppet of the host
    // carries the host's codes (Snapshot.AimCams), and the host's shots for the guest's seat go where the guest's own
    // prediction aimed them: far closer than vanilla's aim would be.
    internal static class LanTrueAimTest
    {
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo AimField = typeof(PlayerEntity).GetField("_aimPosition", Priv)!;
        private static readonly FieldInfo MuzzleField = typeof(PlayerEntity).GetField("_muzzlePos", Priv)!;
        private static readonly FieldInfo GunVecField = typeof(PlayerEntity).GetField("_gunVec1", Priv)!;

        public static void Host(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            string arenaName = args.FirstOrDefault(a => a.StartsWith("arena="))?[6..] ?? "Combat Hall";
            float time = Single.Parse(args.FirstOrDefault(a => a.StartsWith("time="))?[5..] ?? "30", CultureInfo.InvariantCulture);
            RoomMetadata arena = MatchArenas.Find(arenaName) ?? throw new ArgumentException($"no arena {arenaName}");
            MatchSettings template = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 2, 3); // (the joiner takes a bot's seat)
            template.TimeLimitSeconds = time;
            template.PointGoal = 99; // (no early end)
            using LanSession net = LanSession.Host(template, Paths.MphKey, "PC host", 3, port: 0);
            Console.WriteLine($"host: {net.Status}; Battle in {arena.InGameName}, {time} s");
            string dll = typeof(LanTrueAimTest).Assembly.Location;
            string exe = Environment.ProcessPath!;
            bool viaDotnet = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(exe, $"{(viaDotnet ? $"\"{dll}\" " : "")}-tplanjoin hostport={net.Port} time={time.ToString(CultureInfo.InvariantCulture)}")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!
            };
            using Process child = Process.Start(psi)!;
            var childOut = new List<string>();
            child.OutputDataReceived += (_, e) => { if (e.Data != null) lock (childOut) childOut.Add(e.Data); };
            child.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (childOut) childOut.Add("stderr: " + e.Data); };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            var waited = Stopwatch.StartNew();
            while (net.Joiners.Count(j => !j.Left) < 1 && waited.ElapsedMilliseconds < 30000)
            {
                Thread.Sleep(20);
            }
            if (net.Joiners.Count(j => !j.Left) < 1)
            {
                Console.WriteLine("host: nobody joined in 30 s");
                child.Kill();
                child.WaitForExit();
                lock (childOut)
                {
                    childOut.ForEach(l => Console.WriteLine("  guest| " + l));
                }
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine("host: seats " + string.Join(", ", net.Settings!.Players.Select((p, i) =>
                $"{i}:{p.Hunter}{(p.Bot ? " bot" : p.Remote ? " " + p.Nickname : " local")}")));
            int guestSeat = net.Settings.Players.FindIndex(p => p.Remote);

            var hostCodes = new HashSet<ushort>();
            var received = new List<ushort>();
            var hostAim = new Dictionary<int, (Vector3 Muzzle, Vector3 Aim, Vector3 Vanilla)>();
            int lastSeq = -1;
            CampaignHost host = CampaignHost.StartMatch(net.Settings);
            try
            {
                host.RemoteInputTap = (seat, input) => { if (seat == guestSeat) lastSeq = input.Seq; };
                host.TrueAimTap = (seat, code) => { if (seat == guestSeat) received.Add(code); };
                var driver = new MatchNetDriver(net, host);
                float dist = Single.MaxValue;
                ushort code = 0;
                RealTime(host, () =>
                {
                    CampaignInput input = Brain(host);
                    input.TrueAim = code;
                    lastSeq = -1;
                    driver.Step(input);
                    PlayerEntity g = PlayerEntity.Players[guestSeat];
                    if (lastSeq >= 0 && g.HostTrueAimCam != 0 && !g.IsAltForm && g.Health > 0)
                    {
                        Vector3 eye = g.CameraInfo.Position;
                        Vector3 vanilla = eye + (Vector3)GunVecField.GetValue(g)! * Fixed.ToFloat(g.Values.AimDistance);
                        hostAim[lastSeq] = ((Vector3)MuzzleField.GetValue(g)!, (Vector3)AimField.GetValue(g)!, vanilla);
                    }
                    // the app's camera after the step -> the next step's code: first person the first 5 s, then the
                    // shoulders in turn every 5 s
                    long f = host.Frame;
                    if (f < 300)
                    {
                        code = 0;
                        dist = Single.MaxValue;
                    }
                    else
                    {
                        code = ReplayTest.Code(host.Scene, host.Player, f / 300 % 2 == 1, ref dist);
                        if (code != 0) hostCodes.Add(code);
                    }
                }, time + 8);
                Console.WriteLine($"host: {host.Frame} frames, ended {host.Ended}, inputs received {net.InputsReceived}, {driver.StatusLine()}");
            }
            finally
            {
                host.Dispose();
            }
            if (!child.WaitForExit(30000))
            {
                child.Kill();
            }
            child.WaitForExit();
            List<string> lines;
            lock (childOut)
            {
                lines = childOut.ToList();
            }
            foreach (string line in lines.Where(l => !l.StartsWith("TPAIM ")))
            {
                Console.WriteLine("  guest| " + line);
            }

            int failures = 0;
            // 1: the guest's codes, as the host applied them
            var sent = new HashSet<ushort>(lines.Where(l => l.StartsWith("TPSENT ")).SelectMany(l => l[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => UInt16.Parse(s, NumberStyles.HexNumber)));
            var got = new HashSet<ushort>(received.Where(c => c != 0));
            int gotLeft = got.Count(c => (c & 0x4000) != 0);
            bool codesOk = sent.Count > 0 && got.IsSubsetOf(sent) && got.Count >= sent.Count * 9 / 10 && gotLeft > 0 && gotLeft < got.Count;
            Console.WriteLine($"guest's codes: {sent.Count} distinct sent, {got.Count} applied on the host ({got.Count - gotLeft} right, {gotLeft} left), "
                + $"{got.Count(c => !sent.Contains(c))} it never sent -> {(codesOk ? "OK" : "FAIL")}");
            failures += codesOk ? 0 : 1;
            // 2: the host's codes on the guest's puppet of it
            var seen = new HashSet<ushort>(lines.Where(l => l.StartsWith("TPPUPPET ")).SelectMany(l => l[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => UInt16.Parse(s, NumberStyles.HexNumber)).Where(c => c != 0));
            int seenLeft = seen.Count(c => (c & 0x4000) != 0);
            bool puppetOk = seen.Count > 0 && seen.IsSubsetOf(hostCodes) && seen.Count >= hostCodes.Count / 2 && seenLeft > 0 && seenLeft < seen.Count;
            Console.WriteLine($"host's codes: {hostCodes.Count} distinct, {seen.Count} on the guest's puppet ({seen.Count - seenLeft} right, {seenLeft} left), "
                + $"{seen.Count(c => !hostCodes.Contains(c))} the host never had -> {(puppetOk ? "OK" : "FAIL")}");
            failures += puppetOk ? 0 : 1;
            // 3: the guest seat's shot direction on the host vs the guest's own (same input seq), against vanilla's
            var off = new List<float>();
            var vanillaOff = new List<float>();
            foreach (string line in lines.Where(l => l.StartsWith("TPAIM ")))
            {
                float[] v = line[6..].Split(' ').Select(s => Single.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (!hostAim.TryGetValue((int)v[0], out var h))
                {
                    continue;
                }
                var gMuzzle = new Vector3(v[1], v[2], v[3]);
                var gAim = new Vector3(v[4], v[5], v[6]);
                off.Add(Angle(gAim - gMuzzle, h.Aim - h.Muzzle));
                vanillaOff.Add(Angle(gAim - gMuzzle, h.Vanilla - h.Muzzle));
            }
            if (off.Count < 300)
            {
                Console.WriteLine($"shot direction: only {off.Count} matched steps -> FAIL");
                failures++;
            }
            else
            {
                float med = Median(off), vMed = Median(vanillaOff);
                bool aimOk = med < 0.5f && med < vMed / 4;
                Console.WriteLine($"guest seat's shot direction, host vs the guest's own, {off.Count} steps: median {med:0.00} deg, 90% under "
                    + $"{Percentile(off, 0.9f):0.00}; vanilla's aim would be off by a median {vMed:0.00} deg -> {(aimOk ? "OK" : "FAIL")}");
                failures += aimOk ? 0 : 1;
            }
            Console.WriteLine(failures == 0 ? "PASS: LAN third-person true aim matches on the host and the guest"
                : $"FAIL: {failures} check(s) failed");
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        public static void Join(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            int hostPort = Int32.Parse(args.FirstOrDefault(a => a.StartsWith("hostport="))?[9..] ?? "0");
            float time = Single.Parse(args.FirstOrDefault(a => a.StartsWith("time="))?[5..] ?? "30", CultureInfo.InvariantCulture);
            using LanSession net = LanSession.Browse(Paths.MphKey);
            var found = Stopwatch.StartNew();
            LanHostInfo? target = null;
            while (target == null && found.ElapsedMilliseconds < 10000)
            {
                target = net.Hosts.FirstOrDefault(h => (hostPort == 0 || h.Address.Port == hostPort) && !h.Running);
                Thread.Sleep(50);
            }
            if (target == null || !net.Join(target.Address, Hunter.Kanden, "PC client"))
            {
                Console.WriteLine("client: couldn't join (" + net.Status + ")");
                return;
            }
            CampaignHost host = CampaignHost.StartMatch(net.Settings!);
            try
            {
                var driver = new MatchNetDriver(net, host);
                FieldInfo seqField = typeof(MatchNetDriver).GetField("_seq", Priv)!;
                const int hostSeat = 0; // (the host's own seat)
                var sent = new HashSet<ushort>();
                var puppet = new HashSet<ushort>();
                var aims = new List<string>();
                float dist = Single.MaxValue;
                ushort code = 0;
                RealTime(host, () =>
                {
                    CampaignInput input = Brain(host);
                    input.TrueAim = code;
                    int before = (int)seqField.GetValue(driver)!;
                    driver.Step(input);
                    int after = (int)seqField.GetValue(driver)!;
                    PlayerEntity me = host.Player;
                    if (code != 0)
                    {
                        sent.Add(code);
                        if (after == before + 1 && !me.IsAltForm && me.Health > 0)
                        {
                            var m = (Vector3)MuzzleField.GetValue(me)!;
                            var a = (Vector3)AimField.GetValue(me)!;
                            aims.Add(String.Create(CultureInfo.InvariantCulture, $"TPAIM {before} {m.X} {m.Y} {m.Z} {a.X} {a.Y} {a.Z}"));
                        }
                    }
                    puppet.Add(PlayerEntity.Players[hostSeat].HostTrueAimCam);
                    // this device's camera after the step: third person from 3 s, the shoulders in turn every 4 s
                    long f = host.Frame;
                    if (f < 180)
                    {
                        code = 0;
                        dist = Single.MaxValue;
                    }
                    else
                    {
                        code = ReplayTest.Code(host.Scene, me, f / 240 % 2 == 1, ref dist);
                    }
                }, time + 12);
                Console.WriteLine($"client: {host.Frame} frames, snapshots received {net.SnapshotsReceived}, own player pulled back "
                    + $"{host.LocalCorrections} times, ended {host.Ended}, {driver.StatusLine()}");
                Console.WriteLine("TPSENT " + String.Join(' ', sent.Select(c => c.ToString("X4"))));
                Console.WriteLine("TPPUPPET " + String.Join(' ', puppet.Select(c => c.ToString("X4"))));
                foreach (string line in aims)
                {
                    Console.WriteLine(line);
                }
            }
            finally
            {
                host.Dispose();
            }
        }

        // the scripted player: turn to the nearest opponent it can see, walk at it, shoot when lined up, FIRE to respawn
        private static CampaignInput Brain(CampaignHost host)
        {
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            PlayerEntity me = host.Player;
            if (me.Health == 0)
            {
                input.Buttons = CampaignButtons.Shoot;
                return input;
            }
            PlayerEntity? target = null;
            float best = Single.MaxValue;
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (p == me || !p.LoadFlags.TestFlag(LoadFlags.Active) || p.Health == 0)
                {
                    continue;
                }
                float d = (p.Position - me.Position).LengthSquared;
                if (d < best)
                {
                    best = d;
                    target = p;
                }
            }
            long f = host.Frame;
            if (target == null)
            {
                input.AimDelta = new System.Numerics.Vector2(-8, 0);
                return input;
            }
            Vector3 to = target.Position - me.Position;
            float yaw = MathHelper.RadiansToDegrees(MathF.Atan2(to.X, to.Z));
            float pitch = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Atan2(to.Y, MathF.Sqrt(to.X * to.X + to.Z * to.Z))), -80, 80);
            float dyaw = yaw - me.NetYaw;
            dyaw -= 360 * MathF.Floor((dyaw + 180) / 360);
            float dpitch = pitch - me.NetPitch;
            // a mouse-style delta (degrees = -delta / 4), with a sweep so the aim crosses near and far things
            float sweep = MathF.Sin(f / 40f) * 20;
            input.AimDelta = new System.Numerics.Vector2(-4 * Math.Clamp(dyaw + sweep, -12, 12), -4 * Math.Clamp(dpitch, -6, 6));
            input.Move = new System.Numerics.Vector2(f / 120 % 3 == 1 ? 1 : 0, f / 90 % 4 == 3 ? -1 : 1);
            if (MathF.Abs(dyaw) < 25 && f % 20 < 12)
            {
                input.Buttons = CampaignButtons.Shoot;
            }
            return input;
        }

        private static void RealTime(CampaignHost host, Action step, float seconds)
        {
            var clock = Stopwatch.StartNew();
            long frames = 0;
            while (!host.Ended && clock.Elapsed.TotalSeconds < seconds)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * 60);
                while (frames < due && !host.Ended)
                {
                    step();
                    frames++;
                }
                Thread.Sleep(1);
            }
        }

        private static float Angle(Vector3 a, Vector3 b)
        {
            float d = Vector3.Dot(a.Normalized(), b.Normalized());
            return MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(d, -1, 1)));
        }

        private static float Median(List<float> v) => Percentile(v, 0.5f);

        private static float Percentile(List<float> v, float q)
        {
            List<float> s = v.OrderBy(x => x).ToList();
            return s[Math.Clamp((int)(q * (s.Count - 1)), 0, s.Count - 1)];
        }
    }
}
