using System;
using System.Collections.Generic;
using System.Linq;
using MphRecomp.Mods;
using MphRecomp.Save;

namespace MphRead
{
    // Headless regression tests for the mph-recomp MoonSharp mod runtime (MphRecomp.Mods).
    // Run: MphRead.Tools.dll -modtest  (no ROM/device). Proves real Lua executes and the mod API
    // works: loading, event hooks with args, persistent per-mod storage (numbers/strings/tables)
    // round-tripping through the save system, mod isolation, sandboxing, and error isolation.
    internal static class ModTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }

        // Lua uses single-quoted strings so it drops cleanly into a C# verbatim literal.
        const string ModA = @"
mod.log('init ' .. mod.id .. ' v' .. mod.version)
mod.save('counter', mod.load('counter', 0) + 1)
mod.save('profile', { level = 5, name = 'samus', tags = {'a','b','c'} })
mod.on('damage', function(amount) mod.save('total', mod.load('total', 0) + amount) end)
mod.on('tick', function(dt) mod.save('lastDt', dt) end)
";

        const string LiveMod = @"
mod.on('damage', function(target, attacker, amount, beam, fatal)
  mod.save('dmg', mod.load('dmg', 0) + amount) mod.save('beam', beam) mod.save('fatal', fatal) end)
mod.on('tick', function(dt, frame) mod.save('dt', dt) mod.save('frame', frame) end)
mod.on('start', function() mod.save('started', mod.load('started', 0) + 1) end)
mod.log('mode ' .. mod.setting('mode'))
";

        static void LiveTest()
        {
            string data = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mphmodlive_" + Guid.NewGuid().ToString("N"));
            string dir = System.IO.Path.Combine(data, "mods", "live");
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "mod.json"),
                    "{\"Id\":\"live\",\"Name\":\"Live\",\"Settings\":[{\"Id\":\"mode\",\"Values\":[\"a\",\"b\"]}]}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "main.lua"), LiveMod);
                string settingsPath = System.IO.Path.Combine(data, "recomp_settings.json");
                var settings = new MphRecomp.Config.RecompSettings();
                settings.SetModSetting("live", "mode", "b");
                settings.Save(settingsPath);

                Check("live: skipped for a replay / network game", LiveMods.Attach(data, "replay") == 0 && !GameEvents.Any);
                Check("live: folder mod attached", LiveMods.Attach(data, null) == 1 && GameEvents.Any && GameEvents.Tick);
                string log; // (open the way a player's file viewer would, while the game still writes it)
                using (var r = new System.IO.StreamReader(new System.IO.FileStream(System.IO.Path.Combine(data, "mods", LiveMods.LogFileName),
                    System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))) log = r.ReadToEnd();
                Check("live: mod.setting reads the player's pick + log file", log.Contains("[live] mode b"));
                if (GameEvents.Any) GameEvents.Raise("damage", 0, 1, 30, "PowerBeam", false);
                if (GameEvents.Any) GameEvents.Raise("damage", 0, 1, 12, "Missile", true);
                if (GameEvents.Tick) GameEvents.Raise("tick", 1 / 60.0, 600);
                ModInstance? live = LiveMods.Host?.Get("live");
                Check("live: engine event args reach Lua", live != null && live.Data["dmg"].GetDouble() == 42
                    && live.Data["beam"].GetString() == "Missile" && live.Data["fatal"].GetBoolean()
                    && live.Data["frame"].GetDouble() == 600 && Math.Abs(live.Data["dt"].GetDouble() - 1 / 60.0) < 1e-6);
                LiveMods.Detach();
                Check("live: detach clears the bus", !GameEvents.Any && !GameEvents.Tick && GameEvents.Listener == null);
                LiveMods.Attach(data, null);
                if (GameEvents.Any) GameEvents.Raise("damage", 0, -1, 8, "", false);
                Check("live: start fires once per game", LiveMods.Host?.Get("live")?.Data["started"].GetDouble() == 2);
                Check("live: mod.save kept across games (42 + 8)", LiveMods.Host?.Get("live")?.Data["dmg"].GetDouble() == 50);
                LiveMods.Detach();
                settings.DisabledMods.Add("live");
                settings.Save(settingsPath);
                Check("live: a mod turned off in MODS doesn't run", LiveMods.Attach(data, null) == 0 && !GameEvents.Any);
            }
            finally
            {
                LiveMods.Detach();
                try { System.IO.Directory.Delete(data, recursive: true); } catch (Exception) { }
            }
        }

        public static void Run(string[] args)
        {
            var logs = new List<string>();
            var host = new ModHost((id, msg) => logs.Add($"[{id}] {msg}"));

            // 1) load + init side effects (numbers, nested tables) via mod.save
            var a = host.LoadFromSource(new ModManifest { Id = "modA", Name = "Mod A", Version = "1.2.0" }, ModA);
            Check("mod loads enabled, no error", a.Enabled && a.Error == null);
            Check("init log fired", logs.Any(l => l.Contains("init modA v1.2.0")));
            Check("mod.save/load number", a.Data["counter"].GetDouble() == 1);
            Check("mod.save nested table -> object", a.Data["profile"].GetProperty("level").GetDouble() == 5
                && a.Data["profile"].GetProperty("name").GetString() == "samus");
            Check("mod.save array table -> array", a.Data["profile"].GetProperty("tags").GetArrayLength() == 3);

            // 2) event hooks receive args and accumulate
            host.Fire("damage", 10.0);
            host.Fire("damage", 5.0);
            Check("hook received args + accumulated (15)", a.Data["total"].GetDouble() == 15);
            host.Fire("tick", 0.016);
            Check("second hook fired with its arg", Math.Abs(a.Data["lastDt"].GetDouble() - 0.016) < 1e-6);

            // 3) a second mod is isolated (own id, own storage, own hooks)
            var b = host.LoadFromSource(new ModManifest { Id = "modB", Name = "Mod B" },
                "mod.save('who', mod.id) mod.on('damage', function(x) mod.save('bDmg', x) end)");
            Check("mod B has its own id", b.Data["who"].GetString() == "modB");
            Check("isolation: A cannot see B's keys", !a.Data.ContainsKey("who") && !a.Data.ContainsKey("bDmg"));
            host.Fire("damage", 7.0);
            Check("both mods' hooks fire for one event", a.Data["total"].GetDouble() == 22 && b.Data["bDmg"].GetDouble() == 7);

            // 4) sandbox: os / io / dofile are not reachable from mod Lua
            var sb = host.LoadFromSource(new ModManifest { Id = "sb" },
                "mod.save('hasOs', os ~= nil) mod.save('hasIo', io ~= nil) mod.save('hasDofile', dofile ~= nil)");
            Check("sandbox: os unavailable", !sb.Data["hasOs"].GetBoolean());
            Check("sandbox: io unavailable", !sb.Data["hasIo"].GetBoolean());
            Check("sandbox: dofile unavailable", !sb.Data["hasDofile"].GetBoolean());

            // 5) a mod with a syntax error is disabled, host keeps running
            var broken = host.LoadFromSource(new ModManifest { Id = "broken" }, "this is not )( valid lua @#$");
            Check("broken mod disabled with error", !broken.Enabled && broken.Error != null);
            host.Fire("damage", 1.0);
            Check("host keeps firing after a broken mod", a.Data["total"].GetDouble() == 23);

            // 6) a runtime error inside a hook is isolated (logged, doesn't throw or stop others)
            host.LoadFromSource(new ModManifest { Id = "boomer" }, "mod.on('boom', function() error('kaboom') end)");
            host.Fire("boom");
            Check("hook runtime error isolated + logged", logs.Any(l => l.Contains("kaboom")));

            // 6b) runaway guard: an endless loop at load or in a hook turns that mod off instead of hanging the game
            var spinLoad = host.LoadFromSource(new ModManifest { Id = "spinload" }, "while true do end");
            Check("endless loop while loading -> mod off", !spinLoad.Enabled && spinLoad.Error != null && spinLoad.Error.Contains("too long"));
            var spinHook = host.LoadFromSource(new ModManifest { Id = "spinhook" },
                "mod.on('damage', function() while true do end end)");
            Check("spinning mod loads fine", spinHook.Enabled && host.Wants("damage"));
            host.Fire("damage", 1.0);
            Check("endless loop in a hook -> mod off", !spinHook.Enabled && spinHook.Error != null);
            Check("other mods still got the event", a.Data["total"].GetDouble() == 24);
            Check("Wants: false for an event nobody hooks", !host.Wants("nobody_listens"));

            // 7) persistence round-trips through the save system across a fresh host/session
            var save = new SaveData();
            host.SaveDataTo(save);
            Check("save wrote isolated ModData slots", save.ModData.ContainsKey("modA") && save.ModData.ContainsKey("modB"));
            var host2 = new ModHost();
            var a2 = host2.LoadFromSource(new ModManifest { Id = "modA", Version = "1.2.0" }, ModA, save);
            // hydrated counter was 1 from session 1; init's mod.load('counter',0)+1 makes it 2
            Check("persisted counter carried into new session (1 -> 2)", a2.Data["counter"].GetDouble() == 2);
            Check("persisted total survived", a2.Data["total"].GetDouble() == 24);

            // 8) the live game path: engine events (MphRead GameEvents) -> LiveMods -> a folder mod's Lua, saved on detach
            LiveTest();

            Console.WriteLine($"\nMOD TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
