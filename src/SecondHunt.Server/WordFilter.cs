using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SecondHunt.Server
{
    // The word filter for names and typed chat (docs/ONLINE_PLAY.md section 8, decision 14): the LDNOOBW English list
    // (CC BY 4.0, credited in THIRD_PARTY_NOTICES.md; wordlists/ldnoobw_en.txt, deployed as words.txt) plus the owner's own
    // additions file (words_owner.txt beside it). Both are plain text, one entry per line, and are re-read within half a minute
    // of being edited, so the owner changes them on the server without a build or a restart.
    //
    // Owner file lines:   word            also blocked
    //                     *word           blocked inside other words too ("*fuck" catches "motherfucker")
    //                     -word           allowed again (an entry of the LDNOOBW list that is wrong for a shooter, e.g. "-hell")
    //                     # comment
    // Matching is by word, so "class" and "Scunthorpe" stay clean; a word counts with a plural / -ing / -er ending, with common
    // digit-for-letter swaps (0 o, 1 i or l, 3 e, 4 @ a, 5 $ s, 7 t, ! i, | i), stretched ("fuuuck") or spaced out ("f u c k").
    // A few words that no innocent word contains (fuck, shit, bitch, whore, pussy, slut, faggot) and every entry of 7+ letters are
    // also caught inside longer words. Mask turns a match into asterisks; names with a match are refused (Allowed).
    public static class WordFilter
    {
        private sealed class Lists
        {
            public readonly HashSet<string> Words = new();
            public readonly List<string[]> Phrases = new();
            public readonly HashSet<string> Inside = new();
            public readonly List<string> Literals = new();
        }

        private static readonly string[] BuiltInInside = { "fuck", "shit", "bitch", "whore", "pussy", "slut", "faggot" };
        private static readonly string[] Endings = { "s", "es", "ed", "er", "ers", "ing", "in", "y", "ie", "ies", "z" };
        private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(20);

        private static volatile Lists _lists = new();
        private static string? _listPath, _ownerPath;
        private static DateTime _stamp = DateTime.MinValue;
        private static DateTime _checked = DateTime.MinValue;
        private static readonly object ReloadLock = new();

        // listPath = the LDNOOBW list (none = no list), ownerPath = the owner's additions (none = beside the list)
        public static void Load(string? listPath, string? ownerPath = null)
        {
            lock (ReloadLock)
            {
                _listPath = listPath;
                _ownerPath = ownerPath ?? (listPath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(listPath)) ?? ".", "words_owner.txt"));
                Reload();
            }
        }

        // tests: exactly these entries (same syntax as the files)
        public static void Set(IEnumerable<string> entries)
        {
            lock (ReloadLock)
            {
                _listPath = _ownerPath = null;
                _lists = Build(entries, Array.Empty<string>());
            }
        }

        // how many entries are loaded (tests, the admin tool)
        public static int Count => _lists.Words.Count + _lists.Phrases.Count + _lists.Literals.Count;

        private static void Reload()
        {
            var main = ReadLines(_listPath);
            var owner = ReadLines(_ownerPath);
            _lists = Build(main, owner);
            _stamp = Stamp();
            _checked = DateTime.UtcNow;
        }

        private static DateTime Stamp()
        {
            DateTime a = _listPath != null && File.Exists(_listPath) ? File.GetLastWriteTimeUtc(_listPath) : DateTime.MinValue;
            DateTime b = _ownerPath != null && File.Exists(_ownerPath) ? File.GetLastWriteTimeUtc(_ownerPath) : DateTime.MinValue;
            return a > b ? a : b;
        }

        private static string[] ReadLines(string? path)
        {
            try
            {
                return path != null && File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }

        private static Lists Current()
        {
            if (_listPath != null && DateTime.UtcNow - _checked > Recheck)
            {
                lock (ReloadLock)
                {
                    if (DateTime.UtcNow - _checked > Recheck)
                    {
                        _checked = DateTime.UtcNow;
                        if (Stamp() != _stamp)
                        {
                            Reload();
                        }
                    }
                }
            }
            return _lists;
        }

        private static Lists Build(IEnumerable<string> main, IEnumerable<string> owner)
        {
            var lists = new Lists();
            var removed = new HashSet<string>();
            var ownerLines = owner.Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').ToList();
            foreach (string line in ownerLines.Where(l => l[0] == '-'))
            {
                removed.Add(String.Join(" ", Words(line[1..])));
            }
            foreach (string raw in main.Concat(ownerLines.Where(l => l[0] != '-')))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                bool inside = line[0] == '*';
                if (inside)
                {
                    line = line[1..];
                }
                if (removed.Contains(String.Join(" ", Words(line))))
                {
                    continue;
                }
                string[] parts = Words(line);
                if (parts.Length == 0)
                {
                    if (line.Length > 0 && !line.Any(Char.IsAsciiLetterOrDigit))
                    {
                        lists.Literals.Add(line.ToLowerInvariant()); // a symbol or emoji
                    }
                    continue;
                }
                if (parts.Length > 1)
                {
                    lists.Phrases.Add(parts);
                    continue;
                }
                if (parts[0].Length < 3)
                {
                    continue;
                }
                lists.Words.Add(parts[0]);
                if (inside || parts[0].Length >= 7)
                {
                    lists.Inside.Add(parts[0]);
                }
            }
            foreach (string w in BuiltInInside)
            {
                if (!removed.Contains(w))
                {
                    lists.Words.Add(w);
                    lists.Inside.Add(w);
                }
            }
            return lists;
        }

        // a list line as its normalized words
        private static string[] Words(string line)
            => line.ToLowerInvariant().Split(new[] { ' ', '\t', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => Normalize(w, oneIsL: false)).Where(w => w.Length > 0).ToArray();

        public static bool Allowed(string text) => !Matches(text, null);

        // the text with every match turned into asterisks (same length)
        public static string Mask(string text, out bool changed)
        {
            var hit = new bool[text.Length];
            changed = Matches(text, hit);
            if (!changed)
            {
                return text;
            }
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (hit[i])
                {
                    chars[i] = '*';
                }
            }
            return new string(chars);
        }

        public static string Mask(string text) => Mask(text, out _);

        private struct Token
        {
            public int Start, Length;
            public string Text;
        }

        private static bool IsTokenChar(char c) => Char.IsAsciiLetterOrDigit(c) || c is '!' or '@' or '$' or '|';

        private static bool Matches(string text, bool[]? hit)
        {
            Lists lists = Current();
            if (lists.Words.Count + lists.Phrases.Count + lists.Literals.Count == 0)
            {
                return false;
            }
            bool any = false;
            string lower = text.ToLowerInvariant();
            foreach (string lit in lists.Literals)
            {
                for (int at = lower.IndexOf(lit, StringComparison.Ordinal); at >= 0; at = lower.IndexOf(lit, at + lit.Length, StringComparison.Ordinal))
                {
                    any = true;
                    Mark(hit, at, lit.Length);
                }
            }
            var tokens = new List<Token>();
            for (int i = 0; i < text.Length;)
            {
                if (!IsTokenChar(text[i]))
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < text.Length && IsTokenChar(text[i]))
                {
                    i++;
                }
                tokens.Add(new Token { Start = start, Length = i - start, Text = text.Substring(start, i - start) });
            }
            // each token on its own
            for (int t = 0; t < tokens.Count; t++)
            {
                if (TokenHits(lists, tokens[t].Text))
                {
                    any = true;
                    Mark(hit, tokens[t].Start, tokens[t].Length);
                }
            }
            // runs of single letters spelling a word ("f u c k")
            for (int t = 0; t < tokens.Count;)
            {
                int u = t;
                var sb = new StringBuilder();
                while (u < tokens.Count && tokens[u].Length == 1)
                {
                    sb.Append(tokens[u].Text);
                    u++;
                }
                if (u - t >= 3 && TokenHits(lists, sb.ToString()))
                {
                    any = true;
                    for (int k = t; k < u; k++)
                    {
                        Mark(hit, tokens[k].Start, 1);
                    }
                }
                t = Math.Max(u, t + 1);
            }
            // phrases ("two girls one cup"): consecutive tokens
            foreach (string[] phrase in lists.Phrases)
            {
                for (int t = 0; t + phrase.Length <= tokens.Count; t++)
                {
                    bool all = true;
                    for (int k = 0; k < phrase.Length && all; k++)
                    {
                        all = Normalize(tokens[t + k].Text, false) == phrase[k] || Normalize(tokens[t + k].Text, true) == phrase[k];
                    }
                    if (all)
                    {
                        any = true;
                        for (int k = 0; k < phrase.Length; k++)
                        {
                            Mark(hit, tokens[t + k].Start, tokens[t + k].Length);
                        }
                    }
                }
            }
            return any;
        }

        private static void Mark(bool[]? hit, int start, int length)
        {
            if (hit != null)
            {
                for (int i = start; i < start + length; i++)
                {
                    hit[i] = true;
                }
            }
        }

        private static bool TokenHits(Lists lists, string token)
        {
            foreach (bool oneIsL in token.Contains('1') ? new[] { false, true } : new[] { false })
            {
                string norm = Normalize(token, oneIsL);
                if (norm.Length < 3)
                {
                    continue;
                }
                foreach (string variant in Variants(norm))
                {
                    if (WordHit(lists, variant))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // the word as typed, with long runs of one letter cut to two, and to one ("fuuuck")
        private static IEnumerable<string> Variants(string norm)
        {
            yield return norm;
            string two = Collapse(norm, 2);
            if (two != norm)
            {
                yield return two;
            }
            string one = Collapse(norm, 1);
            if (one != norm && one != two)
            {
                yield return one;
            }
        }

        private static string Collapse(string s, int keep)
        {
            var sb = new StringBuilder();
            int run = 0;
            for (int i = 0; i < s.Length; i++)
            {
                run = i > 0 && s[i] == s[i - 1] ? run + 1 : 1;
                if (keep == 1 ? run == 1 : run <= (run >= 3 ? 2 : 2))
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

        private static bool WordHit(Lists lists, string word)
        {
            if (lists.Words.Contains(word))
            {
                return true;
            }
            foreach (string end in Endings)
            {
                if (word.Length > end.Length + 2 && word.EndsWith(end, StringComparison.Ordinal)
                    && lists.Words.Contains(word[..^end.Length]))
                {
                    return true;
                }
            }
            foreach (string inside in lists.Inside)
            {
                if (word.Contains(inside, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // lower case; digit and symbol look-alikes to letters; letters only
        private static string Normalize(string text, bool oneIsL)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char raw in text.ToLowerInvariant())
            {
                char c = raw switch
                {
                    '0' => 'o',
                    '1' => oneIsL ? 'l' : 'i',
                    '!' or '|' => 'i',
                    '3' => 'e',
                    '4' or '@' => 'a',
                    '5' or '$' => 's',
                    '7' => 't',
                    _ => raw,
                };
                if (Char.IsAsciiLetter(c))
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
