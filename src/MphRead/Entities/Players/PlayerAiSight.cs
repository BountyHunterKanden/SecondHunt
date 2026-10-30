using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: bots shooting through walls (2026-10-23, bug-fix batch A3). The sight test is the game's own (UpdateVisibility:
    // eye to eye, every surface blocks, one pair a tick), but several of its fire rules don't use it: the charge path
    // (Func214380C) presses R at a hidden target to start charging, which fires the uncharged shot into the wall first
    // (about two thirds of the bots' wall shots), the flag can be ~12 frames old, the Shock Coil and targets under sqrt(10)
    // skip it, and the gun sits a little off the eyes, so a corner can block the shot when the eyes see. The game's bots do
    // all of this too. With the rule on, a bot whose gun can't see its target player (the ray a beam takes, so grates
    // let it through) doesn't fire its uncharged / repeat shot (PlayerInput's fire check asks BotSightHold) and keeps a
    // charged shot held a few ticks (4), waiting for a clear line. Charging itself still starts behind cover, as a player would.
    // Host-guarded (MatchSettings.BotFireSight; replays before format 57 play without it).
    public partial class PlayerEntity
    {
        public static bool HostBotFireSight { get; set; }

        // PlayerInput's fire check: true = this bot doesn't fire its uncharged / repeat shot this frame
        private bool BotSightHold() => HostBotFireSight && IsBot && AiData.SightBlocked;

        public partial class PlayerAiData
        {
            // frames a charged shot is held for a clear line: 4 ticks (bots vanilla-parity review), 2 s before (format 57-60 replays)
            private static int SightHoldMax => HostBotVanillaParity ? 4 * 2 : 2 * 60;

            private bool _sightBlocked = false;
            private int _sightHoldFrames = 0;

            public bool SightBlocked => _sightBlocked;

            // before ProcessInput commits the buttons: can the gun see the target player?
            private void UpdateSight()
            {
                _sightBlocked = false;
                if (!HostBotFireSight || _player.IsAltForm || _crateAim || !Flags2.TestFlag(AiFlags2.TargetPlayer)
                    || _targetPlayer == null || _targetPlayer.Health == 0
                    || !_buttons.R.IsDown && !_player.Controls.Shoot.IsDown)
                {
                    return;
                }
                // the point the AI aims at (Func2144AE4), then the head, so a target showing over a low wall counts as seen
                _targetPlayer.GetPosition(out Vector3 targetPos);
                Vector3 body = targetPos.AddY(_targetPlayer.IsAltForm
                    ? Fixed.ToFloat(_targetPlayer.Values.AltColYPos)
                    : 0.5f);
                Vector3 muzzle = _player._muzzlePos;
                _sightBlocked = SightRayBlocked(muzzle, body)
                    && (_targetPlayer.IsAltForm || SightRayBlocked(muzzle, _targetPlayer.CameraInfo.Position));
            }

            private bool SightRayBlocked(Vector3 from, Vector3 to)
            {
                CollisionResult result = default;
                if (!CollisionDetection.CheckBetweenPoints(from, to, TestFlags.Beams, _scene, ref result))
                {
                    return false;
                }
                // a turret occupant's own shell doesn't hide it (PlayerEchoesSeat.cs)
                return !(result.EntityCollision?.Entity is EntityBase shell
                    && (shell == _player.HostSeatShell || shell == _targetPlayer?.HostSeatShell));
            }

            // after the R button is committed: the AI let go of a charged shot the gun can't land, so keep it held
            private void SightHoldCharge(Keybind control, bool prevDown)
            {
                if (_sightBlocked && prevDown && !control.IsDown && control == _player.Controls.Shoot
                    && _sightHoldFrames < SightHoldMax
                    && _player.EquipInfo.ChargeLevel >= _player.EquipInfo.Weapon.MinCharge * 2) // todo: FPS stuff
                {
                    _sightHoldFrames++;
                    control.IsDown = true;
                    control.IsReleased = false;
                    return;
                }
                _sightHoldFrames = 0;
            }
        }
    }
}
