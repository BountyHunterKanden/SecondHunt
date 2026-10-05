using System;
using System.Collections.Generic;
using MphRead;
using MphRead.Formats;
using MphRead.Text;

// What the in-ship LOGBOOK shows: the scan log (stringTables/ScanLog) filtered by the save's logbook bits. A bit is a
// SCAN ID = the record's own number (record "L021" = scan 21), which is what scanning and pickups mark; the table skips
// numbers (no L030/L031), so StorySave.CheckLogbook(entry) reads the record's id, not its position. CampaignSaves
// repairs saves made with MphRead's old (position-based) new-game setup.
// Categories by the record's letter: L lore, B bioform, O object, E equipment (either case).
namespace MphRecomp.Frontend
{
    public enum LogCategory { Lore, Bioform, Object, Equipment }

    public sealed class LogEntry
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";
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

        private static bool Is(StringTableEntry entry, LogCategory c) => Char.ToUpperInvariant(entry.Category) == Letter(c);

        public static List<LogEntry> Entries(StorySave save, LogCategory c)
        {
            IReadOnlyList<StringTableEntry> log = Strings.ReadStringTable(StringTables.ScanLog);
            var list = new List<LogEntry>();
            for (int i = 0; i < log.Count; i++)
            {
                if (Is(log[i], c) && save.CheckLogbook(log[i]))
                {
                    list.Add(new LogEntry { Index = i, Name = log[i].Value1, Text = log[i].Value2 });
                }
            }
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
