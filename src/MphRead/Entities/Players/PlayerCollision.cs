using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Formats;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    public struct DamageResult
    {
        public bool TakeDamage;
        public uint Damage;
    }

    public partial class PlayerEntity
    {
        // test tools: one line per Sylux ride-spring contact (-althoptest restscan ALTHOP_TRACE)
        public static Action<string>? AltSpringTrace { get; set; }
        // campaign Sylux: last step's ceiling test with the ride spring's lift added (the ball rests on the floor under it)
        private bool _altSpringCeiling = false;
        public static bool AltNarrowOff { get; set; } = Environment.GetEnvironmentVariable("ALTNARROW_OFF") == "1";

        // the narrow ball's reach along a direction with height ny: sqrt(a^2 (1 - ny^2) + b^2 ny^2), a = a hair under Samus's
        // ball radius (Ice Hive's tunnel lip is 0.99 wide: at Samus's 1.0 the ball wedged between lip and back wall and crept up
        // the floor fillet), b = this ball's own
        private const float NarrowHalfWidth = 0.48f;
        private int NarrowRadius(float ny)
        {
            float a = NarrowHalfWidth, b = Fixed.ToFloat(Values.AltColRadius);
            float y2 = Math.Clamp(ny * ny, 0, 1);
            return Fixed.ToInt(MathF.Sqrt(a * a * (1 - y2) + b * b * y2));
        }
        private const float AltSpringLift = 0.15f;
        private EntityCollision? _collidedEntCol = null;
        private EntityCollision? _standingEntCol = null;

        // todo: visualize EVERYTHING
        private void CheckPlayerCollision()
        {
            foreach (PlayerEntity other in _scene.GetPlayerEntities())
            {
                if (!other.LoadFlags.TestFlag(LoadFlags.Active) || other.Health == 0)
                {
                    continue;
                }
                // (recomp: the Trooper's drone doesn't stand on his own standing body: HostDroneSwitchRule, PlayerScoutDrone.cs)
                if (Flags2.TestFlag(PlayerFlags2.Halfturret) && !(other == this && DroneIgnoresOwnBody))
                {
                    Vector3 toTurret = other.Volume.SpherePosition - _halfturret.Position;
                    float radius = other.Volume.SphereRadius + 0.45f + 0.1f;
                    if (toTurret.LengthSquared <= radius * radius)
                    {
                        CollisionResult turretRes = default;
                        if (toTurret.Y < 0)
                        {
                            toTurret.Y = 0;
                        }
                        Debug.Assert(toTurret != Vector3.Zero);
                        toTurret = toTurret.Normalized();
                        turretRes.Field0 = 0;
                        turretRes.Plane = new Vector4(toTurret);
                        toTurret *= 0.45f;
                        toTurret += _halfturret.Position;
                        turretRes.Plane.W = Vector3.Dot(toTurret, turretRes.Plane.Xyz);
                        other.HandleCollision(turretRes);
                        other.NetHostMoved(NetHostMove.Body); // recomp: the standing body pushed it (PlayerNetPosition.cs)
                        if (other != this)
                        {
                            if (other.Flags1.TestFlag(PlayerFlags1.Boosting))
                            {
                                TakeDamage(other._boostDamage, DamageFlags.NoDmgInvuln | DamageFlags.Halfturret, other.Speed, other);
                                other.EndAltAttack();
                            }
                            if (other._deathaltTimer > 0)
                            {
                                TakeDamage(200, DamageFlags.Deathalt | DamageFlags.NoDmgInvuln | DamageFlags.Halfturret, other.Speed, other);
                            }
                            CheckAltAttackHit2(other, this, halfturret: true);
                        }
                    }
                    CheckAltAttackHit1(other, this, halfturret: true);
                }
                if (other == this)
                {
                    continue;
                }
                Vector3 between = _volume.SpherePosition - other.Volume.SpherePosition;
                float distSqr = between.LengthFast;
                if (distSqr == 0)
                {
                    between = Vector3.UnitX;
                    distSqr = 1;
                }
                float radii = _volume.SphereRadius + other.Volume.SphereRadius;
                if (distSqr < radii * radii)
                {
                    float dist = MathF.Sqrt(distSqr);
                    between /= dist;
                    float dot = Vector3.Dot(Speed, between);
                    Speed -= between * dot;
                    Vector3 posAdd = between * (radii - dist);
                    Position += posAdd;
                    _volume = CollisionVolume.Move(_volume, _volume.SpherePosition + posAdd);
                    float kbAccel = Fixed.ToFloat(Values.AltAttackKnockbackAccel);
                    if (Hunter == Hunter.Noxus && IsAltForm)
                    {
                        other.Acceleration = new Vector3(between.X * -kbAccel, 0, between.Z * -kbAccel);
                        other._accelerationTimer = (ushort)(Values.AltAttackKnockbackTime * 2); // todo: FPS stuff
                        other.NetHostMoved(NetHostMove.Push); // recomp: (PlayerNetPosition.cs)
                    }
                    if (other.Hunter == Hunter.Noxus && other.IsAltForm)
                    {
                        Acceleration = new Vector3(between.X * kbAccel, 0, between.Z * kbAccel);
                        _accelerationTimer = (ushort)(Values.AltAttackKnockbackTime * 2); // todo: FPS stuff
                        NetHostMoved(NetHostMove.Push); // recomp: (PlayerNetPosition.cs)
                    }
                    if (Flags1.TestFlag(PlayerFlags1.Boosting))
                    {
                        other.TakeDamage(_boostDamage, DamageFlags.NoDmgInvuln, Speed, this);
                        EndAltAttack();
                    }
                    if (other.Flags1.TestFlag(PlayerFlags1.Boosting))
                    {
                        TakeDamage(other._boostDamage, DamageFlags.NoDmgInvuln, other.Speed, other);
                        other.EndAltAttack();
                    }
                    if (_deathaltTimer > 0)
                    {
                        other.TakeDamage(200, DamageFlags.Deathalt | DamageFlags.NoDmgInvuln, Speed, this);
                    }
                    if (other._deathaltTimer > 0)
                    {
                        TakeDamage(200, DamageFlags.Deathalt | DamageFlags.NoDmgInvuln, other.Speed, other);
                    }
                    CheckAltAttackHit2(this, other, halfturret: false);
                    CheckAltAttackHit2(other, this, halfturret: false);
                }
                CheckAltAttackHit1(this, other, halfturret: false);
            }
        }

        // todo: more visualization
        private static void CheckAltAttackHit1(PlayerEntity attacker, PlayerEntity target, bool halfturret)
        {
            // the game assumes the hunter is noxus based on the alt attack timer
            if (attacker.Hunter == Hunter.Spire && attacker.Flags2.TestFlag(PlayerFlags2.AltAttack))
            {
                CollisionResult unused = default;
                bool hit = false;
                if (halfturret)
                {
                    var otherVolume = new CollisionVolume(target.Halfturret.Position, 0.45f);
                    hit = CollisionDetection.CheckSphereOverlapVolume(otherVolume, attacker._spireRockPosL, 0.5f, ref unused)
                        || CollisionDetection.CheckSphereOverlapVolume(otherVolume, attacker._spireRockPosR, 0.5f, ref unused);
                }
                else
                {
                    hit = CollisionDetection.CheckSphereOverlapVolume(target.Volume, attacker._spireRockPosL, 0.5f, ref unused)
                        || CollisionDetection.CheckSphereOverlapVolume(target.Volume, attacker._spireRockPosR, 0.5f, ref unused);
                }
                // recomp (HostBotTickPace): the rocks ignore damage invulnerability, so vanilla's once-per-tick check hurts once
                // per tick; MphRead checks every 60 Hz frame (twice the damage)
                if (hit && (!HostBotTickPace || attacker._scene.FrameCount % 2 == 0))
                {
                    Vector3 dir = Vector3.Zero;
                    if (!halfturret)
                    {
                        float x = target.Position.X - attacker.Position.X;
                        float z = target.Position.Z - attacker.Position.Z;
                        float factor = MathF.Sqrt(x * x + z * z) * 4;
                        dir.X = x / factor;
                        dir.Z = z / factor;
                    }
                    ushort damage = attacker.Values.AltAttackDamage;
                    if (attacker.IsBot && GameState.SinglePlayer)
                    {
                        int encounter = GameState.EncounterState[attacker.SlotIndex];
                        if (encounter == 1 || encounter == 3 || encounter == 4
                            || encounter == 0 && attacker.BotLevel == 0)
                        {
                            damage = 2;
                        }
                        else if (encounter != 0 || attacker.BotLevel < 2) // in-game: level !=2
                        {
                            damage = 3;
                        }
                        else
                        {
                            damage = 5;
                        }
                    }
                    DamageFlags flags = DamageFlags.NoSfx | DamageFlags.NoDmgInvuln;
                    if (halfturret)
                    {
                        flags |= DamageFlags.Halfturret;
                    }
                    target.TakeDamage(damage, flags, dir, attacker);
                    attacker._soundSource.PlaySfx(SfxId.SPIRE_ALT_ATTACK_HIT);
                }
            }
            else if (attacker.Hunter == Hunter.Noxus && attacker._altAttackTime >= attacker.Values.AltAttackStartup * 2) // todo: FPS stuff
            {
                Vector3 between;
                if (halfturret)
                {
                    between = target.Halfturret.Position - attacker.Volume.SpherePosition;
                }
                else
                {
                    between = target.Volume.SpherePosition - attacker.Volume.SpherePosition;
                }
                float radius = target.Volume.SphereRadius;
                if (between.Y > -radius && between.Y < radius)
                {
                    float hMagSqr = between.X * between.X + between.Z * between.Z;
                    float radAddSqr = radius + 1.8f;
                    radAddSqr *= radAddSqr;
                    if (hMagSqr < radAddSqr)
                    {
                        float factor = MathF.Sqrt(hMagSqr) * 8;
                        var dir = new Vector3(between.X / factor, 0, between.Z / factor);
                        target.Acceleration = dir;
                        target._accelerationTimer = 8 * 2; // todo: FPS stuff
                        target.NetHostMoved(NetHostMove.Push); // recomp: (PlayerNetPosition.cs)
                        ushort damage = attacker.Values.AltAttackDamage;
                        if (attacker.IsBot && GameState.SinglePlayer)
                        {
                            int encounter = GameState.EncounterState[attacker.SlotIndex];
                            if (encounter == 1 || encounter == 3 || encounter == 4
                                || encounter == 0 && attacker.BotLevel == 0)
                            {
                                damage = 10;
                            }
                            else if (encounter != 0 || attacker.BotLevel < 2) // in-game: level !=2
                            {
                                damage = 12;
                            }
                            else
                            {
                                damage = 15;
                            }
                        }
                        DamageFlags flags = DamageFlags.NoSfx | DamageFlags.NoDmgInvuln;
                        if (halfturret)
                        {
                            flags |= DamageFlags.Halfturret;
                        }
                        target.TakeDamage(damage, flags, dir, attacker);
                        attacker._soundSource.PlaySfx(SfxId.NOX_ALT_ATTACK_HIT);
                        target._scene.SpawnEffect(235, Vector3.UnitX, Vector3.UnitY, target.Position); // noxHit
                        attacker.EndAltAttack();
                    }
                }
            }
        }

        private static void CheckAltAttackHit2(PlayerEntity attacker, PlayerEntity target, bool halfturret)
        {
            if (attacker.Hunter != Hunter.Trace && attacker.Hunter != Hunter.Weavel
                || !attacker.Flags2.TestFlag(PlayerFlags2.AltAttack))
            {
                return;
            }
            // the game has a calculation + condition which is unnecessary since it also passes if Trace or Weavel (always true)
            // --> possibly meant to account for usages where this is called outside the already passed collision check
            Vector3 dir = Vector3.Zero;
            if (attacker.Hunter == Hunter.Trace)
            {
                dir.X = attacker.Speed.X;
                dir.Z = attacker.Speed.Z;
            }
            else
            {
                dir.X = attacker._field70;
                dir.Z = attacker._field74;
            }
            if (!halfturret)
            {
                float kbAccel = Fixed.ToFloat(attacker.Values.AltAttackKnockbackAccel);
                target.Acceleration = new Vector3(dir.X * kbAccel, -0.1f, dir.Z * kbAccel);
                target._accelerationTimer = (ushort)(attacker.Values.AltAttackKnockbackTime * 2); // todo: FPS stuff
                target.NetHostMoved(NetHostMove.Push); // recomp: (PlayerNetPosition.cs)
            }
            ushort damage = attacker.Values.AltAttackDamage;
            if (attacker.IsBot && GameState.SinglePlayer)
            {
                int encounter = GameState.EncounterState[attacker.SlotIndex];
                if (encounter == 1 || encounter == 3 || encounter == 4)
                {
                    damage = (ushort)(attacker.Hunter == Hunter.Trace ? 15 : 10);
                }
                else if (encounter == 0 && attacker.BotLevel == 0)
                {
                    damage = 10;
                }
                else if (encounter != 0 || attacker.BotLevel < 2) // in-game: level !=2
                {
                    damage = 15;
                }
                else
                {
                    damage = (ushort)(attacker.Hunter == Hunter.Trace ? 20 : 18);
                }
            }
            DamageFlags flags = DamageFlags.NoSfx | DamageFlags.NoDmgInvuln;
            if (halfturret)
            {
                flags |= DamageFlags.Halfturret;
            }
            target.TakeDamage(damage, flags, dir, attacker);
            SfxId sfx = attacker.Hunter == Hunter.Weavel ? SfxId.WEAVEL_ALT_ATTACK_HIT : SfxId.TRACE_ALT_ATTACK_HIT;
            attacker._soundSource.PlaySfx(sfx);
            attacker.EndAltAttack();
        }

        // note: the game does not have the functionality of alt attacks hitting enemies or doors
        public bool CheckAltAttackHitEnemy1(EnemyInstanceEntity target)
        {
            if (target.Flags.TestAny(EnemyFlags.Invincible | EnemyFlags.NoBombDamage))
            {
                return false;
            }
            // the game assumes the hunter is noxus based on the alt attack timer
            if (Hunter == Hunter.Spire && Flags2.TestFlag(PlayerFlags2.AltAttack))
            {
                CollisionResult unused = default;
                if (CollisionDetection.CheckSphereOverlapVolume(target.HurtVolume, _spireRockPosL, 0.5f, ref unused)
                    || CollisionDetection.CheckSphereOverlapVolume(target.HurtVolume, _spireRockPosR, 0.5f, ref unused))
                {
                    target.TakeDamage(Values.AltAttackDamage, this);
                    _soundSource.PlaySfx(SfxId.SPIRE_ALT_ATTACK_HIT);
                    return true;
                }
            }
            else if (Hunter == Hunter.Noxus && _altAttackTime >= Values.AltAttackStartup * 2) // todo: FPS stuff
            {
                (Vector3 hurtPos, float radius) = HurtSphere(target.HurtVolume);
                Vector3 between = hurtPos - Volume.SpherePosition;
                if (between.Y > -radius && between.Y < radius)
                {
                    float hMagSqr = between.X * between.X + between.Z * between.Z;
                    float radAddSqr = radius + 1.8f;
                    radAddSqr *= radAddSqr;
                    if (hMagSqr < radAddSqr)
                    {
                        target.TakeDamage(Values.AltAttackDamage, this);
                        _soundSource.PlaySfx(SfxId.NOX_ALT_ATTACK_HIT);
                        _scene.SpawnEffect(235, Vector3.UnitX, Vector3.UnitY, target.Position); // noxHit
                        EndAltAttack();
                        return true;
                    }
                }
            }
            return false;
        }

        // recomp: the enemy alt attacks read the target's hurt volume as a sphere (MPH's enemies' all are); a host's
        // cylinder or box (Echoes' crates) shares those fields with other values (a cylinder's "sphere" is its axis at
        // (0, 1, 0) with its x position as the radius: every crate in reach from anywhere), so it's its centre and the
        // radius that holds it
        private static (Vector3 Position, float Radius) HurtSphere(CollisionVolume v)
        {
            return v.Type switch
            {
                VolumeType.Cylinder => (v.GetCenter(), MathF.Max(v.CylinderRadius, v.CylinderDot / 2)),
                VolumeType.Box => (v.GetCenter(), new Vector3(v.BoxDot1, v.BoxDot2, v.BoxDot3).Length / 2),
                _ => (v.SpherePosition, v.SphereRadius),
            };
        }

        public bool CheckAltAttackHitEnemy2(EnemyInstanceEntity target)
        {
            if (target.Flags.TestAny(EnemyFlags.Invincible | EnemyFlags.NoBombDamage))
            {
                return false;
            }
            if ((Hunter == Hunter.Trace || Hunter == Hunter.Weavel) && Flags2.TestFlag(PlayerFlags2.AltAttack))
            {
                (Vector3 hurtPos, float hurtRadius) = HurtSphere(target.HurtVolume);
                Vector3 between = _volume.SpherePosition - hurtPos;
                float distSqr = between.LengthFast;
                if (distSqr == 0)
                {
                    between = Vector3.UnitX;
                    distSqr = 1;
                }
                float radii = _volume.SphereRadius + hurtRadius;
                if (distSqr < radii * radii)
                {
                    target.TakeDamage(Values.AltAttackDamage, this);
                    SfxId sfx = Hunter == Hunter.Weavel ? SfxId.WEAVEL_ALT_ATTACK_HIT : SfxId.TRACE_ALT_ATTACK_HIT;
                    _soundSource.PlaySfx(sfx);
                    EndAltAttack();
                    return true;
                }
            }
            return false;
        }

        private void AltAttackHitDoor(DoorEntity door)
        {
            if ((Hunter == Hunter.Spire || Hunter == Hunter.Trace || Hunter == Hunter.Weavel) && Flags2.TestFlag(PlayerFlags2.AltAttack)
                || Hunter == Hunter.Noxus && _altAttackTime >= Values.AltAttackStartup * 2) // todo: FPS stuff
            {
                if (door.Flags.TestFlag(DoorFlags.Locked) && door.Data.PaletteId == 8)
                {
                    door.Unlock(updateState: true, noLockAnimSfx: true);
                }
                door.Flags |= DoorFlags.ShotOpen;
                if (Hunter == Hunter.Spire)
                {
                    _soundSource.PlaySfx(SfxId.SPIRE_ALT_ATTACK_HIT, recency: Single.MaxValue);
                }
                else if (Hunter == Hunter.Trace || Hunter == Hunter.Weavel)
                {
                    SfxId sfx = Hunter == Hunter.Weavel ? SfxId.WEAVEL_ALT_ATTACK_HIT : SfxId.TRACE_ALT_ATTACK_HIT;
                    _soundSource.PlaySfx(sfx);
                    EndAltAttack();
                }
                else if (Hunter == Hunter.Noxus)
                {
                    _soundSource.PlaySfx(SfxId.NOX_ALT_ATTACK_HIT);
                    _scene.SpawnEffect(235, Vector3.UnitX, Vector3.UnitY, door.Position); // noxHit
                    EndAltAttack();
                }
            }
        }

        private readonly CollisionResult[] _checkResults = new CollisionResult[40];

        private void CheckCollision()
        {
            _standingEntCol = null;
            _collidedEntCol = null;
            _terrainDamage = false;
            // reused + cleared (same contents as a fresh array): a new one per player per tick was ~1.4 MB/s of sim garbage with 7 bots
            CollisionResult[] results = _checkResults;
            Array.Clear(results);
            CollisionVolume altVolume = PlayerVolumes[(int)Hunter, 2];
            Vector3 point1;
            Vector3 point2;
            Vector3 limitMin;
            Vector3 limitMax;
            float margin;
            if (IsAltForm)
            {
                point1 = PrevPosition + altVolume.SpherePosition;
                point2 = Position + altVolume.SpherePosition;
                margin = altVolume.SphereRadius + 0.4f;
                limitMin = new Vector3(
                    MathF.Min(MathF.Min(Single.MaxValue, point1.X), point2.X) - margin,
                    MathF.Min(MathF.Min(Single.MaxValue, point1.Y), point2.Y) - margin,
                    MathF.Min(MathF.Min(Single.MaxValue, point1.Z), point2.Z) - margin
                );
                limitMax = new Vector3(
                    MathF.Max(MathF.Max(Single.MinValue, point1.X), point2.X) + margin,
                    MathF.Max(MathF.Max(Single.MinValue, point1.Y), point2.Y) + margin,
                    MathF.Max(MathF.Max(Single.MinValue, point1.Z), point2.Z) + margin
                );
                limitMin.Y = MathF.Min(limitMin.Y, Position.Y + Fixed.ToFloat(Values.MaxPickupHeight));
            }
            else
            {
                float midpoint = (Fixed.ToFloat(Values.MaxPickupHeight) + Fixed.ToFloat(Values.MinPickupHeight)) / 2;
                point1 = PrevPosition.AddY(midpoint);
                point2 = Position.AddY(midpoint);
                margin = (Fixed.ToFloat(Values.MaxPickupHeight) - Fixed.ToFloat(Values.MinPickupHeight)) / 2 + 0.4f;
                limitMin = new Vector3(
                    MathF.Min(MathF.Min(Single.MaxValue, point1.X), point2.X) - margin,
                    MathF.Min(MathF.Min(Single.MaxValue, point1.Y), point2.Y) - margin,
                    MathF.Min(MathF.Min(Single.MaxValue, point1.Z), point2.Z) - margin
                );
                limitMax = new Vector3(
                    MathF.Max(MathF.Max(Single.MinValue, point1.X), point2.X) + margin,
                    MathF.Max(MathF.Max(Single.MinValue, point1.Y), point2.Y) + margin,
                    MathF.Max(MathF.Max(Single.MinValue, point1.Z), point2.Z) + margin
                );
            }
            bool includeEntities = GameState.TransitionState == TransitionState.None; // handled differently in-game
            // point1/point2/margin aren't used, but point1 has to be passed in order to include entities
            IReadOnlyList<CollisionCandidate> candidates
                = CollisionDetection.GetCandidatesForLimits(point1, point2, margin, limitMin, limitMax, includeEntities, _scene);
            if (IsAltForm)
            {
                float radius = altVolume.SphereRadius + (Hunter == Hunter.Spire || Hunter == Hunter.Sylux ? 0.5f : 0.35f);
                int count = CollisionDetection.CheckSphereBetweenPoints(candidates, point1, point2, radius,
                    limit: 40, includeOffset: true, TestFlags.Players, _scene, results);
                for (int i = 0; i < count; i++)
                {
                    HandleCollision(results[i]);
                }
                radius = Fixed.ToFloat(Values.BipedColRadius) - 0.15f;
                point1 = Position.AddY(radius);
                float yOffset = Fixed.ToFloat(Values.AltColYPos) + Fixed.ToFloat(Values.MaxPickupHeight) - Fixed.ToFloat(Values.MinPickupHeight)
                    - altVolume.SphereRadius - radius;
                point2 = Position.AddY(yOffset);
                count = CollisionDetection.CheckSphereBetweenPoints(candidates, point1, point2, radius,
                    limit: 1, includeOffset: false, TestFlags.Players, _scene, results);
                if (count > 0)
                {
                    Flags1 |= PlayerFlags1.NoUnmorph;
                }
                // recomp (campaign): Lockjaw's ride spring is off under a ceiling too low to unmorph, but that test runs from
                // where the spring holds the ball. Under a ceiling just out of reach (Ice Hive's tunnel, UNIT4_RM1 ~18.3,36,-38)
                // the spring lifted the ball into the test, the spring cut out, the ball dropped back clear, and the spring
                // lifted it again every 9 frames, forever (owner 2026-11-11: "infinite bob in the tunnel"). Campaign tests
                // the ceiling with the spring's lift added, so there the spring stays off and the ball rests on the floor.
                // Matches and replays keep MPH's test
                _altSpringCeiling = false;
                if (Hunter == Hunter.Sylux && GameState.Mode == GameMode.SinglePlayer)
                {
                    _altSpringCeiling = count > 0 || CollisionDetection.CheckSphereBetweenPoints(candidates, point1, point2.AddY(AltSpringLift),
                        radius, limit: 1, includeOffset: false, TestFlags.Players, _scene, results) > 0;
                }
                if (Hunter == Hunter.Kanden)
                {
                    float altRadius = Fixed.ToFloat(Values.AltColRadius);
                    for (int i = 1; i < _kandenSegPos.Length; i++)
                    {
                        // recomp: on a spider track a segment's centre is along its own up (PlayerSpiderBall.cs)
                        point2 = SpiderAttached ? _kandenSegPos[i] + _kandenSegMtx[i].Row1.Xyz * Fixed.ToFloat(Values.AltColYPos)
                            : _kandenSegPos[i].AddY(Fixed.ToFloat(Values.AltColYPos));
                        count = CollisionDetection.CheckSphereBetweenPoints(candidates, point2, point2, altRadius,
                            limit: 40, includeOffset: true, TestFlags.Players, _scene, results);
                        for (int j = 0; j < count; j++)
                        {
                            CollisionResult result = results[j];
                            if (SpiderIgnores(result))
                            {
                                continue; // recomp: the track holds the segments (SpiderStinglarvaSegments)
                            }
                            if (result.Field0 == 1)
                            {
                                Vector3 edge = result.EdgePoint2 - result.EdgePoint1;
                                Debug.Assert(edge != Vector3.Zero);
                                Vector3 between = point2 - result.EdgePoint1;
                                float dot = Vector3.Dot(between, edge);
                                float div = Math.Clamp(dot / edge.LengthSquared, 0, 1);
                                between = result.EdgePoint1 + edge * div;
                                between = point2 - between;
                                float magSqr = between.LengthSquared;
                                if (magSqr > 0 && magSqr < altRadius * altRadius)
                                {
                                    float mag = MathF.Sqrt(magSqr);
                                    if (altRadius - mag > 0)
                                    {
                                        if (SpiderAttached) // recomp: straight out from the edge, not world up
                                        {
                                            _kandenSegPos[i] += between / mag * (altRadius - mag);
                                        }
                                        else
                                        {
                                            float yInc = between.Y / mag * (altRadius - mag);
                                            _kandenSegPos[i].Y += yInc;
                                        }
                                    }
                                }
                            }
                            else if (result.Field0 == 0)
                            {
                                float dot = altRadius + result.Plane.W - Vector3.Dot(point2, result.Plane.Xyz);
                                if (dot > 0)
                                {
                                    _kandenSegPos[i] += result.Plane.Xyz * dot;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                float radius = (Fixed.ToFloat(Values.MaxPickupHeight) - Fixed.ToFloat(Values.MinPickupHeight)) / 2;
                int count = CollisionDetection.CheckSphereBetweenPoints(candidates, point1, point2, radius,
                    limit: 40, includeOffset: true, TestFlags.Players, _scene, results);
                for (int i = 0; i < count; i++)
                {
                    HandleCollision(results[i]);
                }
            }
            foreach (DoorEntity door in _scene.GetDoorEntities())
            {
                if (door.Flags.TestFlag(DoorFlags.Open) || door.ConnectorInactive)
                {
                    continue;
                }
                Vector3 lockPos = door.LockPosition;
                Vector3 doorFacing = door.FacingVector;
                Vector3 between = Position - lockPos;
                float dot = Vector3.Dot(between, doorFacing);
                if (dot <= 1.25f && dot >= -1.25f)
                {
                    between -= doorFacing * dot;
                    if (between.LengthSquared < door.RadiusSquared)
                    {
                        CollisionResult doorResult = default;
                        doorResult.Field0 = 0;
                        doorResult.Plane = new Vector4(doorFacing);
                        doorResult.EntityCollision = null;
                        doorResult.Flags = CollisionFlags.None;
                        if (Vector3.Dot(PrevPosition - lockPos, doorFacing) < 0)
                        {
                            doorResult.Plane.Xyz *= -1;
                        }
                        doorResult.Plane.W = doorResult.Plane.X * (lockPos.X + 0.4f * doorResult.Plane.X)
                            + doorResult.Plane.Y * (lockPos.Y + 0.4f * doorResult.Plane.Y)
                            + doorResult.Plane.Z * (lockPos.Z + 0.4f * doorResult.Plane.Z);
                        HandleCollision(doorResult);
                        AltAttackHitDoor(door);
                    }
                }
            }
            foreach (ForceFieldEntity forceField in _scene.GetForceFieldEntities())
            {
                if (!forceField.Active)
                {
                    continue;
                }
                float dot1 = Vector3.Dot(Position, forceField.Plane.Xyz) - forceField.Plane.W;
                float dot2 = Vector3.Dot(PrevPosition, forceField.Plane.Xyz) - forceField.Plane.W;
                if (dot1 < 1 && dot1 > -1 || dot2 < 1 && dot2 > -1)
                {
                    Vector3 between = Volume.SpherePosition - forceField.Position;
                    float dotH = Vector3.Dot(between, forceField.UpVector);
                    float dotW = Vector3.Dot(between, forceField.RightVector);
                    if (dotH <= forceField.Height && dotH >= -forceField.Height
                        && dotW <= forceField.Width && dotW >= -forceField.Width)
                    {
                        CollisionResult ffResult = default;
                        ffResult.Field0 = 0;
                        ffResult.EntityCollision = null;
                        ffResult.Flags = CollisionFlags.None;
                        ffResult.Plane = forceField.Plane;
                        if (dot2 < 0)
                        {
                            ffResult.Plane *= -1;
                        }
                        HandleCollision(ffResult);
                    }
                }
            }
            DamageResult dmgRes = default;
            dmgRes.Damage = 1;
            dmgRes.TakeDamage = _terrainDamage;
            if (_collidedEntCol != null)
            {
                _collidedEntCol.Entity.CheckContactDamage(ref dmgRes);
            }
            if (dmgRes.TakeDamage)
            {
                TakeDamage(dmgRes.Damage, DamageFlags.IgnoreInvuln, direction: null, source: null);
            }
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
        }

        // recomp: in an Echoes arena a face a player can't stand on: steeper than MPH's 0.5 floor slope but not a wall, or
        // (Prime's rule) below a 0.85 slope without Echoes' floor material (CollisionFlags.Bit12, EchoesArena's import).
        // Spire's ball climbs walls on purpose.
        private bool EchoesSteep(in CollisionResult result)
        {
            if (!HostEchoesMoves || result.Field0 != 0 || (IsAltForm && Hunter == Hunter.Spire))
            {
                return false;
            }
            float y = result.Plane.Y;
            return y >= 0.1f && (y <= 0.5f || y <= 0.85f && result.Flags.TestFlag(CollisionFlags.Bit12));
        }

        // the DS's fx32 arithmetic (NitroSDK): FX_Mul and VEC_DotProduct round, a squared length drops the remainder, FX_Div is
        // the 64/32 hardware divide rounded at bit 20, FX_Sqrt the 64-bit hardware root rounded at bit 10
        private static int FxIn(float value) => (int)MathF.Round(value * 4096);
        private static int FxMul(int a, int b) => (int)(((long)a * b + 0x800) >> 12);
        private static int FxDot(int ax, int ay, int az, int bx, int by, int bz)
            => (int)(((long)ax * bx + (long)ay * by + (long)az * bz + 0x800) >> 12);
        private static int FxSqr(int x, int y, int z) => (int)(((long)x * x + (long)y * y + (long)z * z) >> 12);
        private static int FxDiv(int a, int b) => (int)((((long)a << 32) / b + 0x80000) >> 20);

        private static int FxSqrt(int value)
        {
            if (value <= 0)
            {
                return 0;
            }
            ulong v = (ulong)value << 32;
            ulong root = (ulong)Math.Sqrt(v);
            while (root * root > v)
            {
                root--;
            }
            while ((root + 1) * (root + 1) <= v)
            {
                root++;
            }
            return (int)((root + 0x200) >> 10);
        }

        public void HandleCollision(CollisionResult result)
        {
            // recomp: attached, the spider track's own faces hold it, not push it (PlayerSpiderBall.cs)
            if (SpiderIgnores(result))
            {
                return;
            }
            bool v163 = false;
            bool v164 = false;
            bool v165 = false;
            bool v166 = false;
            bool isPlatform = false;
            bool isCrusher = false;
            PlatformEntity? platform = null;
            if (result.EntityCollision?.Entity?.Type == EntityType.Platform)
            {
                isPlatform = true;
                platform = (PlatformEntity)result.EntityCollision.Entity;
                if (platform.Flags.TestFlag(PlatformFlags.Hazard))
                {
                    isCrusher = true;
                }
            }
            if (isCrusher && _health == 0)
            {
                return;
            }
            float v2;
            if (IsAltForm)
            {
                // host hook (First Hunt rooms): FH's ball meets a ceiling (a face or an edge above its centre) only while it
                // moves up into it (the cart's ball sweep 0x200d300 reports a down-facing face only when crossed going up), so
                // it rolls under the low lids (Regulator's maze: lid 12.38, rolling centre 12.18), and a bomb under a lid pops
                // it up one tick into the lid, which pushes it back with its upward speed gone (hit response 0x200d948);
                // measured in BizHawk on the cart. Ours clamps it in the same frame (MphRead collides before moving, the cart
                // after), so the cart's one-tick pop (+0.57, then 0.3 into the floor) doesn't show; a ball standing (rolling up a
                // ramp under a lid) never meets it
                if (HostFhBallRules && result.EntityCollision == null && result.Plane.Y < -0.5f && (Speed.Y <= 0 || Flags1.TestFlag(PlayerFlags1.Standing)))
                {
                    return;
                }
                float altRad = Fixed.ToFloat(Values.AltColRadius);
                Vector3 altPos = Position.AddY(Fixed.ToFloat(Values.AltColYPos));
                // recomp (campaign, Sylux/Trace ball): room collision meets the ball as an upright ellipsoid, a hair under Samus's
                // ball width (r 0.48) and its own height (0.63). The campaign rooms are built for Samus's ball, and the bigger one
                // (r 0.63, 1.26 across) didn't fit them: Ice Hive's recessed wall tunnel past the rotating platforms is 0.99 wide
                // at its lip, and the ball held on it and on the maze posts (UNIT4_RM1, owner 2026-11-11). Floors and ceilings keep the
                // ball's own radius, so it rides at the same height and doesn't bob. Platforms and matches keep the sphere.
                // AltNarrowOff (dev) compares without
                bool narrow = GameState.Mode == GameMode.SinglePlayer && result.EntityCollision == null && !AltNarrowOff
                    && (Hunter == Hunter.Sylux || Hunter == Hunter.Trace);
                int fxAltRad = Values.AltColRadius;
                if (result.Field0 == 0)
                {
                    if (narrow)
                    {
                        fxAltRad = NarrowRadius(result.Plane.Y);
                        altRad = Fixed.ToFloat(fxAltRad);
                    }
                    // the cart's face depth in fixed point too (0x201fb18): r + w - dot, the dot rounded
                    v2 = Fixed.ToFloat(fxAltRad + FxIn(result.Plane.W) - FxDot(FxIn(altPos.X), FxIn(altPos.Y), FxIn(altPos.Z),
                        FxIn(result.Plane.X), FxIn(result.Plane.Y), FxIn(result.Plane.Z)));
                }
                else
                {
                    if (result.Field0 != 1)
                    {
                        return;
                    }
                    // host hook (First Hunt rooms): FH's ball has no edge or corner contacts with room collision. The cart's query
                    // (0x201eab0) keeps a face only where the ball centre's path crosses inside the polygon (1/32 slack), and the
                    // response (0x200d73c) always pushes along the face normal. MPH's edge contact pushes along the face normal
                    // too, so a low step's side wall (Morph Ball course, foot of the spawn ramp: 0.175 high) worked as a full wall
                    // and stopped the ball; on the cart it rolls straight up onto the step
                    if (HostFhBallRules && result.EntityCollision == null)
                    {
                        return;
                    }
                    // the cart works this out in fixed point (0x201fb44..0x201fd24): the dots and multiplies round, the squared
                    // lengths drop the remainder, and the divide and square root are the DS's hardware units. In floats a contact
                    // a hair inside or outside the ball came out the other way, so the ball stopped on lips the DS rolls over
                    // (and rolled on where it stops). Measured on 39 crossings vs BizHawk; docs/ACCURACY.md A13
                    int p1x = FxIn(result.EdgePoint1.X), p1y = FxIn(result.EdgePoint1.Y), p1z = FxIn(result.EdgePoint1.Z);
                    int ex = FxIn(result.EdgePoint2.X) - p1x, ey = FxIn(result.EdgePoint2.Y) - p1y, ez = FxIn(result.EdgePoint2.Z) - p1z;
                    if (ex == 0 && ey == 0 && ez == 0)
                    {
                        return;
                    }
                    int ax = FxIn(altPos.X), ay = FxIn(altPos.Y), az = FxIn(altPos.Z);
                    int fxDot = FxDot(ax - p1x, ay - p1y, az - p1z, ex, ey, ez);
                    int lenSqr = FxSqr(ex, ey, ez);
                    int div = lenSqr == 0 ? 0 : Math.Clamp(FxDiv(fxDot, lenSqr), 0, 4096);
                    int dx = ax - (p1x + FxMul(ex, div)), dy = ay - (p1y + FxMul(ey, div)), dz = az - (p1z + FxMul(ez, div));
                    int fxRad = Values.AltColRadius;
                    int magSqr = FxSqr(dx, dy, dz);
                    if (narrow && magSqr > 0)
                    {
                        // the ellipsoid's reach toward the edge: from the contact direction's height
                        fxRad = NarrowRadius(Fixed.ToFloat(dy) / MathF.Sqrt(Fixed.ToFloat(magSqr))); // magSqr is fixed point
                        altRad = Fixed.ToFloat(fxRad);
                    }
                    if (magSqr >= FxMul(fxRad, fxRad) || magSqr <= 0)
                    {
                        return;
                    }
                    int fxMag = FxSqrt(magSqr);
                    int nx = FxDiv(dx, fxMag), ny = FxDiv(dy, fxMag), nz = FxDiv(dz, fxMag);
                    Vector3 between = new Vector3(Fixed.ToFloat(nx), Fixed.ToFloat(ny), Fixed.ToFloat(nz));
                    float mag = Fixed.ToFloat(fxMag);
                    // recomp (campaign): a platform's edge below the ball's centre pushes the ball away from the edge, so it rolls
                    // over the lip. MPH's push goes along the face normal, so the top edge of the next piston's side face acted as a
                    // wall: in Piston Cave (UNIT1_C5) the tops sit 0.2 apart and a contact 0.006 deep zeroed the ball's speed in the
                    // gap (owner 2026-11-05: rolls straight across on the DS). Campaign only: matches and replays keep MPH's response
                    if (GameState.Mode == GameMode.SinglePlayer && result.EntityCollision != null && between.Y > 0)
                    {
                        result.Plane.Xyz = between;
                        v2 = altRad - mag;
                    }
                    else if (narrow && ey * (long)ey > (ex * (long)ex + ey * (long)ey + ez * (long)ez) * 81 / 100)
                    {
                        // an upright room edge (a post, a tunnel mouth's side) pushes the narrow ball straight away from it, so
                        // it slides round the corner instead of meeting the corner as the end of a wall (MPH's face-normal push).
                        // The push is level (the edge is upright), so it can't lift the ball
                        result.Plane.Xyz = between;
                        v2 = altRad - mag;
                    }
                    else
                    {
                        // the cart's depth is the plain product (0x201fd08), sign and all. MphRead squared it, so a contact from
                        // behind the face still pushed and zeroed the speed
                        int fxNormal = FxDot(nx, ny, nz, FxIn(result.Plane.X), FxIn(result.Plane.Y), FxIn(result.Plane.Z));
                        v2 = Fixed.ToFloat(FxMul(fxNormal, fxRad - fxMag));
                        if (GameState.Mode == GameMode.SinglePlayer)
                        {
                            // campaign keeps MphRead's squared depth: with the plain product the Trace/Sylux ball (r 0.63) stuck
                            // on the Ice Hive maze-top post (UNIT4_RM1 ~14.8,41,-38.5) it used to roll past (owner 2026-11-11).
                            // Matches and replays keep the cart's depth (A13)
                            float sq = Vector3.Dot(between, result.Plane.Xyz) * (altRad - mag);
                            v2 = sq * sq;
                        }
                    }
                }
            }
            else if (result.Field0 == 0)
            {
                float radius = Fixed.ToFloat(Values.BipedColRadius);
                Vector3 vec1 = Position.AddY(Fixed.ToFloat(Values.MaxPickupHeight) - radius);
                Vector3 vec2 = Position.AddY(Fixed.ToFloat(Values.MinPickupHeight) + radius);
                float dot1 = radius + result.Plane.W - Vector3.Dot(vec1, result.Plane.Xyz);
                float dot2 = radius + result.Plane.W - Vector3.Dot(vec2, result.Plane.Xyz);
                if (dot2 <= dot1)
                {
                    v2 = dot1;
                    v163 = true;
                }
                else
                {
                    v2 = dot2;
                }
            }
            else
            {
                if (result.Field0 != 1)
                {
                    return;
                }
                float v11 = 0;
                float v162 = 1;
                bool v169 = false;
                Vector3 edge = result.EdgePoint2 - result.EdgePoint1;
                Debug.Assert(edge != Vector3.Zero);
                float yTop = Position.Y + Fixed.ToFloat(Values.MaxPickupHeight);
                float yBot = Position.Y + Fixed.ToFloat(Values.MinPickupHeight);
                float yBotAdd = yBot + 0.5f;
                if (result.EdgePoint1.Y >= yBot)
                {
                    if (result.EdgePoint1.Y > yTop)
                    {
                        if (edge.Y == 0)
                        {
                            return;
                        }
                        v11 = (yTop - result.EdgePoint1.Y) / edge.Y;
                    }
                    v2 = 0; // gets assigned later
                }
                else if (edge.Y != 0)
                {
                    v11 = (yBot - result.EdgePoint1.Y) / edge.Y;
                    v2 = 0; // gets assigned later
                }
                else
                {
                    if (result.EdgePoint1.Y <= yBotAdd)
                    {
                        return;
                    }
                    float betweenX = Position.X - result.EdgePoint1.X;
                    float betweenZ = Position.Z - result.EdgePoint1.Z;
                    float div = (betweenX * edge.X + betweenZ * edge.Z) / (edge.X * edge.X + edge.Z * edge.Z);
                    div = Math.Clamp(div, 0, 1);
                    var between = new Vector3(
                        Position.X - result.EdgePoint1.X + edge.X * div,
                        yBotAdd - result.EdgePoint1.Y,
                        Position.Z - result.EdgePoint1.Z + edge.Z * div
                    );
                    float magSqr = between.LengthSquared;
                    if (magSqr >= 0.25f)
                    {
                        return;
                    }
                    float mag = MathF.Sqrt(magSqr);
                    result.Plane.Xyz = between / mag;
                    v2 = 0.5f - mag;
                    v169 = true;
                }
                if (!v169)
                {
                    if (result.EdgePoint2.Y >= yBot)
                    {
                        if (result.EdgePoint2.Y > yTop)
                        {
                            v162 = (yTop - result.EdgePoint1.Y) / edge.Y;
                        }
                    }
                    else
                    {
                        v162 = (yBot - result.EdgePoint1.Y) / edge.Y;
                    }
                    if (MathF.Abs(v11 - v162) < 1 / 4096f)
                    {
                        return;
                    }
                    var between = new Vector3(
                        Position.X - result.EdgePoint1.X,
                        yTop - result.EdgePoint1.Y,
                        Position.Z - result.EdgePoint1.Z
                    );
                    float dot1 = Vector3.Dot(between, edge);
                    float dot2 = Vector3.Dot(edge, edge);
                    float div = dot1 / dot2;
                    if (div >= v11)
                    {
                        if (div <= v162)
                        {
                            v11 = div;
                        }
                        else
                        {
                            v11 = v162;
                        }
                    }
                    float betweenY = result.EdgePoint1.Y + edge.Y * v11;
                    if (betweenY > yTop + Fixed.ToFloat(2) || betweenY < yBot - Fixed.ToFloat(2))
                    {
                        return;
                    }
                    if (betweenY <= yBotAdd)
                    {
                        between = new Vector3(
                            Position.X - (result.EdgePoint1.X + edge.X * v11),
                            yBotAdd - betweenY,
                            Position.Z - (result.EdgePoint1.Z + edge.Z * v11)
                        );
                        float magSqr = between.LengthSquared;
                        if (magSqr >= 0.25f)
                        {
                            return;
                        }
                        float mag = MathF.Sqrt(magSqr);
                        result.Plane.Xyz = between / mag;
                        v2 = 0.5f - mag;
                    }
                    else
                    {
                        float radius = Fixed.ToFloat(Values.BipedColRadius);
                        float betweenX = Position.X - (result.EdgePoint1.X + edge.X * v11);
                        float betweenZ = Position.Z - (result.EdgePoint1.Z + edge.Z * v11);
                        float v31 = betweenX * betweenX + betweenZ * betweenZ;
                        if (v31 >= radius * radius)
                        {
                            return;
                        }
                        float v32 = MathF.Sqrt(v31);
                        result.Plane.Xyz = new Vector3(betweenX / v32, 0, betweenZ / v32);
                        v2 = radius - v32;
                        if (betweenY > Position.Y)
                        {
                            v163 = true;
                        }
                    }
                }
            }
            Vector3 position = Position;
            if (v2 >= Fixed.ToFloat(-5))
            {
                v164 = true;
            }
            if (v2 > 0)
            {
                // recomp: in an Echoes arena a face too steep to stand on (normal Y 0.1..0.5) pushes out sideways only, like a
                // wall. MPH's push-out along the slanted normal (and the speed bounced off it) lifted a player who kept pushing
                // into it, so they climbed Mining Plaza's centre structure in jumps (owner 2026-10-31, sambot replay); MPH's
                // own rooms barely have such faces. Spire's ball climbs walls on purpose and keeps the old response.
                // Faces that aren't Echoes floor material count up to Prime's 0.85 floor slope (EchoesSteep).
                Vector3 normal = result.Plane.Xyz;
                float depth = v2;
                if (EchoesSteep(result))
                {
                    float hMag = MathF.Sqrt(normal.X * normal.X + normal.Z * normal.Z);
                    depth = v2 / hMag;
                    normal = new Vector3(normal.X / hMag, 0, normal.Z / hMag);
                }
                if (normal.Y < 0.1f && normal.Y > -0.1f)
                {
                    if (Cheats.WalkThroughWalls && !IsAltForm)
                    {
                        return;
                    }
                    v165 = true;
                    Flags1 |= PlayerFlags1.CollidingLateral;
                    if (HostWallCling)
                    {
                        WallClingTouch(normal, face: result.Field0 == 0); // recomp: the Trooper's Wall Cling (PlayerWallCling.cs)
                    }
                }
                // walking into a wall, jumping into a wall/ceiling, or landing/slopes/etc. -- update x/z
                position.X += normal.X * depth;
                position.Z += normal.Z * depth;
                if (!v163 || !Flags1.TestFlag(PlayerFlags1.StandingPrevious))
                {
                    // jumping into a wall/ceiling or landing/slopes/etc. -- update y
                    // hack: mimic the collision response for 1 frame at 30 FPS in the game
                    // --> move downward when hitting ceiling to avoid getting stuck in corners
                    // --> compensate for halved gravity so we can't go up steeper slopes
                    // todo?: the response when moving into walls laterally is also not accurate ("wall sliding")
                    // --> needs a hack; just doubling it results in jittering
                    // walkable floor faces (normal Y > 0.5, the standing test below) keep the whole push-out on a biped: vanilla
                    // does that (measured in an emulator walking up 26.6 and 45 deg ramps, docs/ACCURACY.md A9), and the quarter
                    // made walking up them 10% (26.6 deg) to 40% (45 deg) slower than vanilla. Edges, steeper faces and the
                    // morph balls keep the hack (unmeasured).
                    // recomp: a morph ball keeps the whole (vanilla) ceiling push-out, not 4x: in Data Shrine's low ramp
                    // tunnels (ceiling y 2.96) the 4x push shoved Trace's/Sylux's ball through the ramp below (player report
                    // 2026-10-27, docs/ACCURACY.md A10)
                    float factor = 1;
                    if (normal.Y > 0 && normal.Y < 0.9f
                        && (IsAltForm || result.Field0 != 0 || normal.Y <= 0.5f))
                    {
                        factor = 0.5f / 2; // todo: FPS stuff
                    }
                    else if (normal.Y < 0 && !IsAltForm)
                    {
                        factor = 2 * 2; // todo: FPS stuff
                    }
                    position.Y += normal.Y * depth * factor;
                }
                float dot = Vector3.Dot(Speed, normal);
                if (dot < 0 && IsAltForm && MathF.Abs(normal.Y) <= 0.05f && Values.AltFormStrafe != 0)
                {
                    // recomp: an upright wall the ball pushes into; an edge facing the same way is a wall's, not a lip (PlayerAltCamMove.cs)
                    _altWallNormal = new Vector3(normal.X, 0, normal.Z);
                }
                if (dot < 0 && IsAltForm && normal.Y > 0.05f && normal.Y < 0.9f && Values.AltFormStrafe != 0)
                {
                    // recomp: a lip or ledge edge (slanted, not an upright wall) the strafe alt form pushes into, for next frame's traction (PlayerAltCamMove.cs)
                    float h = MathF.Sqrt(normal.X * normal.X + normal.Z * normal.Z);
                    if (h > 0.1f)
                    {
                        _altEdgeNormal = new Vector3(normal.X / h, 0, normal.Z / h);
                    }
                }
                if (dot < 0)
                {
                    // floor collision
                    float damageSpeed = Fixed.ToFloat(Values.FallDamageSpeed);
                    if (Speed.Y <= -damageSpeed && !v163 && !v165 && !IsAltForm && !IsMorphing
                        && !IsUnmorphing && !Flags1.TestFlag(PlayerFlags1.Standing) && _timeSinceJumpPad > 5 * 2 // todo: FPS stuff
                        && !(HostEchoesPadNoFall && Flags1.TestFlag(PlayerFlags1.UsedJumpPad))) // recomp: Echoes arenas' pads (PlayerEchoesMoves)
                    {
                        // fall damage
                        int damage = (int)(Fixed.ToFloat(Values.FallDamageMax) * -(Speed.Y + damageSpeed) / 0.8f);
                        Debug.Assert(damage >= 0);
                        if (damage == 0)
                        {
                            damage = 1;
                        }
                        TakeDamage((uint)damage, DamageFlags.NoDmgInvuln, direction: null, source: null);
                    }
                    if (Hunter == Hunter.Noxus && IsAltForm && v165)
                    {
                        float magSqr = PrevSpeed.X * result.Plane.X + PrevSpeed.Z * result.Plane.Z;
                        if (magSqr < 0)
                        {
                            float tilt = Fixed.ToFloat(Values.AltBounceTilt) * -magSqr;
                            _altTiltX += result.Plane.X * tilt;
                            _altTiltZ += result.Plane.Z * tilt;
                            _altWobble += Fixed.ToFloat(Values.AltBounceWobble) * -magSqr;
                            _altSpinSpeed -= Fixed.ToFloat(Values.AltBounceSpin) * -magSqr;
                            var rotMtx = DetMatrix.CreateRotationY(MathHelper.DegreesToRadians(40));
                            Vector3 axis = Matrix.Vec3MultMtx3(result.Plane.Xyz, rotMtx);
                            Speed = Speed.AddX(axis.X * (-magSqr / 2)).AddZ(axis.Z * (-magSqr / 2));
                        }
                    }
                    Speed += normal * -dot;
                    if (!v163 && !IsAltForm && result.Field0 != 1)
                    {
                        float hMagSqr = Speed.X * Speed.X + Speed.Z * Speed.Z;
                        if (hMagSqr > 0)
                        {
                            float div = (normal.X * Speed.X + normal.Z * Speed.Z) / MathF.Sqrt(hMagSqr);
                            if (div < 0)
                            {
                                Speed *= div + 1;
                            }
                        }
                    }
                }
            }
            if (IsAltForm)
            {
                bool climbing = Hunter == Hunter.Spire && result.Field0 == 0 && !SpiderAttached;
                if (climbing)
                {
                    if ((_scene.RoomId == 30 || _scene.RoomId == 67) // Magma Drop, Cortex CPU
                        && result.Plane.Y == 0 && result.Plane.Z == 0)
                    {
                        climbing = false;
                    }
                    else if (_scene.RoomId == 80 && CameraSequence.Current != null) // Frost Labyrinth
                    {
                        climbing = false;
                    }
                }
                if (climbing)
                {
                    for (int i = 0; i < _spireAltVecs.Length; i++)
                    {
                        Vector3 vec = Position + _spireAltVecs[i];
                        float dot = result.Plane.W - Vector3.Dot(vec, result.Plane.Xyz);
                        if (dot >= 0)
                        {
                            Debug.Assert(vec != Position);
                            v164 = true;
                            // todo: revisit these calculations and improve the wall climbing "stickiness" issue
                            // --> related to the need for a hack to get pushed out more by horizontal collision (without jittering)
                            Position += result.Plane.Xyz * dot;
                            vec = new Vector3(Position.X - vec.X, 0, Position.Z - vec.Z).Normalized();
                            vec.X *= dot / 4;
                            vec.Z *= dot / 4;
                            vec.X *= _hSpeedMag + 0.1f;
                            vec.Z *= _hSpeedMag + 0.1f;
                            Speed += vec / 2;
                            if (result.Plane.Y > Fixed.ToFloat(-357) && Speed.Y < 0.15f)
                            {
                                v166 = true;
                                float yFactor = _hSpeedMag / 2;
                                if (Speed.Y < 0.01f)
                                {
                                    yFactor += 0.3f;
                                }
                                Speed = Speed.AddY(4 * dot * yFactor / 2);
                                Speed = Speed.WithY(Math.Min(Speed.Y, 0.15f));
                            }
                        }
                    }
                }
                else if (Hunter == Hunter.Sylux && result.Field0 == 0 && result.Plane.Y > Fixed.ToFloat(3138)
                    && !Flags1.TestFlag(PlayerFlags1.NoUnmorphPrevious) && !_altSpringCeiling)
                {
                    Vector3 pos = Position.AddY(Fixed.ToFloat(Values.AltColYPos) - Fixed.ToFloat(Values.AltColRadius) - 0.3f);
                    float dot = result.Plane.W - Vector3.Dot(pos, result.Plane.Xyz);
                    float traceY = Speed.Y;
                    if (dot >= 0)
                    {
                        v164 = true;
                        // recomp: vanilla runs this spring once per 30 Hz tick; we run it every 60 Hz step, so the
                        // gravity and push are halved and the damping is its square root (Lockjaw bounced on and on)
                        Speed = Speed.AddY(Fixed.ToFloat(Values.AltAirGravity) / 2); // todo: FPS stuff
                        if (Speed.Y < 0.25f)
                        {
                            if (Speed.Y < 0)
                            {
                                Speed = Speed.WithY(Speed.Y * MathF.Sqrt(Fixed.ToFloat(4034))); // todo: FPS stuff
                            }
                            // vanilla's fx32 multiply and add drop the remainder, so the rest bob dies in ~13 s; floats kept it going (the
                            // ball rocked for 20+ s). Grids measured against BizHawk's rest tail; docs/ACCURACY.md A13
                            Speed = Speed.WithY(MathF.Truncate(Speed.Y * 8192) / 8192);
                            Speed = Speed.AddY(dot * 0.2f / 2); // todo: FPS stuff
                            Speed = Speed.WithY(MathF.Truncate(Speed.Y * 32768) / 32768);
                            Speed = Speed.WithY(Math.Min(Speed.Y, 0.25f));
                        }
                    }
                    AltSpringTrace?.Invoke($"spring f0 {result.Field0} plane {result.Plane.X:0.000},{result.Plane.Y:0.000},{result.Plane.Z:0.000},{result.Plane.W:0.000}"
                        + $" dot {dot:0.0000} vy {traceY:0.0000}->{Speed.Y:0.0000} ent {result.EntityCollision != null}");
                }
            }
            if (result.EntityCollision != null)
            {
                if (result.Plane.Y > 0.25f)
                {
                    _standingEntCol = result.EntityCollision;
                }
                Flags1 |= PlayerFlags1.CollidingEntity;
                _scene.SendMessage(Message.PlayerCollideWith, this, result.EntityCollision.Entity, 0, _standingEntCol == null ? 0 : 1);
                _collidedEntCol = result.EntityCollision;
                if (isCrusher)
                {
                    Debug.Assert(platform != null);
                    if (result.Field0 == 0 && Vector3.Dot(result.Plane.Xyz, platform.Velocity) >= 0)
                    {
                        float value = Fixed.ToFloat(4080);
                        if (result.Plane.X <= value)
                        {
                            if (result.Plane.X >= -value)
                            {
                                if (result.Plane.Y <= value)
                                {
                                    if (result.Plane.Y >= -value)
                                    {
                                        if (result.Plane.Z <= value)
                                        {
                                            if (result.Plane.Z < -value)
                                            {
                                                _crushBits |= 0x20;
                                            }
                                        }
                                        else
                                        {
                                            _crushBits |= 0x10;
                                        }
                                    }
                                    else
                                    {
                                        _crushBits |= 8;
                                    }
                                }
                                else
                                {
                                    _crushBits |= 4;
                                }
                            }
                            else
                            {
                                _crushBits |= 2;
                            }
                        }
                        else
                        {
                            _crushBits |= 1;
                        }
                        if ((_crushBits & 3) == 3 || (_crushBits & 0xC) == 0xC || (_crushBits & 0x30) == 0x30)
                        {
                            // caught between one crusher moving +X and one moving -X, or one +Z and one -Z, or one +Y and one -Y
                            // recomp: vanilla checks this once a 30 Hz tick, so a squeeze that lasts one of our 60 Hz steps
                            // (the ball rolling off the corners of two closing pistons in Piston Cave) went uncaught there;
                            // in the campaign the squeeze has to hold for two steps
                            _crushedNow = true;
                            if (GameState.Mode != GameMode.SinglePlayer || _crushedBefore)
                            {
                                TakeDamage((uint)_health, DamageFlags.Death | DamageFlags.IgnoreInvuln | DamageFlags.NoDmgInvuln,
                                    direction: null, source: null);
                            }
                        }
                    }
                }
                else if (isPlatform)
                {
                    Debug.Assert(platform != null);
                    if (platform.Velocity.Y <= 0 && (Flags1.TestFlag(PlayerFlags1.Standing) || Flags1.TestFlag(PlayerFlags1.StandingPrevious))
                        && (v163 || result.Plane.Y < Fixed.ToFloat(-3849) && IsAltForm))
                    {
                        // reverse platform Y movement when hitting player
                        platform.Recoil();
                    }
                }
            }
            _terrainDamage = result.Flags.TestFlag(CollisionFlags.Damaging);
            if (v164)
            {
                _field449 = 0;
                if (result.Plane.Y >= Fixed.ToFloat(1401))
                {
                    _fieldC0 += result.Plane.Xyz;
                }
                if (!v163)
                {
                    _slipperiness = result.Slipperiness;
                    _standTerrain = result.Terrain;
                    if (_terrainDamage)
                    {
                        Flags1 |= PlayerFlags1.OnAcid;
                    }
                    if (_standTerrain == Terrain.Lava)
                    {
                        Flags1 |= PlayerFlags1.OnLava;
                    }
                    // solid landing
                    if (result.Field0 == 0 && result.Plane.Y > 0.5f && !EchoesSteep(result))
                    {
                        Flags1 |= PlayerFlags1.Standing;
                        _timeSinceStanding = 0;
                        if (!Flags1.TestFlag(PlayerFlags1.StandingPrevious) || Flags1.TestFlag(PlayerFlags1.AltFormPrevious))
                        {
                            Flags1 &= ~PlayerFlags1.UsedJump;
                        }
                        // if ( (some_flags & PSF_GROUNDED) == 0 && (some_flags & PSF_ALT_FORM) == 0 && player->energy )
                        if (_health > 0 && !IsAltForm && !Flags1.TestFlag(PlayerFlags1.Grounded))
                        {
                            if (Biped1Anim == PlayerAnimation.JumpLeft)
                            {
                                SetBiped1Animation(PlayerAnimation.LandLeft, AnimFlags.NoLoop);
                            }
                            else if (Biped1Anim == PlayerAnimation.JumpRight)
                            {
                                SetBiped1Animation(PlayerAnimation.LandRight, AnimFlags.NoLoop);
                            }
                            else
                            {
                                SetBiped1Animation(PlayerAnimation.LandNeutral, AnimFlags.NoLoop);
                            }
                        }
                    }
                }
            }
            if (v166 && !Flags1.TestFlag(PlayerFlags1.Standing))
            {
                Flags2 |= PlayerFlags2.SpireClimbing;
            }
            Position = position;
            _volume = CollisionVolume.Move(_volumeUnxf, Position);
        }
    }
}
