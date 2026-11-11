using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;
using MphRead.Text;

// What the in-ship LOGBOOK shows: the scan log (stringTables/ScanLog) filtered by the save's logbook bits. A bit is a
// SCAN ID = the record's own number (record "L021" = scan 21), which is what scanning and pickups mark; the table skips
// numbers (no L030/L031), so StorySave.CheckLogbook(entry) reads the record's id, not its position. CampaignSaves
// repairs saves made with MphRead's old (position-based) new-game setup.
// Categories by the record's letter: L lore, B bioform, O object, E equipment, upper case only (rev 1 arm9 0x020626B0,
// the logbook page ov0 0x0210BAF4: a lowercase record -- doors, shields, force fields, portals -- is never listed or
// counted, though scanning one sets its bit; folding case made a full file's OBJECT page 100/102 = 98%).
// The list is every record of the category, scanned or not, sorted by name: 0x0210BAF4 copies each name into a 0x1C-byte
// row (name +0, scan id +0x18, scanned flag +0x1A) and qsorts the rows with strcmp (0x020A3F08, comparator 0x0210BCBC ->
// 0x020A5550). The page draws an unscanned row as "-" (0x02109AB0, string 0x02146A88) and a touch opens only a scanned
// row (0x0210AE78); with nothing scanned (0%) it shows NO ENTRIES instead of rows.
// LOGBOOK SCANS: ALL (owner 2026-10-31, off by default) also lists the scans vanilla leaves out on the OBJECT page:
// 'o' (doors, shields, force fields, portals) and the switch/console messages 'X' / 'x' (scan visor category 4, which
// has no page of its own). The scan already sets their bits; the percentages stay vanilla (Percent never counts them).
namespace MphRecomp.Frontend
{
    public enum LogCategory { Lore, Bioform, Object, Equipment }

    public sealed class LogEntry
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";
        public bool Scanned { get; init; }
    }

    public static class ShipLogbook
    {
        private static char Letter(LogCategory c) => c switch
        {
            LogCategory.Lore => 'L',
            LogCategory.Bioform => 'B',
            LogCategory.Object => 'O',
            _ => 'E'
        };

        private static bool Is(StringTableEntry entry, LogCategory c) => entry.Category == Letter(c);

        private static bool Extra(StringTableEntry entry, LogCategory c) =>
            c == LogCategory.Object && entry.Category is 'o' or 'X' or 'x';

        public static List<LogEntry> Entries(StorySave save, LogCategory c, bool allScans = false)
        {
            IReadOnlyList<StringTableEntry> log = Strings.ReadStringTable(StringTables.ScanLog);
            var list = new List<LogEntry>();
            for (int i = 0; i < log.Count; i++)
            {
                if (Is(log[i], c) || allScans && Extra(log[i], c))
                {
                    list.Add(new LogEntry { Index = i, Name = log[i].Value1, Text = log[i].Value2, Scanned = save.CheckLogbook(log[i]) });
                }
            }
            // strcmp order = ordinal (one char per byte; vanilla's names are all upper case, so ignoring case changes
            // nothing there and files the extra scans' few lower case names in place); the table order breaks ties (two
            // GOREA bioforms, the extra scans' many SWITCHes)
            list.Sort((a, b) =>
            {
                int order = String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (order == 0) order = String.CompareOrdinal(a.Name, b.Name);
                return order != 0 ? order : a.Index - b.Index;
            });
            return list;
        }

        // scanned share of the category's records, rounded down (0-100)
        public static int Percent(StorySave save, params LogCategory[] categories)
        {
            IReadOnlyList<StringTableEntry> log = Strings.ReadStringTable(StringTables.ScanLog);
            int total = 0, found = 0;
            for (int i = 0; i < log.Count; i++)
            {
                if (Array.Exists(categories, c => Is(log[i], c)))
                {
                    total++;
                    if (save.CheckLogbook(log[i])) found++;
                }
            }
            return total == 0 ? 0 : found * 100 / total;
        }
    }
}
