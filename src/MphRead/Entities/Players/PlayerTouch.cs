using System;

namespace MphRead.Entities
{
    // recomp: the DS touch screen's stylus controls that MphRead left as todos, for players on the on-screen touch
    // controls (MphRead.Android/MatchTouchOverlay.cs; owner 2026-10-10 N19 / N20: "morph ball touch control exactly as
    // vanilla DS does it", "double tap to jump ... like vanilla for touch only users"). Read from US rev 1 arm9:
    //   touch state 0x02029778 (one block per player): touching / was touching / new touch / held past the press frame /
    //     released, hold frames, and dx4/dy4 = the stylus movement summed over the last 4 ticks (0 unless held past
    //     the press frame; dy grows downward), in DS touch-screen pixels (256x192)
    //   double tap 0x02029F0C: a new touch 3-7 ticks after the previous new touch (no distance or drag check)
    //   biped jump 0x020253C8: the double tap is an alternate jump input; alt form never reads it
    //   touch roll (ProcessAlt 0x02021DC0, hunters without AltFormStrafe): speed -= dy4 * 5 / 4096 * fb + dx4 * 5 / 4096 * lr
    //     (5 * JumpPadSlideFactor under a jump pad's control lock), before the d-pad's traction; ball friction and the
    //     speed cap do the rest, so a flick rolls on
    //   touch boost 0x020235EC: re-armed (CanTouchBoost) while the stylus is up; a drag of more than 90 px in 4 ticks
    //     while held boosts once at full strength in the drag's direction, instead of the button's charged boost
    // The game runs these per 30 Hz tick; at 60 Hz the 4-tick window is 8 frames and the tap gap 6-15 frames.
    // The host gives each human seat this frame's stylus (HostTouch*: down + pixels moved since the last frame); a seat
    // that never gets any (a controller, a bot) stays "not touching", so nothing here changes how it plays.
    public partial class PlayerEntity
    {
        // set by the host before each frame: the stylus is on the touch area, and how far it moved since the last frame
        public bool HostTouchDown { get; set; }
        public sbyte HostTouchDx { get; set; }
        public sbyte HostTouchDy { get; set; }

        private const int TouchWindow = 4 * 2; // dx4/dy4's 4 ticks
        private const int TouchTapMin = 3 * 2; // the double tap's gap between new touches, 3-7 ticks
        private const int TouchTapMax = 7 * 2 + 1;
        private const int TouchBoostSq = 90 * 90; // 8100: more than 90 px in 4 ticks

        private bool _touching;
        private bool _touchHeld; // held past the press frame
        private readonly sbyte[] _touchRingX = new sbyte[TouchWindow];
        private readonly sbyte[] _touchRingY = new sbyte[TouchWindow];
        private int _touchRingIndex;
        private int _touchDx4;
        private int _touchDy4;
        private int _touchTapCounter = 255;
        private bool _touchDoubleTap;

        // the touch controls (UI): in this alt form a drag on the touch area rolls the ball (Samus, Kanden, Spire, Noxus)
        // instead of turning the view (Trace's and Weavel's alt forms aim with the stylus in the game; ours keep the stick)
        public bool HostTouchRolls => IsAltForm && Values.AltFormStrafe == 0 && !HostScoutDrone;

        private bool TouchMoved => _touchDx4 != 0 || _touchDy4 != 0;

        private void TouchClearRing()
        {
            Array.Clear(_touchRingX);
            Array.Clear(_touchRingY);
            _touchRingIndex = 0;
            _touchDx4 = _touchDy4 = 0;
        }

        // once per frame, before the player reads its input (ProcessInput)
        private void TouchTick()
        {
            bool down = HostTouchDown && !IsBot;
            bool wasTouching = _touching;
            bool newTouch = down && !wasTouching;
            _touching = down;
            _touchHeld = down && wasTouching;
            if (_touchHeld)
            {
                _touchRingX[_touchRingIndex] = HostTouchDx;
                _touchRingY[_touchRingIndex] = HostTouchDy;
                _touchRingIndex = (_touchRingIndex + 1) % TouchWindow;
                int dx = 0, dy = 0;
                for (int i = 0; i < TouchWindow; i++)
                {
                    dx += _touchRingX[i];
                    dy += _touchRingY[i];
                }
                _touchDx4 = dx;
                _touchDy4 = dy;
            }
            else
            {
                TouchClearRing();
            }
            HostTouchDx = HostTouchDy = 0; // a frame's movement counts once (a network seat's input can hold for frames)
            _touchDoubleTap = false;
            if (_touchTapCounter < 255)
            {
                _touchTapCounter++;
            }
            if (newTouch)
            {
                _touchDoubleTap = _touchTapCounter >= TouchTapMin && _touchTapCounter <= TouchTapMax;
                _touchTapCounter = 0;
            }
        }

        // the biped's jump: the double tap is the game's hard-coded alternate jump input
        private bool TouchJumpPressed => _touchDoubleTap && !Flags1.TestFlag(PlayerFlags1.NoAimInput);

        // ProcessAlt (Samus, Kanden, Spire, Noxus): the stylus drag rolls the ball. traction60 is what MphRead does to the
        // d-pad's traction at 60 Hz off a jump pad (s / (1 + s), see ProcessAlt), so the roll keeps vanilla's per-tick speed
        private OpenTK.Mathematics.Vector3 TouchRollDelta(bool traction60)
        {
            if (!_touching || !TouchMoved || IsMorphing || Flags1.TestFlag(PlayerFlags1.NoAimInput))
            {
                return OpenTK.Mathematics.Vector3.Zero;
            }
            float k = 5 / 4096f;
            if (_jumpPadControlLockMin > 0)
            {
                k *= Fixed.ToFloat(Values.JumpPadSlideFactor);
            }
            if (traction60)
            {
                k *= _walkFriction / (1 + _walkFriction);
            }
            float fb = _touchDy4 * k, lr = _touchDx4 * k;
            return new OpenTK.Mathematics.Vector3(-(fb * _altRollFbX + lr * _altRollLrX), 0, -(fb * _altRollFbZ + lr * _altRollLrZ));
        }

        // the touch boost, before the button's: true when it boosted this frame (the button's boost code is skipped)
        private bool TouchBoost(ref OpenTK.Mathematics.Vector3 speedDelta)
        {
            if (!_touching)
            {
                Flags1 |= PlayerFlags1.CanTouchBoost;
            }
            int lenSq = _touchDx4 * _touchDx4 + _touchDy4 * _touchDy4;
            if (Flags1.TestFlag(PlayerFlags1.Boosting) || !Flags1.TestFlag(PlayerFlags1.CanTouchBoost) || !_touchHeld
                || lenSq <= TouchBoostSq)
            {
                return false;
            }
            float len = MathF.Sqrt(lenSq);
            float fb = -_touchDy4 / len, lr = -_touchDx4 / len;
            float max = Fixed.ToFloat(Values.BoostSpeedMax);
            speedDelta.X += max * (fb * CameraInfo.Field48 + lr * CameraInfo.Field50);
            speedDelta.Z += max * (fb * CameraInfo.Field4C + lr * CameraInfo.Field54);
            float cap = Fixed.ToFloat(Values.BoostSpeedCap);
            if (_hSpeedCap < cap)
            {
                _hSpeedCap = cap;
            }
            _boostDamage = Values.AltAttackDamage;
            _altAttackCooldown = (ushort)(Values.AltAttackCooldown * 2); // todo: FPS stuff
            int sfx = Metadata.HunterSfx[(int)Hunter, (int)HunterSfx.Boost];
            _soundSource.PlaySfx(sfx);
            if (IsMainPlayer)
            {
                _boostInst.SetAnimation(start: 0, target: 10, frames: 11, afterAnim: 0);
            }
            if (_boostEffect != null)
            {
                _scene.UnlinkEffectEntry(_boostEffect);
                _boostEffect = null;
            }
            _boostEffect = _scene.SpawnEffectGetEntry(136, _gunVec2, _facingVector, Position); // samusDash
            if (_boostEffect != null)
            {
                _boostEffect.SetElementExtension(true);
            }
            Flags1 |= PlayerFlags1.Boosting;
            Flags1 &= ~PlayerFlags1.CanTouchBoost;
            return true;
        }
    }
}
