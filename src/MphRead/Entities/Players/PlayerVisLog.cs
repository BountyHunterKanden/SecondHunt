using System;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: player visibility log (owner 2026-11-15, Stasis Bunker online: another player vanished in plain sight on the
    // guest's screen; the host's replay draws them, so it's the guest's own state). When another live player in front of
    // the camera (within 50 degrees, 40 units) stops being drawn, logs why (its room part isn't drawn, or HideModel), the
    // part it's in against the part whose geometry is at its spot and the camera's part, distance, angle and alpha; and
    // when it draws again or leaves the view, how many frames it was gone. Draws nothing, changes nothing. Null = off.
    public partial class PlayerEntity
    {
        public static Action<string>? HostVisLog { get; set; }

        private static int _visLogLines;
        private const int VisLogMaxLines = 400;
        private bool _visLogGone;
        private int _visLogFrames;

        // the room-part check in Draw, passed through: logs a vanish/return of a player in view
        private bool VisLogCheck(bool visible)
        {
            VisLogUpdate(visible ? null : "its room part isn't drawn");
            return visible;
        }

        private void VisLogUpdate(string? why)
        {
            if (HostVisLog == null || IsMainPlayer || Health == 0 || _scene.CameraMode != CameraMode.Player)
            {
                _visLogGone = false;
                return;
            }
            Vector3 cam = Main.CameraInfo.Position;
            Vector3 to = Position - cam;
            float dist = to.Length;
            float angle = dist > 0.01f
                ? MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(to / dist, Main.CameraInfo.Facing.Normalized()), -1, 1)))
                : 0;
            bool inView = dist < 40 && angle < 50;
            if (why == null || !inView)
            {
                if (_visLogGone)
                {
                    VisLogWrite($"seat {SlotIndex} {Hunter} {(why == null ? "drawn again" : "left the view still undrawn")}"
                        + $" after {_visLogFrames} frames: part {NodeRef.PartIndex}, camera part {Main.CameraInfo.NodeRef.PartIndex},"
                        + $" dist {dist:0.0}, angle {angle:0}");
                }
                _visLogGone = false;
                return;
            }
            if (_visLogGone)
            {
                _visLogFrames++;
                return;
            }
            _visLogGone = true;
            _visLogFrames = 1;
            NodeRef at = _scene.GetNodeRefContaining(Position);
            VisLogWrite($"seat {SlotIndex} {Hunter} in view but NOT DRAWN ({why}) frame {_scene.FrameCount}:"
                + $" its part {NodeRef.PartIndex} (drawn {_scene.IsNodeRefVisible(NodeRef)}),"
                + $" geometry here part {at.PartIndex} (drawn {(at.PartIndex != -1 && _scene.IsNodeRefVisible(at))}),"
                + $" camera part {Main.CameraInfo.NodeRef.PartIndex}, dist {dist:0.0}, angle {angle:0}, alpha {_curAlpha:0.00},"
                + $" pos {VisF(Position)}, cam {VisF(cam)}, hidden {Flags2.TestFlag(PlayerFlags2.HideModel)}, alt {IsAltForm}");
        }

        private static void VisLogWrite(string msg)
        {
            if (_visLogLines >= VisLogMaxLines)
            {
                return;
            }
            _visLogLines++;
            HostVisLog?.Invoke(_visLogLines == VisLogMaxLines ? msg + " (log limit reached, no more vis lines)" : msg);
        }

        private static string VisF(Vector3 v) => $"{v.X:0.00},{v.Y:0.00},{v.Z:0.00}";
    }
}
