using System.Text;

namespace MphRecomp.Mods
{
    // A LuaLS ---@meta file for the mod API (docs/MOD_API.md §4.15): written next to the mods as mod_api.lua so an editor
    // with the Lua language server autocompletes mod.*, the event names and each filter's fields. `-modmeta` prints it.
    public static class ModApiMeta
    {
        // (event, its arguments as LuaLS params) - keep in step with the GameEvents.Raise sites in MphRead
        static readonly (string Name, string Args)[] Events =
        {
            ("start", ""),
            ("setting_changed", "mod_id: string, setting: string, value: any"),
            ("tick", "frame_time: number, frame_count: integer"),
            ("game_start", "mode: string, room: string, room_id: integer, players: integer"),
            ("match_end", "mode: string"),
            ("room_enter", "room: string, room_id: integer"),
            ("spawn", "player: integer, respawn: boolean"),
            ("damage", "target: integer, attacker: integer, damage: integer, beam: string, dead: boolean"),
            ("death", "player: integer, attacker: integer, beam: string"),
            ("fire", "player: integer, beam: string, charged: boolean"),
            ("weapon_switch", "player: integer, beam: string, previous: string"),
            ("alt_form", "player: integer, alt_form: boolean"),
            ("bomb", "player: integer"),
            ("pickup", "player: integer, item: string"),
            ("scan", "player: integer, scan_id: integer, entity: string"),
            ("door_unlock", "room_id: integer, door_id: integer"),
            ("enemy_killed", "enemy: string, id: integer, killer: integer"),
            ("artifact", "model_id: integer, artifact_id: integer"),
            ("octolith_pickup", "player: integer, team: integer, bounty: boolean"),
            ("octolith_return", "player: integer, team: integer"),
            ("octolith_capture", "player: integer, bounty: boolean"),
        };

        // (filter, its fields; the ones a filter may change are marked) - keep in step with MphRead/GameFilters.cs
        static readonly (string Name, string Fields)[] Filters =
        {
            ("damage", "target integer, attacker integer, damage integer (change), beam string, headshot boolean"),
            ("kill", "victim integer, attacker integer, suicide boolean, beam string, headshot boolean, vanilla_points integer, points integer (change: added on top), scorer integer (change)"),
            ("respawn_time", "player integer, attacker integer, frames integer (change, 60 per second)"),
            ("spawn_point", "player integer, index integer (change), count integer"),
            ("pickup", "player integer, item string, allow boolean (change)"),
            ("player_values", "player integer, hunter string, and every PlayerValues field by name, e.g. JumpSpeed / WalkSpeedCap (change; fixed point, 4096 = 1.0)"),
            ("enemy_damage", "enemy string, attacker integer, damage integer (change)"),
            ("enemy_spawn", "enemy string, room integer, health integer (change: 1..65535, max health follows)"),
        };

        public static string Generate()
        {
            var b = new StringBuilder();
            b.Append("---@meta\n");
            b.Append("-- MPH Recomp mod API ").Append(ModHost.ApiVersion).Append(" (generated; docs/MODDING.md). Don't edit: the app rewrites it.\n\n");
            b.Append("---@alias ModEvent\n");
            foreach (var (name, _) in Events) b.Append("---| '\"").Append(name).Append("\"'\n");
            b.Append("\n---@alias ModFilter\n");
            foreach (var (name, fields) in Filters) b.Append("---| '\"").Append(name).Append("\"' # ").Append(fields).Append('\n');
            b.Append("\n---@class mod\n");
            b.Append("---@field id string\n---@field name string\n---@field version string\n");
            b.Append("---@field kind '\"cosmetic\"'|'\"gameplay\"'\n---@field api integer\n");
            b.Append("mod = {}\n\n");
            b.Append("---Write a line to mod_log.txt (and the MODS page on an error).\n---@param text any\nfunction mod.log(text) end\n\n");
            b.Append("---Run fn when the event happens. Events and their arguments:\n");
            foreach (var (name, args) in Events) b.Append("--- * ").Append(name).Append('(').Append(args).Append(")\n");
            b.Append("---@param event ModEvent\n---@param fn function\nfunction mod.on(event, fn) end\n\n");
            b.Append("---Gameplay mods only: change a value the game is about to use. fn(t) edits t's fields. Filters:\n");
            foreach (var (name, fields) in Filters) b.Append("--- * ").Append(name).Append(": ").Append(fields).Append('\n');
            b.Append("---@param name ModFilter\n---@param fn fun(t: table)\nfunction mod.filter(name, fn) end\n\n");
            b.Append("---Keep a value in mod_data.json (JSON-able; 1 MB per mod).\n---@param key string\n---@param value any\nfunction mod.save(key, value) end\n\n");
            b.Append("---Read a value kept with mod.save.\n---@param key string\n---@param default? any\n---@return any\nfunction mod.load(key, default) end\n\n");
            b.Append("mod.campaign = {}\n\n");
            b.Append("---Read a value kept in the loaded campaign save file (default when none is loaded).\n---@param key string\n---@param default? any\n---@return any\nfunction mod.campaign.get(key, default) end\n\n");
            b.Append("---Gameplay mods: keep a value in the loaded campaign save file (written at the next ship save; 64 KB per mod). False when no campaign file is loaded.\n---@param key string\n---@param value any\n---@return boolean\nfunction mod.campaign.set(key, value) end\n\n");
            b.Append("---A tuning value by dotted path, e.g. mod.config('Camera.FovDegrees', 60). Cosmetic mods can also read 'Settings.<name>'.\n---@param path string\n---@param default? any\n---@return any\nfunction mod.config(path, default) end\n\n");
            b.Append("---An asset's text through the mod file system, or nil.\n---@param path string\n---@return string?\nfunction mod.asset(path) end\n\n");
            b.Append("---Gameplay mods only: spawn a model at a position; returns its id.\n---@param model string\n---@param x number\n---@param y number\n---@param z number\n---@return integer?\nfunction mod.spawn(model, x, y, z) end\n\n");
            b.Append("---The player's value for one of this mod's settings (boolean, number or string by the setting's type).\n---@param id string\n---@return any\nfunction mod.setting(id) end\n\n");
            b.Append("---A random number from the mod's own stream (same forms as math.random). Recorded with replays for gameplay mods.\n---@param m? integer\n---@param n? integer\n---@return number\nfunction mod.random(m, n) end\n");
            b.Append("\n---Match scores. Any mod reads; set/add/team_set/team_add are gameplay mods only. Outside a match reads give 0.\n---@class score\nscore = {}\n");
            b.Append("---@param slot integer\n---@return integer\nfunction score.get(slot) end\n");
            b.Append("---@param slot integer\n---@param points integer\nfunction score.set(slot, points) end\n");
            b.Append("---@param slot integer\n---@param points integer\nfunction score.add(slot, points) end\n");
            b.Append("---@param team integer\n---@return integer\nfunction score.team_get(team) end\n");
            b.Append("---@param team integer\n---@param points integer\nfunction score.team_set(team, points) end\n");
            b.Append("---@param team integer\n---@param points integer\nfunction score.team_add(team, points) end\n");
            b.Append("---@param slot integer\n---@return integer\nfunction score.kills(slot) end\n");
            b.Append("---@param slot integer\n---@return integer\nfunction score.deaths(slot) end\n");
            b.Append("\n---The match being played. Any mod reads; finish() is gameplay mods only.\n---@class match\nmatch = {}\n");
            b.Append("---@return boolean\nfunction match.active() end\n");
            b.Append("---@return string? mode e.g. \"Battle\", \"Capture\"\nfunction match.mode() end\n");
            b.Append("---@return boolean\nfunction match.teams() end\n");
            b.Append("---@return number\nfunction match.time_left() end\n");
            b.Append("---A match rule by name (Arena, PointGoal, TimeLimitSeconds, FriendlyFire...), nil if unknown.\n---@param name string\n---@return any\nfunction match.setting(name) end\n");
            b.Append("---@param slot integer\n---@return {hunter: string, team: integer, bot: boolean}?\nfunction match.player(slot) end\n");
            b.Append("---End the match now; results rank by the scores as they stand.\nfunction match.finish() end\n");
            b.Append("---The point goal (0 = none).\n---@return number\nfunction match.point_goal() end\n");
            b.Append("---The time goal in seconds (Survival lives, Defender / Prime Hunter time; 0 = none).\n---@return number\nfunction match.time_goal() end\n");
            b.Append("---Gameplay mods: set the point goal (0 = none).\n---@param n number\nfunction match.set_point_goal(n) end\n");
            b.Append("---Gameplay mods: set the time goal in seconds.\n---@param seconds number\nfunction match.set_time_goal(seconds) end\n");
            b.Append("---Gameplay mods: set the time left in seconds (0 ends the match).\n---@param seconds number\nfunction match.set_time_left(seconds) end\n");
            b.Append("\n---The mode's rules (gameplay mods only).\n---@class mode\nmode = {}\n");
            b.Append("---Run fn once as the match starts, after its own rules are set (change goals and time here).\n---@param fn fun()\nfunction mode.setup(fn) end\n");
            b.Append("---Run fn(dt) every frame in place of the mode's rules (win checks etc.); the time limit still ends the match.\n---@param fn fun(dt: number)\nfunction mode.tick(fn) end\n");
            b.Append("---Inside mode.tick: run the mode's own rules for this frame (at most once a frame).\nfunction mode.vanilla() end\n");
            b.Append("---Any mod: the mod mode this match plays (mod.json Modes) and the mod that adds it; nil in a plain mode.\n---@return string? mode_id\n---@return string? mod_id\nfunction mode.current() end\n");
            b.Append("\n---Bot objectives.\n---@class bot\nbot = {}\n");
            b.Append("---Gameplay mods: the bot in slot heads for x, y, z when it would otherwise wander; just slot clears it.\n---@param slot integer\n---@param x? number\n---@param y? number\n---@param z? number\nfunction bot.set_objective(slot, x, y, z) end\n");
            b.Append("---The bot's objective, or nil.\n---@param slot integer\n---@return number?, number?, number?\nfunction bot.objective(slot) end\n");
            b.Append("\n---Rooms as they load. Gameplay mods only.\n---@class room\nroom = {}\n");
            b.Append("---@class room_loading\n---@field id integer\n---@field name string\n---@field add_pickup fun(item: string, x: number, y: number, z: number, respawn?: number) an ItemType name, e.g. \"MissileBig\"; respawn in seconds\n---@field add_enemy fun(id: string, x: number, y: number, z: number, facing?: number) e.g. \"mph:Zoomer\", \"fh:Metroid\"; campaign rooms only\n");
            b.Append("---Run fn as each room loads (up to 64 adds per room).\n---@param fn fun(r: room_loading)\nfunction room.on_load(fn) end\n");
            b.Append("\n---Enemies in the running room.\n---@class entity\nentity = {}\n");
            b.Append("---@class entity_info\n---@field kind string\n---@field type string\n---@field x number\n---@field y number\n---@field z number\n---@field health integer\n---@field max_health integer\n");
            b.Append("---Handles of the enemies alive now.\n---@return integer[]\nfunction entity.enemies() end\n");
            b.Append("---An enemy's type, place and health, nil once it's gone.\n---@param h integer\n---@return entity_info?\nfunction entity.get(h) end\n");
            b.Append("---Gameplay mods: spawn an enemy at the end of this frame (campaign rooms only).\n---@param id string e.g. \"mph:Zoomer\"\n---@param x number\n---@param y number\n---@param z number\n---@param facing? number degrees\nfunction entity.spawn_enemy(id, x, y, z, facing) end\n");
            b.Append("---Gameplay mods: remove an enemy at the end of this frame (not bosses or hunters).\n---@param h integer\nfunction entity.remove(h) end\n");
            b.Append("\n---Watch a level-scripting message by name (\"Unlock\") or by the entity id sending or receiving it.\n---@param what string|integer\n---@param fn fun(name: string, from: integer, to: integer, p1: integer?, p2: integer?)\nfunction mod.on_message(what, fn) end\n");
            b.Append("---Gameplay mods: send level-scripting messages (end of the frame, up to 16 a frame).\n---@class msg\nmsg = {}\n");
            b.Append("---@param name string e.g. \"Unlock\"\n---@param from? integer sender entity id\n---@param to? integer target entity id\n---@param p1? integer\n---@param p2? integer\n---@param delay? integer frames, 0-600\nfunction msg.send(name, from, to, p1, p2, delay) end\n");
            return b.ToString();
        }
    }
}
