using System;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.Anim
{
    // What the body is doing THIS 60 Hz tick -- the inputs the game's biped animation logic reads.
    // Movement directions are DIGITAL (the DS D-pad); callers threshold an analog stick into these.
    public struct BipedIntent
    {
        public bool Forward, Back, Left, Right;
        // Horizontal aim rotation applied this tick, in DEGREES, using the game's sign: positive =
        // turning LEFT (counter-clockwise seen from above), negative = turning right. The game plays
        // its Turn clip once this exceeds 3 degrees per tick (PlayerInput ProcessBiped).
        public float AimYawDeltaDeg;
        public bool Grounded;       // on the ground (or within the game's coyote window)
        public bool JumpStarted;    // a jump was launched this tick (also: jump-pad launch)
        public bool Landed;         // touched solid ground this tick after being airborne
        public bool Fired;          // an uncharged shot left the gun this tick
        public bool Charging;       // holding fire with some charge built (the game's ChargeLevel > 0)
        public bool ChargeReleased; // a charged shot was released this tick
        public bool HasInput;       // any player input this tick (resets the idle-flourish timer)
        public int TimeSinceJumpPad; // ticks since the last jump pad (walk clips are suppressed for 14)
    }

    // A faithful port of Metroid Prime Hunters' biped (standing-form) animation selection, as recovered
    // in MphRead's PlayerEntity (PlayerInput.ProcessBiped, PlayerCollision landing, SetBipedAnimation).
    //
    // The game plays TWO animation layers on ONE skeleton: the LEGS layer (biped1) drives the root up
    // to Spine_1; the TORSO layer (biped2) drives everything above it. Normally the torso mirrors the
    // legs, frame-synced; shooting/charging/damage override just the torso so a hunter can fire while
    // walking. Clip indices are the game's PlayerAnimation enum -- identical for all 7 hunters (verified
    // against the ROM with -animprobe: 26 node-animation groups each, same order).
    //
    // Timing: logic runs at 60 Hz; clip frames advance once every 2nd tick (the game's 30 fps animation
    // rate, EntityBase.UpdateAnimFrames), independent of how fast the hunter moves.
    //
    // Intentionally NOT covered (no sim systems for them yet): morph/unmorph into the alt form,
    // freeze/death, camera-sequence locks, Spire's climb, Guardian bots.
    public sealed class BipedAnimator
    {
        public ModelInstance Legs { get; }   // the game's _bipedModel1
        public ModelInstance Torso { get; }  // the game's _bipedModel2
        public Model Model => Legs.Model;

        public long Ticks { get; private set; }
        int _timeIdle, _timeSinceInput;
        public bool Manual; // viewer mode: a fixed clip plays on both layers; ProcessBiped is bypassed

        // idle ticks before the Flourish ("look around") clip -- the game's 300 frames at 30 fps
        public const int FlourishDelayTicks = 300 * 2;
        public const float TurnThresholdDeg = 3f;

        public BipedAnimator(Model model)
        {
            Legs = new ModelInstance(model);
            Torso = new ModelInstance(model);
            // The game sets Spawn on both layers at (re)spawn, looping; the first grounded tick then
            // hands over to Idle (Spawn isn't NoLoop, so ProcessBiped replaces it immediately).
            SetBoth(PlayerAnimation.Spawn, AnimFlags.None);
        }

        public PlayerAnimation LegsAnim => (PlayerAnimation)Legs.AnimInfo.Index[0];
        public PlayerAnimation TorsoAnim => (PlayerAnimation)Torso.AnimInfo.Index[0];
        public int LegsFrame => Legs.AnimInfo.Frame[0];
        public int TorsoFrame => Torso.AnimInfo.Frame[0];
        public int LegsFrameCount => Legs.AnimInfo.FrameCount[0];
        public int TorsoFrameCount => Torso.AnimInfo.FrameCount[0];
        AnimFlags LegsFlags { get => Legs.AnimInfo.Flags[0]; set => Legs.AnimInfo.Flags[0] = value; }
        AnimFlags TorsoFlags { get => Torso.AnimInfo.Flags[0]; set => Torso.AnimInfo.Flags[0] = value; }
        public AnimFlags LegsAnimFlags => LegsFlags;
        public AnimFlags TorsoAnimFlags => TorsoFlags;

        void SetLegs(PlayerAnimation a, AnimFlags f) => Legs.SetAnimation((int)a, f);
        void SetTorso(PlayerAnimation a, AnimFlags f) => Torso.SetAnimation((int)a, f);
        public void SetBoth(PlayerAnimation a, AnimFlags f) { SetTorso(a, f); SetLegs(a, f); }

        // Viewer: pin both layers to one clip (looping) and bypass the state logic.
        public void PlayManual(PlayerAnimation a, AnimFlags f = AnimFlags.None)
        {
            Manual = true;
            SetBoth(a, f);
        }

        // Damage flinch: the game plays these on the TORSO only (PlayerEntity TakeDamage), NoLoop.
        public void PlayDamage(PlayerAnimation which) => SetTorso(which, AnimFlags.NoLoop);

        // One 60 Hz logic tick: choose clips like the game, then advance frames at the 30 fps cadence.
        public void Tick(in BipedIntent it)
        {
            if (!Manual) Process(it);
            Ticks++;
            // EntityBase.UpdateAnimFrames: `FrameCount != 0 && FrameCount % 2 == 0`
            if (Ticks % 2 == 0)
            {
                Legs.UpdateAnimFrames();
                Torso.UpdateAnimFrames();
            }
        }

        void Process(in BipedIntent it)
        {
            _timeSinceInput = it.HasInput ? 0 : _timeSinceInput + 1;
            PlayerAnimation anim1 = PlayerAnimation.None, anim2 = PlayerAnimation.None;
            AnimFlags flags1 = AnimFlags.None, flags2 = AnimFlags.None;

            // An in-progress Turn settles back through whichever half is nearer the rest pose.
            // (MphRead's Biped1FrameCount property returns Frame[0] by typo -- `frame <= frame/2` is then
            // only true at frame 0. The comparison is plainly meant against the clip's frame COUNT, as
            // its name says; we use the real count.)
            if (LegsAnim == PlayerAnimation.Turn)
            {
                if (LegsFrame <= LegsFrameCount / 2) LegsFlags |= AnimFlags.Reverse; else LegsFlags &= ~AnimFlags.Reverse;
                LegsFlags |= AnimFlags.NoLoop;
            }
            if (TorsoAnim == PlayerAnimation.Turn)
            {
                if (TorsoFrame <= TorsoFrameCount / 2) TorsoFlags |= AnimFlags.Reverse; else TorsoFlags &= ~AnimFlags.Reverse;
                TorsoFlags |= AnimFlags.NoLoop;
            }

            // aim-turn while standing: faster than 3 deg/tick plays Turn (left = reversed)
            if (it.Grounded)
            {
                if (it.AimYawDeltaDeg > TurnThresholdDeg)
                {
                    _timeIdle = 0;
                    anim1 = PlayerAnimation.Turn;
                    flags1 = AnimFlags.Reverse;
                    if (TorsoAnim == PlayerAnimation.Turn) { TorsoFlags &= ~AnimFlags.NoLoop; TorsoFlags |= AnimFlags.Reverse; }
                    if (LegsAnim == PlayerAnimation.Turn) { LegsFlags &= ~AnimFlags.NoLoop; LegsFlags |= AnimFlags.Reverse; }
                }
                else if (it.AimYawDeltaDeg < -TurnThresholdDeg)
                {
                    _timeIdle = 0;
                    anim1 = PlayerAnimation.Turn;
                    if (TorsoAnim == PlayerAnimation.Turn) { TorsoFlags &= ~AnimFlags.NoLoop; TorsoFlags &= ~AnimFlags.Reverse; }
                    if (LegsAnim == PlayerAnimation.Turn) { LegsFlags &= ~AnimFlags.NoLoop; LegsFlags &= ~AnimFlags.Reverse; }
                }
            }

            // strafing picks the side-step clip only when not also moving forward/back; forward/back
            // is evaluated after and wins. Both need the ground and >7 DS frames since a jump pad.
            bool padOk = it.TimeSinceJumpPad > 7 * 2;
            if (it.Right) { if (!it.Forward && !it.Back && it.Grounded && padOk) anim1 = PlayerAnimation.WalkRight; }
            else if (it.Left) { if (!it.Forward && !it.Back && it.Grounded && padOk) anim1 = PlayerAnimation.WalkLeft; }
            if (it.Forward) { if (it.Grounded && padOk) anim1 = PlayerAnimation.WalkForward; }
            else if (it.Back) { if (it.Grounded && padOk) anim1 = PlayerAnimation.WalkBackward; }

            // jump: direction clip from the held input (forward > back > left > right > neutral)
            if (it.JumpStarted)
            {
                flags1 = AnimFlags.NoLoop;
                anim1 = it.Forward ? PlayerAnimation.JumpForward
                      : it.Back ? PlayerAnimation.JumpBack
                      : it.Left ? PlayerAnimation.JumpLeft
                      : it.Right ? PlayerAnimation.JumpRight
                      : PlayerAnimation.JumpNeutral;
            }

            // torso-only weapon clips
            if (it.Charging && (TorsoFlags.HasFlag(AnimFlags.Ended) || TorsoAnim == PlayerAnimation.Charge
                || TorsoAnim == PlayerAnimation.Shoot && TorsoFrame > 8))
            {
                anim2 = PlayerAnimation.Charge;
            }
            if (it.ChargeReleased)
            {
                anim2 = PlayerAnimation.ChargeShoot;
                flags2 = AnimFlags.NoLoop;
            }
            if (it.Fired)
            {
                anim2 = PlayerAnimation.Shoot;
                flags2 |= AnimFlags.NoLoop;
                if (TorsoAnim == PlayerAnimation.Shoot) SetTorso(PlayerAnimation.Shoot, TorsoFlags); // restart
            }

            // legs: idle / flourish when nothing else was chosen
            if (anim1 == PlayerAnimation.None)
            {
                if (it.Grounded)
                {
                    if (LegsAnim == PlayerAnimation.Idle)
                    {
                        if (++_timeIdle > FlourishDelayTicks && _timeSinceInput > FlourishDelayTicks)
                            SetLegs(PlayerAnimation.Flourish, AnimFlags.NoLoop);
                    }
                    else if (!LegsFlags.HasFlag(AnimFlags.NoLoop) || LegsFlags.HasFlag(AnimFlags.Ended))
                    {
                        _timeIdle = 0;
                        SetLegs(PlayerAnimation.Idle, AnimFlags.None);
                    }
                }
            }
            else if (anim1 != LegsAnim || anim1 is PlayerAnimation.JumpForward or PlayerAnimation.JumpBack
                or PlayerAnimation.JumpLeft or PlayerAnimation.JumpRight or PlayerAnimation.JumpNeutral)
            {
                SetLegs(anim1, flags1);
            }

            // torso: follow the legs (frame-synced) unless playing its own NoLoop clip
            if (anim2 == PlayerAnimation.None)
            {
                if ((!TorsoFlags.HasFlag(AnimFlags.NoLoop) || TorsoFlags.HasFlag(AnimFlags.Ended)) && TorsoAnim != LegsAnim)
                {
                    SetTorso(LegsAnim, LegsFlags);
                    Torso.AnimInfo.Frame[0] = LegsFrame;
                }
            }
            else if (anim2 != TorsoAnim)
            {
                SetTorso(anim2, flags2);
            }

            // landing (the game does this in its collision pass, after ProcessBiped, same tick)
            if (it.Landed)
            {
                if (LegsAnim == PlayerAnimation.JumpLeft) SetLegs(PlayerAnimation.LandLeft, AnimFlags.NoLoop);
                else if (LegsAnim == PlayerAnimation.JumpRight) SetLegs(PlayerAnimation.LandRight, AnimFlags.NoLoop);
                else SetLegs(PlayerAnimation.LandNeutral, AnimFlags.NoLoop);
            }
        }
    }
}
