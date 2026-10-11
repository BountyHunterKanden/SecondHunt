using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.Replay
{
    // Records a match as it's played (docs/REPLAY_MODE.md):
    //   var rec = new ReplayRecorder(settings, build);   // right before CampaignHost.StartMatch
    //   host = CampaignHost.StartMatch(settings, ...);
    //   rec.Started(host.Scene);                          // right after
    //   each step: host.Step(input); rec.Step(input, host.Scene);
    //   rec.End(complete: host.Ended) -> rec.File.Save(path)
    // A LAN / online match's HOST records too (it runs the whole match): rec.Started also taps the host's remote seats
    // (CampaignHost.RemoteInputTap / SeatDropTap / SeatRejoinTap), so every network input it applies, every seat it drops
    // and every dropped seat it takes back (online: the player joined again) is kept by the step it came before
    // (ReplayNetEvent). A guest doesn't run the whole match (puppets on the host's snapshots), so it
    // doesn't record: it gets the host's file at the end (ReplayLink).
    public sealed class ReplayRecorder
    {
        public ReplayFile File { get; }
        readonly ReplayMarkTracker _marks = new();

        public ReplayRecorder(MatchSettings settings, string build)
        {
            (uint rng1, uint rng2) = RngAtStart();
            File = new ReplayFile
            {
                Build = build,
                Settings = ReplayFile.CopySettings(settings),
                Rng1 = rng1,
                Rng2 = rng2,
                AiAspect = Scene.HostAiAspect
            };
        }

        // Where the RNG streams will be when the match's setup starts. SceneSetup resets both to their start values
        // when the save slot changed since the last scene (SceneSetup.cs ~46): the first match after launch (the
        // previous slot is -1, StartMatch always uses slot 0) starts from them, every later one from wherever the
        // last scene left off. Playback (ReplayPlayer.Start) puts them back here and stops that reset.
        public static (uint Rng1, uint Rng2) RngAtStart() => Menu.PreviousSaveSlot != 0
            ? (Rng.Rng1StartValue, Rng.Rng2StartValue)
            : (Rng.Rng1, Rng.Rng2);

        // the settings that change play, as the match runs with them (StartMatch sets some, e.g. FreeWeaponSelect);
        // and the match plays the same whether or not a step draws (R1), so playback and verification can skip drawing
        public void Started(Scene scene)
        {
            scene.HostDrawFreeSim = true;
            CaptureOptions(File.Options);
            File.Data = ReplayData.Fingerprint(File.Settings.Arena, File.Settings.Mode);
        }

        // a LAN / online host's match (net: "LAN" / "online"): also keep what the network does to its remote seats
        public void Started(CampaignHost host, string net)
        {
            Started(host.Scene);
            File.Net = net;
            host.RemoteInputTap = RemoteInput;
            host.SeatDropTap = SeatDropped;
            host.SeatRejoinTap = SeatRejoined;
        }

        // (CampaignHost's taps, before the step they belong to: Steps.Count is that step's index)
        public void RemoteInput(int seat, NetInput input)
        {
            byte[] bytes = new byte[NetInput.RecordSize];
            input.WriteRecord(bytes);
            File.NetEvents.Add(new ReplayNetEvent(File.Steps.Count, (byte)seat, ReplayNetKind.Input, bytes));
        }

        public void SeatDropped(int seat) => File.NetEvents.Add(new ReplayNetEvent(File.Steps.Count, (byte)seat, ReplayNetKind.Drop, null));

        public void SeatRejoined(int seat) => File.NetEvents.Add(new ReplayNetEvent(File.Steps.Count, (byte)seat, ReplayNetKind.Rejoin, null));

        public void Step(in CampaignInput input, Scene scene)
        {
            File.Steps.Add(new ReplayStep { Input = input, Checksum = ReplayState.Capture(scene).Checksum });
            _marks.Observe(File.Steps.Count, File.Marks);
        }

        public void End(bool complete)
        {
            File.Complete = complete;
        }

        // Features that only change what's drawn or logged (opacities, HUD target info, audio logging): changing them
        // mustn't refuse a replay. Every other setting is compared; when unsure, a setting stays in (a needless refusal
        // beats a quietly wrong replay).
        private static readonly HashSet<string> DisplayOnlyFeatures = new(StringComparer.Ordinal)
        {
            nameof(Features.HelmetOpacity), nameof(Features.VisorOpacity), nameof(Features.HudOpacity),
            nameof(Features.ReticleOpacity), nameof(Features.TopScreenTargetInfo), nameof(Features.TargetInfoSway),
            nameof(Features.LogSpatialAudio)
        };

        internal static void CaptureOptions(SortedDictionary<string, string> into)
        {
            into.Clear();
            foreach ((string key, string value) in Bugfixes.Commit())
            {
                into["bugfixes." + key] = value;
            }
            foreach ((string key, string value) in Features.Commit())
            {
                if (!DisplayOnlyFeatures.Contains(key))
                {
                    into["features." + key] = value;
                }
            }
            foreach ((string key, string value) in Cheats.Commit())
            {
                into["cheats." + key] = value;
            }
        }
    }

    // Plays a recorded match back through the same CampaignHost.Step, checking every step (owner's rule: a replay is
    // exact or refused, never quietly wrong):
    //   string? why = ReplayPlayer.CheckBeforeStart(file);  // null = playable here
    //   var player = new ReplayPlayer(file); host = player.Start(...);
    //   why = player.CheckAfterStart();                     // the play settings as this build applies them
    //   while (player.TryNext(out input)) { host.Step(input); if (!player.Check(host.Scene)) stop: player.Failure }
    // Check runs after the step and before anything draws it, so a step that fails its check is never shown.
    public sealed class ReplayPlayer
    {
        public ReplayFile File { get; }
        // the next step to play
        public int Position { get; private set; }
        // the step whose check failed (-1 = none so far), and what differed when the caller asked (harness only)
        public int FailedAt { get; private set; } = -1;
        public string? Failure { get; private set; }
        public bool Finished => Position >= File.Steps.Count;

        public ReplayPlayer(ReplayFile file)
        {
            File = file;
        }

        // refusals known before loading anything: a different sim, a different fixed AI aspect, an arena this device
        // doesn't have (an imported arena not imported here)
        public static string? CheckBeforeStart(ReplayFile file)
        {
            if (file.SimVersion != ReplayFile.CurrentSimVersion)
            {
                return $"recorded with game logic version {file.SimVersion}; this build plays version {ReplayFile.CurrentSimVersion}";
            }
            if (file.OldTrooper)
            {
                return "recorded with an older Trooper; this build's Trooper plays differently";
            }
            if (file.OldEchoesWater)
            {
                return "recorded with Pipeline's older water; this build's water plays differently";
            }
            if (file.OldNetInput)
            {
                return "recorded with another version of the network match input";
            }
            if (file.AiAspect != Scene.HostAiAspect)
            {
                return "bot vision differs in this build";
            }
            if (!MatchArenas.IsArena(file.Settings.Arena))
            {
                return $"the arena {file.Settings.Arena} isn't on this device (import it first)";
            }
            return null;
        }

        // Starts the recorded match exactly as it started: the same settings, both RNG streams where they were, and no
        // save-slot reset in between (see ReplayRecorder.RngAtStart).
        private CampaignHost? _host;
        private int _event; // the next NetEvents entry to apply

        public CampaignHost Start(bool collectDrawItems = false, int viewWidth = 256, int viewHeight = 192)
        {
            _event = 0;
            Menu.PreviousSaveSlot = 0;
            Rng.SetRng1(File.Rng1);
            Rng.SetRng2(File.Rng2);
            Position = 0;
            FailedAt = -1;
            Failure = null;
            CampaignHost host = CampaignHost.StartMatch(ReplayFile.CopySettings(File.Settings), collectDrawItems,
                viewWidth, viewHeight);
            host.Scene.HostDrawFreeSim = true; // as recorded (ReplayRecorder.Started)
            _host = host;
            return host;
        }

        // the arena's files or a setting that changes play differ from the recording's (null = all the same)
        public string? CheckAfterStart()
        {
            if (File.Data.Length > 0
                && ReplayData.Compare(File.Data, ReplayData.Fingerprint(File.Settings.Arena, File.Settings.Mode)) is string why)
            {
                return why;
            }
            var now = new SortedDictionary<string, string>(StringComparer.Ordinal);
            ReplayRecorder.CaptureOptions(now);
            foreach ((string key, string value) in File.Options)
            {
                if (!now.TryGetValue(key, out string? current))
                {
                    return $"setting {key} doesn't exist in this build";
                }
                if (current != value)
                {
                    return $"setting {key} is {current} here, {value} in the replay";
                }
            }
            foreach (string key in now.Keys)
            {
                if (!File.Options.ContainsKey(key))
                {
                    return $"setting {key} is new in this build";
                }
            }
            return null;
        }

        public bool TryNext(out CampaignInput input)
        {
            if (FailedAt >= 0 || Finished)
            {
                input = default;
                return false;
            }
            input = File.Steps[Position].Input;
            BeforeStep();
            return true;
        }

        // a network match's host: what came in over the network before the next step, applied as the host applied it (TryNext
        // does it; a caller stepping from File.Steps itself calls it before each step; once per step however often it's called)
        public void BeforeStep()
        {
            List<ReplayNetEvent> events = File.NetEvents;
            while (_event < events.Count && events[_event].Step <= Position)
            {
                ReplayNetEvent e = events[_event++];
                if (_host == null || e.Step < Position) continue;
                if (e.Kind == ReplayNetKind.Input) _host.SetRemoteInput(e.Seat, NetInput.ReadRecord(e.Input!));
                else if (e.Kind == ReplayNetKind.Drop) _host.DisconnectSeat(e.Seat);
                else if (e.Kind == ReplayNetKind.Rejoin) _host.ReconnectSeat(e.Seat);
            }
        }

        // T9: the match was put back to a checkpoint (SimSnapshot.Restore) taken after `step` steps: play on from there
        public void Rewind(int step)
        {
            Position = step;
            FailedAt = -1;
            Failure = null;
            _event = 0; // (BeforeStep skips the events before Position)
        }

        // after the step TryNext gave: false = the state differs from the recording's (stop; nothing more plays)
        public bool Check(Scene scene)
        {
            uint checksum = ReplayState.Capture(scene).Checksum;
            int step = Position++;
            if (checksum != File.Steps[step].Checksum)
            {
                FailedAt = step;
                Failure = $"step {step}: state differs from the recording";
                return false;
            }
            return true;
        }
    }

    // each seat's kills and deaths as the match goes (GameState's counters): a new one becomes a ReplayMark at that step
    public sealed class ReplayMarkTracker
    {
        readonly int[] _kills = new int[GameState.Kills.Length], _deaths = new int[GameState.Deaths.Length];

        public void Reset()
        {
            Array.Clear(_kills);
            Array.Clear(_deaths);
        }

        public void Observe(int step, List<ReplayMark> into)
        {
            for (int i = 0; i < _kills.Length; i++)
            {
                for (int k = _kills[i]; k < GameState.Kills[i]; k++) into.Add(new ReplayMark(step, (byte)i, Death: false));
                for (int d = _deaths[i]; d < GameState.Deaths[i]; d++) into.Add(new ReplayMark(step, (byte)i, Death: true));
                _kills[i] = GameState.Kills[i];
                _deaths[i] = GameState.Deaths[i];
            }
        }
    }
}
