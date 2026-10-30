using System;
using System.IO;
using System.Numerics;
using MphRecomp.Game;
using MphRecomp.Input;
using MphRecomp.Mods;
using MphRecomp.Save;
using MphRecomp.Sim;

namespace MphRead
{
    // Headless end-to-end test of the composed game (MphRecomp.Game.GameSession): input -> sim
    // ticks -> player movement + camera -> render snapshot, with mod hooks firing and save
    // persistence, all wired together. Run: MphRead.Tools.dll -gametest  (no ROM/device).
    internal static class GameTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok) { if (ok) _pass++; else _fail++; Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); }

        const string Lua = @"
mod.on('start', function() mod.save('started', true) end)
mod.on('room_enter', function(room) mod.save('room', room) end)
mod.on('tick', function(dt) mod.save('ticks', mod.load('ticks', 0) + 1) end)
";

        public static void Run(string[] args)
        {
            var host = new ModHost();
            var mod = host.LoadFromSource(new ModManifest { Id = "m" }, Lua);
            var save = new SaveData();
            var session = new GameSession(save: save, mods: host);

            session.Start();
            Check("mod received 'start' event", mod.Data.ContainsKey("started") && mod.Data["started"].GetBoolean());

            var player = session.EnterRoom("MP1 SANCTORUS", collision: null, spawnPos: Vector3.Zero, spawnYaw: 0f);
            Check("mod received 'room_enter' with room name", mod.Data["room"].GetString() == "MP1 SANCTORUS");

            // drive forward for ~0.5s of 60fps frames (each Update runs exactly one fixed tick)
            var fwd = new RawInput(); fwd.Set(RawAxis.LeftY, -1f);
            RenderSnapshot snap = null!;
            for (int i = 0; i < 30; i++) snap = session.Update(1.0 / 60.0, fwd);
            Check("input drove player forward (-Z)", player.Position.Z < -1f);
            Check("player did not drift sideways", Math.Abs(player.Position.X) < 1e-3f);
            Check("snapshot camera follows the player", snap.CameraPosition.Z < -0.5f);
            Check("snapshot has no drawn items (player is the camera)", snap.Items.Count == 0);

            // look right and confirm the player turns
            float yaw0 = player.Yaw;
            var look = new RawInput(); look.Set(RawAxis.RightX, 1f);
            for (int i = 0; i < 30; i++) session.Update(1.0 / 60.0, look);
            Check("look input turns the player (yaw increases)", player.Yaw > yaw0 + 0.3f);
            Check("facing vector rotated off -Z", Math.Abs(player.Facing.X) > 0.1f);

            Check("mods received tick events each fixed tick", mod.Data["ticks"].GetDouble() >= 60);

            // save round-trip through the session -> a fresh session/host sees the persisted mod state
            double ticksNow = mod.Data["ticks"].GetDouble();
            string dir = Path.Combine(Path.GetTempPath(), "mphrecomp_gametest_" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "save.json");
            try
            {
                session.SaveTo(path);
                var save2 = SaveManager.Load(path);
                var host2 = new ModHost();
                var mod2 = host2.LoadFromSource(new ModManifest { Id = "m" }, Lua, save2);
                Check("session.SaveTo persisted mod state across a new session",
                    mod2.Data.ContainsKey("ticks") && mod2.Data["ticks"].GetDouble() == ticksNow);
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }

            Console.WriteLine($"\nGAME (integration) TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }
    }
}
