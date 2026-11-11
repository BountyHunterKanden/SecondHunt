using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the DEATH look option's RAGDOLL (owner 2026-11-07, docs/IDEAS_SURVEY.md D1 step 2). Our own small solver on
    // the DS skeleton: 16 points (the hunter's joints and a head top) joined by sticks, with range sticks for the spine,
    // a one-way knee hinge, the room's collision and gravity. It starts from the pose at death, moving as the body was,
    // pushed by the killing shot, and lies still until the respawn. Purely cosmetic: it runs on each device from what the
    // device already knows (online, the killer and weapon in the seat snapshot), at the 60 Hz step with no RNG, so a
    // replay and its seeks show the same fall. It runs (HostRagdoll) for DEATH's RAGDOLL alone, independent of First Hunt
    // (owner 2026-11-09); HostRagdollShown (the host's DEATH setting, draw only) picks whether it's drawn, over MPH's
    // death (switched to VANILLA mid-match). Off by default
    public partial class PlayerEntity
    {
        // host hook: make a ragdoll at each biped death (the match's setting: walked the same by a replay's prepare)
        public static bool HostRagdoll { get; set; }

        // host hook: draw the ragdoll instead of the collapse clip (draw only: excluded from the replay walk)
        public static bool HostRagdollShown { get; set; }

        private PlayerRagdoll? _ragdoll;
        // the host body's pose at death (a draw-only body: the replay prep has none, so excluded from the replay walk)
        private PlayerRagdoll.HostPose? _ragdollHost;

        public PlayerRagdoll? Ragdoll => _ragdoll;

        // the host draws this dead seat's body itself on the ragdoll's pose (an HD suit on this hunter's DS rig: the App's
        // CampaignSuit), so MphRead keeps it "drawn" (HostBipedAlpha, no death particles) though it adds no draw items.
        // Set with HostOwnBiped (the main player's HostOwnMainBiped body always can). Default off: the seat dies as MPH's
        public bool HostOwnRagdoll { get; set; }

        // the ragdoll this dead body is drawn on, for a host drawing the body itself (null: none, or not shown)
        public PlayerRagdoll? HostRagdollPose => RagdollDrawn ? _ragdoll : null;

        // the ragdoll's launch at this seat's last biped death (speed, push): the host sends it while the seat is dead
        // (the snapshot's throws block, Snapshot.cs), so a client's body falls exactly as the host's (owner 2026-11-09,
        // "I want exact throw online"). Worked out at every biped death, whatever this device's DEATH setting (the host
        // may not show ragdolls; its guests might). Online send only: excluded from the replay walk
        public bool NetThrowSet { get; private set; }
        public Vector3 NetThrowSpeed { get; private set; }
        public Vector3 NetThrowPush { get; private set; }
        // a client applying the host's death: the host's launch (null: an older host sent none; worked out here)
        private static (Vector3 Speed, Vector3 Push)? _netDamageThrow;
        // the snapshot's steps for a launch: 1/2048, int16 (+-16)
        public const float NetThrowScale = 2048;

        public static Vector3 NetThrowStep(Vector3 v)
        {
            for (int i = 0; i < 3; i++)
            {
                v[i] = Math.Clamp(MathF.Round(v[i] * NetThrowScale), short.MinValue, short.MaxValue) / NetThrowScale;
            }
            return v;
        }

        // at death, before the speed is cleared and the collapse clip is set: the pose the body has now
        private void RagdollDeath(uint damage, BeamType beamType, PlayerEntity? attacker, BeamProjectileEntity? beam,
            BombEntity? bomb, Vector3? direction)
        {
            _ragdoll = null;
            _ragdollHost = null;
            NetThrowSet = false;
            if (HostFirstHunt || IsAltForm || IsMorphing)
            {
                return;
            }
            var poses = new Matrix4[2][];
            Matrix4 placement = Matrix4.Identity;
            for (int i = 0; i < 2; i++)
            {
                Node? spine = _spineNodes[i];
                if (spine == null)
                {
                    return;
                }
                poses[i] = RagdollPoseNow(_bipedModelLods[i].Model, spine, out placement);
            }
            PlayerRagdoll? doll = PlayerRagdoll.Create(_bipedModelLods[0].Model, poses[0], _bipedModelLods[1].Model, poses[1],
                _facingVector);
            if (doll == null)
            {
                return;
            }
            Vector3 chest = doll.Pos[PlayerRagdoll.Chest];
            Vector3 push = Vector3.Zero;
            if (beam != null && beam.Velocity.LengthSquared > 1e-8f)
            {
                push = beam.Velocity.Normalized();
            }
            else if (bomb != null)
            {
                Vector3 away = chest - bomb.Position;
                push = away.LengthSquared > 1e-8f ? away.Normalized() : Vector3.Zero;
                push.Y += 0.5f;
            }
            else if (direction.HasValue && direction.Value.LengthSquared > 1e-8f)
            {
                push = direction.Value.Normalized();
            }
            else if (attacker != null && attacker != this)
            {
                Vector3 away = Position - attacker.Position;
                push = away.LengthSquared > 1e-8f ? away.Normalized() : Vector3.Zero;
            }
            float strength = Math.Clamp(0.04f + damage * 0.0008f, 0.04f, 0.12f);
            switch (beamType)
            {
            case BeamType.Imperialist:
                strength *= 1.3f;
                break;
            case BeamType.OmegaCannon:
                push = PlayerRagdoll.BigThrow(push);
                strength = 1;
                break;
            case BeamType.ShockCoil:
                strength *= 0.6f;
                break;
            case BeamType.Judicator:
                strength *= 0.8f;
                break;
            case BeamType.Missile:
            case BeamType.Magmaul:
                // a blast: lifted and thrown back (owner 2026-11-10: a missile kill "just seem[s] to make them collapse
                // not lift or blast them away"; x1.2 the damage's throw was a slump). The Magmaul's burst the same (owner
                // 2026-11-10: "maybe we should just make it like the missile/bombs", in place of its writhe)
                strength = PlayerRagdoll.MissileThrow;
                push.Y += 0.6f;
                break;
            }
            if (_netApplyingDamage ? _netDamageTurret : TurretHit(beam)) // (an online client: the seat snapshot's turret bit)
            {
                // the Shooting Gallery turret (a Power Beam shot underneath): thrown as hard as the Omega Cannon (owner
                // 2026-11-08: "a pretty big impact ... omega cannon rag doll ... level to start")
                push = PlayerRagdoll.BigThrow(push);
                strength = 1;
            }
            if (beam == null && bomb == null && attacker != null && attacker != this && attacker.IsAltForm
                && (attacker.Hunter == Hunter.Trace || attacker.Hunter == Hunter.Noxus || attacker.Hunter == Hunter.Spire))
            {
                push = PlayerRagdoll.AltAttackThrow(attacker.Hunter, attacker.Values, attacker.Speed, Position - attacker.Position);
                strength = 1;
            }
            // (on the snapshot's 1/2048 steps, so the host and its guests launch on the very same numbers)
            Vector3 launchSpeed = NetThrowStep(Speed / 2); // todo: FPS stuff (Speed is per DS frame)
            Vector3 launchPush = NetThrowStep(push * strength);
            if (_netApplyingDamage && _netDamageThrow is (Vector3, Vector3) sent)
            {
                (launchSpeed, launchPush) = sent; // the host's launch, exactly
            }
            NetThrowSpeed = launchSpeed;
            NetThrowPush = launchPush;
            NetThrowSet = true;
            if (!HostRagdoll)
            {
                return; // (worked out for the guests only)
            }
            doll.Launch(launchSpeed, launchPush);
            // (from the launch, the hunter and the body's facing alone, so a client's body varies as the host's)
            doll.Vary(Hunter, new Vector3(_field70, 0, _field74), launchSpeed, launchPush);
            // an electric death twitches before lying still (owner 2026-11-09). (An online client learns a Volt Driver
            // shot was charged from the seat snapshot's charged bit, PlayerNet.cs)
            bool charged = _netApplyingDamage ? _netDamageCharged : beam != null && beam.Flags.TestFlag(BeamFlags.Charged);
            if (beamType == BeamType.ShockCoil || beamType == BeamType.VoltDriver && charged)
            {
                // (on foot it stops in its pose and shakes first: owner 2026-11-10. On the ground's flags, which a
                // client's copy of the seat keeps too)
                doll.Twitch(onFoot: Flags1.TestFlag(PlayerFlags1.Standing) || Flags1.TestFlag(PlayerFlags1.StandingPrevious));
            }
            // a Judicator death freezes stiff, then topples (owner 2026-11-09, step 3 batch B). (A burn no longer writhes,
            // Writhe: owner 2026-11-10, "im not sure i like magmauls effect at all"; a burn's last tick falls as any kill.)
            // Only a charged shot of Noxus's own Judicator freezes (the affinity one, Weapons.cs), so only its kill
            // freezes: owner 2026-11-09, and 11-10 "no one should freeze if youre not hitting them as noxus". (The shot's
            // freeze on the host; a client has the host's attacker, PlayerNet.cs)
            else if (beamType == BeamType.Judicator && charged && (beam != null
                ? beam.Afflictions.TestFlag(Affliction.Freeze) : attacker?.AffinityBeam == BeamType.Judicator))
            {
                doll.Freeze();
            }
            // a headshot kill snaps the head back on the neck (owner 2026-11-09, step 3 batch C). (An online client gets
            // the Headshot flag from the seat snapshot's flags, PlayerNet.cs; a frozen body doesn't snap)
            if (NetLastFlags.TestFlag(DamageFlags.Headshot) && !doll.Frozen)
            {
                doll.HeadSnap(launchPush);
            }
            doll.Placement = placement;
            _ragdoll = doll;
            _ragdollIceShown = false;
            if (_hostBody != null && _hostSpineNode != null)
            {
                Model host = _hostBody.Model;
                _ragdollHost = new PlayerRagdoll.HostPose(doll, host, RagdollPoseNow(host, _hostSpineNode, out _));
            }
        }

        // the biped's world pose as the draw makes it (PlayerDraw: legs on layer 1, the upper body on layer 2 turned by
        // the aim's pitch, then the seat's transform), without the host's float/cling/grapple poses
        private Matrix4[] RagdollPoseNow(Model model, Node spineNode, out Matrix4 transform)
        {
            spineNode.AnimIgnoreChild = true;
            Vector3 facing = _facingVector;
            float limit = Fixed.ToFloat(2896);
            float cos = MathF.Sqrt(1 - facing.Y * facing.Y);
            float sin = facing.Y;
            if (MathF.Abs(facing.Y) > limit)
            {
                cos = limit;
                sin = facing.Y <= 0 ? -limit : limit;
            }
            float angle = MathF.Atan2(sin, cos);
            spineNode.AfterTransform = DetMatrix.CreateRotationZ(angle);
            model.AnimateNodes(index: 0, false, Matrix4.Identity, Vector3.One, _bipedModel1.AnimInfo);
            spineNode.AnimIgnoreChild = false;
            model.AnimateNodes(spineNode.ChildIndex, false, Matrix4.Identity, Vector3.One, _bipedModel2.AnimInfo);
            spineNode.AfterTransform = null;
            float scale = Metadata.HunterScales[Hunter];
            float bottom = Fixed.ToFloat(Values.MinPickupHeight);
            var lateral = new Vector3(_field70, 0, _field74);
            transform = Matrix4.Identity;
            transform.Row0.Xyz = -_gunVec2;
            transform.Row1.Xyz = Vector3.Cross(lateral, _gunVec2);
            transform.Row2.Xyz = -lateral;
            transform.Row3.Xyz = Position;
            transform.Row3.Y += bottom + bottom * (1 - scale);
            transform.Row0.Xyz *= scale;
            transform.Row1.Xyz *= scale;
            transform.Row2.Xyz *= scale;
            var pose = new Matrix4[model.Nodes.Count];
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                pose[i] = model.Nodes[i].Animation * transform;
            }
            return pose;
        }

        // each 60 Hz step while dead (ProcessPlayer)
        private void RagdollStep()
        {
            if (_ragdoll == null)
            {
                return;
            }
            if (_health > 0)
            {
                _ragdoll = null;
                _ragdollHost = null;
                return;
            }
            _ragdoll.Step(_scene);
            // (a frozen body's ice breaks as it lands: vanilla's thaw, its sound and shards, once; only a body that showed
            // the ice, and as cosmetic as the thaw's: Rng1 and sound, nothing a peer or a replay keeps)
            if (_ragdoll.IceBrokeNow && _ragdollIceShown)
            {
                _ragdollIceShown = false;
                _soundSource.PlaySfx(SfxId.SHOTGUN_BREAK_FREEZE);
                CreateIceBreakEffectBiped(_bipedIceModel.Model);
            }
        }

        // the ragdoll is drawn: shown, and it has a pose for the body the draw uses (a host body whose bones it couldn't
        // map shows the collapse clip, or with no clip dies as MPH's)
        private bool RagdollDrawn => HostRagdollShown && _ragdoll != null
            && (_hostBody == null || _ragdollHost != null && _ragdollHost.For == _ragdoll && _ragdollHost.Model == _hostBody.Model);

        // the draw: after the seat's transform, the ragdoll's pose replaces the collapse clip's (or the pose at death)
        private void RagdollPoseWorld(Model model, int lod)
        {
            if (!RagdollDrawn || !DrawnWhileDead)
            {
                return;
            }
            if (model == _ragdoll.Models[lod])
            {
                _ragdoll.Pose(model, _ragdoll.Rest[lod], _ragdoll.Drivers[lod]);
                if (_ragdoll.Iced)
                {
                    RagdollIce(model);
                }
            }
            else if (_ragdollHost != null && _ragdollHost.For == _ragdoll && model == _ragdollHost.Model)
            {
                _ragdoll.Pose(model, _ragdollHost.Rest, _ragdollHost.Drivers);
                if (_ragdoll.Iced)
                {
                    RagdollIce(model); // (a host body: its fitted ice, PlayerFittedIce.cs)
                }
            }
            // (else a body it has no pose for: the collapse clip shows)
        }

        // a frozen body's ice (a charged Judicator kill, until it breaks on landing): vanilla's frozen shell (PlayerDraw's
        // _frozenGfxTimer draw) on the ragdoll's pose, or the body's own fitted ice (PlayerFittedIce.cs: a host body always)
        private bool _ragdollIceShown;

        private void RagdollIce(Model model)
        {
            DrawBodyIce(model);
            _ragdollIceShown = true;
        }
    }

    public class PlayerRagdoll
    {
        public const int Pelvis = 0;
        public const int Chest = 1;
        public const int Neck = 2;
        public const int LShoulder = 3;
        public const int LElbow = 4;
        public const int LWrist = 5;
        public const int RShoulder = 6;
        public const int RElbow = 7;
        public const int RWrist = 8;
        public const int LHip = 9;
        public const int LKnee = 10;
        public const int LAnkle = 11;
        public const int RHip = 12;
        public const int RKnee = 13;
        public const int RAnkle = 14;
        public const int HeadTop = 15;
        public const int PointCount = 16;

        // the joints the points sit on (HeadTop is made from the neck)
        private static readonly string[] _pointBones =
        {
            "Pelvis", "Spine_2", "Head_1", "L_shoulder", "L_elbow", "L_wrist", "R_shoulder", "R_elbow", "R_wrist",
            "L_hip", "L_knee", "L_ankle", "R_hip", "R_knee", "R_ankle"
        };

        // the pose drivers: two frames (pelvis, chest) and aimed bones, each turned from its parent's turn
        private const int DPelvis = 0;
        private const int DChest = 1;
        private const int DriverCount = 11;
        private static readonly string[] _driverBones =
        {
            "Pelvis", "Spine_2", "Head_1", "L_shoulder", "L_elbow", "R_shoulder", "R_elbow", "L_hip", "L_knee", "R_hip", "R_knee"
        };
        // aimed drivers: anchor point, aim point, parent driver (frames: -1)
        private static readonly int[] _driverFrom = { Pelvis, Chest, Neck, LShoulder, LElbow, RShoulder, RElbow, LHip, LKnee, RHip, RKnee };
        private static readonly int[] _driverTo = { -1, -1, HeadTop, LElbow, LWrist, RElbow, RWrist, LKnee, LAnkle, RKnee, RAnkle };
        private static readonly int[] _driverParent = { -1, -1, DChest, DChest, 3, DChest, 5, DPelvis, 7, DPelvis, 9 };

        // for a host posing its own body (CampaignSuit): the drivers, each one's bone, the point it turns about, and the
        // driver it hangs from (the pelvis: none; the chest hangs from the pelvis)
        public const int DriverTotal = DriverCount;
        public static int DriverOf(string name) => Array.IndexOf(_driverBones, name);
        public static string DriverBone(int d) => _driverBones[d];
        public static int DriverPoint(int d) => _driverFrom[d];
        public static int DriverUp(int d) => d == DChest ? DPelvis : _driverParent[d];

        // rigid sticks (bones and the torso's and hips' shapes)
        private static readonly int[] _rigid =
        {
            Chest, Neck, Chest, LShoulder, Chest, RShoulder, Neck, LShoulder, Neck, RShoulder, LShoulder, RShoulder,
            Pelvis, LHip, Pelvis, RHip, LHip, RHip,
            Pelvis, Chest,
            Neck, HeadTop,
            LShoulder, LElbow, LElbow, LWrist, RShoulder, RElbow, RElbow, RWrist,
            LHip, LKnee, LKnee, LAnkle, RHip, RKnee, RKnee, RAnkle
        };
        // range sticks (the spine's bend and twist, the head's nod): pairs and their limits times the length at death
        private static readonly int[] _range =
        {
            LHip, LShoulder, RHip, RShoulder, Pelvis, Neck, LHip, RShoulder, RHip, LShoulder, Chest, HeadTop
        };
        private static readonly float[] _rangeMin = { 0.75f, 0.75f, 0.8f, 0.8f, 0.8f, 0.75f };
        private static readonly float[] _rangeMax = { 1.05f, 1.05f, 1.02f, 1.1f, 1.1f, 1.0f };
        // how much of the shot each point takes (the top more than the legs, so the body topples)
        private static readonly float[] _pushWeight =
        {
            0.7f, 1, 1, 1, 0.8f, 0.6f, 1, 0.8f, 0.6f, 0.5f, 0.3f, 0.2f, 0.5f, 0.3f, 0.2f, 1
        };

        private const int Iterations = 16;
        private const int FinalPasses = 32;
        private const float Gravity = 17f / 3600; // (MPH's biped gravity, per 60 Hz step squared)
        private const float Damping = 0.99f;
        private const float SettleDamping = 0.85f;
        private const int SettleTicks = 120;
        private const float Friction = 0.6f;
        private const float MaxStep = 0.5f;
        private const int MaxSubsteps = 4;
        private const float StaticSlip = 0.01f;
        private const float StillMove = 0.002f;
        private const int StillTicks = 30;
        private const int MaxTicks = 360;

        public sealed class HostPose
        {
            public readonly PlayerRagdoll For;
            public readonly Model Model;
            public readonly Matrix4[] Rest;
            public readonly int[] Drivers;

            public HostPose(PlayerRagdoll doll, Model model, Matrix4[] rest)
            {
                For = doll;
                Model = model;
                Rest = rest;
                Drivers = DriverMap(model);
            }
        }

        public readonly Vector3[] Pos = new Vector3[PointCount];
        public readonly Vector3[] Prev = new Vector3[PointCount];
        private readonly Vector3[] _start = new Vector3[PointCount];
        private readonly Vector3[] _contact = new Vector3[PointCount];
        // each point where the last collide left it, clear of the room: the next sweep starts there, so a point the
        // closing stick passes drag a little into a face is still caught and pushed back out
        private readonly Vector3[] _clear = new Vector3[PointCount];
        private readonly float[] _radius = new float[PointCount];
        private readonly float[] _rigidLength;
        private readonly float[] _rangeLength;
        private readonly float[] _armReach = new float[2];
        private readonly float[] _legReach = new float[2];
        private readonly float[] _chestKnee = new float[2];
        private float _hipWidth;
        // each knee's bend side in the pelvis frame
        private readonly Vector3[] _kneeSide = new Vector3[2];
        private readonly CollisionResult[] _results = new CollisionResult[8];
        public int Ticks { get; private set; }
        // the steps it has been let go, each counted by how far it's let go: a freeze, an electric death's shaking hold and
        // a headshot's tilt hold the body's shape, so a native body (CampaignRetroBody) keeps its own pose over them and only
        // then lays its limbs on the doll's (owner 2026-11-10: "the trooper for some reason stands up straight and unposes
        // for all the variety deaths"); a plain death's runs with Ticks
        public float LetGo { get; private set; }
        public int StillCount { get; private set; }
        // how far the body's furthest-moving point went in the last step (DEATH's FOLLOW CAM: near still hands over)
        public float Moved { get; private set; }
        public bool Asleep { get; private set; }
        // an electric death's twitch (Shock Coil, a charged Volt Driver shot): the step it ends (0: none), the kicks'
        // seed and size (owner 2026-11-09: "twitch a little before becoming still")
        private int _twitchFrom;
        private int _twitchEnd;
        private uint _twitchSeed;
        private float _twitchSize;
        // a burn's writhe (Magmaul): the step it ends (0: none), its seed and size
        private int _writheEnd;
        private uint _writheSeed;
        private float _writheSize;
        // a Judicator death's freeze: the step the hold ends, the step the stiff topple after it ends, and the pose held
        // (each point in the body's frame, from the middle of the points)
        private int _freezeEnd;
        private int _stiffEnd;
        private Vector3[]? _shape;
        private float _stiffNow;
        private float _stiffHold; // (how hard the shape is held at first: a freeze's, or a headshot's tilt)
        private readonly Vector3[] _kick = new Vector3[PointCount];
        // moving on its own (a twitch, a writhe, a freeze's hold and topple): not asleep or still yet (the FOLLOW CAM too)
        // a headshot's snap: the step the neck's extra give ends (0: none), and that give now
        private int _snapEnd;
        private float _nodNow;
        private float _snapNow;
        private Vector3 _snapHead; // (the bent-back head's way off the neck, in the chest's frame)
        private float _slow = 1; // (a headshot's tilt: how fast the body's time runs now, 1 = as it is)
        // an electric death on foot: the step the shaking hold ends (0: none), the pose held and the launch kept for after
        private int _shockEnd;
        private Vector3[]? _shockPose;
        private Vector3 _shockWent;
        public bool Twitching => Ticks < _shockEnd || Ticks < _twitchEnd || Ticks < _writheEnd || Ticks < _stiffEnd || Ticks < _snapEnd || Iced;
        public bool Frozen => Ticks < _freezeEnd;
        // the freeze's ice: on from the freeze until the toppled body lands (its chest, neck, head or a shoulder on a
        // floor), or after the topple it comes to rest; IceBrokeNow the one step it breaks
        public bool Iced => _freezeEnd > 0 && !IceBroken;
        public bool IceBroken { get; private set; }
        public bool IceBrokeNow { get; private set; }
        private bool _landed;
        // the hunter's looseness (Vary): how far the spine's and head's range sticks give, times their limits' give
        private float _loose = 1;

        // the pose at death: the points, both LODs' node matrices and drivers, the drivers' frames and aims
        public readonly Model[] Models;
        public readonly Matrix4[][] Rest;
        public readonly int[][] Drivers;
        public readonly Vector3[] Start = new Vector3[PointCount];
        // the seat's transform at death (PlayerDraw's: the rig's space -> the world), which Rest is in
        public Matrix4 Placement { get; internal set; } = Matrix4.Identity;
        private readonly Matrix3 _pelvis0;
        private readonly Matrix3 _chest0;

        private PlayerRagdoll(Model[] models, Matrix4[][] rest, int[][] drivers, float[] rigidLength, float[] rangeLength,
            Matrix3 pelvis0, Matrix3 chest0)
        {
            Models = models;
            Rest = rest;
            Drivers = drivers;
            _rigidLength = rigidLength;
            _rangeLength = rangeLength;
            _pelvis0 = pelvis0;
            _chest0 = chest0;
        }

        // a copy to step on its own (the Tools check)
        public PlayerRagdoll Clone()
        {
            var copy = new PlayerRagdoll(Models, Rest, Drivers, _rigidLength, _rangeLength, _pelvis0, _chest0);
            Array.Copy(Pos, copy.Pos, PointCount);
            Array.Copy(Prev, copy.Prev, PointCount);
            Array.Copy(Start, copy.Start, PointCount);
            Array.Copy(_clear, copy._clear, PointCount);
            Array.Copy(_radius, copy._radius, PointCount);
            Array.Copy(_armReach, copy._armReach, 2);
            Array.Copy(_legReach, copy._legReach, 2);
            Array.Copy(_chestKnee, copy._chestKnee, 2);
            Array.Copy(_kneeSide, copy._kneeSide, 2);
            copy._hipWidth = _hipWidth;
            copy.Ticks = Ticks;
            copy.LetGo = LetGo;
            copy.StillCount = StillCount;
            copy.Asleep = Asleep;
            copy._twitchFrom = _twitchFrom;
            copy._twitchEnd = _twitchEnd;
            copy._twitchSeed = _twitchSeed;
            copy._twitchSize = _twitchSize;
            copy._writheEnd = _writheEnd;
            copy._writheSeed = _writheSeed;
            copy._writheSize = _writheSize;
            copy._freezeEnd = _freezeEnd;
            copy._stiffEnd = _stiffEnd;
            copy._shape = _shape; // (never changed once set)
            copy._stiffNow = _stiffNow;
            copy._stiffHold = _stiffHold;
            copy.IceBroken = IceBroken;
            copy.IceBrokeNow = IceBrokeNow;
            copy._landed = _landed;
            copy._snapEnd = _snapEnd;
            copy._nodNow = _nodNow;
            copy._snapNow = _snapNow;
            copy._snapHead = _snapHead;
            copy._shockEnd = _shockEnd;
            copy._shockPose = _shockPose; // (never changed once set)
            copy._shockWent = _shockWent;
            copy._slow = _slow;
            copy._loose = _loose;
            copy.Placement = Placement;
            return copy;
        }

        // the worst rigid stick's length off its length at death, as a share of it (the Tools check)
        public float MaxStickError()
        {
            float worst = 0;
            for (int i = 0; i < _rigidLength.Length; i++)
            {
                float rest = _rigidLength[i];
                if (rest > 1e-4f)
                {
                    float length = (Pos[_rigid[i * 2 + 1]] - Pos[_rigid[i * 2]]).Length;
                    worst = MathF.Max(worst, MathF.Abs(length - rest) / rest);
                }
            }
            return worst;
        }

        // null when the rig lacks a joint or its shape can't make frames (that death keeps the collapse clip)
        public static PlayerRagdoll? Create(Model lod0, Matrix4[] pose0, Model lod1, Matrix4[] pose1, Vector3 facing)
        {
            // the rigs differ: some have no Pelvis bone (the hips' middle stands in) and a cannon arm has no wrist (one is
            // made down the forearm)
            var points = new Vector3[PointCount];
            var index = new int[_pointBones.Length];
            for (int i = 0; i < _pointBones.Length; i++)
            {
                index[i] = lod0.GetNodeIndexByName(_pointBones[i]);
                bool optional = i is Pelvis or LWrist or RWrist;
                if ((index[i] < 0) != (lod1.GetNodeIndexByName(_pointBones[i]) < 0) || (index[i] < 0 && !optional))
                {
                    return null;
                }
                if (index[i] >= 0)
                {
                    points[i] = pose0[index[i]].Row3.Xyz;
                }
            }
            if (index[Pelvis] < 0)
            {
                points[Pelvis] = (points[LHip] + points[RHip]) * 0.5f;
            }
            {
                // the hip triangle needs depth: a pelvis on (or near) the hip line bends freely and the sticks can't hold it
                Vector3 hips = points[RHip] - points[LHip];
                float width = hips.Length;
                if (width > 1e-4f)
                {
                    hips /= width;
                    Vector3 mid = (points[LHip] + points[RHip]) * 0.5f;
                    Vector3 off = points[Pelvis] - mid;
                    off -= hips * Vector3.Dot(off, hips);
                    Vector3 toChest = points[Chest] - mid;
                    toChest -= hips * Vector3.Dot(toChest, hips);
                    float want = 0.3f * width;
                    if (off.Length < want && toChest.LengthSquared > 1e-8f)
                    {
                        points[Pelvis] = mid + toChest.Normalized() * want;
                    }
                }
            }
            for (int side = 0; side < 2; side++)
            {
                int sh = side == 0 ? LShoulder : RShoulder;
                int other = side == 0 ? RShoulder : LShoulder;
                Vector3 guess = Forearm(pose0[index[sh]], pose0[index[sh + 1]], points[sh + 1] - points[sh]);
                if (index[sh + 2] < 0)
                {
                    float upper = (points[sh + 1] - points[sh]).Length;
                    float fore = index[other + 2] >= 0 ? (points[other + 2] - points[other + 1]).Length : 0.9f * upper;
                    points[sh + 2] = points[sh + 1] + guess * fore;
                }
            }
            for (int i = 0; i < DriverCount; i++)
            {
                // (nodes over every driver follow the pelvis frame, so the Pelvis bone itself isn't needed)
                if (i != DPelvis && (lod0.GetNodeIndexByName(_driverBones[i]) < 0 || lod1.GetNodeIndexByName(_driverBones[i]) < 0))
                {
                    return null;
                }
            }
            Vector3 up = points[Neck] - points[Chest];
            float neckLength = up.Length;
            up = neckLength > 1e-4f ? up / neckLength : Vector3.UnitY;
            points[HeadTop] = points[Neck] + up * MathF.Max(0.5f * neckLength, 0.15f);
            if (!TryFrame(points[RHip] - points[LHip], points[Chest] - points[Pelvis], out Matrix3 pelvis0)
                || !TryFrame(points[RShoulder] - points[LShoulder], points[Neck] - points[Chest], out Matrix3 chest0))
            {
                return null;
            }
            var rigidLength = new float[_rigid.Length / 2];
            for (int i = 0; i < rigidLength.Length; i++)
            {
                rigidLength[i] = (points[_rigid[i * 2 + 1]] - points[_rigid[i * 2]]).Length;
            }
            var rangeLength = new float[_range.Length / 2];
            for (int i = 0; i < rangeLength.Length; i++)
            {
                rangeLength[i] = (points[_range[i * 2 + 1]] - points[_range[i * 2]]).Length;
            }
            var doll = new PlayerRagdoll(new[] { lod0, lod1 }, new[] { pose0, pose1 }, new[] { DriverMap(lod0), DriverMap(lod1) },
                rigidLength, rangeLength, pelvis0, chest0);
            for (int side = 0; side < 2; side++)
            {
                int sh = side == 0 ? LShoulder : RShoulder;
                int hip = side == 0 ? LHip : RHip;
                doll._armReach[side] = (points[sh + 1] - points[sh]).Length + (points[sh + 2] - points[sh + 1]).Length;
                doll._legReach[side] = (points[hip + 1] - points[hip]).Length + (points[hip + 2] - points[hip + 1]).Length;
                doll._chestKnee[side] = 0.55f * ((points[Chest] - points[Pelvis]).Length + (points[hip + 1] - points[hip]).Length);
                // the bend side: where the knee sits off the hip-ankle line now, else the facing
                Vector3 hipPos = points[hip];
                Vector3 line = points[hip + 2] - hipPos;
                float lineSqr = line.LengthSquared;
                Vector3 off = points[hip + 1] - hipPos;
                if (lineSqr > 1e-8f)
                {
                    off -= line * (Vector3.Dot(off, line) / lineSqr);
                }
                Vector3 bend = off.Length > 0.02f * doll._legReach[side] ? off.Normalized() : new Vector3(facing.X, 0, facing.Z);
                if (bend.LengthSquared < 1e-8f)
                {
                    bend = Vector3.Zero;
                }
                else
                {
                    bend.Normalize();
                }
                doll._kneeSide[side] = ToLocal(bend, pelvis0);
            }
            doll._hipWidth = (points[RHip] - points[LHip]).Length;
            float minY = MathF.Min(points[LAnkle].Y, points[RAnkle].Y);
            float height = MathF.Max(points[HeadTop].Y - minY, 0.5f);
            for (int i = 0; i < PointCount; i++)
            {
                // (the torso's points sit close together, so they share one radius and their push-outs don't fight)
                bool torso = i is Pelvis or Chest or Neck or LHip or RHip or LShoulder or RShoulder;
                doll._radius[i] = (torso ? 0.09f : i == HeadTop ? 0.08f : 0.06f) * height;
                doll.Pos[i] = points[i];
                doll.Prev[i] = points[i];
                doll.Start[i] = points[i];
                doll._clear[i] = points[i];
            }
            return doll;
        }

        // the Omega Cannon's and the turret's throw, from the shot's way (per 60 Hz step, for Launch's push with strength
        // 1). Owner 2026-11-08: "at least twice as dramatic", then "about twice as strong as it is now still" (x8 the
        // strongest shot's, with a lift: doubling the push alone only went ~1.5x as far, the feet dragged on the floor);
        // then 2026-11-09, "after seeing how far triskelion sends ragdolls i think the turret and omega cannon ragdoll
        // needs to be ramped way up": x2.5 that, with Step sub-stepping up to MaxSubsteps (the body leaves at ~1.3 a
        // step, ~2x the 11-08 throw and ~4x the Triskelion's, -ragdoll's throw probe in the open)
        public const float BigThrowStrength = 0.12f * 8 * 2.5f;

        // a missile kill's throw (per 60 Hz step, for Launch's push with its lift): ~1/8 the Omega Cannon's, ~2.5x the
        // strongest beam's
        public const float MissileThrow = 0.3f;

        public static Vector3 BigThrow(Vector3 way)
        {
            return (way + new Vector3(0, 0.3f, 0)) * BigThrowStrength;
        }

        // the push for a kill by an alt form's attack: the Triskelion's lunge (owner 2026-11-09: "launch you pretty
        // strongly", "transfer triskelions attack momentum to the ragdoll", "vanillas knock back may be insignificant"),
        // then the Vhoscythe's and the Dialanche's (owner 2026-11-09: "apply the directional momentum and force of volsyths
        // attack and dialanche attack like we did triskelion"). The body takes the attacker's own speed at the hit, plus
        // the knockback MPH gives that hit (per DS frame):
        // - Triskelion (CheckAltAttackHit2): its horizontal speed times the knockback accel, for the knockback time (its
        //   small downward part only keeps a live body on the floor, so left out)
        // - Vhoscythe (CheckAltAttackHit1): 1/8 a frame out from the blade's middle, for 8 frames
        // - Dialanche (CheckAltAttackHit1): 1/4 out from it, with the rocks' hit
        // From the attacker's speed, values and place alone, so an online client (which gets the kill with no direction)
        // throws it the same. At least half the Omega Cannon's most (before its 2026-11-09 ramp) along it, so a slow hit
        // still throws hard. Per 60 Hz step, for Launch's push with strength 1
        public static Vector3 AltAttackThrow(Hunter hunter, in PlayerValues values, Vector3 speed, Vector3 away)
        {
            Vector3 outward = away.WithY(0);
            outward = outward.LengthSquared > 1e-8f ? outward.Normalized() : Vector3.Zero;
            Vector3 knock = hunter switch
            {
                Hunter.Trace => speed.WithY(0) * (Fixed.ToFloat(values.AltAttackKnockbackAccel) * values.AltAttackKnockbackTime),
                Hunter.Noxus => outward * (1 / 8f * 8),
                Hunter.Spire => outward * 0.25f,
                _ => Vector3.Zero
            };
            Vector3 thrown = speed + knock;
            Vector3 along = thrown.WithY(0);
            if (along.LengthSquared < 1e-8f)
            {
                along = outward;
            }
            float least = 0.12f * 4 * 2; // (the push is per 60 Hz step, speed per DS frame)
            if (along.LengthSquared > 1e-8f && along.Length < least)
            {
                thrown += along.Normalized() * (least - along.Length);
            }
            thrown.Y = MathF.Max(thrown.Y, 0) + 0.35f * least;
            // (as the shot's push: the top takes more than the legs, so the body topples over the way it was hit)
            return thrown / 2;
        }

        // the moving start: the body's own speed and the shot's push (per step)
        public void Launch(Vector3 speed, Vector3 push)
        {
            for (int i = 0; i < PointCount; i++)
            {
                Prev[i] = Pos[i] - (speed + push * _pushWeight[i]);
            }
        }

        // DEATH's variety (owner 2026-11-09, step 3 batch A), after Launch: each hunter's weight and looseness, the way the
        // shot came (in the face, the back, the side), a tumble when the body was in the air or a blast lifted it (a
        // missile's, a bomb's, the big throws). From the launch, the hunter and the body's facing alone (no RNG), so a
        // replay walks it the same and an online client, which launches on the host's numbers (the snapshot's throws
        // block), varies the same
        public void Vary(Hunter hunter, Vector3 facing, Vector3 speed, Vector3 push)
        {
            (float heavy, float loose) = Build(hunter);
            _loose = loose;
            var extra = new Vector3[PointCount];
            // (the weight: a light body takes more of the shot, a heavy one less)
            for (int i = 0; i < PointCount; i++)
            {
                extra[i] = push * (_pushWeight[i] * (heavy - 1));
            }
            Vector3 face = facing.WithY(0);
            face = face.LengthSquared > 1e-8f ? face.Normalized() : Vector3.Zero;
            Vector3 flat = push.WithY(0);
            float size = flat.Length;
            if (size > 1e-5f && face != Vector3.Zero)
            {
                Vector3 way = flat / size;
                // (a big throw flings the body anyway: the way it was hit only shapes an ordinary shot's fall)
                size = MathF.Min(size, HitMost) * heavy;
                float along = Vector3.Dot(way, face); // 1: from behind, -1: in the face
                float across = face.X * way.Z - face.Z * way.X; // the side it came from
                float front = MathF.Max(0, -along);
                float back = MathF.Max(0, along);
                // in the face: the head snaps back with the shot and the knees buckle the other way, so it falls back
                extra[HeadTop] += way * (size * 0.9f * front);
                extra[Neck] += way * (size * 0.4f * front);
                extra[LKnee] -= way * (size * 0.35f * front);
                extra[RKnee] -= way * (size * 0.35f * front);
                // in the back: folded forward at the waist, the arms flung on ahead
                extra[HeadTop] += way * (size * 0.5f * back);
                extra[LWrist] += way * (size * 0.9f * back);
                extra[RWrist] += way * (size * 0.9f * back);
                extra[LElbow] += way * (size * 0.5f * back);
                extra[RElbow] += way * (size * 0.5f * back);
                extra[Pelvis] -= way * (size * 0.2f * back);
                // from the side: the top half twists round the spine, the legs stay
                Vector3 spine = (Pos[Pelvis] + Pos[Chest]) * 0.5f;
                float twist = across * size * HitTwist;
                for (int i = Chest; i <= RWrist; i++)
                {
                    extra[i] += Vector3.Cross(Vector3.UnitY, (Pos[i] - spine).WithY(0)) * twist;
                }
                extra[HeadTop] += Vector3.Cross(Vector3.UnitY, (Pos[HeadTop] - spine).WithY(0)) * twist;
            }
            // the tumble: head over heels the way the body goes, when it was off the floor (its own speed up or down) or
            // a blast lifted it (the push's upward share)
            Vector3 moving = (speed + push).WithY(0);
            moving = moving.LengthSquared > 1e-8f ? moving.Normalized() : face;
            float air = Math.Clamp((MathF.Abs(speed.Y) - AirSpeed) / AirSpeed, 0, 1);
            float pushSize = push.Length;
            float lift = pushSize > 1e-5f ? Math.Clamp(push.Y / pushSize - 0.15f, 0, 1) * MathF.Min(pushSize, 0.3f) : 0;
            float turn = (air * AirTurn + lift * BlastTurn) / heavy;
            if (turn > 0 && moving != Vector3.Zero)
            {
                Vector3 axis = Vector3.Cross(Vector3.UnitY, moving) * turn;
                Vector3 middle = Vector3.Zero;
                for (int i = 0; i < PointCount; i++)
                {
                    middle += Pos[i];
                }
                middle /= PointCount;
                for (int i = 0; i < PointCount; i++)
                {
                    extra[i] += Vector3.Cross(axis, Pos[i] - middle);
                }
            }
            for (int i = 0; i < PointCount; i++)
            {
                Prev[i] -= extra[i];
            }
        }

        private const float HitMost = 0.15f; // (per step: a little over the strongest ordinary shot's)
        private const float HitTwist = 0.5f; // (a turn of the shoulders, not a spin: 2.5 spun a side-hit body round, owner 2026-11-10)
        private const float AirSpeed = 0.04f; // (per step: a body on the floor has gravity's step or so)
        private const float AirTurn = 0.05f; // (turns a step: ~3 a second)
        private const float BlastTurn = 1.5f;

        // each hunter's build: how much of the shot it takes, how loose its spine and neck are
        public static (float Throw, float Loose) Build(Hunter hunter) => hunter switch
        {
            Hunter.Kanden => (1.05f, 1.5f), // (wiry and floppy)
            Hunter.Trace => (1.2f, 1.15f), // (light)
            Hunter.Sylux => (1.0f, 1.1f),
            Hunter.Noxus => (0.95f, 0.85f),
            Hunter.Spire => (0.7f, 0.5f), // (rock: heavy and stiff)
            Hunter.Weavel => (1.1f, 1.3f),
            Hunter.Guardian => (1.1f, 1.2f),
            _ => (1, 1) // (Samus: the suit's own)
        };

        // twitch for a while before lying still (an electric death): sharp jerks of any limb at any moment, and the whole
        // body trembling under them, fading out (owner 2026-11-10: "a sporadic random jerk in any limb at any time and
        // maybe the whole body should shake a little too like a trembling like its being actively electricuted"). The
        // kicks come from the step count and a seed taken from the pose at death, with no RNG, so a replay and its seeks
        // show the same twitch. On foot (standing, walking, running) it first stops in the pose it died in and shakes
        // hard for ShockTicks, then collapses and the limbs twitch (owner 2026-11-10: "the body tremor for electric death
        // seems absent ... if theyre standing or walking or running they sort of stop in their current pose and shake
        // violoently for a second and a half before collpasing and linbs get twitchy")
        public void Twitch(bool onFoot = false)
        {
            _twitchSeed = Hash((uint)BitConverter.SingleToInt32Bits(Start[Pelvis].X)
                ^ (uint)BitConverter.SingleToInt32Bits(Start[Pelvis].Z) * 0x9E3779B9u);
            _twitchSize = (Start[HeadTop] - Start[Pelvis]).Length * TwitchKick;
            _twitchFrom = TwitchStart;
            if (onFoot)
            {
                _shockEnd = ShockTicks;
                _shockPose = (Vector3[])Pos.Clone();
                _shockWent = Vector3.Zero;
                for (int i = 0; i < PointCount; i++)
                {
                    _shockWent += (Pos[i] - Prev[i]) / PointCount;
                }
                _twitchFrom = ShockTicks;
            }
            _twitchEnd = _twitchFrom + TwitchTicks;
        }

        private const int ShockTicks = 90; // (1.5 s)
        private const float ShockBody = 0.012f; // (the whole body's shake, times the body's height)
        private const float ShockLimb = 0.12f; // (a limb's own shake about its root, radians)
        private const float ShockCarry = 0.3f; // (the share of the launch the collapse keeps)
        // the shaking limbs, each from a root that stays with the body to its end (the head on the neck alone)
        private static readonly int[][] _shockLimbs =
        {
            new[] { LShoulder, LElbow, LWrist }, new[] { RShoulder, RElbow, RWrist }, new[] { LHip, LKnee, LAnkle },
            new[] { RHip, RKnee, RAnkle }, new[] { Neck, HeadTop }
        };

        // (held in the pose it died in, every point shaken off it afresh each step: the whole body one way, each limb its
        // own, the sign flipping step to step so it buzzes in place. Feet shake less, being on the floor)
        private void ShockShake()
        {
            float height = (Start[HeadTop] - Start[Pelvis]).Length;
            uint key = _twitchSeed + (uint)Ticks * 0x9E3779B9u;
            float sign = (Ticks & 1) == 0 ? 1 : -1;
            var body = new Vector3(HashUnit(key), HashUnit(key + 1) * 0.5f, HashUnit(key + 2)) * (height * ShockBody * sign);
            for (int i = 0; i < PointCount; i++)
            {
                Pos[i] = _shockPose![i] + body;
            }
            for (int limb = 0; limb < _shockLimbs.Length; limb++)
            {
                uint lk = key + (uint)(limb + 1) * 0x85EBCA6Bu;
                var axis = new Vector3(HashUnit(lk), HashUnit(lk + 1), HashUnit(lk + 2));
                if (axis.LengthSquared < 1e-6f)
                {
                    continue;
                }
                // (turned about its root, the end a little further about the middle, so no stick stretches)
                float angle = ShockLimb * sign * (limb == 2 || limb == 3 ? 0.35f : 1);
                int[] chain = _shockLimbs[limb];
                Vector3 root = Pos[chain[0]], mid = Pos[chain[1]];
                Quaternion turn = Quaternion.FromAxisAngle(axis.Normalized(), angle);
                Pos[chain[1]] = root + Vector3.Transform(mid - root, turn);
                if (chain.Length < 3)
                {
                    continue;
                }
                Vector3 end = root + Vector3.Transform(Pos[chain[2]] - root, turn);
                Quaternion more = Quaternion.FromAxisAngle(axis.Normalized(), angle * 0.6f);
                Pos[chain[2]] = Pos[chain[1]] + Vector3.Transform(end - Pos[chain[1]], more);
            }
            for (int i = 0; i < PointCount; i++)
            {
                Prev[i] = Pos[i];
            }
        }

        private const int TwitchStart = 3;
        private const int TwitchTicks = 165; // (~2.75 s)
        // (owner 2026-11-10: "makes the whole body flop it should just have twitchy limbs", and "a bit more obvious": the
        // torso no longer kicked, the kicks cancelled within the body, harder)
        private const float TwitchKick = 0.035f;
        private const uint JerkChance = 22; // (in 1000, a limb a step: about one jerk a limb a second, at random)
        private const float TrembleKick = 0.22f; // (of a jerk's size, every point every step, back and forth)
        // the limbs a jerk can take, each its end or its middle (the head nods, at half the size)
        private static readonly int[][] _twitchLimbs =
        {
            new[] { LWrist, LElbow }, new[] { RWrist, RElbow }, new[] { LAnkle, LKnee }, new[] { RAnkle, RKnee },
            new[] { HeadTop, HeadTop }
        };
        // a jerk's kicks, step by step from its start: a snap out over two steps and back (not all in one step: Trace's short
        // sticks stretched)
        private static readonly float[] _twitchShake = { 1.6f, 0.9f, -1.2f, -0.6f };

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }

        private static float HashUnit(uint x) => (Hash(x) & 0xFFFF) / 32767.5f - 1; // -1..1

        private void TwitchKicks()
        {
            int t = Ticks - _twitchFrom;
            if (t < 0 || Ticks >= _twitchEnd)
            {
                return;
            }
            // (fading: smaller towards the end, then the body lies still)
            float fade = 1 - 0.7f * t / TwitchTicks;
            Array.Clear(_kick);
            for (int limb = 0; limb < _twitchLimbs.Length; limb++)
            {
                for (int back = 0; back < _twitchShake.Length && back <= t; back++)
                {
                    // (a jerk that started this step or the last few: whether one did is the limb's and that step's hash)
                    uint key = _twitchSeed + (uint)(t - back) * 0x9E3779B9u + (uint)limb * 0x85EBCA6Bu;
                    if (Hash(key) % 1000 >= JerkChance)
                    {
                        continue;
                    }
                    int point = _twitchLimbs[limb][Hash(key + 1) & 1];
                    var dir = new Vector3(HashUnit(key + 2), HashUnit(key + 3) * 0.5f + 0.5f, HashUnit(key + 4));
                    if (dir.LengthSquared < 1e-4f)
                    {
                        dir = Vector3.UnitY;
                    }
                    float size = _twitchSize * _twitchShake[back] * fade * (point == HeadTop ? 0.5f : 1);
                    _kick[point] += dir.Normalized() * size;
                }
            }
            // (the tremble: every point a small kick each step, its sign flipping step to step, so the body shakes
            // where it lies rather than drifting)
            float tremble = _twitchSize * TrembleKick * fade * ((t & 1) == 0 ? 1 : -1);
            uint shake = _twitchSeed + (uint)(t >> 1) * 0xC2B2AE35u;
            for (int i = 0; i < PointCount; i++)
            {
                uint pk = shake + (uint)i * 0x27D4EB2Fu;
                _kick[i] += new Vector3(HashUnit(pk), HashUnit(pk + 1) * 0.5f, HashUnit(pk + 2)) * tremble;
            }
            // (as the writhe's: the kicks add up to nothing, so the limbs jerk and the body stays where it lies)
            Vector3 mean = Vector3.Zero;
            for (int i = 0; i < PointCount; i++)
            {
                mean += _kick[i];
            }
            mean /= PointCount;
            for (int i = 0; i < PointCount; i++)
            {
                Prev[i] -= _kick[i] - mean;
            }
        }

        // (not called since 2026-11-10: the owner dropped the burn death; kept for their call) writhe for a while before
        // lying still (a burn): slow curls (knees and arms in, the head tucked, then out) and
        // rolls about the spine, fading. From the step count and a seed taken from the pose at death, with no RNG and no
        // sines (the same bits on every machine)
        public void Writhe()
        {
            _writheEnd = WritheStart + WritheTicks;
            _writheSeed = Hash((uint)BitConverter.SingleToInt32Bits(Start[Pelvis].Z)
                ^ (uint)BitConverter.SingleToInt32Bits(Start[Pelvis].X) * 0x85EBCA6Bu);
            _writheSize = (Start[HeadTop] - Start[Pelvis]).Length * WritheForce;
        }

        // (owner 2026-11-10: "a little too shakey and busy it should be more subtle": a third the push, shorter, slower
        // curls and a small roll)
        private const int WritheStart = 4;
        private const int WritheTicks = 150;
        private const int CurlPeriod = 70;
        private const int RollPeriod = 110;
        private const float WritheForce = 0.009f;
        private const float WritheRoll = 0.3f; // (the roll's share of the curl's size)
        // (after SettleTicks the damping is heavier: the writhe pushes harder so it still shows)
        private const float WritheSettled = 1.5f;

        // a smooth wave from the step count alone: -1..1, a parabola each half period (sine-like, no MathF.Sin)
        private static float Wave(int t, int period)
        {
            int p = (t % period + period) % period;
            float u = p * 2f / period;
            return u < 1 ? 4 * u * (1 - u) : -4 * (u - 1) * (2 - u);
        }

        private void WritheKicks()
        {
            int t = Ticks - WritheStart;
            if (t < 0 || Ticks >= _writheEnd)
            {
                return;
            }
            float fade = 1 - t / (float)WritheTicks;
            float size = _writheSize * fade * (Ticks < SettleTicks ? 1 : WritheSettled);
            float curl = Wave(t + (int)(_writheSeed % CurlPeriod), CurlPeriod) * size;
            float roll = Wave(t + (int)((_writheSeed >> 8) % RollPeriod), RollPeriod) * size * WritheRoll
                * ((_writheSeed & 0x10000) != 0 ? 1 : -1);
            Array.Clear(_kick);
            Toward(LKnee, Chest, curl);
            Toward(RKnee, Chest, curl);
            Toward(LAnkle, Pelvis, curl * 0.5f);
            Toward(RAnkle, Pelvis, curl * 0.5f);
            Toward(LWrist, Chest, curl);
            Toward(RWrist, Chest, curl);
            Toward(HeadTop, Pelvis, curl * 0.5f);
            // (the roll: each side point pushed round the spine)
            Vector3 axis = Pos[Chest] - Pos[Pelvis];
            if (axis.LengthSquared > 1e-8f)
            {
                axis.Normalize();
                foreach (int point in _rollPoints)
                {
                    Vector3 r = Pos[point] - Pos[Pelvis];
                    r -= axis * Vector3.Dot(r, axis);
                    Vector3 round = Vector3.Cross(axis, r);
                    if (round.LengthSquared > 1e-8f)
                    {
                        _kick[point] += round.Normalized() * roll;
                    }
                }
            }
            // (all from within: the kicks add up to nothing, so the body squirms where it lies rather than sliding off)
            Vector3 mean = Vector3.Zero;
            for (int i = 0; i < PointCount; i++)
            {
                mean += _kick[i];
            }
            mean /= PointCount;
            for (int i = 0; i < PointCount; i++)
            {
                Prev[i] -= _kick[i] - mean;
            }
        }

        private void Toward(int point, int to, float by)
        {
            Vector3 d = Pos[to] - Pos[point];
            if (d.LengthSquared > 1e-8f)
            {
                _kick[point] += d.Normalized() * by;
            }
        }

        private static readonly int[] _rollPoints = { LShoulder, RShoulder, LElbow, RElbow, LHip, RHip, LKnee, RKnee };

        // freeze stiff where it stands, then topple the way the shot went (a Judicator death). The launch waits out the
        // hold, then the body goes over as one piece and loosens as it lands
        public void Freeze()
        {
            _freezeEnd = FreezeTicks;
            _stiffEnd = FreezeTicks + StiffTicks;
            _stiffHold = StiffHold;
            if (!TryFrame(Pos[RHip] - Pos[LHip], Pos[Chest] - Pos[Pelvis], out Matrix3 frame))
            {
                _stiffEnd = _freezeEnd; // (no frame: held, then loose)
                return;
            }
            Vector3 middle = Middle();
            _shape = new Vector3[PointCount];
            float low = float.MaxValue;
            Vector3 went = Vector3.Zero;
            for (int i = 0; i < PointCount; i++)
            {
                _shape[i] = ToLocal(Pos[i] - middle, frame);
                low = MathF.Min(low, Pos[i].Y);
                went += Pos[i] - Prev[i];
            }
            // (the topple: the higher a point, the more it goes over, the feet staying put; the way the launch went, or
            // back from the facing at death when it went nowhere)
            Vector3 way = went.WithY(0);
            if (way.LengthSquared < 1e-10f)
            {
                way = -frame.Row2.WithY(0);
            }
            if (way.LengthSquared < 1e-10f)
            {
                return;
            }
            way.Normalize();
            // (the launch as one move, the whole body's: Vary's twist and tumble differ point to point, and let go under
            // the held shape they spun the frozen body fast as it fell, owner 2026-11-10)
            went /= PointCount;
            for (int i = 0; i < PointCount; i++)
            {
                Prev[i] = Pos[i] - went - way * (ToppleSpeed * (Pos[i].Y - low));
            }
        }

        private const int FreezeTicks = 40;
        private const int StiffTicks = 45;
        private const float ToppleSpeed = 0.014f; // (per step, per unit of height above the lowest point)
        private const float StiffHold = 0.6f;
        private const float LandFloor = 0.5f; // (a contact's normal this far up: a floor, for the ice's break)
        private const int IceStill = 6;
        private const int IceCap = 90;

        // a headshot kill: the head snaps back on the neck, the way the shot went, ahead of the body. The neck gives more
        // than its nod for a moment, then firms up again (from the launch's push alone, so online and replays agree)
        public void HeadSnap(Vector3 push)
        {
            Vector3 way = push.WithY(0);
            if (way.LengthSquared < 1e-10f || _shockEnd > 0) // (an electric death's shaking hold is its own: no tilt over it)
            {
                return;
            }
            way.Normalize();
            // (the body tips over the way the shot went, already moving, the higher a point the faster, and gravity takes
            // the tip on into the collapse; its shape loosens as it goes so it bends late rather than buckling at once.
            // Owner 2026-11-10: "head jerks with headshot> body starts tilting in that direction and naturally gravity
            // catches the tilt momentum and the body collapses", not frozen in place; then "it needs to take like 1.5 seconds from the
            // head tilt to hitting the ground", then "its still really fast ... maybe 2.5 seconds is right": the head is down
            // at about 2 s, the body settled by about 2.5 s)
            if (TryFrame(Pos[RHip] - Pos[LHip], Pos[Chest] - Pos[Pelvis], out Matrix3 frame))
            {
                Vector3 middle = Middle(tilt: true);
                _shape = new Vector3[PointCount];
                float low = float.MaxValue;
                for (int i = 0; i < PointCount; i++)
                {
                    _shape[i] = ToLocal(Pos[i] - middle, frame);
                    low = MathF.Min(low, Pos[i].Y);
                }
                // (the shot's throw goes into the head: the body keeps only some of it, or it is flat in a quarter second
                // whatever holds it)
                for (int i = 0; i < PointCount; i++)
                {
                    Prev[i] = Pos[i] - (Pos[i] - Prev[i]) * TiltCarry - way * (TiltSpeed * (Pos[i].Y - low));
                }
                _stiffEnd = TiltTicks;
                _stiffHold = TiltHold;
            }
            float size = (Start[HeadTop] - Start[Pelvis]).Length * SnapKick;
            Prev[HeadTop] -= (way + Vector3.UnitY * SnapLift) * size;
            Prev[Neck] -= way * (size * SnapNeck);
            // (where the head is held for a moment: bent SnapAngle off the neck's line, back the way the shot went as the
            // chest saw it, so it stays bent back while the body goes over instead of the body catching it up)
            if (!TryFrame(Pos[RShoulder] - Pos[LShoulder], Pos[Neck] - Pos[Chest], out Matrix3 chest))
            {
                return;
            }
            Vector3 up = ToLocal((Pos[Neck] - Pos[Chest]).Normalized(), chest);
            Vector3 back = ToLocal(way, chest);
            back -= up * Vector3.Dot(back, up);
            if (back.LengthSquared < 1e-10f)
            {
                return;
            }
            _snapHead = up * SnapCos + back.Normalized() * SnapSin;
            _snapEnd = SnapTicks;
        }

        private void HoldHead(float by)
        {
            if (!TryFrame(Pos[RShoulder] - Pos[LShoulder], Pos[Neck] - Pos[Chest], out Matrix3 chest))
            {
                return;
            }
            // (the neck takes the other half, so it turns the head on the neck instead of pulling the whole body over)
            float length = (Pos[HeadTop] - Pos[Neck]).Length;
            Vector3 move = (Pos[Neck] + FromLocal(_snapHead, chest) * length - Pos[HeadTop]) * (by * 0.5f);
            Pos[HeadTop] += move;
            Pos[Neck] -= move;
        }

        private const float SnapKick = 0.03f; // (per step, times the body's height)
        private const float SnapLift = 0.3f;
        private const float SnapNeck = 0.3f;
        // (owner 2026-11-10: "happens to fast to see if the head is jerking back": held back 0.2 s, let go by 0.5 s)
        private const int SnapTicks = 30;
        private const float SnapGive = 0.25f; // (the nod's least length, less this share of the length at death)
        // (the head held bent back this hard for SnapHold steps, let go by SnapTicks: a kick alone only passes on into the
        // chest, and the whole body tips back with it)
        private const float SnapPull = 0.3f; // (per pass)
        private const float SnapCos = 0.4226f; // (65 degrees, as numbers so every device gets the same)
        private const float SnapSin = 0.9063f;
        private const int SnapHold = 12;
        private const float TiltCarry = 0.1f; // (the share of the throw the body keeps)
        private const float TiltSpeed = 0.005f; // (per step, per unit of height above the lowest point)
        // (owner 2026-11-10: "leave the head alone but make the body crumple about twice as fast on the same curve": the
        // body's clock runs TiltRate times as fast along the same curve, over half the time, so it is over in about a
        // second; the head's snap is held by HoldHead in real steps, so it stays as it was)
        private const int TiltTicks = 100; // (the shape let go and the body's time back up to speed over ~1.7 s)
        private const float TiltHold = 0.6f;
        private const float TiltSlow = 0.13f; // (how fast the body's time runs at the shot, before TiltRate)
        private const float TiltRate = 2;
        private const int NodStick = 5; // (_range's Chest-HeadTop pair)

        // (a headshot's tilt holds only the trunk and legs: the head and neck are the snap's, and the shoulders give the
        // snap its frame. A middle that counts points the hold leaves out pushes the held body as a whole, every pass,
        // and holding the shoulders too fed the snap's pull back into the body, both sending it back and up off the floor)
        private static bool Tilted(int i)
        {
            return i == Pelvis || i == Chest || i >= LHip && i <= RAnkle;
        }

        private Vector3 Middle(bool tilt = false)
        {
            Vector3 middle = Vector3.Zero;
            int count = 0;
            for (int i = 0; i < PointCount; i++)
            {
                if (!tilt || Tilted(i))
                {
                    middle += Pos[i];
                    count++;
                }
            }
            return middle / count;
        }

        // (a frozen body's topple: each point pulled to its place in the held pose, put where the body is now)
        private void HoldShape(float by)
        {
            if (_shape == null || !TryFrame(Pos[RHip] - Pos[LHip], Pos[Chest] - Pos[Pelvis], out Matrix3 frame))
            {
                return;
            }
            bool tilt = _snapEnd > 0;
            Vector3 middle = Middle(tilt);
            frame = BestTurn(frame, middle, tilt);
            for (int i = 0; i < PointCount; i++)
            {
                if (tilt && !Tilted(i))
                {
                    continue;
                }
                Pos[i] += (middle + FromLocal(_shape[i], frame) - Pos[i]) * by;
            }
        }

        // the turn that best puts the held shape on the held points as they are now, from the hips' frame on (a frame from
        // the hips alone turned the whole held body with every bump on a hip, so it spun and swung a leg up in the air:
        // owner 2026-11-10, frozen and headshot bodies "still spinning", "a leg is usually sticking up in the air"). The
        // frame's rows are the turn's columns (FromLocal); each pass turns them toward the points, a fixed count of passes
        // so every device gets the same
        private Matrix3 BestTurn(Matrix3 frame, Vector3 middle, bool tilt)
        {
            Vector3 a0 = Vector3.Zero, a1 = Vector3.Zero, a2 = Vector3.Zero;
            for (int i = 0; i < PointCount; i++)
            {
                if (tilt && !Tilted(i))
                {
                    continue;
                }
                Vector3 d = Pos[i] - middle;
                Vector3 l = _shape![i];
                a0 += d * l.X;
                a1 += d * l.Y;
                a2 += d * l.Z;
            }
            Vector3 r0 = frame.Row0, r1 = frame.Row1, r2 = frame.Row2;
            for (int pass = 0; pass < TurnPasses; pass++)
            {
                Vector3 w = (Vector3.Cross(r0, a0) + Vector3.Cross(r1, a1) + Vector3.Cross(r2, a2))
                    / (MathF.Abs(Vector3.Dot(r0, a0) + Vector3.Dot(r1, a1) + Vector3.Dot(r2, a2)) + 1e-9f);
                float angle = w.Length;
                if (angle < 1e-7f)
                {
                    break;
                }
                Vector3 axis = w / angle;
                float cos = MathF.Cos(angle);
                float sin = MathF.Sin(angle);
                r0 = Turn(r0, axis, cos, sin);
                r1 = Turn(r1, axis, cos, sin);
                r2 = Turn(r2, axis, cos, sin);
            }
            return new Matrix3(r0, r1, r2);
        }

        private static Vector3 Turn(Vector3 v, Vector3 axis, float cos, float sin)
        {
            return v * cos + Vector3.Cross(axis, v) * sin + axis * (Vector3.Dot(axis, v) * (1 - cos));
        }

        private const int TurnPasses = 6;

        // a held body's turn about the up axis taken out (a frozen body until it loosens, a headshot's tilt): it goes over the
        // way it was hit instead of swinging round on a foot that caught the floor (owner 2026-11-10: "the freeze death is
        // still spinning", "id think theyd just fall the direction they were hit")
        private void StopSpin()
        {
            Vector3 middle = Vector3.Zero;
            for (int i = 0; i < PointCount; i++)
            {
                middle += Pos[i];
            }
            middle /= PointCount;
            float spin = 0;
            float inertia = 0;
            for (int i = 0; i < PointCount; i++)
            {
                Vector3 r = Pos[i] - middle;
                Vector3 v = Pos[i] - Prev[i];
                spin += r.Z * v.X - r.X * v.Z;
                inertia += r.X * r.X + r.Z * r.Z;
            }
            if (inertia < 1e-8f)
            {
                return;
            }
            float w = spin / inertia;
            for (int i = 0; i < PointCount; i++)
            {
                Vector3 r = Pos[i] - middle;
                Prev[i] += new Vector3(r.Z, 0, -r.X) * w;
            }
        }

        public void Step(Scene scene)
        {
            IceBrokeNow = false;
            if (Asleep)
            {
                return;
            }
            if (Ticks < _freezeEnd)
            {
                // (held where it froze, the launch waiting in Prev)
                Ticks++;
                Moved = 0;
                StillCount = 0;
                return;
            }
            if (Ticks < _shockEnd)
            {
                Ticks++;
                StillCount = 0;
                if (Ticks < _shockEnd)
                {
                    ShockShake();
                    Moved = 0;
                    return;
                }
                // (let go where it stood, from rest but for a share of the launch, and it collapses)
                for (int i = 0; i < PointCount; i++)
                {
                    Pos[i] = _shockPose![i];
                    Prev[i] = Pos[i] - _shockWent * ShockCarry;
                }
                Moved = 0;
                return;
            }
            _stiffNow = Ticks < _stiffEnd ? _stiffHold * (1 - (Ticks - _freezeEnd) / (float)(_stiffEnd - _freezeEnd)) : 0;
            _nodNow = Ticks < _snapEnd ? SnapGive * (1 - Ticks / (float)SnapTicks) : 0;
            _snapNow = Ticks < _snapEnd ? SnapPull * MathF.Min(1, (_snapEnd - Ticks) / (float)(SnapTicks - SnapHold)) : 0;
            TwitchKicks();
            WritheKicks();
            _landed = false;
            // (once the fall is over, more damping so a body caught on an edge settles instead of trembling)
            float damping = Ticks < SettleTicks ? Damping : SettleDamping;
            float slow = 1;
            if (_snapEnd > 0 && Ticks < _stiffEnd)
            {
                // (a headshot's tilt: the body's time runs slow at the shot and comes back up to speed along a curve, so the
                // tip follows gravity's own curve, only slower, and gravity takes it on into the fall, whatever pose it died
                // in. A drag held it back as a steady sink instead; owner 2026-11-10: "maybe 2.5 seconds is right")
                float t = Ticks / (float)_stiffEnd;
                slow = MathF.Min(1, TiltRate * (TiltSlow + (1 - TiltSlow) * t * t));
                _stiffNow = _stiffHold * (1 - t);
            }
            if (Iced || Ticks < _stiffEnd)
            {
                StopSpin();
            }
            float gravity = Gravity;
            if (slow != _slow)
            {
                float r = slow / _slow;
                for (int i = 0; i < PointCount; i++)
                {
                    Prev[i] = Pos[i] - (Pos[i] - Prev[i]) * r;
                }
                _slow = slow;
            }
            if (slow != 1)
            {
                gravity *= slow * slow;
                damping = MathF.Pow(damping, slow);
            }
            // (a throw faster than MaxStep a step goes in smaller steps, so the collision and the sticks only ever see
            // moves the size of an ordinary death's: the turret and Omega Cannon throws, owner 2026-11-08; up to 4 since their
        // 11-09 ramp, 2 capped them at ~1 a step)
            float fastest = 0;
            for (int i = 0; i < PointCount; i++)
            {
                _start[i] = Pos[i];
                fastest = MathF.Max(fastest, (Pos[i] - Prev[i]).Length * damping);
            }
            int steps = Math.Clamp((int)MathF.Ceiling(fastest / MaxStep), 1, MaxSubsteps);
            if (steps == 1)
            {
                SubStep(scene, damping, gravity);
            }
            else
            {
                for (int i = 0; i < PointCount; i++)
                {
                    Prev[i] = Pos[i] - (Pos[i] - Prev[i]) / steps;
                }
                for (int s = 0; s < steps; s++)
                {
                    SubStep(scene, MathF.Pow(damping, 1f / steps), gravity / (steps * steps));
                }
                for (int i = 0; i < PointCount; i++)
                {
                    Prev[i] = Pos[i] - (Pos[i] - Prev[i]) * steps;
                }
            }
            float moved = 0;
            for (int i = 0; i < PointCount; i++)
            {
                moved = MathF.Max(moved, (Pos[i] - _start[i]).Length);
            }
            Moved = moved;
            Ticks++;
            LetGo += Iced ? 0 : _stiffHold > 0 ? 1 - _stiffNow / _stiffHold : 1;
            StillCount = moved < StillMove ? StillCount + 1 : 0;
            // (or, when it never lands on its top half, once the topple's over and it's come to rest)
            if (Iced && (_landed || Ticks >= _stiffEnd && StillCount >= IceStill || Ticks >= _stiffEnd + IceCap))
            {
                IceBroken = true;
                IceBrokeNow = true;
                // (the landing takes the statue's turn: it goes on as one move, or the held shape vaults the legs up over
                // the head that hit first)
                Vector3 went = Vector3.Zero;
                for (int i = 0; i < PointCount; i++)
                {
                    went += Pos[i] - Prev[i];
                }
                went /= PointCount;
                for (int i = 0; i < PointCount; i++)
                {
                    Prev[i] = Pos[i] - went;
                }
            }
            if ((StillCount >= StillTicks || Ticks >= MaxTicks) && !Twitching)
            {
                Asleep = true;
            }
        }

        private void SubStep(Scene scene, float damping, float gravity)
        {
            Vector3 min = Pos[0];
            Vector3 max = Pos[0];
            for (int i = 0; i < PointCount; i++)
            {
                _contact[i] = Vector3.Zero;
                Vector3 v = (Pos[i] - Prev[i]) * damping;
                float speed = v.Length;
                if (speed > MaxStep)
                {
                    v *= MaxStep / speed;
                }
                v.Y -= gravity;
                Prev[i] = Pos[i];
                Pos[i] += v;
                min = Vector3.ComponentMin(min, Vector3.ComponentMin(Pos[i], _clear[i]));
                max = Vector3.ComponentMax(max, Vector3.ComponentMax(Pos[i], _clear[i]));
            }
            float margin = 0.5f;
            min -= new Vector3(margin);
            max += new Vector3(margin);
            for (int it = 0; it < Iterations; it++)
            {
                SolveSticks();
                if (_stiffNow > 0)
                {
                    HoldShape(_stiffNow);
                }
                if (_snapNow > 0)
                {
                    HoldHead(_snapNow);
                }
                if (it % 4 == 1 || it == Iterations - 1)
                {
                    Collide(min, max, scene);
                }
            }
            // (only a point that got here without crossing a face: one a push shoved into one keeps its old spot, so the
            // next sweep crosses that face and pushes it back out)
            for (int i = 0; i < PointCount; i++)
            {
                var result = new CollisionResult();
                if (!CollisionDetection.CheckBetweenPoints(_clear[i], Pos[i], TestFlags.None, scene, ref result))
                {
                    _clear[i] = Pos[i];
                }
            }
            for (int i = 0; i < PointCount; i++)
            {
                Vector3 n = _contact[i];
                if (n != Vector3.Zero)
                {
                    n.Normalize();
                    if (n.Y > LandFloor && (i == Chest || i == Neck || i == HeadTop || i == LShoulder || i == RShoulder))
                    {
                        _landed = true;
                    }
                    Vector3 v = Pos[i] - Prev[i];
                    float dot = Vector3.Dot(v, n);
                    Vector3 tangent = v - n * dot;
                    // (a slow slide sticks where it was, so a body on a gentle slope comes to rest instead of creeping)
                    // (not while writhing: its slow squirm would stick fast)
                    if (tangent.Length < StaticSlip && Ticks >= _writheEnd)
                    {
                        Pos[i] -= tangent;
                        Prev[i] = Pos[i];
                    }
                    else
                    {
                        Prev[i] = Pos[i] - tangent * Friction;
                    }
                }
            }
            // (the sticks get the last word, so a push-out or a pin never leaves the short torso sticks stretched)
            // (a hard landing can leave them far off, so it keeps going until they hold or it runs out of passes)
            for (int it = 0; it < FinalPasses; it++)
            {
                SolveSticks();
                if (it >= 3 && MaxStickError() < 0.01f)
                {
                    break;
                }
            }
            Array.Clear(_results);
        }

        private void SolveSticks()
        {
            for (int i = 0; i < _rigidLength.Length; i++)
            {
                Fit(_rigid[i * 2], _rigid[i * 2 + 1], _rigidLength[i], _rigidLength[i]);
            }
            for (int i = 0; i < _rangeLength.Length; i++)
            {
                float rest = _rangeLength[i];
                float give = i == NodStick ? _nodNow : 0;
                Fit(_range[i * 2], _range[i * 2 + 1], rest * (1 - (1 - _rangeMin[i]) * _loose - give), rest * (1 + (_rangeMax[i] - 1) * _loose));
            }
            for (int side = 0; side < 2; side++)
            {
                int sh = side == 0 ? LShoulder : RShoulder;
                int hip = side == 0 ? LHip : RHip;
                Fit(sh, sh + 2, _armReach[side] * 0.35f, _armReach[side]);
                Fit(hip, hip + 2, _legReach[side] * 0.4f, _legReach[side]);
                Fit(Chest, hip + 1, _chestKnee[side], float.MaxValue);
            }
            Fit(LKnee, RKnee, _hipWidth * 0.5f, float.MaxValue);
            Fit(LAnkle, RAnkle, _hipWidth * 0.5f, float.MaxValue);
            // the knees bend one way: back to the bend side of the hip-ankle line
            if (TryFrame(Pos[RHip] - Pos[LHip], Pos[Chest] - Pos[Pelvis], out Matrix3 pelvis))
            {
                for (int side = 0; side < 2; side++)
                {
                    int hip = side == 0 ? LHip : RHip;
                    Vector3 bend = FromLocal(_kneeSide[side], pelvis);
                    Vector3 mid = (Pos[hip] + Pos[hip + 2]) * 0.5f;
                    float dot = Vector3.Dot(Pos[hip + 1] - mid, bend);
                    if (dot < 0)
                    {
                        Pos[hip + 1] -= bend * dot;
                    }
                }
            }
        }

        private void Fit(int a, int b, float minLength, float maxLength)
        {
            Vector3 between = Pos[b] - Pos[a];
            float length = between.Length;
            if (length < 1e-6f)
            {
                return;
            }
            float target = length < minLength ? minLength : length > maxLength ? maxLength : length;
            if (target == length)
            {
                return;
            }
            Vector3 fix = between * ((length - target) / length * 0.5f);
            Pos[a] += fix;
            Pos[b] -= fix;
        }

        // the room's faces push each point out along their normal (as a bomb's, BombEntity)
        private void Collide(Vector3 min, Vector3 max, Scene scene)
        {
            // (fetched each time and used before any CheckInRadius, which refills the shared list with its own room-only
            // few; a point1 brings in the entities' collision too, the Echoes arenas' grates, owner 2026-11-08: "ragdoll
            // bodies can clip through grates")
            var candidates = CollisionDetection.GetCandidatesForLimits(min, max, 0, min, max, includeEntities: true, scene);
            for (int i = 0; i < PointCount; i++)
            {
                float radius = _radius[i];
                int count = CollisionDetection.CheckSphereBetweenPoints(candidates, _clear[i], Pos[i], radius, limit: 8,
                    includeOffset: false, TestFlags.None, scene, _results);
                for (int j = 0; j < count; j++)
                {
                    CollisionResult result = _results[j];
                    if (result.Field0 != 0)
                    {
                        continue;
                    }
                    float dotw = result.Plane.W - Vector3.Dot(Pos[i], result.Plane.Xyz) + radius;
                    if (dotw > 0)
                    {
                        Pos[i] += result.Plane.Xyz * dotw;
                        _contact[i] += result.Plane.Xyz;
                    }
                }
            }
            for (int i = 0; i < PointCount; i++)
            {
                float radius = _radius[i];
                // (the sweep only sees faces square under a point: this pushes it off a ledge's edge or corner too)
                int count = CollisionDetection.CheckInRadius(Pos[i], radius, limit: 8, getSimpleNormal: false, TestFlags.None, scene, _results);
                for (int j = 0; j < count; j++)
                {
                    CollisionResult result = _results[j];
                    Pos[i] += result.Plane.Xyz * (radius - result.Field14);
                    _contact[i] += result.Plane.Xyz;
                }
            }
            // a limb bone lying across a ledge's edge is held up along its length too (each end by how near it is), so it
            // can't sag through the corner
            for (int b = 0; b < _limbBones.Length; b += 2)
            {
                int one = _limbBones[b];
                int two = _limbBones[b + 1];
                float radius = MathF.Min(_radius[one], _radius[two]);
                for (int k = 1; k <= 3; k++)
                {
                    float t = k * 0.25f;
                    Vector3 at = Pos[one] + (Pos[two] - Pos[one]) * t;
                    int count = CollisionDetection.CheckInRadius(at, radius, limit: 8, getSimpleNormal: false, TestFlags.None, scene, _results);
                    for (int j = 0; j < count; j++)
                    {
                        CollisionResult result = _results[j];
                        Vector3 push = result.Plane.Xyz * (radius - result.Field14);
                        Pos[one] += push * (2 * (1 - t));
                        Pos[two] += push * (2 * t);
                        _contact[one] += result.Plane.Xyz;
                        _contact[two] += result.Plane.Xyz;
                    }
                }
            }
        }

        private static readonly int[] _limbBones =
        {
            LShoulder, LElbow, LElbow, LWrist, RShoulder, RElbow, RElbow, RWrist,
            LHip, LKnee, LKnee, LAnkle, RHip, RKnee, RKnee, RAnkle, Pelvis, Chest,
        };

        // the draw: each node is its pose at death moved by its driver's turn about the driver's anchor
        public void Pose(Model model, Matrix4[] rest, int[] drivers)
        {
            if (rest.Length != model.Nodes.Count || drivers.Length != model.Nodes.Count)
            {
                return;
            }
            Span<Matrix3> turn = stackalloc Matrix3[DriverCount];
            if (!Turns(turn))
            {
                return;
            }
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int d = drivers[i];
                int anchor = _driverFrom[d];
                Matrix4 m = rest[i];
                m.Row3.Xyz -= Start[anchor];
                Matrix3 r = turn[d];
                var r4 = new Matrix4(new Vector4(r.Row0, 0), new Vector4(r.Row1, 0), new Vector4(r.Row2, 0), Vector4.UnitW);
                m *= r4;
                m.Row3.Xyz += Pos[anchor];
                model.Nodes[i].Animation = m;
            }
        }

        // each driver's turn from the pose at death (row vectors: a bone's matrix times it); false when the body's shape
        // can't make frames now
        public bool Turns(Span<Matrix3> turn)
        {
            if (turn.Length < DriverCount
                || !TryFrame(Pos[RHip] - Pos[LHip], Pos[Chest] - Pos[Pelvis], out Matrix3 pelvis)
                || !TryFrame(Pos[RShoulder] - Pos[LShoulder], Pos[Neck] - Pos[Chest], out Matrix3 chest))
            {
                return false;
            }
            turn[DPelvis] = Transpose(_pelvis0) * pelvis;
            turn[DChest] = Transpose(_chest0) * chest;
            for (int d = 2; d < DriverCount; d++)
            {
                Matrix3 parent = turn[_driverParent[d]];
                Vector3 aim0 = Start[_driverTo[d]] - Start[_driverFrom[d]];
                Vector3 aimNow = Pos[_driverTo[d]] - Pos[_driverFrom[d]];
                turn[d] = aim0.LengthSquared < 1e-10f || aimNow.LengthSquared < 1e-10f
                    ? parent : parent * Arc(Mul(aim0.Normalized(), parent), aimNow.Normalized());
            }
            return true;
        }

        // the forearm's way for an arm with no wrist bone: the upper arm's way in the shoulder's frame, carried by the elbow's
        // turn (a rig's arm lies straight in its bind pose)
        private static Vector3 Forearm(Matrix4 shoulder, Matrix4 elbow, Vector3 upperArm)
        {
            Vector3 local = Vector3.TransformVector(upperArm, Matrix4.Invert(shoulder.ClearTranslation()));
            Vector3 dir = Vector3.TransformVector(local, elbow.ClearTranslation());
            float length = dir.Length;
            return length > 1e-6f ? dir / length : upperArm.Normalized();
        }

        // each node's driver: itself or its nearest ancestor that is one (the rest ride the pelvis)
        private static int[] DriverMap(Model model)
        {
            var map = new int[model.Nodes.Count];
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = DPelvis;
                for (int j = i; j != -1; j = model.Nodes[j].ParentIndex)
                {
                    int d = Array.IndexOf(_driverBones, model.Nodes[j].Name);
                    if (d >= 0)
                    {
                        map[i] = d;
                        break;
                    }
                }
            }
            return map;
        }

        // rows: across (x), up (y), and their cross (z), orthonormal
        private static bool TryFrame(Vector3 across, Vector3 upward, out Matrix3 frame)
        {
            frame = Matrix3.Identity;
            float xl = across.Length;
            if (xl < 1e-4f)
            {
                return false;
            }
            Vector3 x = across / xl;
            Vector3 z = Vector3.Cross(x, upward);
            float zl = z.Length;
            if (zl < 1e-4f)
            {
                return false;
            }
            z /= zl;
            Vector3 y = Vector3.Cross(z, x);
            frame = new Matrix3(x, y, z);
            return true;
        }

        private static Vector3 ToLocal(Vector3 v, Matrix3 f)
        {
            return new Vector3(Vector3.Dot(v, f.Row0), Vector3.Dot(v, f.Row1), Vector3.Dot(v, f.Row2));
        }

        private static Vector3 FromLocal(Vector3 l, Matrix3 f)
        {
            return f.Row0 * l.X + f.Row1 * l.Y + f.Row2 * l.Z;
        }

        private static Vector3 Mul(Vector3 v, Matrix3 m)
        {
            return m.Row0 * v.X + m.Row1 * v.Y + m.Row2 * v.Z;
        }

        private static Matrix3 Transpose(Matrix3 m)
        {
            return new Matrix3(m.Column0, m.Column1, m.Column2);
        }

        // the smallest turn taking unit a to unit b (row vectors: a * Arc(a, b) = b)
        private static Matrix3 Arc(Vector3 a, Vector3 b)
        {
            Vector3 k = Vector3.Cross(a, b);
            float c = Vector3.Dot(a, b);
            if (c < -0.9999f)
            {
                Vector3 p = MathF.Abs(a.X) < 0.9f ? Vector3.Cross(a, Vector3.UnitX) : Vector3.Cross(a, Vector3.UnitY);
                p.Normalize();
                return new Matrix3(
                    new Vector3(2 * p.X * p.X - 1, 2 * p.X * p.Y, 2 * p.X * p.Z),
                    new Vector3(2 * p.Y * p.X, 2 * p.Y * p.Y - 1, 2 * p.Y * p.Z),
                    new Vector3(2 * p.Z * p.X, 2 * p.Z * p.Y, 2 * p.Z * p.Z - 1));
            }
            float h = 1 / (1 + c);
            return new Matrix3(
                new Vector3(c + h * k.X * k.X, k.Z + h * k.X * k.Y, -k.Y + h * k.X * k.Z),
                new Vector3(-k.Z + h * k.X * k.Y, c + h * k.Y * k.Y, k.X + h * k.Y * k.Z),
                new Vector3(k.Y + h * k.X * k.Z, -k.X + h * k.Y * k.Z, c + h * k.Z * k.Z));
        }
    }
}
