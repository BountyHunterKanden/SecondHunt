using System;
using System.Reflection;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;

namespace MphRecomp.Mods
{
    // What a mod can read and change of the running game (docs/MOD_API.md §4.4): ModHost's score and match tables go
    // through this. LiveMods hands mods the game's own (LiveGame); the tests hand them a fake. Reading is open to every
    // mod; changing (score.set/add, match.finish) is for gameplay mods only, which run offline and are replayed.
    public interface IModGame
    {
        bool InMatch { get; }
        int Slots { get; }
        string Mode { get; }
        // the mod mode being played as "<mod id>:<mode id>" (mod.json Modes), null = none
        string? ModMode { get; }
        bool Teams { get; }
        int Points(int slot);
        void SetPoints(int slot, int points);
        int TeamPoints(int team);
        void SetTeamPoints(int team, int points);
        int Kills(int slot);
        int Deaths(int slot);
        float TimeLeft { get; }
        // the match's goals (0 = none) and time left (0 ends the match): match.set_point_goal / set_time_goal / set_time_left
        int PointGoal { get; set; }
        float TimeGoal { get; set; }
        void SetTimeLeft(float seconds);
        // mode.vanilla: the mode's own rules for this frame, inside a mode.tick only and at most once a frame
        void VanillaRules();
        // bot.set_objective: where a bot heads when it would wander to a random spot (null = wander as usual)
        (float X, float Y, float Z)? BotObjective(int slot);
        void SetBotObjective(int slot, (float X, float Y, float Z)? at);
        // a MatchSettings value by property name (Arena, PointGoal, TimeLimitSeconds, FriendlyFire...), null = not set
        object? Setting(string name);
        // a seat's hunter / team / bot, null = no such seat
        (string Hunter, int Team, bool Bot)? Player(int slot);
        void Finish();
    }

    // the game's IModGame: MphRead's GameState, and the settings of the match being played (a replay's recorded ones)
    public sealed class LiveGame : IModGame
    {
        public static readonly LiveGame Instance = new();
        public static MatchSettings? Settings { get; set; }

        public bool InMatch => GameState.Multiplayer;
        public int Slots => PlayerEntity.SlotCapacity;
        public string Mode => GameState.Mode.ToString();
        // set by LiveMods.StartMatch for the match it starts
        public static string? CurrentModMode { get; set; }
        public string? ModMode => GameState.Multiplayer ? CurrentModMode : null;
        public bool Teams => GameState.Teams;
        static bool Ok(int i) => i >= 0 && i < PlayerEntity.SlotCapacity;
        public int Points(int slot) => Ok(slot) ? GameState.Points[slot] : 0;
        public void SetPoints(int slot, int points) { if (Ok(slot)) GameState.Points[slot] = points; }
        public int TeamPoints(int team) => Ok(team) ? GameState.TeamPoints[team] : 0;
        public void SetTeamPoints(int team, int points) { if (Ok(team)) GameState.TeamPoints[team] = points; }
        public int Kills(int slot) => Ok(slot) ? GameState.Kills[slot] : 0;
        public int Deaths(int slot) => Ok(slot) ? GameState.Deaths[slot] : 0;
        public float TimeLeft => GameState.MatchTime;
        public void Finish() { if (GameState.Multiplayer) GameState.ForceEndGame = true; }
        public int PointGoal { get => GameState.PointGoal; set { if (GameState.Multiplayer) GameState.PointGoal = Math.Max(value, 0); } }
        public float TimeGoal { get => GameState.TimeGoal; set { if (GameState.Multiplayer) GameState.TimeGoal = Math.Max(value, 0); } }
        public void SetTimeLeft(float seconds) { if (GameState.Multiplayer) GameState.MatchTime = Math.Max(seconds, 0); }

        // mode.tick (GameState.HostModeTick): the gameplay mods' mode_tick hooks run in place of the mode's rules; mode.vanilla
        // runs those once. If every such mod has stopped (an error), the mode's own rules come back that same frame.
        static Scene? _tickScene;
        static bool _vanillaRan;

        public void VanillaRules()
        {
            if (_tickScene == null || _vanillaRan) return;
            _vanillaRan = true;
            GameState.ModeState(_tickScene);
        }

        public static void ModeTick(Scene scene, ModHost host)
        {
            if (!GameState.Multiplayer || !host.Wants(ModHost.ModeTickEvent))
            {
                GameState.ModeState(scene);
                return;
            }
            _tickScene = scene;
            _vanillaRan = false;
            try { host.Fire(ModHost.ModeTickEvent, (double)scene.FrameTime); }
            finally { _tickScene = null; }
            if (!_vanillaRan && !host.Wants(ModHost.ModeTickEvent)) GameState.ModeState(scene);
        }

        // bot.set_objective (PlayerEntity.HostBotObjective, set only while a seat has one): the nav node closest to it
        // replaces a bot's random wander target. Cleared when the mods detach and as each match starts
        static readonly (float X, float Y, float Z)?[] _objectives = new (float X, float Y, float Z)?[PlayerEntity.SlotCapacity];

        public (float X, float Y, float Z)? BotObjective(int slot) => Ok(slot) ? _objectives[slot] : null;

        public void SetBotObjective(int slot, (float X, float Y, float Z)? at)
        {
            if (!Ok(slot) || !GameState.Multiplayer) return;
            _objectives[slot] = at;
            PlayerEntity.HostBotObjective = Array.Exists(_objectives, o => o.HasValue) ? ObjectiveOf : null;
        }

        static OpenTK.Mathematics.Vector3? ObjectiveOf(int slot)
            => Ok(slot) && _objectives[slot] is { } o ? new OpenTK.Mathematics.Vector3(o.X, o.Y, o.Z) : null;

        public static void ClearObjectives()
        {
            Array.Clear(_objectives);
            PlayerEntity.HostBotObjective = null;
        }

        public object? Setting(string name) => SettingOf(Settings, name);

        public (string Hunter, int Team, bool Bot)? Player(int slot)
        {
            if (Settings == null || slot < 0 || slot >= Settings.Players.Count) return null;
            MatchPlayer p = Settings.Players[slot];
            return (p.Character ?? p.Hunter.ToString(), p.Team, p.Bot);
        }

        // a plain value of a MatchSettings property (bool, number, string, enum as its name); lists and objects aren't
        // offered, and nothing about the network (IsClient, ServedHost...) is either: only the rules
        public static object? SettingOf(MatchSettings? settings, string name)
        {
            if (settings == null || string.IsNullOrEmpty(name)) return null;
            PropertyInfo? prop = typeof(MatchSettings).GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || prop.GetIndexParameters().Length > 0 || Array.IndexOf(Hidden, prop.Name) >= 0) return null;
            Type t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object? v = prop.GetValue(settings);
            if (v == null) return null;
            if (t.IsEnum) return v.ToString();
            if (t == typeof(bool) || t == typeof(string)) return v;
            if (t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long)) return Convert.ToDouble(v);
            return null;
        }

        static readonly string[] Hidden = { "IsClient", "ServedHost", "FruitySync", "LocalSeat" };
    }
}
