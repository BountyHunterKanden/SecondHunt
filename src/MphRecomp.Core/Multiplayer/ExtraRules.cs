using System;
using System.IO;
using System.Text.Json;

namespace MphRecomp.Multiplayer
{
    // The ADVANCED box's two recomp rules (owner 2026-10-10): EXTRA PICKUPS (the Pirate Blaster: MatchSettings.PirateBlaster)
    // and EXTRA CHARACTERS (TROOPER, DARK SAMUS: MatchSettings.ExtraCharacters). On by default, and unlike vanilla's rules
    // the only ones kept between matches and app closes (extra_rules.json beside the Hunter License), so a host who wants
    // vanilla matches turns them off once. Characters need pickups (the Trooper plays with his own blaster or not at all):
    // turning pickups off turns characters off, turning characters on turns pickups on.
    // EXTRA ARENAS (owner 2026-10-10, release test notes N28): the imported arenas (Echoes: ExtraArenas) in the setup's arena
    // list and RANDOM's picks; shown only on a device that has some. On by default as the other two; on its own (no forcing).
    public sealed class ExtraRules
    {
        public const string FileName = "extra_rules.json";

        public bool Pickups { get; set; } = true;
        public bool Characters { get; set; } = true;
        public bool Arenas { get; set; } = true;

        public void SetPickups(bool on)
        {
            Pickups = on;
            if (!on) Characters = false;
        }

        public void SetCharacters(bool on)
        {
            Characters = on;
            if (on) Pickups = true;
        }

        // the rules on a match being set up (Characters.ApplyExtraRules then strips banned characters)
        public void ApplyTo(MatchSettings s)
        {
            s.PirateBlaster = Pickups;
            s.ExtraCharacters = Characters && Pickups;
        }

        public static ExtraRules Load(string? dir)
        {
            try
            {
                string? path = dir == null ? null : Path.Combine(dir, FileName);
                if (path != null && File.Exists(path))
                {
                    ExtraRules rules = JsonSerializer.Deserialize<ExtraRules>(File.ReadAllText(path)) ?? new();
                    rules.Characters &= rules.Pickups;
                    return rules;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Console.Error.WriteLine($"multiplayer: {FileName} not read: {ex.Message}");
            }
            return new();
        }

        public void Save(string? dir)
        {
            if (dir == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Console.Error.WriteLine($"multiplayer: {FileName} not saved: {ex.Message}");
            }
        }
    }
}
