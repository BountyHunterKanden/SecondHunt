using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // The DEATH look's RAGDOLL on a body drawn on a DS rig with its own bone lengths (the App's CampaignSuit; owner
    // 2026-11-08). PlayerRagdoll falls MphRead's DS body as 16 points, each bone group turning rigidly with its driver
    // about its joint. Here the body's pose at death is MphRead's DS pose then (doll.Rest) on the rig's bone lengths /
    // rest offsets; each frame every bone is that pose turned by its driver's turn about its driver's joint. The joints
    // are carried down the body's own limbs from the ragdoll's pelvis: each one its parent joint plus its own segment
    // turned by the parent's turn, plus the ragdoll's give there (its point off that rigid place: a bent spine, a
    // stretched stick) scaled to the body's segment, so a body with other limb lengths stays joined up and bends as the
    // ragdoll does. With lengths 1 and no offsets it equals PlayerRagdoll.Pose (checked by -ragdoll).
    public sealed class SuitRagdoll
    {
        readonly Matrix4 _model, _modelInv; // the body's model matrix at death and its inverse
        readonly Matrix4[] _rest;           // each bone at death, drawn space (bone * model)
        readonly int[] _driver;             // each bone's driver
        readonly Vector3[] _anchor;         // each driver's joint at death, drawn space
        readonly float[] _give;             // each driver's segment (from its parent's joint) over the ragdoll's
        readonly Vector3[] _anchorNow = new Vector3[PlayerRagdoll.DriverTotal];
        readonly Matrix3[] _turn = new Matrix3[PlayerRagdoll.DriverTotal];
        // a native body's limbs laid along the ragdoll's (null: the DS rig's, which already are): each driver's turn
        // from its own segment at death to the ragdoll's, eased in over the fall's first ticks
        readonly Vector4[]? _lay;           // native: each driver's turn onto the doll's segment (axis, angle)
        readonly Matrix3[] _laid = new Matrix3[PlayerRagdoll.DriverTotal];
        const float LayTicks = 12;
        readonly (int Ankle, int Toe, int Knee, int[] Foot)[]? _feet; // native: each foot (the ankle's bones) and its toe
        const float PinTicks = 20;          // native: its pelvis slides onto the doll's over the fall's first third of a second
        readonly bool[]? _ground;           // native: the bones kept off the floor (not its root or an unanimated one)

        public readonly bool Ok;
        public readonly string? Missing; // the rig bone the ragdoll's body has none of (then !Ok)
        public readonly bool[] Animated; // the bones the DS pose animates (the rest are identity, as DsSkeleton.Fk's)

        // skel: the rig's DS skeleton; lengths / offsets: the rig's (TrophyRig); model: the body's model matrix at death
        // (HunterRig.ModelMatrix on doll.Placement, plus any lift)
        public SuitRagdoll(PlayerRagdoll doll, DsSkeleton skel, float[]? lengths, Vector3?[]? offsets, Matrix4 model)
        {
            int n = skel.Count;
            _model = model;
            _modelInv = model.Inverted();
            _rest = new Matrix4[n];
            _driver = new int[n];
            _anchor = new Vector3[PlayerRagdoll.DriverTotal];
            _give = new float[PlayerRagdoll.DriverTotal];
            Animated = new bool[n];
            Model ds = doll.Models[0];
            Matrix4[] rest = doll.Rest[0];
            Matrix4 toRig = doll.Placement.Inverted();
            // MphRead's DS pose at death, in the rig's space (= DsSkeleton's world with lengths 1)
            var dsRig = new Matrix4[n];
            for (int i = 0; i < n; i++)
            {
                int j = ds.GetNodeIndexByName(skel.Names[i]);
                if (j < 0 || j >= rest.Length)
                {
                    Missing = skel.Names[i];
                    return;
                }
                dsRig[i] = rest[j] * toRig;
                Animated[i] = !Near(dsRig[i], Matrix4.Identity); // AnimateNodes: unanimated -> identity (DsSkeleton.Fk)
            }
            // ... on the rig's bone lengths / offsets, parents first
            var depth = new int[n];
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                for (int k = skel.Parent[i]; k >= 0; k = skel.Parent[k]) depth[i]++;
            }
            Array.Sort(depth, order);
            var world = new Matrix4[n];
            Vector3 ms = skel.ModelScale;
            foreach (int i in order)
            {
                int parent = skel.Parent[i];
                if (!Animated[i])
                {
                    world[i] = Matrix4.Identity;
                    continue;
                }
                Matrix4 local = parent >= 0 ? dsRig[i] * dsRig[parent].Inverted() : dsRig[i];
                if (offsets != null && offsets.Length > i && offsets[i] is Vector3 o) local.Row3.Xyz = new Vector3(o.X / ms.X, o.Y / ms.Y, o.Z / ms.Z);
                else if (lengths != null && lengths.Length > i) local.Row3.Xyz *= lengths[i];
                world[i] = parent >= 0 ? local * world[parent] : local;
            }
            for (int i = 0; i < n; i++) _rest[i] = world[i] * model;
            // each bone's driver: itself or its nearest ancestor that is one (the rest ride the pelvis); each driver's joint
            for (int i = 0; i < n; i++)
            {
                for (int k = i; k >= 0; k = skel.Parent[k])
                {
                    int d = PlayerRagdoll.DriverOf(skel.Names[k]);
                    if (d >= 0)
                    {
                        _driver[i] = d;
                        break;
                    }
                }
            }
            // each driver's joint: its ragdoll point at death, held in its bone (the point may sit off the bone's origin,
            // the pelvis's does)
            for (int d = 0; d < _anchor.Length; d++)
            {
                int b = skel.IndexOf(PlayerRagdoll.DriverBone(d));
                Vector3 start = doll.Start[PlayerRagdoll.DriverPoint(d)];
                int j = b >= 0 ? ds.GetNodeIndexByName(skel.Names[b]) : -1;
                _anchor[d] = b >= 0 && Animated[b] ? Point(Point(start, rest[j].Inverted()), _rest[b]) : start;
            }
            Gives(doll);
            Ok = true;
        }

        // a body on its own rig (the App's native bodies, CampaignRetroBody: Prime 4 Sylux, owner 2026-11-08): bones = its
        // pose at death in model space (NativeBody.World), parent = its skeleton's, dsName = each bone's DS counterpart or
        // null (BodyClips.Ragdoll). Each driver's joint is the origin of the bone named for it; every other bone rides its
        // nearest ancestor that is one (or the pelvis: a spine hung off the root beside the hips). An arm whose DS forearm
        // is its cannon needs nothing more: the forearm's driver turns the cannon, the ragdoll makes the wrist point itself
        public SuitRagdoll(PlayerRagdoll doll, Matrix4[] bones, int[] parent, string?[] dsName, Matrix4 model)
        {
            int n = bones.Length;
            _model = model;
            _modelInv = model.Inverted();
            _rest = new Matrix4[n];
            _driver = new int[n];
            _anchor = new Vector3[PlayerRagdoll.DriverTotal];
            _give = new float[PlayerRagdoll.DriverTotal];
            Animated = new bool[n];
            _ground = new bool[n];
            var at = new int[PlayerRagdoll.DriverTotal];
            Array.Fill(at, -1);
            for (int i = 0; i < n; i++)
            {
                _rest[i] = bones[i] * model;
                Animated[i] = true;
                _ground[i] = parent[i] >= 0 && !Near(bones[i], Matrix4.Identity);
                int d = DriverOfBone(dsName, i);
                if (d >= 0 && at[d] < 0) at[d] = i;
            }
            for (int d = 0; d < at.Length; d++)
            {
                if (at[d] < 0)
                {
                    Missing = PlayerRagdoll.DriverBone(d);
                    return;
                }
                _anchor[d] = _rest[at[d]].Row3.Xyz;
            }
            // its limbs (and chest to head) point their own way at death, which may be the doll's or not (its own clips):
            // each is turned to the doll's segment, or a leg lying flat could end in the floor
            _lay = new Vector4[PlayerRagdoll.DriverTotal];
            foreach ((string driver, string child, int point) in _limbs)
            {
                int d = PlayerRagdoll.DriverOf(driver), c = Array.FindIndex(dsName, x => x == child);
                if (d < 0 || c < 0 || c >= n) continue;
                Vector3 own = _rest[c].Row3.Xyz - _anchor[d];
                Vector3 doll2 = doll.Start[point] - doll.Start[PlayerRagdoll.DriverPoint(d)];
                if (own.LengthSquared < 1e-8f || doll2.LengthSquared < 1e-8f) continue;
                own.Normalize();
                doll2.Normalize();
                Vector3 axis = Vector3.Cross(own, doll2);
                float sin = axis.Length, cos = Vector3.Dot(own, doll2);
                if (sin < 1e-5f) continue; // along it already (or straight back: left as it is)
                _lay[d] = new Vector4(axis / sin, MathF.Atan2(sin, cos));
            }
            for (int i = 0; i < n; i++)
            {
                for (int k = i; k >= 0 && k < n; k = parent[k])
                {
                    int d = DriverOfBone(dsName, k);
                    if (d >= 0)
                    {
                        _driver[i] = d;
                        break;
                    }
                }
            }
            // its feet: a long foot (Sylux's) would point into the floor as he lies face down; Pose turns it back down the
            // shin as far as it needs, as a limp foot does
            var feet = new List<(int, int, int, int[])>();
            foreach ((string ankleName, string knee) in new[] { ("L_ankle", "L_knee"), ("R_ankle", "R_knee") })
            {
                int ankle = Array.FindIndex(dsName, x => x == ankleName), k = at[PlayerRagdoll.DriverOf(knee)];
                if (ankle < 0 || ankle >= n || k < 0) continue;
                var foot = new List<int>();
                int toe = -1;
                float far = 0;
                for (int i = 0; i < n; i++)
                {
                    int j = i;
                    while (j >= 0 && j < n && j != ankle) j = parent[j];
                    if (j != ankle) continue;
                    foot.Add(i);
                    float dist = (_rest[i].Row3.Xyz - _rest[ankle].Row3.Xyz).Length;
                    if (dist > far) { far = dist; toe = i; }
                }
                if (toe >= 0 && far > 0.05f) feet.Add((ankle, toe, k, foot.ToArray()));
            }
            _feet = feet.ToArray();
            Gives(doll);
            Ok = true;
        }

        // each driver turned to the doll's segment: its child joint's DS name and the doll's point there (the head and the
        // pelvis keep their own way; an arm with no wrist, Sylux's coil arm, keeps its forearm's)
        static readonly (string Driver, string Child, int Point)[] _limbs =
        {
            ("Spine_2", "Head_1", PlayerRagdoll.Neck),
            ("L_shoulder", "L_elbow", PlayerRagdoll.LElbow), ("L_elbow", "L_wrist", PlayerRagdoll.LWrist),
            ("R_shoulder", "R_elbow", PlayerRagdoll.RElbow), ("R_elbow", "R_wrist", PlayerRagdoll.RWrist),
            ("L_hip", "L_knee", PlayerRagdoll.LKnee), ("L_knee", "L_ankle", PlayerRagdoll.LAnkle),
            ("R_hip", "R_knee", PlayerRagdoll.RKnee), ("R_knee", "R_ankle", PlayerRagdoll.RAnkle),
        };

        static int DriverOfBone(string?[] dsName, int i) => i < dsName.Length && dsName[i] is string s ? PlayerRagdoll.DriverOf(s) : -1;

        // each driver's segment (from its parent's joint) over the ragdoll's
        void Gives(PlayerRagdoll doll)
        {
            for (int d = 1; d < _anchor.Length; d++)
            {
                int up = PlayerRagdoll.DriverUp(d);
                float dsLength = (doll.Start[PlayerRagdoll.DriverPoint(d)] - doll.Start[PlayerRagdoll.DriverPoint(up)]).Length;
                _give[d] = dsLength > 1e-4f ? (_anchor[d] - _anchor[up]).Length / dsLength : 1f;
                // a native body's segment far longer than the doll's (Zero Suit's hips and waist over the DS rig's short
                // ones) would grow the doll's give with it and pull the joint off its bone: no more than the doll's own
                if (_lay != null) _give[d] = MathF.Min(_give[d], 1f);
            }
        }

        public Matrix4 Model => _model;

        // the bones now, in the rig's space (draw with Model); false when the ragdoll can't make its frames now
        public bool Pose(PlayerRagdoll doll, Matrix4[] world)
        {
            Span<Matrix3> turn = _turn;
            if (!Ok || world.Length < _rest.Length || !doll.Turns(turn))
            {
                return false;
            }
            // (from when the doll is let go: a held death keeps the body's own pose)
            float w = MathF.Min(1, doll.LetGo / LayTicks);
            if (_lay != null)
            {
                for (int d = 0; d < turn.Length; d++)
                {
                    _laid[d] = Turn(_lay[d].Xyz, _lay[d].W * w) * _turn[d];
                }
                turn = _laid;
            }
            int pelvis = PlayerRagdoll.DriverPoint(0);
            // a native body stands on its own legs, its pelvis off the DS rig's (Sylux's ~0.3 lower): the offset turned as
            // it lies would sink it (or float it), so it eases onto the doll's and the laid limbs hang from there
            float off = _lay != null ? 1 - MathF.Min(1, doll.LetGo / PinTicks) : 1;
            _anchorNow[0] = doll.Pos[pelvis] + RowMul(_anchor[0] - doll.Start[pelvis], turn[0]) * off;
            for (int d = 1; d < _anchorNow.Length; d++)
            {
                int up = PlayerRagdoll.DriverUp(d);
                int at = PlayerRagdoll.DriverPoint(d), from = PlayerRagdoll.DriverPoint(up);
                // the doll's own stretch, from its own turn (not the laid one)
                Vector3 give = doll.Pos[at] - doll.Pos[from] - RowMul(doll.Start[at] - doll.Start[from], _turn[up]);
                _anchorNow[d] = _anchorNow[up] + RowMul(_anchor[d] - _anchor[up], turn[up]) + give * _give[d];
            }
            for (int i = 0; i < _rest.Length; i++)
            {
                int d = _driver[i];
                Matrix3 r = turn[d];
                var r4 = new Matrix4(new Vector4(r.Row0, 0), new Vector4(r.Row1, 0), new Vector4(r.Row2, 0), Vector4.UnitW);
                Matrix4 m = _rest[i];
                m.Row3.Xyz -= _anchor[d];
                m *= r4;
                m.Row3.Xyz += _anchorNow[d];
                world[i] = m;
            }
            if (_feet != null)
            {
                foreach ((int ankle, int toe, int knee, int[] foot) in _feet)
                {
                    Lift(world, ankle, toe, knee, foot, w);
                }
            }
            if (_ground != null)
            {
                // a body broader than the DS rig (Sylux's shoulders) lying on its side sinks its lower half under the doll's
                // points: no joint goes more than Sink under the doll's lowest (the doll's points ride a radius off the floor)
                const float Sink = 0.08f;
                float floor = float.MaxValue, low = float.MaxValue;
                foreach (Vector3 p in doll.Pos) floor = MathF.Min(floor, p.Y);
                for (int i = 0; i < _rest.Length; i++)
                {
                    if (_ground[i]) low = MathF.Min(low, world[i].Row3.Y);
                }
                float lift = (floor - Sink - low) * w; // (not while it's held standing: it stands on its own feet)
                if (lift > 0)
                {
                    for (int i = 0; i < _rest.Length; i++) world[i].Row3.Y += lift;
                }
            }
            for (int i = 0; i < _rest.Length; i++)
            {
                world[i] *= _modelInv;
            }
            return true;
        }

        // a foot pointing down past its ankle (the floor's there) turned back toward the shin's way, just enough
        static void Lift(Matrix4[] world, int ankle, int toe, int knee, int[] foot, float w)
        {
            const float Below = 0.05f;
            if (w <= 0) return; // (eased in with the lay: a foot flat on the floor as it stood would turn down the shin)
            Vector3 a = world[ankle].Row3.Xyz;
            Vector3 f = world[toe].Row3.Xyz - a;
            if (f.Y >= -Below) return;
            Vector3 shin = a - world[knee].Row3.Xyz;
            float length = f.Length;
            if (shin.LengthSquared < 1e-8f || length < 1e-4f) return;
            shin.Normalize();
            Vector3 axis = Vector3.Cross(f / length, shin);
            float sin = axis.Length;
            if (sin < 1e-4f) return;
            axis /= sin;
            float most = MathF.Atan2(sin, Vector3.Dot(f / length, shin)), angle = most;
            if (RowMul(f, Turn(axis, most)).Y >= -Below)
            {
                float lo = 0, hi = most;
                for (int i = 0; i < 12; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (RowMul(f, Turn(axis, mid)).Y < -Below) lo = mid;
                    else hi = mid;
                }
                angle = hi;
            }
            Matrix3 r = Turn(axis, angle * w);
            var r4 = new Matrix4(new Vector4(r.Row0, 0), new Vector4(r.Row1, 0), new Vector4(r.Row2, 0), Vector4.UnitW);
            foreach (int i in foot)
            {
                Matrix4 m = world[i];
                m.Row3.Xyz -= a;
                m *= r4;
                m.Row3.Xyz += a;
                world[i] = m;
            }
        }

        // a turn of angle about the unit axis, for RowMul (each row is where that axis goes)
        static Matrix3 Turn(Vector3 axis, float angle)
        {
            if (angle == 0) return Matrix3.Identity;
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            Vector3 Row(Vector3 e) => e * c + Vector3.Cross(axis, e) * s + axis * (Vector3.Dot(axis, e) * (1 - c));
            return new Matrix3(Row(Vector3.UnitX), Row(Vector3.UnitY), Row(Vector3.UnitZ));
        }

        static Vector3 RowMul(Vector3 v, in Matrix3 m) => v.X * m.Row0 + v.Y * m.Row1 + v.Z * m.Row2;
        static Vector3 Point(Vector3 v, in Matrix4 m) => (new Vector4(v, 1) * m).Xyz;

        static bool Near(in Matrix4 a, in Matrix4 b)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (MathF.Abs(a[r, c] - b[r, c]) > 1e-4f) return false;
                }
            }
            return true;
        }
    }
}
