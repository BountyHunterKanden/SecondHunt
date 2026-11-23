using System;
using System.Globalization;
using System.Linq;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Frontend;
using MphRecomp.Multiplayer;
using MphRecomp.SideView;
using OpenTK.Mathematics;

namespace MphRead
{
    // -sideviewtest: side-view mode P0 (owner 2026-11-12, docs/SIDE_VIEW_MODE.md): Data Shrine walked on the plane x = 0
    //   stage: the Data Shrine stage's floors, solids and KO lines (pure maths)
    //   match: Samus + 3 idle bots on the stage: everyone placed on a spawn on the plane; running moves along z only; the
    //          jump rises SideViewJump (also after a live change); Down drops through the soft walkway onto the lower floor,
    //          a jump from there lands back on the walkway; the hexes are solid (a side hex's side stops a run, its top
    //          catches a drop from the walkway; no drop through the centre one); past a KO line the player is put back on a
    //          spawn. -v traces the main player's first 600 frames
    //   menu: GAME MODE's dev row (MultiplayerMenu.SideView.cs): shown in a dev build; a tap on it, or the d-pad down to it
    //          and A, opens BATTLE's rules page and START GAME's setup carries sv=1 with Data Shrine; a plain BATTLE after it
    //          (a new CREATE GAME) plays a plain match; a public build has no row
    internal static class SideViewTest
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static bool _pass = true;
        static bool _verbose;

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction()) return;
            _verbose = Array.IndexOf(args, "-v") >= 0;
            try
            {
                Stage();
                Match();
                Menu();
            }
            finally
            {
                MphRecomp.Config.RecompSettings.PublicBuild = false;
                PlayerEntity.SideViewOn = false;
                PlayerEntity.SideStage = null;
                PlayerEntity.SideViewJump = PlayerEntity.SideViewJumpDefault;
                PlayerEntity.SideViewRunScale = 1;
                PlayerEntity.SideViewKOs = 0;
                PlayerEntity.SideViewStanceHeld = false;
                PlayerEntity.HostIdleBots = false;
                PlayerEntity.HostSpawnAtOnce = false;
            }
            Console.WriteLine(_pass ? "PASS sideviewtest" : "FAIL sideviewtest");
        }

        static void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"[{(ok ? "ok" : "FAIL")}] {what}{(detail.Length > 0 ? ": " + detail : "")}");
            _pass &= ok;
        }

        static string F(float v) => v.ToString("0.000", Inv);

        static void Stage()
        {
            SideViewStage s = DataShrineStage.Create();
            Vector2[] walk = s.SoftFloors[0];
            Check("stage: walkway at the end walls", MathF.Abs(SideViewStage.FloorY(walk, -18.84f) - 3.53f) < 1e-4f);
            Check("stage: walkway on the hex's top in the middle", MathF.Abs(SideViewStage.FloorY(walk, 0) - 3.74f) < 1e-4f);
            Check("stage: walkway ramps between", SideViewStage.FloorY(walk, -13.75f) is > 3.53f and < 3.74f);
            // (owner 11-12, "there should be some room between off screen and actually hitting the kill zone")
            Check("stage: KO lines the gap past the end walls", s.PastKo(new Vector3(0, 2, 24.9f)) && s.PastKo(new Vector3(0, 2, -24.9f))
                && s.PastKo(new Vector3(0, 31.4f, 0)) && s.PastKo(new Vector3(0, -11.4f, 0)) && !s.PastKo(new Vector3(0, 2, 24.8f))
                && !s.PastKo(new Vector3(0, 2, -24.8f)) && !s.PastKo(new Vector3(0, 2, 18.84f)),
                $"KO z {F(s.KoLeft)} .. {F(s.KoRight)}");
            Check("stage: the floors run on past the KO lines", MathF.Abs(SideViewStage.FloorY(walk, 30) - 3.53f) < 1e-4f
                && MathF.Abs(SideViewStage.FloorY(walk, -30) - 3.53f) < 1e-4f);
            SideViewStage g = DataShrineStage.Create();
            g.KoLeft = g.CamZMin - 10;
            g.KoRight = g.CamZMax + 10;
            Check("stage: the KO gap moves the side lines (keys 9 0)", !g.PastKo(new Vector3(0, 2, 28.7f)) && g.PastKo(new Vector3(0, 2, 28.9f))
                && !g.PastKo(new Vector3(0, 2, -28.7f)) && g.PastKo(new Vector3(0, 2, -28.9f)));
            Check("stage: the camera stops at the end walls", s.CamZMin == -18.84f && s.CamZMax == 18.84f);
            // a body falling onto the soft floor lands; one rising through it doesn't; one dropping falls on
            var prev = new Vector3(0, 4.0f, -20);
            var pos = new Vector3(0, 3.4f, -20);
            var speed = new Vector3(0, -0.5f, 0);
            SideViewStage.Contact c = s.Resolve(prev, ref pos, ref speed, 0, 2, 0.5f, dropping: false);
            Check("stage: lands on the soft floor from above", c.Standing && c.Soft && MathF.Abs(pos.Y - 3.53f) < 1e-4f, F(pos.Y));
            prev = new Vector3(0, 3.0f, -20);
            pos = new Vector3(0, 3.6f, -20);
            speed = new Vector3(0, 0.5f, 0);
            c = s.Resolve(prev, ref pos, ref speed, 0, 2, 0.5f, dropping: false);
            Check("stage: rises through the soft floor", !c.Standing && !c.Bonk && MathF.Abs(pos.Y - 3.6f) < 1e-4f, F(pos.Y));
            prev = new Vector3(0, 3.53f, -20);
            pos = new Vector3(0, 3.4f, -20);
            speed = new Vector3(0, -0.2f, 0);
            c = s.Resolve(prev, ref pos, ref speed, 0, 2, 0.5f, dropping: true);
            Check("stage: dropping falls through the soft floor", !c.Standing && MathF.Abs(pos.Y - 3.4f) < 1e-4f, F(pos.Y));
            prev = new Vector3(0, 3.74f, 0);
            pos = new Vector3(0, 3.6f, 0);
            speed = new Vector3(0, -0.2f, 0);
            c = s.Resolve(prev, ref pos, ref speed, 0, 2, 0.5f, dropping: true);
            Check("stage: the centre hex's top holds even when dropping", c.Standing && !c.Soft && MathF.Abs(pos.Y - 3.74f) < 1e-4f, F(pos.Y));
            prev = new Vector3(0, 0, -10);
            pos = new Vector3(0, 0, -8.0f);
            speed = new Vector3(0, 0, 1);
            c = s.Resolve(prev, ref pos, ref speed, 0, 2, 0.5f, dropping: false);
            Check("stage: the left hex's side pushes back", c.Lateral && MathF.Abs(pos.Z - (-8.24f - 0.5f)) < 1e-4f, F(pos.Z));
            prev = new Vector3(0.3f, 0, -20);
            pos = new Vector3(0.4f, 0, -20);
            speed = new Vector3(0.1f, 0, 0);
            s.Resolve(prev, ref pos, ref speed, 0, 2, 0.5f, dropping: false);
            Check("stage: kept on the plane", pos.X == 0 && speed.X == 0);
        }

        static void Match()
        {
            PlayerEntity.SideViewOn = true;
            PlayerEntity.SideStage = DataShrineStage.Create();
            PlayerEntity.SideViewJump = PlayerEntity.SideViewJumpDefault;
            PlayerEntity.SideViewRunScale = 1;
            PlayerEntity.SideViewKOs = 0;
            PlayerEntity.HostIdleBots = true;
            PlayerEntity.HostSpawnAtOnce = true; // as the app's ConfigureSideView: no FIRE to spawn
            MatchSettings settings = MatchSettings.Quick(GameMode.Battle, DataShrineStage.Room, Hunter.Samus, 3, 1);
            settings.TimeLimitSeconds = -1;
            settings.PointGoal = 99;
            settings.BotStuckFix = false;
            using CampaignHost host = CampaignHost.StartMatch(settings);
            PlayerEntity me = PlayerEntity.Players[0];
            for (int f = 0; f < 600; f++)
            {
                host.Step(default);
                if (_verbose && (f < 20 || f % 25 == 0))
                {
                    Console.WriteLine($"  f{f}: x {F(me.Position.X)} y {F(me.Position.Y)} z {F(me.Position.Z)} speed ({F(me.Speed.Y)}, {F(me.Speed.Z)}) health {me.Health} KOs {PlayerEntity.SideViewKOs}");
                }
            }
            SideViewStage stage = PlayerEntity.SideStage!;
            float minY = Fixed.ToFloat(me.Values.MinPickupHeight);
            float Feet(PlayerEntity p) => p.Position.Y + Fixed.ToFloat(p.Values.MinPickupHeight);
            for (int i = 0; i < PlayerEntity.Players.Count; i++)
            {
                PlayerEntity p = PlayerEntity.Players[i];
                if (!p.LoadFlags.TestFlag(LoadFlags.Active)) continue;
                Vector2 spawn = stage.Spawns[p.SlotIndex % stage.Spawns.Length];
                bool onPlane = MathF.Abs(p.Position.X) < 1e-3f;
                bool atSpawn = MathF.Abs(p.Position.Z - spawn.X) < 0.75f && MathF.Abs(Feet(p) - spawn.Y) < 0.25f;
                Check($"placed: seat {i} {p.Hunter}{(p.IsBot ? " (bot)" : "")} slot {p.SlotIndex}", onPlane && atSpawn,
                    $"x {F(p.Position.X)} z {F(p.Position.Z)} feet {F(Feet(p))}, spawn ({spawn.X}, {spawn.Y}), health {p.Health}");
            }

            // run right along z
            float z0 = me.Position.Z;
            float maxX = 0;
            for (int f = 0; f < 40; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(1, 0) });
                maxX = MathF.Max(maxX, MathF.Abs(me.Position.X));
            }
            Settle(host, 40);
            Check("run: right moves along +z on the plane", me.Position.Z > z0 + 2 && maxX < 1e-3f, $"z {F(z0)} -> {F(me.Position.Z)}, |x| max {F(maxX)}");

            // the jump's rise, at the default and after a live change
            foreach (float j in new[] { PlayerEntity.SideViewJumpDefault, 6f })
            {
                PlayerEntity.SideViewJump = j;
                float rise = JumpRise(host, me);
                Check($"jump: rises J {j:0.0}", MathF.Abs(rise - j) <= 0.15f, $"rise {F(rise)}");
            }
            PlayerEntity.SideViewJump = PlayerEntity.SideViewJumpDefault;

            // Down on the walkway (z -17.5: not over a hex, inside the KO line) drops to the lower floor
            Put(host, me, -17.5f, 3.53f);
            float walkFeet = Feet(me);
            for (int f = 0; f < 60; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, -1) });
            }
            Settle(host, 30);
            Check("drop: Down falls through the soft walkway to the floor", MathF.Abs(walkFeet - 3.53f) < 0.05f && MathF.Abs(Feet(me)) < 0.05f,
                $"feet {F(walkFeet)} -> {F(Feet(me))}");
            // a jump from the floor rises through the walkway and lands on it
            host.Step(new CampaignInput { Buttons = CampaignButtons.Jump });
            Settle(host, 90);
            Check("soft: a jump from the floor lands on the walkway", MathF.Abs(Feet(me) - 3.53f) < 0.05f, $"feet {F(Feet(me))}");

            // the centre hex's top: Down doesn't drop through
            Put(host, me, 0, 3.74f);
            for (int f = 0; f < 40; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, -1) });
            }
            Check("solid: no drop through the centre hex", MathF.Abs(Feet(me) - 3.74f) < 0.05f, $"feet {F(Feet(me))}");

            // the left hex's side stops a run along the floor (started clear of Spire's spawn at
            // z -14: a player at a bot's exact eye depth trips the AI's Debug.Assert(w != 0) in Matrix.ProjectPosition)
            Put(host, me, -11, 0);
            for (int f = 0; f < 60; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(1, 0) });
            }
            float halfZ = Fixed.ToFloat(me.Values.BipedColRadius);
            Check("solid: the left hex's side stops the run", me.Position.Z <= -8.24f - halfZ + 0.01f && MathF.Abs(Feet(me)) < 0.05f,
                $"z {F(me.Position.Z)} feet {F(Feet(me))}");
            // the walkway runs over the side hexes: Down from it over the left hex lands on the hex's top, which holds
            Put(host, me, -6, 3.74f);
            for (int f = 0; f < 40; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, -1) });
            }
            Settle(host, 30);
            float hexFeet = Feet(me);
            for (int f = 0; f < 40; f++)
            {
                host.Step(new CampaignInput { Move = new System.Numerics.Vector2(0, -1) });
            }
            Check("solid: Down from the walkway lands on the left hex's top, and it holds", MathF.Abs(hexFeet - 2.14f) < 0.05f
                && MathF.Abs(Feet(me) - 2.14f) < 0.05f, $"z {F(me.Position.Z)} feet {F(hexFeet)} -> {F(Feet(me))}");
            // past the end wall but short of the KO line: still in play (off-screen, in a bubble)
            int kos = PlayerEntity.SideViewKOs;
            Put(host, me, 22, 3.53f, settle: false);
            host.Step(default);
            host.Step(default);
            Check("KO: past the end wall, short of the line, still in play", PlayerEntity.SideViewKOs == kos
                && me.Position.Z > 21, $"KOs {kos} -> {PlayerEntity.SideViewKOs}, z {F(me.Position.Z)}");
            // past the right KO line: put back on a spawn
            Put(host, me, 25.5f, 3.53f, settle: false);
            host.Step(default);
            host.Step(default);
            Vector2 back = stage.Spawns[me.SlotIndex % stage.Spawns.Length];
            Check("KO: past z 24.84 counts and puts the player back", PlayerEntity.SideViewKOs == kos + 1
                && MathF.Abs(me.Position.Z - back.X) < 0.75f && MathF.Abs(me.Position.X) < 1e-3f,
                $"KOs {kos} -> {PlayerEntity.SideViewKOs}, z {F(me.Position.Z)}");
            Aim(host, me);
            _ = minY;
        }

        // the aim all the way round (owner 11-12, "aim in a full circle around you ... like you can in dread") and the
        // stance: held, the player stands still, the move stick aims and the laser stops at what the shot would meet
        static void Aim(CampaignHost host, PlayerEntity me)
        {
            Check("aim: the ray meets a box ahead at its near side", MathF.Abs(PlayerEntity.SideViewRayBox(0, 0, 1, 0, 2, 3, -1, 1) - 2) < 1e-5f);
            Check("aim: the ray misses a box beside it", PlayerEntity.SideViewRayBox(0, 0, 0, 1, 2, 3, -1, 1) < 0);
            Check("aim: the ray starts inside a box", PlayerEntity.SideViewRayBox(2.5f, 0, -1, 1, 2, 3, -1, 1) == 0);
            Put(host, me, -11, 0);
            try
            {
                PlayerEntity.SideViewStickX = 0;
                PlayerEntity.SideViewStickY = -1; // (+y is down)
                for (int f = 0; f < 3; f++) host.Step(default);
                Vector2 up = me.SideViewAimDir;
                PlayerEntity.SideViewStickY = 0;
                for (int f = 0; f < 20; f++) host.Step(default);
                Vector2 held = me.SideViewAimDir;
                Check("aim: the stick points straight up, and the aim stays there when it's let go", up.Y > 0.999f && held.Y > 0.999f,
                    $"({F(up.X)}, {F(up.Y)}), let go ({F(held.X)}, {F(held.Y)})");
                PlayerEntity.SideViewStickX = -0.5f;
                PlayerEntity.SideViewStickY = -0.866f;
                for (int f = 0; f < 3; f++) host.Step(default);
                Vector2 over = me.SideViewAimDir;
                PlayerEntity.SideViewStickX = -1;
                PlayerEntity.SideViewStickY = 0.5f;
                for (int f = 0; f < 3; f++) host.Step(default);
                Vector2 back = me.SideViewAimDir;
                Check("aim: on over the top and down behind", over.X < -0.45f && over.Y > 0.8f && back.X < -0.85f && back.Y < -0.4f,
                    $"({F(over.X)}, {F(over.Y)}), ({F(back.X)}, {F(back.Y)})");
                PlayerEntity.SideViewStickX = PlayerEntity.SideViewStickY = 0;
                PlayerEntity.SideViewStanceHeld = true;
                float z0 = me.Position.Z;
                for (int f = 0; f < 30; f++)
                {
                    host.Step(new CampaignInput { Move = new System.Numerics.Vector2(1, 0) });
                }
                Vector2 aim = me.SideViewAimDir;
                float laser = (me.SideViewLaserEnd - me.SideViewMuzzle).Length;
                Check("stance: stands still, the move stick aims, the laser stops at the left hex's side",
                    me.SideViewStance && MathF.Abs(me.Position.Z - z0) < 0.05f && aim.X > 0.999f && laser is > 0.2f and < 4,
                    $"z {F(z0)} -> {F(me.Position.Z)}, aim ({F(aim.X)}, {F(aim.Y)}), laser {F(laser)} to z {F(me.SideViewLaserEnd.Z)}");
                PlayerEntity.SideViewStanceHeld = false;
                for (int f = 0; f < 30; f++)
                {
                    host.Step(new CampaignInput { Move = new System.Numerics.Vector2(-1, 0) });
                }
                Check("stance: let go, the player runs again", !me.SideViewStance && me.Position.Z < z0 - 1, $"z {F(z0)} -> {F(me.Position.Z)}");
                // the free mouse reticle (owner 11-12, "can we get the reticle to follow the mouse freely"): it goes where
                // the mouse takes it, keeps to the frame, stays put on the screen when the camera moves, and the aim
                // points at it
                Put(host, me, -11, 0);
                PlayerEntity.SideViewFrameCentre = new Vector2(-5, 6);
                PlayerEntity.SideViewFrameHalf = new Vector2(19, 10.7f);
                host.Step(new CampaignInput { AimDelta = new System.Numerics.Vector2(40, 0) });
                Vector2 r0 = me.SideViewReticle;
                for (int f = 0; f < 3; f++) host.Step(new CampaignInput { AimDelta = new System.Numerics.Vector2(200, 0) });
                Vector2 r1 = me.SideViewReticle;
                Vector2 toward = me.SideViewAimDir;
                Vector2 muzzle = new Vector2(me.SideViewMuzzle.Z, me.SideViewMuzzle.Y);
                Vector2 want = (r1 - muzzle).Normalized();
                for (int f = 0; f < 4; f++) host.Step(new CampaignInput { AimDelta = new System.Numerics.Vector2(400, 0) });
                Vector2 edge = me.SideViewReticle;
                PlayerEntity.SideViewFrameCentre = new Vector2(-3, 6);
                host.Step(default);
                Vector2 moved = me.SideViewReticle;
                Check("mouse: the reticle follows it freely and the aim points at it", MathF.Abs(r1.X - r0.X - 15) < 1e-3f
                    && MathF.Abs(r1.Y - r0.Y) < 1e-3f && Vector2.Dot(toward, want) > 0.999f,
                    $"({F(r0.X)}, {F(r0.Y)}) -> ({F(r1.X)}, {F(r1.Y)}), aim ({F(toward.X)}, {F(toward.Y)})");
                Check("mouse: the reticle keeps to the frame, and moves with it", MathF.Abs(edge.X - 13.5f) < 1e-3f
                    && MathF.Abs(moved.X - 15.5f) < 1e-3f, $"edge z {F(edge.X)}, camera +2: z {F(moved.X)}");
            }
            finally
            {
                PlayerEntity.SideViewStickX = PlayerEntity.SideViewStickY = 0;
                PlayerEntity.SideViewStanceHeld = false;
                PlayerEntity.SideViewFrameCentre = PlayerEntity.SideViewFrameHalf = Vector2.Zero;
            }
        }

        static void Menu()
        {
            const int pick = 2360; // MultiplayerMenu.SideViewPickCall
            int[] Row(FrontendSession s) => Enumerable.Range(0, s.Menu.Page!.Items.Count)
                .Where(i => s.Menu.Page.Items[i].Actions.Any(a => a.Calls.Any(c => c.B == pick))).ToArray();
            bool Hidden(FrontendSession s) => Row(s).All(i => s.Menu.ItemState(i) == MenuState.Hidden);
            bool Tap(FrontendSession s)
            {
                foreach (int i in Row(s))
                {
                    MenuItem it = s.Menu.Page!.Items[i];
                    if (s.Menu.ItemState(i) == MenuState.Hidden) continue;
                    foreach (MenuAction a in it.Actions)
                    {
                        if (!a.Calls.Any(c => c.B == pick) || !MenuEngine.TryRect(a, it, out float x0, out float y0, out float x1, out float y1)) continue;
                        s.TouchDs((x0 + x1) / 2, 192 - (y0 + y1) / 2);
                        return true;
                    }
                }
                return false;
            }
            // as -mpmenu modmodes: MULTIPLAYER, LOCAL, CREATE GAME, `choose` on the mode page at frame 140, then the rules
            // page's OK and on through the lobby (a few bots) to START GAME
            (MatchSettings? Setup, string Raw, int RulesPage, bool Saw) Go(Func<FrontendSession, bool> choose)
            {
                string? setup = null;
                int rules = -1;
                bool saw = false;
                FrontendSession session = FrontendSession.Load(Paths.FileSystem);
                session.Request = r => { if (r.Kind == FrontendRequestKind.StartMatch) setup = r.Arg; };
                session.Start(18);
                string script = "20:ds@172/72,60:ds@48/72,100:ds@48/72,240:ds@212/173,280:ds@26/134,300:ds@70/77,340:ds@223/170,"
                    + "360:ds@223/170,380:ds@223/170,440:ds@98/139,450:ds@154/63,460:ds@212/139,500:ds@195/41";
                var steps = script.Split(',').Select(t => t.Split(':')).Select(p => (F: Int32.Parse(p[0]), In: p[1][3..].Split('/'))).ToList();
                for (int f = 0; f <= 620 && setup == null; f++)
                {
                    if (f == 140) saw = session.Menu.Page?.Index == MultiplayerMenu.ModePage && choose(session);
                    if (f == 220) rules = session.Menu.Page?.Index ?? -1;
                    foreach (var st in steps.Where(t => t.F == f)) session.TouchDs(Single.Parse(st.In[0], Inv), Single.Parse(st.In[1], Inv));
                    session.Tick();
                }
                return (setup == null ? null : MatchSetupText.Read(setup), setup ?? $"no START GAME (page {session.Menu.Page?.Index})", rules, saw);
            }
            bool SideOn(MatchSettings? s) => s is { SideView: true, Mode: GameMode.Battle } && s.Arena == DataShrineStage.Room;

            MphRecomp.Config.RecompSettings.PublicBuild = false;
            var tap = Go(s => !Hidden(s) && Tap(s));
            Check("menu: the dev row shows; a tap opens BATTLE's rules page, the setup has sv=1 on Data Shrine",
                tap.Saw && tap.RulesPage == 28 && SideOn(tap.Setup) && tap.Raw.Contains("sv=1"), $"rules page {tap.RulesPage}, {tap.Raw}");

            int presses = -1;
            var pad = Go(s =>
            {
                int box = Row(s).FirstOrDefault(-1); // (the box: the row's one item with its call)
                for (int n = 0; n < 12 && box >= 0; n++)
                {
                    if (s.Menu.FocusedItem() == box)
                    {
                        presses = n;
                        break;
                    }
                    s.Navigate(0, -1);
                    for (int w = 0; w < 4; w++) s.Tick();
                }
                if (presses < 0) return false;
                s.Press(MenuKeys.A);
                return true;
            });
            Check("menu: d-pad down to the row + A, the same", pad.Saw && pad.RulesPage == 28 && SideOn(pad.Setup),
                $"{presses} presses, rules page {pad.RulesPage}, {pad.Raw}");

            // the pick is remembered across the menu (static); a new CREATE GAME and BATTLE drops it
            var plain = Go(s =>
            {
                s.TouchDs(44, 68); // BATTLE
                return true;
            });
            Check("menu: a new CREATE GAME + BATTLE after it: a plain match, no sv", plain.Saw && plain.Setup is { SideView: false }
                && !plain.Raw.Contains("sv="), plain.Raw);

            MphRecomp.Config.RecompSettings.PublicBuild = true;
            var pub = Go(s =>
            {
                bool hidden = Hidden(s) && Row(s).Length > 0;
                s.TouchDs(44, 68);
                return hidden;
            });
            MphRecomp.Config.RecompSettings.PublicBuild = false;
            Check("menu: a public build has no row (BATTLE plays plain)", pub.Saw && pub.Setup is { SideView: false }, pub.Raw);
        }

        // the most the feet rise above where they were, from one jump press
        static float JumpRise(CampaignHost host, PlayerEntity me)
        {
            Settle(host, 20);
            float start = me.Position.Y, peak = start;
            host.Step(new CampaignInput { Buttons = CampaignButtons.Jump });
            for (int f = 0; f < 120; f++)
            {
                peak = MathF.Max(peak, me.Position.Y);
                host.Step(default);
            }
            Settle(host, 30);
            return peak - start;
        }

        static void Put(CampaignHost host, PlayerEntity me, float z, float feet, bool settle = true)
        {
            me.Position = new Vector3(0, feet - Fixed.ToFloat(me.Values.MinPickupHeight) + 0.02f, z);
            me.Speed = Vector3.Zero;
            if (settle) Settle(host, 20);
        }

        static void Settle(CampaignHost host, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                host.Step(default);
            }
        }
    }
}
