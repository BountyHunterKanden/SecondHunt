using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Campaign
{
    // The menus' arena backdrop (LOOK > MENUS "menu background" ARENA, owner 2026-11-20): a multiplayer arena with no
    // players in a headless scene of its own, its room, pickups and animated parts running, seen from a fixed camera
    // (MenuBackdropShots). The App's CampaignRenderer draws it under the front end's menus with no sound (its host
    // seams are left unset while it steps). One MphRead scene at a time: it is disposed before a match or campaign starts.
    public sealed partial class CampaignHost
    {
        public bool IsMenuBackdrop { get; private set; }
        public static string LoadTimes = ""; // (where a backdrop's load time goes, logged by the App)

        static System.Threading.Tasks.Task<CampaignHost>? _menuBackdropLoad;

        // The backdrop's scene built on a worker (owner 11-21: "everything is just sitting waiting to be handed off"): a
        // headless scene makes no GL calls (Scene.Headless: textures decoded into HostTextures, meshes registered), so
        // the whole room load runs off the GL thread from the menus' first frame, the startup screens or not. MphRead is
        // one scene at a time: the App keeps the menus off MphRead while it runs (they only show the startup and title
        // pages meanwhile) and anything else that builds a scene waits for it first (WaitMenuBackdrop). Silent: the
        // room's music and sound seams are unset while it loads (the menus drive their own audio, not through them).
        public static System.Threading.Tasks.Task<CampaignHost> BeginMenuBackdrop(string arena, int viewWidth, int viewHeight)
        {
            WaitMenuBackdrop();
            long begun = System.Diagnostics.Stopwatch.GetTimestamp();
            // holiday mode's sky map and ground snow made beside the room load, on another worker (HolidaySnow.PrepareEarly):
            // the worker's Prepare only takes them
            MphRecomp.Holiday.HolidaySnow.PrepareEarly(MphRead.Metadata.GetRoomByName(arena).Item1);
            var load = System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                _workerWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(begun).TotalMilliseconds;
                var (music, roomMusic) = (MusicPlayer.Host, MphRead.Music.RoomHost);
                MusicPlayer.Host = null;
                MphRead.Music.RoomHost = null;
                try { return StartMenuBackdrop(arena, viewWidth, viewHeight); }
                finally
                {
                    MusicPlayer.Host = music;
                    MphRead.Music.RoomHost = roomMusic;
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
                System.Threading.Tasks.TaskScheduler.Default);
            _menuBackdropLoad = load;
            return load;
        }

        // a backdrop scene being built finishes first (a match, campaign or other scene about to start; the menus leaving
        // the title). Its result stays with whoever started it
        public static void WaitMenuBackdrop()
        {
            var load = _menuBackdropLoad;
            if (load == null) return;
            try { load.Wait(); }
            catch (AggregateException) { } // (its owner sees the failure)
            if (_menuBackdropLoad == load) _menuBackdropLoad = null;
        }

        public static bool MenuBackdropLoading => _menuBackdropLoad is { IsCompleted: false };

        static double _workerWaitMs; // BeginMenuBackdrop: how long its worker took to start (LoadTimes)

        public static CampaignHost StartMenuBackdrop(string arena, int viewWidth, int viewHeight)
        {
            var setup = System.Diagnostics.Stopwatch.StartNew();
            PlayerEntity.HostFirstHunt = false;
            PlayerEntity.HostNoLocalSeat = false;
            PlayerEntity.HostHoldMainSpawn = false;
            Scene.Headless = true;
            Scene.CollectDrawItems = true;
            MphRead.Music.Init(); // the room's music setup needs the tables (Start); nothing plays: no host is set
            Menu.SaveSlot = 0;
            PlayerEntity.MaxPlayers = PlayerEntity.VanillaMaxPlayers; // (a match may have set 8: P8)
            // a match's arena extras stay out of the picture: no crates, breakables, turret, Echoes moves, UA or placed pickups
            MphRecomp.Arenas.EchoesCrates.Enabled = false;
            MphRecomp.Arenas.EchoesBreakables.Enabled = false;
            MphRecomp.Arenas.EchoesBreakables.Mirror = false;
            MphRecomp.Arenas.EchoesTurret.Enabled = false;
            MphRecomp.Arenas.EchoesTurret.Mirror = false;
            MphRecomp.Arenas.EchoesMovement.Enabled = false;
            MphRecomp.Arenas.EchoesAmmo.Enabled = false;
            MphRecomp.Arenas.PlacedPickups.Enabled = false;
            MphRecomp.Arenas.PlacedPickups.CratesEnabled = false;
            MphRecomp.Arenas.PlacedPickups.BaseEnabled = false;
            long beforeScene = setup.ElapsedMilliseconds;
            var scene = new Scene(new Vector2i(viewWidth, viewHeight), null!, null!, _ => { }, () => { });
            var host = new CampaignHost(scene) { IsMenuBackdrop = true };
            long madeScene = setup.ElapsedMilliseconds;
            // no seats: the room's four-player layer (its pickups, jump pads and lights), the camera roams
            var sw = System.Diagnostics.Stopwatch.StartNew();
            SceneSetup.HostNoHunterResources = true; // nobody plays: no hunter models (most of the load)
            try { scene.AddRoom(arena, GameMode.Battle, playerCount: 4); }
            finally { SceneSetup.HostNoHunterResources = false; }
            long room = sw.ElapsedMilliseconds;
            scene.OnLoad();
            long loaded = sw.ElapsedMilliseconds;
            // holiday mode's sky map and ground snow here on the worker, not in the menus' first frame (HolidaySnow.Prepare)
            MphRecomp.Holiday.HolidaySnow.Prepare(scene);
            // its snow cover's vertices decoded on another worker meanwhile: the menus' first draw only uploads them
            MphRecomp.Holiday.HolidayGround.DecodeCoverAhead(scene);
            double wait = _workerWaitMs;
            _workerWaitMs = 0;
            LoadTimes = $"start {wait:0} ms, setup {beforeScene} ms, new scene {madeScene - beforeScene} ms, room {room} ms, scene {loaded - room} ms, holiday snow {sw.ElapsedMilliseconds - loaded} ms";
            return host;
        }

        // one 60 Hz step of the room alone (no input, no players)
        public void StepBackdrop() => Scene.OnUpdateFrame();

        // the arena's player spawn points (position, facing), in entity order: the backdrop's fallback cameras
        public List<(Vector3 Position, Vector3 Facing)> SpawnPoints()
        {
            var spawns = new List<(Vector3, Vector3)>();
            foreach (EntityBase entity in Scene.Entities)
            {
                if (entity is PlayerSpawnEntity spawn)
                {
                    Vector3 facing = spawn.FacingVector;
                    if (spawn.FacingFlipped) facing = -facing;
                    spawns.Add((spawn.Position, facing));
                }
            }
            return spawns;
        }
    }
}
