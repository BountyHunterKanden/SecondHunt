using System;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.Campaign
{
    // Snapshots on the match host and their use on a match client. A client runs the same match with its own seat
    // local (MatchSettings.LocalSeat) and every other seat a puppet: driven by the buttons and view the host relays,
    // so MphRead animates it with its own sounds and effects, and pulled onto the host's pose each snapshot. Damage
    // never starts on a client (PlayerEntity.NetClient); the host's health changes play through TakeDamage there.
    // The mode's state is the host's too (NetObjective): who holds an octolith and when it drops/resets/scores, who
    // stands on a node, the mode timers and stats.
    public sealed partial class CampaignHost
    {
        private ItemSync? _itemSync;
        private long _itemsSentAt = long.MinValue;
        private long _statsSentAt = -1000;

        // host: every seat after this frame (and the items, every 4th frame or when they change; everyPart: always, for the
        // online deltas, which send only what changed anyway, with the online-only extras)
        // lanStats: the detail stats in their own block every 4th frame (LAN 14; online sends them in Extras)
        public Snapshot CaptureSnapshot(bool everyPart = false, bool lanStats = false)
        {
            int seats = Match?.Players.Count ?? PlayerEntity.PlayerCount;
            var snap = new Snapshot
            {
                Tick = (uint)Frame,
                MatchTime = GameState.MatchTime,
                MatchState = (byte)GameState.MatchState,
                Seats = new PlayerNetState[seats],
                Points = new short[seats],
                Kills = new short[seats],
                Deaths = new short[seats]
            };
            for (int i = 0; i < seats; i++)
            {
                snap.Seats[i] = PlayerEntity.Players[i].NetCapture();
                snap.Points[i] = (short)GameState.Points[i];
                snap.Kills[i] = (short)GameState.Kills[i];
                snap.Deaths[i] = (short)GameState.Deaths[i];
            }
            _itemSync ??= new ItemSync();
            NetItem[] items = _itemSync.Capture(Scene, out bool changed);
            if (everyPart || changed || Frame - _itemsSentAt >= 4)
            {
                snap.Items = items;
                _itemsSentAt = Frame;
            }
            if (GameState.Mode != GameMode.Battle && GameState.Mode != GameMode.BattleTeams)
            {
                snap.Objective = NetObjective.Capture(Scene);
            }
            // the Trooper's cling + drone, every snapshot (null: no seat has an ability)
            snap.Abilities = SeatAbilities.Capture(seats);
            // the Echoes arenas' crates + breakables, every snapshot (a byte each)
            snap.World = NetWorld.Capture(Scene);
            if (lanStats && Frame - _statsSentAt >= 4)
            {
                snap.Stats = SeatExtras.CaptureStats(seats);
                _statsSentAt = Frame;
            }
            if (everyPart)
            {
                snap.Extras = SeatExtras.Capture(seats);
                snap.Handoff = HostHandoff.Capture(Scene, _itemSync);
            }
            return snap;
        }

        // client: how often the local player was pulled onto the host's pose (drifted past the tolerance)
        public int LocalCorrections { get; private set; }
        public void CountLocalCorrection() => LocalCorrections++;

        // client: take the host's frame. The local seat keeps its own (predicted) pose unless it drifted further than
        // localTolerance; everything else is the host's.
        // online guest (Net/RemoteSmoothing.cs): the other seats' poses come from the blended past, not each snapshot
        public bool SmoothRemotes { get; set; }

        // client: per puppet, the FIRE held over from its respawn press (ApplySnapshot)
        private readonly bool[] _puppetShootHeld = new bool[PlayerEntity.MaxPlayers];

        public void ApplySnapshot(Snapshot snap, float localTolerance = 0.5f)
        {
            int local = PlayerEntity.MainPlayerIndex;
            for (int i = 0; i < snap.Seats.Length && i < PlayerEntity.MaxPlayers; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                ref PlayerNetState s = ref snap.Seats[i];
                if (i == local)
                {
                    if (p.NetApply(s, pose: false, poseTolerance: localTolerance))
                    {
                        LocalCorrections++;
                    }
                    continue;
                }
                if (snap.Extras != null)
                {
                    // online: a seat the host took out (its player left) or brought back (it rejoined) does the same here
                    p.NetFollowSeat((s.Flags & PlayerNetState.Active) != 0);
                }
                bool wasDead = p.Health == 0;
                p.NetApply(s, pose: !SmoothRemotes);
                if (p.HostRemote)
                {
                    int c = s.Controls;
                    // the FIRE press that respawns a seat never shoots on the host (the spawn's NoShotsFired: a held
                    // trigger waits for the next press), but here it reaches the puppet after the spawn and reads as a
                    // fresh press: a shot from someone the other player never saw fire (match BF87SN). Held off
                    // until the host's buttons let go, as the host has it.
                    if (wasDead || s.Health == 0)
                    {
                        _puppetShootHeld[i] = (c & NetControls.Shoot) != 0;
                    }
                    else if ((c & NetControls.Shoot) == 0)
                    {
                        _puppetShootHeld[i] = false;
                    }
                    if (_puppetShootHeld[i])
                    {
                        c &= ~NetControls.Shoot;
                    }
                    int moveX = ((c & NetControls.Right) != 0 ? 1 : 0) - ((c & NetControls.Left) != 0 ? 1 : 0);
                    int moveY = ((c & NetControls.Up) != 0 ? 1 : 0) - ((c & NetControls.Down) != 0 ? 1 : 0);
                    var buttons = CampaignButtons.None;
                    if ((c & NetControls.Shoot) != 0) buttons |= CampaignButtons.Shoot;
                    if ((c & NetControls.Jump) != 0) buttons |= CampaignButtons.Jump;
                    if ((c & NetControls.Morph) != 0) buttons |= CampaignButtons.Morph;
                    if ((c & NetControls.Boost) != 0) buttons |= CampaignButtons.Boost;
                    if ((c & NetControls.AltAttack) != 0) buttons |= CampaignButtons.AltAttack;
                    if ((c & NetControls.Zoom) != 0) buttons |= CampaignButtons.Zoom;
                    SetControls(p.Controls, moveX, moveY, buttons, BeamType.None);
                    if (!SmoothRemotes)
                    {
                        p.HostRemoteYaw = s.Yaw;
                        p.HostRemotePitch = s.Pitch;
                    }
                    p.HostRemoteAimEpoch = p.NetAimEpoch; // a puppet follows the host's view whatever re-aimed it here
                }
            }
            // the Trooper's cling + drone as the host has them (after the poses: a form change comes first)
            snap.Abilities?.Apply(local);
            snap.World?.Apply(Scene);
            if (snap.Items != null)
            {
                _itemSync ??= new ItemSync();
                _itemSync.Apply(Scene, snap.Items, Frame);
            }
            // the host's clock, unless this device is already further on (its time ran out a frame or two before the
            // host's): a snapshot from before the host's time-up would rewind the results timer and end the match early
            if (snap.MatchState > (byte)GameState.MatchState)
            {
                // the host is a stage further on (it ended the match: time-up, the point goal, a team left, a forced end;
                // or GAME OVER -> the ending): this device's stage ends now too, through the game's own steps. It used to
                // take the host's time, the next stage's, so this stage ran that long again, then took the host's -1
                // (exiting) and never reached 0: the guest kept the winner camera spinning until the host went silent
                // (release 0.2.0 F1, scorehang replay)
                GameState.MatchTime = 0;
            }
            else if (snap.MatchState == (byte)GameState.MatchState)
            {
                // (the host's -1 in the ending: it already faded out, so this device's ending is over too)
                GameState.MatchTime = snap.MatchTime < 0 && GameState.MatchState == MatchState.Ending ? 0 : snap.MatchTime;
            }
            // the mode's state: octolith and node events play here (their points and stats are then the host's below)
            snap.Objective?.Apply(Scene);
            for (int i = 0; i < snap.Seats.Length && i < PlayerEntity.MaxPlayers; i++) // (P8: up to 8 seats)
            {
                GameState.Points[i] = snap.Points[i];
                GameState.Kills[i] = snap.Kills[i];
                GameState.Deaths[i] = snap.Deaths[i];
            }
            // online: freezes, burns, disruptions, power-ups, turrets and the detail stats as the host has them
            snap.Extras?.Apply();
            // LAN: the detail stats as the host has them (efficiency, kills by weapon, headshots...)
            if (snap.Stats != null)
            {
                SeatExtras.ApplyStats(snap.Stats);
            }
            // online: kept for a host migration (nothing of it runs here)
            if (snap.Handoff != null)
            {
                _handoff = snap.Handoff;
            }
        }

        // client (online): the newest item spawner + drop timer state the host sent (O5b)
        private HostHandoff? _handoff;

        // O5b host migration (docs/ONLINE_PLAY.md section 9b): this match client hosts the match from now on, from its
        // own copy of it (every seat as the old host last sent it). Damage, respawns, pickups, drops, spawners and the mode's
        // rules run here now (hits the old host never confirmed are lost); the old host's bots become this device's own
        // bots; the old host's seats leave the match as a player who quit. Remote humans already are network seats here:
        // their inputs drive them from now on (SetRemoteInput).
        public void PromoteToHost(System.Collections.Generic.IReadOnlyList<int> oldHostSeats,
            System.Collections.Generic.IReadOnlyList<(int Seat, int Level)> bots)
        {
            if (Match == null || !Match.IsClient)
            {
                return;
            }
            Match.IsClient = false;
            PlayerEntity.NetClient = false;
            PlayerEntity.NetWeaponPending = false;
            MphRecomp.Arenas.EchoesBreakables.Mirror = false; // crates + breakables run here now (a broken one back soon)
            MphRecomp.Arenas.EchoesTurret.Mirror = false; // and Shooting Gallery's turret, from the state it mirrored
            _itemSync ??= new ItemSync();
            _handoff?.Apply(Scene, _itemSync);
            _itemSync.TakeOver();
            _itemsSentAt = long.MinValue;
            foreach ((int seat, int level) in bots)
            {
                if (seat < 0 || seat >= Match.Players.Count)
                {
                    continue;
                }
                PlayerEntity p = PlayerEntity.Players[seat];
                SetControls(p.Controls, 0, 0, CampaignButtons.None, BeamType.None);
                p.NetBecomeBot(level);
                Match.Players[seat].Bot = true;
                Match.Players[seat].Remote = false;
                Match.Players[seat].BotLevel = level;
            }
            foreach (int seat in oldHostSeats)
            {
                if (seat >= 0 && seat < Match.Players.Count && seat != PlayerEntity.MainPlayerIndex)
                {
                    DisconnectSeat(seat);
                }
            }
        }
    }
}

namespace MphRecomp.Multiplayer
{
    // One device's networking around each 60 Hz step of a LAN or online match (Android and the PC tests share it).
    //   host: each remote seat's newest input -> its seat, step, snapshot out.
    //   client: the newest snapshot in, step (own seat on its own input), own input out -- buttons, digital move,
    //   absolute view, the aim epoch it has taken, and a weapon pick when its weapon differs from the host's.
    public sealed class MatchNetDriver
    {
        private readonly Net.IMatchNet _net;
        // an online match (O5): deltas at 30/20 Hz, the input jitter buffer, prediction + lag compensation; LAN stays as it was
        private readonly Net.NetMatchSession? _online;
        private readonly Campaign.CampaignHost _host;
        // client: the host frame the view shows (the newest snapshot taken + the frames stepped since); 0 = none yet
        private uint _viewTick;
        // online client: N3 prediction + reconciliation of its own player
        private readonly Net.ClientPrediction? _prediction;
        // the host's handed pushes for this seat as last seen (SeatExtras.Pushes)
        private bool _pushSeen;
        private byte _pushCount;
        private OpenTK.Mathematics.Vector3 _pushSum;
        // online client: the local player is set onto the host's pose outright only past this drift (reconciliation does the rest)
        private static float OnlineSnapDistance => Net.ClientPrediction.FruityCorrect ? Net.ClientPrediction.DivergeUnits : 6f;

        public Net.ClientPrediction? Prediction => _prediction;
        private Net.RemoteSmoothing? _smoothing;
        public Net.RemoteSmoothing? Smoothing => _smoothing;
        private readonly System.Collections.Generic.HashSet<int> _goneSeats = new();
        private int _seq; // (sent as 16 bits)
        private byte _epoch;
        private sbyte _hostWeapon = -1;
        private BeamType _wantWeapon = BeamType.None; // a switch made here, asked of the host until it has it
        private int _wantSince;
        private bool _gone;
        // online client (E, 2026-10-19): the host's pace asks (NetMatchSession.Pace): the last serial acted on, the frames
        // still to skip (+) or run twice (-), the steps since the last one
        private int _paceSerial, _paceFrames, _paceGap;
        // online client (0.3.6, Net/ClientPosition.cs): the newest host snapshot its prediction took in (its own player compared
        // against it, or set onto it): the basis its position reports name; and the last snapshot's tick (NetApplyTicks)
        private uint _posBasis, _lastSnapTick;
        public int PaceSkipped { get; private set; }
        public int PaceDoubled { get; private set; }

        // host: every remote seat left or went silent (their seats were dropped); client: the host did (match ended)
        public bool PeerGone => _gone;
        // host: the remote seats dropped so far
        public int SeatsGone => _goneSeats.Count;
        // host (online): dropped seats that came back (rejoin)
        public int SeatsBack { get; private set; }
        // online (O5b): this device took over as the match host / followed the match to a new host
        public bool TookOver { get; private set; }
        public int HostMoves => _migrations - _migrationsBefore;
        // (the session's count at this match's start: a rematch after a migration doesn't follow the old one again)
        private int _migrations, _migrationsBefore;

        public MatchNetDriver(Net.IMatchNet net, Campaign.CampaignHost host)
        {
            _net = net;
            _online = net as Net.NetMatchSession;
            _host = host;
            // a served match on Fruity Prime's movement sync (Net/FruitySync.cs): set before the pieces below read it
            Net.FruitySync.On = _online != null && host.Match?.FruitySync == true;
            // (LAN guests too since LAN protocol 17: Wi-Fi to a handheld is a round trip of 30-150 ms, past the old 0.5 pull-back
            // that snapped them onto the host's older pose over and over: 0.2.0 owner 2026-10-11, rubber banding)
            _prediction = !net.IsHost ? new Net.ClientPrediction() : null;
            // online guest: the other players drawn a few frames in the past, blended (Net/RemoteSmoothing.cs)
            _smoothing = !net.IsHost && _online != null && Net.RemoteSmoothing.Enabled ? new Net.RemoteSmoothing() : null;
            host.SmoothRemotes = _smoothing != null;
            _migrations = _migrationsBefore = _online?.Migrations ?? 0;
            // client: the host's form taken over this device's own (PlayerNetConfirm.cs), in the device log
            PlayerEntity.NetFormGraceFrames = DefaultFormGrace;
            Net.SeatPings.Start(net); // (each seat's ping on the scoreboard: Net/SeatPings.cs)
            Net.ShotReport.Start(net); // (each seat's shots and hits, here and on its device: Net/ShotReport.cs)
            Net.ShotCheck.Start(net); // (each shot and claimed hit checked against the host's, in shadow: Net/ShotCheck.cs)
            Net.HitClaims.Start(net); // (a guest's hits its host's copy of the shot missed, checked and applied: Net/HitClaims.cs)
            Net.ClientPosition.Start(net, host); // (an online guest's own position, checked and taken: Net/ClientPosition.cs)
            // (an online guest's snapshot-counted waits count frames: 60 Hz snapshots wait as long as 30 Hz ones did)
            PlayerEntity.NetApplyTicks = 1;
            PlayerEntity.NetApplyScale = !net.IsHost && _online != null ? 2 : 1;
            PlayerEntity.NetFormForced = (p, alt) => Console.WriteLine($"match: guest {_net.Kind} step {_seq}: own player put "
                + $"{(alt ? "in alt form" : "on foot")} as the host has it (grace {PlayerEntity.NetFormGraceFrames} frames)");
        }

        // client: how long this device's own form switch may wait for the host before the host's form wins: a round trip
        // (the ping, once known) plus the host's input buffer, at least 0.75 s
        private const int DefaultFormGrace = 45;

        private void UpdateFormGrace()
        {
            int ping = _online?.LocalPingMs?.Invoke() ?? -1;
            PlayerEntity.NetFormGraceFrames = ping > 0 ? Math.Clamp(ping * 60 / 1000 + 30, DefaultFormGrace, 150) : DefaultFormGrace;
        }

        public Net.IMatchNet Session => _net;

        public void Step(in Campaign.CampaignInput input)
        {
            if (_online != null && _online.Migrations != _migrations)
            {
                FollowMigration();
            }
            if (_online != null && GameState.MatchState >= MatchState.GameOver)
            {
                _online.MatchOver = true; // (scores are final: a host leaving now isn't followed)
            }
            // a player who never showed up gets 20 s (loading on a slow device), one who played 10 s of silence
            if (_net.IsHost)
            {
                System.Collections.Generic.IReadOnlyList<int> seats = _net.RemoteSeats;
                for (int i = 0; i < seats.Count; i++)
                {
                    int seat = seats[i];
                    if (_goneSeats.Contains(seat))
                    {
                        // back (online: its app restarted or its Wi-Fi came back and it joined again; LAN too since release
                        // test T1: LanSession re-admits a held seat): its inputs arrive again
                        if (_net.SilentMs(seat) < 1000 && _net.TryTakeInput(seat, out Net.NetInput back))
                        {
                            _goneSeats.Remove(seat);
                            SeatsBack++;
                            Console.WriteLine($"match: seat {seat} back (inputs again, seq {back.Seq})");
                            _host.ReconnectSeat(seat);
                            _host.SetRemoteInput(seat, back);
                        }
                        continue;
                    }
                    long allowed = _net.InputsFrom(seat) > 0 ? 10000 : 20000;
                    if (_net.SilentMs(seat) > allowed)
                    {
                        _goneSeats.Add(seat);
                        Console.WriteLine($"match: seat {seat} dropped (silent {_net.SilentMs(seat)} ms)");
                        _host.DisconnectSeat(seat);
                        continue;
                    }
                    if (_net.TryTakeInput(seat, out Net.NetInput remote))
                    {
                        _host.SetRemoteInput(seat, remote);
                        if (_online != null) Net.ClientPosition.HostOffer(_host, seat, remote); // (its own position: taken or not)
                    }
                    else if (_online != null)
                    {
                        // (a held frame: Fruity's hold at the last pinned place, or doesn't slide a seat whose device runs its push)
                        if (!Net.ClientPosition.HostHold(_host, seat))
                        {
                            _host.HoldClientPushed(seat);
                        }
                    }
                }
                _gone = seats.Count > 0 && _goneSeats.Count >= seats.Count;
                _host.Step(input);
                if (_online != null) Net.ClientPosition.HostStep(_host); // (the moves the host made by itself this step)
                if (_online != null) Net.ClientPosition.HostPin(_host); // (pinned seats put at their devices' place: before the snapshot)
                _net.SendSnapshot(_host.CaptureSnapshot(everyPart: _online != null, lanStats: _online == null));
                if (_online != null) Net.SeatPings.HostTick(_online);
                Net.HitClaims.HostStep(_host); // (before ShotCheck drains the shot log)
                Net.ShotCheck.HostStep(_host);
                return;
            }
            long allowedHost = _net.SnapshotsReceived > 0 ? 10000 : 20000;
            // (online, O5b: a server that can name a new host gets MigrateWaitMs more; the match runs on meanwhile)
            if (_online != null && _online.CanMigrate)
            {
                allowedHost += Net.NetMatchSession.MigrateWaitMs;
            }
            // (online: this device's own connection dropped: it waits to get back in, release test T1)
            _online?.ReconnectTick();
            if (_online is { Reconnecting: true })
            {
                allowedHost = Math.Max(allowedHost, Net.NetMatchSession.RejoinHoldMs);
            }
            if (!_gone && _net.SilentMs(0) > allowedHost)
            {
                _gone = true;
                Console.WriteLine($"match: no word from the host for {_net.SilentMs(0)} ms: this device's match ends");
                _host.EndMatchNow();
                return;
            }
            if (_gone)
            {
                // ended here: a host heard again later doesn't pull this device back in (it played on as a spectator whose
                // inputs the host never took, after the hold ran out: o-wifi retest 10:04)
                _host.Step(input);
                return;
            }
            // E: the host asked for a frame skipped (nothing stepped or sent: this device's inputs wait less there) or run
            // twice (inputs came late there; the second step without this frame's aim and touch moves), one per few steps
            if (_paceFrames > 0 && ++_paceGap >= Net.NetMatchSession.PaceStepEvery)
            {
                _paceGap = 0;
                _paceFrames--;
                PaceSkipped++;
                return;
            }
            StepClient(input);
            if (_paceFrames < 0 && ++_paceGap >= Net.NetMatchSession.PaceStepEvery && !_gone)
            {
                _paceGap = 0;
                _paceFrames++;
                PaceDoubled++;
                Campaign.CampaignInput again = input;
                again.AimDelta = default;
                again.TouchDx = again.TouchDy = 0;
                again.DialogButton = null;
                StepClient(again);
            }
        }

        private void StepClient(in Campaign.CampaignInput input)
        {
            int local = PlayerEntity.MainPlayerIndex;
            if (_net.TryTakeSnapshot(out Net.Snapshot snap) && local < snap.Seats.Length)
            {
                if (Net.NetMatchSession.ReadPace(snap.InputPace, out int paceSerial, out int paceFrames) && paceSerial != _paceSerial)
                {
                    _paceSerial = paceSerial;
                    _paceFrames = paceFrames;
                    _paceGap = 0;
                }
                if (_online != null)
                {
                    PlayerEntity.NetApplyTicks = _lastSnapTick == 0 ? 1 : (int)Math.Clamp((long)snap.Tick - _lastSnapTick, 1, 6);
                    _lastSnapTick = snap.Tick;
                }
                int pulled = _host.LocalCorrections;
                // online: the raw pose isn't judged in ApplySnapshot (it is a round trip old: a fast player is far from it all the
                // time); the prediction's like-for-like error says when the pose is really off
                _host.ApplySnapshot(snap, _prediction != null && !Net.ClientPrediction.SnapFix ? OnlineSnapDistance
                    : _prediction != null ? 0f : 0.5f);
                _smoothing?.Add(snap, local, _seq);
                if (_prediction != null)
                {
                    bool snapNow = _host.LocalCorrections != pulled;
                    var reconciled = Net.ReconcileResult.NotCompared;
                    if (!snapNow && Net.ClientPrediction.SnapFix)
                    {
                        PlayerEntity me0 = _host.Player;
                        Net.ReconcileResult result = _prediction.Reconcile(snap, local, me0);
                        reconciled = result;
                        // (no like-for-like check possible: the raw distance is all there is to go by, as before)
                        snapNow = result == Net.ReconcileResult.Snap || (result == Net.ReconcileResult.NotCompared
                            && (snap.Seats[local].Position - me0.Position).Length > OnlineSnapDistance);
                        if (snapNow && me0.NetSnapPose(snap.Seats[local]))
                        {
                            _host.CountLocalCorrection();
                        }
                        else
                        {
                            snapNow = false;
                        }
                    }
                    else if (!snapNow)
                    {
                        reconciled = _prediction.Reconcile(snap, local, _host.Player);
                    }
                    if (snapNow)
                    {
                        _prediction.Restart(_seq); // the pose is the host's now: what came before doesn't compare
                    }
                    // (this device's player took the host's word on it as of this snapshot: any move the host made by itself
                    // up to here is in its pose now, so its position reports can name this tick: Net/ClientPosition.cs)
                    if (snapNow || reconciled == Net.ReconcileResult.Compared)
                    {
                        _posBasis = snap.Tick;
                    }
                }
                // knockback (0.3.6, as Fruity Prime does it): the pushes the host handed this device since the last snapshot
                // seen, run here on its own player at once (the host didn't push its copy: it takes this player's reported
                // position and speed instead, Net/ClientPosition.cs). The first sighting only starts the count
                if (_online != null && snap.Extras?.Pushes is { } pushes && local < pushes.Length)
                {
                    Net.SeatExtras.PushState push = pushes[local];
                    if (_pushSeen && push.Count != _pushCount)
                    {
                        OpenTK.Mathematics.Vector3 run = push.Sum - _pushSum;
                        if (Single.IsFinite(run.X) && Single.IsFinite(run.Y) && Single.IsFinite(run.Z) && run.Length <= 10
                            && _host.Player.Health > 0)
                        {
                            _host.Player.ApplyPush(run);
                            _prediction?.GuestPush();
                        }
                    }
                    _pushSeen = true;
                    _pushCount = push.Count;
                    _pushSum = push.Sum;
                }
                _epoch = snap.Seats[local].AimEpoch;
                _hostWeapon = snap.Seats[local].Weapon;
                _viewTick = snap.Tick;
            }
            else if (_viewTick != 0)
            {
                _viewTick++;
            }
            _prediction?.PreStep(_host.Player);
            if (_seq % 60 == 0) UpdateFormGrace();
            if (_online != null) Net.ShotReport.GuestTick(_online, local);
            _smoothing?.Apply(_seq, local);
            BeamType before = _host.Player.CurrentWeapon;
            // (0.3.6: where this player starts this step, the correction still to come included, as the prediction keeps it)
            PlayerEntity self = _host.Player;
            bool report = _online != null && _posBasis != 0 && self.Health > 0 && self.LoadFlags.TestFlag(LoadFlags.Spawned)
                && _prediction?.HoldReports != true; // (held while the host pushes this seat: ClientPrediction.PushGap)
            OpenTK.Mathematics.Vector3 reportAt = self.Position + (_prediction?.Pending ?? OpenTK.Mathematics.Vector3.Zero);
            bool reportAlt = self.IsAltForm;
            OpenTK.Mathematics.Vector3 reportSpeed = self.Speed;
            _host.Step(input);
            PlayerEntity me = _host.Player;
            // a switch made on this device (wheel, cycling, a predicted pickup) is asked of the host until the host has it
            // (or ~1 s: refused). Only switches made in this step count: a host-side switch (pickup auto-equip, empty
            // switch) arrives in the snapshot, so it isn't pushed back as this device's (0.2.0 guest blaster bug)
            if (me.CurrentWeapon != before && (sbyte)me.CurrentWeapon != _hostWeapon)
            {
                _wantWeapon = me.CurrentWeapon;
                _wantSince = _seq;
            }
            if (_wantWeapon != BeamType.None && ((sbyte)_wantWeapon == _hostWeapon || _seq - _wantSince > 60))
            {
                _wantWeapon = BeamType.None;
            }
            BeamType select = input.SelectWeapon != BeamType.None ? input.SelectWeapon : _wantWeapon;
            PlayerEntity.NetWeaponPending = select != BeamType.None;
            var sent = new Net.NetInput
            {
                Seq = (ushort)_seq,
                Buttons = input.Buttons & ~Campaign.CampaignButtons.WeaponMenu,
                MoveX = (sbyte)(input.Move.X > Campaign.CampaignInput.MoveDeadZone ? 1 : input.Move.X < -Campaign.CampaignInput.MoveDeadZone ? -1 : 0),
                MoveY = (sbyte)(input.Move.Y > Campaign.CampaignInput.MoveDeadZone ? 1 : input.Move.Y < -Campaign.CampaignInput.MoveDeadZone ? -1 : 0),
                SelectWeapon = select,
                Yaw = me.NetYaw,
                Pitch = me.NetPitch,
                AimEpoch = _epoch,
                Touching = input.TouchDown,
                TouchDx = input.TouchDx,
                TouchDy = input.TouchDy,
                // (the other seats drawn in the past: the frame they show, +1 for the step they just ran)
                ViewTick = _online == null ? 0 : _smoothing != null && _smoothing.RenderTick(_seq) > 0
                    ? (uint)(_smoothing.RenderTick(_seq) + 1) : _viewTick,
                // (its own position at this step's start: the host may take it, Net/ClientPosition.cs)
                ReportBasis = report ? _posBasis : 0,
                Report = reportAt,
                ReportAlt = reportAlt,
                // (this device runs the pushes the host hands it: the host takes its speed for a while after one)
                ReportPushes = _online != null && Net.ClientPrediction.ClientPushes,
                ReportSpeed = reportSpeed
            };
            _prediction?.Record(_seq, me, Net.ClientPrediction.KeyOf(sent));
            _net.SendInput(sent);
            if (_online != null) Net.HitClaims.GuestStep(_online, local, _seq, sent.ViewTick, _seq); // (before ShotCheck drains it)
            if (_online != null) Net.ShotCheck.GuestStep(_online, local, _seq, sent.ViewTick, _seq);
            _seq++;
            if (_seq % 600 == 0)
            {
                // (device logs: how the guest's own player follows the host)
                Console.WriteLine($"match: guest {_net.Kind} step {_seq}, snapshots {_net.SnapshotsReceived}, pulled back "
                    + $"{_host.LocalCorrections}, {_prediction?.Summary() ?? "no prediction"}");
            }
        }

        // O5b: the server named a new host. This device: the match host from now on (its own copy of the match goes on);
        // another device: its snapshots and acks start over from the new host's, prediction too
        private void FollowMigration()
        {
            _migrations = _online!.Migrations;
            if (_online.IsHost && !TookOver && _online.Promoted is Net.NetMatchSession.Promotion promo)
            {
                TookOver = true;
                _smoothing = null; // (a host runs every seat itself)
                _host.SmoothRemotes = false;
                _host.PromoteToHost(promo.OldHostSeats, promo.Bots);
                Net.ClientPosition.TookOver(_host); // (its guests' positions are checked here now)
                PlayerEntity.NetApplyTicks = PlayerEntity.NetApplyScale = 1;
                return;
            }
            if (_smoothing != null) _smoothing = new Net.RemoteSmoothing(); // (the new host's ticks are its own)
            _prediction?.Restart(_seq);
            _viewTick = 0;
            _posBasis = _lastSnapTick = 0; // (the new host's ticks are its own: no report until its snapshots are taken in)
            _hostWeapon = -1;
            _paceSerial = _paceFrames = _paceGap = 0; // (the new host's asks start over)
        }

        public string StatusLine()
        {
            string role = _net.IsHost ? $"{_net.Kind} host, inputs {_net.InputsReceived}"
                : $"{_net.Kind} client, snapshots {_net.SnapshotsReceived}";
            // O5b: the host left mid-match and the match went on (this device took over, or follows the one that did)
            role += TookOver ? "  (host left: you host now)" : HostMoves > 0 ? "  (host left: a new host took over)" : "";
            int remotes = _net.IsHost ? _net.RemoteSeats.Count : 1;
            if (_gone)
            {
                return role + (!_net.IsHost ? "  (the host left)" : remotes > 1 ? "  (the other players left)"
                    : "  (the other player left)");
            }
            if (_goneSeats.Count > 0)
            {
                return role + $"  ({_goneSeats.Count} of {remotes} players left)";
            }
            return role + (_net.PeerAlive ? "" : remotes > 1 ? "  (no word from the other players)" : "  (no word from the other player)");
        }
    }

    public static class MatchClient
    {
        // The same match as the host's, seen from `seat`: that seat is played here, every other seat (the host's
        // player, other clients, the host's bots) is a puppet of the host.
        public static MatchSettings ForSeat(MatchSettings host, int seat)
        {
            var client = new MatchSettings
            {
                Mode = host.Mode,
                Arena = host.Arena,
                LocalSeat = seat,
                IsClient = true,
                PointGoal = host.PointGoal,
                TimeLimitSeconds = host.TimeLimitSeconds,
                TimeGoalSeconds = host.TimeGoalSeconds,
                DamageLevel = host.DamageLevel,
                FriendlyFire = host.FriendlyFire,
                OctolithReset = host.OctolithReset,
                RadarPlayers = host.RadarPlayers,
                AffinityWeapons = host.AffinityWeapons,
                Abilities = host.Abilities,
                PirateBlaster = host.PirateBlaster,
                Crates = host.Crates, Breakables = host.Breakables, // (the same crates + breakables as the host's: ids, NetWorld)
                ExtraCharacters = host.ExtraCharacters, // (shown only: the host's seats already have them stripped)
                FruitySync = host.FruitySync
            };
            for (int i = 0; i < host.Players.Count; i++)
            {
                MatchPlayer h = host.Players[i];
                client.Players.Add(new MatchPlayer
                {
                    Hunter = h.Hunter,
                    Recolor = h.Recolor,
                    Team = h.Team,
                    Nickname = h.Nickname ?? (h.Bot ? h.Hunter.ToString() : null),
                    Bot = false,
                    Remote = i != seat,
                    RankLevel = h.RankLevel, // (the Hunter License's ranking points: -1 for the host's bots)
                    // every device draws each seat in its skin and knows a Trooper (abilities stay off in network
                    // matches: Characters.AbilitiesFor)
                    Character = h.Character,
                    Skin = h.Skin,
                    ColourPick = h.ColourPick // (the same colours as the host's: LookAlikes.CopyIndices)
                });
            }
            return client;
        }
    }
}
