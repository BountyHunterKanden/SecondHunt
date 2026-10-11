using System;
using System.Linq;

namespace MphRecomp.Online
{
    // Online names (docs/ONLINE_PLAY.md section 4): the Hunter License name (vanilla rules: printable characters, at most
    // 10, as typed) plus a 4-digit tag the server picks, shown NAME#0421, so names needn't be unique and can be searched.
    public static class OnlineNames
    {
        public const int MaxName = 10;
        public const int MaxTag = 9999;
        public const string Default = "HUNTER";

        // the same rule as HunterLicense.Clean, without its default (the server applies Default)
        public static string Clean(string? name)
        {
            string s = new string((name ?? "").Where(c => c >= ' ' && c <= '~').ToArray()).Trim();
            if (s.Length > MaxName)
            {
                s = s[..MaxName].TrimEnd();
            }
            return s;
        }

        // what search and tag uniqueness compare: case-insensitive
        public static string Key(string name) => Clean(name).ToUpperInvariant();

        public static string Tag(int tag) => tag.ToString("D4");

        public static string Display(string name, int tag) => $"{name}#{Tag(tag)}";

        // "kanden" -> (kanden, null); "kanden#421" / "KANDEN#0421" -> (kanden, 421)
        public static (string Name, int? Tag) ParseQuery(string? query)
        {
            string q = query ?? "";
            int hash = q.LastIndexOf('#');
            if (hash >= 0 && Int32.TryParse(q[(hash + 1)..].Trim(), out int tag) && tag >= 1 && tag <= MaxTag)
            {
                return (Clean(q[..hash]), tag);
            }
            return (Clean(q), null);
        }
    }
}
