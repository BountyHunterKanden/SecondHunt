using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Replay;

namespace MphRead
{
    // -replaynettest [mode=] [arena=] [time=] [seed=] [guests=2] [bots=1] [loss=0.2]: replays of LAN / online matches (docs/ONLINE_PLAY.md
    // O10, Replays track). Part 1: a host match whose remote seats are scripted guests behind a stand-in IMatchNet, driven by the
    // real MatchNetDriver (their inputs arrive on most steps, not all; one guest goes silent mid-match and is dropped, then taken back), recorded
    // through CampaignHost's taps -> the file replays exact in this process and in a fresh one, a changed remote input is
    // caught, and a file with another network input size is refused. Part 2: ReplayLink over a lossy, reordering stand-in link:
    // a guest's identity reaches the host, and the host's recording reaches the guest byte for byte.
    // served=1 (docs/DEDICATED_SERVERS.md section 4): the match as SecondHuntMatchHost records it: no local seat (ServedHost), every
    // human remote, seat 0 too, the host stepping an idle input; the file must read back as served (ReplayFile.ReadSettings: not
    // written), play exact, and play exact drawn with the watched seat's HUD on (PlayerEntity.HostSeatlessHud, each seat in turn,
    // seat 0 included: drawing only, as the app shows a served match's replay).
    internal static partial class ReplayTest
    {
        public static void NetTest(string[] args)
        {
            var o = new Options { Bots = 1, Time = 45 };
            int guests = 2;
            double loss = 0.2;
            bool served = false;
            o = Parse(args.Skip(1).Where(a =>
            {
                if (a.StartsWith("guests=")) { guests = int.Parse(a[7..]); return false; }
                if (a.StartsWith("served=")) { served = a[7..] != "0"; return false; }
                if (a.StartsWith("loss=")) { loss = double.Parse(a[5..], System.Globalization.CultureInfo.InvariantCulture); return false; }
                return true;
            }), o);
            if (!Setup())
            {
                return;
            }
            int failures = 0;
            GameMode mode = ParseMode(o.Mode);
            RoomMetadata arena = MatchArenas.Find(o.Arena ?? "Combat Hall") ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings settings = MatchSettings.Quick(mode, arena.Name, o.Hunter, 0, o.Level);
            if (served)
            {
                // (as Program.Template in SecondHunt.MatchHost: no human of its own; NetMatchSession puts the first joiner on seat 0)
                settings.Players.Clear();
                settings.ServedHost = true;
                guests = Math.Max(guests, 2);
            }
            Hunter[] hunters = { Hunter.Kanden, Hunter.Sylux, Hunter.Trace, Hunter.Weavel, Hunter.Noxus, Hunter.Spire };
            for (int g = 0; g < guests; g++)
            {
                settings.Players.Add(new MatchPlayer { Hunter = hunters[g % hunters.Length], Remote = true, Nickname = $"GUEST{g + 1}" });
            }
            for (int b = 0; b < o.Bots; b++)
            {
                settings.Players.Add(new MatchPlayer { Hunter = hunters[(guests + b) % hunters.Length], Bot = true, BotLevel = o.Level });
            }
            settings.TimeLimitSeconds = o.Time;
            settings.Validate();
            Console.WriteLine($"{mode} in {arena.InGameName}: {(served ? "a served host (no seat)" : "host")} + {guests} remote guest(s) + {o.Bots} bot(s), {o.Time} s, seed {o.Seed}");
            // the seat that goes silent and comes back: guest 2 (served: the last guest)
            int dropSeat = guests > 1 ? (served ? guests - 1 : 2) : -1;

            // ---- part 1: record a host match, replay it ----
            ReplayFile file;
            var net = new ScriptedGuests(settings, o.Seed, dropSeat: dropSeat, dropAtStep: (int)(o.Time * 60 * 0.6f));
            long rejoinAt = (long)(o.Time * 60 * 0.6f) + 600;
            {
                var rec = new ReplayRecorder(settings, "tools");
                CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: false);
                try
                {
                    rec.Started(host, "online");
                    var driver = new MatchNetDriver(net, host);
                    var script = new ScriptedHuman(o.Seed);
                    long max = (long)(o.Time * 60) + 600;
                    while (!host.Ended && host.Frame < max)
                    {
                        // (a served host has no player: it steps an idle input, as SecondHuntMatchHost's Play)
                        CampaignInput input = served ? new CampaignInput { SelectWeapon = BeamType.None } : script.Next(host);
                        net.Step = (int)host.Frame;
                        // the dropped seat back 10 s later, as MatchNetDriver takes back an online seat that joined again
                        // (ReconnectSeat; the scripted seat stays silent after, so the driver sends it nothing more)
                        if (dropSeat >= 0 && host.Frame == rejoinAt) host.ReconnectSeat(dropSeat);
                        driver.Step(input);
                        rec.Step(input, host.Scene);
                    }
                    rec.End(host.Ended);
                    file = rec.File;
                }
                finally
                {
                    host.Dispose();
                }
            }
            int inputs = file.NetEvents.Count(e => e.Kind == ReplayNetKind.Input);
            int drops = file.NetEvents.Count(e => e.Kind == ReplayNetKind.Drop);
            int rejoins = file.NetEvents.Count(e => e.Kind == ReplayNetKind.Rejoin);
            int positions = file.NetEvents.Count(e => e.Kind == ReplayNetKind.Position); // (0.3.6: online guests' own positions taken)
            string path = Path.Combine(OutDir(), "replaynettest.mphreplay");
            file.Save(path);
            Console.WriteLine($"recorded: {file.Steps.Count} steps, {inputs} remote inputs applied ({net.Skipped} steps with none), "
                + $"{drops} seat drop(s), {rejoins} rejoin(s), {positions} position(s) taken, {new FileInfo(path).Length / 1024} KB -> {path}");
            if (drops != (guests > 1 ? 1 : 0) || rejoins != (guests > 1 ? 1 : 0) || inputs == 0)
            {
                Console.WriteLine("  FAIL: the taps didn't see what the driver did");
                failures++;
            }
            {
                ReplayFile loaded = ReplayFile.Load(path);
                bool same = loaded.Net == "online" && loaded.NetEvents.Count == file.NetEvents.Count
                    && loaded.NetEvents.Zip(file.NetEvents).All(p => p.First.Step == p.Second.Step && p.First.Seat == p.Second.Seat
                        && p.First.Kind == p.Second.Kind && (p.First.Input ?? Array.Empty<byte>()).SequenceEqual(p.Second.Input ?? Array.Empty<byte>()));
                Console.WriteLine($"file round trip (format {ReplayFile.FormatVersion}): {(same ? "same events" : "EVENTS DIFFER")}");
                failures += same ? 0 : 1;
                Run r = Replay(loaded, View.NoDraw, types: false, reference: null);
                Console.WriteLine($"replay in this process:      {r.Verdict()}");
                failures += r.Exact ? 0 : 1;
                (int code, string output) = RunChild("-replayplay", path);
                string last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                Console.WriteLine($"replay in a fresh process:   {(code == 0 ? "" : $"[exit {code}] ")}{last}");
                failures += code == 0 ? 0 : 1;
            }
            if (served)
            {
                failures += ServedChecks(path);
            }
            {
                // without the remote seats' inputs the match can't play the same: the checks must say so early
                ReplayFile bare = ReplayFile.Load(path);
                int first = bare.NetEvents.First(e => e.Kind == ReplayNetKind.Input).Step;
                bare.NetEvents.Clear();
                Run r = Replay(bare, View.NoDraw, types: false, reference: null);
                bool caught = r.FirstFail >= 0 && r.FirstFail < first + 120;
                Console.WriteLine($"replay without its network inputs: {r.Verdict()} -> {(caught ? "caught" : "NOT CAUGHT")}");
                failures += caught ? 0 : 1;
            }
            {
                // one remote input's view turned a third of the way in: caught within a second
                ReplayFile tampered = ReplayFile.Load(path);
                int k = tampered.NetEvents.FindIndex(e => e.Kind == ReplayNetKind.Input && e.Step >= tampered.Steps.Count / 3);
                int at = tampered.NetEvents[k].Step;
                for (int i = k; i < tampered.NetEvents.Count && tampered.NetEvents[i].Step < at + 60; i++)
                {
                    ReplayNetEvent e = tampered.NetEvents[i];
                    if (e.Kind != ReplayNetKind.Input) continue;
                    NetInput input = NetInput.ReadRecord(e.Input);
                    input.Yaw += 30;
                    byte[] bytes = new byte[NetInput.RecordSize];
                    input.WriteRecord(bytes);
                    tampered.NetEvents[i] = e with { Input = bytes };
                }
                Run r = Replay(tampered, View.NoDraw, types: false, reference: null);
                bool caught = r.FirstFail >= at && r.FirstFail < at + 60;
                Console.WriteLine($"remote aim turned from step {at}: {r.Verdict()} -> {(caught ? "caught" : "NOT CAUGHT")}");
                failures += caught ? 0 : 1;
            }
            {
                // a file whose network input isn't this build's size: refused before the first step
                ReplayFile other = ReplayFile.Load(path);
                int k = other.NetEvents.FindIndex(e => e.Kind == ReplayNetKind.Input);
                other.NetEvents[k] = other.NetEvents[k] with { Input = other.NetEvents[k].Input![..NetInput.Size] };
                var ms = new MemoryStream();
                other.Write(ms);
                ms.Position = 0;
                ReplayFile reread = ReplayFile.Read(ms);
                string? why = ReplayPlayer.CheckBeforeStart(reread);
                Console.WriteLine($"other network input size: {(why != null ? "refused: " + why : "NOT REFUSED")}");
                failures += why != null ? 0 : 1;
            }

            // ---- part 2: the link ----
            failures += LinkTest(File.ReadAllBytes(path), loss, o.Seed);

            Console.WriteLine(failures == 0 ? "PASS: network-match replays exact, changes caught, the link delivers both ways"
                : $"FAIL: {failures} check(s) failed");
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        // served=1: the file reads back as a served match's, and plays exact drawn with each seat's HUD in turn (the app's view)
        private static int ServedChecks(string path)
        {
            int failures = 0;
            ReplayFile loaded = ReplayFile.Load(path);
            bool seatless = loaded.Settings.ServedHost && loaded.Settings.Players[loaded.Settings.LocalSeat].Remote;
            Console.WriteLine($"served match, read back: {(seatless ? "served (no local seat)" : "NOT SERVED: it would play with a local seat")}");
            failures += seatless ? 0 : 1;
            int seats = loaded.Settings.Players.Count;
            _beforeStep = host =>
            {
                // the watched seat's HUD, the next seat every 5 s (seat 0, Main, too), as MatchReplay.PlaceReplayView sets it
                PlayerEntity.HostSeatlessHud = true;
                PlayerEntity.HostHudSeat = PlayerEntity.Players[(int)(host.Frame / 300 % seats)];
            };
            Run r;
            try
            {
                r = Replay(loaded, View.Draw, types: false, reference: null);
            }
            finally
            {
                _beforeStep = null;
                PlayerEntity.HostSeatlessHud = false;
                PlayerEntity.HostHudSeat = null;
            }
            Console.WriteLine($"served match, drawn with each seat's HUD: {r.Verdict()}");
            failures += r.Exact ? 0 : 1;
            return failures;
        }

        private static int LinkTest(byte[] replay, double loss, int seed)
        {
            int failures = 0;
            var (hostNet, guestNet) = LossyPair.Make(loss, seed, guestSeat: 1);
            using var hostLink = new ReplayLink(hostNet);
            using var guestLink = new ReplayLink(guestNet);
            ReplaySeatIdentity? got = null;
            byte[]? file = null;
            hostLink.IdentityReceived += id => got = id;
            guestLink.ReplayReceived += bytes => file = bytes;
            var sent = new ReplaySeatIdentity(7, "1234-5678-9012", "KANDEN", "{\"RankPoints\":1234}", 4412);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            guestLink.SendIdentity(sent, wantReplay: true);
            while (got == null && clock.ElapsedMilliseconds < 10000) Thread.Sleep(10);
            bool idOk = got is ReplaySeatIdentity g && g.Seat == 1 && g.FriendCode == sent.FriendCode && g.Name == sent.Name
                && g.Tag == sent.Tag && g.LicenseJson == sent.LicenseJson && hostLink.SeatsWantingReplay.Contains(1);
            Console.WriteLine($"link, guest identity -> host ({loss:P0} loss): {(idOk ? $"arrived in {clock.ElapsedMilliseconds} ms as seat 1" : "NOT RECEIVED / WRONG")}");
            failures += idOk ? 0 : 1;
            clock.Restart();
            hostLink.SendReplay(1, replay);
            while ((file == null || hostLink.Sending) && clock.ElapsedMilliseconds < 20000) Thread.Sleep(10);
            bool fileOk = file != null && file.AsSpan().SequenceEqual(replay) && !hostLink.Sending;
            Console.WriteLine($"link, host recording -> guest: {(fileOk ? $"{replay.Length / 1024} KB in {clock.ElapsedMilliseconds} ms, identical, all acked" : "NOT RECEIVED / DIFFERS")}"
                + $" ({hostNet.Sent + guestNet.Sent} packets, {hostNet.Lost + guestNet.Lost} lost)");
            failures += fileOk ? 0 : 1;
            return failures;
        }

        // the host side of a match as MatchNetDriver sees it, its remote seats played by scripts: each sends its input on most
        // steps (none on about one in six, as packets go missing); dropSeat goes silent at dropAtStep (the driver drops it 10 s on)
        private sealed class ScriptedGuests : IMatchNet
        {
            private readonly int[] _seats;
            private readonly Dictionary<int, uint> _rng = new();
            private readonly Dictionary<int, (float Yaw, float Pitch, CampaignButtons Held, sbyte X, sbyte Y)> _state = new();
            private readonly int _dropSeat, _dropAt;
            private ushort _seq;
            public int Step;
            public int Skipped;

            public ScriptedGuests(MatchSettings s, int seed, int dropSeat, int dropAtStep)
            {
                _seats = Enumerable.Range(0, s.Players.Count).Where(i => s.Players[i].Remote).ToArray();
                foreach (int seat in _seats) _rng[seat] = (uint)(seed * 7919 + seat * 104729);
                (_dropSeat, _dropAt) = (dropSeat, dropAtStep);
            }

            private int Roll(int seat, int n)
            {
                uint x = _rng[seat] = _rng[seat] * 1664525u + 1013904223u;
                return (int)(((x >> 8) * (ulong)n) >> 24);
            }

            public bool IsHost => true;
            public string Kind => "test";
            public IReadOnlyList<int> RemoteSeats => _seats;
            public int InputsReceived => 0;
            public int SnapshotsReceived => 0;
            public bool PeerAlive => true;
            public int InputsFrom(int seat) => 1;
            // (silent after the drop step: past the driver's 10 s, so the seat goes at once)
            public long SilentMs(int seat) => seat == _dropSeat && Step >= _dropAt ? 60000 : 0;
            public void SendSnapshot(Snapshot snap) { }
            public bool TryTakeSnapshot(out Snapshot snap) { snap = default!; return false; }
            public void SendInput(in NetInput input) { }
            public void SendSide(int seat, ReadOnlySpan<byte> payload) { }
            public event Action<int, byte[]>? SideReceived { add { } remove { } }

            public bool TryTakeInput(int seat, out NetInput input)
            {
                input = default;
                if (Roll(seat, 6) == 0)
                {
                    Skipped++;
                    return false;
                }
                (float yaw, float pitch, CampaignButtons held, sbyte x, sbyte y) = _state.GetValueOrDefault(seat);
                if (Roll(seat, 20) == 0) { x = (sbyte)(Roll(seat, 3) - 1); y = (sbyte)(Roll(seat, 4) == 0 ? -1 : 1); }
                if (Roll(seat, 30) == 0) held = Roll(seat, 4) switch { 0 => CampaignButtons.Shoot, 1 => CampaignButtons.Jump, 2 => CampaignButtons.None, _ => CampaignButtons.Shoot | CampaignButtons.Jump };
                yaw += (Roll(seat, 9) - 4) * 0.8f;
                pitch = Math.Clamp(pitch + (Roll(seat, 5) - 2) * 0.3f, -30, 30);
                _state[seat] = (yaw, pitch, held, x, y);
                PlayerEntity p = PlayerEntity.Players[seat];
                var made = new NetInput
                {
                    Seq = _seq++,
                    Buttons = held,
                    SelectWeapon = MphRead.BeamType.None,
                    MoveX = x,
                    MoveY = y,
                    Yaw = yaw,
                    Pitch = pitch,
                    AimEpoch = (byte)p.NetAimEpoch,
                    ViewTick = (uint)Math.Max(0, Step - 4)
                };
                // through the wire forms, as a real one arrives
                byte[] wire = new byte[NetInput.RecordSize];
                made.WriteRecord(wire);
                input = NetInput.ReadRecord(wire);
                return true;
            }
        }

        // two ends of a match's side channel (host seat 0, one guest), losing `loss` of the packets and delivering the rest
        // late and out of order, on their own threads (as a socket's receive thread)
        private sealed class LossyPair : IMatchNet
        {
            private readonly bool _host;
            private readonly int _guestSeat;
            private LossyPair _other = null!;
            private readonly Random _random;
            private readonly double _loss;
            private readonly object _lock = new();
            public int Sent, Lost;

            private LossyPair(bool host, int guestSeat, double loss, int seed)
            {
                (_host, _guestSeat, _loss) = (host, guestSeat, loss);
                _random = new Random(seed + (host ? 1 : 2));
            }

            public static (LossyPair Host, LossyPair Guest) Make(double loss, int seed, int guestSeat)
            {
                var h = new LossyPair(true, guestSeat, loss, seed);
                var g = new LossyPair(false, guestSeat, loss, seed);
                (h._other, g._other) = (g, h);
                return (h, g);
            }

            public void SendSide(int seat, ReadOnlySpan<byte> payload)
            {
                if (_host && seat != _guestSeat) return;
                byte[] copy = payload.ToArray();
                int delay;
                lock (_lock)
                {
                    Sent++;
                    if (_random.NextDouble() < _loss)
                    {
                        Lost++;
                        return;
                    }
                    delay = _random.Next(1, 30);
                }
                int from = _host ? 0 : _guestSeat;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(delay);
                    _other.SideReceived?.Invoke(from, copy);
                });
            }

            public event Action<int, byte[]>? SideReceived;
            public bool IsHost => _host;
            public string Kind => "test";
            public IReadOnlyList<int> RemoteSeats => _host ? new[] { _guestSeat } : Array.Empty<int>();
            public bool TryTakeInput(int seat, out NetInput input) { input = default; return false; }
            public int InputsFrom(int seat) => 0;
            public int InputsReceived => 0;
            public long SilentMs(int seat) => 0;
            public void SendSnapshot(Snapshot snap) { }
            public bool TryTakeSnapshot(out Snapshot snap) { snap = default!; return false; }
            public int SnapshotsReceived => 0;
            public void SendInput(in NetInput input) { }
            public bool PeerAlive => true;
        }
    }
}
