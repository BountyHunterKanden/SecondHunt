using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using MphRecomp.Import.Bodies;

namespace MphRecomp.Campaign
{
    // Zero Suit Samus on her own Prime 3 skeleton (bodies/MP3ZeroSuit, docs/ZERO_SUIT_SAMUS.md; owner 2026-10-12: "straighten
    // her out ... a regular human rig ... keep her arm in the firing position like others", one import, nothing mixed in).
    // Her CHAR has only cutscene shots, so she moves with the clips of the same pak's people put on her skeleton by bone
    // name (recipe "clips", BodyClipRetarget): the civilian woman's walk, run and idle, the troopers' strafes. Samus's
    // gameplay: her right arm is held straight along the aim from her own bind pose (DarkSamusBody.Hold, trued ahead and
    // pitched by MPH's aim) and her waist bends with the aim as a hunter's does (AimBone Spine_1).
    public static class ZeroSuitBody
    {
        const string Idle = "b_stand_idle_c_malecivilian", Run = "b_run_c_femalecivilian", Walk = "b_walk_c_femalecivilian";

        // dev (--es zspose <clip>, e.g. "bind" = her T-pose): every move plays this one clip, no arm hold
        public static string? Pose;

#pragma warning disable CA2255 // registers her map with BodyClips before any body loads; no other caller needed
        [ModuleInitializer]
        internal static void Init() => BodyClips.Register(For);
#pragma warning restore CA2255

        static BodyClips? For(NativeBody body)
        {
            if (body.Clip(Run) == null || body.Clip(BodyClipRetarget.BindClip) == null) return null;
            if (Pose != null && body.Clip(Pose) != null)
            {
                var p = new BodyClips { Idle = Pose };
                foreach (PlayerAnimation a in System.Enum.GetValues<PlayerAnimation>()) p.Set(a, Pose, true);
                return p;
            }
            var m = new BodyClips { Idle = Idle };
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.Spawn, PlayerAnimation.Flourish, PlayerAnimation.Turn,
                PlayerAnimation.Morph, PlayerAnimation.Unmorph, PlayerAnimation.JumpNeutral, PlayerAnimation.LandNeutral,
                PlayerAnimation.LandLeft, PlayerAnimation.LandRight, PlayerAnimation.Charge, PlayerAnimation.Shoot, PlayerAnimation.ChargeShoot })
            {
                m.Set(a, Idle, true);
            }
            m.Set(PlayerAnimation.WalkForward, Run, true);
            m.Set(PlayerAnimation.WalkBackward, Walk, true);
            m.Set(PlayerAnimation.WalkLeft, "b_strafe_left_strooper", true);
            m.Set(PlayerAnimation.WalkRight, "b_strafe_right_strooper", true);
            // no jump of anyone's in the pak: mid-stride through the air (first pass)
            foreach (PlayerAnimation j in new[] { PlayerAnimation.JumpForward, PlayerAnimation.JumpBack, PlayerAnimation.JumpLeft, PlayerAnimation.JumpRight })
            {
                m.Set(j, Run, true);
            }
            m.TorsoRoot = "Spine_1";
            m.AimBone = "Spine_1";
            // (hits: no flinch of the right size in the pak yet -- the civilians' heavy hit knocks them down)
            if (DarkSamusBody.Hold.For(body, BodyClipRetarget.BindClip, 0, "R_middle_1") is DarkSamusBody.Hold hold) m.AfterPose = hold.Apply;
            return m;
        }
    }
}
