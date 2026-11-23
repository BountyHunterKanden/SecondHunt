using System.Globalization;
using System.Text;
using MphRead;
using MphRead.Entities;
using MphRecomp.Mods;
using MphRecomp.Multiplayer;

namespace MphRecomp.App.Platform;

// The gameplay lines of the one log (docs/LOGS.md): what happened in the match or campaign, one line per GameEvents
// event, under "MPHPlay" with tags ("MPHPlay[pickup,weapon,omega]") so one log can be read by topic: grep "omega",
// "[^]]*weapon", or weapon lines without input. Always on (owner 2026-11-14: one complete log, no switches); not while
// a replay plays back, where the lines would repeat the recorded match. (Scene.Headless is no guard: the app always runs
// the sim headless and draws it itself. Replay prep and the tests never call Start.)
public static class PlayLog
{
    public static Func<bool>? Muted;

    public static void Start()
    {
        GameEvents.LogListener ??= On;
        GameEvents.Any = true; // (LiveMods keeps it on while LogListener is set)
    }

    static void On(string name, object[] a)
    {
        if (name == "tick" || Muted?.Invoke() == true) return;
        switch (name)
        {
            case "game_start":
            {
                string mode = Str(a, 0);
                bool campaign = mode == nameof(GameMode.SinglePlayer);
                Info(campaign ? ["campaign", "room"] : ["match", "arena"],
                    $"{(campaign ? "campaign" : "match")} start: {mode} in {Room(Int(a, 2), Str(a, 1))}, {Int(a, 3)} players{Seats()}");
                break;
            }
            case "match_end": Info(["match"], $"match end: {Str(a, 0)}"); break;
            case "room_enter": Info(["room"], $"room: {Room(Int(a, 1), Str(a, 0))}"); break;
            case "spawn": Info(["spawn"], $"{Who(Int(a, 0))} {(Bool(a, 1) ? "respawned" : "spawned")}"); break;
            case "pickup":
            {
                int slot = Int(a, 0);
                string item = Str(a, 1);
                string? own = item == "AffinityWeapon" ? Affinity(slot) : null; // (each hunter's own weapon, so "magmaul" finds it)
                Info(["pickup", .. ItemTags(item), .. own != null ? Lower(own) : []],
                    $"{Who(slot)} picked up {Words(item)}{(own != null ? $" ({Words(own)})" : "")}");
                break;
            }
            case "weapon_switch":
                if (Str(a, 1) == Str(a, 2)) break; // (a spawn re-equips the same beam)
                Info(["weapon", .. BeamTags(Str(a, 1))], $"{Who(Int(a, 0))} switched to {Words(Str(a, 1))} (from {Words(Str(a, 2))})");
                break;
            case "death":
            {
                int slot = Int(a, 0), by = Int(a, 1);
                string beam = Str(a, 2);
                Info(["death", "kill", "weapon", .. BeamTags(beam)], by < 0 || by == slot
                    ? $"{Who(slot)} died{With(beam)}"
                    : $"{Who(by)} killed {Who(slot)}{With(beam)}");
                break;
            }
            case "damage":
            {
                int slot = Int(a, 0), by = Int(a, 1);
                string beam = Str(a, 3);
                Debug(["damage", "weapon", .. BeamTags(beam)],
                    $"{Who(slot)} took {Int(a, 2)} damage{(by >= 0 ? " from " + Who(by) : "")}{With(beam)}{(Bool(a, 4) ? ", dead" : "")}");
                break;
            }
            case "fire":
            {
                bool charged = Bool(a, 2);
                string beam = Str(a, 1);
                Debug(charged ? ["weapon", "fire", "charged", .. BeamTags(beam)] : ["weapon", "fire", .. BeamTags(beam)],
                    $"{Who(Int(a, 0))} fired {Words(beam)}{(charged ? " (charged)" : "")}");
                break;
            }
            case "bomb": Debug(["weapon", "bomb"], $"{Who(Int(a, 0))} laid a bomb"); break;
            case "alt_form": Debug(["altform"], $"{Who(Int(a, 0))} {(Bool(a, 1) ? "went into alt form" : "left alt form")}"); break;
            case "enemy_killed":
            {
                string type = Str(a, 0);
                Info(["enemy", "kill", .. Lower(type)], $"{Who(Int(a, 2))} killed {Words(type)} (id {Int(a, 1)})");
                break;
            }
            case "scan": Info(["scan"], $"{Who(Int(a, 0))} scanned {Words(Str(a, 2))} (scan {Str(a, 1)})"); break;
            case "door_unlock": Info(["door"], $"door {Int(a, 1)} unlocked in {Room(Int(a, 0))}"); break;
            case "artifact": Info(["artifact", "pickup"], $"artifact {Int(a, 1)} picked up (model {Int(a, 0)})"); break;
            case "octolith_pickup": Info(["octolith"], $"{Who(Int(a, 0))} took the octolith{Team(Int(a, 1))}"); break;
            case "octolith_return": Info(["octolith"], $"octolith returned{Team(Int(a, 1))}{(Int(a, 0) >= 0 ? " by " + Who(Int(a, 0)) : "")}"); break;
            case "octolith_capture": Info(["octolith", "score"], $"{Who(Int(a, 0))} captured the octolith"); break;
        }
    }

    static void Info(string[] tags, string message) => Log.Info("MPHPlay", tags, message);
    static void Debug(string[] tags, string message) => Log.Debug("MPHPlay", tags, message);

    // "P2 TROOPER "name" (you)": the seat, its character as the menus name it, the nickname
    static string Who(int slot)
    {
        if (slot < 0) return "the arena";
        var sb = new StringBuilder("P").Append(slot + 1);
        MatchPlayer? seat = LiveGame.Settings?.Players is { } seats && slot < seats.Count ? seats[slot] : null;
        string? character = null;
        if (seat != null)
        {
            character = (seat.Character != null ? Characters.All.FirstOrDefault(c => c.Id == seat.Character)?.Name : null)
                ?? Characters.Of(seat.Hunter, seat.Skin)?.Name ?? seat.Hunter.ToString().ToUpperInvariant();
        }
        else if (slot < PlayerEntity.Players.Count && PlayerEntity.Players[slot] is { } p)
        {
            character = p.Hunter.ToString().ToUpperInvariant();
        }
        if (character != null) sb.Append(' ').Append(character);
        if (!string.IsNullOrWhiteSpace(seat?.Nickname)) sb.Append(" \"").Append(seat.Nickname).Append('"');
        if (seat?.Bot == true) sb.Append(" (bot)");
        if (slot == PlayerEntity.MainPlayerIndex && seat?.Bot != true) sb.Append(" (you)");
        return sb.ToString();
    }

    static string Seats()
    {
        if (LiveGame.Settings?.Players is not { Count: > 0 } seats) return "";
        var sb = new StringBuilder(": ");
        for (int i = 0; i < seats.Count; i++) sb.Append(i > 0 ? ", " : "").Append(Who(i));
        return sb.ToString();
    }

    // "Alinos Gateway (Gateway Hall, id 27)"
    static string Room(int id, string fallback = "")
    {
        RoomMetadata? room = id >= 0 ? Metadata.GetRoomById(id, noThrow: true) : null;
        if (room == null) return fallback.Length > 0 ? $"{fallback} (id {id})" : $"room {id}";
        return room.InGameName is { Length: > 0 } shown && shown != room.Name ? $"{shown} ({room.Name}, id {id})" : $"{room.Name} (id {id})";
    }

    static string? Affinity(int slot) => slot >= 0 && slot < PlayerEntity.Players.Count && PlayerEntity.Players[slot] is { } p
        ? p.Hunter switch
        {
            Hunter.Kanden => "VoltDriver", Hunter.Trace => "Imperialist", Hunter.Sylux => "ShockCoil", Hunter.Noxus => "Judicator",
            Hunter.Spire => "Magmaul", Hunter.Weavel => "Battlehammer", _ => null,
        }
        : null;

    static string Team(int team) => team == 0 ? " (orange)" : team == 1 ? " (green)" : "";
    static string With(string beam) => beam.Length > 0 ? " with " + Words(beam) : "";

    static string[] BeamTags(string beam) => beam.Length == 0 ? [] : beam == "OmegaCannon" ? ["omega", "omegacannon"] : Lower(beam);

    static string[] ItemTags(string item) => item switch
    {
        "OmegaCannon" => ["weapon", "omega", "omegacannon"],
        "VoltDriver" or "Battlehammer" or "Imperialist" or "Judicator" or "Magmaul" or "ShockCoil" or "PirateBlaster"
            or "HyperBeam" or "Paralyzer" or "AffinityWeapon" or "PickWpnMissile" => ["weapon", item.ToLowerInvariant()],
        _ when item.StartsWith("Health", StringComparison.Ordinal) || item == "EnergyTank" => ["health", item.ToLowerInvariant()],
        _ when item.StartsWith("Missile", StringComparison.Ordinal) || item.StartsWith("UA", StringComparison.Ordinal) => ["ammo", item.ToLowerInvariant()],
        _ => [item.ToLowerInvariant()],
    };

    // "mp2:Grenchler" -> ["mp2", "grenchler"]; "ShockCoil" -> ["shockcoil"]
    static string[] Lower(string s) => s.Length == 0 ? [] : s.ToLowerInvariant().Split(':', StringSplitOptions.RemoveEmptyEntries);

    // "OmegaCannon" -> "Omega Cannon", "mp2:Grenchler" -> "mp2 Grenchler"
    static string Words(string s)
    {
        if (s.Length == 0) return "nothing";
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ':') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(s[i - 1]))))
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string Str(object[] a, int i) => i < a.Length && a[i] != null ? Convert.ToString(a[i], CultureInfo.InvariantCulture) ?? "" : "";
    static int Int(object[] a, int i) => i < a.Length && a[i] is IConvertible c ? c.ToInt32(CultureInfo.InvariantCulture) : -1;
    static bool Bool(object[] a, int i) => i < a.Length && a[i] is bool b && b;
}
