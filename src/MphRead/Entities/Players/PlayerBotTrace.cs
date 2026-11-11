using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp dev: one line per active bot (position, facing, speed, held move buttons, target, node, AI state path, the item
    // it goes for), shared by MphRead.Tools -replaybots and the app's replay viewer (dev builds log it to logcat "MPHBot"
    // while a replay plays: the PC's float math can part from a device's, so a device-recorded match is traced on the
    // device; 2026-11-06, the Mining Plaza stairs bot). Reads only.
    public partial class PlayerEntity
    {
        public static string HostBotTraceLine(PlayerEntity p, int frame)
        {
            PlayerEntity? target = p.AiData.HostTargetPlayer;
            Vector3? node = p.AiData.HostNodePos;
            float t = frame / 60f;
            return $"f{frame,6} {(int)t / 60}:{t % 60,5:00.00} s{p.SlotIndex} {p.Hunter,-8} hp {p.Health,3} pos {p.Position.X,6:0.00} {p.Position.Y,5:0.00} {p.Position.Z,6:0.00}"
                + $" yaw {MathHelper.RadiansToDegrees(MathF.Atan2(p.FacingVector.X, p.FacingVector.Z)),4:0} spd {p.Speed.X,5:0.00} {p.Speed.Y,5:0.00} {p.Speed.Z,5:0.00} gnd {(p.Flags1.TestFlag(PlayerFlags1.Grounded) ? 1 : 0)}"
                + $" alt {(p.IsAltForm ? 1 : 0)} btn {p.AiData.HostTraceButtons} tgt {(target == null ? "-" : target.SlotIndex.ToString())}"
                + $" node {(node is Vector3 n ? $"{n.X:0.0} {n.Y:0.0} {n.Z:0.0}" : "-")} f2 {(uint)p.AiData.Flags2:X} st {p.AiData.HostState} item {p.AiData.HostTraceItem}"
                + (target != null ? $" tpos {target.Position.X:0.0} {target.Position.Y:0.0} {target.Position.Z:0.0} thp {target.Health}" : "");
        }

        public partial class PlayerAiData
        {
            // the move buttons held (X fwd, B back, Y left, A right), L jump, R fire
            public string HostTraceButtons => $"{(_buttons.X.IsDown ? "X" : "-")}{(_buttons.B.IsDown ? "B" : "-")}{(_buttons.Y.IsDown ? "Y" : "-")}"
                + $"{(_buttons.A.IsDown ? "A" : "-")}{(_buttons.L.IsDown ? "L" : "-")}{(_buttons.R.IsDown ? "R" : "-")}";

            public ItemInstanceEntity? HostTargetItem => Flags2.TestFlag(AiFlags2.TargetItem) ? _itemC8 : null;
            public string HostTraceItem => _itemC8 is ItemInstanceEntity it
                ? $"{it.ItemType} {it.Position.X:0.0} {it.Position.Y:0.0} {it.Position.Z:0.0} dsp {it.DespawnTimer}" : "-";
        }
    }
}
