using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: client-reported position (0.3.6, MphRecomp Net/ClientPosition.cs). An online guest sends where its own player
    // was at the start of each input's step; the host takes it in place of its own copy when it passes the host's checks.
    // A report made before the guest saw a move the host made by itself is stale, so the host counts those moves here
    // (NetHostMoves): a knockback or hit push, a jump pad, the Trooper's standing body pushing a player out. The rest of
    // the host's own moves (respawn, teleport, a turret seat, morph and unmorph, the drone's split and merge, freezes, death)
    // show in state the host compares around each step (ClientPosition.HostStep). Counting only: nothing here changes how a
    // match plays, and only a host's remote seats count (HostRemote), so local matches and replays are unchanged.
    public enum NetHostMove : byte { None, Push, Pad, Body }

    public partial class PlayerEntity
    {
        // host, a remote seat: the moves the game made by itself so far (MphRecomp Net/ClientPosition.cs) and the last kind
        public int NetHostMoves { get; private set; }
        public NetHostMove NetHostMoveKind { get; private set; }

        private void NetHostMoved(NetHostMove kind)
        {
            if (!HostRemote)
            {
                return;
            }
            NetHostMoves++;
            NetHostMoveKind = kind;
        }

        // the host's own state a guest's report must agree with (ClientPosition: frozen and stunned players don't move)
        public bool NetFrozen => _frozenTimer > 0;

        // host, a remote seat: put where its own device had it (a report the host checked), before the step runs. Only the
        // player's own place moves (and its camera and collision volume with it, as Reposition's do); its speed stays the
        // host's, its beams stay where they are.
        // Knockback (0.3.6, as Fruity Prime does it): a device that says it runs pushes itself (clientPushes) gets a hit's push
        // handed to it (NetHandPush) instead of the host pushing its copy, and for a while after one the host takes its
        // reported speed too (speed), so the host's copy slides as the device's does.
        public void HostTakePosition(Vector3 position, bool clientPushes = false, Vector3? speed = null)
        {
            NetClientPushes = clientPushes;
            if (!HostRemote || !LoadFlags.TestFlag(LoadFlags.Spawned) || _health == 0)
            {
                return;
            }
            if (speed.HasValue)
            {
                Speed = speed.Value;
            }
            Vector3 prev = Position;
            Vector3 offset = position - prev;
            Position = position;
            PrevPosition += offset;
            CameraInfo camInfo = CameraInfo;
            camInfo.Position += offset;
            camInfo.PrevPosition += offset;
            camInfo.Target += offset;
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
            NodeRef = NetSnapNodeRef(_scene, NodeRef, prev, position);
        }

        // ---- knockback handed to the device (0.3.6) ----

        // host, a remote seat: its device runs the host's pushes itself (its last taken report said so)
        public bool NetClientPushes { get; private set; }
        // host: the pushes handed to the device so far (SeatExtras' push section: the count and their sum, which the device
        // compares with the last it saw, so a lost snapshot loses no push)
        public byte NetPushCount { get; private set; }
        public Vector3 NetPushSum { get; private set; }

        // TakeDamage, a hit's push on this player: true = handed to its device (the host's copy isn't pushed; no host move)
        private bool NetHandPush(Vector3 push)
        {
            if (!HostRemote || !NetClientPushes)
            {
                return false;
            }
            if (push != Vector3.Zero)
            {
                NetPushSum += push;
                NetPushCount++;
            }
            return true;
        }

        // a hit's push in normal form (TakeDamage's own math; a match client runs the pushes the host handed it with it.
        // An alt-form push is handed flat, which this adds the same way TakeDamage does)
        public void ApplyPush(Vector3 push)
        {
            Vector3 speed = Speed + push.WithY(0);
            if (push.Y <= 0)
            {
                speed.Y += push.Y;
            }
            else if (speed.Y < 0.25f)
            {
                speed.Y += push.Y;
                if (speed.Y > 0.25f)
                {
                    speed.Y = 0.25f;
                }
            }
            Speed = speed;
        }

        // ---- match client: the snapshot counts below were tuned for 30 Hz online snapshots (0.3.6 sends 60) ----

        // what each applied snapshot counts for in NetApply's waits (the weapon, form and cling mismatches, Weavel's merge):
        // online, the frames since the last one (1 at 60 Hz, 2 at 30, 3 at 20) against waits twice the old snapshot counts, so
        // they last as long as they did at 30 Hz; LAN (a snapshot every frame): 1 and 1, as before. Set by MatchNetDriver
        public static int NetApplyTicks { get; set; } = 1;
        public static int NetApplyScale { get; set; } = 1;
    }
}
