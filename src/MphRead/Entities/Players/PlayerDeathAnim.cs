using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the DEATH look option (owner 2026-11-07, docs/IDEAS_SURVEY.md D1): First Hunt's collapse on MPH's hunters.
    // The host (MphRecomp.Core Anim/DeathCollapse) retargets the cart's collapse onto each hunter's rig and adds it to
    // the body model as clip DeathClip; with HostDeathCollapse on, a biped death plays it once on both layers and the
    // body stays drawn on its last frame until the respawn hides it, as First Hunt's does (FirstHuntDeath), instead of
    // MPH's death particles. The alt forms keep MPH's death. Off by default: MphRead unchanged
    public partial class PlayerEntity
    {
        public static bool HostDeathCollapse { get; set; }
        public const int DeathClip = 26; // (after MPH's 26 clips, First Hunt's number for it in MPH's order)

        // the cart's body in MPH's clip order (FirstHuntBodyModel), for the retarget
        public static ModelInstance FirstHuntBody() => FirstHuntBodyModel(0);

        // this death collapses: the option is on, a biped, and the body has the clip (a hunter it couldn't be made for
        // dies as MPH's)
        private bool _deathCollapsing;

        private void DeathCollapse()
        {
            _deathCollapsing = false;
            if (!HostDeathCollapse || HostFirstHunt || IsAltForm || IsMorphing)
            {
                return;
            }
            // (the draw swaps in the far LOD's model or a host body: each one it can use needs the clip, or SetModel would
            // index past its lists)
            if (_hostBody != null ? !HasDeathClip(_hostBody.Model) : !HasDeathClip(_bipedModelLods[0].Model) || !HasDeathClip(_bipedModelLods[1].Model))
            {
                return;
            }
            _deathCollapsing = true;
            DeathOrbClips();
            SetBipedAnimation((PlayerAnimation)DeathClip, AnimFlags.NoLoop);
        }

        private static bool HasDeathClip(Model m)
        {
            AnimationGroups g = m.AnimationGroups;
            return g.Node.Count > DeathClip && g.Material.Count > DeathClip && g.Texcoord.Count > DeathClip && g.Texture.Count > DeathClip;
        }

        // the body stays drawn while dead: First Hunt's, or a collapsing or ragdoll death (PlayerRagdoll.cs). Where the host
        // draws the seat's own body (an imported suit, with no collapse clip): only a ragdoll the host poses that body on
        // (HostOwnRagdoll; the main player's suit always does), otherwise that seat dies as MPH's
        private bool DrawnWhileDead => HostFirstHunt
            || (IsMainPlayer && HostOwnMainBiped ? RagdollDrawn
                : HostOwnBiped ? RagdollDrawn && HostOwnRagdoll
                : _deathCollapsing || RagdollDrawn);

        // a multiplayer body that stays drawn while dead stays until the respawn (owner 2026-11-09: "can the bodies stay
        // on the map until the player respawns"): the respawn countdown's end hides the model (HideModel, sim state the
        // replay walks, left as it is) while the seat waits to respawn, so the draw keeps a collapsed or ragdoll body past
        // it. First Hunt's own mode keeps its rules
        private bool KeptWhileDead => _health == 0 && GameState.Multiplayer && !HostFirstHunt && DrawnWhileDead;

        // DEATH's VANILLA ORBS (owner 2026-11-09: "combine it with vanilla so you get the blue orbs in the form of the
        // fighter floating up plus the rag doll"): a body drawn while dead also gets MPH's death particles, made as MPH
        // makes them: every frame from the body's pose where it stood, on the clips MPH's death leaves playing (it sets
        // none: the clips at death run on, PlayerProcess). The collapse clip replaces those in the sim, so its death keeps
        // copies of them running for the orbs (match state, like the clips: a seek shows the same orbs); the ragdoll
        // replaces only the drawn pose, so the orbs take the pose from before it. The setting is draw only
        public static bool HostDeathOrbs { get; set; }
        private ModelInstance? _deathOrbClip1;
        private ModelInstance? _deathOrbClip2;
        // for the test (RagdollTest): the lower-body clip the orbs play (the kept one under the collapse, else the body's),
        // its frame, whether it ended, and whether it's a kept one
        public (int Clip, int Frame, bool Ended, bool Kept) DeathOrbClip
        {
            get
            {
                AnimationInfo info = (_deathOrbClip1 ?? _bipedModel1).AnimInfo;
                return (info.NodeIndex, info.NodeFrame, info.Flags[info.Node.Slot].TestFlag(AnimFlags.Ended), _deathOrbClip1 != null);
            }
        }

        // the orbs' pose this frame, in the body's model space (draw only)
        private Vector3[]? _deathOrbPose;

        // at a collapsing death, before the collapse clip is set: the clips MPH's death would keep playing
        private void DeathOrbClips()
        {
            _deathOrbClip1 = OrbClip(_bipedModel1);
            _deathOrbClip2 = OrbClip(_bipedModel2);
        }

        private static ModelInstance OrbClip(ModelInstance from)
        {
            var inst = new ModelInstance(from.Model);
            AnimationInfo a = from.AnimInfo;
            AnimationInfo b = inst.AnimInfo;
            for (int i = 0; i < 2; i++)
            {
                b.Index[i] = a.Index[i];
                b.PrevIndex[i] = a.PrevIndex[i];
                b.Frame[i] = a.Frame[i];
                b.FrameCount[i] = a.FrameCount[i];
                b.Flags[i] = a.Flags[i];
                b.Step[i] = a.Step[i];
            }
            b.Node.Slot = a.Node.Slot;
            b.Node.Group = a.Node.Group;
            b.Material.Slot = a.Material.Slot;
            b.Material.Group = a.Material.Group;
            b.Texcoord.Slot = a.Texcoord.Slot;
            b.Texcoord.Group = a.Texcoord.Group;
            b.Texture.Slot = a.Texture.Slot;
            b.Texture.Group = a.Texture.Group;
            return inst;
        }

        // the sim's frame step, beside the body's clips (the same 30 Hz advance): while dead and collapsing, else let go
        private void DeathOrbFrames()
        {
            if (_health > 0 || !_deathCollapsing)
            {
                _deathOrbClip1 = null;
                _deathOrbClip2 = null;
            }
            else if (_deathOrbClip1 != null && _deathOrbClip2 != null)
            {
                UpdateAnimFrames(_deathOrbClip1);
                UpdateAnimFrames(_deathOrbClip2);
            }
        }

        // the draw, before it animates the body (the spine split set up): the orbs' pose, animated as MPH's draw animates
        // the body, on the kept clips under the collapse or else the body's own
        private void DeathOrbPose(Model model, Node spineNode)
        {
            if (!HostDeathOrbs || _health > 0 || !DrawnWhileDead)
            {
                _deathOrbPose = null;
                return;
            }
            AnimationInfo lower = _deathOrbClip1?.AnimInfo ?? _bipedModel1.AnimInfo;
            AnimationInfo upper = _deathOrbClip2?.AnimInfo ?? _bipedModel2.AnimInfo;
            model.AnimateNodes(index: 0, false, Matrix4.Identity, Vector3.One, lower);
            spineNode.AnimIgnoreChild = false;
            model.AnimateNodes(spineNode.ChildIndex, false, Matrix4.Identity, Vector3.One, upper);
            spineNode.AnimIgnoreChild = true;
            if (_deathOrbPose == null || _deathOrbPose.Length != model.Nodes.Count)
            {
                _deathOrbPose = new Vector3[model.Nodes.Count];
            }
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                _deathOrbPose[i] = new Vector3(model.Nodes[i].Animation.Row3);
            }
        }

        // a node of the orbs' pose, placed where the body stands (the draw's transform, as MPH's nodes are)
        private Vector3 DeathOrbNode(int i)
        {
            return _deathOrbPose != null && i < _deathOrbPose.Length ? Vector3.TransformPosition(_deathOrbPose[i], _modelTransform)
                : new Vector3(_bipedModel1.Model.Nodes[i].Animation.Row3);
        }
    }
}
