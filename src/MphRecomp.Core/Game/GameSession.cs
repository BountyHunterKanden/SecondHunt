using System.Numerics;
using MphRecomp.Assets;
using MphRecomp.Config;
using MphRecomp.Input;
using MphRecomp.Mods;
using MphRecomp.Save;
using MphRecomp.Sim;

namespace MphRecomp.Game
{
    // Composes every mph-recomp system into one running game: config-driven tuning, a versioned
    // save, the sandboxed Lua mod host, the fixed-timestep sim loop + world, and the input map.
    // The presentation layer (Android app) creates one, feeds it raw input + real dt per frame,
    // and renders the returned snapshot. This is the seam where the static viewer becomes a game:
    //   input -> sim ticks (+ mod hooks) -> interpolated render snapshot.
    // Everything here is engine/render-free -> headlessly testable.
    public sealed class GameSession
    {
        public GameConfig Config { get; }
        public SaveData Save { get; }
        public ModHost Mods { get; }
        public SimWorld World { get; }
        public GameLoop Loop { get; }
        public InputMap Input { get; }
        public PlayerController? Player { get; private set; }

        private readonly RenderSnapshot _snapshot = new();

        public AssetVfs? Assets { get; }

        public GameSession(GameConfig? config = null, SaveData? save = null, ModHost? mods = null,
            AssetVfs? assets = null, double tickRate = 60.0)
        {
            Config = config ?? ConfigManager.Default();
            Save = save ?? new SaveData();
            Mods = mods ?? new ModHost();
            Assets = assets;
            World = new SimWorld();
            Loop = new GameLoop(tickRate);
            Input = new InputMap();
            // give mods access to tuning, assets, and the world (enables mod.config/asset/spawn)
            Mods.Config = Config;
            Mods.Assets = Assets;
            Mods.World = World;
        }

        // Call once after mods have been loaded into Mods: hydrate their persisted data and notify.
        public void Start()
        {
            Mods.LoadData(Save);
            Mods.Fire("start");
        }

        // Enter a room: set the world's room, spawn the first-person player at the given spot, and
        // notify mods. Collision comes from the presentation (renderer) or null for free movement.
        public PlayerController EnterRoom(string roomName, ICollision? collision = null,
            Vector3 spawnPos = default, float spawnYaw = 0f)
        {
            World.RoomName = roomName;
            Player = World.Spawn(new PlayerController(collision)
            {
                Position = spawnPos,
                Yaw = spawnYaw,
                LookSpeed = Config.Camera.LookSpeed
            });
            Mods.Fire("room_enter", roomName);
            return Player;
        }

        // Advance one presentation frame: map input, run the fixed sim ticks that fit (each also
        // firing the mod "tick" event), then fill + return the interpolated render snapshot.
        // Reuses one snapshot -> zero per-frame allocation.
        public RenderSnapshot Update(double realDeltaSeconds, RawInput raw)
        {
            InputState input = Input.Resolve(raw);
            Loop.Advance(realDeltaSeconds, dt =>
            {
                World.Tick(dt, input);
                Mods.Fire("tick", (double)dt);
            });
            World.Snapshot(Loop.Alpha, _snapshot);
            return _snapshot;
        }

        // Persist: push each mod's data into the save, then write the save to disk.
        public void SaveTo(string path)
        {
            Mods.SaveDataTo(Save);
            SaveManager.Save(Save, path);
        }
    }
}
