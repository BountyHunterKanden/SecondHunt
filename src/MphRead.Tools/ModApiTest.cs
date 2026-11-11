using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Assets;
using MphRecomp.Config;
using MphRecomp.Mods;
using MphRecomp.Sim;

namespace MphRead
{
    // Headless tests for the extended mod API that connects the pillars: mod.config (read tuning),
    // mod.asset (read override-able assets via the VFS), and mod.spawn (add sim entities). Run:
    // MphRead.Tools.dll -modapitest  (no ROM/device).
    internal static class ModApiTest
    {
        // a stand-in game for the score/match tables
        sealed class FakeGame : IModGame
        {
            public int[] P = { 0, 0, 0, 4 }, T = { 0, 0 };
            public bool Finished;
            public bool InMatch => true;
            public int Slots => 4;
            public string Mode => "Battle";
            public string? ModModeKey;
            public string? ModMode => ModModeKey;
            public bool Teams => false;
            public int Points(int slot) => slot >= 0 && slot < 4 ? P[slot] : 0;
            public void SetPoints(int slot, int points) { if (slot >= 0 && slot < 4) P[slot] = points; }
            public int TeamPoints(int team) => team >= 0 && team < 2 ? T[team] : 0;
            public void SetTeamPoints(int team, int points) { if (team >= 0 && team < 2) T[team] = points; }
            public int Kills(int slot) => slot == 2 ? 4 : 0;
            public int Deaths(int slot) => 0;
            public float TimeLeft => 60;
            public object? Setting(string name) => name == "PointGoal" ? 10.0 : null;
            public (string Hunter, int Team, bool Bot)? Player(int slot) => slot == 0 ? ("Noxus", -1, false) : null;
            public void Finish() => Finished = true;
            public int PointGoal { get; set; } = 7;
            public float TimeGoal { get; set; }
            public float Left = 60;
            public void SetTimeLeft(float seconds) => Left = seconds;
            public int Vanilla;
            public void VanillaRules() => Vanilla++;
            public (float X, float Y, float Z)?[] Obj = new (float X, float Y, float Z)?[4];
            public (float X, float Y, float Z)? BotObjective(int slot) => slot >= 0 && slot < 4 ? Obj[slot] : null;
            public void SetBotObjective(int slot, (float X, float Y, float Z)? at) { if (slot >= 0 && slot < 4) Obj[slot] = at; }
        }

        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        const string Lua = @"
mod.save('fov', mod.config('Camera.FovDegrees', 0))
mod.save('ambient', mod.config('Lighting.AmbientBase', 0))
mod.save('missing', mod.config('Nope.Nope', -1))
mod.save('greeting', mod.asset('data/hello.txt'))
mod.save('noAsset', mod.asset('data/none.txt') == nil)
mod.save('spawnId', mod.spawn('pick_health_A', 1, 2, 3))
";

        public static void Run(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_modapi_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "base", "data"));
                File.WriteAllText(Path.Combine(dir, "base", "data", "hello.txt"), "hi mod");
                var vfs = new AssetVfs();
                vfs.AddLayer("base", Path.Combine(dir, "base"));

                var world = new SimWorld();
                var host = new ModHost { Config = ConfigManager.Default(), Assets = vfs, World = world };
                var m = host.LoadFromSource(new ModManifest { Id = "apimod", Kind = "gameplay" }, Lua);
                Check("mod loaded (no error)", m.Enabled && m.Error == null);

                // config reads
                Check("mod.config reads a number (FOV 62)", m.Data["fov"].GetDouble() == 62);
                Check("mod.config reads nested (ambient 0.55)", Math.Abs(m.Data["ambient"].GetDouble() - 0.55) < 1e-6);
                Check("mod.config unknown path -> default", m.Data["missing"].GetDouble() == -1);

                // asset reads through the VFS
                Check("mod.asset reads a base asset", m.Data["greeting"].GetString() == "hi mod");
                Check("mod.asset missing -> nil", m.Data["noAsset"].GetBoolean());

                // spawn into the sim world
                Check("mod.spawn returned an id", m.Data["spawnId"].GetDouble() >= 1);
                world.Tick(1f / 60f, default); // integrate the queued spawn
                var spawned = world.Entities.FirstOrDefault(e => e.ModelName == "pick_health_A");
                Check("mod.spawn added the entity to the world", spawned != null);
                Check("mod.spawn placed it at the given position",
                    spawned != null && spawned.Position == new System.Numerics.Vector3(1, 2, 3));

                // asset override: a mod layer replaces the greeting -> mod.asset sees the override
                Directory.CreateDirectory(Path.Combine(dir, "mod", "data"));
                File.WriteAllText(Path.Combine(dir, "mod", "data", "hello.txt"), "modded");
                vfs.AddLayer("mymod", Path.Combine(dir, "mod"));
                var host2 = new ModHost { Assets = vfs };
                var m2 = host2.LoadFromSource(new ModManifest { Id = "reader" }, "mod.save('g', mod.asset('data/hello.txt'))");
                Check("mod.asset honors override layer", m2.Data["g"].GetString() == "modded");

                // no context -> calls degrade gracefully (return defaults / nil, no crash)
                var bare = new ModHost().LoadFromSource(new ModManifest { Id = "bare", Kind = "gameplay" },
                    "mod.save('c', mod.config('Camera.FovDegrees', 99)) mod.save('a', mod.asset('x') == nil) mod.save('s', mod.spawn('m') == nil)");
                Check("no context: config -> default", bare.Data["c"].GetDouble() == 99);
                Check("no context: asset -> nil, spawn -> nil", bare.Data["a"].GetBoolean() && bare.Data["s"].GetBoolean());
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }

            // MOD_API �10 phase 1: Kind is enforced - a cosmetic mod may not filter or spawn
            var cos = new ModHost().LoadFromSource(new ModManifest { Id = "cos", Kind = "cosmetic" },
                "mod.filter('damage', function(t) t.damage = 0 end)");
            Check("cosmetic mod calling mod.filter is refused", !cos.Enabled && cos.Error != null && cos.Error.Contains("gameplay"));
            var gp = new ModHost().LoadFromSource(new ModManifest { Id = "gp", Kind = "gameplay" },
                "mod.filter('damage', function(t) t.damage = t.damage * 2 end)");
            Check("gameplay mod may filter damage", gp.Enabled && gp.Error == null);
            var bad = new ModHost().LoadFromSource(new ModManifest { Id = "bad", Kind = "gameplay" },
                "mod.filter('no_such_filter', function(t) end)");
            Check("unknown filter name is refused", !bad.Enabled && bad.Error != null);

            // phase 1 runtime: require loads the mod's own files (once), nothing outside its folder
            var files = new Dictionary<string, string> { ["lib/util.lua"] = "count = (count or 0) + 1 return { twice = function(x) return x * 2 end }" };
            var req = new ModHost().LoadFromSource(new ModManifest { Id = "req" },
                "local u = require('lib.util') local v = require('lib.util') mod.save('r', u.twice(21)) mod.save('n', count)",
                readFile: f => files.TryGetValue(f, out var t) ? t : null);
            Check("require loads lib/util.lua once", req.Enabled && req.Data["r"].GetDouble() == 42 && req.Data["n"].GetDouble() == 1);
            var esc = new ModHost().LoadFromSource(new ModManifest { Id = "esc" }, "require('..secret')", readFile: f => "return 1");
            Check("require refuses a path outside the mod", !esc.Enabled && esc.Error != null);

            // load order: Requires sorts a mod after its dependency; a missing one leaves the mod off with a reason
            var mgr = new ModManager();
            mgr.Add(new ModManifest { Id = "child", Requires = { "base>=1.0.0" } }, "");
            mgr.Add(new ModManifest { Id = "base", Version = "1.2.0" }, "");
            mgr.Add(new ModManifest { Id = "orphan", Requires = { "nowhere" } }, "");
            var order = mgr.Resolve().Select(e => e.Manifest.Id).ToList();
            Check("Requires loads base before child", order.IndexOf("base") >= 0 && order.IndexOf("base") < order.IndexOf("child"));
            Check("a missing requirement leaves the mod off", !order.Contains("orphan")
                && mgr.Problems.Any(p => p.Id == "orphan" && p.Problem.Contains("nowhere")));

            // typed settings: normalize, step and clamp
            var sb = new ModSetting { Id = "b", Type = "bool", Default = "true" };
            var si = new ModSetting { Id = "i", Type = "int", Min = 1, Max = 9, Default = "3" };
            var sr = new ModSetting { Id = "r", Type = "range", Min = 0.5, Max = 2, Step = 0.25, Default = "1" };
            Check("typed settings: bool/int/range normalize + step", sb.Normalize("junk") == "true" && sb.Next("true", 1) == "false"
                && si.Normalize("42") == "9" && si.Next("9", 1) == "9" && si.Next("3", -1) == "2"
                && sr.Normalize("1.1") == "1" && sr.Next("2", 1) == "2" && sr.Next("1", 1) == "1.25");

            // setting_changed reaches the mod
            var scHost = new ModHost();
            var sc = scHost.LoadFromSource(new ModManifest { Id = "sc" },
                "mod.on('setting_changed', function(m, k, v) mod.save('got', m .. ':' .. k .. '=' .. tostring(v)) end)");
            scHost.Fire("setting_changed", "sc", "lives", 5);
            Check("setting_changed fires with mod, setting, value", sc.Data.TryGetValue("got", out var got) && got.GetString() == "sc:lives=5");

            // mod.save keeps at most 1 MB per mod
            var big = new ModHost().LoadFromSource(new ModManifest { Id = "big" },
                "mod.save('a', 'x') mod.save('b', string.rep('y', 1100000))");
            Check("mod.save over 1 MB is an error (and keeps the old data)", !big.Enabled && big.Error?.Contains("KB") == true
                && big.Data.ContainsKey("a") && !big.Data.ContainsKey("b"));

            // determinism: a gameplay mod's math is DetMath; mod.random repeats for the same seed and differs per seed
            var dm = new ModHost().LoadFromSource(new ModManifest { Id = "dm", Kind = "gameplay" }, "mod.save('s', math.sin(1.234))");
            Check("gameplay math.sin == DetMath.Sin", dm.Data["s"].GetDouble() == DetMath.Sin(1.234));
            double Roll(ulong seed) => new ModHost { ModSeed = seed }.LoadFromSource(new ModManifest { Id = "rng", Kind = "gameplay" },
                "mod.save('v', mod.random(1, 1000000))").Data["v"].GetDouble();
            Check("mod.random: same seed same value, other seed differs", Roll(7) == Roll(7) && Roll(7) != Roll(8));
            var clock = new ModHost().LoadFromSource(new ModManifest { Id = "clk", Kind = "gameplay" }, "mod.save('c', os == nil or os.time == nil)");
            Check("gameplay mods get no clock", clock.Data["c"].GetBoolean());

            ReplayTest.ModsPhase1(Check);

            // ---- phase 2: the enemy_spawn filter, the score and match tables ----
            var es = new ModHost().LoadFromSource(new ModManifest { Id = "es", Kind = "gameplay" },
                "mod.filter('enemy_spawn', function(t) if t.enemy == 'Zoomer' then t.health = t.health * 3 end end)");
            Check("enemy_spawn is a filter mods can hook", es.Enabled && es.Filters.ContainsKey("enemy_spawn"));
            var esHost = new ModHost();
            esHost.LoadFromSource(new ModManifest { Id = "es2", Kind = "gameplay" },
                "mod.filter('enemy_spawn', function(t) if t.enemy == 'Zoomer' then t.health = t.health * 3 end if t.enemy == 'War' then t.health = -5 end end)");
            GameFilters.Set(esHost.FilterNames(), esHost.ApplyFilter);
            Check("enemy_spawn: Zoomer health 3x, others unchanged, clamped to 1",
                GameFilters.EnemySpawnHealth("Zoomer", 0, 20) == 60 && GameFilters.EnemySpawnHealth("Petrasyl1", 0, 20) == 20
                && GameFilters.EnemySpawnHealth("War", 0, 20) == 1);
            GameFilters.Clear();
            Check("enemy_spawn: no gameplay mod, no flag", !GameFilters.EnemySpawn);

            var game = new FakeGame();
            var sg = new ModHost { Game = game }.LoadFromSource(new ModManifest { Id = "sg", Kind = "gameplay" },
                "score.set(1, 5) score.add(1, 2) score.team_add(0, 3) mod.save('p', score.get(1)) mod.save('k', score.kills(2))"
                + " mod.save('m', match.mode()) mod.save('g', match.setting('PointGoal')) mod.save('h', match.player(0).hunter)"
                + " mod.save('n', match.setting('Nope') == nil) match.finish()");
            Check("score.set/add/get + team_add (gameplay mod)", sg.Enabled && game.P[1] == 7 && game.T[0] == 3
                && sg.Data["p"].GetDouble() == 7 && sg.Data["k"].GetDouble() == 4);
            Check("match.mode / setting / player read, unknown setting nil", sg.Data["m"].GetString() == "Battle"
                && sg.Data["g"].GetDouble() == 10 && sg.Data["h"].GetString() == "Noxus" && sg.Data["n"].GetBoolean());
            Check("match.finish ends the match", game.Finished);
            var cg = new FakeGame();
            var cs = new ModHost { Game = cg }.LoadFromSource(new ModManifest { Id = "cs", Kind = "cosmetic" },
                "mod.save('p', score.get(3)) score.set(3, 99)");
            Check("cosmetic mod reads scores but score.set is refused", !cs.Enabled && cs.Error?.Contains("gameplay") == true
                && cs.Data["p"].GetDouble() == 4 && cg.P[3] == 4);
            var none = new ModHost().LoadFromSource(new ModManifest { Id = "ng", Kind = "gameplay" },
                "score.add(0, 1) match.finish() mod.save('s', score.get(0)) mod.save('a', match.active())");
            Check("no game: score reads 0, changes do nothing", none.Enabled && none.Data["s"].GetDouble() == 0 && !none.Data["a"].GetBoolean());
            var ms = MphRecomp.Multiplayer.MatchSettings.Quick(GameMode.Capture, "MP3 PROVING GROUND", Hunter.Spire, 1);
            ms.PointGoal = 5; ms.FriendlyFire = true;
            Check("LiveGame.SettingOf: enum name, number, bool, hidden network fields, case-insensitive",
                (string?)LiveGame.SettingOf(ms, "Mode") == "Capture" && (double?)LiveGame.SettingOf(ms, "pointgoal") == 5
                && LiveGame.SettingOf(ms, "FriendlyFire") is true && LiveGame.SettingOf(ms, "IsClient") == null
                && LiveGame.SettingOf(ms, "Players") == null);

            // ---- phase 2: room.on_load ----
            var rlHost = new ModHost();
            var rl = rlHost.LoadFromSource(new ModManifest { Id = "rl", Kind = "gameplay" },
                "room.on_load(function(r) mod.save('room', r.name .. ':' .. r.id)"
                + " if r.name == 'UNIT1_LAND' then r.add_pickup('MissileBig', 1, 2.5, -3) r.add_enemy('mph:Zoomer', 4, 0, 5, 90)"
                + " r.add_pickup('UASmall', 0, 0, 0, 12) end end)");
            Check("room.on_load: a gameplay mod's handler is registered", rl.Enabled && rlHost.WantsRoomLoad());
            List<RoomAdd> adds = rlHost.RoomLoad(27, "UNIT1_LAND");
            Check("room.on_load: adds come back in order with their fields", adds.Count == 3
                && adds[0] == new RoomAdd("rl", "pickup", "MissileBig", 1, 2.5f, -3, 0)
                && adds[1] == new RoomAdd("rl", "enemy", "mph:Zoomer", 4, 0, 5, 90)
                && adds[2].Kind == "pickup" && adds[2].Value == 12 && rl.Data["room"].GetString() == "UNIT1_LAND:27");
            Check("room.on_load: another room adds nothing", rlHost.RoomLoad(28, "UNIT1_C0").Count == 0);
            var rc = new ModHost().LoadFromSource(new ModManifest { Id = "rc", Kind = "cosmetic" },
                "room.on_load(function(r) end)");
            Check("room.on_load is refused for a cosmetic mod", !rc.Enabled && rc.Error?.Contains("gameplay") == true);
            var rbHost = new ModHost();
            rbHost.LoadFromSource(new ModManifest { Id = "rb", Kind = "gameplay" },
                "room.on_load(function(r) r.add_pickup('MissileBig', 0, 0, 0) r.add_pickup(5) end)"
                + " room.on_load(function(r) r.add_pickup('UABig', 1, 1, 1) end)");
            List<RoomAdd> rbAdds = rbHost.RoomLoad(1, "X");
            Check("room.on_load: a handler that errors adds nothing, the next still runs", rbAdds.Count == 1 && rbAdds[0].Type == "UABig");
            var rmHost = new ModHost();
            rmHost.LoadFromSource(new ModManifest { Id = "rm", Kind = "gameplay" },
                "room.on_load(function(r) for i = 1, 100 do r.add_pickup('UASmall', i, 0, 0) end end)");
            Check($"room.on_load: over {ModHost.MaxRoomAdds} adds is an error (none kept)", rmHost.RoomLoad(1, "X").Count == 0);
            var rrHost = new ModHost();
            var rr = rrHost.LoadFromSource(new ModManifest { Id = "rr", Kind = "gameplay" },
                "room.on_load(function(r) r.add_pickup('UASmall', 0, 0, 0) while true do end end)");
            Check("room.on_load: a runaway handler is turned off, adds nothing", rrHost.RoomLoad(1, "X").Count == 0 && !rr.Enabled
                && !rrHost.WantsRoomLoad());

            // ---- phase 2: entity.* ----
            var fakeEnt = new FakeEntities();
            int queued = 0;
            var enHost = new ModHost { Entities = fakeEnt, EntityOpsQueued = () => queued++ };
            var en = enHost.LoadFromSource(new ModManifest { Id = "en", Kind = "gameplay" },
                "function look() local hs = entity.enemies() local e = entity.get(hs[2])"
                + " mod.save('look', #hs .. ':' .. e.type .. ':' .. e.health .. '/' .. e.max_health .. ':' .. e.x .. ':' .. tostring(entity.get(99))) end"
                + " function act() entity.spawn_enemy('mph:Zoomer', 1, 2, 3, 90) entity.remove(7) end"
                + " function flood() for i = 1, 20 do entity.remove(i) end end");
            en.Script.Call(en.Script.Globals.Get("look"));
            Check("entity.enemies/get read the scene's enemies", en.Enabled && en.Data["look"].GetString() == "2:Zoomer:5/8:4:nil");
            en.Script.Call(en.Script.Globals.Get("act"));
            List<EntityOp> ops = enHost.TakeEntityOps();
            Check("entity.spawn_enemy/remove are queued in order", ops.Count == 2 && queued == 2
                && ops[0] == new EntityOp("en", "spawn", "mph:Zoomer", -1, 1, 2, 3, 90)
                && ops[1].Op == "remove" && ops[1].Handle == 7 && enHost.TakeEntityOps().Count == 0);
            bool capped = false;
            try { en.Script.Call(en.Script.Globals.Get("flood")); } catch (MoonSharp.Interpreter.ScriptRuntimeException) { capped = true; }
            Check($"entity: over {ModHost.MaxEntityOpsPerFrame} ops a frame is an error", capped
                && enHost.TakeEntityOps().Count == ModHost.MaxEntityOpsPerFrame);
            var ecHost = new ModHost { Entities = fakeEnt };
            var ec = ecHost.LoadFromSource(new ModManifest { Id = "ec", Kind = "cosmetic" },
                "mod.save('n', #entity.enemies()) entity.remove(1)");
            Check("entity: a cosmetic mod reads but can't remove", !ec.Enabled && ec.Error?.Contains("gameplay") == true
                && ec.Data["n"].GetRawText() == "2" && ecHost.TakeEntityOps().Count == 0);
            var eoHost = new ModHost();
            var eo = eoHost.LoadFromSource(new ModManifest { Id = "eo", Kind = "gameplay" },
                "mod.save('n', #entity.enemies() .. tostring(entity.get(1)))");
            Check("entity: with no scene, enemies() is empty and get is nil", eo.Enabled && eo.Data["n"].GetString() == "0nil");
            var edHost = new ModHost();
            var ed = edHost.LoadFromSource(new ModManifest { Id = "ed", Kind = "gameplay" },
                "mod.on('tick', function() entity.remove(1) while true do end end)");
            edHost.Fire("tick");
            Check("entity: a turned-off mod's queued ops are dropped", !ed.Enabled && edHost.TakeEntityOps().Count == 0);

            // ---- phase 2: the message bus (mod.on_message, msg.send) ----
            int mq = 0;
            var mbHost = new ModHost { EntityOpsQueued = () => mq++ };
            var mb = mbHost.LoadFromSource(new ModManifest { Id = "mb", Kind = "cosmetic" },
                "mod.on_message('Unlock', function(t, f, to, a, b) mod.save('u', t .. f .. ':' .. to .. ':' .. a .. ':' .. b) end)"
                + " mod.on_message(9, function(t, f, to, a) mod.save('id', t .. ':' .. tostring(a)) end)");
            mbHost.FireMessage(MphRead.Message.Unlock, 5, 9, 1, 0);
            bool unlockSeen = mb.Data.TryGetValue("u", out var uv) && uv.GetString() == "Unlock5:9:1:0";
            mbHost.FireMessage(MphRead.Message.Lock, 9, 3, new object(), 0);
            mbHost.FireMessage(MphRead.Message.Damage, 1, 2, 4, 0);
            Check("mod.on_message watches by name and by entity id (sender or target); a cosmetic mod may watch",
                mb.Enabled && unlockSeen && mb.Data["id"].GetString() == "Lock:nil" && mbHost.WantsMessages());
            var mnHost = new ModHost();
            var mn = mnHost.LoadFromSource(new ModManifest { Id = "mn", Kind = "cosmetic" }, "mod.on_message('Nope', function() end)");
            Check("mod.on_message: an unknown name is an error", !mn.Enabled && mn.Error?.Contains("Nope") == true && !mnHost.WantsMessages());
            var msd = mbHost.LoadFromSource(new ModManifest { Id = "ms", Kind = "gameplay" },
                "msg.send('Unlock', 3, 9, 1) msg.send('SetTriggerState', nil, nil, 5, 0, 30) msg.send('Activate', nil, 12)"
                + " function flood() for i = 1, 20 do msg.send('Lock', nil, i) end end");
            List<MessageOp> sent = mbHost.TakeMessageOps();
            Check("msg.send queues in order, from defaults to nil (-1)", msd.Enabled && sent.Count == 3 && mq == 3
                && sent[0] == new MessageOp("ms", MphRead.Message.Unlock, 3, 9, 1, 0, 0)
                && sent[1] == new MessageOp("ms", MphRead.Message.SetTriggerState, -1, -1, 5, 0, 30)
                && sent[2] == new MessageOp("ms", MphRead.Message.Activate, -1, 12, 0, 0, 0) && mbHost.TakeMessageOps().Count == 0);
            bool Err(string call, string has)
            {
                var h = new ModHost();
                var b = h.LoadFromSource(new ModManifest { Id = "mbad", Kind = "gameplay" }, call);
                return !b.Enabled && b.Error?.Contains(has) == true && h.TakeMessageOps().Count == 0;
            }
            Check("msg.send refuses unsendable messages, long delays, bad bits, no target and fractions",
                Err("msg.send('Damage', 1, 2)", "can't send") && Err("msg.send('Unlock', 1, 2, 0, 0, 601)", "delay")
                && Err("msg.send('SetTriggerState', nil, nil, 32)", "bit") && Err("msg.send('Unlock', 1)", "target")
                && Err("msg.send('Unlock', 1, 2.5)", "to (") && Err("msg.send('Nope', 1, 2)", "message name"));
            bool mcap = false;
            try { msd.Script.Call(msd.Script.Globals.Get("flood")); } catch (MoonSharp.Interpreter.ScriptRuntimeException) { mcap = true; }
            Check($"msg.send: over {ModHost.MaxMessagesPerFrame} a frame is an error", mcap
                && mbHost.TakeMessageOps().Count == ModHost.MaxMessagesPerFrame);
            var mcHost = new ModHost();
            var mc = mcHost.LoadFromSource(new ModManifest { Id = "mc", Kind = "cosmetic" }, "msg.send('Unlock', 1, 2)");
            Check("msg.send: a cosmetic mod can't send", !mc.Enabled && mc.Error?.Contains("gameplay") == true && mcHost.TakeMessageOps().Count == 0);
            var mrHost = new ModHost();
            var mr = mrHost.LoadFromSource(new ModManifest { Id = "mr", Kind = "gameplay" },
                "mod.on_message('Unlock', function() msg.send('Lock', nil, 1) while true do end end)");
            mrHost.FireMessage(MphRead.Message.Unlock, 1, 2, 0, 0);
            Check("mod.on_message: a runaway handler is turned off and its sends dropped", !mr.Enabled && !mrHost.WantsMessages()
                && mrHost.TakeMessageOps().Count == 0);

            // ---- mod.campaign: per-campaign-file values ----
            const string CampLua = "got = mod.campaign.get('n', 'none'); ok = mod.campaign.set('n', 3); ok2 = mod.campaign.set('t', {a = 'x'}); "
                + "back = mod.campaign.get('n'); flag = mod.campaign.get('missing', true)";
            var noCampHost = new ModHost();
            var nc = noCampHost.LoadFromSource(new ModManifest { Id = "cg", Kind = "gameplay" }, CampLua);
            Check("mod.campaign: no campaign file = get gives the default, set does nothing", nc.Enabled
                && nc.Script.Globals.Get("got").String == "none" && nc.Script.Globals.Get("ok").Boolean == false
                && nc.Script.Globals.Get("back").IsNil());
            var campHost = new ModHost { Campaign = new() { ["other"] = new() { ["k"] = System.Text.Json.JsonSerializer.SerializeToElement(1) } } };
            var campMod = campHost.LoadFromSource(new ModManifest { Id = "cg", Kind = "gameplay" }, CampLua);
            Check("mod.campaign: set/get by mod id, tables kept, the default for a missing key", campMod.Enabled
                && campMod.Script.Globals.Get("ok").Boolean && campMod.Script.Globals.Get("ok2").Boolean && campMod.Script.Globals.Get("back").Number == 3
                && campMod.Script.Globals.Get("flag").Boolean && campHost.Campaign["cg"]["n"].GetDouble() == 3 && campHost.Campaign.Count == 2);
            var ccHost = new ModHost { Campaign = campHost.Campaign };
            var cc = ccHost.LoadFromSource(new ModManifest { Id = "cg", Kind = "cosmetic" }, "seen = mod.campaign.get('n'); mod.campaign.set('n', 4)");
            Check("mod.campaign: a cosmetic mod reads but can't set", !cc.Enabled && cc.Error?.Contains("gameplay") == true
                && cc.Script.Globals.Get("seen").Number == 3 && campHost.Campaign["cg"]["n"].GetDouble() == 3);
            var bigHost = new ModHost { Campaign = new() };
            var bigMod = bigHost.LoadFromSource(new ModManifest { Id = "big", Kind = "gameplay" },
                $"mod.campaign.set('a', string.rep('x', {ModHost.MaxCampaignBytes / 2})); mod.campaign.set('b', string.rep('x', {ModHost.MaxCampaignBytes}))");
            Check("mod.campaign: over the size cap is an error and keeps the old values", !bigMod.Enabled && bigMod.Error?.Contains("KB") == true
                && bigHost.Campaign["big"].Count == 1);
            var file = new MphRecomp.Campaign.CampaignSaveFile { Story = new MphRead.StorySave(), Mods = campHost.Campaign };
            var round = MphRecomp.Campaign.CampaignSaves.FromJson(MphRecomp.Campaign.CampaignSaves.ToJson(file));
            var plain = MphRecomp.Campaign.CampaignSaves.FromJson(MphRecomp.Campaign.CampaignSaves.ToJson(
                new MphRecomp.Campaign.CampaignSaveFile { Story = new MphRead.StorySave() }));
            Check("campaign save: Mods round-trips, saves without it read as none", round.Mods != null
                && round.Mods["cg"]["n"].GetDouble() == 3 && round.Mods["cg"]["t"].GetProperty("a").GetString() == "x"
                && round.Mods["other"]["k"].GetInt32() == 1 && plain.Mods == null);
            LiveMods.AdoptCampaign(round.Mods);
            var forSave = LiveMods.CampaignDataForSave();
            LiveMods.Detach();
            Check("LiveMods: campaign values kept for unloaded mods, cleared at Detach", forSave != null && forSave.ContainsKey("other")
                && forSave["cg"].Count == 2 && LiveMods.CampaignDataForSave() == null);

            // ---- match goals + mode.setup / mode.tick / mode.vanilla ----
            var modeGame = new FakeGame();
            var modeHost = new ModHost { Game = modeGame };
            var md = modeHost.LoadFromSource(new ModManifest { Id = "md", Kind = "gameplay" },
                "g0 = match.point_goal() "
                + "mode.setup(function() match.set_point_goal(25) match.set_time_goal(30) match.set_time_left(90) end) "
                + "ticks = 0 mode.tick(function(dt) ticks = ticks + 1 if ticks == 2 then mode.vanilla() end end)");
            Check("mode.setup/tick register, nothing runs at load", md.Enabled && md.Script.Globals.Get("g0").Number == 7
                && modeHost.Wants(ModHost.ModeSetupEvent) && modeHost.Wants(ModHost.ModeTickEvent)
                && modeGame.PointGoal == 7 && md.Script.Globals.Get("ticks").Number == 0);
            modeHost.Fire(ModHost.ModeSetupEvent);
            Check("mode.setup: set_point_goal / set_time_goal / set_time_left", modeGame.PointGoal == 25 && modeGame.TimeGoal == 30
                && modeGame.Left == 90);
            modeHost.Fire(ModHost.ModeTickEvent, 1 / 60.0);
            modeHost.Fire(ModHost.ModeTickEvent, 1 / 60.0);
            Check("mode.tick runs each frame, mode.vanilla runs the mode's rules", md.Script.Globals.Get("ticks").Number == 2
                && modeGame.Vanilla == 1);
            var modeCosHost = new ModHost { Game = modeGame };
            var mcRead = modeCosHost.LoadFromSource(new ModManifest { Id = "mc", Kind = "cosmetic" },
                "seen = match.point_goal() match.set_point_goal(3)");
            var mcTick = modeCosHost.LoadFromSource(new ModManifest { Id = "mt", Kind = "cosmetic" }, "mode.tick(function() end)");
            Check("cosmetic mods read the goals but can't set them or run the mode", !mcRead.Enabled
                && mcRead.Script.Globals.Get("seen").Number == 25 && modeGame.PointGoal == 25 && !mcTick.Enabled
                && !modeCosHost.Wants(ModHost.ModeTickEvent));
            var onAt = new ModHost().LoadFromSource(new ModManifest { Id = "oa", Kind = "gameplay" }, "mod.on('@mode_tick', function() end)");
            Check("mod.on can't take the mode's hook names", !onAt.Enabled && onAt.Error?.Contains("mode.tick") == true);

            // ---- bot.set_objective / bot.objective ----
            var botGame = new FakeGame();
            var botHost = new ModHost { Game = botGame };
            var bo = botHost.LoadFromSource(new ModManifest { Id = "bo", Kind = "gameplay" },
                "none = bot.objective(1) bot.set_objective(1, 2, 3.5, -4) x, y, z = bot.objective(1) "
                + "bot.set_objective(2, 1, 1, 1) bot.set_objective(2) cleared = bot.objective(2)");
            Check("bot.set_objective: sets, reads back, clears with just the slot", bo.Enabled
                && bo.Script.Globals.Get("none").IsNil() && bo.Script.Globals.Get("x").Number == 2
                && bo.Script.Globals.Get("y").Number == 3.5 && bo.Script.Globals.Get("z").Number == -4
                && botGame.Obj[1] == (2f, 3.5f, -4f) && botGame.Obj[2] == null && bo.Script.Globals.Get("cleared").IsNil());
            var boBad = botHost.LoadFromSource(new ModManifest { Id = "bb", Kind = "gameplay" }, "bot.set_objective(0, 'here')");
            var boCos = new ModHost { Game = botGame }.LoadFromSource(new ModManifest { Id = "bc", Kind = "cosmetic" },
                "seen = bot.objective(1) bot.set_objective(1)");
            Check("bot.set_objective: a bad position is an error; cosmetic mods read but can't set", !boBad.Enabled
                && boBad.Error?.Contains("position") == true && !boCos.Enabled && boCos.Script.Globals.Get("seen").Number == 2
                && botGame.Obj[1] != null);

            // ---- mod modes (mod.json Modes) + mode.current ----
            ModManifest ModeMod(string kind, params ModModeInfo[] modes)
            {
                var m = new ModManifest { Id = "tagmod", Kind = kind };
                m.Modes.AddRange(modes);
                return m;
            }
            Check("Modes: a gameplay mod's mode validates; Base by name, any case", ModeMod("gameplay",
                new ModModeInfo { Id = "tag", Base = "prime hunter" }).Validate() == null
                && new ModModeInfo { Base = "survival" }.BaseMode == MphRead.GameMode.Survival);
            Check("Modes: refused on a cosmetic mod, unknown Base, two the same, ':' in the Id, a '$' setting",
                ModeMod("cosmetic", new ModModeInfo { Id = "tag" }).Validate()?.Contains("gameplay") == true
                && ModeMod("gameplay", new ModModeInfo { Id = "tag", Base = "SinglePlayer" }).Validate()?.Contains("Base") == true
                && ModeMod("gameplay", new ModModeInfo { Id = "a" }, new ModModeInfo { Id = "a" }).Validate()?.Contains("two") == true
                && ModeMod("gameplay", new ModModeInfo { Id = "a:b" }).Validate()?.Contains("bad mode") == true
                && new ModManifest { Id = "s", Settings = { new ModSetting { Id = "$mode" } } }.Validate()?.Contains("setting") == true);
            var mcGame = new FakeGame { ModModeKey = "tagmod:tag" };
            var mcur = new ModHost { Game = mcGame }.LoadFromSource(new ModManifest { Id = "mc" },
                "id, owner = mode.current() mod.save('c', tostring(id) .. '/' .. tostring(owner))");
            mcGame.ModModeKey = null;
            var mnone = new ModHost { Game = mcGame }.LoadFromSource(new ModManifest { Id = "mn" }, "mod.save('c', mode.current() == nil)");
            Check("mode.current: the mode's id and its mod (any mod reads), nil for a plain mode", mcur.Enabled
                && mcur.Data["c"].GetString() == "tag/tagmod" && mnone.Data["c"].GetBoolean());
            LiveModesCheck();

            Console.WriteLine($"\nMOD API TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        // LiveMods with a mod that adds a mode and one that doesn't: the menu list, which mods a match runs, the replay row
        static void LiveModesCheck()
        {
            string data = Path.Combine(Path.GetTempPath(), "mphrecomp_modmodes_" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach ((string id, string extra) in new[] { ("plain", ""),
                    ("tagmod", ", \"Modes\": [ { \"Id\": \"tag\", \"Name\": \"TAG\", \"Base\": \"Battle\", \"PointGoal\": 5, \"TimeLimit\": 3 } ]") })
                {
                    string dir = Path.Combine(data, "mods", id);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "mod.json"),
                        $"{{ \"Id\": \"{id}\", \"Version\": \"1.0.0\", \"Kind\": \"gameplay\", \"Entry\": \"main.lua\"{extra} }}");
                    File.WriteAllText(Path.Combine(dir, "main.lua"), "mod.on('tick', function() end)");
                }
                int outside = LiveMods.Attach(data, null);
                bool modeModIdle = LiveMods.PlayHost?.Get("tagmod") == null;
                LiveMods.Detach();
                Check("mod modes: outside a match the mode's mod stays off", outside == 1 && modeModIdle);

                LiveMods.Attach(data, null, match: true);
                List<LiveMods.ModeChoice> modes = LiveMods.Modes();
                Check("mod modes: the menu list has the mod's mode with its starting values", modes.Count == 1
                    && modes[0].Key == "tagmod:tag" && modes[0].Name == "TAG" && modes[0].Base == MphRead.GameMode.Battle
                    && modes[0].PointGoal == 5 && modes[0].TimeLimitMinutes == 3 && modes[0].TimeGoalSeconds == null);
                var plainHeader = LiveMods.StartMatch(null, out _, new MphRecomp.Multiplayer.MatchSettings());
                bool plainKey = LiveGame.CurrentModMode == null;
                var tagHeader = LiveMods.StartMatch(null, out _, new MphRecomp.Multiplayer.MatchSettings { ModMode = "tagmod:tag" });
                bool tagKey = LiveGame.CurrentModMode == "tagmod:tag";
                var badHeader = LiveMods.StartMatch(null, out _, new MphRecomp.Multiplayer.MatchSettings { ModMode = "tagmod:nope" });
                bool badKey = LiveGame.CurrentModMode == null;
                Check("mod modes: a plain match runs only mods without modes; the mode's match adds its mod and records $mode",
                    plainHeader.Count == 1 && plainHeader[0].Id == "plain" && plainKey
                    && tagHeader.Count == 2 && tagHeader.Single(m => m.Id == "tagmod").Settings[LiveMods.ModeSettingKey] == "tag"
                    && !tagHeader.Single(m => m.Id == "plain").Settings.ContainsKey(LiveMods.ModeSettingKey) && tagKey
                    && badHeader.Count == 1 && badKey);
                var rec = new MphRecomp.Replay.ReplayFile();
                rec.Mods.AddRange(tagHeader);
                LiveMods.StartMatch(rec, out _);
                Check("mod modes: playback takes the mode from the recorded row", LiveGame.CurrentModMode == "tagmod:tag"
                    && LiveMods.PlayHost?.Get("tagmod")?.Enabled == true);
                var carry = new MphRecomp.Multiplayer.MatchSettings { ModMode = "tag mod:tag" };
                Check("mod modes: the setup line carries the mode offline only",
                    MphRecomp.Frontend.MatchSetupText.Read(MphRecomp.Frontend.MatchSetupText.Write(carry)).ModMode == "tag mod:tag"
                    && MphRecomp.Frontend.MatchSetupText.Read(MphRecomp.Frontend.MatchSetupText.Write(carry, "host")).ModMode == null);
            }
            finally
            {
                LiveMods.Detach();
                LiveGame.CurrentModMode = null;
                try { Directory.Delete(data, recursive: true); } catch { }
            }
        }

        sealed class FakeEntities : IModEntities
        {
            public IReadOnlyList<int> Enemies() => new[] { 3, 7 };
            public ModEntityInfo? Get(int handle) => handle switch
            {
                3 => new ModEntityInfo("enemy", "War Wasp", 0, 0, 0, 1, 1),
                7 => new ModEntityInfo("enemy", "Zoomer", 4, 0, 5, 5, 8),
                _ => null,
            };
        }
    }
}
