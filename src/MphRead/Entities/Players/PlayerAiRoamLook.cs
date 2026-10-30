using System;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: bots looking 30-50 degrees up or down while they roam (2026-10-23, bug-fix batch A6). Nearly all of it is the
    // AI aiming at its target player through the floor or a wall: the fight contexts (Func21436D8 -> Func2144B88) track
    // the target's position whether the bot sees it or not, so a bot roaming under a target on a ledge walks along looking
    // at the ceiling. The game's own bots do this too. With the rule on, while a biped bot can't see its target player
    // (the game's own sight flag), or has no aim of its own for two ticks, its pitch eases toward the slope of its path
    // (the next node, up to 15 degrees), level when it has none, as a player walking would; the yaw is the AI's. Once
    // the bot sees its target the AI's aim takes over again (2-6 degrees a tick), and a bot that can't see its target
    // doesn't fire at it (PlayerAiSight.cs), so the shots it takes are its own.
    // Host-guarded (MatchSettings.BotRoamLook; replays before format 57 play without it).
    public partial class PlayerEntity
    {
        public static bool HostBotRoamLook { get; set; }

        public partial class PlayerAiData
        {
            private const float RoamLookMax = 15;    // degrees up or down toward the path
            private const float RoamLookStep = 1.5f; // degrees a frame at most (an AI aim step is 2-6 a tick)
            private int _roamLookIdle = 0;           // ProcessInput frames since the AI last moved the pitch

            // in ProcessInput's commit, after the AI's own aim: aimY = what the AI set this frame
            private void RoamLook(float aimY)
            {
                _roamLookIdle = aimY != 0 ? 0 : Math.Min(_roamLookIdle + 1, 1000);
                if (!HostBotRoamLook || _crateAim || HostIdleBots || _player.IsAltForm || _player.IsMorphing || _player.IsUnmorphing
                    || _player.Health == 0 || _player.HostSeatShell != null)
                {
                    return;
                }
                bool hasTarget = _targetPlayer != null && _targetPlayer.Health > 0;
                if (hasTarget && IsPlayerVisible(_player, _targetPlayer!))
                {
                    return;
                }
                // the AI's own aim at something other than a hidden target player (a look point, a door, a half turret)
                if (aimY != 0 && !(hasTarget && Flags2.TestFlag(AiFlags2.TargetPlayer)) || aimY == 0 && _roamLookIdle < 4)
                {
                    return;
                }
                float want = 0;
                if (_node40 != null)
                {
                    // nodes sit on the floor (MPH's a little above it), the player half a body up: a small rise is level
                    Vector3 to = _node40.Position - _player.Position;
                    float flat = to.Xz.Length;
                    float rise = to.Y + 0.5f;
                    if (flat > 1 && MathF.Abs(rise) > 0.75f)
                    {
                        want = Math.Clamp(MathHelper.RadiansToDegrees(MathF.Atan2(rise, flat)), -RoamLookMax, RoamLookMax);
                    }
                }
                float pitch = 90 - MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(_player.CameraInfo.Facing.Y, -1, 1)));
                float diff = want - pitch;
                _player._buttonAimY = MathF.Abs(diff) < 0.5f ? 0 : Math.Clamp(diff * 0.1f, -RoamLookStep, RoamLookStep);
            }
        }
    }
}
