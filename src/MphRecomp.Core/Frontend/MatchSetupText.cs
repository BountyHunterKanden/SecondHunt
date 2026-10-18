using System;
using System.Collections.Generic;
using System.Globalization;
using MphRead;
using MphRecomp.Multiplayer;

// The match the vanilla multiplayer menu (MultiplayerMenu) sets up, as one line of text: the platform shells pass it to
// the match screen as a launch argument ("setup"), so it travels the same way through an Android intent and a Windows
// argument list, and survives the shell's own activity/screen change. Keys: mode, arena, local (seat), pg (point goal),
// tl (time limit s), tg (time goal s), dmg (0-2), ff, or (octolith reset), rad (radar), aff (affinity weapons), xp (EXTRA
// PICKUPS: MatchSettings.PirateBlaster), xc (EXTRA CHARACTERS), then one
// p=hunter,recolor,team,bot,remote,level,nickname[,skin[,character]] per seat in order (skin: MatchPlayer.Skin,
// character: MatchPlayer.Character, each left out when none; a character without a skin writes an empty skin). A rule
// left out keeps the mode's own default.
// net=host|client: a LAN match (M3): the match screen plays LanSession.Current (its Settings are the match; the line's
// own seats only say who this device is), launched with --es net and --es menu 1 instead of --es setup.
namespace MphRecomp.Frontend
{
    public static class MatchSetupText
    {
        public const string ArgName = "setup";

        // The match screen's launch arguments for a setup line (FrontendRequestKind.StartMatch's Arg): the line itself,
        // the local seat's hunter (the match screen picks its gun from it before it reads the setup) and MPH's own gun,
        // as every match plays today (MatchLobby.Gun).
        public static Dictionary<string, string> LaunchArgs(string setup)
        {
            MatchSettings s = Read(setup);
            string? net = NetRole(setup);
            var args = new Dictionary<string, string>
            {
                ["hunter"] = s.Players[s.LocalSeat].Hunter.ToString(),
                ["gun"] = "original",
            };
            if (net != null)
            {
                args["net"] = net;
                args["menu"] = "1";
            }
            else
            {
                args[ArgName] = setup;
            }
            foreach (string part in setup.Split(';'))
            {
                if (part == TryPart || part == TryActivePart) args["try"] = part[4..]; // TRY CONTROLS (App SoloArena.cs)
            }
            return args;
        }

        // OPTIONS > CONTROLS' TRY CONTROLS (RecompMenus.ZoomAim.cs): the line's extra part (Read skips it) that launches the
        // match as a practice run (App SoloArena.cs, --es try 1: idle bots as targets; try 2: bots that fight back)
        public const string TryPart = "try=1", TryActivePart = "try=2";

        // "host" / "client" for a LAN match's line, else null
        public static string? NetRole(string setup)
        {
            foreach (string part in setup.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("net=", StringComparison.Ordinal)) return part[4..];
            }
            return null;
        }

        public static string Write(MatchSettings s, string? net = null)
        {
            // numbers always invariant: Read parses them invariant, and e.g. Finnish writes -1 with U+2212 (10-16 crash)
            CultureInfo ci = CultureInfo.InvariantCulture;
            var parts = new List<string> { "mode=" + s.Mode, "arena=" + Uri.EscapeDataString(s.Arena), "local=" + s.LocalSeat.ToString(ci) };
            if (net != null) parts.Add("net=" + net);
            void Add(string key, object? value)
            {
                if (value == null) return;
                parts.Add(key + "=" + value switch
                {
                    bool b => b ? "1" : "0",
                    float f => f.ToString(CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture),
                });
            }
            Add("pg", s.PointGoal);
            Add("tl", s.TimeLimitSeconds);
            Add("tg", s.TimeGoalSeconds);
            Add("dmg", s.DamageLevel);
            Add("ff", s.FriendlyFire);
            Add("or", s.OctolithReset);
            Add("rad", s.RadarPlayers);
            Add("aff", s.AffinityWeapons);
            Add("xp", s.PirateBlaster);
            Add("xc", s.ExtraCharacters);
            foreach (MatchPlayer p in s.Players)
            {
                parts.Add(string.Create(ci, $"p={p.Hunter},{p.Recolor},{p.Team},{(p.Bot ? 1 : 0)},{(p.Remote ? 1 : 0)},{p.BotLevel},")
                    + Uri.EscapeDataString(p.Nickname ?? "")
                    + (string.IsNullOrEmpty(p.Skin) && string.IsNullOrEmpty(p.Character) ? "" : "," + Uri.EscapeDataString(p.Skin ?? ""))
                    + (string.IsNullOrEmpty(p.Character) ? "" : "," + Uri.EscapeDataString(p.Character)));
            }
            return string.Join(";", parts);
        }

        // Throws (FormatException / ArgumentException) on text that isn't a setup line.
        public static MatchSettings Read(string text)
        {
            var s = new MatchSettings();
            foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) throw new FormatException($"match setup: bad part '{part}'");
                string key = part[..eq], value = part[(eq + 1)..];
                int Int() => Int32.Parse(value, CultureInfo.InvariantCulture);
                float Float() => Single.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                bool Bool() => value == "1";
                switch (key)
                {
                case "mode": s.Mode = Enum.Parse<GameMode>(value); break;
                case "arena": s.Arena = Uri.UnescapeDataString(value); break;
                case "local": s.LocalSeat = Int(); break;
                case "pg": s.PointGoal = Int(); break;
                case "tl": s.TimeLimitSeconds = Float(); break;
                case "tg": s.TimeGoalSeconds = Float(); break;
                case "dmg": s.DamageLevel = Int(); break;
                case "ff": s.FriendlyFire = Bool(); break;
                case "or": s.OctolithReset = Bool(); break;
                case "rad": s.RadarPlayers = Bool(); break;
                case "aff": s.AffinityWeapons = Bool(); break;
                case "xp": s.PirateBlaster = Bool(); break;
                case "xc": s.ExtraCharacters = Bool(); break;
                case "net": break; // NetRole
                case "p":
                {
                    string[] f = value.Split(',');
                    if (f.Length is not (7 or 8 or 9)) throw new FormatException($"match setup: bad seat '{value}'");
                    s.Players.Add(new MatchPlayer
                    {
                        Hunter = Enum.Parse<Hunter>(f[0]),
                        Recolor = Int32.Parse(f[1], CultureInfo.InvariantCulture),
                        Team = Int32.Parse(f[2], CultureInfo.InvariantCulture),
                        Bot = f[3] == "1",
                        Remote = f[4] == "1",
                        BotLevel = Int32.Parse(f[5], CultureInfo.InvariantCulture),
                        Nickname = f[6].Length == 0 ? null : Uri.UnescapeDataString(f[6]),
                        Skin = f.Length > 7 && f[7].Length > 0 ? Uri.UnescapeDataString(f[7]) : null,
                        Character = f.Length > 8 && f[8].Length > 0 ? Uri.UnescapeDataString(f[8]) : null,
                    });
                    break;
                }
                }
            }
            return s;
        }
    }
}
