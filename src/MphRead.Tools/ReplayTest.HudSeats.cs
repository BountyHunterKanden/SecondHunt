using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // -replayhudseats <file>: plays a replay once per seat, drawn, with that seat watched (its HUD and its dead view, as the
    // app's replay screen sets them), and checks the three things a player's report named (2026-11-22, a served match's
    // replay): the Weavel "turret energy" text only on the seat whose turret it is, the mode score (prime time, points,
    // lives) the watched seat's own, and a dead remote seat's view not spinning. Each check says how often the replay
    // could tell the fixed way from the old one (seat 0's values), so a pass on a replay that never had the case is named.
    internal static partial class ReplayTest
    {
        public static void HudSeats(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: -replayhudseats <file>");
                return;
            }
            if (!Setup())
            {
                return;
            }
            ReplayFile first = ReplayFile.Load(args[1]);
            var s = first.Settings;
            bool seatless = s.ServedHost && s.Players[s.LocalSeat].Remote;
            int count = s.Players.Count;
            Console.WriteLine($"{s.Mode} in {s.Arena}, build {first.Build}, served {s.ServedHost} (no local seat {seatless}), "
                + $"{first.Steps.Count} steps");
            for (int i = 0; i < count; i++)
            {
                Console.WriteLine($"  seat {i}: {s.Players[i].Nickname} {s.Players[i].Hunter} bot={s.Players[i].Bot} remote={s.Players[i].Remote}");
            }
            const BindingFlags inst = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo queue = typeof(PlayerEntity).GetField("_hudMessageQueue", BindingFlags.NonPublic | BindingFlags.Static)!;
            Type msgType = typeof(PlayerEntity).GetNestedType("HudMessage", BindingFlags.NonPublic)!;
            PropertyInfo life = msgType.GetProperty("Lifetime")!;
            PropertyInfo text = msgType.GetProperty("Text")!;
            MethodInfo format = typeof(PlayerEntity).GetMethod("FormatModeScore", inst)!;
            int failures = 0, turretTold = 0, scoreTold = 0, deadTold = 0, turretShown = 0;
            for (int seat = 0; seat < count; seat++)
            {
                int k = seat;
                ReplayFile file = ReplayFile.Load(args[1]);
                string? prefix = null;
                // turret text
                int shown = 0, leak = 0, ownOut = 0, otherOut = 0;
                long lastOwn = -100000, firstLeak = -1;
                // mode score
                int draws = 0, wrong = 0, told = 0;
                string? wrongExample = null;
                // dead view
                int deaths = 0, deadSteps = 0;
                float viewTurn = 0, rawTurn = 0, viewWorst = 0, rawWorst = 0;
                float? lastView = null, lastRaw = null;
                bool wasDead = false;
                PlayerEntity.HostModeScoreNote = (slot, id, drawn) =>
                {
                    if (id < 212 || id > 218) return;
                    PlayerEntity p = PlayerEntity.Players[k];
                    string own = (string)format.Invoke(p, new object[] { k })!;
                    string seat0 = (string)format.Invoke(p, new object[] { 0 })!;
                    draws++;
                    if (own != seat0) told++;
                    if (drawn != own || slot != k)
                    {
                        wrong++;
                        wrongExample ??= $"message {id} drew \"{drawn}\" by slot {slot}, seat {k}'s is \"{own}\"";
                    }
                };
                _beforeStep = host =>
                {
                    var players = PlayerEntity.Players;
                    PlayerEntity p = players[k];
                    PlayerEntity.HostSeatlessHud = seatless;
                    PlayerEntity.HostHudSeat = p == PlayerEntity.Main && !seatless ? null : p;
                    PlayerEntity.HostDeathFollowSeat = k;
                    if (prefix == null)
                    {
                        string message = Text.Strings.GetHudMessage(233); // turret energy: %d
                        int cut = message.IndexOf('%');
                        prefix = (cut > 0 ? message[..cut] : message).Trim();
                    }
                    // ---- the turret text (the queue is every player's)
                    bool own = p.Flags2.TestFlag(PlayerFlags2.Halfturret);
                    bool other = false;
                    for (int i = 0; i < count; i++)
                    {
                        other |= i != k && players[i].Flags2.TestFlag(PlayerFlags2.Halfturret);
                    }
                    if (own)
                    {
                        ownOut++;
                        lastOwn = host.Frame;
                    }
                    if (other && !own) otherOut++;
                    bool present = false;
                    var msgs = (IList)queue.GetValue(null)!;
                    for (int m = 0; m < msgs.Count; m++)
                    {
                        if ((float)life.GetValue(msgs[m])! <= 0) continue;
                        var chars = (char[])text.GetValue(msgs[m])!;
                        int end = Array.IndexOf(chars, '\0');
                        string line = new string(chars, 0, end < 0 ? chars.Length : end);
                        present |= line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    }
                    // (frame 0 is before the HUD's setup clears the queue: it still holds this tool's last run's lines)
                    if (present && host.Frame > 0)
                    {
                        shown++;
                        if (host.Frame - lastOwn > 180) // (not this seat's turret: none out here for 3 s)
                        {
                            leak++;
                            if (firstLeak < 0) firstLeak = host.Frame;
                        }
                    }
                    // ---- the dead view (what the replay screen draws for this seat: RemoteDeadView, else MPH's camera)
                    bool dead = p.Health == 0 && p.LoadFlags.TestFlag(LoadFlags.Spawned);
                    if (dead)
                    {
                        if (!wasDead)
                        {
                            deaths++;
                            viewWorst = Math.Max(viewWorst, viewTurn);
                            rawWorst = Math.Max(rawWorst, rawTurn);
                            viewTurn = rawTurn = 0;
                            lastView = lastRaw = null;
                        }
                        deadSteps++;
                        Vector3 rawFacing = p.CameraInfo.Target - p.CameraInfo.Position;
                        Vector3 viewFacing = p.RemoteDeadView is HostView v ? v.Target - v.Position : rawFacing;
                        float raw = MathHelper.RadiansToDegrees(MathF.Atan2(rawFacing.X, rawFacing.Z));
                        float view = MathHelper.RadiansToDegrees(MathF.Atan2(viewFacing.X, viewFacing.Z));
                        if (lastRaw is float lr) rawTurn += MathF.Abs(Wrap(raw - lr));
                        if (lastView is float lv) viewTurn += MathF.Abs(Wrap(view - lv));
                        lastRaw = raw;
                        lastView = view;
                    }
                    wasDead = dead;
                };
                Run r;
                try
                {
                    r = Replay(file, View.Draw, types: false, reference: null);
                }
                finally
                {
                    _beforeStep = null;
                    PlayerEntity.HostModeScoreNote = null;
                    PlayerEntity.HostSeatlessHud = false;
                    PlayerEntity.HostHudSeat = null;
                    PlayerEntity.HostDeathFollowSeat = -1;
                }
                viewWorst = Math.Max(viewWorst, viewTurn);
                rawWorst = Math.Max(rawWorst, rawTurn);
                bool remote = s.Players[k].Remote;
                bool turretOk = leak == 0, scoreOk = wrong == 0, deadOk = viewWorst <= 360;
                turretTold += otherOut > 0 ? 1 : 0;
                turretShown += shown > 0 && ownOut > 0 ? 1 : 0;
                scoreTold += told > 0 ? 1 : 0;
                deadTold += rawWorst > 720 ? 1 : 0;
                Console.WriteLine($"seat {k} ({s.Players[k].Nickname}, {s.Players[k].Hunter}{(remote ? ", remote" : "")}): replay {r.Verdict()}");
                Console.WriteLine($"  {(turretOk ? "PASS" : "FAIL")} turret text: shown {shown} steps, {leak} with no turret of its own "
                    + $"(own turret out {ownOut} steps; another seat's out {otherOut} steps){(firstLeak >= 0 ? $", first at frame {firstLeak}" : "")}");
                Console.WriteLine($"  {(scoreOk ? "PASS" : "FAIL")} mode score: {draws} draws, {wrong} not this seat's "
                    + $"({told} draws where seat 0's value differed){(wrongExample != null ? ": " + wrongExample : "")}");
                Console.WriteLine($"  {(deadOk ? "PASS" : "FAIL")} dead view: {deaths} deaths, {deadSteps} steps dead, most turn in one death "
                    + $"{viewWorst:0} deg drawn (MPH's own camera {rawWorst:0} deg)");
                failures += (turretOk ? 0 : 1) + (scoreOk ? 0 : 1) + (deadOk ? 0 : 1) + (r.Exact ? 0 : 1);
            }
            Console.WriteLine($"could tell the old way from the new: turret text on {turretTold} seat(s) (another seat's turret out while watched; "
                + $"shown rightly on {turretShown}), mode score on {scoreTold} seat(s), dead view on {deadTold} seat(s) (MPH's camera turned over 720 deg)");
            Console.WriteLine(failures == 0 ? "replayhudseats: all PASS" : $"replayhudseats: FAIL ({failures})");
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        private static float Wrap(float degrees)
        {
            while (degrees > 180) degrees -= 360;
            while (degrees < -180) degrees += 360;
            return degrees;
        }
    }
}
