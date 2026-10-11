using System;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: the Trooper's Shield Wall (docs/CHARACTERS_AND_SKINS.md, D11). Host-guarded: off unless the match host turns
    // it on for a seat (HostShieldWall, a Trooper seat in a local match) before the match loads; with it off nothing here
    // runs and the player is MphRead's own. Held button (Zoom) plants an energy barrier in front of him (not while the
    // Imperialist is equipped: then the button zooms as usual, and switching to it drops the barrier):
    //   - locked: no walking, jumping or morphing while it's up; his aim stays inside a firing slit around the planted
    //     direction (ShieldConeYaw left/right, ShieldConePitch up/down), so he can't hit what's outside it
    //   - shots from the front arc (ShieldArcCos: 120 degrees) go into the barrier's health; sides and back hit him
    //   - the barrier breaks when its health is spent: a short stagger (no moving, firing or planting; the hunter's own
    //     DamageFront clip), then a longer cooldown than after letting go
    //   - bots: a trooper bot plants when shot at from the front by the player it's fighting; any bot whose target
    //     is planted and facing it strafes around to the side instead of shooting into the barrier
    // Every size and angle here is sim code, never measured from a model (the hitbox rule: the HD skin, a look-alike
    // fallback and the DS model are all hit the same).
    public partial class PlayerEntity
    {
        // set by the match host for this seat before the match loads (default off: MphRead's own player)
        public bool HostShieldWall { get; set; }
        // any seat in this match has it (bots then check for planted troopers to walk around); set with the seats
        public static bool HostAnyShieldWall { get; set; }
        // drawing only: the ForceField model's palette for this seat's barrier (the skin's colour, set by the app)
        public int HostShieldRecolor { get; set; }

        public const int ShieldMaxHealth = 60;
        private const int _shieldCooldownDrop = 6 * 60; // frames (60 Hz)
        private const int _shieldCooldownBreak = 10 * 60;
        private const int _shieldStaggerTime = 40; // ~0.67 s
        private const int _shieldBotHoldTime = 150; // a bot keeps it up ~2.5 s at most
        private const float _shieldArcCos = 0.5f; // the front 120 degrees: |angle| <= 60
        private const float _shieldConeYaw = 20; // degrees
        private const float _shieldConePitch = 15;
        // the barrier as drawn: a ForceField plane this far in front of him, this big (drawing only)
        private const float _shieldDrawDistance = 0.75f;
        private const float _shieldDrawWidth = 1.7f;
        private const float _shieldDrawHeight = 1.9f;

        private bool _shieldUp = false;
        private int _shieldHealth = ShieldMaxHealth;
        private int _shieldCooldown = 0;
        private int _shieldStagger = 0;
        private int _shieldBotHold = 0;
        private Vector3 _shieldFacing = -Vector3.UnitZ; // horizontal unit vector, set on planting
        private float _shieldPitch = 0; // _aimY when planted
        private float _shieldDrawAlpha = 0; // fades with the barrier (sim-stepped, drawing only)
        private int _shieldHitFlash = 0;
        private ModelInstance? _shieldModel = null;

        public bool ShieldUp => _shieldUp;
        public int ShieldHealth => _shieldHealth;
        public int ShieldCooldown => _shieldCooldown;
        public bool ShieldStaggered => _shieldStagger > 0;
        public Vector3 ShieldFacing => _shieldFacing;
        // counts for tests (-shieldtest)
        public int ShieldPlants { get; private set; }
        public int ShieldBlocks { get; private set; }
        public int ShieldBlockedDamage { get; private set; }
        public int ShieldBreaks { get; private set; }
        public int ShieldFlankFrames { get; private set; }

        // locked in place: the barrier is up, or he's staggering after it broke
        private bool ShieldWallLocked => HostShieldWall && (_shieldUp || _shieldStagger > 0);
        private bool ShieldWallStaggered => HostShieldWall && _shieldStagger > 0;

        // Initialize: the barrier's model (only for a seat that has it)
        private void ShieldWallInit()
        {
            ShieldWallReset();
            if (HostShieldWall)
            {
                _shieldModel = Read.GetModelInstance("ForceField");
                _shieldModel.SetAnimation(0);
                _models.Add(_shieldModel);
            }
        }

        private void ShieldWallReset()
        {
            _shieldUp = false;
            _shieldHealth = ShieldMaxHealth;
            _shieldCooldown = 0;
            _shieldStagger = 0;
            _shieldBotHold = 0;
            _shieldDrawAlpha = 0;
            _shieldHitFlash = 0;
        }

        // every frame, from ProcessInput (bots' controls are already set for this frame)
        private void ShieldWallTick()
        {
            if (HostShieldWall)
            {
                if (_health == 0)
                {
                    ShieldWallReset();
                    return;
                }
                if (_shieldUp && (IsAltForm || IsMorphing || _frozenTimer > 0 || !Flags1.TestFlag(PlayerFlags1.Grounded)))
                {
                    ShieldDrop(broken: false);
                }
                if (_shieldStagger > 0)
                {
                    _shieldStagger--;
                }
                if (_shieldCooldown > 0 && !_shieldUp)
                {
                    _shieldCooldown--;
                }
                if (_shieldHitFlash > 0)
                {
                    _shieldHitFlash--;
                }
                float target = _shieldUp ? 1 : 0;
                _shieldDrawAlpha += Math.Clamp(target - _shieldDrawAlpha, -1 / 8f, 1 / 8f);
            }
            if (IsBot && _health > 0 && !IsAltForm)
            {
                ShieldWallFlank();
            }
        }

        // ProcessBiped, after aiming and before walking/jumping: plant or drop, then the firing slit
        private void ShieldWallInput()
        {
            bool want;
            if (EquipWeapon.Flags.TestFlag(WeaponFlags.CanZoom))
            {
                want = false; // the Imperialist: the zoom button zooms, no barrier (owner, 2026-10-08)
            }
            else if (IsBot)
            {
                want = ShieldBotWants();
            }
            else
            {
                want = Controls.Zoom.IsDown;
            }
            if (_shieldUp && !want)
            {
                ShieldDrop(broken: false);
            }
            else if (!_shieldUp && want && (Controls.Zoom.IsPressed || IsBot) && _shieldCooldown == 0 && _shieldStagger == 0
                && Flags1.TestFlag(PlayerFlags1.Standing) && !IsAltForm && !IsMorphing && !IsUnmorphing)
            {
                ShieldPlant();
            }
            if (_shieldUp)
            {
                ShieldClampAim();
            }
        }

        private void ShieldPlant()
        {
            var facing = new Vector3(_gunVec1.X, 0, _gunVec1.Z);
            if (facing.LengthSquared < 1e-6f)
            {
                facing = new Vector3(_field70, 0, _field74);
            }
            _shieldFacing = facing.Normalized();
            _shieldPitch = Math.Clamp(_aimY, -85 + _shieldConePitch, 85 - _shieldConePitch);
            _shieldUp = true;
            _shieldHealth = ShieldMaxHealth;
            _shieldBotHold = _shieldBotHoldTime;
            ShieldPlants++;
            _soundSource.PlaySfx(SfxId.FORCEFIELD_APPEAR);
        }

        private void ShieldDrop(bool broken)
        {
            _shieldUp = false;
            _shieldBotHold = 0;
            if (broken)
            {
                _shieldCooldown = _shieldCooldownBreak;
                _shieldStagger = _shieldStaggerTime;
                ShieldBreaks++;
                Speed = Speed.WithX(0).WithZ(0);
                if (!IsMorphing)
                {
                    SetBipedAnimation(PlayerAnimation.DamageFront, AnimFlags.NoLoop, setBiped1: true, setBiped2: true, setIfMorphing: false);
                }
                _soundSource.PlaySfx(SfxId.SHOTGUN_BREAK_FREEZE);
            }
            else
            {
                _shieldCooldown = _shieldCooldownDrop;
                _soundSource.PlaySfx(SfxId.GEN_OFF);
            }
        }

        // the firing slit: yaw within _shieldConeYaw of the planted direction, pitch within _shieldConePitch of the
        // planted pitch (vector math + sqrt only on the yaw side, MphRead's own aim pitch for the rest)
        private void ShieldClampAim()
        {
            bool clamped = false;
            float pitch = _aimY;
            if (pitch > _shieldPitch + _shieldConePitch || pitch < _shieldPitch - _shieldConePitch)
            {
                pitch = Math.Clamp(pitch, _shieldPitch - _shieldConePitch, _shieldPitch + _shieldConePitch);
                clamped = true;
            }
            var h = new Vector3(_gunVec1.X, 0, _gunVec1.Z);
            if (h.LengthSquared < 1e-6f)
            {
                h = _shieldFacing;
                clamped = true;
            }
            h = h.Normalized();
            Vector3 f = _shieldFacing;
            float cos = Vector3.Dot(h, f);
            float coneCos = MathF.Cos(MathHelper.DegreesToRadians(_shieldConeYaw));
            if (cos < coneCos)
            {
                float coneSin = MathF.Sin(MathHelper.DegreesToRadians(_shieldConeYaw));
                // which side of the planted direction he's turned to (the y of f x h)
                float side = f.Z * h.X - f.X * h.Z;
                var perp = new Vector3(-f.Z, 0, f.X); // f turned a quarter: the side with side > 0
                h = f * coneCos + perp * (side >= 0 ? coneSin : -coneSin);
                clamped = true;
            }
            if (clamped)
            {
                _aimY = pitch;
                float rad = MathHelper.DegreesToRadians(pitch);
                float c = MathF.Cos(rad);
                _gunVec1 = new Vector3(h.X * c, MathF.Sin(rad), h.Z * c).Normalized();
                _aimPosition = CameraInfo.Position + _gunVec1 * Fixed.ToFloat(Values.AimDistance);
                UpdateAimFacing();
            }
        }

        // TakeDamage, after the damage is scaled: true = the barrier took it (and he doesn't)
        private bool ShieldWallAbsorbs(uint damage, DamageFlags flags, Vector3? direction, EntityBase? source)
        {
            if (!_shieldUp || damage == 0 || flags.TestFlag(DamageFlags.Death))
            {
                return false;
            }
            Vector3 from;
            if (source is BeamProjectileEntity beam)
            {
                from = beam.Velocity.LengthSquared > 1e-8f ? -beam.Velocity : beam.Position - Position;
            }
            else if (source != null)
            {
                source.GetPosition(out Vector3 sourcePos);
                from = sourcePos - Position;
            }
            else if (direction.HasValue)
            {
                from = -direction.Value;
            }
            else
            {
                return false; // burn ticks and the like: no direction, not stopped
            }
            from.Y = 0;
            if (from.LengthSquared < 1e-8f || Vector3.Dot(from.Normalized(), _shieldFacing) < _shieldArcCos)
            {
                return false;
            }
            ShieldBlocks++;
            ShieldBlockedDamage += (int)damage;
            _shieldHitFlash = 6;
            if (damage >= _shieldHealth)
            {
                _shieldHealth = 0;
                ShieldDrop(broken: true);
            }
            else
            {
                _shieldHealth -= (int)damage;
            }
            return true;
        }

        // a trooper bot: plant when the player it's fighting shoots it from the front, hold while that player stays in
        // the slit (for a while), let go when they leave it or die
        private bool ShieldBotWants()
        {
            PlayerEntity? target = AiData.HostTargetPlayer;
            if (_shieldUp)
            {
                if (_shieldBotHold > 0)
                {
                    _shieldBotHold--;
                }
                return _shieldBotHold > 0 && target != null && target._health > 0 && ShieldInFront(target, 0.7f);
            }
            return target != null && target._health > 0 && _timeSinceDamage < 20 && ShieldInFront(target, 0.9f)
                && (target.Position - Position).LengthSquared < 30 * 30;
        }

        // the other player is ahead of where he faces (cos of the horizontal angle at least minCos)
        private bool ShieldInFront(PlayerEntity other, float minCos)
        {
            Vector3 to = other.Position - Position;
            to.Y = 0;
            Vector3 facing = _shieldUp ? _shieldFacing : new Vector3(_gunVec1.X, 0, _gunVec1.Z);
            if (to.LengthSquared < 1e-6f || facing.LengthSquared < 1e-6f)
            {
                return false;
            }
            return Vector3.Dot(to.Normalized(), facing.Normalized()) >= minCos;
        }

        // any bot: its target has the barrier up and faces it -> strafe out of the barrier's front instead of shooting
        // into it (the side away from the barrier's middle)
        private void ShieldWallFlank()
        {
            PlayerEntity? target = AiData.HostTargetPlayer;
            if (target == null || !target.HostShieldWall || !target._shieldUp || target._health == 0)
            {
                return;
            }
            Vector3 rel = Position - target.Position;
            rel.Y = 0;
            if (rel.LengthSquared > 30 * 30 || rel.LengthSquared < 1e-6f
                || Vector3.Dot(rel.Normalized(), target._shieldFacing) < _shieldArcCos)
            {
                return;
            }
            Vector3 lateral = rel - target._shieldFacing * Vector3.Dot(rel, target._shieldFacing);
            var right = new Vector3(-_field78, 0, -_field7C); // MoveRightLeft's direction for "right"
            bool goRight = Vector3.Dot(right, lateral) >= 0;
            Controls.MoveRight.IsDown = goRight;
            Controls.MoveLeft.IsDown = !goRight;
            ShieldFlankFrames++;
        }

        // Draw: the barrier, a ForceField plane in front of him (MPH's own energy field; drawing only)
        private void DrawShieldWall()
        {
            if (!HostShieldWall || _shieldModel == null || _shieldDrawAlpha <= 0 || _health == 0)
            {
                return;
            }
            if (!IsMainPlayer && !IsVisible(NodeRef))
            {
                return;
            }
            float alpha = _shieldDrawAlpha * (0.45f + 0.55f * _shieldHealth / ShieldMaxHealth);
            if (_shieldHitFlash > 0)
            {
                alpha = Math.Min(1, alpha + 0.35f);
            }
            if (IsMainPlayer && CameraType == CameraType.First)
            {
                alpha *= 0.5f; // he looks through it
            }
            float bottom = Fixed.ToFloat(Values.MinPickupHeight);
            Vector3 position = Position + _shieldFacing * _shieldDrawDistance;
            position.Y += bottom + _shieldDrawHeight / 2;
            // the ForceField model is a 2 x 2 square around its origin (as ForceFieldEntity scales it by half sizes)
            Matrix4 transform = Matrix4.CreateScale(_shieldDrawWidth / 2, _shieldDrawHeight / 2, 1)
                * GetTransformMatrix(_shieldFacing, Vector3.UnitY, position);
            UpdateTransforms(_shieldModel, transform, HostShieldRecolor);
            GetDrawItems(_shieldModel, _shieldModel.Model.Nodes[0], alpha, recolor: HostShieldRecolor);
        }
    }
}
