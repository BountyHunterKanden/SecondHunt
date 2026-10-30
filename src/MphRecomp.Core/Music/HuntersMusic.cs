using System;
using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Formats.Sound;

namespace MphRecomp.PrimeMusic
{
    // Metroid Prime Hunters' own songs in the song lists (owner 2026-10-29: "I want them added yes", docs/PRIME_MUSIC.md):
    // played from the player's own ROM by MphRead's sequence player, so nothing to import. Ids "mph:<Name>"; the names,
    // areas and labels are in MusicCatalog with the Prime songs'. The songs are the ones MphRead's sound test names
    // (Menu.MusicList), less the mode and weapon variants of a song (a VS. theme's octolith and node tracks, Gorea's
    // weapon tracks), the short jingles and the title screen's stream (21 s, doesn't loop).
    public static class HuntersMusic
    {
        public const string Kind = "mph";

        // (name, a MusicId, or a SeqId when seqOnly)
        static readonly (string Name, int Id, bool SeqOnly)[] s_songs =
        {
            ("MPHShadows", (int)MusicId.SEQ_GREY_M17, false),
            ("MPHTheArchives", (int)MusicId.SEQ_YELLOW_M1, false),
            ("MPHSpaceDecay", (int)MusicId.SEQ_INDIGO_M59, false),
            ("MPHEnemies", (int)MusicId.SEQ_ENEMY_1_M28, false),
            ("MPHPursuit", (int)MusicId.SEQ_INTRO_KANDEN_M30, false),
            ("MPHShipCelestial", (int)SeqId.SHIP_LAND2, true),
            ("MPHAlinos", (int)MusicId.SEQ_RED_M13, false),
            ("MPHTheElders", (int)MusicId.SEQ_RED_M60, false),
            ("MPHMagmaDrop", (int)MusicId.SEQ_BRINSTAR_M67, false),
            ("MPHShipAlinos", (int)SeqId.SHIP_LAND1, true),
            ("MPHTheOutpost", (int)MusicId.SEQ_GREEN_M19, false),
            ("MPHWatching", (int)MusicId.SEQ_BLACK_M53, false),
            ("MPHShipVdo", (int)SeqId.SHIP_LAND3, true),
            ("MPHDesolation", (int)MusicId.SEQ_WHITE_M48, false),
            ("MPHArcterra", (int)MusicId.SEQ_BLUE_M14, false),
            ("MPHDesolationMaze", (int)MusicId.SEQ_WHITE_M54, false),
            ("MPHShipArcterra", (int)SeqId.SHIP_LAND4, true),
            ("MPHForeboding", (int)MusicId.SEQ_AMBIENT_1_M2, false),
            ("MPHGorea", (int)MusicId.SEQ_GOREA_1_M20, false),
            ("MPHOubliette", (int)MusicId.SEQ_GOREA_2_M34, false),
            ("MPHKanden", (int)MusicId.SEQ_GUMBO_M3, false),
            ("MPHSpire", (int)MusicId.SEQ_GUMBO_M37, false),
            ("MPHWeavel", (int)MusicId.SEQ_GUMBO_M10, false),
            ("MPHSylux", (int)MusicId.SEQ_GUMBO_M39, false),
            ("MPHNoxus", (int)MusicId.SEQ_GUMBO_M7, false),
            ("MPHTrace", (int)MusicId.SEQ_GUMBO_M41, false),
            ("MPHGuardians", (int)MusicId.SEQ_GUARDIAN_M18, false),
            ("MPHCretaphid", (int)MusicId.SEQ_GARLIC_M4, false),
            ("MPHSlench", (int)MusicId.SEQ_SAFFRON_M29, false),
            ("MPHDemonSpawn", (int)MusicId.SEQ_PARASITE_M16, false),
            ("MPHAftermath", (int)MusicId.SEQ_TELEPORT_M5, false),
            ("MPHEscape", (int)MusicId.SEQ_OREGANO_M55, false),
            ("MPHCelestialVs", (int)MusicId.SEQ_MP2_M15, false),
            ("MPHAlinosVs", (int)MusicId.SEQ_MP1_M12, false),
            ("MPHVdoVs", (int)MusicId.SEQ_DILL_M33, false),
            ("MPHArcterraVs", (int)MusicId.SEQ_PEPPER_M36, false),
            ("MPHIntro", (int)SeqId.DRONE, true),
            ("MPHMenu", (int)SeqId.CHUTNEY, true),
            ("MPHResults", (int)SeqId.RESULTS, true),
            ("MPHStory", (int)SeqId.NEW_GAME, true),
            ("MPHTetraGalaxy", (int)SeqId.SHIP, true),
            ("MPHCredits", (int)MusicId.SEQ_CREDITS_M65, false),
            ("MPHMenuUnused", (int)SeqId.MENU1, true),
        };

        static readonly Lazy<IReadOnlyList<Track>> s_all = new(() => s_songs.Select(s => new Track
        {
            Name = s.Name, Game = Kind, Source = Kind, Seq = s.Id, SeqOnly = s.SeqOnly, Format = "seq",
            Title = MusicCatalog.Listed(s.Name) ? MusicCatalog.Label(s.Name) : s.Name,
        }).ToList());

        public static IReadOnlyList<Track> All => s_all.Value;

        public static Track? Find(string name) => All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        static IReadOnlyList<MusicTrack>? s_info;

        // the sequence and tracks a song plays (a MusicId's from the ROM's INTERMUSIC table, as MphRead's PlayMusic does)
        public static (SeqId Seq, ushort Tracks) Tune(Track t)
        {
            if (t.SeqOnly) return ((SeqId)t.Seq, 0xFFFF);
            s_info ??= SoundRead.ReadInterMusicInfo();
            return t.Seq >= 0 && t.Seq < s_info.Count ? (s_info[t.Seq].SeqId, s_info[t.Seq].Tracks) : (SeqId.None, (ushort)0);
        }

        // in a match: through MphRead's own music, so the last-minute tempo-up and the fades are vanilla's
        public static void PlayInGame(Track t)
        {
            if (t.SeqOnly) MphRead.Music.PlaySeq((SeqId)t.Seq, 0xFFFF, queue: true, notReady: true);
            else MphRead.Music.PlayMusic((MusicId)t.Seq);
        }
    }
}
