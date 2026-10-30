using System;
using System.Buffers;
using System.Diagnostics;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public partial class PlayerEntity
    {
        // host hook: don't draw the main player (a host's own fixed camera, e.g. the gunship cockpit view while the
        // in-ship menu is open, where a camera sequence would otherwise show the body)
        public static bool HostHideMainPlayer { get; set; }

        // host hook: don't draw this seat (a replay's first-person view from another player's eyes). Default -1, none.
        public static int HostHideSeat { get; set; } = -1;

        // recomp host hooks (a replay's first-person view from another seat's eyes, MatchReplay.cs; drawing only):
        // HostFirstPersonSeat draws its first-person gun instead of its body, turned about its eye by HostFirstPersonTurn
        // (the view's turn from the seat's own camera: a bot's eye kept level while it has no target); HostMainSpectated
        // draws the main player's body, since the view isn't its eyes. Default -1 / identity / off.
        public static int HostFirstPersonSeat { get; set; } = -1;
        public static Quaternion HostFirstPersonTurn { get; set; } = Quaternion.Identity;
        public static bool HostMainSpectated { get; set; }
        private bool HostFirstPersonView => SlotIndex == HostFirstPersonSeat && !IsMainPlayer;

        private Vector3 HostTurnAboutEye(Vector3 point)
            => CameraInfo.Position + Vector3.Transform(point - CameraInfo.Position, HostFirstPersonTurn);

        // recomp: the campaign host draws the main player's biped / alt form itself (an HD suit): MphRead still poses
        // them and draws the rest (trail, ice, charge and muzzle effects). HostShowMainBiped draws the main player's
        // biped in first person too (the host's third-person view: render only, the game's camera stays first person).
        // HostMainBipedAlpha = the main biped's alpha as last drawn (unmorph fade, cloak), 0 when it wasn't drawn.
        // All default off.
        public static bool HostOwnMainBiped { get; set; }
        public static bool HostOwnMainAlt { get; set; }
        public static bool HostShowMainBiped { get; set; }
        public static float HostMainBipedAlpha { get; private set; }
        public ModelInstance HostBipedModel1 => _bipedModel1; // the legs layer (BipedModel2 = the torso layer)

        // recomp: the same per player (a multiplayer skin on any seat, bots and remote players included): the host draws
        // this player's biped / alt form itself; HostBipedAlpha = its biped's alpha as last drawn, 0 when it wasn't drawn
        // (not visible, dead, morphed); HostDamageFlash = MphRead would draw it red this frame. Default off.
        public bool HostOwnBiped { get; set; }
        public Vector3 HostShootPoint { get; private set; }
        public Vector3? HostFxShootOffset { get; set; }
        public bool HostOwnAlt { get; set; }
        // recomp: the alt form's turn as MphRead draws it (its rotation rows), for an alt form the host draws (Beyond Lockjaw)
        public Matrix4 HostAltTurn => _modelTransform;

        // recomp: the main player's first-person charge glow, muzzle flash and gun smoke at this point of MPH's gun space
        // (world units: X left, Y up, Z forward) instead of the muzzle (0, 0, MuzzleOffset) -- the host's first-person arm
        // cannon puts them by its own barrel. Drawing only: the shots still spawn at _muzzlePos. Default null.
        public Vector3? HostFxMuzzle { get; set; }
        private Vector3 FxMuzzlePos => HostFxMuzzle is Vector3 at ? Matrix.Vec3MultMtx4(at, GetTransformMatrix(_aimVec, _upVector, _gunDrawPos)) : _muzzlePos;
        public float HostBipedAlpha { get; private set; }
        public bool HostDamageFlash => _timeSinceDamage < Values.DamageFlashTime * 2; // todo: FPS stuff (as Draw's)
        // recomp: the morph ball trail's, morph sphere's (samusFurl) and boost glow's (samusDash) colour for this player (a
        // skin's own, e.g. Dark Samus's blue); null = MPH's
        public Vector3? HostTrailColor { get; set; }
        // recomp: a no-import look-alike's palette set (D5, Read.HostExtraRecolors) for this seat's body and alt models; -1 =
        // Recolor. Drawing only (the gun: HostGunLookRecolor).
        public int HostLookRecolor { get; set; } = -1;
        // recomp: the seat's picked character's name (D7: TROOPER, DARK SAMUS) for the target health bar; null = the hunter's
        public string? HostCharacterName { get; set; }
        // recomp: world positions of these body nodes in the pose just drawn (HostTrackedPoints, in order; HostTrackedPoses
        // counts the poses), e.g. a look-alike's glows on its shoulders. Null = none (default).
        public string[]? HostTrackNodes { get; set; }
        public Vector3[] HostTrackedPoints { get; } = new Vector3[4];
        public int HostTrackedPoses { get; private set; }
        private void TrackHostNodes(Model model)
        {
            if (HostTrackNodes == null) return;
            for (int i = 0; i < HostTrackNodes.Length && i < HostTrackedPoints.Length; i++)
            {
                HostTrackedPoints[i] = model.GetNodeByName(HostTrackNodes[i])?.Animation.Row3.Xyz ?? Position;
            }
            HostTrackedPoses++;
        }
        // recomp: the same for the first-person gun (a look-alike's set on the gun model: the Trooper's / Dark Samus's
        // stand-in arm cannon in the seat's colours); -1 = Recolor
        public int HostGunLookRecolor { get; set; } = -1;
        // recomp: the first-person gun's light strips on the Power Beam in a host colour (MPH animates them amber per weapon;
        // the Dark Samus look-alike / stand-in: deep phazon blue, owner 10-27). null = MPH's
        public Vector3? HostGunLightColor { get; set; }
        // recomp: this player's bombs in a host colour (the 5th-8th seat with the same hunter, MphRecomp HunterColours;
        // Recolor stays MPH's 0-3): Kanden's tail bomb model's palette set, Sylux's Lockjaw glow colour (its bombStartSylux
        // effect's glow element) and trail (arcWelder1's set). -1 / null = Recolor's. Drawing only
        public int HostBombLookRecolor { get; set; } = -1;
        public Vector3? HostSyluxBombColor { get; set; }
        public int HostSyluxTrailRecolor { get; set; } = -1;

        internal int BodyRecolor(ModelInstance inst, int recolor)
            => HostLookRecolor >= 0 && HostLookRecolor < inst.Model.Recolors.Count ? HostLookRecolor : recolor;

        // recomp: a stand-in body drawn in place of the hunter's at both distances, on the hunter's skeleton (the same node
        // names: Limoki's Dark Samus built from Samus_lod0, MphRecomp.Core DarkSamusStandin.cs), in its own palette set
        // HostBodyRecolor (-1 = Recolor). Drawing only; null = the hunter's own (default).
        private ModelInstance? _hostBody;
        private Node? _hostSpineNode;
        private Node? _hostShootNode;
        public int HostBodyRecolor { get; set; } = -1;
        public ModelInstance? HostBodyModel
        {
            get => _hostBody;
            set
            {
                if (value == _hostBody) return;
                Node? spine = value?.Model.GetNodeByName(HostFirstHunt ? "Spine_2" : "Spine_1");
                Node? shoot = value?.Model.GetNodeByName(Hunter == Hunter.Guardian ? "Head_1" : "R_elbow");
                bool ok = spine != null && shoot != null;
                _hostBody = ok ? value : null;
                _hostSpineNode = ok ? spine : null;
                _hostShootNode = ok ? shoot : null;
            }
        }

        // recomp: a stand-in morph ball drawn in place of the hunter's plain rolling ball (Limoki's Dark Samus ball, built on
        // SamusAlt_lod0's ball node), in its own palette set HostAltRecolor (-1 = Recolor). Drawing only; null = the
        // hunter's own (default).
        public ModelInstance? HostAltModel { get; set; }
        public int HostAltRecolor { get; set; } = -1;

        public void Draw()
        {
            if (_furlEffect != null) _furlEffect.HostColor = HostTrailColor;
            if (_boostEffect != null) _boostEffect.HostColor = HostTrailColor;
            if (IsMainPlayer)
            {
                HostMainBipedAlpha = 0;
            }
            HostBipedAlpha = 0;
            if (IsMainPlayer && HostHideMainPlayer || SlotIndex == HostHideSeat)
            {
                // recomp: a hidden seat's charge glow / muzzle flash off too (else they stay where it was last drawn)
                _chargeEffect?.SetDrawEnabled(false);
                _muzzleEffect?.SetDrawEnabled(false);
                return;
            }
            DrawShadow();
            if (IsMainPlayer && ScanVisor)
            {
                DrawScanModels();
            }
            if (Flags2.TestFlag(PlayerFlags2.HideModel))
            {
                return;
            }
            if (Hunter == Hunter.Spire && Flags2.TestFlag(PlayerFlags2.AltAttack) && !_scene.HostDrawFreeSim)
            {
                UpdateSpireAltAttack();
            }
            int lod = 0;
            Flags2 &= ~PlayerFlags2.Lod1;
            if (!IsMainPlayer && !Features.MaxPlayerDetail
                && (Position - Main.CameraInfo.Position).LengthSquared >= 3 * 3)
            {
                lod = 1;
                Flags2 |= PlayerFlags2.Lod1;
            }
            _bipedModel1.SetModel(_hostBody?.Model ?? _bipedModelLods[lod].Model); // recomp: HostBodyModel (default none)
            _bipedModel2.SetModel(_hostBody?.Model ?? _bipedModelLods[lod].Model);
            Flags2 &= ~PlayerFlags2.DrawnThirdPerson;
            bool drawBiped = false;
            // todo: entity visibility check needs to do more than use active room parts
            // example issue - Kanden visible for one frame before Data Shrine 03 cam seq starts
            // should be culled because the cam seq frustum info has already been loaded, and is facing away from him,
            // even though the player's view is still what's on the screen (at least that seems to be what's happening)
            if (IsMainPlayer || HostFirstPersonView || IsVisible(NodeRef))
            {
                drawBiped = !IsMainPlayer && !HostFirstPersonView || CameraType != CameraType.First || CameraSequence.Current != null
                    || _camSwitchTimer < Values.CamSwitchTime * 2 // todo: FPS stuff
                    || IsMainPlayer && (HostShowMainBiped || HostMainSpectated); // (only non-main: HostFirstPersonView)
                if (IsAltForm)
                {
                    _modelTransform.Row3.Xyz = Position;
                    if (_timeSinceDamage < Values.DamageFlashTime * 2) // todo: FPS stuff
                    {
                        PaletteOverride = Metadata.RedPalette;
                    }
                    if (Hunter == Hunter.Kanden)
                    {
                        DrawKandenAlt();
                    }
                    else if (Hunter == Hunter.Spire && Flags2.TestFlag(PlayerFlags2.AltAttack))
                    {
                        DrawSpireAltAttack();
                    }
                    else if (HostScoutDrone)
                    {
                        DrawScoutDrone(); // recomp: the Trooper's drone (PlayerScoutDrone.cs)
                    }
                    else
                    {
                        ModelInstance ball = HostAltModel ?? _altModel;
                        UpdateTransforms(ball, _modelTransform, HostAltModel != null
                            ? HostAltRecolor >= 0 && HostAltRecolor < ball.Model.Recolors.Count ? HostAltRecolor : Recolor
                            : BodyRecolor(_altModel, Recolor));
                        if ((!IsMainPlayer || !HostOwnMainAlt) && !HostOwnAlt)
                        {
                            GetDrawItems(ball, ball.Model.Nodes[0], _curAlpha);
                        }
                    }
                    PaletteOverride = null;
                    if (_frozenGfxTimer > 0)
                    {
                        float radius = _volume.SphereRadius + 0.2f;
                        Matrix4 transform = Matrix4.CreateScale(radius) * _modelTransform;
                        transform.Row3.Y += Fixed.ToFloat(Values.AltColYPos);
                        UpdateTransforms(_altIceModel, transform, recolor: 0);
                        GetDrawItems(_altIceModel, _altIceModel.Model.Nodes[0], alpha: 1, recolor: 0);
                    }
                    if (Hunter == Hunter.Samus && !Flags2.TestFlag(PlayerFlags2.Cloaking))
                    {
                        DrawMorphBallTrail();
                    }
                    DrawHyperBall(); // recomp: Dark Samus's Hyper Ball stand-in (PlayerHyperBall.cs)
                    _modelTransform.Row3.Xyz = Vector3.Zero;
                    Flags2 |= PlayerFlags2.DrawnThirdPerson;
                }
                else if (drawBiped)
                {
                    // we want to animate first up to the spine with _bipedModel1's animation info, then the rest with _bipedModel2's info
                    Node spineNode = _hostSpineNode ?? _spineNodes[lod]!;
                    spineNode.AnimIgnoreChild = true;
                    // todo: we can just figure out the angle directly from the facing vector
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
                    Model model = _bipedModel1.Model;
                    model.AnimateNodes(index: 0, false, Matrix4.Identity, Vector3.One, HostFloatLegAnim(model)); // recomp: the float's still legs (PlayerHostFloat.cs), else _bipedModel1.AnimInfo
                    spineNode.AnimIgnoreChild = false;
                    model.AnimateNodes(spineNode.ChildIndex, false, Matrix4.Identity, Vector3.One, HostFloatUpperAnim(model)); // recomp: the float's still upper body (PlayerHostFloat.cs), else _bipedModel2.AnimInfo
                    spineNode.AfterTransform = null;
                    WallClingPoseModel(model); // recomp: Weavel's cling pose (PlayerWallCling.cs), off unless HostWallClingPose
                    HostFloatPoseModel(model); // recomp: Dark Samus's float on Samus's rig (PlayerHostFloat.cs), off unless HostFloatPose
                    float scale = Metadata.HunterScales[Hunter];
                    float bottom = Fixed.ToFloat(Values.MinPickupHeight);
                    var lateral = new Vector3(_field70, 0, _field74);
                    Matrix4 transform = Matrix4.Identity;
                    transform.Row0.Xyz = -_gunVec2;
                    transform.Row1.Xyz = Vector3.Cross(lateral, _gunVec2);
                    transform.Row2.Xyz = -lateral;
                    transform.Row3.Xyz = Position;
                    transform.Row3.Y += bottom + bottom * (1 - scale);
                    transform.Row0.Xyz *= scale;
                    transform.Row1.Xyz *= scale;
                    transform.Row2.Xyz *= scale;
                    for (int i = 0; i < model.Nodes.Count; i++)
                    {
                        Node node = model.Nodes[i];
                        node.Animation *= transform; // todo?: could do this in the shader
                    }
                    WallClingPoseWorld(model, lateral); // recomp: legs turned out of the wall while clinging
                    model.UpdateMatrixStack();
                    if (_health > 0)
                    {
                        if (_timeSinceDamage < Values.DamageFlashTime * 2) // todo: FPS stuff
                        {
                            PaletteOverride = Metadata.RedPalette;
                        }
                        float alpha = _curAlpha;
                        // recomp: no unmorph fade in the host's third-person view (it's for the camera going back into the head)
                        if (IsMainPlayer && CameraSequence.Current == null && !HostShowMainBiped
                            && _bipedModel1.AnimInfo.Index[0] == (int)PlayerAnimation.Unmorph)
                        {
                            alpha -= alpha * _bipedModel1.AnimInfo.Frame[0] / _bipedModel1.AnimInfo.FrameCount[0];
                            alpha = Math.Clamp(alpha, 0, 1);
                        }
                        UpdateMaterials(_bipedModel2, _hostBody != null
                            ? HostBodyRecolor >= 0 && HostBodyRecolor < _hostBody.Model.Recolors.Count ? HostBodyRecolor : Recolor
                            : BodyRecolor(_bipedModel2, Recolor));
                        if (IsMainPlayer)
                        {
                            HostMainBipedAlpha = alpha;
                        }
                        HostBipedAlpha = alpha;
                        if ((!IsMainPlayer || !HostOwnMainBiped) && !HostOwnBiped)
                        {
                            GetDrawItems(_bipedModel2, _bipedModel2.Model.Nodes[0], alpha);
                        }
                        PaletteOverride = null;
                        // recomp: HostShootPoint = where the third-person charge glow / muzzle flash go (the shoot node);
                        // HostFxShootOffset moves them (left, up, forward of Position by the facing), draw only. Default null.
                        HostShootPoint = Matrix.Vec3MultMtx4(Metadata.MuzzleOffests[(int)Hunter], (_hostShootNode ?? _shootNodes[lod]!).Animation);
                        TrackHostNodes(model); // recomp: HostTrackNodes (default none)
                        if (_chargeEffect != null || _muzzleEffect != null)
                        {
                            Vector3 muzzlePos = HostShootPoint;
                            if (HostFxShootOffset is Vector3 fx && lateral.LengthSquared > 1e-12f)
                            {
                                Vector3 ahead = lateral.Normalized();
                                muzzlePos = Position + Vector3.Cross(Vector3.UnitY, ahead) * fx.X + Vector3.UnitY * fx.Y + ahead * fx.Z;
                            }
                            if (_chargeEffect != null)
                            {
                                _chargeEffect.SetDrawEnabled(true);
                                _chargeEffect.Transform(_gunVec2, _gunVec1, muzzlePos);
                            }
                            if (_muzzleEffect != null)
                            {
                                _muzzleEffect.SetDrawEnabled(true);
                                _muzzleEffect.Transform(_gunVec2, _gunVec1, muzzlePos);
                            }
                        }
                        if (_frozenGfxTimer > 0)
                        {
                            for (int i = 0; i < _bipedIceModel.Model.Nodes.Count; i++)
                            {
                                _bipedIceModel.Model.Nodes[i].Animation = _bipedModel1.Model.Nodes[i].Animation;
                                _bipedIceTransforms[i] = _bipedModel1.Model.Nodes[i].Animation;
                            }
                            _bipedIceModel.Model.UpdateMatrixStack();
                            UpdateMaterials(_bipedIceModel, recolor: 0);
                            GetDrawItems(_bipedIceModel, _bipedIceModel.Model.Nodes[0], alpha: 1, recolor: 0);
                        }
                    }
                    _modelTransform = transform;
                    if (_health == 0)
                    {
                        DrawDeathParticles();
                    }
                    Flags2 |= PlayerFlags2.DrawnThirdPerson;
                }
                else if (AttachedEnemy == null && !_field6D0 && Hunter != Hunter.Guardian && !HostSeated) // recomp: no gun in the turret's seat (PlayerEchoesSeat.cs)
                {
                    Matrix4 transform = GunDrawTransform; // recomp: turned with a replay's view (HostFirstPersonSeat)
                    UpdateTransforms(_gunModel, transform, HostGunLookRecolor >= 0 && HostGunLookRecolor < _gunModel.Model.Recolors.Count
                        ? HostGunLookRecolor : HostFirstHunt ? 0 : Recolor); // recomp: HostGunLookRecolor; First Hunt's gun has one
                    if (HostGunLightColor is Vector3 light && CurrentWeapon == BeamType.PowerBeam)
                    {
                        // recomp: HostGunLightColor, the strips' own brightness kept
                        foreach (Material material in _gunModel.Model.Materials)
                        {
                            if (material.Name == "lambert10" || material.Name.StartsWith("Glow")) // the strips; the charge glows
                            {
                                Vector3 d = material.CurrentDiffuse;
                                material.CurrentDiffuse = light * MathF.Max(d.X, MathF.Max(d.Y, d.Z));
                            }
                        }
                    }
                    // recomp: see-through (Trace's cloak), the gun's own faces don't stack: on the DS a translucent
                    // pixel never takes a second fragment of its polygon ID, and the whole gun is one ID (RenderItem.HostOneLayer)
                    _scene.HostOneLayerItems = _curAlpha < 1;
                    // recomp: owner 10-28, Trace's cloaked gun keeps its lights (the unlit FullBright/Glow meshes) at
                    // their own alpha, on their own polygon ID so the casing's one-layer stencil doesn't drop them (10-28 round 2:
                    // only the lesion/strip nodes -- the unlit body panels stayed solid red)
                    _hostLightsId = _curAlpha < 1 && Hunter == Hunter.Trace ? _scene.GetNextPolygonId() : -1;
                    GetDrawItems(_gunModel, _gunModel.Model.Nodes[0], _curAlpha);
                    _hostLightsId = -1;
                    _scene.HostOneLayerItems = false;
                    if (Flags1.TestFlag(PlayerFlags1.DrawGunSmoke))
                    {
                        // todo?: the game uses an alternate projection matrix to draw this
                        var drawPos = HostFxMuzzle ?? new Vector3(0, 0, Fixed.ToFloat(Values.MuzzleOffset)); // recomp: HostFxMuzzle
                        drawPos = Matrix.Vec3MultMtx4(drawPos, transform);
                        transform.Row3.Xyz = drawPos;
                        UpdateTransforms(_gunSmokeModel, transform, recolor: 0);
                        GetDrawItems(_gunSmokeModel, _gunSmokeModel.Model.Nodes[0], _smokeAlpha, recolor: 0);
                    }
                }
            }
            if (!IsMainPlayer && !drawBiped)
            {
                if (_chargeEffect != null)
                {
                    _chargeEffect.SetDrawEnabled(false);
                }
                if (_muzzleEffect != null)
                {
                    _muzzleEffect.SetDrawEnabled(false);
                }
            }
            if (GameState.SinglePlayer && IsMainPlayer && _deathCountdown > 0 && _deathCountdown <= 119 / 30f
                && System.Numerics.BitOperations.PopCount(GameState.StorySave.CurrentOctoliths) > 0)
            {
                Matrix4 transform = Matrix4.Identity;
                transform.Row3.Xyz = _lostOctolithDrawPos;
                UpdateTransforms(_octolithSimpleModel, transform, recolor: 0);
                GetDrawItems(_octolithSimpleModel, _octolithSimpleModel.Model.Nodes[0], alpha: 1, recolor: 0);
            }
            DrawShieldWall(); // recomp: the Trooper's barrier (PlayerShield.cs); nothing unless HostShieldWall
            DrawVolumes();
        }

        private void DrawKandenAlt()
        {
            for (int i = 0; i < _kandenSegMtx.Length; i++)
            {
                _altModel.Model.Nodes[i].Animation = _kandenSegMtx[i];
            }
            _altModel.Model.UpdateMatrixStack();
            UpdateMaterials(_altModel, BodyRecolor(_altModel, Recolor)); // recomp: HostLookRecolor
            GetDrawItems(_altModel, _altModel.Model.Nodes[0], _curAlpha);
        }

        // recomp (Scene.HostDrawFreeSim): the scene calls this every frame where Draw would run, drawn or not. Spire's
        // rocks hit players (PlayerCollision.cs CheckAltAttackHit1), so they're placed here with Draw's own conditions
        // minus the drawing-only ones (HostHideMainPlayer); Draw then draws the nodes this posed.
        internal void UpdateDrawFreeSim()
        {
            if (!Flags2.TestFlag(PlayerFlags2.HideModel) && Hunter == Hunter.Spire && Flags2.TestFlag(PlayerFlags2.AltAttack))
            {
                UpdateSpireAltAttack();
            }
        }

        private void UpdateSpireAltAttack()
        {
            Matrix4 transform = GetTransformMatrix(_spireAltFacing, _spireAltUp);
            _altModel.Model.AnimateNodes(index: 0, useNodeTransform: false, transform, Vector3.One, _altModel.AnimInfo);
            _spireRockPosL = _spireAltNodes[0]!.Animation.Row3.Xyz + Position;
            _spireRockPosR = _spireAltNodes[1]!.Animation.Row3.Xyz + Position;
        }

        private void DrawSpireAltAttack()
        {
            _altModel.Model.Nodes[0].Animation = _modelTransform;
            for (int i = 1; i < _altModel.Model.Nodes.Count; i++)
            {
                Node node = _altModel.Model.Nodes[i];
                Matrix4 animation = node.Animation;
                animation.Row3.Xyz += _modelTransform.Row3.Xyz;
                node.Animation = animation;
            }
            _altModel.Model.UpdateMatrixStack();
            UpdateMaterials(_altModel, BodyRecolor(_altModel, Recolor)); // recomp: HostLookRecolor
            GetDrawItems(_altModel, _altModel.Model.Nodes[0], _curAlpha);
        }

        private int _hostLightsId = -1; // recomp: Trace's cloaked gun lights, see Draw

        private void GetDrawItems(ModelInstance inst, Node node, float alpha, int polygonId = -1, int recolor = -1)
        {
            if (alpha <= 0)
            {
                return;
            }
            if (polygonId == -1)
            {
                polygonId = _scene.GetNextPolygonId();
            }
            Model model = inst.Model;
            if (node.Enabled)
            {
                int start = node.MeshId / 2;
                for (int i = 0; i < node.MeshCount; i++)
                {
                    Mesh mesh = model.Meshes[start + i];
                    if (!mesh.Visible)
                    {
                        continue;
                    }
                    Material material = model.Materials[mesh.MaterialId];
                    Vector3 emission = GetEmission(inst, material, mesh.MaterialId);
                    Matrix4 texcoordMatrix = GetTexcoordMatrix(inst, material, mesh.MaterialId, node, recolor);
                    Vector4? color = null;
                    SelectionType selectionType = SelectionType.None;
                    int? bindingOverride = GetBindingOverride(inst, material, mesh.MaterialId);
                    // recomp: _hostLightsId -- the lesions and the glow strips (TraceGun's Lesion*/z_* nodes, unlit); the Body node's
                    // big FullBright panels are unlit too but cloak with the casing
                    bool hostLight = _hostLightsId != -1 && material.Lighting == 0
                        && (node.Name.StartsWith("Lesion") || node.Name.StartsWith("z_"));
                    bool oneLayer = _scene.HostOneLayerItems;
                    if (hostLight)
                    {
                        _scene.HostOneLayerItems = false;
                    }
                    // 10-28 round 3: the lesion spots fade with the cloak like on the DS (BizHawk, cloaked Trace in Combat Hall);
                    // only the strips stay at full alpha
                    float itemAlpha = hostLight && node.Name.StartsWith("z_") ? 1 : alpha;
                    _scene.AddRenderItem(material, hostLight ? _hostLightsId : polygonId, itemAlpha, emission, GetLightInfo(), texcoordMatrix,
                        node.Animation, mesh.ListId, model.NodeMatrixIds.Count, model.MatrixStackValues, color,
                        PaletteOverride, selectionType, node.BillboardMode, _drawScale, bindingOverride);
                    _scene.HostOneLayerItems = oneLayer;
                }
                if (node.ChildIndex != -1)
                {
                    GetDrawItems(inst, model.Nodes[node.ChildIndex], alpha, polygonId, recolor);
                }
            }
            if (node.NextIndex != -1)
            {
                GetDrawItems(inst, model.Nodes[node.NextIndex], alpha, polygonId, recolor);
            }
        }

        protected override int? GetBindingOverride(ModelInstance inst, Material material, int index)
        {
            if (_doubleDmgTimer > 0 && (Hunter != Hunter.Spire || !(inst == _gunModel && index == 0)) && material.Lighting > 0)
            {
                return _doubleDmgBindingId;
            }
            return base.GetBindingOverride(inst, material, index);
        }

        protected override Vector3 GetEmission(ModelInstance inst, Material material, int index)
        {
            if (_doubleDmgTimer > 0 && (Hunter != Hunter.Spire || !(inst == _gunModel && index == 0)) && material.Lighting > 0)
            {
                return Metadata.EmissionGray;
            }
            if (Team == Team.Orange)
            {
                return Metadata.EmissionOrange;
            }
            if (Team == Team.Green)
            {
                return Metadata.EmissionGreen;
            }
            return base.GetEmission(inst, material, index);
        }

        protected override Matrix4 GetTexcoordMatrix(ModelInstance inst, Material material, int materialId,
            Node node, int recolor)
        {
            if (_doubleDmgTimer > 0 && (Hunter != Hunter.Spire || !(inst == _gunModel && materialId == 0))
                && material.Lighting > 0 && node.BillboardMode == BillboardMode.None)
            {
                Texture texture = _doubleDmgModel.Model.Recolors[0].Textures[0];
                // product should start with the upper 3x3 of the node animation result,
                // texgenMatrix should be multiplied with the view matrix if lighting is enabled,
                // and the result should be transposed.
                // these steps are done in the shader so the view matrix can be updated when frame advance is on.
                // strictly speaking, the use_light check in the shader is not the same as what the game does,
                // since the game checks if *any* material in the model uses lighting, but the result is the same.
                Matrix4 texgenMatrix = Matrix4.Identity;
                // in-game, there's only one uniform scale factor for models
                if (inst.Model.Scale.X != 1 || inst.Model.Scale.Y != 1 || inst.Model.Scale.Z != 1)
                {
                    texgenMatrix = Matrix4.CreateScale(inst.Model.Scale) * texgenMatrix;
                }
                Matrix4 product = texgenMatrix;
                product.M12 *= -1;
                product.M13 *= -1;
                product.M22 *= -1;
                product.M23 *= -1;
                product.M32 *= -1;
                product.M33 *= -1;
                ulong frame = _scene.LiveFrames / 2;
                float rotZ = ((int)(16 * ((781874935307L * (53248 * frame) >> 32) + 2048)) >> 20) * (360 / 4096f);
                float rotY = ((int)(16 * ((781874935307L * (26624 * frame) + 0x80000000000) >> 32)) >> 20) * (360 / 4096f);
                var rot = DetMatrix.CreateRotationZ(MathHelper.DegreesToRadians(rotZ));
                rot *= DetMatrix.CreateRotationY(MathHelper.DegreesToRadians(rotY));
                product = rot * product;
                product *= 1.0f / (texture.Width / 2);
                product = new Matrix4(
                    product.Row0 * 16.0f,
                    product.Row1 * 16.0f,
                    product.Row2 * 16.0f,
                    product.Row3
                );
                return product;
            }
            return base.GetTexcoordMatrix(inst, material, materialId, node, recolor);
        }

        private void DrawShadow()
        {
            if (IsMainPlayer && CameraType == CameraType.First)
            {
                return;
            }
            Material material = _trailModel.Model.Materials[1];
            Vector3 point1 = _volume.SpherePosition;
            Vector3 point2 = _volume.SpherePosition.AddY(-10);
            CollisionResult colRes = default;
            if (CollisionDetection.CheckBetweenPoints(point1, point2, TestFlags.None, _scene, ref colRes)
                && colRes.Plane.Y >= Fixed.ToFloat(4))
            {
                float height = point1.Y - colRes.Position.Y;
                if (height < 10)
                {
                    float pct = 1 - height / 10;
                    float alpha = _curAlpha * pct;
                    if (_health == 0)
                    {
                        float respawnTime = _deathCountdown > 0 ? UInt16.MaxValue : RespawnTime;
                        float decrease = 2 * (respawnTime - _respawnTimer) / 2f; // todo: FPS stuff
                        alpha -= decrease;
                    }
                    if (alpha > 0)
                    {
                        Vector3 row1 = Vector3.Cross(colRes.Plane.Xyz, Vector3.UnitZ).Normalized();
                        Vector3 row2 = colRes.Plane.Xyz;
                        var row3 = Vector3.Cross(row1, colRes.Plane.Xyz);
                        row1 *= pct;
                        row2 *= pct;
                        row3 *= pct;
                        float factor = Fixed.ToFloat(100);
                        var row4 = new Vector3(
                            colRes.Position.X + colRes.Plane.X * factor,
                            colRes.Position.Y + colRes.Plane.Y * factor,
                            colRes.Position.Z + colRes.Plane.Z * factor
                        );
                        var transform = new Matrix4(
                            row1.X, row1.Y, row1.Z, 0,
                            row2.X, row2.Y, row2.Z, 0,
                            row3.X, row3.Y, row3.Z, 0,
                            row4.X, row4.Y, row4.Z, 1
                        );
                        Vector3[] uvsAndVerts = ArrayPool<Vector3>.Shared.Rent(8);
                        uvsAndVerts[0] = new Vector3(0, 0, 0);
                        uvsAndVerts[1] = new Vector3(-0.75f, 0.03125f, -0.75f);
                        uvsAndVerts[2] = new Vector3(0, 1, 0);
                        uvsAndVerts[3] = new Vector3(-0.75f, 0.03125f, 0.75f);
                        uvsAndVerts[4] = new Vector3(1, 1, 0);
                        uvsAndVerts[5] = new Vector3(0.75f, 0.03125f, 0.75f);
                        uvsAndVerts[6] = new Vector3(1, 0, 0);
                        uvsAndVerts[7] = new Vector3(0.75f, 0.03125f, -0.75f);
                        int polygonId = _scene.GetNextPolygonId();
                        var color = new Vector3(0, 0, 0);
                        _scene.AddRenderItem(RenderItemType.Particle, alpha, polygonId, color, material.XRepeat, material.YRepeat,
                            material.ScaleS, material.ScaleT, transform, uvsAndVerts, _trailBindingId2);
                    }
                }
            }
        }

        private void DrawMorphBallTrail()
        {
            Debug.Assert(_trailModel != null);
            Material material = _trailModel.Model.Materials[0];
            Debug.Assert(_trailModel.Model.Recolors[0].Textures[material.TextureId].Width == 32);
            float[] matrixStack = ArrayPool<float>.Shared.Rent(16 * _mbTrailSegments);
            for (int i = 0; i < _mbTrailSegments; i++)
            {
                Matrix4 matrix = _mbTrailMatrices[SlotIndex, i];
                matrixStack[i * 16] = matrix.Row0.X;
                matrixStack[i * 16 + 1] = matrix.Row0.Y;
                matrixStack[i * 16 + 2] = matrix.Row0.Z;
                matrixStack[i * 16 + 3] = matrix.Row0.W;
                matrixStack[i * 16 + 4] = matrix.Row1.X;
                matrixStack[i * 16 + 5] = matrix.Row1.Y;
                matrixStack[i * 16 + 6] = matrix.Row1.Z;
                matrixStack[i * 16 + 7] = matrix.Row1.W;
                matrixStack[i * 16 + 8] = matrix.Row2.X;
                matrixStack[i * 16 + 9] = matrix.Row2.Y;
                matrixStack[i * 16 + 10] = matrix.Row2.Z;
                matrixStack[i * 16 + 11] = matrix.Row2.W;
                matrixStack[i * 16 + 12] = matrix.Row3.X;
                matrixStack[i * 16 + 13] = matrix.Row3.Y;
                matrixStack[i * 16 + 14] = matrix.Row3.Z;
                matrixStack[i * 16 + 15] = matrix.Row3.W;
            }
            int count = 0;
            int index = _mbTrailIndices[SlotIndex];
            Vector3[] uvsAndVerts = ArrayPool<Vector3>.Shared.Rent(8 * _mbTrailSegments);
            for (int i = 0; i < _mbTrailSegments; i++)
            {
                // going backwards with wrap-around
                int mtxId1 = index - 1 - i + (index - 1 - i < 0 ? _mbTrailSegments : 0);
                int mtxId2 = mtxId1 - 1 + (mtxId1 - 1 < 0 ? _mbTrailSegments : 0);
                float alpha1 = _mbTrailAlphas[SlotIndex, mtxId1];
                float alpha2 = _mbTrailAlphas[SlotIndex, mtxId2];
                if (alpha1 > 0 && alpha2 > 0)
                {
                    float uvS1 = (31 - (int)(alpha1 * 31)) / 32f;
                    float uvS2 = (31 - (int)(alpha2 * 31)) / 32f;
                    uvsAndVerts[i * 8] = new Vector3(uvS1, 0, mtxId1);
                    uvsAndVerts[i * 8 + 1] = new Vector3(0, 0.375f, 0);
                    uvsAndVerts[i * 8 + 2] = new Vector3(uvS1, 1, mtxId1);
                    uvsAndVerts[i * 8 + 3] = new Vector3(0, -0.375f, 0);
                    uvsAndVerts[i * 8 + 4] = new Vector3(uvS2, 1, mtxId2);
                    uvsAndVerts[i * 8 + 5] = new Vector3(0, -0.375f, 0);
                    uvsAndVerts[i * 8 + 6] = new Vector3(uvS2, 0, mtxId2);
                    uvsAndVerts[i * 8 + 7] = new Vector3(0, 0.375f, 0);
                    count++;
                }
            }
            if (count > 0)
            {
                var color = HostTrailColor ?? new Vector3(1, 27 / 31f, 11 / 31f);
                _scene.AddRenderItem(RenderItemType.TrailStack, _scene.GetNextPolygonId(), color, material.XRepeat, material.YRepeat,
                    material.ScaleS, material.ScaleT, _mbTrailSegments, matrixStack, uvsAndVerts, count, _trailBindingId1);
            }
            ArrayPool<float>.Shared.Return(matrixStack);
        }

        // todo: FPS stuff
        private void DrawDeathParticles()
        {
            // get current percentage through the first 1/3 of the respawn cooldown
            float timePct = 1 - ((_respawnTimer - (2 / 3f * RespawnTime)) / (1 / 3f * RespawnTime));
            if (timePct < 0 || timePct > 1)
            {
                return;
            }
            float scale = timePct / 2 + 0.1f;
            // todo: the angle stuff could be removed
            float angle = MathF.Sin(MathHelper.DegreesToRadians(270 - 90 * timePct));
            float sin270 = MathF.Sin(MathHelper.DegreesToRadians(270));
            float sin180 = MathF.Sin(MathHelper.DegreesToRadians(180));
            float offset = (angle - sin270) / (sin180 - sin270);
            for (int i = 1; i < _bipedModel1.Model.Nodes.Count; i++)
            {
                Node node = _bipedModel1.Model.Nodes[i];
                var nodePos = new Vector3(node.Animation.Row3);
                nodePos.Y += offset;
                if (node.ChildIndex != -1)
                {
                    Debug.Assert(node.ChildIndex > 0);
                    var childPos = new Vector3(_bipedModel1.Model.Nodes[node.ChildIndex].Animation.Row3);
                    childPos.Y += offset;
                    for (int j = 1; j < 5; j++)
                    {
                        var segPos = new Vector3(
                            nodePos.X + j * (childPos.X - nodePos.X) / 5,
                            nodePos.Y + j * (childPos.Y - nodePos.Y) / 5,
                            nodePos.Z + j * (childPos.Z - nodePos.Z) / 5
                        );
                        segPos += (segPos - Position).Normalized() * offset;
                        _scene.AddSingleParticle(SingleType.Death, segPos, Vector3.One, 1 - timePct, scale);
                    }
                }
                if (node.NextIndex != -1)
                {
                    Debug.Assert(node.NextIndex > 0);
                    var nextPos = new Vector3(_bipedModel1.Model.Nodes[node.NextIndex].Animation.Row3);
                    nextPos.Y += offset;
                    for (int j = 1; j < 5; j++)
                    {
                        var segPos = new Vector3(
                            nodePos.X + j * (nextPos.X - nodePos.X) / 5,
                            nodePos.Y + j * (nextPos.Y - nodePos.Y) / 5,
                            nodePos.Z + j * (nextPos.Z - nodePos.Z) / 5
                        );
                        segPos += (segPos - Position).Normalized() * offset;
                        _scene.AddSingleParticle(SingleType.Death, segPos, Vector3.One, 1 - timePct, scale);
                    }
                }
                nodePos += (nodePos - Position).Normalized() * offset;
                _scene.AddSingleParticle(SingleType.Death, nodePos, Vector3.One, 1 - timePct, scale);
            }
        }

        private void DrawVolumes()
        {
            if (_scene.ShowVolumes == VolumeDisplay.KillPlane)
            {
                if (!IsAltForm && !IsMorphing && !IsUnmorphing)
                {
                    AddVectorItem(_gunDrawPos, _aimVec * 3, Vector3.UnitZ);
                    AddDotItem(_muzzlePos, Vector3.UnitX);
                    AddDotItem(_muzzlePos + (_aimPosition - _muzzlePos).Normalized() * 3, Vector3.UnitX);
                }
                AddVectorItem(_position, _facingVector * 3, Vector3.UnitY);
            }
        }

        public override void GetDrawInfo()
        {
        }
    }
}
