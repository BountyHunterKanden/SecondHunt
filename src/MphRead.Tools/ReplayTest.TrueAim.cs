using System;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // -tpreplay [arena=Combat Hall] [time=30] [seed=1] [bots=2]: replays of matches played with third-person true aim
    // (PlayerHostAim.cs, replay format 96; owner 2026-11-13: "true aim for third person for local matches too"). A LAN host
    // match (host Samus + 1 scripted remote guest through the real MatchNetDriver + bots): the host's code goes in each step's
    // input (CampaignInput.TrueAim, as CampaignRenderer sends it: first person, then each shoulder in turn), the
    // guest's arrives through CampaignHost.SetRemoteTrueAim (as MatchNet applies it with the guest's input) -> the file
    // round-trips, replays exact in this process and a fresh one, and without its codes (host's or guest's) it doesn't. And a
    // file stamped with an older format when saved again (ReplayFile.SavedFormat) reads back step for step.
    internal static partial class ReplayTest
    {
        public static void TrueAimTest(string[] args)
        {
            var o = new Options { Bots = 2, Time = 30, Arena = "Combat Hall" };
            o = Parse(args.Skip(1), o);
            if (!Setup())
            {
                return;
            }
            int failures = 0;
            RoomMetadata arena = MatchArenas.Find(o.Arena!) ?? throw new ArgumentException($"no arena {o.Arena}");
            MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 0, o.Level);
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Remote = true, Nickname = "GUEST1" });
            Hunter[] bots = { Hunter.Sylux, Hunter.Trace, Hunter.Weavel, Hunter.Noxus };
            for (int b = 0; b < o.Bots; b++)
            {
                settings.Players.Add(new MatchPlayer { Hunter = bots[b % bots.Length], Bot = true, BotLevel = o.Level });
            }
            settings.TimeLimitSeconds = o.Time;
            settings.PointGoal = 99; // (no early end)
            settings.Validate();
            const int guestSeat = 1;
            Console.WriteLine($"Battle in {arena.InGameName}: host Samus + a remote guest (Kanden) + {o.Bots} bot(s), {o.Time} s, "
                + $"seed {o.Seed}, third person with true aim");

            ReplayFile file;
            var net = new ScriptedGuests(settings, o.Seed, dropSeat: -1, dropAtStep: Int32.MaxValue);
            {
                var rec = new ReplayRecorder(settings, "tools");
                CampaignHost host = CampaignHost.StartMatch(settings, collectDrawItems: false);
                try
                {
                    rec.Started(host, "LAN");
                    var driver = new MatchNetDriver(net, host);
                    var script = new ScriptedHuman(o.Seed);
                    float hostDist = Single.MaxValue, guestDist = Single.MaxValue;
                    ushort hostCode = 0;
                    long max = (long)(o.Time * 60) + 600;
                    while (!host.Ended && host.Frame < max)
                    {
                        CampaignInput input = script.Next(host);
                        input.TrueAim = hostCode;
                        // the guest's camera as its device has it: third person from 3 s, the shoulders in turn every 4 s,
                        // first person again for 2 s every 10 s (each change is an AimCam event)
                        long f = host.Frame;
                        bool guestTp = f >= 180 && f % 600 >= 120;
                        ushort guestCode = guestTp ? Code(host.Scene, PlayerEntity.Players[guestSeat], f / 240 % 2 == 1, ref guestDist) : (ushort)0;
                        if (!guestTp) guestDist = Single.MaxValue;
                        host.SetRemoteTrueAim(guestSeat, guestCode);
                        net.Step = (int)host.Frame;
                        driver.Step(input);
                        rec.Step(input, host.Scene);
                        // the app's camera after the step -> the next step's code (first person the first 5 s, then the
                        // shoulders in turn every 5 s: right, left, ...)
                        f = host.Frame;
                        if (f < 300)
                        {
                            hostCode = 0;
                            hostDist = Single.MaxValue;
                        }
                        else
                        {
                            hostCode = Code(host.Scene, host.Player, f / 300 % 2 == 0, ref hostDist);
                        }
                    }
                    rec.End(host.Ended);
                    file = rec.File;
                }
                finally
                {
                    host.Dispose();
                }
            }
            int aimCams = file.NetEvents.Count(e => e.Kind == ReplayNetKind.AimCam);
            int inputs = file.NetEvents.Count(e => e.Kind == ReplayNetKind.Input);
            int tpSteps = file.Steps.Count(s => s.Input.TrueAim != 0);
            int leftSteps = file.Steps.Count(s => (s.Input.TrueAim & 0x4000) != 0);
            int guestLeft = file.NetEvents.Count(e => e.Kind == ReplayNetKind.AimCam && (e.Input![1] & 0x40) != 0);
            string path = Path.Combine(OutDir(), "tpreplaytest.mphreplay");
            file.Save(path);
            Console.WriteLine($"recorded: {file.Steps.Count} steps, {tpSteps} with the host's true aim ({tpSteps - leftSteps} right, {leftSteps} left), "
                + $"{inputs} guest inputs, {aimCams} guest AimCam change(s) ({guestLeft} to the left shoulder), {new FileInfo(path).Length / 1024} KB -> {path}");
            if (leftSteps == 0 || tpSteps == leftSteps || guestLeft == 0 || guestLeft == aimCams || inputs == 0)
            {
                Console.WriteLine("  FAIL: the recording doesn't hold both seats' codes");
                failures++;
            }
            {
                ReplayFile loaded = ReplayFile.Load(path);
                bool same = loaded.Net == "LAN" && !loaded.OldNetInput && loaded.NetEvents.Count == file.NetEvents.Count
                    && loaded.NetEvents.Zip(file.NetEvents).All(p => p.First.Step == p.Second.Step && p.First.Seat == p.Second.Seat
                        && p.First.Kind == p.Second.Kind && (p.First.Input ?? Array.Empty<byte>()).SequenceEqual(p.Second.Input ?? Array.Empty<byte>()))
                    && loaded.Steps.Count == file.Steps.Count
                    && loaded.Steps.Zip(file.Steps).All(p => p.First.Input.TrueAim == p.Second.Input.TrueAim && p.First.Checksum == p.Second.Checksum);
                Console.WriteLine($"file round trip (format {ReplayFile.FormatVersion}): {(same ? "same steps, codes and events" : "DIFFERS")}");
                failures += same ? 0 : 1;
                Run r = Replay(loaded, View.NoDraw, types: false, reference: null);
                Console.WriteLine($"replay in this process:      {r.Verdict()}");
                failures += r.Exact ? 0 : 1;
                (int code, string output) = RunChild("-replayplay", path);
                string last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                Console.WriteLine($"replay in a fresh process:   {(code == 0 ? "" : $"[exit {code}] ")}{last}");
                failures += code == 0 ? 0 : 1;
            }
            {
                // the host's codes taken out: its aim is vanilla's again, so the match can't play the same
                ReplayFile bare = ReplayFile.Load(path);
                int first = bare.Steps.FindIndex(s => s.Input.TrueAim != 0);
                for (int i = 0; i < bare.Steps.Count; i++)
                {
                    ReplayStep s = bare.Steps[i];
                    s.Input.TrueAim = 0;
                    bare.Steps[i] = s;
                }
                Run r = Replay(bare, View.NoDraw, types: false, reference: null);
                bool caught = r.FirstFail >= first;
                Console.WriteLine($"replay without the host's codes (first at step {first}): {r.Verdict()} -> {(caught ? "caught" : "NOT CAUGHT")}");
                failures += caught ? 0 : 1;
            }
            {
                // the guest's codes taken out: the same for its seat
                ReplayFile bare = ReplayFile.Load(path);
                int first = bare.NetEvents.First(e => e.Kind == ReplayNetKind.AimCam).Step;
                bare.NetEvents.RemoveAll(e => e.Kind == ReplayNetKind.AimCam);
                Run r = Replay(bare, View.NoDraw, types: false, reference: null);
                bool caught = r.FirstFail >= first;
                Console.WriteLine($"replay without the guest's codes (first at step {first}): {r.Verdict()} -> {(caught ? "caught" : "NOT CAUGHT")}");
                failures += caught ? 0 : 1;
            }
            {
                // an older file saved again keeps its own stamp (format 86 here: BlasterBuff off) and so its own step layout:
                // it reads back step for step (as an older file holds no codes, they're taken out first)
                ReplayFile older = ReplayFile.Load(path);
                older.NetEvents.RemoveAll(e => e.Kind == ReplayNetKind.AimCam);
                for (int i = 0; i < older.Steps.Count; i++)
                {
                    ReplayStep s = older.Steps[i];
                    s.Input.TrueAim = 0;
                    older.Steps[i] = s;
                }
                older.Settings.BlasterBuff = false;
                var ms = new MemoryStream();
                older.Write(ms);
                ms.Position = 0;
                bool ok;
                string why = "";
                try
                {
                    ReplayFile reread = ReplayFile.Read(ms);
                    ok = reread.Steps.Count == older.Steps.Count && reread.NetEvents.Count == older.NetEvents.Count
                        && reread.Settings.BlasterBuff == false
                        && reread.Steps.Zip(older.Steps).All(p => p.First.Checksum == p.Second.Checksum
                            && p.First.Input.Move == p.Second.Input.Move && p.First.Input.AimDelta == p.Second.Input.AimDelta
                            && p.First.Input.Buttons == p.Second.Input.Buttons);
                }
                catch (Exception ex)
                {
                    ok = false;
                    why = $" ({ex.GetType().Name}: {ex.Message})";
                }
                Console.WriteLine($"an older file saved again (stamped 86): {(ok ? "reads back step for step" : "MISREAD" + why)}");
                failures += ok ? 0 : 1;
            }

            Console.WriteLine(failures == 0 ? "PASS: true-aim matches replay exact, both seats' codes matter, older files re-save intact"
                : $"FAIL: {failures} check(s) failed");
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        // CampaignRenderer.UpdateThirdPerson's match case: the code a seat's app sends with its next input (0 in an alt form
        // or seated: the camera held in; -tplan uses it too)
        internal static ushort Code(Scene scene, PlayerEntity p, bool left, ref float dist)
        {
            if (p.IsAltForm || p.HostSeated || !p.LoadFlags.TestFlag(LoadFlags.Spawned))
            {
                dist = 0;
                return 0;
            }
            if (!PlayerEntity.HostThirdPersonOffset(scene, p.CameraInfo.ViewMatrix, left, ref dist, out Vector3 _))
            {
                dist = Single.MaxValue;
            }
            return PlayerEntity.HostTrueAimCode(left, dist);
        }
    }
}
