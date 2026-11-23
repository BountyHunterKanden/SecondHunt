using System;
using System.Reflection;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: Brawl Zero Suit Samus's army crawl, her alt form in place of the Morph Ball (docs/ZERO_SUIT_SAMUS.md section 7;
    // owner 2026-11-18: "work on zero suit Samus "alt form" army crawl from brawl ... brawl model only"). A slow prone crawl:
    //  - aimed with the camera as the strafing alt forms (Trace, Sylux, Weavel: AltFormStrafe), crawling back or sideways
    //    without turning; a quarter of her walk's top speed;
    //  - no bombs, no boost, no spider ball;
    //  - the alt attack fires her uncharged Paralyzer bolt where she aims, one every CrawlShotCooldown frames (no charge, so
    //    no stun: Paralyzer.Stuns);
    //  - shots hit her standing cylinder laid flat along her facing (CrawlLength long, BipedColRadius thick) instead of
    //    the ball; her movement collision stays the ball's sphere, so she fits wherever the ball does.
    // A stunned crawl can't move or unmorph (PlayerParalyzer). Sim only: every peer runs it from the same seat flag (Core
    // MatchStart: Characters.CrawlFor). The app draws her Brawl Squat clips in the ball's place (CampaignRenderer MatchCrawl.cs).
    public partial class PlayerEntity
    {
        public bool HostCrawl { get; set; }
        public bool HostCrawling => HostCrawl && IsAltForm;

        // her standing cylinder (MaxPickupHeight - MinPickupHeight, about 1.6) laid down
        public const float CrawlLength = 1.6f;
        // 30 frames at 60, 2 a second: "slower than standing" (section 7; its "one shot per 5 ticks" predates the standing
        // rate's cut to Paralyzer.ShotCooldown = 10 DS ticks, 20 frames, 3 a second, owner 10-15)
        public const int CrawlShotCooldown = 30;

        private EquipInfo? _crawlEquip = null;
        private BeamProjectileEntity[]? _crawlBeams = null;
        private const int _crawlBeamPool = 8;
        private int _crawlCooldown = 0;
        public int CrawlShots { get; private set; } // (tests)

        // her body's facing on the floor: the camera's (a strafing alt form faces its aim)
        public Vector3 CrawlFacing
        {
            get
            {
                var f = new Vector3(_field70, 0, _field74);
                return f.LengthSquared > 1e-8f ? f.Normalized() : Vector3.UnitZ;
            }
        }

        private float CrawlSpeedCap => Fixed.ToFloat(Values.WalkSpeedCap) / 4;

        // Initialize, with the other recomp inits: her bolt's weapon
        private void CrawlInit()
        {
            _crawlCooldown = 0;
            if (!HostCrawl)
            {
                return;
            }
            _crawlEquip ??= new EquipInfo();
            _crawlEquip.Weapon = Paralyzer.WeaponMP;
            _crawlEquip.InfiniteAmmo = true;
            _crawlBeams ??= SceneSetup.CreateBeamList(_crawlBeamPool, _scene);
            _crawlEquip.Beams = _crawlBeams;
        }

        // Initialize, once her values are set: as a strafing alt form's, a patched copy (the shared table in Metadata is never
        // touched, as GameFilters.PatchValues)
        private void CrawlValues()
        {
            if (!HostCrawl)
            {
                return;
            }
            object boxed = Values;
            typeof(PlayerValues).GetField(nameof(PlayerValues.AltFormStrafe), BindingFlags.Public | BindingFlags.Instance)!
                .SetValue(boxed, (byte)1);
            Values = (PlayerValues)boxed;
        }

        // ProcessInput's alt attack, while she crawls: a bolt where she aims (bots: at their target) while it's held
        private void CrawlFire()
        {
            if (_crawlCooldown > 0)
            {
                _crawlCooldown--;
            }
            if (_crawlEquip == null || _health == 0 || IsMorphing || _frozenTimer > 0 || _crawlCooldown > 0
                || !Controls.AltAttack.IsDown)
            {
                return;
            }
            // from about her head: the front of her body, a ball's height off the floor
            Vector3 center = Volume.SpherePosition;
            Vector3 aim = _gunVec1;
            if (IsBot && AiData.HostTargetPlayer is PlayerEntity target && target.Health > 0)
            {
                aim = target.Volume.SpherePosition - center;
            }
            if (aim.LengthSquared < 1e-6f)
            {
                aim = CrawlFacing;
            }
            aim = aim.Normalized();
            Vector3 muzzle = center + CrawlFacing * (CrawlLength / 2);
            // never past a wall: from the wall, a hair back toward her
            var col = new CollisionResult();
            if (CollisionDetection.CheckBetweenPoints(center, muzzle, TestFlags.Beams, _scene, ref col))
            {
                Vector3 toWall = col.Position - center;
                float len = toWall.Length;
                muzzle = len > 0.05f ? center + toWall * ((len - 0.05f) / len) : center;
            }
            _crawlEquip.ChargeLevel = 0;
            BeamSpawnFlags flags = _doubleDmgTimer > 0 ? BeamSpawnFlags.DoubleDamage
                : IsPrimeHunter ? BeamSpawnFlags.PrimeHunter : BeamSpawnFlags.None;
            BeamResultFlags result = BeamProjectileEntity.Spawn(this, _crawlEquip, muzzle, aim, flags, NodeRef, _scene);
            if (result == BeamResultFlags.NoSpawn)
            {
                return;
            }
            CrawlShots++;
            _crawlCooldown = CrawlShotCooldown;
            _timeSinceShot = 0;
            PlayBeamShotSfx(BeamType.Paralyzer, charged: false, continuous: false, homing: false, amountA: 0);
        }

        // the shots' test (BeamProjectileEntity): true when a beam from back to front touches her laid-down body; radii = her
        // body's radius + the beam's, as the standing test's
        public bool CrawlHit(Vector3 back, Vector3 front, float radii, ref CollisionResult res)
        {
            Vector3 facing = CrawlFacing;
            Vector3 bottom = Volume.SpherePosition - facing * (CrawlLength / 2);
            return CollisionDetection.CheckCylindersOverlap(back, front, bottom, facing, CrawlLength, radii, ref res);
        }

        // splash damage's reach (BeamProjectileEntity): from point to the nearest point on her body's line, her body moved
        // by lagShift (the ball's reach is to its centre)
        public float CrawlDistance(Vector3 point, Vector3 lagShift)
        {
            Vector3 facing = CrawlFacing;
            Vector3 a = Volume.SpherePosition + lagShift - facing * (CrawlLength / 2);
            float t = Math.Clamp(Vector3.Dot(point - a, facing), 0, CrawlLength);
            return Vector3.Distance(point, a + facing * t);
        }
    }
}
