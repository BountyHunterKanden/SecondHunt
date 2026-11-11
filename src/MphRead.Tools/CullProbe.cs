using System;
using System.Collections.Generic;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -replaycullprobe <file> [from] [to] (aim view pop-in, owner 2026-11-02): each step, the room pieces the app's turned
    // views (the aim view at the step and part-way to the next, smooth frames' eye carry included) need, against the
    // pieces the widened walk (Scene.HostDrawFrustum's maths) puts in the draw list. Prints the missing pieces per step.
    // The room's own step walk is run again afterwards, so the replay plays as recorded.
    internal static class CullProbe
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo _view = typeof(Scene).GetField("_viewMatrix", F)!;
        static readonly FieldInfo _camPos = typeof(Scene).GetField("_cameraPosition", F)!;
        static readonly FieldInfo _fEye = typeof(Scene).GetField("_frustumCamPos", F)!;
        static readonly FieldInfo _fFov = typeof(Scene).GetField("_frustumFov", F)!;
        static readonly MethodInfo _fill = typeof(Scene).GetMethod("FillFrustum", F)!;
        static readonly FieldInfo _hcf = typeof(RoomEntity).GetField("_hostCullFrustum", F)!;
        static readonly FieldInfo _hcp = typeof(RoomEntity).GetField("_hostCullPerspective", F)!;
        static readonly MethodInfo _clear = typeof(RoomEntity).GetMethod("ClearRoomPartState", F)!;
        static readonly MethodInfo _walk = typeof(RoomEntity).GetMethod("UpdateRoomParts", F)!;
        static readonly MethodInfo _nodeVis = typeof(RoomEntity).GetMethod("IsNodeVisible", F)!;
        static readonly FieldInfo _head = typeof(RoomEntity).GetField("_partVisInfoHead", F)!;
        static readonly FieldInfo _links = typeof(RoomEntity).GetField("_roomFrustumLinks", F)!;
        static readonly FieldInfo _models = typeof(RoomEntity).GetField("_models", F)!;
        static readonly FieldInfo _connectors = typeof(RoomEntity).GetField("_connectorModels", F)!;
        static readonly FieldInfo _collision = typeof(RoomEntity).GetField("_roomCollision", F)!;

        static int _from, _to;
        static Vector3 _prevAim, _prevEye;
        static bool _havePrev;
        static int _unwidened; static int _steps, _missSteps, _missStepsClassic;

        public static void Run(string[] args)
        {
            _from = args.Length > 2 ? Int32.Parse(args[2]) : 0;
            _to = args.Length > 3 ? Int32.Parse(args[3]) : Int32.MaxValue;
            if (Environment.GetEnvironmentVariable("CULLPROBE_WIDEN") == "1") { Scene.HostDrawCullWiden = _ => 0.6f; ReplayTest.StepProbe = null; } else ReplayTest.StepProbe = Probe; // CULLPROBE_WIDEN=1: plays it with the draw list widened 0.6 rad instead (the replay must stay exact)
            ReplayTest.PlayFile(new[] { "-replayplay", args[1], "view=wide" });
            Console.WriteLine($"CULLPROBE: {_steps} steps checked, {_missSteps} with pieces missing from the aim view's draw list, "
                + $"{_unwidened} node-pictures the UNwidened step list would miss, {_missStepsClassic} with pieces missing for the plain (classic) view's in-between pictures");
        }

        static void Probe(int step, CampaignHost host)
        {
            Scene scene = host.Scene;
            Scene.HostFramingAspect = 0; // WIDE, the default (the replay test starts in DS framing)
            PlayerEntity? main = PlayerEntity.Main;
            if (scene.Room is not RoomEntity room || main == null || !main.IsMainPlayer || main.CameraType != CameraType.First
                || main.Health == 0 || main.IsAltForm || main.IsMorphing || main.IsUnmorphing || main.CameraInfo.NodeRef.PartIndex == -1)
            {
                _havePrev = false;
                return;
            }
            Matrix4 view = (Matrix4)_view.GetValue(scene)!;
            Vector3 camPos = (Vector3)_camPos.GetValue(scene)!;
            Vector3 eye = (Vector3)_fEye.GetValue(scene)!;
            float fov = (float)_fFov.GetValue(scene)!;
            Vector3 facing = main.FacingVector.Normalized(), aim = main.NetAimVector.Normalized();
            if (step < _from || step > _to || fov <= 0)
            {
                Remember(aim, eye);
                return;
            }
            _steps++;
            // the widened walk (LookCullWiden + HostDrawFrustum)
            float turn = MathF.Acos(Math.Clamp(Vector3.Dot(facing, aim), -1f, 1f));
            float aimTurn = _havePrev ? MathF.Min(MathF.Acos(Math.Clamp(Vector3.Dot(_prevAim, aim), -1f, 1f)), 1.2f) : 0;
            float widen = turn + aimTurn + 0.15f;
            float aspect = scene.Size.X / (float)scene.Size.Y;
            float sides = MathF.Min(MathF.Atan(aspect * MathF.Tan(fov / 2)) + widen, MathHelper.DegreesToRadians(85));
            float wfov = MathF.Min(MathF.Max(fov + 2 * widen, 2 * MathF.Atan(MathF.Tan(sides) / aspect)), MathHelper.DegreesToRadians(170));
            HashSet<string> drawn = Walk(scene, room, view, camPos, eye, wfov);
            // the pictures: the aim view at phase 0, 0.5, 1 (the aim's last turn carried, the eye carried by the step's move)
            Vector3 move = _havePrev ? eye - _prevEye : Vector3.Zero;
            Vector3 carryAxis = _havePrev ? Vector3.Cross(_prevAim, aim) : Vector3.Zero;
            float carrySin = carryAxis.Length, carryAngle = MathF.Atan2(carrySin, Vector3.Dot(_prevAim, aim));
            var missing = new SortedSet<string>();
            var missingClassic = new SortedSet<string>();
            HashSet<string> stepOwn = Walk(scene, room, view, camPos, eye, fov);
            foreach (float phase in new[] { 0f, 0.5f, 1f })
            {
                Vector3 a = aim;
                if (phase > 0 && carrySin > 1e-5f && carryAngle < 1.2f) a = Rotate(aim, carryAxis / carrySin, carryAngle * phase).Normalized();
                Matrix4 world = view.Inverted();
                Vector3 fwd = -world.Row2.Xyz.Normalized(), up = world.Row1.Xyz.Normalized();
                Vector3 axis = Vector3.Cross(facing, a);
                float s = axis.Length;
                if (s > 1e-6f)
                {
                    float t = MathF.Atan2(s, Vector3.Dot(facing, a));
                    fwd = Rotate(fwd, axis / s, t);
                    up = Rotate(up, axis / s, t);
                }
                Vector3 carry = move.LengthSquared < 9 ? move * phase : Vector3.Zero;
                Vector3 pos = world.Row3.Xyz + carry;
                Matrix4 turned = Matrix4.LookAt(pos, pos + fwd, up);
                foreach (string n in Walk(scene, room, turned, camPos + carry, eye + carry, fov)) { if (!drawn.Contains(n)) missing.Add(n); if (!stepOwn.Contains(n)) _unwidened++; }
                // classic with smooth frames: the step's view, eye carried (the camera's own turn is ignored here)
                Matrix4 plain = Matrix4.CreateTranslation(-carry) * view;
                foreach (string n in Walk(scene, room, plain, camPos + carry, eye + carry, fov)) if (!stepOwn.Contains(n)) missingClassic.Add(n);
            }
            if (missing.Count > 0)
            {
                _missSteps++;
                Console.WriteLine($"  step {step}: turn {MathHelper.RadiansToDegrees(turn):0.0} aimTurn {MathHelper.RadiansToDegrees(aimTurn):0.0} "
                    + $"widen {MathHelper.RadiansToDegrees(widen):0.0} move {move.Length:0.000} MISSING {string.Join(", ", missing)}");
            }
            if (missingClassic.Count > 0)
            {
                _missStepsClassic++;
                Console.WriteLine($"  step {step}: classic carry move {move.Length:0.000} MISSING {string.Join(", ", missingClassic)}");
            }
            // the room's own step walk again (the game reads it)
            _view.SetValue(scene, view);
            _camPos.SetValue(scene, camPos);
            _hcf.SetValue(room, null);
            _clear.Invoke(room, null);
            _walk.Invoke(room, null);
            Remember(aim, eye);
        }

        static void Remember(Vector3 aim, Vector3 eye)
        {
            _prevAim = aim;
            _prevEye = eye;
            _havePrev = true;
        }

        // the room walk from this view, eye and fov: the nodes DrawRoomParts would draw ("part:model:node name")
        static HashSet<string> Walk(Scene scene, RoomEntity room, Matrix4 view, Vector3 camPos, Vector3 eye, float fov)
        {
            Matrix4 keepView = (Matrix4)_view.GetValue(scene)!;
            Vector3 keepCam = (Vector3)_camPos.GetValue(scene)!;
            _view.SetValue(scene, view);
            _camPos.SetValue(scene, camPos);
            var frustum = new FrustumInfo();
            _fill.Invoke(scene, new object[] { frustum, eye, fov });
            _hcf.SetValue(room, frustum);
            _hcp.SetValue(room, scene.GetPerspectiveMatrix(fov));
            _clear.Invoke(room, null);
            _walk.Invoke(room, null);
            _hcf.SetValue(room, null);
            var result = new HashSet<string>();
            var links = (Array)_links.GetValue(room)!;
            var models = (IReadOnlyList<ModelInstance>)_models.GetValue(room)!;
            var connectors = (IReadOnlyList<ModelInstance>)_connectors.GetValue(room)!;
            var collision = (System.Collections.IList)_collision.GetValue(room)!;
            object? part = _head.GetValue(room);
            while (part != null)
            {
                Type pt = part.GetType();
                var nodeRef = (NodeRef)pt.GetField("NodeRef", F)!.GetValue(part)!;
                object? link0 = links.GetValue(nodeRef.PartIndex);
                ModelInstance inst = nodeRef.ModelIndex == 0 ? models[0] : connectors[nodeRef.ModelIndex - 1];
                Vector3 offset = Vector3.Zero;
                if (nodeRef.ModelIndex != 0)
                {
                    object c = collision[nodeRef.ModelIndex]!;
                    offset = (Vector3)c.GetType().GetProperty("Translation", F)!.GetValue(c)!;
                }
                int nodeIndex = nodeRef.NodeIndex;
                while (inst.Active && nodeIndex != -1)
                {
                    Node node = inst.Model.Nodes[nodeIndex];
                    if (node.Enabled && node.MeshCount > 0)
                    {
                        for (object? link = link0; link != null; link = link.GetType().GetField("Next", F)!.GetValue(link))
                        {
                            object info = link.GetType().GetField("Info", F)!.GetValue(link)!;
                            if ((bool)_nodeVis.Invoke(room, new object[] { info, node, 0x8FFF, offset })!)
                            {
                                result.Add($"{nodeRef.PartIndex}:{nodeRef.ModelIndex}:{node.Name}");
                                break;
                            }
                        }
                    }
                    nodeIndex = node.NextIndex;
                }
                part = pt.GetField("Next", F)!.GetValue(part);
            }
            _view.SetValue(scene, keepView);
            _camPos.SetValue(scene, keepCam);
            return result;
        }

        static Vector3 Rotate(Vector3 v, Vector3 axis, float angle)
        {
            float c = MathF.Cos(angle), s = MathF.Sin(angle);
            return v * c + Vector3.Cross(axis, v) * s + axis * (Vector3.Dot(axis, v) * (1 - c));
        }
    }
}
