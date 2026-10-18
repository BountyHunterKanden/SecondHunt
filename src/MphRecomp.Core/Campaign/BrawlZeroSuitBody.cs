using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using MphRecomp.Anim;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Brawl Zero Suit Samus on HER OWN skeleton (bodies/SSBBZeroSuit: Brawl's fighter FitSZerosuit00, Import/BrawlBody;
    // docs/ZERO_SUIT_SAMUS.md section 8). Owner 2026-10-15:
    //   15:05 "the idle pose is really the only pose i like ... the only brawl animation we should have is her idle and her
    //   crawl. the rest should all be from samuss rig";
    //   15:3x on Samus's clips: "her running looks really funny ... samus vanilla is in armor and zero suit is just a
    //   person ... we need to get her gun facing where she is aiming like samus always is, and we need to make all her
    //   movement animation more fluid like a human not wearing armor would look" -- a free CC0 humanoid set is fine
    //   ("i need the movement to look like it belongs in the game"), with vanilla Samus "for whatever this set is missing".
    //   16:0x on that build ("all the animations look bad"): "use samuss vanilla rig, but adjust it so that the body parts
    //   sit relative where they should be ... dont use the free rig pack but maybe you can use it as a reference to get
    //   samus legs closer together".
    // So (Moves, live in the gun-adjust pause):
    //  - Moves.Samus (default): every move vanilla Samus's DS clips put on her skeleton at load (DsRetarget, from the
    //    player's own MPH ROM), idle included, with Samus's armoured spacing taken in (BrawlZeroSuitRig.Narrow: legs closer
    //    together, the off arm against her side, by angles measured against the human set);
    //  - Moves.SamusAsIs: the same clips untouched (the 10-15 15:16 build), for comparison;
    //  - Moves.Human: the Quaternius Universal Animation Library clips (CC0 1.0) put on her skeleton by the body build
    //    ("ual:<name>", Tools UalRetarget) for the run forward, jumps, landings and hits, her Brawl Wait1 idle, Samus's
    //    for the rest (the 10-15 15:39 build);
    //  - her gun arm on the aim in every state, her hair and chest on springs (BrawlZeroSuitRig, after each pose);
    //  - her Brawl crawl (Crawl) is in the body for the crawl alt form, which isn't built yet (section 7, phase 3).
    // Hits play on the upper body too, from BustN (dev --es zsaim arm: from the gun shoulder only). The waist bends with
    // the aim as a hunter's does (AimBone BodyN).
    public static class BrawlZeroSuitBody
    {
        const string Wait = "Wait1";
        public const string Ds = "ds:", Ual = "ual:";
        // Samus's clips baked onto her (BrawlZeroSuitRig.Bake): taken in, and her walks straightened too
        const string Taken = "dsn:", Straight = "dss:";
        // and with her torso held upright (BrawlZeroSuitRig.Upright)
        const string Upright = "dsu:";
        // the idle's and the walks' clips: stand-ins whose frames are the chosen set's (the animator keeps the idle and the
        // lean blend's clips it was given)
        const string Idle = "zs:Idle", Forward = "zs:Forward", Back = "zs:Back", Left = "zs:Left", Right = "zs:Right";
        // her Brawl idle with both arms Samus's (taken in): owner 10-15 17:1x "can we get her gun arm like vanilla samus while
        // idling? and same with her other arm since the idle pose is her holding the gun with both hands, but keep the rest
        // of the idle"
        const string WaitArms = "zs:WaitArms";
        // and with her torso upright (owner 10-15 17:3x: "straighten her torso in idle pose, but keep everything else")
        const string WaitUp = "zs:WaitUp", WaitUpArms = "zs:WaitUpArms";
        // her idle flourish: Brawl's Wait2 (owner 10-15: "there is a 'flourish' animation that she does if she idles for a
        // while. vanilla hunters have flourish animations can we give her this as one"), upright as her idle is; MPH's
        // Flourish state's length (fitted), her gun arm off the aim meanwhile and her face Wait2's (BrawlZeroSuitRig)
        const string Flourish = BrawlZeroSuitFaces.FlourishClip, FlourishUp = "zs:Wait2Up";
        // the first-person gun arm's hold (BrawlZeroSuitRig.Cannon): the idle's grip
        public const string HoldClip = WaitUpArms, HoldFallback = WaitArms;
        static readonly string[] Arms = { "LShoulderN", "RShoulderN" };
        // her Brawl crawl clips (the alt form's, when it's built)
        public static readonly string[] Crawl = { "Squat", "SquatWait", "SquatF", "SquatB", "SquatRv" };

        // dev (--es zsaim): "upper" (hits from BustN, default) or "arm"
        public static string Aim = "upper";
        public enum Moves { Samus, SamusAsIs, Human }
        // dev, live (the gun-adjust pause)
        public static Moves Set { get; private set; } = Moves.Samus;

#pragma warning disable CA2255 // registers her map with BodyClips before any body loads; no other caller needed
        [ModuleInitializer]
        internal static void Init() => BodyClips.Register(For);
#pragma warning restore CA2255

        static readonly PlayerAnimation[] Walks =
        {
            PlayerAnimation.WalkForward, PlayerAnimation.WalkBackward, PlayerAnimation.WalkLeft, PlayerAnimation.WalkRight,
        };
        static readonly PlayerAnimation[] Jumps =
        {
            PlayerAnimation.JumpNeutral, PlayerAnimation.JumpForward, PlayerAnimation.JumpBack, PlayerAnimation.JumpLeft, PlayerAnimation.JumpRight,
        };
        static readonly PlayerAnimation[] Lands = { PlayerAnimation.LandNeutral, PlayerAnimation.LandLeft, PlayerAnimation.LandRight };
        static readonly PlayerAnimation[] Hits =
        {
            PlayerAnimation.DamageFront, PlayerAnimation.DamageBack, PlayerAnimation.DamageLeft, PlayerAnimation.DamageRight,
        };

        static bool Loops(PlayerAnimation a) => a is PlayerAnimation.Idle or PlayerAnimation.Turn or PlayerAnimation.WalkForward
            or PlayerAnimation.WalkBackward or PlayerAnimation.WalkLeft or PlayerAnimation.WalkRight or PlayerAnimation.Charge;

        // the last body loaded: what it has (for the dev pause text and log)
        public static string Info { get; private set; } = "";

        // the bodies loaded (for the live switch)
        static readonly List<(WeakReference<NativeBody> Body, BodyClips Map)> _loaded = new();

        static BodyClips? For(NativeBody body)
        {
            if (body.Game != "ssbb" || body.Clip(Wait) == null || body.Bone("RHaveN") < 0 || body.Bone("RShoulderJ") < 0) return null;
            var all = new List<PlayerAnimation> { PlayerAnimation.Idle, PlayerAnimation.Turn, PlayerAnimation.Morph, PlayerAnimation.Unmorph };
            all.AddRange(Walks);
            all.AddRange(Jumps);
            all.AddRange(Lands);
            all.AddRange(Hits);
            // (her hands keep her Brawl idle's grip: Samus's DS hands have no fingers)
            DsRetarget.AddClips(body, all, Loops, (Wait, new[] { "RHandN", "LHandN" }));
            var rig = new BrawlZeroSuitRig(body);
            foreach (PlayerAnimation a in all)
            {
                if (body.Clip(Ds + a) is not HdGun.Anim src) continue;
                bool walk = Array.IndexOf(Walks, a) >= 0;
                Bake(body, rig.Bake(body, src, Taken + a, walk: false, track: false), Ds + a);
                bool track = a is PlayerAnimation.WalkForward or PlayerAnimation.WalkBackward;
                if (walk) Bake(body, rig.Bake(body, src, Straight + a, walk: true, track), Ds + a);
                if (walk) Bake(body, rig.Bake(body, src, Upright + a, walk: true, track, upright: true), Ds + a);
            }

            if (body.Clip(Wait) is HdGun.Anim wait && body.Clip(Taken + PlayerAnimation.Idle) is HdGun.Anim samusIdle)
            {
                body.Data.Anims[WaitArms] = WithArms(body, wait, samusIdle, WaitArms);
                body.Data.Anims[WaitUp] = rig.BakeUpright(body, wait, WaitUp);
                body.Data.Anims[WaitUpArms] = WithArms(body, body.Data.Anims[WaitUp], samusIdle, WaitUpArms);
            }
            if (body.Clip(Flourish) is HdGun.Anim flourish) body.Data.Anims[FlourishUp] = rig.BakeUpright(body, flourish, FlourishUp);

            var m = new BodyClips { Idle = Idle };
            foreach (string s in new[] { Idle, Forward, Back, Left, Right }) body.Data.Anims[s] = new HdGun.Anim { Name = s };
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Idle, PlayerAnimation.Spawn, PlayerAnimation.Flourish,
                PlayerAnimation.Charge, PlayerAnimation.Shoot, PlayerAnimation.ChargeShoot })
            {
                m.Set(a, Idle, true);
            }
            m.Set(PlayerAnimation.WalkForward, Forward, true);
            m.Set(PlayerAnimation.WalkBackward, Back, true);
            m.Set(PlayerAnimation.WalkLeft, Left, true);
            m.Set(PlayerAnimation.WalkRight, Right, true);
            if (body.Clip(Ds + PlayerAnimation.WalkBackward) != null && body.Clip(Ds + PlayerAnimation.WalkLeft) != null
                && body.Clip(Ds + PlayerAnimation.WalkRight) != null)
            {
                m.Lean = (Forward, Back, Left, Right);
            }
            m.TorsoRoot = Aim == "arm" ? "RShoulderJ" : "BustN";
            m.AimBone = "BodyN";
            m.AfterPose = rig.AfterPose;
            bool human = body.Clip(Ual + "Jog_Fwd_Loop") != null;
            Info = $"{(human ? "human clips" : "NO human clips (an older body build)")}; {rig.Describe()}";
            Apply(body, m, Set);
            lock (_loaded) _loaded.Add((new WeakReference<NativeBody>(body), m));
            return m;
        }

        // dev, live: every loaded body's moves to that set
        public static void SetMoves(Moves set)
        {
            Set = set;
            lock (_loaded)
            {
                _loaded.RemoveAll(e => !e.Body.TryGetTarget(out _));
                foreach ((WeakReference<NativeBody> w, BodyClips m) in _loaded)
                {
                    if (w.TryGetTarget(out NativeBody? body)) Apply(body, m, set);
                }
            }
        }

        // clip `body`'s pose with the arms (Arms and all under them) from clip `arms`, on arms's keys: body's loop played a
        // whole number of times in the same span (a little faster or slower), so both loop together. Each arm hangs from its
        // shoulder the way it does in `arms` in model space (her Brawl idle turns her chest toward the gun; under it Samus's
        // arms, taken as they sit on Samus's chest, held the off elbow out wide)
        static HdGun.Anim WithArms(NativeBody nb, HdGun.Anim body, HdGun.Anim arms, string name)
        {
            HdGun g = nb.Data;
            int n = g.BoneCount, stride = g.KeyStride;
            var arm = new bool[n];
            foreach (int i in g.Order) arm[i] = Array.IndexOf(Arms, g.Names[i]) >= 0 || g.Parent[i] >= 0 && arm[g.Parent[i]];
            int loops = body.Duration > 0 ? Math.Max(1, (int)MathF.Round(arms.Duration / body.Duration)) : 1;
            float rate = arms.Duration > 0 ? body.Duration * loops / arms.Duration : 0;
            var a = new HdGun.Anim { Name = name, Keys = arms.Keys, Tick = arms.Tick, Duration = arms.Duration, Frames = new float[arms.Keys * n * stride] };
            var wa = new Quaternion[n];
            var wb = new Quaternion[n];
            for (int key = 0; key < arms.Keys; key++)
            {
                float t = Math.Min(arms.Duration, key * arms.Tick), tb = body.Duration > 0 ? t * rate % body.Duration : 0;
                if (key == arms.Keys - 1) tb = 0;   // (the loop's last key = its first)
                // each clip's bones turned in model space (row vectors: local, then the parent's)
                foreach (int i in g.Order)
                {
                    g.Sample(arms, t, i, out Quaternion qa, out _);
                    g.Sample(body, tb, i, out Quaternion qb, out _);
                    wa[i] = g.Parent[i] >= 0 ? wa[g.Parent[i]] * qa : qa;
                    wb[i] = g.Parent[i] >= 0 ? wb[g.Parent[i]] * qb : qb;
                }
                for (int i = 0; i < n; i++)
                {
                    g.Sample(arm[i] ? arms : body, arm[i] ? t : tb, i, out Quaternion q, out Vector3 p, out Vector3 s);
                    if (arm[i] && Array.IndexOf(Arms, g.Names[i]) >= 0 && g.Parent[i] >= 0)
                    {
                        // the arm's root: arms's model-space turn, under body's parent (its place stays body's)
                        g.Sample(body, tb, i, out _, out p, out s);
                        q = Quaternion.Invert(wb[g.Parent[i]]) * wa[i];
                        q.Normalize();
                    }
                    int o = (key * n + i) * stride;
                    a.Frames[o] = q.X; a.Frames[o + 1] = q.Y; a.Frames[o + 2] = q.Z; a.Frames[o + 3] = q.W;
                    a.Frames[o + 4] = p.X; a.Frames[o + 5] = p.Y; a.Frames[o + 6] = p.Z;
                    if (stride >= 10) { a.Frames[o + 7] = s.X; a.Frames[o + 8] = s.Y; a.Frames[o + 9] = s.Z; }
                }
            }
            return a;
        }

        // a baked clip added, with its source's ground speed (for the step rate)
        static void Bake(NativeBody body, HdGun.Anim a, string from)
        {
            body.Data.Anims[a.Name] = a;
            if (body.Speeds.TryGetValue(from, out float v)) body.Speeds[a.Name] = v;
        }

        static void Apply(NativeBody body, BodyClips m, Moves set)
        {
            bool human = set == Moves.Human && body.Clip(Ual + "Jog_Fwd_Loop") != null;
            // Samus's clips: taken in (Moves.Samus) or as they are; her walks straightened too (BrawlZeroSuitRig.Straight)
            string p = set == Moves.Samus ? Taken : Ds, w = set == Moves.Samus && BrawlZeroSuitRig.Straight ? Straight : p;
            if (w == Straight && BrawlZeroSuitRig.Upright) w = Upright;
            string idle = BrawlZeroSuitRig.Upright && body.Clip(WaitUpArms) != null ? WaitUpArms : WaitArms;
            // the stand-ins take the chosen clips' frames (the walks their ground speed too, for the step rate); her own
            // Brawl idle (owner 10-15 16:5x: "we lost the idle pose and her hand holding the gun"), Samus's only as they are
            Stand(body, Idle, set == Moves.SamusAsIs && body.Clip(Ds + PlayerAnimation.Idle) != null ? Ds + PlayerAnimation.Idle
                : body.Clip(idle) != null ? idle : Wait);
            Stand(body, Forward, human ? Ual + "Jog_Fwd_Loop" : w + PlayerAnimation.WalkForward);
            Stand(body, Back, w + PlayerAnimation.WalkBackward);
            Stand(body, Left, w + PlayerAnimation.WalkLeft);
            Stand(body, Right, w + PlayerAnimation.WalkRight);
            if (body.Clip(p + PlayerAnimation.Turn) != null) m.Set(PlayerAnimation.Turn, p + PlayerAnimation.Turn, true);
            Legs(m, body, PlayerAnimation.Flourish, BrawlZeroSuitRig.Upright && body.Clip(FlourishUp) != null ? FlourishUp : Flourish, loop: false, fit: true);
            foreach (PlayerAnimation a in new[] { PlayerAnimation.Morph, PlayerAnimation.Unmorph }) Legs(m, body, a, p + a, loop: false, fit: true);
            foreach (PlayerAnimation a in Jumps) Legs(m, body, a, human ? Ual + "Jump_Loop" : p + a, loop: human, fit: !human);
            foreach (PlayerAnimation a in Lands) Legs(m, body, a, human ? Ual + "Jump_Land" : p + a, loop: false, fit: true);
            foreach (PlayerAnimation a in Hits)
            {
                string clip = human ? Ual + (a == PlayerAnimation.DamageFront ? "Hit_Head" : "Hit_Chest") : p + a;
                Legs(m, body, a, clip, loop: false, fit: true);
                if (body.Clip(clip) != null) m.SetTorso(a, clip, false);
            }
        }

        static void Stand(NativeBody body, string stand, string clip)
        {
            if (body.Clip(clip) is not HdGun.Anim src || body.Clip(stand) is not HdGun.Anim to) return;
            (to.Keys, to.Tick, to.Duration, to.Frames) = (src.Keys, src.Tick, src.Duration, src.Frames);
            if (body.Speeds.TryGetValue(clip, out float v)) body.Speeds[stand] = v;
            else body.Speeds.Remove(stand);
        }

        static void Legs(BodyClips m, NativeBody body, PlayerAnimation a, string clip, bool loop, bool fit)
        {
            if (body.Clip(clip) == null) return;
            m.Set(a, clip, loop);
            if (fit) m.Fit.Add(a);
            else m.Fit.Remove(a);
        }
    }

    // Vanilla Samus's DS clips put on another skeleton (a native body's), baked into its clip list as "ds:<state>". Each
    // mapped bone takes its DS bone's turn in model space since Samus's rest (her Idle clip's first frame), trued by the
    // fixed turn laying its own bind direction (bone -> its aim child) onto the DS bone's rest one -- so her limbs point the
    // way Samus's point (BodyClipRetarget's method, for a bind that carries rotations):
    //   R_dst(t) = R_dst_bind * A * R_src_rest^-1 * R_src(t)    (row vectors, model space; A: dst bind dir -> src rest dir)
    // The spine, pelvis and head aren't trued (A = identity): they keep her own bind posture and take Samus's turns from
    // her rest. A bone with no aim child takes its parent's A; an unmapped bone keeps its bind turn under its parent. Only
    // the pelvis moves, by Samus's pelvis travel from her rest scaled by the legs' length ratio. The DS model faces -Z with
    // its left at -X: turned 180 degrees about Y onto the body's +Z-forward space.
    // A Spec names the hunter whose clips are put on, the bone map, the bones that move (the pelvis, and a spine root that
    // isn't under it: Prime 4 Sylux's upper body hangs off the skeleton's root beside his hips), the leg measured for the
    // travel, the clips' name prefix, and optionally a clip whose first frame's feet the moves stand at (FloorClip: the
    // rest's feet put where the body's own idle has them, so a straight-legged bind doesn't float or sink the moves)
    public static class DsRetarget
    {
        public sealed record Spec(Hunter Hunter, (string Dst, string Src, string? DstChild, string? SrcChild)[] Map,
            (string Dst, string Src)[] Movers, (string Hip, string Knee, string Foot) DstLeg, string Prefix,
            string? FloorClip = null, string[]? FloorBones = null);

        // (dst bone, DS bone, dst aim child, DS aim child): aim children null = take the parent's correction, "" = not
        // trued; "*fwd" = the DS bone's local axis nearest Samus's forward at rest (her arm cannon has no child bone)
        static readonly (string Dst, string Src, string? DstChild, string? SrcChild)[] Map =
        {
            ("HipN", "Pelvis", "", ""), ("BodyN", "Spine_1", "", ""), ("BustN", "Spine_2", "", ""), ("HeadN", "Head_1", "", ""),
            ("LShoulderJ", "L_shoulder", "LArmJ", "L_elbow"), ("LArmJ", "L_elbow", "LHandN", "L_wrist"), ("LHandN", "L_wrist", null, null),
            ("RShoulderJ", "R_shoulder", "RArmJ", "R_elbow"), ("RArmJ", "R_elbow", "RHandN", "*fwd"),
            ("LLegJ", "L_hip", "LKneeJ", "L_knee"), ("LKneeJ", "L_knee", "LFootJ", "L_ankle"), ("LFootJ", "L_ankle", null, null),
            ("RLegJ", "R_hip", "RKneeJ", "R_knee"), ("RKneeJ", "R_knee", "RFootJ", "R_ankle"), ("RFootJ", "R_ankle", null, null),
        };
        // Brawl Zero Suit Samus: vanilla Samus's clips
        public static readonly Spec ZeroSuit = new(Hunter.Samus, Map, new[] { ("HipN", "Pelvis") }, ("LLegJ", "LKneeJ", "LFootJ"), BrawlZeroSuitBody.Ds);
        const float DsFps = 30f;
        static readonly Matrix4 Turn = Matrix4.CreateRotationY(MathF.PI);

        // hold: a clip of the body's own whose first frame the unmapped bones at and under the named bones take instead of
        // their bind (Brawl Zero Suit's fingers gripping her gun, from her idle)
        public static void AddClips(NativeBody body, IReadOnlyList<PlayerAnimation> clips, Func<PlayerAnimation, bool> loops,
            (string Clip, string[] Under)? hold = null) => AddClips(body, ZeroSuit, clips, loops, hold);

        public static void AddClips(NativeBody body, Spec spec, IReadOnlyList<PlayerAnimation> clips, Func<PlayerAnimation, bool> loops,
            (string Clip, string[] Under)? hold = null)
        {
            Model m = Read.GetModelInstance(Metadata.HunterModels[spec.Hunter][0], noCache: true).Model; // a worker thread: its own copy
            var skel = new DsSkeleton(m);
            var pose = new SkeletonPose(skel.Count);
            var ds = new Matrix4[skel.Count];
            HdGun g = body.Data;
            int n = g.BoneCount;

            // the body's bind in model space
            var bind = new Matrix4[n];
            for (int i = 0; i < n; i++) bind[i] = g.InvBind[i].Inverted();
            var bindLocal = new Matrix4[n];
            for (int i = 0; i < n; i++) bindLocal[i] = g.Parent[i] >= 0 ? bind[i] * bind[g.Parent[i]].Inverted() : bind[i];

            // Samus's rest
            DsSkeleton.Sample(m, (int)PlayerAnimation.Idle, 0, pose);
            skel.Fk(pose, ds);
            Matrix4[] rest = Convert(ds);

            var src = new int[n];
            var corr = new Matrix4[n];
            var mapped = new bool[n];
            Array.Fill(src, -1);
            for (int i = 0; i < n; i++) corr[i] = Matrix4.Identity;
            foreach (int i in g.Order)
            {
                if (g.Parent[i] >= 0) corr[i] = corr[g.Parent[i]];
                int k = Array.FindIndex(spec.Map, e => e.Dst == g.Names[i]);
                if (k < 0) continue;
                (string _, string s, string? dc, string? sc) = spec.Map[k];
                int si = skel.IndexOf(s);
                if (si < 0) continue;
                src[i] = si;
                mapped[i] = true;
                if (dc == "") corr[i] = Matrix4.Identity;
                else if (dc != null)
                {
                    int di = g.BoneIndex(dc);
                    if (di < 0) continue;
                    Vector3 from = bind[di].ExtractTranslation() - bind[i].ExtractTranslation();
                    Vector3 to;
                    if (sc == "*fwd") to = NearestAxis(rest[si], Vector3.UnitZ);
                    else
                    {
                        int sci = skel.IndexOf(sc!);
                        if (sci < 0) continue;
                        to = rest[sci].ExtractTranslation() - rest[si].ExtractTranslation();
                    }
                    corr[i] = Onto(from, to);
                }
            }
            if (hold is (string hc, string[] under) && body.Clip(hc) is HdGun.Anim held)
            {
                var keep = new bool[n];
                foreach (int i in g.Order)
                {
                    keep[i] = Array.IndexOf(under, g.Names[i]) >= 0 || g.Parent[i] >= 0 && keep[g.Parent[i]];
                    if (!keep[i] || mapped[i]) continue;
                    g.Sample(held, 0, i, out Quaternion q, out Vector3 t);
                    bindLocal[i] = Matrix4.CreateFromQuaternion(q) * Matrix4.CreateTranslation(t);
                }
            }
            // the moving bones: each takes its DS bone's travel from the rest, scaled by the legs' length ratio
            var mover = new int[n];
            Array.Fill(mover, -1);
            foreach ((string md, string ms) in spec.Movers)
            {
                int di = g.BoneIndex(md), si = skel.IndexOf(ms);
                if (di >= 0 && si >= 0) mover[di] = si;
            }
            float legRatio = Leg(bind, g, spec.DstLeg.Hip, spec.DstLeg.Knee, spec.DstLeg.Foot) / Math.Max(1e-5f, Leg(rest, skel, "L_hip", "L_knee", "L_ankle"));

            var restInv = new Matrix4[skel.Count];
            for (int s = 0; s < skel.Count; s++) restInv[s] = Rot(rest[s]).Inverted();
            var world = new Matrix4[n];
            Vector3 drop = Vector3.Zero;
            // one key: the body's model-space pose for the DS pose cur
            void PoseKey(Matrix4[] cur)
            {
                foreach (int i in g.Order)
                {
                    Matrix4 parent = g.Parent[i] >= 0 ? world[g.Parent[i]] : Matrix4.Identity;
                    Matrix4 r = mapped[i]
                        ? Rot(bind[i]) * corr[i] * restInv[src[i]] * Rot(cur[src[i]])
                        : Rot(bindLocal[i]) * Rot(parent);
                    Vector3 at = mover[i] >= 0
                        ? bind[i].ExtractTranslation() + (cur[mover[i]].ExtractTranslation() - rest[mover[i]].ExtractTranslation()) * legRatio + drop
                        : Vector3.TransformPosition(bindLocal[i].ExtractTranslation(), parent);
                    world[i] = r * Matrix4.CreateTranslation(at);
                }
            }
            // FloorClip: the rest's feet where that clip's first frame has them (height only)
            if (spec.FloorClip != null && spec.FloorBones is string[] feet && body.Clip(spec.FloorClip) is HdGun.Anim floor)
            {
                int[] fb = Array.ConvertAll(feet, g.BoneIndex);
                if (Array.TrueForAll(fb, b => b >= 0))
                {
                    PoseKey(rest);
                    float have = fb.Min(b => world[b].M42);
                    body.Pose(floor, 0);
                    float want = fb.Min(b => body.World[b].M42);
                    drop = new Vector3(0, want - have, 0);
                }
            }
            foreach (PlayerAnimation a in clips)
            {
                string name = spec.Prefix + a;
                if (g.Anims.ContainsKey(name) || (int)a < 0 || (int)a >= m.AnimationGroups.Node.Count) continue;
                int frames = m.AnimationGroups.Node[(int)a].FrameCount;
                if (frames <= 0) continue;
                bool loop = loops(a);
                int keys = loop ? frames + 1 : frames;
                var anim = new HdGun.Anim { Name = name, Keys = keys, Tick = 1 / DsFps, Duration = (keys - 1) / DsFps };
                anim.Frames = new float[keys * n * g.KeyStride];
                for (int key = 0; key < keys; key++)
                {
                    DsSkeleton.Sample(m, (int)a, key % frames, pose);
                    skel.Fk(pose, ds);
                    Matrix4[] cur = Convert(ds);
                    PoseKey(cur);
                    foreach (int i in g.Order)
                    {
                        Matrix4 parent = g.Parent[i] >= 0 ? world[g.Parent[i]] : Matrix4.Identity;
                        Matrix4 local = g.Parent[i] >= 0 ? world[i] * parent.Inverted() : world[i];
                        Quaternion q = local.ExtractRotation();
                        Vector3 t = local.ExtractTranslation();
                        int o = (key * n + i) * g.KeyStride;
                        anim.Frames[o] = q.X; anim.Frames[o + 1] = q.Y; anim.Frames[o + 2] = q.Z; anim.Frames[o + 3] = q.W;
                        anim.Frames[o + 4] = t.X; anim.Frames[o + 5] = t.Y; anim.Frames[o + 6] = t.Z;
                        if (g.KeyStride >= 10) { anim.Frames[o + 7] = 1; anim.Frames[o + 8] = 1; anim.Frames[o + 9] = 1; }
                    }
                }
                g.Anims[name] = anim;
            }
        }

        // DS model space -> the body's (turned 180 degrees about Y), scale left out of the rotations
        static Matrix4[] Convert(Matrix4[] ds)
        {
            var o = new Matrix4[ds.Length];
            for (int i = 0; i < ds.Length; i++) o[i] = Turn * ds[i] * Turn;
            return o;
        }

        static Matrix4 Rot(Matrix4 m) => Matrix4.CreateFromQuaternion(m.ExtractRotation());

        // the turn taking direction `from` onto `to` (row vectors: from * result points along to)
        internal static Matrix4 Onto(Vector3 from, Vector3 to)
        {
            if (from.LengthSquared < 1e-12f || to.LengthSquared < 1e-12f) return Matrix4.Identity;
            from.Normalize();
            to.Normalize();
            Vector3 axis = Vector3.Cross(from, to);
            float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
            if (axis.LengthSquared < 1e-12f)
            {
                if (dot > 0) return Matrix4.Identity;
                axis = Math.Abs(from.X) < 0.9f ? Vector3.Cross(from, Vector3.UnitX) : Vector3.Cross(from, Vector3.UnitY);
            }
            return Matrix4.CreateFromAxisAngle(axis.Normalized(), MathF.Acos(dot));
        }

        // the bone's local axis (+-X/Y/Z, in model space) nearest `want`
        static Vector3 NearestAxis(Matrix4 world, Vector3 want)
        {
            Matrix4 r = Rot(world);
            Vector3 best = Vector3.UnitZ;
            float bestDot = float.MinValue;
            foreach (Vector3 ax in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ })
            {
                Vector3 v = Vector3.TransformNormal(ax, r);
                float d = Vector3.Dot(v, want);
                if (d > bestDot) { bestDot = d; best = v; }
            }
            return best;
        }

        static float Leg(Matrix4[] w, HdGun g, string a, string b, string c)
        {
            int ia = g.BoneIndex(a), ib = g.BoneIndex(b), ic = g.BoneIndex(c);
            if (ia < 0 || ib < 0 || ic < 0) return 1;
            return (w[ib].ExtractTranslation() - w[ia].ExtractTranslation()).Length + (w[ic].ExtractTranslation() - w[ib].ExtractTranslation()).Length;
        }

        static float Leg(Matrix4[] w, DsSkeleton s, string a, string b, string c)
        {
            int ia = s.IndexOf(a), ib = s.IndexOf(b), ic = s.IndexOf(c);
            if (ia < 0 || ib < 0 || ic < 0) return 1;
            return (w[ib].ExtractTranslation() - w[ia].ExtractTranslation()).Length + (w[ic].ExtractTranslation() - w[ib].ExtractTranslation()).Length;
        }
    }
}
