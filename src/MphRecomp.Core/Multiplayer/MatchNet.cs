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
    public sealed partial class CampaignHost
    {
        private ItemSync? _itemSync;
        private long _itemsSentAt = long.MinValue;

        // host: every seat after this frame (and the items, every 4th frame or when they change)
        public Snapshot CaptureSnapshot()
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
            if (changed || Frame - _itemsSentAt >= 4)
            {
                snap.Items = items;
                _itemsSentAt = Frame;
            }
            return snap;
        }

        // client: how often the local player was pulled onto the host's pose (drifted past the tolerance)
        public int LocalCorrections { get; private set; }

        // client: take the host's frame. The local seat keeps its own (predicted) pose unless it drifted further than
        // localTolerance; everything else is the host's.
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
                p.NetApply(s, pose: true);
                if (p.HostRemote)
                {
                    int c = s.Controls;
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
                    p.HostRemoteYaw = s.Yaw;
                    p.HostRemotePitch = s.Pitch;
                    p.HostRemoteAimEpoch = p.NetAimEpoch; // a puppet follows the host's view whatever re-aimed it here
                }
            }
            if (snap.Items != null)
            {
                _itemSync ??= new ItemSync();
                _itemSync.Apply(Scene, snap.Items, Frame);
            }
            GameState.MatchTime = snap.MatchTime;
            for (int i = 0; i < snap.Seats.Length && i < 4; i++)
            {
                GameState.Points[i] = snap.Points[i];
                GameState.Kills[i] = snap.Kills[i];
                GameState.Deaths[i] = snap.Deaths[i];
            }
        }
    }
}

namespace MphRecomp.Multiplayer
{
    // One device's networking around each 60 Hz step of a LAN match (Android and the PC tests share it).
    //   host: the joiner's newest input -> its seat, step, snapshot out.
    //   client: the newest snapshot in, step (own seat on its own input), own input out -- buttons, digital move,
    //   absolute view, the aim epoch it has taken, and a weapon pick when its weapon differs from the host's.
    public sealed class MatchNetDriver
    {
        private readonly Net.LanSession _net;
        private readonly Campaign.CampaignHost _host;
        private ushort _seq;
        private byte _epoch;
        private sbyte _hostWeapon = -1;
        private bool _gone;

        // the other device left or went silent: the host dropped its seat / the client ended the match
        public bool PeerGone => _gone;

        public MatchNetDriver(Net.LanSession net, Campaign.CampaignHost host)
        {
            _net = net;
            _host = host;
        }

        public Net.LanSession Session => _net;

        public void Step(in Campaign.CampaignInput input)
        {
            // a player who never showed up gets 20 s (loading on a slow device), one who played 10 s of silence
            long allowed = (_net.IsHost ? _net.InputsReceived : _net.SnapshotsReceived) > 0 ? 10000 : 20000;
            if (!_gone && _net.PeerSilentMs > allowed)
            {
                _gone = true;
                if (_net.IsHost)
                {
                    _host.DisconnectSeat(_net.RemoteSeat);
                }
                else
                {
                    _host.EndMatchNow();
                    return;
                }
            }
            if (_net.IsHost)
            {
                if (!_gone && _net.TryTakeInput(out Net.NetInput remote))
                {
                    _host.SetRemoteInput(_net.RemoteSeat, remote);
                }
                _host.Step(input);
                _net.SendSnapshot(_host.CaptureSnapshot());
                return;
            }
            int local = PlayerEntity.MainPlayerIndex;
            if (_net.TryTakeSnapshot(out Net.Snapshot snap) && local < snap.Seats.Length)
            {
                _host.ApplySnapshot(snap);
                _epoch = snap.Seats[local].AimEpoch;
                _hostWeapon = snap.Seats[local].Weapon;
            }
            _host.Step(input);
            PlayerEntity me = _host.Player;
            BeamType select = input.SelectWeapon;
            if (select == BeamType.None && _hostWeapon >= 0 && (sbyte)me.CurrentWeapon != _hostWeapon)
            {
                select = me.CurrentWeapon; // switched here (wheel, cycling): ask the host for the same weapon
            }
            _net.SendInput(new Net.NetInput
            {
                Seq = _seq++,
                Buttons = input.Buttons & ~Campaign.CampaignButtons.WeaponMenu,
                MoveX = (sbyte)(input.Move.X > Campaign.CampaignInput.MoveDeadZone ? 1 : input.Move.X < -Campaign.CampaignInput.MoveDeadZone ? -1 : 0),
                MoveY = (sbyte)(input.Move.Y > Campaign.CampaignInput.MoveDeadZone ? 1 : input.Move.Y < -Campaign.CampaignInput.MoveDeadZone ? -1 : 0),
                SelectWeapon = select,
                Yaw = me.NetYaw,
                Pitch = me.NetPitch,
                AimEpoch = _epoch
            });
        }

        public string StatusLine()
        {
            string role = _net.IsHost ? $"LAN host, inputs {_net.InputsReceived}" : $"LAN client, snapshots {_net.SnapshotsReceived}";
            if (_gone)
            {
                return role + (_net.IsHost ? "  (the other player left)" : "  (the host left)");
            }
            return role + (_net.PeerAlive ? "" : "  (no word from the other player)");
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
                AffinityWeapons = host.AffinityWeapons
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
                    Remote = i != seat
                });
            }
            return client;
        }
    }
}
