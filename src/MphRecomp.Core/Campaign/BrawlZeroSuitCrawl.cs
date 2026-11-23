using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // Brawl Zero Suit Samus's army crawl, drawn (the sim's: MphRead PlayerCrawl.cs; docs/ZERO_SUIT_SAMUS.md section 7): her
    // Brawl crouch clips (BrawlZeroSuitBody.Crawl) in the ball's place, laid along her facing with her feet on the floor
    // under the ball. One clip clock per seat, stepped after each sim step while she crawls.
    public static class BrawlZeroSuitCrawl
    {
        const string Down = "Squat", Wait = "SquatWait", Forward = "SquatF", Back = "SquatB";
        // moving: faster than this (world units a step) plays a crawl; the clips at her top speed play this fast
        const float MoveMin = 0.004f, TopRate = 1.0f, MinRate = 0.35f;

        // true for a Brawl body that has her crawl clips
        public static bool Has(NativeBody body) => body.Game == "ssbb" && body.Clip(Forward) != null && body.Clip(Wait) != null;

        // model -> world: her standing body's matrix, its feet on the floor under the alt form's sphere, turned to her facing
        public static Matrix4 ModelMatrix(NativeBody body, PlayerEntity p)
        {
            Vector3 at = p.Volume.SpherePosition;
            float floor = at.Y - p.Volume.SphereRadius;
            float bottom = Fixed.ToFloat(Metadata.PlayerValues[(int)p.Hunter].MinPickupHeight);
            return body.ModelMatrix(p.Hunter, new Vector3(at.X, floor - bottom, at.Z), p.CrawlFacing);
        }

        public sealed class Animator
        {
            readonly NativeBodyAnimator _a;
            bool _down; // her drop to the floor played since she last stood

            public Animator(NativeBody body, BodyClips map) => _a = new NativeBodyAnimator(body, map);

            public NativeBodyAnimator Clips => _a;
            public string Playing => _a.Playing;

            // after each sim step: crawling = in her alt form (HostCrawling), else she stood and the next crawl drops again
            public void Step(PlayerEntity p, bool crawling, float dt)
            {
                if (!crawling)
                {
                    _down = false;
                    return;
                }
                var v = new Vector3(p.Speed.X, 0, p.Speed.Z);
                float speed = v.Length;
                if (speed < MoveMin || p.IsMorphing)
                {
                    // the drop, then her crouch loop; after a crawl, straight back to the loop
                    _a.Step(_down ? new BodyClips.Choice("wait", Wait, true) : new BodyClips.Choice("down", Down, false, Then: Wait), dt);
                    return;
                }
                _down = true;
                // forward or back by the push along her facing; sideways crawls forward
                bool back = Vector3.Dot(v, p.CrawlFacing) < -0.3f * speed;
                float cap = Fixed.ToFloat(p.Values.WalkSpeedCap) / 4;
                float rate = Math.Clamp(cap > 0 ? speed / cap * TopRate : TopRate, MinRate, 1.2f);
                _a.Step(back ? new BodyClips.Choice("back", Back, true, rate) : new BodyClips.Choice("fwd", Forward, true, rate), dt);
            }
        }
    }
}
