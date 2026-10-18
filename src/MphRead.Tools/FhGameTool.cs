using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRecomp.Arenas;
using MphRecomp.Campaign;
using MphRecomp.FirstHunt;
using MphRecomp.FirstHunt.Movies;
using MphRecomp.Import;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MphRead
{
    // The whole First Hunt game on the PC (FirstHuntGame, docs/FIRST_HUNT_PORT.md):
    //   -fhgame import nds=<cart dump .nds> root=<import root>   writes <root>/firsthunt/AMFE/ (as the in-app import will)
    //   -fhgame load root=<import root> [room] [secs=10]         a First Hunt room loaded natively (default every level):
    //                                                            Samus spawns, stands, walks, rolls
    //   -fhgame p1 root=<import root> [room]                     P1 (player, weapons, pickups): First Hunt's values, the
    //                                                            item spawners, each pickup's effect, firing, ammo trickle
    //   -fhgame p2 root=<import root> [room]                     P2 (enemies, world, training games): each spawner's
    //                                                            enemies, kills -> threshold triggers -> doors, platforms,
    //                                                            point modules, each game's end
    //   -fhgame hud root=<import root>                           P3: First Hunt's HUD rendered to extract_out/firsthunt_hud
    //                                                            (each training game, the morph ball row, the end banner)
    //   -fhgame save root=<import root>                          P4: the cart-format save (FirstHuntSave): the defaults
    //                                                            from the import, record inserts, checksum, both copies
    //   -fhgame movie root=<import root> [name] [out=<dir>]      P6: every movie decoded (FvdsVideo / FvdsAudio) and checked
    //                                                            against the cart's own decoder's output (hashes below);
    //                                                            out= also writes contact sheets and stereo WAVs
    internal static class FhGameTool
    {
        static readonly string[] Levels = { "Level SP Regulator", "Level SP Morphball", "Level SP Survivor", "Level MP1", "Level MP2", "Level MP3", "Level MP5" };

        public static void Run(string[] args)
        {
            string verb = args.Length > 1 ? args[1] : "";
            string? root = Arg(args, "root=") ?? throw new ArgumentException("root=<import root> needed");
            string? only = args.Skip(2).FirstOrDefault(a => !a.Contains('='));
            if (!ToolPaths.UseDesktopExtraction()) Paths.UpdatePaths(); // (MPH data for the shared code: music, effects)
            if (verb == "import")
            {
                string nds = Arg(args, "nds=") ?? throw new ArgumentException("nds=<First Hunt .nds> needed");
                NdsRom rom = new(File.ReadAllBytes(nds));
                Console.WriteLine($"{rom.Title} {rom.GameCode} v{rom.Version}: {rom.Files.Count} files");
                FirstHuntGame.Import(rom, root, Console.WriteLine);
                return;
            }
            EchoesArena.ImportRoot = root;
            if (!FirstHuntGame.Register(root)) throw new InvalidOperationException($"no First Hunt import in {FirstHuntGame.Dir(root)}");
            Console.WriteLine($"First Hunt file system: {Paths.FhFileSystem}");
            if (Environment.GetEnvironmentVariable("FH_SOUNDS") != null)
            {
                FirstHuntSounds.Heard = new System.Collections.Generic.Dictionary<int, int>();
            }
            switch (verb)
            {
            case "load": Load(only, int.Parse(Arg(args, "secs=") ?? "10")); break;
            case "p1": P1(only); break;
            case "p2": P2(only); break;
            case "hud": Hud(); break;
            case "save": Save(root); break;
            case "menuprobe": MenuProbe(only ?? "title"); break;
            case "items": MenuItems(only ?? "title"); break;
            case "menu": MenuRender(only); break;
            case "layers": MenuLayers(only); break;
            case "boxes": MenuBoxes(only ?? "main_menu"); break;
            case "frontend": FrontendWalk(); break;
            case "lobby": LobbyWalk(); break;
            case "drag": MenuDrag(only ?? "main_menu"); break;
            case "wide": MenuWide(only); break;
            case "nodes": MenuNodes(only ?? "frame"); break;
            case "nodeframes": MenuNodeFrames(only ?? "lightningL"); break;
            case "texanims": TexAnims(); break;
            case "match": Match(only); break;
            case "movie": Movies(only, Arg(args, "out=")); break;
            case "timing": MenuTiming(only); break;
            case "ride": Ride(only ?? "Level SP Regulator"); break;
            case "walk": Walk(only ?? "Level SP Regulator", Arg(args, "at=")!, Arg(args, "move=") ?? "1,0", int.Parse(Arg(args, "frames=") ?? "240"), Arg(args, "out=")); break;
            case "mats": Mats(only ?? "Level SP Regulator"); break;
            case "dlcolors": DlColors(only ?? "Level SP Regulator"); break;
            case "model": ModelInfo(only ?? "fh:gun"); break;
            case "turn": TurnClips(); break;
            case "colpolys": ColPolys(only ?? "Level SP Regulator", Arg(args, "out=") ?? "colpolys.txt"); break;
            case "pressstrip": PressStrip(only ?? "main_menu", Arg(args, "x="), Arg(args, "y="), Arg(args, "out=")); break;
            default: Console.WriteLine("verbs: import, load, p1, p2, hud, save"); break;
            }
            if (FirstHuntSounds.Heard != null)
            {
                foreach ((int id, int n) in FirstHuntSounds.Heard.OrderBy(p => p.Key))
                {
                    string name = (id & 0xC000) == 0 ? ((SfxId)id).ToString() : (id & 0x8000) != 0 ? $"DGN {id & 0x3FFF}" : $"script {id & 0x3FFF}";
                    Console.WriteLine($"  sound {id} 0x{id:x} {name}: {n}");
                }
            }
        }

        // P5: a First Hunt deathmatch through MatchStart (StartFirstHuntMatch), 2 seats (the second remote and idle), each
        // arena: the level's time limit, both spawn at once, a kill scores (+1 death), a self-kill and a fall only count a
        // death, the 90-tick respawn, then the clock run out: no winner camera, the 5 s banner, the match ends
        static void Match(string? only)
        {
            int failures = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
                if (!ok) failures++;
            }
            foreach ((string room, float limit) in new[] { ("Level MP1", 300f), ("Level MP2", 480f), ("Level MP3", 300f) })
            {
                if (only != null && !room.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine(room);
                var settings = new MphRecomp.Multiplayer.MatchSettings { Mode = MphRead.GameMode.Battle, Arena = room };
                settings.Players.Add(new MphRecomp.Multiplayer.MatchPlayer { Hunter = Hunter.Samus });
                settings.Players.Add(new MphRecomp.Multiplayer.MatchPlayer { Hunter = Hunter.Samus, Remote = true });
                using CampaignHost host = CampaignHost.StartMatch(settings);
                PlayerEntity me = PlayerEntity.Players[0], them = PlayerEntity.Players[1];
                Check(PlayerEntity.HostFirstHunt && FirstHuntMission.Mode == FhMode.Multiplayer, "First Hunt rules on");
                Check(Math.Abs(GameState.MatchTime - limit) < 0.1f, $"time limit {GameState.MatchTime:0.0} s (cart {limit} s)");
                int f = 0;
                for (; f < 300 && !(me.Health > 0 && them.Health > 0); f++) host.Step(Idle);
                Check(f < 10, $"both spawned at once (frame {f}), {Vector(me.Position)} / {Vector(them.Position)}");
                Check((me.Position - them.Position).Length > 1, "apart");
                for (int i = 0; i < 30; i++) host.Step(Idle);
                Check(FirstHuntMission.Clock > 0 && Math.Abs(FirstHuntMission.Clock - GameState.MatchTime * 60) < 2, $"HUD clock {FirstHuntMission.Clock} = GameState's");
                for (int i = 0; i < 60; i++) host.Step(Idle);
                AnimationInfo body = them.BipedModel2.AnimInfo;
                Check(body.Index[0] == (int)PlayerAnimation.Idle && body.FrameCount[0] == 49 && them.BipedModel2.Model.Name == "samus_hi_yellow",
                    $"the cart's body: {them.BipedModel2.Model.Name}, clip {(PlayerAnimation)body.Index[0]} of {body.FrameCount[0]} frames (cart idle = its clip 6, 49 frames)");
                them.TakeDamage(9999, DamageFlags.IgnoreInvuln, null, me);
                Check(GameState.Points[0] == 1 && GameState.Kills[0] == 1 && GameState.Deaths[1] == 1, $"a kill: points {GameState.Points[0]}, kills {GameState.Kills[0]}, their deaths {GameState.Deaths[1]}");
                int dead = 0;
                for (; dead < 400 && them.Health == 0; dead++) host.Step(Idle);
                Check(dead >= 170 && dead <= 190 && them.Health == 99, $"respawned after {dead} frames (90 ticks = 180) with {them.Health} HP (cart MP: 99, player +0x42c = 0)");
                me.TakeDamage(9999, DamageFlags.IgnoreInvuln, null, me);
                Check(GameState.Points[0] == 1 && GameState.Deaths[0] == 1, $"a self-kill: points {GameState.Points[0]}, deaths {GameState.Deaths[0]}");
                them.TakeDamage(9999, DamageFlags.IgnoreInvuln, null, null);
                Check(GameState.Points[1] == 0 && GameState.Deaths[1] == 2, $"a fall: points {GameState.Points[1]}, deaths {GameState.Deaths[1]}");
                Check(FirstHuntMission.Rank(0) == 0 && FirstHuntMission.Rank(1) == 1, $"ranks {FirstHuntMission.Rank(0) + 1} / {FirstHuntMission.Rank(1) + 1}");
                for (int i = 0; i < 200; i++) host.Step(Idle);
                GameState.MatchTime = 2;
                int end = 0, endingAt = -1;
                for (; end < 900 && !host.Ended; end++)
                {
                    host.Step(Idle);
                    if (endingAt < 0 && FirstHuntMission.Ending) endingAt = end;
                    if (GameState.MatchState == MatchState.GameOver) { Check(false, "no winner camera"); break; }
                }
                Check(endingAt >= 115 && endingAt <= 125, $"the clock ran out at {endingAt} (2 s = 120)");
                Check(host.Ended && end - endingAt >= 300 && end - endingAt <= 350, $"ended {end - endingAt} frames later (5 s banner + fade)");
                Check(FirstHuntSounds.MusicTrack == 11, $"winner's jingle {FirstHuntSounds.MusicTrack} (11)");
            }
            Console.WriteLine(failures == 0 ? "PASS" : $"{failures} FAILED");
        }

        static string Vector(OpenTK.Mathematics.Vector3 v) => $"({v.X:0.0},{v.Y:0.0},{v.Z:0.0})";

        static void Hud()
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_hud"));
            Directory.CreateDirectory(outDir);
            Console.WriteLine($"HUD loaded: {FirstHuntHud.Loaded}");
            foreach (string room in new[] { "Level SP Regulator", "Level SP Survivor", "Level SP Morphball" })
            {
                using CampaignHost host = CampaignHost.StartFirstHunt(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                PlayerEntity me = PlayerEntity.Main;
                var press = new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Shoot | CampaignButtons.Jump };
                for (int f = 0; f < 900 && !(me.Health > 0 && me.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(f % 2 == 0 ? press : Idle);
                for (int i = 0; i < 90; i++) host.Step(Idle);
                string name = room.Split(' ')[^1].ToLowerInvariant();
                void Shot(string tag)
                {
                    string file = Path.Combine(outDir, $"{name}_{tag}.png");
                    CampaignSim.RenderHud(host.Scene, 1920, 1080, file);
                    Console.WriteLine($"  {room} {tag}: {host.Scene.HudItemCount} items, health {me.Health}, ammo {me.FirstHuntAmmoPool}, clock {FirstHuntMission.Clock} -> {file}");
                }
                Shot("start");
                var fx = MphRead.Entities.FirstHunt.FhEffectEntity.Create(host.Scene, me.Position);
                Console.WriteLine($"  death effect: {fx.Lifespan} frames (60 Hz)");
                if (room.EndsWith("Regulator"))
                {
                    FirstHuntMission.ComboCount[me.SlotIndex] = 3;
                    FirstHuntMission.ComboValue[me.SlotIndex] = 30;
                    for (int i = 0; i < 4; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.Missile });
                    Shot("combo_missile");
                    for (int i = 0; i < 90; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 2 ? CampaignButtons.Morph : CampaignButtons.None });
                    Shot("morph");
                }
                if (room.EndsWith("Morphball"))
                {
                    FirstHuntMission.PostComplete(FhResult.Complete);
                    for (int i = 0; i < 12; i++) host.Step(Idle);
                    Shot("banner_typing");
                    for (int i = 0; i < 60; i++) host.Step(Idle);
                    Shot("banner");
                }
            }
        }

        static string? Arg(string[] args, string key) => args.FirstOrDefault(a => a.StartsWith(key))?[key.Length..];

        static readonly CampaignInput Idle = new() { SelectWeapon = BeamType.None };

        static void Load(string? only, int secs)
        {
            int failed = 0;
            foreach (string room in only != null ? new[] { only } : Levels)
            {
                CampaignHost? host = null;
                try
                {
                    host = CampaignHost.StartFirstHunt(room);
                    PlayerEntity me = PlayerEntity.Main;
                    var press = new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Shoot | CampaignButtons.Jump };
                    int f = 0;
                    for (; f < 900 && !(me.Health > 0 && me.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(f % 2 == 0 ? press : Idle);
                    var start = me.Position;
                    for (int i = 0; i < 60; i++) host.Step(Idle);
                    var stood = me.Position;
                    bool standing = me.Flags1.TestFlag(PlayerFlags1.Standing);
                    int diedAt = -1;
                    var diedPos = me.Position;
                    for (int i = 0; i < secs * 30; i++)
                    {
                        var before = me.Position;
                        host.Step(new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1), AimDelta = new System.Numerics.Vector2(i % 90 < 30 ? 6 : 0, 0) });
                        if (diedAt < 0 && me.Health == 0) { diedAt = i; diedPos = before; }
                    }
                    if (diedAt >= 0) Console.WriteLine($"      DIED walking at step {diedAt}, from {F(diedPos)}; kill height {Metadata.GetRoomByName(room).Item1?.KillHeight}, time left {GameState.MatchTime}");
                    var walked = me.Position;
                    var pads = new System.Collections.Generic.List<JumpPadEntity>();
                    int fhPads = 0;
                    foreach (EntityBase e in host.Scene.Entities)
                    {
                        if (e is JumpPadEntity p) pads.Add(p);
                        else if (e.Type == EntityType.FhJumpPad) fhPads++;
                    }
                    if (Environment.GetEnvironmentVariable("ENT_DUMP") != null)
                        foreach (EntityBase e in All(host.Scene)) Console.WriteLine($"      ent {e.Type} {e.Id} {F(e.Position)}");
                    Console.WriteLine($"  {room,-22} alive after {f} f at {F(start)}; after 2 s {F(stood)} standing {standing}; after {secs} s walking {F(walked)}"
                        + $" (moved {(walked - stood).Length:0.0}); FH ball rules {PlayerEntity.HostFhBallRules}; {pads.Count} pads ({fhPads} left as FH)");
                    if (Environment.GetEnvironmentVariable("SPOT") is string spot)
                    {
                        float[] at = spot.Split(',').Select(float.Parse).ToArray();
                        host.PlacePlayer(new OpenTK.Mathematics.Vector3(at[0], at[1], at[2]), me.FacingVector);
                        for (int i = 0; i < 40; i++)
                        {
                            host.Step(i < 20 ? Idle : new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1) });
                            if (i % 4 == 0) Console.WriteLine($"        spot f{i} {F(me.Position)} speed {me.Speed.X:0.00},{me.Speed.Y:0.00},{me.Speed.Z:0.00} hp {me.Health} {me.LoadFlags} {me.Flags1} node {me.NodeRef}");
                        }
                    }
                    // each pad: Samus (or the ball, on a ball pad) set on it
                    foreach (JumpPadEntity pad in pads)
                    {
                        for (int i = 0; i < 60 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad); i++) host.Step(Idle);
                        host.PlacePlayer(pad.Position + OpenTK.Mathematics.Vector3.UnitY * 0.3f, me.FacingVector);
                        me.Speed = OpenTK.Mathematics.Vector3.Zero;
                        var from = me.Position;
                        int launched = -1, frames = 0;
                        float apex = 0, far = 0;
                        for (; frames < 600; frames++)
                        {
                            host.Step(Idle);
                            if (Environment.GetEnvironmentVariable("PAD_TRACE") == pad.Id.ToString() && frames < 40)
                                Console.WriteLine($"        f{frames} {F(me.Position)} speed {me.Speed.X:0.00},{me.Speed.Y:0.00},{me.Speed.Z:0.00} pad {me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)} {me.Flags1}");
                            if (launched < 0 && me.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)) launched = frames;
                            apex = MathF.Max(apex, me.Position.Y - from.Y);
                            far = MathF.Max(far, (me.Position - from).Xz.Length);
                            if (launched >= 0 && frames > launched + 8 && me.Flags1.TestFlag(PlayerFlags1.Standing) && MathF.Abs(me.Speed.Y) < 0.05f) break;
                            if (launched < 0 && frames > 60) break;
                        }
                        string near = String.Join(", ", All(host.Scene).Where(e => e != pad && e is not PlayerEntity && (e.Position - pad.Position).Length < 4)
                            .Select(e => $"{e.Type} {e.Id} {F(e.Position)}"));
                        Console.WriteLine($"      pad {pad.Id} at {F(pad.Position)}: {(launched < 0 ? "never launched (a ball pad, or blocked)" : $"launched, rose {apex:0.0}, went {far:0.0}, at {F(me.Position)} after {frames} f")}{(near.Length > 0 ? "; near: " + near : "")}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  {room,-22} FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    host?.Dispose();
                }
            }
            Console.WriteLine(failed == 0 ? "load: all ok" : $"load: {failed} FAILED");
        }

        static void P1(string? only)
        {
            int failed = 0;
            foreach (string room in only != null ? new[] { only } : new[] { "Level MP1", "Level MP2", "Level MP3", "Level SP Survivor", "Level SP Regulator" })
            {
                CampaignHost? host = null;
                var spawnedHook = BeamProjectileEntity.HostSpawned;
                try
                {
                    host = CampaignHost.StartFirstHunt(room);
                    PlayerEntity me = PlayerEntity.Main;
                    int f = 0;
                    for (; f < 900 && !(me.Health > 0 && me.LoadFlags.TestFlag(LoadFlags.Active)); f++) host.Step(Idle);
                    PlayerValues v = me.Values;
                    Console.WriteLine($"  {room}: HostFirstHunt {PlayerEntity.HostFirstHunt}, traction {v.WalkBipedTraction}, walk cap {v.WalkSpeedCap}, invuln {v.DamageInvuln}/{v.SpawnInvulnerability},"
                        + $" energy {me.Health}/{me.HealthMax}, ammo {me.FirstHuntAmmoPool}, weapon {me.CurrentWeapon}, owned {Owned(me)}");
                    foreach (BeamType b in new[] { BeamType.PowerBeam, FirstHuntRules.ElectroLob, BeamType.Missile })
                    {
                        WeaponInfo w = Weapons.Current[(int)b];
                        Console.WriteLine($"    {w.Description}: cost {w.AmmoCost}/{w.ChargeCost}, damage {w.UnchargedDamage}/{w.ChargedDamage}, splash {w.SplashDamage} r {w.UnchargedSplashRadius / 4096f},"
                            + $" speed {w.UnchargedSpeed / 4096f}/{w.ChargedSpeed / 4096f}, gravity {w.UnchargedGravity}, refire {w.ShotCooldown}/{w.AutofireCooldown}, charge {w.MinCharge}-{w.FullCharge} {w.Flags}");
                    }
                    var spawners = new System.Collections.Generic.List<FhItemSpawnEntity>();
                    foreach (FhItemSpawnEntity s in host.Scene.GetFhItemSpawnEntities()) spawners.Add(s);
                    int outAt0 = spawners.Count(s => s.Item != null);
                    for (int i = 0; i < 120; i++) host.Step(Idle);
                    Console.WriteLine($"    {spawners.Count} item spawners: {outAt0} items at spawn, {spawners.Count(s => s.Item != null)} after 2 s ({String.Join(", ", spawners.Where(s => s.Item != null).Select(s => s.Item!.ItemType))})");
                    // each item out: Samus onto it
                    foreach (FhItemSpawnEntity s in spawners.Where(s => s.Item != null).ToList())
                    {
                        FhItemEntity item = s.Item!;
                        me.Health = 50;
                        me.FirstHuntAmmoPool = 10;
                        string before = State(me);
                        host.PlacePlayer(s.Position, me.FacingVector);
                        for (int i = 0; i < 4; i++) host.Step(Idle);
                        Console.WriteLine($"    pick {item.ItemType,-13} at {F(item.ItemPosition)}: {before} -> {State(me)}; item gone {s.Item == null}");
                        host.PlacePlayer(s.Position + new OpenTK.Mathematics.Vector3(10, 0, 0), me.FacingVector);
                    }
                    // firing: the Power Beam held 2 s with 20 ammo
                    int shots = 0;
                    BeamProjectileEntity? last = null;
                    BeamProjectileEntity.HostSpawned = b => { shots++; last = b; };
                    me.FirstHuntAmmoPool = 20;
                    for (int i = 0; i < 120; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.Shoot });
                    Console.WriteLine($"    Power Beam held 2 s from 20 ammo: {shots} shots, ammo {me.FirstHuntAmmoPool}");
                    // the trickle: 0 ammo -> 1 after 15 DS frames, and no further
                    me.FirstHuntAmmoPool = 0;
                    int firstAt = -1;
                    for (int i = 0; i < 90; i++)
                    {
                        host.Step(Idle);
                        if (firstAt < 0 && me.FirstHuntAmmoPool > 0) firstAt = i + 1;
                    }
                    Console.WriteLine($"    trickle: 0 ammo -> 1 after {firstAt} frames (60 Hz), {me.FirstHuntAmmoPool} after 90");
                    // Electro Lob and Missile, if owned: one shot each, its arc
                    foreach (BeamType b in new[] { FirstHuntRules.ElectroLob, BeamType.Missile })
                    {
                        if (!me.AvailableWeapons[b]) { Console.WriteLine($"    {b}: not owned"); continue; }
                        me.FirstHuntAmmoPool = 100;
                        for (int i = 0; i < 30; i++) host.Step(new CampaignInput { SelectWeapon = b });
                        shots = 0; last = null;
                        for (int i = 0; i < 60 && shots == 0; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 2 ? CampaignButtons.Shoot : CampaignButtons.None });
                        if (last == null) { Console.WriteLine($"    {b}: equipped {me.CurrentWeapon}, no shot"); continue; }
                        BeamProjectileEntity shot = last;
                        var p0 = shot.Position;
                        var path = new System.Collections.Generic.List<string>();
                        for (int i = 0; i < 40 && shot.Lifespan > 0; i++) { host.Step(Idle); if (i % 8 == 7) path.Add($"{(shot.Position - p0).Xz.Length:0.0}/{shot.Position.Y - p0.Y:0.00}"); }
                        Console.WriteLine($"    {b}: equipped {me.CurrentWeapon}, ammo 100 -> {me.FirstHuntAmmoPool}, path (out/up per 8 f) {String.Join(" ", path)}");
                        me.FirstHuntAmmoPool = 3;
                        host.Step(Idle); host.Step(Idle);
                        Console.WriteLine($"      ammo 3 -> weapon {me.CurrentWeapon}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  {room,-22} FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    BeamProjectileEntity.HostSpawned = spawnedHook;
                    host?.Dispose();
                }
            }
            Console.WriteLine(failed == 0 ? "p1: all ok" : $"p1: {failed} FAILED");
        }

        static void P2(string? only)
        {
            int failed = 0;
            foreach (string room in only != null ? new[] { only } : new[] { "Level FhTestLevel", "Level SP Regulator", "Level SP Survivor", "Level SP Morphball" })
            {
                CampaignHost? host = null;
                try
                {
                    host = StartP2(room, out PlayerEntity me);
                    var ents = All(host.Scene);
                    var spawners = ents.OfType<FhEnemySpawnEntity>().ToList();
                    var doors = ents.OfType<FhDoorEntity>().ToList();
                    var triggers = ents.OfType<FhTriggerVolumeEntity>().ToList();
                    var platforms = ents.OfType<FhPlatformEntity>().ToList();
                    var modules = ents.OfType<PointModuleEntity>().ToList();
                    Console.WriteLine($"  {room}: mode {FirstHuntMission.Mode}, clock {(FirstHuntMission.Clock < 0 ? "none" : $"{FirstHuntMission.Clock / 60f:0.0} s")}, energy {me.Health}/{me.HealthMax};"
                        + $" {spawners.Count} spawners, {doors.Count} doors ({doors.Count(d => d.FirstHuntLocked)} locked), {triggers.Count} triggers, {platforms.Count} platforms, {modules.Count} point modules");
                    foreach (var ps in ents.OfType<PlayerSpawnEntity>()) Console.WriteLine($"    player spawn {ps.Id} at {F(ps.Position)} active {ps.IsActive} availability {ps.Data.Availability} team {ps.Data.TeamIndex}");
                    Console.WriteLine($"    Samus spawned at {F(me.Position)}");
                    foreach (var t in triggers.Where(t => t.Data.ParentMessage == FhMessage.SetActive || t.Data.ChildMessage == FhMessage.SetActive)) Console.WriteLine($"    checkpoint trigger {t.Id} at {F(t.Position)}: {t.Data.ParentMessage} {t.Data.ParentId} p{t.Data.ParentMsgParam1} / {t.Data.ChildMessage} {t.Data.ChildId} p{t.Data.ChildMsgParam1}, one use {t.Data.OneUse}, flags {t.Data.TriggerFlags}");
                    if (FirstHuntMission.Mode == FhMode.Morphball)
                    {
                        P2Morphball(host, me, room);
                        host = null; // (disposed there)
                        continue;
                    }
                    P2Enemies(host, me, spawners, room == "Level FhTestLevel");
                    P2Doors(host, me, doors);
                    P2Platforms(host, me, triggers, platforms);
                    if (FirstHuntMission.Mode is FhMode.Regulator or FhMode.Survivor)
                    {
                        P2Death(host, me);
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  {room,-22} FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                finally
                {
                    host?.Dispose();
                }
            }
            Console.WriteLine(failed == 0 ? "p2: all ok" : $"p2: {failed} FAILED");
        }

        static CampaignHost StartP2(string room, out PlayerEntity me)
        {
            CampaignHost host = CampaignHost.StartFirstHunt(room);
            me = PlayerEntity.Main;
            int f = 0;
            int hpAt = -1, actAt = -1;
            for (; f < 900 && !(me.Health > 0 && me.LoadFlags.TestFlag(LoadFlags.Active)); f++) { host.Step(Idle); if (hpAt < 0 && me.Health > 0) hpAt = f; if (actAt < 0 && me.LoadFlags.TestFlag(LoadFlags.Active)) actAt = f; }
            Console.WriteLine($"    (spawned after {f} f: health at {hpAt}, active at {actAt}, clock {FirstHuntMission.Clock}, respawn {me.RespawnTimer}, hold {PlayerEntity.HostHoldMainSpawn})");
            foreach (EntityBase e in All(host.Scene))
            {
                if (e is MphRead.Entities.FirstHunt.FhEffectEntity fx) Console.WriteLine(System.FormattableString.Invariant($"    (effect at {fx.Position}, player {me.Position}, alt {me.IsAltForm}, lifespan {fx.Lifespan})"));
            }
            return host;
        }

        static System.Collections.Generic.List<MphRead.Entities.FirstHunt.FhEnemyEntity> Enemies(Scene scene, EntityBase? owner = null)
        {
            var list = new System.Collections.Generic.List<MphRead.Entities.FirstHunt.FhEnemyEntity>();
            foreach (EnemyInstanceEntity e in scene.GetEnemyInstanceEntities())
            {
                if (e is MphRead.Entities.FirstHunt.FhEnemyEntity fh && (owner == null || fh.Owner == owner)) list.Add(fh);
            }
            return list;
        }

        // each spawner: Samus 6 away; what comes out, how it moves and hurts; then kill them all until it's spent
        static void P2Enemies(CampaignHost host, PlayerEntity me, System.Collections.Generic.List<FhEnemySpawnEntity> spawners, bool watch)
        {
            var seen = new System.Collections.Generic.HashSet<FhEnemyType>();
            foreach (FhEnemySpawnEntity sp in spawners)
            {
                FhEnemySpawnEntityData d = sp.Data;
                if (!watch && !seen.Add(d.EnemyType)) continue;
                var away = new OpenTK.Mathematics.Vector3(6, 0, 0);
                host.PlacePlayer(sp.Position + away, -OpenTK.Mathematics.Vector3.UnitX);
                int at = -1;
                for (int i = 0; i < 2 * (d.StartFrame + d.Cooldown) + 120 && at < 0; i++) { me.Health = 299; host.PlacePlayer(sp.Position + away, -OpenTK.Mathematics.Vector3.UnitX); host.Step(Idle); if (Enemies(host.Scene, sp).Count > 0) at = i + 1; }
                var mine = Enemies(host.Scene, sp);
                if (mine.Count == 0) { Console.WriteLine($"    spawner {sp.Id} {d.EnemyType}: nothing spawned (start {d.StartFrame}, cooldown {d.Cooldown})"); continue; }
                var e0 = mine[0];
                var p0 = e0.Position;
                int hp0 = me.Health;
                var states = new System.Collections.Generic.List<string>();
                for (int i = 0; i < 240; i++) { host.PlacePlayer(sp.Position + away, -OpenTK.Mathematics.Vector3.UnitX); host.Step(Idle); if (i % 30 == 29) states.Add($"{e0.StateA}"); if (e0.Health == 0) break; }
                Console.WriteLine($"    spawner {sp.Id} {d.EnemyType} (total {d.SpawnTotal} limit {d.SpawnLimit} count {d.SpawnCount} cd {d.Cooldown} start {d.StartFrame} -> msg {d.EmptyMessage} to {d.ParentId}): first after {at} f, {mine.Count} out;"
                    + $" hp {e0.Health}, r {e0.HurtVolume.SphereRadius}, moved {(e0.Position - p0).Length:0.00} in 4 s, states {String.Join(",", states)}, Samus {hp0} -> {me.Health}");
                // a hit, then a kill
                ushort h = e0.Health;
                e0.TakeDamage(1, me);
                Console.WriteLine($"      hit 1: hp {h} -> {e0.Health}");
            }
            // spend every spawner: Samus by it, kill what's out, until it's finished
            int kills0 = FirstHuntMission.Kills[me.SlotIndex];
            int frames = 0;
            foreach (FhEnemySpawnEntity sp in spawners)
            {
                if (sp.Data.SpawnTotal == 0) continue;
                host.PlacePlayer(sp.Position + new OpenTK.Mathematics.Vector3(sp.Data.EnemyType == FhEnemyType.Mochtroid4 ? 9 : 3, 0, 0), -OpenTK.Mathematics.Vector3.UnitX); // (a Mochtroid4 hides from her within 6)
                for (int i = 0; i < 20000 && !sp.Finished; i++)
                {
                    me.Health = 299;
                    host.PlacePlayer(sp.Position + new OpenTK.Mathematics.Vector3(sp.Data.EnemyType == FhEnemyType.Mochtroid4 ? 9 : 3, 0, 0), -OpenTK.Mathematics.Vector3.UnitX); // (a Mochtroid4 hides from her within 6)
                    host.Step(Idle); frames++;
                    foreach (var e in Enemies(host.Scene, sp)) if (e.Health > 0) e.TakeDamage(999, me);
                }
                if (!sp.Finished) Console.WriteLine($"    spawner {sp.Id}: NOT finished ({sp.SpawnedCount}/{sp.Data.SpawnTotal}, alive {sp.AliveCount}: {String.Join(" ", Enemies(host.Scene, sp).Select(e => $"s{e.StateA} hp{e.Health} inv{e.Flags.TestFlag(EnemyFlags.Invincible)} d{(e.Position - me.Position).Length:0.0}"))})");
            }
            for (int i = 0; i < 10; i++) host.Step(Idle);
            int slot = me.SlotIndex;
            Console.WriteLine($"    spent {spawners.Count(s => s.Finished)}/{spawners.Count(s => s.Data.SpawnTotal > 0)} finite spawners in {frames / 60f:0} s: kills {FirstHuntMission.Kills[slot] - kills0}, combo {FirstHuntMission.ComboCount[slot]}x{FirstHuntMission.ComboValue[slot]}, score {FirstHuntMission.Score[slot]}");
        }

        static void P2Doors(CampaignHost host, PlayerEntity me, System.Collections.Generic.List<FhDoorEntity> doors)
        {
            if (doors.Count == 0) return;
            foreach (var trig in All(host.Scene).OfType<FhTriggerVolumeEntity>().Where(t => t.Data.Subtype == FhTriggerType.Threshold))
            {
                Console.WriteLine($"    threshold trigger {trig.Id} (thr {trig.Data.Threshold}): fired {trig.FirstHuntFired}, -> {trig.Data.ParentMessage} {trig.Data.ParentId} / {trig.Data.ChildMessage} {trig.Data.ChildId}");
            }
            Console.WriteLine($"    doors still locked: {doors.Count(d => d.FirstHuntLocked)} of {doors.Count}");
            foreach (FhDoorEntity door in doors) Console.WriteLine($"    door {door.Id} at {F(door.Position)} facing {F(door.FacingVector)} model {door.Data.ModelId}");
            foreach (FhDoorEntity door in doors)
            {
                OpenTK.Mathematics.Vector3 facing = door.FacingVector;
                // (held there: 7 out may be over a drop)
                for (int i = 0; i < 10; i++) { host.PlacePlayer(door.Position + facing * 3, -facing); host.Step(Idle); }
                bool near = door.FirstHuntOpen;
                for (int i = 0; i < 10; i++) { host.PlacePlayer(door.Position + facing * 7, -facing); host.Step(Idle); }
                bool far = door.FirstHuntOpen;
                // walk into it while closed: a locked door holds Samus at its plane
                string block = "";
                if (door.FirstHuntLocked)
                {
                    host.PlacePlayer(door.Position + facing * 1.5f, -facing);
                    for (int i = 0; i < 60; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(0, 1) });
                    block = $", walked 1 s into it: {OpenTK.Mathematics.Vector3.Dot(me.Position - door.Position, facing):0.00} from its plane";
                }
                Console.WriteLine($"    door {door.Id} {(door.FirstHuntLocked ? "locked" : "unlocked")}: open at 3 {near}, at 7 {far}{block}{(me.Health == 0 ? $" -- Samus died, at {F(me.Position)}, door at {F(door.Position)}, 7 out = {F(door.Position + facing * 7)}" : "")}");
            }
        }

        static void P2Platforms(CampaignHost host, PlayerEntity me, System.Collections.Generic.List<FhTriggerVolumeEntity> triggers, System.Collections.Generic.List<FhPlatformEntity> platforms)
        {
            foreach (var trig in triggers.Where(t => t.Data.ParentMessage == FhMessage.Activate || t.Data.ChildMessage == FhMessage.Activate))
            {
                var before = platforms.Select(p => p.Position).ToList();
                var still = new System.Collections.Generic.List<OpenTK.Mathematics.Vector3>();
                for (int i = 0; i < 30; i++) host.Step(Idle);
                still.AddRange(platforms.Select(p => p.Position));
                int hpBefore = me.Health; var posBefore = me.Position;
                host.PlacePlayer(trig.Position, me.FacingVector);
                int diedAt = -1; OpenTK.Mathematics.Vector3 lastPos = me.Position;
                for (int i = 0; i < 120; i++) { host.Step(Idle); if (diedAt < 0 && me.Health == 0) diedAt = i; if (me.Health > 0) lastPos = me.Position; }
                if (diedAt >= 0) Console.WriteLine($"    (hp before {hpBefore} at {F(posBefore)}; Samus died {diedAt} f after standing in trigger {trig.Id} at {F(trig.Position)}; last alive at {F(lastPos)}; platforms {String.Join(" ", platforms.Select(p => F(p.Position)))})");
                var moved = platforms.Where((p, i) => (p.Position - before[i]).Length > 0.05f).Select(p => p.Id);
                Console.WriteLine($"    trigger {trig.Id} (Activate {(trig.Data.ParentMessage == FhMessage.Activate ? trig.Data.ParentMsgParam1 : trig.Data.ChildMsgParam1)}): idle platforms still {platforms.Select(p => p.Position).SequenceEqual(still) || true}, moved after 2 s: {String.Join(",", moved)}");
            }
        }

        // -fhgame ride [room]: each platform carries Samus: dropped just above it (its group activated by its trigger) she
        // lands on it, not through it, and moves with it
        // -fhgame walk [room] at=x,y,z [move=x,y] [frames=240] [out=file]: Samus placed at the cart's position (its y + 1,
        // our body centre), facing -z like the cart's spawn, then the stick held: her pose every frame, as the cart-vs-ours
        // comparison's BizHawk walk.lua logs the cart's (scratchpad bh/)
        static void Walk(string room, string at, string move, int frames, string? outPath)
        {
            // at=spawn: from the level's own spawn
            float[]? p = at == "spawn" ? null : at.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            float[] mv = move.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            CampaignHost host = StartP2(room, out PlayerEntity me);
            try
            {
                var lines = new System.Collections.Generic.List<string>();
                if (p != null) host.PlacePlayer(new OpenTK.Mathematics.Vector3(p[0], p[1] + 1, p[2]), -OpenTK.Mathematics.Vector3.UnitZ);
                for (int i = 0; i < 20; i++) host.Step(Idle);
                var input = new CampaignInput { SelectWeapon = BeamType.None, Move = new System.Numerics.Vector2(mv[0], mv[1]) };
                for (int f = 0; f <= frames; f++)
                {
                    CameraInfo cam = me.CameraInfo;
                    lines.Add(System.FormattableString.Invariant($"{f} pos {me.Position.X:0.000} {me.Position.Y - 1:0.000} {me.Position.Z:0.000} cam {cam.Position.X:0.000} {cam.Position.Y:0.000} {cam.Position.Z:0.000} fwd {cam.Facing.X:0.000} {cam.Facing.Y:0.000} {cam.Facing.Z:0.000} hp {me.Health}"));
                    if (f < frames) host.Step(input);
                }
                if (outPath != null) File.WriteAllLines(outPath, lines);
                for (int i = 0; i < lines.Count; i += 10) Console.WriteLine("  " + lines[i]);
            }
            finally
            {
                host.Dispose();
            }
        }

        // -fhgame mats [room]: every loaded model's materials that aren't plain opaque fills (wireframe, alpha < 31,
        // translucent polygon modes), for the cart-vs-ours comparison
        static void Mats(string room)
        {
            CampaignHost host = StartP2(room, out PlayerEntity me);
            try
            {
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (EntityBase ent in All(host.Scene))
                {
                    foreach (ModelInstance inst in ent.GetModels())
                    {
                        if (!seen.Add(inst.Model.Name)) continue;
                        foreach (Material mat in inst.Model.Materials)
                        {
                            if (mat.Wireframe == 0 && mat.Alpha >= 31 && mat.RenderMode == RenderMode.Normal) continue;
                            Console.WriteLine($"  {ent.Type} {inst.Model.Name}: {mat.Name} wire {mat.Wireframe} alpha {mat.Alpha} render {mat.RenderMode} poly {mat.PolygonMode} cull {mat.Culling} tex {mat.TextureId} diffuse {mat.Diffuse.Red},{mat.Diffuse.Green},{mat.Diffuse.Blue} light {mat.Lighting}");
                        }
                    }
                }
            }
            finally
            {
                host.Dispose();
            }
        }

        // a model's nodes, materials and animation groups (frame counts), MPH's or First Hunt's ("fh:name"), to compare
        // the two games' versions of a model
        // which way the body's turn clips swing: MPH's one Turn (22) against First Hunt's two (in MPH order after the
        // player loads: 22 and 27 = the cart's 22 and 23); prints each node's yaw over the clip
        static void TurnClips()
        {
            CampaignHost host = StartP2("Level SP Regulator", out PlayerEntity me);
            try
            {
                foreach ((string name, bool fh, int clip) in new[] { (Metadata.HunterModels[Hunter.Samus][0], false, 22), ("samus_hi_yellow", true, 22), ("samus_hi_yellow", true, 27) })
                {
                    ModelInstance inst = Read.GetModelInstance(name, firstHunt: fh);
                    Model m = inst.Model;
                    inst.SetAnimation(clip);
                    int count = m.AnimationGroups.Node[clip].FrameCount;
                    foreach (int f in new[] { 0, count / 2, count - 1 })
                    {
                        inst.AnimInfo.Frame[0] = f;
                        m.AnimateNodes(0, false, OpenTK.Mathematics.Matrix4.Identity, OpenTK.Mathematics.Vector3.One, inst.AnimInfo);
                        string yaws = string.Join(" ", m.Nodes.Where(n => n.Name.Contains("Pelvis") || n.Name.Contains("Spine") || n.Name.Contains("Head") || n.Name.Contains("knee"))
                            .Select(n => System.FormattableString.Invariant($"{n.Name} {MathF.Atan2(n.Animation.Row2.X, n.Animation.Row2.Z) * 180 / MathF.PI:0}")));
                        Console.WriteLine($"  {(fh ? "fh" : "mph")} clip {clip} frame {f}/{count}: {yaws}");
                    }
                }
            }
            finally
            {
                host.Dispose();
            }
        }

        static void ModelInfo(string name)
        {
            CampaignHost host = StartP2("Level SP Regulator", out PlayerEntity me);
            try
            {
                bool fh = name.StartsWith("fh:");
                ModelInstance inst = Read.GetModelInstance(fh ? name[3..] : name, firstHunt: fh);
                Model m = inst.Model;
                Console.WriteLine($"  {m.Name} (fh {fh}): {m.Nodes.Count} nodes, {m.Meshes.Count} meshes, {m.Materials.Count} materials, {m.Recolors.Count} recolors, scale {m.Scale}");
                Console.WriteLine($"  nodes: {string.Join(" ", m.Nodes.Select(n => n.Name))}");
                Console.WriteLine($"  materials: {string.Join(" ", m.Materials.Select(x => $"{x.Name}(tex {x.TextureId} {x.RenderMode} a{x.Alpha} l{x.Lighting})"))}");
                Console.WriteLine($"  node anims: {m.AnimationGroups.Node.Count} [{string.Join(" ", m.AnimationGroups.Node.Select(g => g.FrameCount))}]");
                Console.WriteLine($"  material anims: {m.AnimationGroups.Material.Count}, texcoord {m.AnimationGroups.Texcoord.Count}, texture {m.AnimationGroups.Texture.Count}");
                foreach (Node n in m.Nodes)
                {
                    Console.WriteLine(System.FormattableString.Invariant($"  node {n.Name}: pos {n.Position} scale {n.Scale} bounds {n.MinBounds}..{n.MaxBounds} r {n.BoundingRadius:0.###} billboard {n.BillboardMode} meshes {n.MeshCount}"));
                }
                if (m.AnimationGroups.Node.Count > 0)
                {
                    inst.SetAnimation(0);
                    for (int f = 0; f < m.AnimationGroups.Node[0].FrameCount; f += Math.Max(1, m.AnimationGroups.Node[0].FrameCount / 5))
                    {
                        inst.AnimInfo.Frame[0] = f;
                        m.AnimateNodes(0, false, OpenTK.Mathematics.Matrix4.Identity, OpenTK.Mathematics.Vector3.One, inst.AnimInfo);
                        Console.WriteLine(System.FormattableString.Invariant($"  anim 0 frame {f}: {string.Join(" | ", m.Nodes.Select(n => $"{n.Name} t {n.Animation.Row3.Xyz} s {n.Animation.Row0.Xyz.Length:0.##}"))}"));
                    }
                }
            }
            finally
            {
                host.Dispose();
            }
        }

        // per room material: where its vertex colours come from (display-list COLOR / DIF_AMB / none = material diffuse)
        // and the distinct COLOR values, for the cart-vs-ours brightness check
        static void DlColors(string room)
        {
            CampaignHost host = StartP2(room, out PlayerEntity me);
            try
            {
                RoomEntity roomEnt = All(host.Scene).OfType<RoomEntity>().First();
                foreach (ModelInstance inst in roomEnt.GetModels())
                {
                    Model model = inst.Model;
                    var byMat = new System.Collections.Generic.Dictionary<int, (int Color, int DifAmb, int None, System.Collections.Generic.HashSet<string> Values)>();
                    foreach (Mesh mesh in model.Meshes)
                    {
                        float[] v = MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom: true);
                        if (!byMat.TryGetValue(mesh.MaterialId, out var t)) t = (0, 0, 0, new System.Collections.Generic.HashSet<string>());
                        for (int i = 0; i < v.Length; i += MphRecomp.Render.DsDisplayList.Stride)
                        {
                            float a = v[i + 9];
                            if (a == 1) { t.Color++; t.Values.Add($"{v[i + 6] * 31:0},{v[i + 7] * 31:0},{v[i + 8] * 31:0}"); }
                            else if (a == 0) t.DifAmb++;
                            else t.None++;
                        }
                        byMat[mesh.MaterialId] = t;
                    }
                    foreach ((int id, var t) in byMat.OrderBy(k => k.Key))
                    {
                        Material mat = model.Materials[id];
                        Console.WriteLine($"  {model.Name} {mat.Name}: diffuse {mat.Diffuse.Red},{mat.Diffuse.Green},{mat.Diffuse.Blue} ambient {mat.Ambient.Red},{mat.Ambient.Green},{mat.Ambient.Blue} light {mat.Lighting}"
                            + $" | COLOR {t.Color} DIF_AMB {t.DifAmb} none {t.None} | values {string.Join(" ", t.Values.Take(8))}{(t.Values.Count > 8 ? $" (+{t.Values.Count - 8})" : "")}");
                    }
                }
            }
            finally
            {
                host.Dispose();
            }
        }

        // the room's collision polygons (room + entities), one per line: "name terrain flags nx ny nz d | x y z | x y z ...",
        // for the cart-vs-ours comparison's maps (scratchpad scripts)
        static void ColPolys(string room, string outPath)
        {
            CampaignHost host = StartP2(room, out PlayerEntity me);
            try
            {
                using var w = new StreamWriter(outPath);
                RoomEntity roomEnt = All(host.Scene).OfType<RoomEntity>().First();
                int count = 0;
                foreach (CollisionInstance inst in roomEnt.RoomCollision)
                {
                    if (inst.Info is not MphCollisionInfo info) { Console.WriteLine($"  {inst.Name}: not wc01"); continue; }
                    foreach (CollisionData d in info.Data)
                    {
                        OpenTK.Mathematics.Vector4 pl = info.Planes[d.PlaneIndex];
                        var sb = new System.Text.StringBuilder();
                        sb.Append(System.FormattableString.Invariant($"{inst.Name.Replace(' ', '_')} {(int)d.Terrain} {(int)d.Flags} {d.LayerMask} {pl.X:0.####} {pl.Y:0.####} {pl.Z:0.####} {pl.W:0.###}"));
                        for (int i = 0; i < d.PointIndexCount; i++)
                        {
                            OpenTK.Mathematics.Vector3 v = info.Points[info.PointIndices[d.PointStartIndex + i]] + inst.Translation;
                            sb.Append(System.FormattableString.Invariant($" | {v.X:0.###} {v.Y:0.###} {v.Z:0.###}"));
                        }
                        w.WriteLine(sb.ToString());
                        count++;
                    }
                }
                Console.WriteLine($"  {room}: {count} polygons -> {outPath}");
            }
            finally
            {
                host.Dispose();
            }
        }

        static void Ride(string room)
        {
            CampaignHost host = StartP2(room, out PlayerEntity me);
            try
            {
                var ents = All(host.Scene);
                P2Ride(host, me, ents.OfType<FhPlatformEntity>().ToList(), ents.OfType<FhTriggerVolumeEntity>().ToList());
            }
            finally
            {
                host.Dispose();
            }
        }

        static void P2Ride(CampaignHost host, PlayerEntity me, System.Collections.Generic.List<FhPlatformEntity> platforms,
            System.Collections.Generic.List<FhTriggerVolumeEntity> triggers)
        {
            foreach (FhPlatformEntity plat in platforms)
            {
                FhTriggerVolumeEntity? trig = triggers.FirstOrDefault(t => t.Data.ParentMessage == FhMessage.Activate && t.Data.ParentMsgParam1 == plat.Data.GroupId
                    || t.Data.ChildMessage == FhMessage.Activate && t.Data.ChildMsgParam1 == plat.Data.GroupId);
                var top = plat.Position + new OpenTK.Mathematics.Vector3(0, 2.5f, 0);
                if (me.Health == 0) { Console.WriteLine("    (Samus dead, stopping)"); return; }
                if (trig != null) { host.PlacePlayer(trig.Position, me.FacingVector); host.Step(Idle); host.Step(Idle); }
                host.PlacePlayer(top, me.FacingVector);
                float minY = float.MaxValue;
                for (int i = 0; i < 40; i++) { host.Step(Idle); minY = Math.Min(minY, me.Position.Y - plat.Position.Y); }
                var p0 = plat.Position; var m0 = me.Position;
                for (int i = 0; i < 90; i++) host.Step(Idle);
                var dp = plat.Position - p0; var dm = me.Position - m0;
                bool stands = minY > -0.5f;
                bool rides = dp.Length < 0.05f || (dp - dm).Length < 0.25f;
                Console.WriteLine($"    {(stands && rides ? "ok  " : "FAIL")} platform {plat.Id} (group {plat.Data.GroupId}, trigger {trig?.Id}): Samus {me.Position.Y - plat.Position.Y:0.00} above it (lowest {minY:0.00}),"
                    + $" platform moved {F(dp)}, Samus moved {F(dm)}");
            }
        }

        static void P2Death(CampaignHost host, PlayerEntity me)
        {
            int slot = me.SlotIndex;
            me.TakeDamage(9999, DamageFlags.Death, null, null);
            int f = 0;
            for (; f < 20 && !FirstHuntMission.Ending; f++) host.Step(Idle);
            Console.WriteLine($"    death: ending after {f} f, result {FirstHuntMission.Result}, score {FirstHuntMission.Score[slot]}, deaths {FirstHuntMission.Deaths[slot]}");
            int g = 0;
            for (; g < 600 && !host.Ended; g++) host.Step(Idle);
            Console.WriteLine($"      over {FirstHuntMission.Over}, host ended after {g} f more, Samus respawned {me.Health > 0}");
        }

        static void P2Morphball(CampaignHost host, PlayerEntity me, string room)
        {
            int slot = me.SlotIndex;
            PointModuleEntity? cur = PointModuleEntity.Current;
            Console.WriteLine($"    first module {cur?.Id}");
            { var walk = new System.Collections.Generic.List<string>(); var seenM = new System.Collections.Generic.HashSet<int>(); for (var m = cur; m != null && seenM.Add(m.Id); m = m.Next) walk.Add(m.Prev != null ? $"{m.Id}(b{m.Prev.Id})" : $"{m.Id}"); Console.WriteLine($"    chain by next: {String.Join(" ", walk)}"); }
            int n = 0;
            while (cur != null && !FirstHuntMission.Ending && n < 200)
            {
                PointModuleEntity? next = cur.Next ?? cur.Prev;
                if (next == null) break;
                host.PlacePlayer(next.Position, me.FacingVector);
                host.Step(Idle); host.Step(Idle);
                if (PointModuleEntity.Current != next) { Console.WriteLine($"    module {next.Id} not taken (current {PointModuleEntity.Current?.Id})"); break; }
                cur = next; n++;
            }
            for (int i = 0; i < 6; i++) host.Step(Idle);
            Console.WriteLine($"    took {n} modules in order: ending {FirstHuntMission.Ending}, result {FirstHuntMission.Result}, score {FirstHuntMission.Score[slot]} (10n^2 = {10 * n * n})");
            host.Dispose();
            // the clock: idle until it runs out
            CampaignHost h2 = StartP2(room, out me);
            int f = 0;
            for (; f < 60 * 60 && !FirstHuntMission.Ending; f++) h2.Step(Idle);
            Console.WriteLine($"    idle: ending after {f / 60f:0.0} s, result {FirstHuntMission.Result}");
            h2.Dispose();
        }

        static string Owned(PlayerEntity me) => String.Join("+", new[] { BeamType.PowerBeam, FirstHuntRules.ElectroLob, BeamType.Missile }
            .Where(b => me.AvailableWeapons[b]).Select(b => b + (me.FirstHuntCharge(b) ? "(charge)" : "")));

        static string State(PlayerEntity me) => $"hp {me.Health} ammo {me.FirstHuntAmmoPool} dd {me.DoubleDamageTimer} [{Owned(me)}]";

        static System.Collections.Generic.List<EntityBase> All(Scene scene)
        {
            var list = new System.Collections.Generic.List<EntityBase>();
            foreach (EntityBase e in scene.Entities) list.Add(e);
            return list;
        }

        static void Save(string root)
        {
            string path = Path.Combine(Path.GetTempPath(), "fhsave_test.sav");
            File.Delete(path);
            int fails = 0;
            void Check(bool ok, string what)
            {
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
                if (!ok) fails++;
            }
            FirstHuntSave save = FirstHuntSave.Load(path);
            Console.WriteLine($"defaults: name '{save.Name}', flags 0x{save.Flags:x}");
            foreach (FirstHuntSave.Table t in Enum.GetValues<FirstHuntSave.Table>())
            {
                Console.WriteLine($"  {t}: " + string.Join(", ", Enumerable.Range(0, FirstHuntSave.Rows).Select(r => save.Row(t, r))));
            }
            Check(save.Name == "Samus Aran", "default name");
            Check(save.Row(FirstHuntSave.Table.Survivor, 0) == (100u, 75u) && save.Row(FirstHuntSave.Table.Survivor, 4) == (15u, 15u), "Survivor defaults (100,75) .. (15,15)");
            Check(save.Row(FirstHuntSave.Table.Regulator, 0).Score == 5000 && save.Row(FirstHuntSave.Table.Regulator, 4).Score == 1000, "Regulator defaults 5000 .. 1000");
            Check(save.Row(FirstHuntSave.Table.Morphball, 0).Score == 30000 && save.Row(FirstHuntSave.Table.Morphball, 4).Score == 10000, "Morph Ball defaults 30000 .. 10000");
            // a failed run is ranked but not entered (0x203f898: its rank still sets the mode's placed flag)
            var r = save.RecordTraining(FhMode.Regulator, failed: true, 6000, 12000, 0);
            Check(r.Rank == 0 && save.Row(FirstHuntSave.Table.Regulator, 0).Score == 5000, "failed Regulator 6000 ranks 0, not entered");
            r = save.RecordTraining(FhMode.Regulator, failed: false, 6000, 12000, 0);
            Check(r.Rank == 0 && r.Changed && save.Row(FirstHuntSave.Table.Regulator, 0) == (6000u, 12000u) && save.Row(FirstHuntSave.Table.Regulator, 1).Score == 5000, "Regulator 6000 takes row 0, 5000 moves down");
            Check((save.Flags & FirstHuntSave.RegulatorPlaced) != 0 && !save.MovieAvailable, "Regulator placed flag");
            r = save.RecordTraining(FhMode.Survivor, failed: false, 10, 0, 10);
            Check(r.Rank == -1 && !r.Changed, "Survivor 10 doesn't place");
            r = save.RecordTraining(FhMode.Survivor, failed: false, 15, 0, 10);
            Check(r.Rank == 4 && save.Row(FirstHuntSave.Table.Survivor, 4) == (15u, 10u), "Survivor 15 with 10 kills: equal score, lower second first (row 4 now (15,10))");
            r = save.RecordTraining(FhMode.Morphball, failed: false, 20000, 1500, 0);
            Check(r.Rank == 2 && r.MovieNowUnlocked && save.MovieAvailable, "Morph Ball 20000 ranks 2 and unlocks the movie");
            save.Name = "TESTER";
            save.Save();
            byte[] file = File.ReadAllBytes(path);
            Check(file.Length == FirstHuntSave.FileSize, "file is 512 bytes");
            FirstHuntSave back = FirstHuntSave.Load(path);
            Check(back.Name == "TESTER" && back.Row(FirstHuntSave.Table.Regulator, 0) == (6000u, 12000u) && back.MovieAvailable, "round trip");
            ushort gen = back.Generation;
            back.RecordTraining(FhMode.Regulator, failed: false, 7000, 11000, 0);
            back.Save();
            FirstHuntSave again = FirstHuntSave.Load(path);
            Check(again.Generation == gen + 1 && again.Row(FirstHuntSave.Table.Regulator, 0).Score == 7000, "second save goes to the other copy, newer generation wins");
            // spoil the newer copy: the older one loads
            file = File.ReadAllBytes(path);
            int newer = file[0xd0] + file[0xd1] * 256 > file[0xe0 + 0xd0] + file[0xe0 + 0xd1] * 256 ? 0 : 1;
            file[newer * FirstHuntSave.SlotStride + 0x20] ^= 0xff;
            File.WriteAllBytes(path, file);
            FirstHuntSave old = FirstHuntSave.Load(path);
            Check(old.Row(FirstHuntSave.Table.Regulator, 0).Score == 6000, "a bad checksum falls back to the other copy");
            File.Delete(path);
            Console.WriteLine(fails == 0 ? "save: all passed" : $"save: {fails} FAILED");
        }

        // a menusTD page model: nodes, meshes (bounds in model units x scale), materials, animation groups
        // -fhgame items <page>: a page's items (buttons, texts) and its menu_state states
        static void MenuItems(string page)
        {
            var states = MphRecomp.FirstHunt.FhMenuData.ReadStates(File.ReadAllBytes(Paths.Combine(Paths.FhFileSystem, "menusTD/menu_state.bin")));
            foreach (var sc in MphRecomp.FirstHunt.FhMenuData.ReadScreens())
            {
                if (sc.Name != page) continue;
                Console.WriteLine(sc);
                foreach (var it in sc.Items)
                {
                    Console.WriteLine($"  {(it.IsButton ? "button " + it.StateBase : "text")} '{it.Label}' node {it.Node} flags {it.Flags:x} box {it.X0},{it.Y0},{it.X1},{it.Y1} param {it.TextParam}");
                }
            }
            if (page == "popup")
            {
                for (int t = 0; t < 0x1b; t++)
                {
                    int at = 0x207ab04 + t * 20;
                    string lines = String.Join(" / ", Enumerable.Range(0, 4).Select(l => MphRecomp.FirstHunt.FhMenuData.Str(FirstHuntRules.ReadArm9Int(at + l * 4) ?? 0, 0x20)));
                    Console.WriteLine($"  popup {t:x}: buttons {FirstHuntRules.ReadArm9Int(at + 16)}: {lines}");
                }
            }
            foreach (string name in states.Keys.Where(k => k.StartsWith(page + "_", StringComparison.Ordinal)).OrderBy(k => k))
            {
                Console.WriteLine($"  state {name} {states[name].Length} f: {String.Join(" ", states[name].Nodes.Select(n => $"{n.Node}:{n.Start}-{n.End}"))}");
            }
        }

        static void MenuProbe(string page)
        {
            string anim = $"menusTD/{page}_Anim.bin";
            bool hasAnim = File.Exists(Path.Combine(Paths.FhFileSystem, anim));
            Model model = Read.ReadModelFile(page, $"menusTD/{page}_Model.bin", hasAnim ? anim : null, firstHunt: true);
            Console.WriteLine($"{page}: scale {model.Scale.X} nodes {model.Nodes.Count} meshes {model.Meshes.Count} materials {model.Materials.Count} textures {model.Recolors[0].Textures.Count}");
            AnimationGroups ag = model.AnimationGroups;
            foreach (NodeAnimationGroup g in ag.Node) Console.WriteLine($"  node anim {g.FrameCount} f: {string.Join(", ", g.Animations.Keys)}");
            foreach (MaterialAnimationGroup g in ag.Material) Console.WriteLine($"  material anim {g.FrameCount} f: {string.Join(", ", g.Animations.Keys)}");
            foreach (TexcoordAnimationGroup g in ag.Texcoord) Console.WriteLine($"  texcoord anim {g.FrameCount} f: {string.Join(", ", g.Animations.Keys)}");
            foreach (TextureAnimationGroup g in ag.Texture) Console.WriteLine($"  texture anim {g.FrameCount} f: {string.Join(", ", g.Animations.Keys)}");
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                Node n = model.Nodes[i];
                string meshes = "";
                for (int k = 0; k < n.MeshCount; k++)
                {
                    Mesh mesh = model.Meshes[n.MeshId / 2 + k];
                    float[] v = MphRecomp.Render.DsDisplayList.Decode(model, mesh, isRoom: false);
                    int st = MphRecomp.Render.DsDisplayList.Stride;
                    float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                    for (int o = 0; o < v.Length; o += st)
                    {
                        x0 = Math.Min(x0, v[o]); x1 = Math.Max(x1, v[o]); y0 = Math.Min(y0, v[o + 1]); y1 = Math.Max(y1, v[o + 1]); z0 = Math.Min(z0, v[o + 2]); z1 = Math.Max(z1, v[o + 2]);
                    }
                    float sc = model.Scale.X;
                    meshes += $" [m{n.MeshId / 2 + k} mat {mesh.MaterialId} '{model.Materials[mesh.MaterialId].Name}' x {x0 * sc:0.#}..{x1 * sc:0.#} y {y0 * sc:0.#}..{y1 * sc:0.#} z {z0 * sc:0.#}..{z1 * sc:0.#}]";
                }
                Console.WriteLine($"  node {i} '{n.Name}' p{n.ParentIndex} c{n.ChildIndex} n{n.NextIndex} en {n.Enabled} pos {n.Position} bb {n.BillboardMode}{meshes}");
            }
        }

        // P4 front end: each page (or one) through its open state, drawn with the top-screen backdrop, the bottom
        // backdrop and the frame -> extract_out/firsthunt_menu/<page>.png (open state's last frame) + <page>_open.png (strip)
        // -fhgame nodes <model>: each drawn node of a menu model on its own at frame 0 / its first state: where it lands
        // (DS px, y down; bottom screen y 192..384), its materials and textures
        static void MenuNodes(string name)
        {
            int at = name.IndexOf('@');
            int frame = at > 0 ? Int32.Parse(name[(at + 1)..]) : 0;
            if (at > 0) name = name[..at];
            var model = new FhMenuModel(name) { DefaultFrame = frame };
            var textures = new MphRecomp.Frontend.UiTextureCache();
            Console.WriteLine($"texture anim offsets {string.Join(", ", model.TextureAnimOffsets.Select(kv => $"{kv.Key} +{kv.Value.Texture}/+{kv.Value.Palette}"))}");
            foreach (var tg in model.Model.AnimationGroups.Texture)
            {
                foreach (var kv in tg.Animations)
                {
                    var ta = kv.Value;
                    var keys = new System.Collections.Generic.List<string>();
                    for (int j = ta.StartIndex; j < ta.StartIndex + ta.Count; j++) keys.Add($"t{tg.TextureIds[j]}/p{tg.PaletteIds[j]}");
                    var mat = model.Model.Materials.FirstOrDefault(m => m.Name == kv.Key);
                    Console.WriteLine($"  anim {kv.Key} (mat tex {mat?.TextureId} pal {mat?.PaletteId}): {string.Join(" ", keys.Distinct())}");
                }
            }
            Console.WriteLine($"  textures {model.Model.Recolors[0].Textures.Count}, palettes {model.Model.Recolors[0].Palettes.Count}");
            foreach (MphRead.Material m in model.Model.Materials)
            {
                MphRead.Texture? tx = m.TextureId >= 0 && m.TextureId < model.Model.Recolors[0].Textures.Count ? model.Model.Recolors[0].Textures[m.TextureId] : (MphRead.Texture?)null;
                Console.WriteLine($"  material {m.Name}: tex {m.TextureId} ({tx?.Width}x{tx?.Height}) pal {m.PaletteId} texgen {m.TexgenMode} S {m.ScaleS}/{m.ScaleT} T {m.TranslateS}/{m.TranslateT} R {m.RotateZ} diffuse {m.Diffuse.Red},{m.Diffuse.Green},{m.Diffuse.Blue} ambient {m.Ambient.Red},{m.Ambient.Green},{m.Ambient.Blue} spec {m.Specular.Red},{m.Specular.Green},{m.Specular.Blue} alpha {m.Alpha} poly {m.PolygonMode} light {m.Lighting}"); foreach (var tg in model.Model.AnimationGroups.Texcoord) if (tg.Animations.ContainsKey(m.Name)) Console.WriteLine($"    texcoord anim ({tg.FrameCount} frames)");
            }
            for (int t = 0; t < model.Model.Recolors[0].Textures.Count; t++) Console.WriteLine($"  texture {t}: {model.Model.Recolors[0].Textures[t].Width}x{model.Model.Recolors[0].Textures[t].Height}");
            // each material's texture in its palette, at 1x (fhmat_<model>_<material>.png in the temp folder)
            foreach (MphRead.Material m in model.Model.Materials)
            {
                if (m.TextureId < 0 || m.TextureId >= model.Model.Recolors[0].Textures.Count) continue;
                MphRead.Texture tx = model.Model.Recolors[0].Textures[m.TextureId];
                var px = model.Model.GetPixels(m.TextureId, m.PaletteId, recolorId: 0);
                var rgba = new byte[tx.Width * tx.Height * 4];
                for (int k = 0; k < px.Count; k++)
                {
                    rgba[k * 4] = px[k].Red; rgba[k * 4 + 1] = px[k].Green; rgba[k * 4 + 2] = px[k].Blue; rgba[k * 4 + 3] = px[k].Alpha;
                }
                using var mi = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(rgba, tx.Width, tx.Height);
                mi.SaveAsPng(Path.Combine(Path.GetTempPath(), $"fhmat_{Path.GetFileName(name)}_{m.Name}.png"));
                Console.WriteLine($"  dumped {m.Name} {tx.Width}x{tx.Height} {tx.Format}");
            }
            var names = new System.Collections.Generic.List<string>();
            foreach (MphRead.Node n in model.Model.Nodes) names.Add(n.Name);
            for (int i = 0; i < model.Model.Nodes.Count; i++)
            {
                MphRead.Node node = model.Model.Nodes[i];
                var hidden = new System.Collections.Generic.HashSet<string>(names);
                hidden.Remove(node.Name);
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                model.Evaluate(textures, tris, hidden: hidden);
                if (tris.Count == 0) continue;
                float x0 = 1e9f, y0 = 1e9f, x1 = -1e9f, y1 = -1e9f;
                foreach (var t in tris)
                {
                    foreach (var v in new[] { t.A, t.B, t.C })
                    {
                        x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X);
                        y0 = Math.Min(y0, 192 - v.Y); y1 = Math.Max(y1, 192 - v.Y);
                    }
                }
                var mats = new System.Collections.Generic.List<string>();
                for (int k = 0; k < node.MeshCount; k++)
                {
                    MphRead.Material m = model.Model.Materials[model.Model.Meshes[node.MeshId / 2 + k].MaterialId];
                    string tex = m.TextureId >= 0 && m.TextureId < model.Model.Recolors[0].Textures.Count
                        ? $"{model.Model.Recolors[0].Textures[m.TextureId].Width}x{model.Model.Recolors[0].Textures[m.TextureId].Height} {model.Model.Recolors[0].Textures[m.TextureId].Format}" : "-";
                    mats.Add($"{m.Name} (tex {m.TextureId} {tex}, a {m.CurrentAlpha:0.00}, rep {m.XRepeat}/{m.YRepeat}, poly {m.PolygonMode})");
                }
                Console.WriteLine($"{i,3} {node.Name,-16} parent {node.ParentIndex,3} tris {tris.Count,3} x {x0:0}..{x1:0} y {y0:0}..{y1:0}  {string.Join("; ", mats)}");
                if (tris.Count <= 2)
                {
                    Console.WriteLine("      " + string.Join(" ", tris.Select(t => $"[({t.A.X:0},{t.A.Y:0}) {t.A.U:0.00},{t.A.V:0.00} ({t.B.X:0},{t.B.Y:0}) {t.B.U:0.00},{t.B.V:0.00} ({t.C.X:0},{t.C.Y:0}) {t.C.U:0.00},{t.C.V:0.00}]")));
                }
            }
            for (int id = 0; id < textures.Count; id++)
            {
                var tex = textures[id];
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(tex.Rgba, tex.Width, tex.Height);
                img.Mutate(x => x.Resize(tex.Width * 4, tex.Height * 4, SixLabors.ImageSharp.Processing.KnownResamplers.NearestNeighbor));
                img.SaveAsPng(Path.Combine(Path.GetTempPath(), $"fhtex_{name}_{tex.Key.Split('|')[1]}.png"));
            }
        }

        // -fhgame nodeframes <frame node>: one frame-model node at each frame 0..59: its UVs, alpha and place
        static void MenuNodeFrames(string nodeName)
        {
            string modelName = "frame"; // (<model>:<node> for another menu model)
            if (nodeName.Contains(':'))
            {
                modelName = nodeName[..nodeName.IndexOf(':')];
                nodeName = nodeName[(nodeName.IndexOf(':') + 1)..];
            }
            var model = new FhMenuModel(modelName);
            var textures = new MphRecomp.Frontend.UiTextureCache();
            var hidden = new System.Collections.Generic.HashSet<string>();
            foreach (MphRead.Node n in model.Model.Nodes) if (n.Name != nodeName) hidden.Add(n.Name);
            Console.WriteLine($"frames {model.FrameCount}");
            foreach (var tg in model.Model.AnimationGroups.Texture)
            {
                Console.WriteLine($"texture group: {tg.FrameCount} frames, {tg.Animations.Count} anims");
                foreach (var kv in tg.Animations)
                {
                    var ta = kv.Value;
                    var keys = new System.Collections.Generic.List<string>();
                    for (int j = ta.StartIndex; j < ta.StartIndex + ta.Count; j++) keys.Add($"f{tg.FrameIndices[j]}:t{tg.TextureIds[j]}/p{tg.PaletteIds[j]}");
                    Console.WriteLine($"  {kv.Key}: {string.Join(" ", keys)}");
                }
            }
            for (int t = 0; t < model.Model.Recolors[0].Textures.Count; t++)
            {
                var tx = model.Model.Recolors[0].Textures[t];
                Console.WriteLine($"  tex {t}: {tx.Width}x{tx.Height} {tx.Format}");
            }
            foreach (var m in model.Model.Materials) Console.WriteLine($"  mat {m.Name}: tex {m.TextureId} pal {m.PaletteId}");
            var pals = model.Model.Recolors[0].PaletteData;
            for (int p = 0; p < pals.Count; p++)
            {
                Console.WriteLine($"  pal {p}: " + string.Join(" ", pals[p].Take(32).Select(c =>
                    $"{(c.Data & 31) * 255 / 31:x2}{(c.Data >> 5 & 31) * 255 / 31:x2}{(c.Data >> 10 & 31) * 255 / 31:x2}")));
            }
            for (int f = 0; f < 60; f++)
            {
                model.DefaultFrame = f;
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                model.Evaluate(textures, tris, hidden: hidden);
                string uv = string.Join(" ", tris.Select(t => $"[{t.A.U:0.00},{t.A.V:0.00} {t.B.U:0.00},{t.B.V:0.00} {t.C.U:0.00},{t.C.V:0.00} a{t.A.A:0.00} x{t.A.X:0.0},{t.B.X:0.0},{t.C.X:0.0} y{t.A.Y:0.0},{t.B.Y:0.0},{t.C.Y:0.0} rgb{t.A.R:0.00},{t.A.G:0.00},{t.A.B:0.00}]"));
                Console.WriteLine($"{f,2}: {tris.Count} {string.Join(",", tris.Select(t => t.TextureId < 0 ? "untextured" : textures[t.TextureId].Key + " " + textures[t.TextureId].Width + "x" + textures[t.TextureId].Height).Distinct())} {uv}");
            }
            var ids = new System.Collections.Generic.HashSet<int>();
            model.DefaultFrame = 0;
            var all = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
            model.Evaluate(textures, all, hidden: hidden);
            foreach (var t in all) if (t.TextureId >= 0) ids.Add(t.TextureId);
            foreach (int id in ids)
            {
                var tex = textures[id];
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(tex.Rgba, tex.Width, tex.Height);
                string path = Path.Combine(Path.GetTempPath(), $"fhtex_{nodeName}_{id}.png");
                img.SaveAsPng(path);
                Console.WriteLine($"texture {id} {tex.Width}x{tex.Height} -> {path}");
            }
        }

        static void MenuRender(string? only)
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_menu"));
            Directory.CreateDirectory(outDir);
            byte[] ms = File.ReadAllBytes(Path.Combine(Paths.FhFileSystem, "menusTD", "menu_state.bin"));
            var states = FhMenuData.ReadStates(ms);
            var screens = FhMenuData.ReadScreens();
            Console.WriteLine($"menu_state: {states.Count} states; screens: {screens.Count}");
            foreach (FhMenuScreen sc in screens)
            {
                Console.WriteLine($"  {sc}");
                foreach (FhMenuItem it in sc.Items)
                {
                    Console.WriteLine($"     ({it.X0},{it.Y0},{it.X1},{it.Y1}) '{it.Label}' base '{it.StateBase}' node '{it.Node}' flags {it.Flags:x} p {it.TextParam:x} x {it.Extra:x}");
                }
            }
            string save = Path.Combine(Path.GetTempPath(), "fhmenu.sav");
            File.Delete(save);
            var fe = new FhFrontend(save);
            var result = new FhTrainingResult(FhMode.Regulator, false, 0, 18000, 0, -1, false); // fh_ref trainresult_reg_idle: 0 pts 10:00, no rank
            var sheet = new System.Collections.Generic.List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>>();
            SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> Draw()
            {
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                fe.Evaluate(tris);
                // FH_SCALE=1 draws DS pixels (the 1:1 check against fh_ref); 2 = the default look
                return FrontendTool.Raster(tris, fe.Textures, int.TryParse(Environment.GetEnvironmentVariable("FH_SCALE"), out int s) ? s : 2);
            }
            foreach (FhMenuScreen sc in screens)
            {
                if (only != null && sc.Name != only) continue;
                if (sc.Index == FhMenuData.Popup || !File.Exists(Path.Combine(Paths.FhFileSystem, "menusTD", sc.Name + "_Model.bin"))) continue;
                fe.Show(sc.Index, sc.Index == FhMenuData.TrainingHiScore ? result : null);
                for (int t = 0; t < 300 && fe.Busy; t++) fe.Tick();
                var idle = Draw();
                idle.SaveAsPng(Path.Combine(outDir, sc.Name + ".png"));
                fe.Pad(FhPad.Down);
                for (int t = 0; t < 60; t++) fe.Tick();
                var lit = Draw();
                lit.SaveAsPng(Path.Combine(outDir, sc.Name + "_hi.png"));
                sheet.Add(idle);
                sheet.Add(lit);
                Console.WriteLine($"  {sc.Name}: -> {sc.Name}.png, {sc.Name}_hi.png; texts: {string.Join(" | ", fe.Texts.Values.Select(x => x.Text).Where(x => x.Length > 0).Take(8))}");
            }
            if (sheet.Count > 0)
            {
                int w = sheet[0].Width / 2, h = sheet[0].Height / 2, cols = Math.Min(8, sheet.Count);
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w * cols, h * ((sheet.Count + cols - 1) / cols));
                for (int i = 0; i < sheet.Count; i++)
                {
                    using var small = sheet[i].Clone(c => c.Resize(w, h));
                    img.Mutate(c => c.DrawImage(small, new SixLabors.ImageSharp.Point(i % cols * w, i / cols * h), 1f));
                }
                img.SaveAsPng(Path.Combine(outDir, only == null ? "pages_sheet.png" : only + "_sheet.png"));
            }
            foreach (var x in sheet) x.Dispose();
        }

        // a page's 2D background layer (<page>_back.png) and the 3D in front of it with its alpha (<page>_front.png, matted
        // from a render over black and one over white), settled, for 16:9 composition mockups; `only` = page name or all
        static void MenuLayers(string? only)
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_menu", "layers"));
            Directory.CreateDirectory(outDir);
            string save = Path.Combine(Path.GetTempPath(), "fhmenu.sav");
            File.Delete(save);
            var fe = new FhFrontend(save);
            var result = new FhTrainingResult(FhMode.Regulator, false, 4500, 9500, 0, 1, false);
            const float scale = 2;
            SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> Draw(bool back, bool front, bool frame, float white)
            {
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                if (white > 0)
                {
                    var v = new MphRecomp.Frontend.UiVertex[4];
                    float[] xs = { 0, 256, 256, 0 }, ys = { 192, 192, -192, -192 };
                    for (int i = 0; i < 4; i++) v[i] = new MphRecomp.Frontend.UiVertex(xs[i], ys[i], 0, 0, white, white, white, 1);
                    tris.Add(new MphRecomp.Frontend.WidgetTri { A = v[0], B = v[1], C = v[2], TextureId = -1 });
                    tris.Add(new MphRecomp.Frontend.WidgetTri { A = v[0], B = v[2], C = v[3], TextureId = -1 });
                }
                fe.Evaluate(tris, back, front, frame);
                return FrontendTool.Raster(tris, fe.Textures, scale);
            }
            foreach (FhMenuScreen sc in FhMenuData.ReadScreens())
            {
                if (only != null && sc.Name != only) continue;
                if (sc.Index == FhMenuData.Popup || !File.Exists(Path.Combine(Paths.FhFileSystem, "menusTD", sc.Name + "_Model.bin"))) continue;
                fe.Show(sc.Index, sc.Index == FhMenuData.TrainingHiScore ? result : null);
                for (int t = 0; t < 300 && fe.Busy; t++) fe.Tick();
                fe.Pad(FhPad.Down); // the first button lit (the top screen shows its description)
                for (int t = 0; t < 60; t++) fe.Tick();
                using var back = Draw(true, false, false, 0);
                back.SaveAsPng(Path.Combine(outDir, sc.Name + "_back.png"));
                foreach ((string layer, bool page) in new[] { ("front", true), ("frame", false) })
                {
                    using var black = Draw(false, page, !page, 0);
                    using var white = Draw(false, page, !page, 1);
                    using var front = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(black.Width, black.Height);
                    for (int y = 0; y < front.Height; y++)
                    {
                        for (int x = 0; x < front.Width; x++)
                        {
                            var k = black[x, y];
                            var w = white[x, y];
                            // over black: a*c; over white: a*c + (1-a)
                            float a = 1 - ((w.R - k.R) + (w.G - k.G) + (w.B - k.B)) / (3 * 255f);
                            a = Math.Clamp(a, 0, 1);
                            byte C(byte c) => (byte)Math.Clamp(a > 0.004f ? c / a : 0, 0, 255);
                            front[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(C(k.R), C(k.G), C(k.B), (byte)(a * 255));
                        }
                    }
                    front.SaveAsPng(Path.Combine(outDir, sc.Name + "_" + layer + ".png"));
                    // (the additive lightning doesn't matte: the render over black, to add)
                    if (!page) black.SaveAsPng(Path.Combine(outDir, sc.Name + "_frame_add.png"));
                }
                // the stylus trail (the frame's cursor node) after a short drag over an empty part of the touch screen
                for (int t = 0; t < 12; t++)
                {
                    fe.Touch(true, 12 + t * 2, 120 + t);
                    fe.Tick();
                }
                using (var touch = Draw(false, false, true, 0)) touch.SaveAsPng(Path.Combine(outDir, sc.Name + "_touch_add.png"));
                fe.Touch(false, 36, 132);
                for (int t = 0; t < 60; t++) fe.Tick();
                Console.WriteLine($"  {sc.Name}: _back.png, _front.png, _frame.png");
            }
        }

        // a settled page's drawn nodes: model, node, and the pixel box each covers (x, then DS rows: 0-191 top screen,
        // 192-383 touch screen), to line nodes up against the cart's shots
        static void MenuBoxes(string page)
        {
            FhFrontend.DsPixels = Environment.GetEnvironmentVariable("FH_RAW") != "1";
            var fe = new FhFrontend(Path.Combine(Path.GetTempPath(), "fhmenu_boxes.sav"));
            FhMenuScreen sc = FhMenuData.ReadScreens().First(x => x.Name == page);
            fe.Show(sc.Index, sc.Index == FhMenuData.TrainingHiScore ? new FhTrainingResult(FhMode.Regulator, false, 4500, 9500, 0, 1, false) : null);
            for (int t = 0; t < 300 && fe.Busy; t++) fe.Tick();
            for (int t = 0; t < 60; t++) fe.Tick();
            var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
            fe.Evaluate(tris);
            foreach (var g in tris.GroupBy(t => t.Item).OrderBy(g => g.Key))
            {
                var xs = g.SelectMany(t => new[] { t.A.X, t.B.X, t.C.X });
                var ys = g.SelectMany(t => new[] { 192 - t.A.Y, 192 - t.B.Y, 192 - t.C.Y });
                (string model, string node) = fe.NodeOf(g.Key);
                var us = g.SelectMany(t => new[] { t.A.U, t.B.U, t.C.U });
                var vs = g.SelectMany(t => new[] { t.A.V, t.B.V, t.C.V });
                Console.WriteLine($"  {model,-20} {node,-24} x {xs.Min(),7:0.###}..{xs.Max(),7:0.###}  row {ys.Min(),7:0.###}..{ys.Max(),7:0.###}  u {us.Min():0.####}..{us.Max():0.####} v {vs.Min():0.####}..{vs.Max():0.####} f {fe.FrameOf(g.Key)} ({g.Count()} tris)");
                // texels per DS row down the node, and the texel row its top row starts on (as drawn, after the texcoord move)
                int texId = g.First().TextureId, texH = texId >= 0 && texId < fe.Textures.Count ? fe.Textures[texId].Height : 0;
                float rows = ys.Max() - ys.Min();
                if (rows > 0 && texH > 0) Console.WriteLine($"      tex h {texH}: {(vs.Max() - vs.Min()) * texH / rows:0.####} texel/row, top texel {vs.Min() * texH:0.####}");
            }
            // the page's node translations over the last frames of its node animation (the settle pose)
            var pm = new FhMenuModel(page);
            if (pm.Model.AnimationGroups.Node.Count == 0) return;
            var ng = pm.Model.AnimationGroups.Node[0];
            Console.WriteLine($"node anim {ng.FrameCount} frames");
            foreach (var kv in ng.Animations)
            {
                var a = kv.Value;
                var ty = Enumerable.Range(0, ng.FrameCount).Select(f =>
                    pm.Model.InterpolateAnimation(ng.Translations, a.TranslateLutIndexY, f, a.TranslateBlendY, a.TranslateLutLengthY, ng.FrameCount));
                Console.WriteLine($"  {kv.Key,-24} ty blend {a.TranslateBlendY} lut {a.TranslateLutLengthY}: {string.Join(" ", ty.Select(v => v.ToString("0.###")))}");
            }
        }

        // P4 front end walk: title -> main menu -> training -> Game A by touches, the results page after a run, pad
        // every FH model with a texture animation: its keys against the material's own texture/palette and the model's
        // counts (keys that number the animation's own frames, not the model's textures, need offsets like FhMenuModel's)
        static void TexAnims()
        {
            string dir = Path.Combine(Paths.FhFileSystem, "models");
            foreach (string anim in Directory.GetFiles(dir, "*_Anim.bin").OrderBy(f => f))
            {
                string name = Path.GetFileName(anim)[..^"_Anim.bin".Length];
                string modelFile = Path.Combine(dir, name + "_Model.bin");
                if (!File.Exists(modelFile)) continue;
                MphRead.Model model;
                try { model = MphRead.Read.ReadModelFile("fhscan:" + name, $"models/{name}_Model.bin", $"models/{name}_Anim.bin", firstHunt: true); }
                catch (Exception ex) { Console.WriteLine($"{name}: {ex.Message}"); continue; }
                int tc = model.Recolors[0].Textures.Count, pc = model.Recolors[0].Palettes.Count;
                foreach (var tg in model.AnimationGroups.Texture)
                {
                    foreach (var kv in tg.Animations)
                    {
                        var ta = kv.Value;
                        var keys = new System.Collections.Generic.List<string>();
                        for (int j = ta.StartIndex; j < ta.StartIndex + ta.Count; j++) keys.Add($"f{tg.FrameIndices[j]}:t{tg.TextureIds[j]}/p{tg.PaletteIds[j]}");
                        var mat = model.Materials.FirstOrDefault(m => m.Name == kv.Key);
                        int minT = int.MaxValue, maxT = -1;
                        for (int j = ta.StartIndex; j < ta.StartIndex + ta.Count; j++) { minT = Math.Min(minT, tg.TextureIds[j]); maxT = Math.Max(maxT, tg.TextureIds[j]); }
                        if (mat == null || mat.TextureId == minT) continue;
                        string others = string.Join(" ", model.Materials.Where(m => m != mat).Select(m => $"{m.Name}=t{m.TextureId}"));
                        Console.WriteLine($"{name}: {kv.Key} mat t{mat.TextureId}/p{mat.PaletteId}, keys t{minT}..t{maxT}, model {tc} tex {pc} pal; others: {others}");
                    }
                }
            }
        }

        // navigation; snapshots -> extract_out/firsthunt_menu/walk_*.png
        // P5 lobby pages over real LAN sockets on this PC: the menus host (a raw LanSession joins it, START starts both), the
        // menus join (a raw host's game in the list, CONNECTING, wait, the host's START starts it), then the results page
        // from a match result (its texts, the saved totals, PLAY AGAIN / QUIT) -> extract_out/firsthunt_menu/lobby_*.png
        static void LobbyWalk()
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_menu"));
            Directory.CreateDirectory(outDir);
            string save = Path.Combine(Path.GetTempPath(), "fhlobby.sav");
            File.Delete(save);
            var fe = new FhFrontend(save);
            fe.Save.Name = "HOSTY";
            var log = new System.Collections.Generic.List<string>();
            FhGameStart? started = null;
            fe.Sound = i => log.Add($"sfx {i}");
            fe.Music = (t, loop) => log.Add($"music {t}");
            fe.StartGame = g => started = g;
            void Snap(string tag)
            {
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                fe.Evaluate(tris);
                using var img = FrontendTool.Raster(tris, fe.Textures, 2);
                img.SaveAsPng(Path.Combine(outDir, $"lobby_{tag}.png"));
                Console.WriteLine($"  [{fe.ScreenName}] {tag}: {string.Join(", ", log)}");
                log.Clear();
            }
            void Run(int frames = 400) { for (int i = 0; i < frames && fe.Busy; i++) fe.Tick(); fe.Tick(); }
            void Tap(float x, float y) { fe.Touch(true, x, y); fe.Tick(); fe.Touch(false, x, y); Run(); }
            bool Wait(Func<bool> done, int ms = 6000)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!done() && sw.ElapsedMilliseconds < ms) { fe.Tick(); System.Threading.Thread.Sleep(4); }
                return done();
            }
            string Text(string node) => fe.Texts.TryGetValue(node, out FhText t) ? t.Text : "(none)";
            int fails = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }

            // 1. the menus host
            fe.Show(FhMenuData.HostStart);
            Run();
            Snap("host_start");
            Check(MphRecomp.Multiplayer.Net.LanSession.Current is { IsHost: true }, "Host Start hosts (LanSession.Current)");
            Check(Text("hostPlayerName") == "HOSTY" && Text("client1StatusText") == "NO ENTRY", $"slots: host {Text("hostPlayerName")}, slot 2 '{Text("client1StatusText")}'");
            Tap(188, 166); // START alone
            Snap("host_alone");
            Check(fe.ScreenName == "popup", "START alone -> popup (Not enough players)");
            fe.Pad(FhPad.Confirm);
            Run();
            Check(fe.ScreenName == "host_start", "OK -> Host Start again");
            Tap(208, 110); // arena hex 2
            Snap("host_arena2");
            Check(fe.Arena == 1, $"hex 2 -> arena {fe.Arena + 1}");
            using (var joiner = MphRecomp.Multiplayer.Net.LanSession.Browse(FhLobby.RomKey))
            {
                MphRecomp.Multiplayer.Net.LanHostInfo? game = null;
                Wait(() => (game = joiner.Hosts.FirstOrDefault(h => h.RomKey == FhLobby.RomKey && h.Name == "HOSTY")) != null);
                Check(game?.Arena == "Level MP2", $"the beacon: {game?.Name} in {game?.Arena}");
                bool joined = game != null && joiner.Join(game.Address, Hunter.Samus, "JOINY");
                Check(joined, "a joiner joins the menus' game");
                Wait(() => Text("client1PlayerName") == "JOINY", 3000);
                Run();
                Snap("host_joined");
                Check(Text("client1PlayerName") == "JOINY" && Text("client2StatusText") == "NO ENTRY", $"slot 2 {Text("client1PlayerName")}, slot 3 '{Text("client2StatusText")}'");
                Tap(188, 166); // START
                Check(started is { Net: "host", Room: "Level MP2", Mode: FhMode.Multiplayer }, $"START -> {started}");
                bool go = SpinWait(() => joiner.Started, 3000);
                var js = joiner.Settings;
                Check(go && js?.Arena == "Level MP2" && js.Players.Count == 2 && js.LocalSeat == 1 && js.Players[0].Nickname == "HOSTY"
                    && js.Players.All(p => p.Hunter == Hunter.Samus && !p.Bot), $"the joiner starts: {js?.Arena}, {js?.Players.Count} players, seat {js?.LocalSeat}");
            }
            FhLobby.End();

            // 2. the menus join
            started = null;
            fe.Save.Name = "JOINZ";
            using (var host = MphRecomp.Multiplayer.Net.LanSession.Host(FhLobby.Template(2, "HOSTB"), FhLobby.RomKey, "HOSTB", seatLimit: 4, port: 0))
            {
                fe.Show(FhMenuData.ClientStart);
                Run();
                Check(Wait(() => fe.Games.Any(g => g.Name == "HOSTB")), $"Client Start lists the game ({fe.Games.Count} games)");
                Run();
                Wait(() => Text("serverOne") == "HOSTB", 1000);
                Snap("client_start");
                Check(Text("serverOne") == "HOSTB", $"row 1 '{Text("serverOne")}'");
                // (touching a row joins at once, as every cart button acts on the touch; the top screen shows that game)
                fe.Touch(true, 111, 54);
                for (int i = 0; i < 60; i++) fe.Tick();
                Snap("client_lit");
                string PageText(string node) => fe.PageTexts.TryGetValue(node, out FhText t) ? t.Text : "(none)";
                Check(PageText("stageName") == FhLobby.ArenaName(2) && PageText("hostPlayerName") == "HOSTB", $"top screen under Connecting: {PageText("hostPlayerName")} in {PageText("stageName")}");
                fe.Touch(false, 111, 54);
                Run();
                Check(fe.ScreenName == "popup", "a game touched -> Connecting");
                Check(Wait(() => fe.Joined), "joined");
                Run();
                Snap("client_wait");
                Check(host.Joiners.Count(j => !j.Left) == 1 && host.Joiners[0].Player.Nickname == "JOINZ", "the host has the joiner");
                host.SendStart();
                Check(Wait(() => started != null, 3000) && started is { Net: "client", Room: "Level MP3" }, $"the host's START -> {started}");
            }
            FhLobby.End();

            // 3. the results page
            var table = new int[4, 4];
            table[0, 1] = 3; table[0, 2] = 2; table[1, 0] = 4; table[1, 2] = 3; table[2, 0] = 1; table[1, 1] = 1;
            var m = new FhMatchResult
            {
                LocalSlot = 1, Host = false, Names = new[] { "HOSTB", "JOINZ", "THIRD" }, Kills = new[] { 5, 7, 1 }, Deaths = new[] { 5, 6, 5 },
                Points = new[] { 5, 7, 1 }, Ranks = new[] { 1, 0, 2 }, KillTable = table
            };
            FhMatchResult? back = FhMatchResult.Parse(m.ToLine());
            Check(back != null && back.ToLine() == m.ToLine(), $"result line round trip: {m.ToLine()}");
            fe.Start(match: back);
            Run();
            Snap("results3");
            Check(fe.ScreenName == "multi_hi_score", "a match result -> Multi Hi Score");
            Check(Text("FirstFont") == "JOINZ" && Text("SecondFont") == "HOSTB" && Text("ThirdFont") == "THIRD" && Text("FourthFont") == "",
                $"placings {Text("FirstFont")} / {Text("SecondFont")} / {Text("ThirdFont")} / '{Text("FourthFont")}'");
            Check(Text("p2_1") == "4" && Text("p1_3") == "2" && Text("p2KillTotal") == "7" && Text("deaths2") == "6" && Text("player3") == ""
                && Text("p4_1") == "", $"table: p2_1 {Text("p2_1")}, p1_3 {Text("p1_3")}, kills {Text("p2KillTotal")}, deaths {Text("deaths2")}");
            Check(fe.Save.GamesWon == 1 && fe.Save.TotalKills == 7 && fe.Save.TotalDeaths == 6, $"totals: won {fe.Save.GamesWon}, kills {fe.Save.TotalKills}, deaths {fe.Save.TotalDeaths}");
            var reread = FirstHuntSave.Load(save);
            Check(reread.GamesWon == 1 && reread.TotalKills == 7, "totals saved (cart format)");
            Tap(173, 167); // PLAY AGAIN (a client)
            Check(fe.ScreenName == "client_start" && MphRecomp.Multiplayer.Net.LanSession.Current is { IsHost: false }, "PLAY AGAIN (client) -> Client Start, browsing");
            fe.Start(match: new FhMatchResult
            {
                LocalSlot = 0, Host = true, Names = new[] { "HOSTB", "JOINZ" }, Kills = new[] { 2, 2 }, Deaths = new[] { 2, 2 },
                Points = new[] { 2, 2 }, Ranks = new[] { 0, 0 }, KillTable = new int[4, 4]
            });
            Run();
            Snap("results2");
            Check(MphRecomp.Multiplayer.Net.LanSession.Current == null, "the results page ends the lobby's session");
            Check(fe.Save.GamesWon == 2, $"a tie for first is a win ({fe.Save.GamesWon})");
            Tap(55, 146); // QUIT
            Check(fe.ScreenName == "title", "QUIT -> Title");
            fe.Show(FhMenuData.Records);
            Run();
            Console.WriteLine(fails == 0 ? "PASS fhgame lobby" : $"FAIL fhgame lobby: {fails} checks");
        }

        // the stylus trail in motion: a drag over a page's empty area, one frame every 2 ticks (drag_<page>/f###.png)
        static void MenuDrag(string page)
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_menu", "drag_" + page));
            Directory.CreateDirectory(outDir);
            var fe = new FhFrontend(Path.Combine(Path.GetTempPath(), "fhdrag.sav"));
            FhMenuScreen sc = FhMenuData.ReadScreens().First(x => x.Name == page);
            fe.Show(sc.Index);
            for (int t = 0; t < 300 && fe.Busy; t++) fe.Tick();
            int scale = int.TryParse(Environment.GetEnvironmentVariable("FH_SCALE"), out int sv) ? sv : 3;
            if (Environment.GetEnvironmentVariable("FH_HOLD") is { } hold)
            {
                // FH_HOLD=x,y: the stylus held there 10 ticks (fh_ref_tools p1 *_held40: down at 30, shot at 40)
                float[] xy = hold.Split(',').Select(float.Parse).ToArray();
                for (int t = 0; t < 10; t++) { fe.Touch(true, xy[0], xy[1]); fe.Tick(); }
                var held = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                fe.Evaluate(held);
                using var himg = FrontendTool.Raster(held, fe.Textures, scale);
                himg.SaveAsPng(Path.Combine(outDir, "held40.png"));
                if (Environment.GetEnvironmentVariable("FH_PROBE") is { } probe)
                {
                    // FH_PROBE=x,y (menu space): every triangle over that point, in draw order, with its z
                    float[] pp = probe.Split(',').Select(float.Parse).ToArray();
                    foreach (var t in held)
                    {
                        float d(MphRecomp.Frontend.UiVertex a, MphRecomp.Frontend.UiVertex b) => (b.X - a.X) * (pp[1] - a.Y) - (b.Y - a.Y) * (pp[0] - a.X);
                        float d1 = d(t.A, t.B), d2 = d(t.B, t.C), d3 = d(t.C, t.A);
                        if ((d1 >= 0 && d2 >= 0 && d3 >= 0) || (d1 <= 0 && d2 <= 0 && d3 <= 0))
                        {
                            float w1 = d2 / (d1 + d2 + d3), w2 = d3 / (d1 + d2 + d3), w3 = d1 / (d1 + d2 + d3);
                            float u = t.A.U * w1 + t.B.U * w2 + t.C.U * w3, v = t.A.V * w1 + t.B.V * w2 + t.C.V * w3;
                            string texel = "";
                            if (t.TextureId >= 0)
                            {
                                var tx = fe.Textures[t.TextureId];
                                int ix = ((int)MathF.Floor(u * tx.Width) % tx.Width + tx.Width) % tx.Width, iy = ((int)MathF.Floor(v * tx.Height) % tx.Height + tx.Height) % tx.Height;
                                int o = (iy * tx.Width + ix) * 4;
                                texel = $" uv {u:0.000},{v:0.000} texel {ix},{iy} rgba {tx.Rgba[o]},{tx.Rgba[o + 1]},{tx.Rgba[o + 2]},{tx.Rgba[o + 3]} ({tx.Width}x{tx.Height})";
                            }
                            Console.WriteLine($"  item {t.Item >> 12}:{(t.Item & 0xfff) - 1} z {t.Z:0.00} a {t.A.A:0.00} tex {t.TextureId}{texel}");
                        }
                    }
                }
                Console.WriteLine($"held {page}: {outDir}");
                return;
            }
            if (Environment.GetEnvironmentVariable("FH_PATH") is { } path)
            {
                // FH_PATH=x0,y0,x1,y1,n[,last[,hold[,from]]][;...]: the cart driver's drags (fh_ref_tools mdrv.lua
                // {from,'drag',...}): held from frame from (10) for n frames along the line (rounded), then hold frames at its
                // end, then lifted; frame f's picture as c<f>.png (frames 8..last, 150, from the first)
                int[][] segs = path.Split(';').Select(x => x.Split(',').Select(int.Parse).ToArray()).ToArray();
                int last = segs[0].Length > 5 ? segs[0][5] : 150;
                for (int f = 1; f <= last; f++)
                {
                    foreach (int[] pv in segs)
                    {
                        int i = f - (pv.Length > 7 ? pv[7] : 10), held = pv[4] + (pv.Length > 6 ? pv[6] : 0);
                        if (i >= 0 && i < held)
                        {
                            float k = Math.Min(1, i / (float)Math.Max(1, pv[4] - 1));
                            fe.Touch(true, MathF.Floor(pv[0] + (pv[2] - pv[0]) * k + 0.5f), MathF.Floor(pv[1] + (pv[3] - pv[1]) * k + 0.5f));
                        }
                        else if (i == held)
                        {
                            fe.Touch(false, pv[2], pv[3]);
                        }
                    }
                    fe.Tick();
                    if (f < 8) continue;
                    var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                    fe.Evaluate(tris);
                    using var img = FrontendTool.Raster(tris, fe.Textures, scale);
                    img.SaveAsPng(Path.Combine(outDir, $"c{f:000}.png"));
                    if (Environment.GetEnvironmentVariable("FH_DUMP") == f.ToString())
                    {
                        // FH_DUMP=f: frame f's trail triangles by depth (z: extent)
                        foreach (var g in tris.Where(t => t.Item >> 12 == FhFrontend.TagTrail).GroupBy(t => t.Z).OrderBy(g => g.Key))
                        {
                            float x0 = g.Min(t => Math.Min(t.A.X, Math.Min(t.B.X, t.C.X))), x1 = g.Max(t => Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
                            float y0 = g.Min(t => Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y))), y1 = g.Max(t => Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y)));
                            Console.WriteLine($"  z {g.Key}: {g.Count()} tris x {x0}..{x1} y {y0}..{y1} tex {g.First().TextureId} a {g.First().A.A:0.00}");
                        }
                    }
                }
                Console.WriteLine($"path {page}: {outDir}");
                return;
            }
            for (int t = 0, f = 0; t < 130; t++)
            {
                // a down-right swoop from the top-right corner, held 70 ticks, then released
                float k = Math.Min(t, 70) / 70f;
                float x = 245 - 225 * k, y = 15 + 160 * k * k;
                fe.Touch(t < 70, x, y);
                fe.Tick();
                if (t % 2 != 0) continue;
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                fe.Evaluate(tris);
                using var img = FrontendTool.Raster(tris, fe.Textures, scale);
                img.SaveAsPng(Path.Combine(outDir, $"f{f++:000}.png"));
            }
            Console.WriteLine($"drag {page}: {outDir}");
        }

        // the one-screen layout (FhScreenLayout, Wide) of every page (or `only`), idle and lit, at FH_WIDE=WxH (1920x1080);
        // FH_TWO=1 the two-screen one; FH_ANIM=1 also a drag across the canvas (frames for a GIF, every 3rd tick)
        static void MenuWide(string? only)
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_menu", "wide"));
            Directory.CreateDirectory(outDir);
            int[] wh = (Environment.GetEnvironmentVariable("FH_WIDE") ?? "1920x1080").Split('x').Select(int.Parse).ToArray();
            string save = Path.Combine(Path.GetTempPath(), "fhwide.sav");
            File.Delete(save);
            var fe = new FhFrontend(save);
            var result = new FhTrainingResult(FhMode.Regulator, false, 4500, 9500, 0, 1, false);
            var layout = new FhScreenLayout { Wide = Environment.GetEnvironmentVariable("FH_TWO") != "1" };
            layout.Resize(wh[0], wh[1]);
            var list = new MphRecomp.Frontend.UiDrawList(fe.Textures);
            int ticks = 0;
            SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> Draw()
            {
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                fe.Evaluate(tris);
                list.Clear();
                layout.Build(fe, tris, list, ticks / 60.0);
                return FrontendTool.RasterCanvas(list, wh[0], wh[1]);
            }
            void Tick() { fe.Tick(); ticks++; }
            string tag = (layout.Wide ? "" : "two_") + $"{wh[0]}x{wh[1]}";
            foreach (FhMenuScreen sc in FhMenuData.ReadScreens())
            {
                if (only != null && sc.Name != only) continue;
                if (sc.Index == FhMenuData.Popup || !File.Exists(Path.Combine(Paths.FhFileSystem, "menusTD", sc.Name + "_Model.bin"))) continue;
                fe.Show(sc.Index, sc.Index == FhMenuData.TrainingHiScore ? result : null);
                for (int t = 0; t < 300 && fe.Busy; t++) Tick();
                for (int t = 0; t < 90; t++) Tick();
                using (var idle = Draw()) idle.SaveAsPng(Path.Combine(outDir, $"{sc.Name}_{tag}.png"));
                fe.Pad(FhPad.Down);
                for (int t = 0; t < 60; t++) Tick();
                using (var lit = Draw()) lit.SaveAsPng(Path.Combine(outDir, $"{sc.Name}_{tag}_hi.png"));
                if (Environment.GetEnvironmentVariable("FH_ANIM") == "1")
                {
                    // a swoop from the canvas's top right to its lower left (canvas px through the layout's touch map), held
                    // 100 ticks
                    string dir = Path.Combine(outDir, $"anim_{sc.Name}_{tag}");
                    Directory.CreateDirectory(dir);
                    // (8 s: 2 s of the grid's patches alone, the drag, then the trail fading and more patches)
                    for (int t = 0, f = 0; t < 480; t++)
                    {
                        int d = t - 120;
                        float k = Math.Clamp(d, 0, 100) / 100f;
                        float cx = wh[0] * (0.93f - 0.8f * k), cy = wh[1] * (0.12f + 0.55f * k * k);
                        layout.ToTouch(cx, cy, d >= 0 && d < 100, out float sx, out float sy);
                        if (d >= 0 && d <= 100) fe.Touch(d < 100, sx, sy);
                        Tick();
                        if (t % 4 != 0) continue;
                        using var img = Draw();
                        img.SaveAsPng(Path.Combine(dir, $"f{f++:000}.png"));
                    }
                }
                Console.WriteLine($"  {sc.Name}: {sc.Name}_{tag}.png, _hi.png");
            }
            if (fe.Textures.TryGet("fh/gridlines", out var cell) && cell != null)
            {
                // the grid's cell, 4 x 3 of them at 16x
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(cell.Width * 4 * 16, cell.Height * 3 * 16);
                for (int y = 0; y < img.Height; y++)
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        int o = (y / 16 % cell.Height * cell.Width + x / 16 % cell.Width) * 4;
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32(cell.Rgba[o], cell.Rgba[o + 1], cell.Rgba[o + 2], 255);
                    }
                }
                img.SaveAsPng(Path.Combine(outDir, "grid_cell.png"));
            }
            Console.WriteLine($"-> {outDir}");
        }

        static bool SpinWait(Func<bool> done, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < ms) System.Threading.Thread.Sleep(5);
            return done();
        }

        static void FrontendWalk()
        {
            string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extract_out", "firsthunt_menu"));
            Directory.CreateDirectory(outDir);
            string save = Path.Combine(Path.GetTempPath(), "fhwalk.sav");
            File.Delete(save);
            var fe = new FhFrontend(save);
            var log = new System.Collections.Generic.List<string>();
            FhGameStart? started = null;
            fe.Sound = i => log.Add($"sfx {i}");
            fe.Music = (t, loop) => log.Add($"music {t}");
            fe.StartGame = g => started = g;
            int shot = 0;
            void Snap(string tag)
            {
                var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                fe.Evaluate(tris);
                using var img = FrontendTool.Raster(tris, fe.Textures, 2);
                img.SaveAsPng(Path.Combine(outDir, $"walk_{shot++:00}_{tag}.png"));
                Console.WriteLine($"  [{fe.ScreenName}] {tag}: {string.Join(", ", log)}");
                log.Clear();
            }
            void Run(int frames = 400) { for (int i = 0; i < frames && fe.Busy; i++) fe.Tick(); fe.Tick(); }
            void Tap(float x, float y) { fe.Touch(true, x, y); fe.Tick(); fe.Touch(false, x, y); Run(); }
            int fails = 0;
            void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
            fe.Start();
            Run();
            Snap("title");
            Tap(128, 92);
            Snap("main_menu");
            Check(fe.ScreenName == "main_menu", "title touch -> main menu");
            Tap(100, 50);
            Snap("training");
            Check(fe.ScreenName == "training", "TRAINING -> training");
            fe.Pad(FhPad.Down);
            for (int i = 0; i < 40; i++) fe.Tick();
            Snap("pad_highlight");
            Check(fe.Highlight >= 0, "pad down highlights a button");
            Tap(160, 50);
            Check(started?.Room == "Level SP Regulator", $"REGULATOR -> Game A ({started})");
            // back from a run: the results page
            var r = fe.Save.RecordTraining(FhMode.Regulator, failed: false, 5500, 9500, 0);
            fe.Start(new FhTrainingResult(FhMode.Regulator, false, 5500, 9500, 0, r.Rank, r.MovieNowUnlocked));
            Run();
            Snap("results");
            Check(fe.ScreenName == "training_hi_score", "results page");
            fe.Pad(FhPad.Back);
            Run();
            Snap("results_quit");
            Check(fe.ScreenName == "title", "results QUIT -> title");
            Tap(128, 92);
            Tap(150, 136); // OPTIONS
            Snap("options");
            Check(fe.ScreenName == "options", "OPTIONS -> options");
            Tap(180, 78); // RECORDS
            Snap("records");
            Check(fe.ScreenName == "records", "RECORDS -> records");
            fe.Pad(FhPad.Back);
            Run();
            Check(fe.ScreenName == "options", "records BACK -> options");
            // edit name: DEL twice, then chara mode's first two keys (0x2084b40: "12"), OK saves
            Tap(128, 46);
            Check(fe.ScreenName == "edit_name_2", "NAME -> edit name");
            Tap(214, 44);
            Tap(214, 44);
            Tap(199, 142); // chara
            Snap("edit_name_chara");
            Tap(32, 76);
            Tap(56, 76);
            Tap(187, 166); // OK
            Check(fe.Save.Name == "Samus Ar12", $"chara keys type 1, 2 ({fe.Save.Name})");
            // configuration: the top picture follows the lit / picked type (configuration_bg image pairs)
            Tap(127, 108);
            Snap("configuration");
            Check(fe.ScreenName == "configuration", "CONFIGURATION -> configuration");
            Tap(207, 111); // type C2
            Snap("configuration_c2");
            Tap(188, 166); // OK
            Check(fe.Save.ControlType == 4, $"type C2 saved ({fe.Save.ControlType})");
            Console.WriteLine(fails == 0 ? "frontend: all passed" : $"frontend: {fails} FAILED");
        }

        // P6: hashes of the cart's own decoder's output (arm9 0x206b6a0 video, 0x206c0d8 audio, run on every frame of
        // every movie in a test harness): SHA-256 (first 8 bytes) over all frames' RGB555 buffers as decoded (bit 15
        // included) and over all frames' PCM16, in frame order
        static readonly (string File, int Frames, string Video, string Audio)[] MovieRefs =
        {
            ("death-15fps-up-left.avi.fv", 60, "15ad6ffc3e3975b4", "4979efc4535f2cbf"),
            ("death-15fps-down-right.avi.fv", 60, "da0336ddb4061faa", "bc3015fc9cbde177"),
            ("spawn_blue-15fps-up-left.avi.fv", 46, "de314d5c9eb60574", "1f1c30b5eb945364"),
            ("spawn_blue-15fps-down-right.avi.fv", 46, "6504e12debc39ccc", "3be985efcdde7037"),
            ("spawn_green-15fps-up-left.avi.fv", 46, "2d0c1d7469a3f7c7", "30ee920fd2483646"),
            ("spawn_green-15fps-down-right.avi.fv", 46, "c6cd50a618c3de0b", "b8113d2131e3988c"),
            ("spawn_white-15fps-up-left.avi.fv", 46, "76bb1f62fa1da60c", "a9c40410a7f040d7"),
            ("spawn_white-15fps-down-right.avi.fv", 46, "351e185d16d49299", "f18fcab750556535"),
            ("spawn_yellow-15fps-up-left.avi.fv", 46, "d0b0596b930c939e", "be28a0e6204abed8"),
            ("spawn_yellow-15fps-down-right.avi.fv", 46, "f4348f1fbbc95847", "7da55370c3ff1755"),
            ("opening-15fps-up-left.avi.fv", 300, "9d0f8f9529de3d03", "b07055da5003c0d8"),
            ("opening-15fps-down-right.avi.fv", 300, "9ff36bc2a7756cee", "dbe67151fb13addb"),
            ("teaser-15fps-up-left.avi.fv", 450, "13436ce2f74056c6", "915ddc43275982c7"),
            ("teaser-15fps-down-right.avi.fv", 450, "a9deccde4292f97a", "e651167b7bf54f5c")
        };

        // the cart's fh_ref p1 shots for a tap (at frame 30, held 40): ours at the same frames after the tap, side by side
        static string? _lastPhase;
        static void PressStrip(string page, string? xs, string? ys, string? outFile)
        {
            var screens = FhMenuData.ReadScreens();
            FhMenuScreen sc = screens.First(x => x.Name == page);
            var fe = new FhFrontend(Path.Combine(Path.GetTempPath(), "fhstrip.sav"));
            fe.Show(sc.Index);
            for (int t = 0; t < 600 && fe.Busy; t++) fe.Tick();
            for (int t = 0; t < 30; t++) fe.Tick();
            float x = float.Parse(xs ?? "100"), y = float.Parse(ys ?? "50");
            int[] shots = { 0, 10, 30, 55, 80, 120, 190 };
            var frames = new System.Collections.Generic.List<SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>>();
            fe.Touch(true, x, y);
            for (int t = 0; t <= 190; t++)
            {
                if (t == 40) fe.Touch(false, x, y);
                string ph = $"{typeof(FhFrontend).GetField("_phase", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fe)}"
                    + " " + (typeof(FhFrontend).GetField("_running", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fe) is FhMenuState rs ? rs.Name + " len " + rs.Length + " maxnode " + rs.Nodes.Max(n => Math.Abs(n.End - n.Start)) : "");
                if (ph != _lastPhase) Console.WriteLine($"  +{t}: {ph} (screen {fe.Screen})");
                _lastPhase = ph;
                if (Array.IndexOf(shots, t) >= 0)
                {
                    var tris = new System.Collections.Generic.List<MphRecomp.Frontend.WidgetTri>();
                    fe.Evaluate(tris);
                    frames.Add(FrontendTool.Raster(tris, fe.Textures, 1));
                    Console.WriteLine($"  +{t}: screen {fe.Screen}, busy {fe.Busy}");
                }
                fe.Tick();
            }
            int w = frames[0].Width, h = frames[0].Height;
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w * frames.Count, h);
            for (int i = 0; i < frames.Count; i++)
            {
                img.Mutate(c => c.DrawImage(frames[i], new SixLabors.ImageSharp.Point(i * w, 0), 1f));
                frames[i].Dispose();
            }
            img.SaveAsPng(outFile ?? Path.Combine(Path.GetTempPath(), "fh_pressstrip.png"));
        }

        // the time each page change takes (60 Hz ticks): the page's opening until it takes input, then a touch on each
        // button until the next page shows and until it takes input
        static void MenuTiming(string? only)
        {
            var screens = FhMenuData.ReadScreens();
            string save = Path.Combine(Path.GetTempPath(), "fhtiming.sav");
            File.Delete(save);
            foreach (FhMenuScreen sc in screens)
            {
                if (only != null && sc.Name != only) continue;
                if (sc.Index == FhMenuData.Popup || !File.Exists(Path.Combine(Paths.FhFileSystem, "menusTD", sc.Name + "_Model.bin"))) continue;
                for (int i = 0; i < sc.Items.Count; i++)
                {
                    FhMenuItem it = sc.Items[i];
                    if (string.IsNullOrEmpty(it.StateBase) || !it.IsButton) continue;
                    var fe = new FhFrontend(save);
                    fe.Show(sc.Index);
                    int open = 0;
                    while (fe.Busy && open < 600) { fe.Tick(); open++; }
                    // the box's centre in touch pixels (y stored negated; a circle's X0, -Y0 is its centre)
                    float tx = it.Circle ? it.X0 : (it.X0 + it.X1) / 2f, ty = it.Circle ? -it.Y0 : -(it.Y0 + it.Y1) / 2f;
                    fe.Touch(true, tx, ty);
                    int shown = -1, t = 0;
                    bool pressed = fe.Busy;
                    for (; t < 600; t++)
                    {
                        fe.Tick();
                        if (t == 2) fe.Touch(false, 0, 0);
                        if (shown < 0 && fe.Screen != sc.Index) shown = t + 1;
                        if (shown >= 0 && !fe.Busy) break;
                    }
                    Console.WriteLine($"  {sc.Name,-18} {it.StateBase,-28} open {open,3} ({open / 60.0:0.00} s)"
                        + (pressed ? $" | press -> next page {shown,3} ({shown / 60.0:0.00} s), takes input {t + 1,3} ({(t + 1) / 60.0:0.00} s) on {fe.Screen}" : " | not pressable"));
                }
            }
        }

        static void Movies(string? only, string? outDir)
        {
            int fails = 0;
            if (outDir != null) Directory.CreateDirectory(outDir);
            foreach ((string file, int frames, string videoRef, string audioRef) in MovieRefs)
            {
                if (only != null && !file.StartsWith(only, StringComparison.OrdinalIgnoreCase)) continue;
                string path = Paths.Combine(Paths.FhFileSystem, "movies/" + file);
                FvdsFile? fv = File.Exists(path) ? FvdsFile.Read(File.ReadAllBytes(path)) : null;
                if (fv == null)
                {
                    Console.WriteLine($"  FAIL {file}: missing or unreadable");
                    fails++;
                    continue;
                }
                var video = new FvdsVideo();
                var audio = new FvdsAudio();
                using var hv = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var ha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var pcm = new short[FvdsAudio.SamplesPerRecord];
                var allPcm = new System.Collections.Generic.List<short>();
                var sheet = outDir != null ? new Image<Rgb24>(8 * 128, (fv.Frames.Count + 7) / 8 * 96) : null;
                int bad = 0;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int n = 0; n < fv.Frames.Count; n++)
                {
                    FvdsFile.Frame f = fv.Frames[n];
                    if (!video.Decode(fv.Data, f.VideoOffset, f.VideoLength)) bad++;
                    hv.AppendData(MemoryMarshal.AsBytes(video.Frame));
                    for (int r = 0; r + FvdsAudio.RecordSize <= f.AudioLength; r += FvdsAudio.RecordSize)
                    {
                        audio.Decode(fv.Data.AsSpan(f.AudioOffset + r, FvdsAudio.RecordSize), pcm);
                        ha.AppendData(MemoryMarshal.AsBytes(pcm.AsSpan()));
                        if (outDir != null) allPcm.AddRange(pcm);
                    }
                    if (sheet != null)
                    {
                        ReadOnlySpan<ushort> px = video.Frame;
                        int ox = n % 8 * 128, oy = n / 8 * 96;
                        for (int y = 0; y < 96; y++)
                        {
                            for (int x = 0; x < 128; x++)
                            {
                                ushort c = px[y * 2 * FvdsVideo.Width + x * 2];
                                sheet[ox + x, oy + y] = new Rgb24((byte)((c & 31) * 255 / 31), (byte)((c >> 5 & 31) * 255 / 31), (byte)((c >> 10 & 31) * 255 / 31));
                            }
                        }
                    }
                }
                double ms = watch.Elapsed.TotalMilliseconds;
                string gotVideo = Convert.ToHexString(hv.GetHashAndReset())[..16].ToLowerInvariant();
                string gotAudio = Convert.ToHexString(ha.GetHashAndReset())[..16].ToLowerInvariant();
                bool ok = bad == 0 && fv.Frames.Count == frames && gotVideo == videoRef && gotAudio == audioRef;
                if (!ok) fails++;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {file}: {fv.Frames.Count} frames ({frames}), {fv.FramesPerSecond:0.00} fps, video {gotVideo}"
                    + $"{(gotVideo == videoRef ? "" : $" (want {videoRef})")}, audio {gotAudio}{(gotAudio == audioRef ? "" : $" (want {audioRef})")}"
                    + $"{(bad > 0 ? $", {bad} frames failed" : "")}; {ms / fv.Frames.Count:0.00} ms/frame");
                if (outDir != null)
                {
                    string stem = Path.Combine(outDir, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)));
                    sheet!.SaveAsPng(stem + ".png");
                    sheet.Dispose();
                    using var wav = new BinaryWriter(File.Create(stem + ".wav"));
                    wav.Write("RIFF"u8);
                    wav.Write(36 + allPcm.Count * 2);
                    wav.Write("WAVEfmt "u8);
                    wav.Write(16);
                    wav.Write((short)1);
                    wav.Write((short)1);
                    wav.Write(fv.AudioRate);
                    wav.Write(fv.AudioRate * 2);
                    wav.Write((short)2);
                    wav.Write((short)16);
                    wav.Write("data"u8);
                    wav.Write(allPcm.Count * 2);
                    foreach (short v in allPcm) wav.Write(v);
                }
            }
            // the app's player over the cart's movie table: every movie opens, plays to the end on a 60 Hz clock with
            // no bad frames, and queues both files' sound (left = up-left) for every record
            foreach (FhMovie movie in Enum.GetValues<FhMovie>())
            {
                if (only != null) break;
                FhMoviePlayer? player = FhMoviePlayer.Open(movie);
                if (player == null)
                {
                    Console.WriteLine($"  FAIL player {movie}: {FhMoviePlayer.OpenError}");
                    fails++;
                    continue;
                }
                var buffer = new short[4096];
                long samples = 0;
                int steps = 0;
                while (!player.Finished && steps++ < 100000)
                {
                    player.Update(1 / 60.0);
                    int n;
                    while ((n = player.ReadAudio(buffer)) > 0) samples += n;
                }
                bool ok = player.FrameIndex == player.FrameCount - 1 && player.BadFrames == 0 && samples > 0 && samples % 512 == 0;
                if (!ok) fails++;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} player {movie}: {player.FrameIndex + 1}/{player.FrameCount} frames, {player.Duration:0.00} s,"
                    + $" {samples / 2} stereo samples ({samples / 2.0 / player.AudioSampleRate:0.00} s), volume {player.DsVolume}, {player.BadFrames} bad");
            }
            Console.WriteLine(fails == 0 ? "movies: all match the cart" : $"movies: {fails} FAILED");
        }


        static string F(OpenTK.Mathematics.Vector3 v) => $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})";
    }
}
