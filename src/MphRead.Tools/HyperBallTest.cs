using System;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -hyperballtest: Dark Samus's morph ball (MphRead PlayerHyperBall.cs, docs/DARK_SAMUS_HYPER.md H1).
    //   hop: the jump button lifts the ball at once, as high as a bomb jump (vanilla Samus's own bomb jump measured too), in
    //   replays from format 32-43 only (Settings.NoBallJump = false); from format 44 the jump button does nothing in the ball
    //   hyper: the bomb button held spends 30 UA then 4 a frame, places no bomb, hurts an enemy in reach, never a
    //          teammate, and stops when UA runs out; by Corruption's rules (Settings.HyperBallCorruption, the default) 2 a
    //          zap every 2 frames (60 in 60 frames), by the starting ones (replays before format 34) 12 per 15 frames (48)
    //   reach: by Corruption's rules a target is in reach when its box comes within 7.5 (Corruption units) of the ball's
    //          centre: a target whose centre is just past that but whose body isn't is hurt; one wholly past it isn't
    //   puppet: another player's ball on a client (NetApplyHyperBall each frame, no button) still picks its arcs and zaps
    //          by Corruption's rules for the look, but hurts no one and isn't kicked
    //   kick: by Corruption's rules the arcs to the floor kick the ball about (it moves, and the same way on every run);
    //          by the starting ones it stays put
    //   old: with Settings.HyperBall = false (replays before format 32) she bombs like Samus and the jump button does nothing
    //   public: a public build's match (HyperBall unset) has no Hyper Ball (owner 2026-10-18), and its replay says so
    internal static class HyperBallTest
    {
        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            bool pass = Hop() & Hyper(teams: false, corruption: true) & Hyper(teams: true, corruption: true)
                & Hyper(teams: false, corruption: false) & Hyper(teams: true, corruption: false) & Kick() & Reach() & Puppet()
                & Old() & Public() & Net() & Solo();
            if (Array.IndexOf(args, "bots") >= 0)
            {
                Bots();
            }
            Console.WriteLine(pass ? "PASS hyperballtest" : "FAIL hyperballtest");
        }

        private static CampaignHost Start(bool? hyper, bool teams, out PlayerEntity me, out PlayerEntity other, bool? corruption = null, bool? noJump = null)
        {
            var settings = new MatchSettings { Mode = teams ? GameMode.BattleTeams : GameMode.Battle,
                Arena = MatchArenas.Find("Combat Hall")!.Name, HyperBall = hyper, HyperBallCorruption = corruption, NoBallJump = noJump };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Character = "darksamus", Nickname = "Player1", Team = 0 });
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Bot = true, Team = teams ? 0 : 1 });
            if (teams)
            {
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Spire, Nickname = "Player3", Team = 1 });
            }
            CampaignHost host = CampaignHost.StartMatch(settings);
            me = PlayerEntity.Players[0];
            other = PlayerEntity.Players[1];
            for (int f = 0; f < 900; f++)
            {
                // spawned, past the spawn invulnerability (a team match waits for a press to spawn)
                host.Step(new CampaignInput { Buttons = teams && me.Health == 0 && f % 30 == 0 ? CampaignButtons.Shoot : CampaignButtons.None });
            }
            me.HyperBallUa = 599; // (a full UA pool)
            return host;
        }

        private static void Morph(CampaignHost host, PlayerEntity me)
        {
            host.Step(new CampaignInput { Buttons = CampaignButtons.Morph });
            for (int f = 0; f < 90 && !(me.IsAltForm && !me.IsMorphing); f++)
            {
                host.Step(default);
            }
            for (int f = 0; f < 60; f++)
            {
                host.Step(default); // (settled on the floor)
            }
        }

        // the highest the ball gets over where it started, and the frames until it rises
        private static (float Peak, int Start) Rise(CampaignHost host, PlayerEntity me, CampaignButtons press, int frames)
        {
            float y0 = me.Position.Y;
            float peak = 0;
            int start = -1;
            for (int f = 0; f < frames; f++)
            {
                host.Step(new CampaignInput { Buttons = f == 0 ? press : CampaignButtons.None });
                float dy = me.Position.Y - y0;
                if (start < 0 && dy > 0.01f)
                {
                    start = f;
                }
                peak = Math.Max(peak, dy);
            }
            return (peak, start);
        }

        private static bool Hop()
        {
            // vanilla Samus's bomb jump first (her seat with the Hyper Ball off)
            float bombPeak;
            using (CampaignHost host = Start(hyper: false, teams: false, out PlayerEntity samus, out _))
            {
                Morph(host, samus);
                (bombPeak, int bombStart) = Rise(host, samus, CampaignButtons.AltAttack, 150);
                Console.WriteLine($"[hop] bomb jump: peak {bombPeak:0.000} after {bombStart} frames");
            }
            bool ok;
            // a replay from before the hop was removed (format 32-43)
            using (CampaignHost host = Start(hyper: null, teams: false, out PlayerEntity me, out _, noJump: false))
            {
                Morph(host, me);
                (float peak, int start) = Rise(host, me, CampaignButtons.Jump, 150);
                Console.WriteLine($"[hop] old ball hop: peak {peak:0.000} after {start} frames, hops {me.BallJumps}");
                ok = me.BallJumps == 1 && start >= 0 && start <= 1 && bombPeak > 0.3f
                    && Math.Abs(peak - bombPeak) <= bombPeak * 0.15f;
                if (!ok)
                {
                    Console.WriteLine("[hop] FAIL: expected one hop rising at once to about the bomb jump's peak");
                }
            }
            // now (owner 2026-10-18): no hop
            using (CampaignHost host = Start(hyper: null, teams: false, out PlayerEntity me, out _))
            {
                Morph(host, me);
                (float peak, _) = Rise(host, me, CampaignButtons.Jump, 60);
                Console.WriteLine($"[hop] now: jump button rise {peak:0.000}, hops {me.BallJumps}");
                if (peak >= 0.05f || me.BallJumps != 0)
                {
                    Console.WriteLine("[hop] FAIL: expected no hop");
                    ok = false;
                }
            }
            return ok;
        }

        private static bool Hyper(bool teams, bool corruption)
        {
            string label = (teams ? "teammate" : "enemy") + (corruption ? "" : ", starting rules");
            using CampaignHost host = Start(hyper: null, teams, out PlayerEntity me, out PlayerEntity other, corruption);
            Morph(host, me);
            Vector3 offset = Clear(host, me); // (somewhere open in reach)
            other.Position = me.Position + offset;
            host.Step(default);
            int ua = me.HyperBallUa;
            int hp = other.Health;
            int bombs = me.HyperBallBombAmmo;
            const int held = 60;
            for (int f = 0; f < held; f++)
            {
                other.Position = me.Position + offset; // (it stays in reach)
                host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
            }
            bool active = me.HyperBallActive;
            int spent = ua - me.HyperBallUa;
            int dealt = me.HyperBallDealt;
            int lost = hp - other.Health;
            Console.WriteLine($"[{label}] held {held} frames: active {active}, UA {ua}->{me.HyperBallUa} (spent {spent}),"
                + $" dealt {dealt}, target hp {hp}->{other.Health}, bombs {bombs}->{me.HyperBallBombAmmo}");
            int expectSpent = 30 + 4 * (held - 1);
            int expectDealt = corruption ? 60 : 48;
            bool ok = active && (spent == expectSpent || spent == expectSpent - 4) && me.HyperBallBombAmmo == bombs
                && (teams ? dealt == 0 && lost == 0 : Math.Abs(dealt - expectDealt) <= 2 && lost == dealt);
            // let go: off (the release lands the next step); then hold until the UA runs out
            host.Step(default);
            host.Step(default);
            bool offOnRelease = !me.HyperBallActive;
            for (int f = 0; f < 15; f++)
            {
                host.Step(default); // (the release wait)
            }
            int frames = 0;
            while (frames < 900)
            {
                host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
                frames++;
                if (!me.HyperBallActive && frames > 2)
                {
                    break;
                }
            }
            Console.WriteLine($"[{label}] off on release {offOnRelease}; held to empty: stopped after {frames} frames with UA {me.HyperBallUa}");
            ok &= offOnRelease && me.HyperBallUa < 4 && frames < 900;
            if (!ok)
            {
                Console.WriteLine($"[{label}] FAIL: expected UA {expectSpent} spent, no bomb, "
                    + (teams ? "no hurt on a teammate" : $"{expectDealt} dealt") + ", off on release and when UA runs out");
            }
            return ok;
        }

        // held 120 frames on the floor with no one about: how far the ball ends up from where it was (twice by Corruption's
        // rules: it moves, the same both times; once by the starting rules: it stays)
        private static bool Kick()
        {
            Vector3 Drift(bool corruption)
            {
                using CampaignHost host = Start(hyper: null, teams: false, out PlayerEntity me, out PlayerEntity other, corruption);
                Morph(host, me);
                other.Position = me.Position + new Vector3(0, 40, 0); // (out of reach)
                Vector3 at = me.Position;
                for (int f = 0; f < 120; f++)
                {
                    other.Position = at + new Vector3(0, 40, 0);
                    host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
                }
                return me.Position - at;
            }
            Vector3 a = Drift(true), b = Drift(true), c = Drift(false);
            float flat(Vector3 v) => v.Xz.Length;
            Console.WriteLine($"[kick] 120 frames held: Corruption's rules moved it {flat(a):0.000} then {flat(b):0.000}, the starting rules {flat(c):0.000}");
            bool ok = flat(a) > 0.02f && a == b && flat(c) < 0.005f;
            if (!ok)
            {
                Console.WriteLine("[kick] FAIL: expected the arcs to move the ball (the same on both runs) and no move without them");
            }
            return ok;
        }

        // a target held with its centre `past` beyond the reach (its radius times `past`), the button held 30 frames: hurt?
        private static bool Reach()
        {
            int Hurt(float past, out float centre)
            {
                using CampaignHost host = Start(hyper: null, teams: false, out PlayerEntity me, out PlayerEntity other, corruption: true);
                Morph(host, me);
                Vector3 dir = Clear(host, me).Normalized();
                const float reach = 7.5f * 1.7f / 3f;
                Vector3 lift = other.Volume.SpherePosition - other.Position;
                float at = reach + other.Volume.SphereRadius * past;
                int hp = other.Health;
                for (int f = 0; f < 30; f++)
                {
                    other.Position = me.Volume.SpherePosition + dir * at - lift;
                    host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
                }
                centre = (other.Volume.SpherePosition - me.Volume.SpherePosition).Length;
                return hp - other.Health;
            }
            int inside = Hurt(0.6f, out float c1), outside = Hurt(1.8f, out float c2);
            const float reachU = 7.5f * 1.7f / 3f;
            Console.WriteLine($"[reach] reach {reachU:0.00}: centre {c1:0.00} away (body inside) lost {inside}, centre {c2:0.00} (body past) lost {outside}");
            bool ok = inside > 0 && outside == 0;
            if (!ok)
            {
                Console.WriteLine("[reach] FAIL: expected a hurt when the body comes into reach, none when it is all past it");
            }
            return ok;
        }

        // another player's ball as a client sees it: the host's word (on) each frame, no button here
        private static bool Puppet()
        {
            using CampaignHost host = Start(hyper: null, teams: false, out PlayerEntity me, out PlayerEntity other, corruption: true);
            Morph(host, me);
            Vector3 offset = Clear(host, me);
            host.Step(default);
            Vector3 at = me.Position;
            int hp = other.Health, ua = me.HyperBallUa, zaps = 0, arcs = 0;
            for (int f = 0; f < 60; f++)
            {
                other.Position = me.Position + offset;
                me.NetApplyHyperBall(1, 0, pose: true);
                host.Step(default);
                zaps += me.HyperBallZapped == other ? 1 : 0;
                arcs += me.HyperBallNewArcs;
            }
            float moved = (me.Position - at).Xz.Length;
            Console.WriteLine($"[puppet] 60 frames on by the host's word: {zaps} zaps and {arcs} arcs drawn, target hp {hp}->{other.Health}, UA {ua}->{me.HyperBallUa}, moved {moved:0.000}");
            bool ok = me.HyperBallActive && zaps >= 25 && arcs > 0 && other.Health == hp && me.HyperBallUa == ua && moved < 0.005f;
            if (!ok)
            {
                Console.WriteLine("[puppet] FAIL: expected about 30 zaps and some arcs for the look, no hurt, no UA spent, no kick");
            }
            return ok;
        }

        // the dev solo run (MatchSettings.Solo, SoloArena.cs): she spawns with full UA, the Hyper Ball drains it, and it's
        // full again 1.5 s after she lets go
        private static bool Solo()
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name, Solo = true,
                TimeLimitSeconds = -1 };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Character = "darksamus", Nickname = "Player1" });
            using CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity me = PlayerEntity.Players[0];
            for (int f = 0; f < 900; f++)
            {
                host.Step(default);
            }
            int spawned = me.HyperBallUa;
            Morph(host, me);
            for (int f = 0; f < 60; f++)
            {
                host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
            }
            int held = me.HyperBallUa;
            bool active = me.HyperBallActive;
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
            int soon = me.HyperBallUa;
            for (int f = 0; f < 60; f++)
            {
                host.Step(default);
            }
            int later = me.HyperBallUa;
            Console.WriteLine($"[solo] UA at spawn {spawned}, after 60 held frames {held} (active {active}), 1 s after {soon}, 2 s after {later}");
            bool ok = spawned == 599 && active && held < spawned && soon <= held && soon >= held - 4 && later == 599; // (the release lands a step late: one more frame paid)
            if (!ok)
            {
                Console.WriteLine("[solo] FAIL: expected full UA at spawn, drained while held, full again 1.5 s after release");
            }
            return ok;
        }

        // 2.5 units from the ball, the first of four directions with nothing solid in between
        private static Vector3 Clear(CampaignHost host, PlayerEntity me)
        {
            Vector3 a = me.Volume.SpherePosition;
            foreach (Vector3 d in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
            {
                Vector3 offset = d * 2.5f + new Vector3(0, 0.3f, 0);
                Formats.CollisionResult res = default;
                if (!Formats.CollisionDetection.CheckBetweenPoints(a, a + offset * 1.3f, Formats.TestFlags.Beams, host.Scene, ref res))
                {
                    return offset;
                }
            }
            return new Vector3(0, 2.5f, 0);
        }

        // -hyperballtest bots: three Dark Samus bots (and an idle local seat) with full UA for two minutes (what they do with it; no pass rule)
        private static void Bots()
        {
            var settings = new MatchSettings { Mode = GameMode.Battle, Arena = MatchArenas.Find("Combat Hall")!.Name };
            settings.Players.Add(new MatchPlayer { Hunter = Hunter.Kanden, Nickname = "Player1" }); // (seat 0 is local: stands)
            for (int i = 1; i < 4; i++)
            {
                settings.Players.Add(new MatchPlayer { Hunter = Hunter.Samus, Character = "darksamus", Bot = true, BotLevel = 2 });
            }
            using CampaignHost host = CampaignHost.StartMatch(settings);
            int alt = 0;
            for (int f = 0; f < 7200; f++)
            {
                if (f % 600 == 0)
                {
                    for (int i = 0; i < 4; i++) PlayerEntity.Players[i].HyperBallUa = 599;
                }
                host.Step(default);
                for (int i = 0; i < 4; i++) if (PlayerEntity.Players[i].IsAltForm) alt++;
            }
            for (int i = 0; i < 4; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                Console.WriteLine($"[bots] seat {i}: hops {p.BallJumps}, hyper dealt {p.HyperBallDealt}, bombs {p.HyperBallBombAmmo}");
            }
            Console.WriteLine($"[bots] ball frames (all seats) {alt}");
        }

        // the snapshot's abilities block carries each seat's Hyper Ball (on + targets) both ways
        private static bool Net()
        {
            var a = new MphRecomp.Multiplayer.Net.SeatAbilities
            {
                Cling = new PlayerNetCling[3], Drone = new PlayerNetDrone[3],
                Hyper = new byte[] { 1, 0, 1 }, HyperTargets = new byte[] { 0x32, 0, 0x01 }
            };
            var buffer = new byte[a.Size];
            int written = a.Write(buffer);
            var b = MphRecomp.Multiplayer.Net.SeatAbilities.Read(buffer, out int read);
            bool ok = written == a.Size && read == written;
            for (int i = 0; i < 3; i++)
            {
                ok &= b.Hyper[i] == a.Hyper[i] && b.HyperTargets[i] == a.HyperTargets[i];
            }
            Console.WriteLine($"[net] abilities block {written} bytes, read {read}: hyper round trip {(ok ? "same" : "DIFFERENT")}");
            return ok;
        }

        private static bool Old()
        {
            using CampaignHost host = Start(hyper: false, teams: false, out PlayerEntity me, out _);
            Morph(host, me);
            int bombs = me.HyperBallBombAmmo;
            host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
            host.Step(default);
            int afterBomb = me.HyperBallBombAmmo;
            for (int f = 0; f < 120; f++)
            {
                host.Step(default);
            }
            (float peak, _) = Rise(host, me, CampaignButtons.Jump, 60);
            Console.WriteLine($"[old] bombs {bombs}->{afterBomb}, hyper {me.HyperBallActive}, jump button rise {peak:0.000}");
            bool ok = afterBomb == bombs - 1 && !me.HyperBallActive && peak < 0.05f && me.BallJumps == 0;
            if (!ok)
            {
                Console.WriteLine("[old] FAIL: expected a bomb, no hop and no Hyper Ball (vanilla Samus)");
            }
            return ok;
        }

        private static bool Public()
        {
            bool saved = MphRecomp.Config.RecompSettings.PublicBuild;
            try
            {
                MphRecomp.Config.RecompSettings.PublicBuild = true;
                using CampaignHost host = Start(hyper: null, teams: false, out PlayerEntity me, out _);
                Morph(host, me);
                int bombs = me.HyperBallBombAmmo;
                host.Step(new CampaignInput { Buttons = CampaignButtons.AltAttack });
                host.Step(default);
                var file = new MphRecomp.Replay.ReplayFile { Settings = new MatchSettings() };
                using var ms = new System.IO.MemoryStream();
                file.Write(ms);
                ms.Position = 0;
                bool? recorded = MphRecomp.Replay.ReplayFile.Read(ms).Settings.HyperBall;
                Console.WriteLine($"[public] hyper ball {me.HostHyperBall}, bombs {bombs}->{me.HyperBallBombAmmo}, replay records {recorded}");
                bool ok = !me.HostHyperBall && me.HyperBallBombAmmo == bombs - 1 && recorded == false;
                if (!ok)
                {
                    Console.WriteLine("[public] FAIL: expected no Hyper Ball, a bomb, and a replay that records it off");
                }
                return ok;
            }
            finally
            {
                MphRecomp.Config.RecompSettings.PublicBuild = saved;
            }
        }
    }
}
