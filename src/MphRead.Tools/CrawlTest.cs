using System;
using System.Collections.Generic;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -crawltest: Brawl Zero Suit Samus's army crawl alt form (MphRead PlayerCrawl.cs, docs/ZERO_SUIT_SAMUS.md section 7).
    // Proving Ground, her Brawl seat against a pinned far-off bot:
    //  - gating: the Brawl seat crawls; her Prime 3 seat, a replay before format 97 (ZeroSuitCrawl false) and a Samus don't;
    //  - in the crawl: no bombs and no boost; held forward her top speed is a quarter of her walk (and well under her ball's);
    //  - the alt attack held for CRAWL_FIRE steps fires one uncharged bolt every CrawlShotCooldown steps;
    //  - her hurtbox is her standing cylinder laid along her facing: a shot across her feet hits, one past them misses, and
    //    the same shot across her feet would miss the ball.
    internal static class CrawlTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            var errors = new List<string>();
            void Check(bool ok, string what)
            {
                Console.WriteLine($"[crawl] {(ok ? "ok  " : "FAIL")} {what}");
                if (!ok) errors.Add(what);
            }
            FieldInfo abilities = typeof(PlayerEntity).GetField("_abilities", BindingFlags.NonPublic | BindingFlags.Instance)!;

            Check(Gate("SSBBZeroSuit", zeroSuit: true, null) && Gate("SSBBZeroSuit03", zeroSuit: true, null),
                "a Brawl Zero Suit seat (and a recolour) crawls");
            Check(!Gate(null, zeroSuit: true, null), "her Prime 3 seat rolls the ball");
            Check(!Gate("SSBBZeroSuit", zeroSuit: true, false), "a replay before format 97 rolls the ball");
            Check(!Gate("SSBBZeroSuit", zeroSuit: false, null), "Samus with that skin name rolls the ball");

            float crawlTop = Drive("SSBBZeroSuit", fire: 0, out PlayerEntity? me, out int shots);
            float walk = Fixed.ToFloat(Metadata.PlayerValues[(int)Hunter.Samus].WalkSpeedCap);
            Check(me != null && me.HostCrawling, "she's in the crawl");
            if (me != null)
            {
                var ab = (AbilityFlags)abilities.GetValue(me)!;
                Check(!ab.TestFlag(AbilityFlags.Bombs) && !ab.TestFlag(AbilityFlags.Boost), $"no bombs, no boost ({ab})");
                Check(me.Values.AltFormStrafe != 0, "she strafes (camera aim) in the crawl");
            }
            float ballTop = Drive(null, fire: 0, out _, out _);
            Check(crawlTop > walk / 4 * 0.8f && crawlTop <= walk / 4 * 1.02f,
                $"crawl top speed {crawlTop:0.000} = a quarter of her walk {walk:0.000} ({walk / 4:0.000})");
            Check(ballTop > crawlTop * 2, $"well under her ball's {ballTop:0.000}");

            int fireSteps = int.TryParse(Environment.GetEnvironmentVariable("CRAWL_FIRE"), out int fs) ? fs : 120;
            Drive("SSBBZeroSuit", fire: fireSteps, out _, out shots);
            int want = (fireSteps + PlayerEntity.CrawlShotCooldown - 1) / PlayerEntity.CrawlShotCooldown;
            Check(shots >= want - 1 && shots <= want, $"{shots} bolts in {fireSteps} steps held (one every {PlayerEntity.CrawlShotCooldown}: {want})");

            if (me != null)
            {
                // geometry on the last crawl's seat: shots straight down through points along her body
                Vector3 c = me.Volume.SpherePosition;
                Vector3 f = me.CrawlFacing;
                float radii = me.Volume.SphereRadius + 0.05f;
                bool Hits(float along)
                {
                    Vector3 at = c + f * along;
                    CollisionResult res = default;
                    return me.CrawlHit(at.AddY(2), at.AddY(-2), radii, ref res);
                }
                bool BallHits(float along)
                {
                    Vector3 at = c + f * along;
                    CollisionResult res = default;
                    return CollisionDetection.CheckCylinderOverlapSphere(at.AddY(2), at.AddY(-2), c, radii, ref res);
                }
                Check(Hits(0) && Hits(0.75f) && Hits(-0.75f), "shots on her head, middle and feet hit");
                Check(!Hits(1.4f) && !Hits(-1.4f), "shots past her head and feet miss");
                Check(!BallHits(-0.75f), "(the ball alone would miss her feet)");
                Vector3 side = new Vector3(f.Z, 0, -f.X);
                Vector3 s = c + side * 0.7f;
                CollisionResult r2 = default;
                Check(!me.CrawlHit(s.AddY(2), s.AddY(-2), radii, ref r2), "a shot beside her misses (0.5 wide)");
                Check(me.CrawlDistance(c - f * 0.8f, Vector3.Zero) < 0.01f && me.CrawlDistance(c - f * 1.8f, Vector3.Zero) > 0.95f,
                    "splash reach is to her body, not the ball's centre");
            }

            foreach (string e in errors) Console.WriteLine("  FAIL " + e);
            Console.WriteLine(errors.Count == 0 ? "PASS crawltest" : $"FAIL crawltest ({errors.Count})");
        }

        static MatchSettings Settings(string? skin, bool zeroSuit, bool? crawl)
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = "MP3 PROVING GROUND", ZeroSuitCrawl = crawl };
            settings.Players.Add(zeroSuit
                ? new MatchPlayer { Hunter = Characters.ZeroSuit.Base, Character = Characters.ZeroSuit.Id, Skin = skin, Nickname = "Player1" }
                : new MatchPlayer { Hunter = Hunter.Samus, Skin = skin, Nickname = "Player1" });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, BotLevel = 0 });
            return settings;
        }

        static bool Gate(string? skin, bool zeroSuit, bool? crawl)
        {
            using CampaignHost host = CampaignHost.StartMatch(Settings(skin, zeroSuit, crawl));
            return PlayerEntity.Players[0].HostCrawl;
        }

        // morphs her, then holds forward (and the alt attack for the first `fire` steps); her top horizontal speed
        static float Drive(string? skin, int fire, out PlayerEntity? me, out int shots)
        {
            using CampaignHost host = CampaignHost.StartMatch(Settings(skin, zeroSuit: true, null));
            PlayerEntity p = PlayerEntity.Players[0];
            void Step(CampaignInput input)
            {
                PlayerEntity bot = PlayerEntity.Players[1];
                bot.Position = bot.PrevPosition = new Vector3(-40, 0.5f, -40);
                bot.Speed = Vector3.Zero;
                host.Step(input);
            }
            for (int f = 0; f < 900; f++) Step(default);
            Step(new CampaignInput { Buttons = CampaignButtons.Morph });
            for (int f = 0; f < 300 && !(p.IsAltForm && !p.IsMorphing); f++) Step(default);
            for (int f = 0; f < 30; f++) Step(default);
            float top = 0;
            int start = p.CrawlShots;
            Vector3 at = p.Position;
            int steps = Math.Max(fire, 180);
            for (int f = 0; f < steps; f++)
            {
                Step(new CampaignInput
                {
                    Move = fire > 0 ? default : new System.Numerics.Vector2(0, 1),
                    Buttons = f < fire ? CampaignButtons.AltAttack : 0,
                });
                if (fire == 0)
                {
                    top = MathF.Max(top, MathF.Sqrt(p.Speed.X * p.Speed.X + p.Speed.Z * p.Speed.Z));
                    // pinned to the start column (same speed, no walls)
                    var slide = new Vector3(p.Position.X - at.X, 0, p.Position.Z - at.Z);
                    p.Position -= slide;
                    p.PrevPosition -= slide;
                }
            }
            shots = p.CrawlShots - start;
            me = p;
            return top;
        }
    }
}
