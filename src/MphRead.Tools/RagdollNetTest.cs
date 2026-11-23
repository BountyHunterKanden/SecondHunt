using System;
using System.Diagnostics;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer.Net;
using OpenTK.Mathematics;

namespace MphRead
{
    // -ragdoll's online agreement checks (owner 2026-11-15, vc 686: "the two phones put a ragdolled body in different
    // places"; Entities/Players/PlayerRagdollNet.cs), from RagdollTest.NetThrowCheck on one live bot seat:
    // a host with DEATH off still runs the body (undrawn) to rest and sends where; the snapshot's ragdolls block round-trips
    // behind every mix of the blocks before it, and an older reader (one that stops at Shots) reads the same snapshot with
    // it on; a client applying the host's death on a stale copy of the seat builds the host's very body and rests where the
    // host's did (and without the block, as before, elsewhere); a client body that settled elsewhere eases onto the host's
    // rest a few cm a step, its shape kept. Prints the bytes and the costs (the host's steps per body, a client's ease).
    internal static class RagdollNetTest
    {
        public static int Check(CampaignHost host, int seat, CampaignInput input)
        {
            int fails = 0;
            void Fail(string s) { fails++; Console.WriteLine($"  [FAIL] net ragdoll: {s}"); }
            PlayerEntity p = PlayerEntity.Players[seat];
            int seats = PlayerEntity.PlayerCount;
            bool Respawn(int wait)
            {
                for (int f = 0; f < 900 && p.Health <= 0; f++) { p.RespawnTimer = 0; host.Step(input); }
                for (int f = 0; f < wait && p.Health > 0; f++) host.Step(input);
                return p.Health > 0 && !p.IsAltForm && !p.IsMorphing;
            }
            void HoldDead(int frames)
            {
                for (int f = 0; f < frames; f++) { p.RespawnTimer = 1000; host.Step(input); }
            }
            bool shown = PlayerEntity.HostRagdoll;
            try
            {
                // 1. the host with DEATH off (a served match host's case): no body drawn, but one run to rest and sent
                if (!Respawn(20)) { Fail("the seat never came back as a biped"); return fails; }
                PlayerEntity.NetRagdollRest = true;
                PlayerEntity.HostRagdoll = false;
                p.TakeDamage(999u, DamageFlags.IgnoreInvuln, new Vector3(-0.8f, 0, 0.6f), null);
                if (p.Ragdoll != null || !p.NetDeathPoseSet) Fail($"DEATH off on the host: doll drawn {p.Ragdoll != null}, pose set {p.NetDeathPoseSet} (want none, set)");
                int restAt = -1;
                for (int f = 0; f < 600 && restAt < 0; f++)
                {
                    p.RespawnTimer = 1000;
                    host.Step(input);
                    if (p.NetDeathPoseNow.HasRest) restAt = f + 1;
                }
                PlayerEntity.HostRagdoll = shown;
                NetDeathPose offRest = p.NetDeathPoseNow;
                if (restAt < 0) Fail("DEATH off on the host: its undrawn body never came to rest in 10 s");
                else if (!Finite(offRest.RestPelvis) || (offRest.RestPelvis - offRest.Position).Length > 6)
                    Fail($"DEATH off on the host: rest {offRest.RestPelvis} from a death at {offRest.Position}");

                // 2. the block on the wire: every mix of the blocks before it, the rest on and off, an older reader
                PlayerNetState[] now = Capture(seats);
                if (!now[seat].HasDeathPose || !now[seat].DeathPose.HasRest) Fail("NetCapture left the dead seat's pose or rest out");
                for (int i = 0; i < seats; i++) if (i != seat && now[i].HasDeathPose && PlayerEntity.Players[i].Health > 0) Fail($"seat {i}: a pose while alive");
                int mixes = 0;
                for (int mix = 0; mix < 16; mix++)
                {
                    Snapshot snap = Mix(now, mix);
                    var bytes = new byte[snap.Size];
                    int length = snap.Write(bytes);
                    if (length != bytes.Length) { Fail($"mix {mix}: Size {bytes.Length}, wrote {length}"); continue; }
                    Snapshot read = Snapshot.Read(bytes);
                    for (int i = 0; i < seats; i++)
                    {
                        if (read.Seats[i].HasDeathPose != now[i].HasDeathPose) Fail($"mix {mix} seat {i}: pose {now[i].HasDeathPose} -> {read.Seats[i].HasDeathPose}");
                        else if (now[i].HasDeathPose && !read.Seats[i].DeathPose.Equals(now[i].DeathPose)) Fail($"mix {mix} seat {i}: the pose changed on the way");
                    }
                    if ((read.Taken != null) != (snap.Taken != null) || read.HasLives != snap.HasLives || (read.AimCams != null) != (snap.AimCams != null)
                        || (read.Shots != null) != (snap.Shots != null))
                        Fail($"mix {mix}: the blocks before it read back wrong");
                    // an older reader: the bytes without the block (none of the seats' poses) are these bytes' start, and
                    // these bytes cut where the block starts read back as that same snapshot
                    Snapshot plain = Mix(now, mix, poses: false);
                    var plainBytes = new byte[plain.Size];
                    int plainLength = plain.Write(plainBytes);
                    int poseCount = 0;
                    foreach (PlayerNetState s in now) if (s.HasDeathPose) poseCount++;
                    int blockAt = length - 2 - poseCount * Snapshot.DeathPoseSize;
                    if (!bytes.AsSpan(0, plainLength).SequenceEqual(plainBytes.AsSpan(0, plainLength))) Fail($"mix {mix}: the bytes before the block changed");
                    Snapshot old = Snapshot.Read(bytes.AsSpan(0, blockAt));
                    var again = new byte[old.Size];
                    int againLength = old.Write(again);
                    if (!again.AsSpan(0, againLength).SequenceEqual(plainBytes.AsSpan(0, plainLength))) Fail($"mix {mix}: an older reader read something else");
                    mixes++;
                }
                // the bytes: the block on a snapshot, and online (SnapshotDelta) the same pose again and the rest arriving
                Snapshot bare = Mix(now, 0, poses: false), unrested = Mix(now, 0), rested = Mix(now, 0);
                unrested.Seats[seat].DeathPose = now[seat].DeathPose with { HasRest = false, RestPelvis = default, RestHeading = 0, RestFromHips = false, RestNoHeading = false };
                int blockBytes = unrested.Size - bare.Size;
                SnapshotDelta.Encoded eBare = SnapshotDelta.Encode(bare), eUnrested = SnapshotDelta.Encode(unrested), eRested = SnapshotDelta.Encode(rested);
                var packet = new byte[4096];
                int dBareSame = SnapshotDelta.Write(packet, eBare, eBare, 0, 0);
                int dSame = SnapshotDelta.Write(packet, eUnrested, eUnrested, 0, 0) - dBareSame;
                int dRest = SnapshotDelta.Write(packet, eRested, eUnrested, 0, 0) - dBareSame;
                int dDeath = SnapshotDelta.Write(packet, eUnrested, eBare, 0, 0) - dBareSame;
                Console.WriteLine($"  [..] net ragdoll bytes: block {blockBytes} B on one dead seat (+{Snapshot.DeathPoseSize} each more), online delta: "
                    + $"at death +{dDeath} B, unchanged +{dSame} B, the rest arriving +{dRest} B");

                // 3. the host with DEATH on, for the client checks: its body at death and where it rests (a copy stepped on its own)
                if (!Respawn(20)) { Fail("the seat never came back for the client check"); return fails; }
                p.TakeDamage(999u, DamageFlags.IgnoreInvuln, new Vector3(0.6f, 0, 0.8f), null);
                if (p.Ragdoll == null || !p.NetDeathPoseSet) { Fail("no host body at the death"); return fails; }
                NetDeathPose hostPose = p.NetDeathPoseNow;
                Vector3 hostSpeed = p.NetThrowSpeed, hostPush = p.NetThrowPush;
                PlayerRagdoll hostStart = p.Ragdoll.Clone();
                // (the costs: a warm-up copy, then a timed one)
                Rest(hostStart.Clone(), host);
                PlayerRagdoll hostRest = hostStart.Clone();
                var sw = Stopwatch.StartNew();
                int steps = Rest(hostRest, host);
                double stepUs = sw.Elapsed.TotalMilliseconds * 1000 / Math.Max(steps, 1);
                if (!hostRest.Asleep) Fail("the host's body never came to rest in 10 s");
                (float hostYaw, bool hostHips) = PlayerRagdoll.RestHeading(hostRest);
                NetDeathPose restPose = hostPose;
                restPose.HasRest = true;
                restPose.RestPelvis = hostRest.Pos[PlayerRagdoll.Pelvis];
                restPose.RestFromHips = hostHips;
                restPose.RestNoHeading = float.IsNaN(hostYaw);
                restPose.RestHeading = float.IsNaN(hostYaw) ? (short)0 : NetDeathPose.PackYaw(hostYaw);
                HoldDead(10);
                PlayerEntity.NetRagdollRest = false;

                // 4. a client: its copy of the seat stale (alive somewhere else, another clip), the host's death applied
                float without = -1;
                for (int pass = 0; pass < 2; pass++)
                {
                    bool sent = pass == 0;
                    if (!Respawn(45)) { Fail("the seat never came back for the client check"); return fails; }
                    float stale = (p.Position - hostPose.Position).Length;
                    PlayerNetState death = p.NetCapture();
                    death.Health = 0;
                    death.HasThrow = true;
                    death.ThrowSpeed = hostSpeed;
                    death.ThrowPush = hostPush;
                    death.HasDeathPose = sent;
                    death.DeathPose = sent ? hostPose : default;
                    PlayerEntity.NetClient = true;
                    p.NetApply(death, pose: false);
                    PlayerRagdoll? doll = p.Ragdoll;
                    if (doll == null) { PlayerEntity.NetClient = false; Fail($"no client body ({(sent ? "pose sent" : "no pose")})"); return fails; }
                    float startOff = 0;
                    for (int i = 0; i < PlayerRagdoll.PointCount; i++) startOff = MathF.Max(startOff, (doll.Pos[i] - hostStart.Pos[i]).Length);
                    // the host's rest arrives a second in (from the snapshot after its body rests)
                    PlayerNetState rest = death;
                    rest.DeathPose = restPose;
                    rest.HasDeathPose = sent;
                    for (int f = 0; f < 600; f++)
                    {
                        p.RespawnTimer = 1000;
                        if (f >= 60) p.NetApply(rest, pose: false);
                        host.Step(input);
                    }
                    PlayerEntity.NetClient = false;
                    float restOff = (doll.Pos[PlayerRagdoll.Pelvis] - restPose.RestPelvis).Length;
                    if (sent)
                    {
                        if (startOff > 1e-3f) Fail($"client body built {startOff:0.0000} from the host's (its copy of the seat {stale:0.00} away)");
                        if (restOff > 0.01f) Fail($"client body rests {restOff:0.000} from the host's");
                        if (fails == 0) Console.WriteLine($"  [OK] net ragdoll: stale client copy {stale:0.00} away -> body built {startOff:0.000000} from the host's, rests {restOff:0.0000} from it");
                    }
                    else
                    {
                        without = startOff;
                        Console.WriteLine($"  [..] net ragdoll: no pose (an older host): body built {startOff:0.00} from the host's, rests {restOff:0.00} from it (the owner's report)");
                    }
                }

                // 5. the ease on a body that settled elsewhere: a copy of the host's resting body put 0.3 off and a turn of
                // 0.4 away, eased back each step as a client does (no pop: at most EaseMove / EaseTurn a step, shape kept)
                PlayerRagdoll moved = hostRest.Clone();
                Vector3 off = new Vector3(0.24f, 0, -0.18f);
                float turnBy = 0.4f;
                for (int i = 0; i < PlayerRagdoll.PointCount; i++)
                {
                    Vector3 v = moved.Pos[i] - hostRest.Pos[PlayerRagdoll.Pelvis];
                    float c = MathF.Cos(-turnBy), s = MathF.Sin(-turnBy);
                    moved.Pos[i] = hostRest.Pos[PlayerRagdoll.Pelvis] + off + new Vector3(v.X * c + v.Z * s, v.Y, v.Z * c - v.X * s);
                    moved.Prev[i] = moved.Pos[i];
                }
                float shape0 = Shape(moved);
                float yawTo = restPose.RestNoHeading ? float.NaN : restPose.RestYaw;
                float maxMove = 0, maxTurn = 0;
                int easedIn = -1;
                var sw2 = Stopwatch.StartNew();
                for (int f = 0; f < 600; f++)
                {
                    Vector3 before = moved.Pos[PlayerRagdoll.Pelvis];
                    float yawBefore = PlayerRagdoll.RestHeading(moved, restPose.RestFromHips).Yaw;
                    moved.EaseTo(restPose.RestPelvis, yawTo, restPose.RestFromHips);
                    maxMove = MathF.Max(maxMove, (moved.Pos[PlayerRagdoll.Pelvis] - before).Length);
                    float dy = PlayerRagdoll.RestHeading(moved, restPose.RestFromHips).Yaw - yawBefore;
                    dy -= MathF.Tau * MathF.Floor((dy + MathF.PI) / MathF.Tau);
                    if (!float.IsNaN(dy)) maxTurn = MathF.Max(maxTurn, MathF.Abs(dy));
                    if (easedIn < 0 && (moved.Pos[PlayerRagdoll.Pelvis] - restPose.RestPelvis).Length < 0.005f && YawOff(moved, restPose) < 0.01f) easedIn = f + 1;
                }
                double easeUs = sw2.Elapsed.TotalMilliseconds * 1000 / 600;
                float shapeOff = MathF.Abs(Shape(moved) - shape0);
                if (easedIn < 0) Fail($"eased body still {(moved.Pos[PlayerRagdoll.Pelvis] - restPose.RestPelvis).Length:0.000} / {YawOff(moved, restPose):0.000} rad off after 10 s");
                if (maxMove > 0.0301f || maxTurn > 0.0251f) Fail($"ease step {maxMove:0.000} / {maxTurn:0.000} rad (a pop: want at most 0.03 / 0.025)");
                if (shapeOff > 1e-3f) Fail($"ease changed the body's shape by {shapeOff:0.0000}");
                Console.WriteLine($"  [..] net ragdoll costs: host body {steps} steps to rest at {stepUs:0.0} us a step (8 seats x 2 served matches "
                    + $"all falling at once: {16 * stepUs / 1000:0.00} ms of a 16.7 ms frame), DEATH-off host body rested at step {restAt}; "
                    + $"client ease {easeUs:0.00} us a step, 0.3 m + 0.4 rad eased in {easedIn} steps (max {maxMove:0.000} m, {maxTurn:0.000} rad a step)");
                if (fails == 0) Console.WriteLine($"  [OK] net ragdoll: block round trip on {mixes} block mixes, older reader the same, DEATH-off host rest at "
                    + $"step {restAt}, client from the host's pose (without: {without:0.00} off), ease in {easedIn} steps");
            }
            finally
            {
                PlayerEntity.HostRagdoll = shown;
                PlayerEntity.NetRagdollRest = false;
                PlayerEntity.NetClient = false;
            }
            return fails;
        }

        static PlayerNetState[] Capture(int seats)
        {
            var s = new PlayerNetState[seats];
            for (int i = 0; i < seats; i++) s[i] = PlayerEntity.Players[i].NetCapture();
            return s;
        }

        // a snapshot of these seats with the later blocks on or off (mix bits: Taken, lives, aim cams, shots)
        static Snapshot Mix(PlayerNetState[] seats, int mix, bool poses = true)
        {
            var snap = new Snapshot
            {
                Seats = (PlayerNetState[])seats.Clone(), Points = new short[seats.Length], Kills = new short[seats.Length],
                Deaths = new short[seats.Length]
            };
            if (!poses) for (int i = 0; i < seats.Length; i++) snap.Seats[i].HasDeathPose = false;
            if ((mix & 1) != 0) snap.Taken = new (ushort, byte)[] { (7, 1) };
            if ((mix & 2) != 0) { snap.HasLives = true; snap.MatchId = 3; }
            if ((mix & 4) != 0) snap.AimCams = new ushort[] { 5, 9 };
            if ((mix & 8) != 0) snap.Shots = new NetHostShotBlock(11, Array.Empty<(byte, NetShotEvent)>());
            return snap;
        }

        static int Rest(PlayerRagdoll doll, CampaignHost host)
        {
            int f = 0;
            for (; f < 600 && !doll.Asleep; f++) doll.Step(host.Scene);
            return f;
        }

        static float YawOff(PlayerRagdoll doll, NetDeathPose rest)
        {
            if (rest.RestNoHeading) return 0;
            float d = PlayerRagdoll.RestHeading(doll, rest.RestFromHips).Yaw - rest.RestYaw;
            d -= MathF.Tau * MathF.Floor((d + MathF.PI) / MathF.Tau);
            return MathF.Abs(d);
        }

        // the sum of every point's distance from the pelvis (kept by a move and a turn)
        static float Shape(PlayerRagdoll doll)
        {
            float sum = 0;
            for (int i = 0; i < PlayerRagdoll.PointCount; i++) sum += (doll.Pos[i] - doll.Pos[PlayerRagdoll.Pelvis]).Length;
            return sum;
        }

        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
