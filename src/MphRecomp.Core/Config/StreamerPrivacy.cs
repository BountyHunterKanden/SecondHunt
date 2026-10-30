using System;
using System.Text.RegularExpressions;

namespace MphRecomp.Config
{
    // STREAMING (owner 2026-10-28: "streaming settings where a user can toggle off their ip address, username, and friend
    // code, hiding them anywhere they appear in the game at all so they dont show if they dont want when streaming").
    // Process-wide copies of RecompSettings' ShowAddress / ShowName / ShowFriendCode (set on every settings load and save
    // and by the menu's switches), so any draw site can mask without holding the settings. Only what is drawn changes:
    // the other players, the server and the logs still get the real values. (Not in Online/: the server links that folder.)
    public static class StreamerPrivacy
    {
        public static bool HideAddress { get; private set; }
        public static bool HideName { get; private set; }
        public static bool HideCode { get; private set; }

        // what a hidden value is drawn as (the menus' fonts all have letters, digits, '-' and '#'; HUNTER = the LAN default
        // nickname, so a hidden name looks like an unnamed player)
        public const string NameMask = "HUNTER";
        public const string CodeMask = "####-####-####";
        public const string AddressMask = "hidden";

        // this device's online profile and code (a name is masked by whose seat it is, never by matching the text: a
        // stranger with the same name masked too would give the streamer's name away)
        private static string? s_onlineName, s_code;
        private static int s_onlineTag = -1;

        public static void Apply(RecompSettings s)
        {
            HideAddress = !s.ShowAddress;
            HideName = !s.ShowName;
            HideCode = !s.ShowFriendCode;
            MphRead.GameState.HideLocalName = HideName; // (the match HUD: scoreboard, killfeed)
        }

        // the online profile (OnlineClient.Me) and this device's friend code (OnlineIdentity)
        public static void SetOnline(string? name, int tag, string? code)
        {
            s_onlineName = string.IsNullOrWhiteSpace(name) ? null : name;
            s_onlineTag = tag;
            SetOwnCode(code);
        }
        public static void SetOwnCode(string? code)
        {
            if (!string.IsNullOrEmpty(code)) s_code = code;
        }

        // ---- the draw sites' calls ----

        // an IP address (this device's or another player's: a stream shows either)
        public static string Address(string address) => HideAddress ? AddressMask : address;

        // a friend code as drawn (formatted or not): masked when it's this device's own
        public static string Code(string code) => HideCode && IsOwnCode(code) ? CodeMask : code;
        public static bool IsOwnCode(string code)
            => s_code != null && code.Replace("-", "").Replace(" ", "") == s_code;

        // a player's name as drawn; mine = this device's player
        public static string Name(string name, bool mine) => HideName && mine ? NameMask : name;

        // an online NAME#tag: masked (tag and all) when it's this device's profile
        public static string Display(string name, int tag)
            => HideName && tag == s_onlineTag && Same(name, s_onlineName) ? NameMask : MphRecomp.Online.OnlineNames.Display(name, tag);

        private static bool Same(string a, string? b) => b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

        // free text (status lines, labels, popups): every IPv4 address when addresses are hidden (not the hints' example
        // 999.999.999.999: a number over 255 is no address)
        private static readonly Regex s_ipv4 = new(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?(?![\d.])", RegexOptions.Compiled);
        public static string Scrub(string text)
            => HideAddress && text.Length > 6 ? s_ipv4.Replace(text, m => IsAddress(m.Value) ? AddressMask : m.Value) : text;
        private static bool IsAddress(string s)
        {
            foreach (string part in s.Split(':')[0].Split('.'))
            {
                if (int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) > 255) return false;
            }
            return true;
        }

        // ---- what editing a hidden value says (the owner: hidden values can't be edited; turn the switch back on) ----

        public const string NameLocked = "your name is hidden for streaming.\n\nturn show name back on in settings > game > streaming to edit it.";
        public const string CodeLocked = "your friend code is hidden for streaming, and your backup code with it.\n\nturn show friend code back on in settings > game > streaming to see or enter a backup code.";
        public const string AddressLocked = "ip addresses are hidden for streaming: turn show ip address back on in settings > game > streaming to type one.";
        // First Hunt's popup: four short lines
        public static readonly string[] NameLockedLines = { "Your name is hidden", "for streaming. Turn", "SHOW NAME back on", "to edit it." };
    }
}
