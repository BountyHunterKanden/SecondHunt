using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the same ragdoll on every device online (owner 2026-11-15, vc 686: "the two phones put a ragdolled body in
    // different places"). Each device built the body at death from its own copy of the seat, and a client's copy is late
    // and smoothed (its place, facing and clips), so the bodies started apart. Now the host sends the pose it built its
    // body from while the seat is dead (the snapshot's ragdolls block, Snapshot.cs: the place, facing, the body's turn and
    // the two layers' clips and frames, the very numbers the host built its own from) and a client builds from that; with
    // the launch (the throws block) the fall starts the same. The host also runs the body at every biped death, drawn or
    // not (a served match host too: NetRagdollRest), and once it lies still sends where (the pelvis and the heading): a
    // client eases its settled body there a few cm a step, so a body that settled elsewhere (this device's world or float
    // math) ends where the host's did. An older host sends neither: its guests build from their own copy as before.
    // Cosmetic: online send only, nothing here is in the replay walk or the checksum
    public struct NetDeathPose
    {
        // a layer with no clip index the snapshot can name (a client poses it on its own clip)
        public const byte NoClip = 0xFF;

        public Vector3 Position;
        // the facing (pitch included), the flat lateral (_field70/_field74) and the body's side (_gunVec2), exact: rounded,
        // a body built from them was another body (the verlet fall is that touchy: -ragdollvariety, -ragdollair)
        public Vector3 Facing;
        public float LateralX, LateralZ;
        public Vector3 Gun;
        // the legs' (layer 1) and the upper body's (layer 2) clip and frame
        public byte LegsAnim, TorsoAnim;
        public ushort LegsFrame, TorsoFrame;
        // stood on the ground at death (an electric death's on-foot hold)
        public bool OnFoot;
        // the host's body at rest: its pelvis and heading (65536 steps a turn), the heading taken along the hips when the
        // chest stood straight up from the pelvis, none when neither had a heading
        public bool HasRest, RestFromHips, RestNoHeading;
        public Vector3 RestPelvis;
        public short RestHeading;

        public Vector3 Lateral => new Vector3(LateralX, 0, LateralZ);
        public float RestYaw => RestHeading * MathF.PI / 32768;

        public static short PackYaw(float yaw) => (short)Math.Clamp((int)MathF.Round(yaw * 32768 / MathF.PI), -32768, 32767);
    }

    public partial class PlayerEntity
    {
        // the pose this seat's last biped death built its ragdoll from (the host sends it while the seat is dead)
        public bool NetDeathPoseSet { get; private set; }
        private NetDeathPose _netDeathPoseNow;
        public NetDeathPose NetDeathPoseNow => _netDeathPoseNow;
        // a client applying the host's death: the pose the host built from (null: an older host sent none)
        private static NetDeathPose? _netDeathPose;

        // host hook (MatchNetDriver; a client promoted to host): run the ragdoll at every biped death, drawn or not, and
        // send where it comes to rest
        public static bool NetRagdollRest { get; set; }
        // the host's body when this device makes none to draw (HostRagdoll off): never drawn, only run for its rest
        private PlayerRagdoll? _netRagdoll;
        // client: the host's rest for this body (null: none yet) and the steps since it came
        private NetDeathPose? _netRest;
        private int _netRestFor;
        // a body still moving here is eased onto the host's rest once it has had this long (1 s) to settle itself
        private const int NetRestWait = 60;

        // the pose at death as the host sends it: the place, the vectors, the clips (the host's own body is built from these
        // very numbers, so it and its guests start alike)
        private NetDeathPose NetDeathPoseHere()
        {
            var pose = new NetDeathPose
            {
                Position = Position,
                Facing = _facingVector,
                LateralX = _field70,
                LateralZ = _field74,
                Gun = _gunVec2,
                OnFoot = Flags1.TestFlag(PlayerFlags1.Standing) || Flags1.TestFlag(PlayerFlags1.StandingPrevious)
            };
            int clips = _bipedModelLods[0].Model.AnimationGroups.Node.Count;
            (pose.LegsAnim, pose.LegsFrame) = NetDeathClipHere(_bipedModel1.AnimInfo, clips);
            (pose.TorsoAnim, pose.TorsoFrame) = NetDeathClipHere(_bipedModel2.AnimInfo, clips);
            return pose;
        }

        private static (byte Anim, ushort Frame) NetDeathClipHere(AnimationInfo info, int clips)
        {
            int index = info.NodeIndex;
            if (info.Node.Group == null || index < 0 || index >= clips || index >= NetDeathPose.NoClip)
            {
                return (NetDeathPose.NoClip, 0);
            }
            return ((byte)index, (ushort)Math.Clamp(info.NodeFrame, 0, ushort.MaxValue));
        }

        // a layer's clip at death from the pose's numbers: this device's own clip when it's the same one (a host body's
        // clips included), else the DS body's; its own as it is when the pose names none
        private AnimationInfo NetDeathClip(ModelInstance layer, byte anim, ushort frame)
        {
            AnimationInfo own = layer.AnimInfo;
            if (anim == NetDeathPose.NoClip)
            {
                return own;
            }
            NodeAnimationGroup? group = own.NodeIndex == anim ? own.Node.Group : null;
            if (group == null)
            {
                IReadOnlyList<NodeAnimationGroup> groups = _bipedModelLods[0].Model.AnimationGroups.Node;
                if (anim >= groups.Count)
                {
                    return own;
                }
                group = groups[anim];
            }
            var info = new AnimationInfo();
            info.Index[0] = anim;
            info.Node.Slot = 0;
            info.Node.Group = group;
            info.FrameCount[0] = group.FrameCount;
            info.Frame[0] = Math.Min(frame, Math.Max(group.FrameCount - 1, 0));
            return info;
        }

        // client (NetApply): the host's rest for this dead body
        private void NetRestFromHost(NetDeathPose rest)
        {
            if (_netRest == null)
            {
                _netRestFor = 0;
            }
            _netRest = rest;
        }

        // each step while dead, after the bodies' steps (RagdollStep)
        private void NetRagdollStep()
        {
            PlayerRagdoll? doll = _ragdoll ?? _netRagdoll;
            if (doll == null)
            {
                return;
            }
            if (NetRagdollRest)
            {
                // host: once its body lies still, where (sent from the next snapshot on; the undrawn body's job is done)
                if (NetDeathPoseSet && !_netDeathPoseNow.HasRest && doll.Asleep)
                {
                    (float yaw, bool hips) = PlayerRagdoll.RestHeading(doll);
                    _netDeathPoseNow.HasRest = true;
                    _netDeathPoseNow.RestPelvis = doll.Pos[PlayerRagdoll.Pelvis];
                    _netDeathPoseNow.RestFromHips = hips;
                    _netDeathPoseNow.RestNoHeading = float.IsNaN(yaw);
                    _netDeathPoseNow.RestHeading = float.IsNaN(yaw) ? (short)0 : NetDeathPose.PackYaw(yaw);
                    _netRagdoll = null;
                }
                return;
            }
            if (NetClient && _ragdoll != null && _netRest is NetDeathPose rest)
            {
                _netRestFor++;
                if (_ragdoll.Asleep || _netRestFor >= NetRestWait)
                {
                    _ragdoll.EaseTo(rest.RestPelvis, rest.RestNoHeading ? float.NaN : rest.RestYaw, rest.RestFromHips);
                }
            }
        }
    }

    public partial class PlayerRagdoll
    {
        // a client's settled body onto the host's: at most EaseMove a step, EaseTurn radians a step (a twentieth of what's
        // left, at least EaseMoveMin / EaseTurnMin, so it ends): a few cm a step, a turn over a second or so, no pop.
        // Past EaseTurnMax off it isn't turned (lying another way up: a turn would only twist it)
        private const float EaseMove = 0.03f, EaseMoveMin = 0.002f, EaseTurn = 0.025f, EaseTurnMin = 0.003f;
        private const float EaseTurnMax = MathF.PI / 2;

        // the body's heading at rest: the pelvis to the chest along the ground, or (that standing straight up) along the hips
        // turned a quarter; NaN when neither has one. fromHips: use that one (the host's choice, so both measure alike)
        public static (float Yaw, bool FromHips) RestHeading(PlayerRagdoll doll, bool? fromHips = null)
        {
            Vector3 spine = doll.Pos[Chest] - doll.Pos[Pelvis];
            var flat = new Vector3(spine.X, 0, spine.Z);
            bool hips = fromHips ?? flat.LengthSquared < 0.25f * spine.LengthSquared;
            if (hips)
            {
                Vector3 across = doll.Pos[RHip] - doll.Pos[LHip];
                flat = new Vector3(across.Z, 0, -across.X); // (UnitY x across)
            }
            if (flat.LengthSquared < 1e-8f)
            {
                return (float.NaN, hips);
            }
            return (MathF.Atan2(flat.X, flat.Z), hips);
        }

        // move and turn the whole body (both step's points and where it was clear) a little toward a rest: the pelvis
        // there, the heading yaw (NaN: not turned). Its shape is kept, and its steps go on from where it's put
        public void EaseTo(Vector3 pelvis, float yaw, bool fromHips)
        {
            Vector3 off = pelvis - Pos[Pelvis];
            float dist = off.Length;
            if (dist > 1e-6f)
            {
                float step = MathF.Min(dist, Math.Clamp(dist * 0.05f, EaseMoveMin, EaseMove));
                Vector3 by = off * (step / dist);
                for (int i = 0; i < PointCount; i++)
                {
                    Pos[i] += by;
                    Prev[i] += by;
                    _clear[i] += by;
                }
            }
            if (float.IsNaN(yaw))
            {
                return;
            }
            float now = RestHeading(this, fromHips).Yaw;
            if (float.IsNaN(now))
            {
                return;
            }
            float diff = yaw - now;
            diff -= MathF.Tau * MathF.Floor((diff + MathF.PI) / MathF.Tau);
            if (MathF.Abs(diff) < 1e-5f || MathF.Abs(diff) > EaseTurnMax)
            {
                return;
            }
            float turn = MathF.Min(MathF.Abs(diff), Math.Clamp(MathF.Abs(diff) * 0.05f, EaseTurnMin, EaseTurn));
            turn = diff < 0 ? -turn : turn;
            float cos = MathF.Cos(turn);
            float sin = MathF.Sin(turn);
            Vector3 about = Pos[Pelvis];
            for (int i = 0; i < PointCount; i++)
            {
                Pos[i] = TurnAbout(Pos[i], about, cos, sin);
                Prev[i] = TurnAbout(Prev[i], about, cos, sin);
                _clear[i] = TurnAbout(_clear[i], about, cos, sin);
            }
        }

        // about the up axis through a point, raising atan2(x, z) by the turn
        private static Vector3 TurnAbout(Vector3 v, Vector3 about, float cos, float sin)
        {
            float x = v.X - about.X;
            float z = v.Z - about.Z;
            return new Vector3(about.X + x * cos + z * sin, v.Y, about.Z + z * cos - x * sin);
        }
    }
}
