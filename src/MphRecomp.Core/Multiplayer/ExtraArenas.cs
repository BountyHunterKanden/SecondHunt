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
    public static partial class ExtraArenas
    {
        // an arena only some devices have (by room name; an Echoes room counts even where it isn't imported)
        public static bool IsExtra(string? arena)
            => arena != null && (EchoesArena.IsEchoesRoom(arena) || FirstHuntArena.IsFirstHuntRoom(arena) || MatchArenas.Find(arena) is { Id: > MatchArenas.LastId });

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
                owned = MatchArenas.All.Where(r => r.Id > MatchArenas.LastId && !FirstHuntArena.IsDevRoom(r.Name)).Select(r => r.Name).ToHashSet();
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

        // Retail arenas that became playable after some builds shipped (2026-10-21 hotfix: Stasis Bunker, room 119, table
        // name "E3 FIRST HUNT"; MatchArenas stopped at 118 before it). LAN only compares the sim version, so an older joiner
        // can sit in a newer host's lobby, and a match there fails on the older device. A newer device names these in JOIN's
        // extra-arenas field (Write; no wire change: an older host ignores names it doesn't know), and a host takes a joiner
        // for one, lets RANDOM pick it and starts in it only when the joiner named it, as for an imported arena (Has, Refusal)
        private static readonly string[] LateNames = { "E3 FIRST HUNT" };

        public static bool IsLate(string? arena) => arena != null && Array.IndexOf(LateNames, arena) >= 0;

        // a player with these extra arenas (null = an older device: none) can play this arena (a late one too: above)
        public static bool Has(IReadOnlyCollection<string>? owned, string arena) => !IsExtra(arena) && !IsLate(arena) || owned?.Contains(arena) == true;

        // the arena's name for messages ("spires")
        public static string Label(string arena)
            => (MatchArenas.Find(arena)?.InGameName ?? EchoesArena.Find(arena)?.InGameName ?? FirstHuntArena.Find(arena)?.InGameName ?? arena).ToLowerInvariant();

        // the JOIN message's last field: this device's extra arenas. Optional, so it needs no protocol bump: an older host
        // ignores it, and a JOIN without it (an older joiner) reads as null = no extra arenas
        public static void Write(BinaryWriter w)
        {
            List<string> owned = LateNames.Concat(Owned().OrderBy(a => a, StringComparer.Ordinal)).Take(255).ToList(); // (late ones first: never cut)
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
            : IsLate(arena) ? $"this game is in {Label(arena)}, which your version of the game can't play. update the game to play it"
            : $"this game is in {Label(arena)}, an arena you don't have. import {(FirstHuntArena.IsFirstHuntRoom(arena) ? "metroid prime hunters: first hunt" : "metroid prime 2: echoes")} on import games to play it";
    }
}
