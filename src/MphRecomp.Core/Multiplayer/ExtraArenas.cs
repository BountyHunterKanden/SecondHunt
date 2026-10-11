using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRecomp.Arenas;

namespace MphRecomp.Multiplayer
{
    // The arenas past the retail ones (MatchArenas.All's host rooms: the Echoes arenas, once IMPORT GAMES has them), and who has
    // them (owner 2026-10-10, release test notes N27-N29). Nobody plays an arena their device lacks: a LAN / online host refuses a
    // JOIN for an arena the joiner doesn't have, START waits until every seated player has the arena, RANDOM picks only from the
    // arenas every seated player has, FIND GAME votes only arenas everyone in the group has, and the online list hides games on
    // an arena this device lacks. The ADVANCED box's EXTRA ARENAS rule (ExtraRules.Arenas) puts them in the arena list at all.
    public static class ExtraArenas
    {
        // an arena only some devices have (by room name; an Echoes room counts even where it isn't imported)
        public static bool IsExtra(string? arena)
            => arena != null && (EchoesArena.IsEchoesRoom(arena) || MatchArenas.Find(arena) is { Id: > MatchArenas.LastId });

        private static HashSet<string>? s_owned;
        private static long s_ownedAt;

        // the extra arenas this device can play now (room names; looked up again after a second: the menus ask every frame)
        public static HashSet<string> Owned()
        {
            long now = Environment.TickCount64;
            if (s_owned is HashSet<string> cached && now - s_ownedAt < 1000) return cached;
            HashSet<string> owned;
            try
            {
                owned = MatchArenas.All.Where(r => r.Id > MatchArenas.LastId).Select(r => r.Name).ToHashSet();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                owned = new HashSet<string>();
            }
            s_owned = owned;
            s_ownedAt = now;
            return owned;
        }

        public static bool AnyOwned => Owned().Count > 0;

        public static bool Have(string arena) => !IsExtra(arena) || Owned().Contains(arena);

        // a player with these extra arenas (null = an older device: none) can play this arena
        public static bool Has(IReadOnlyCollection<string>? owned, string arena) => !IsExtra(arena) || owned?.Contains(arena) == true;

        // the arena's name for messages ("spires")
        public static string Label(string arena)
            => (MatchArenas.Find(arena)?.InGameName ?? EchoesArena.Find(arena)?.InGameName ?? arena).ToLowerInvariant();

        // the JOIN message's last field: this device's extra arenas. Optional, so it needs no protocol bump: an older host
        // ignores it, and a JOIN without it (an older joiner) reads as null = no extra arenas
        public static void Write(BinaryWriter w)
        {
            List<string> owned = Owned().OrderBy(a => a, StringComparer.Ordinal).Take(255).ToList();
            w.Write((byte)owned.Count);
            foreach (string a in owned) w.Write(a);
        }

        public static HashSet<string>? Read(BinaryReader r)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) return null;
            int n = r.ReadByte();
            var owned = new HashSet<string>();
            for (int i = 0; i < n; i++) owned.Add(r.ReadString());
            return owned;
        }

        // why a joiner with these arenas can't have this arena, or null
        public static string? Refusal(IReadOnlyCollection<string>? owned, string arena)
            => Has(owned, arena) ? null
            : $"this game is in {Label(arena)}, an arena you don't have. import metroid prime 2: echoes on import games to play it";
    }
}
