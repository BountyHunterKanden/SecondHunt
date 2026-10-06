using System.Text;

namespace MphRecomp.App;

// First launch: nothing of the game ships with the app, so before the front end can draw anything the player picks
// their own ROM and it is unpacked into the app's own storage (MphRead's Extract). The hosts' setup screens (Android:
// RomSetupActivity; Windows: RomSetupScreen) show the file picker and these steps' messages.
internal static class RomSetup
{
    // the marker a finished unpack leaves in filesDir (the hosts go straight to the game when it's there)
    public static string MarkerPath(string filesDir) => Path.Combine(filesDir, ".extracted");

    public static bool IsExtracted(string filesDir) => File.Exists(MarkerPath(filesDir));

    // Beta 1 supports Metroid Prime Hunters USA (AMHE), rev 0 (1.0) and rev 1 (1.1): the only ROMs tested end to end
    // (public beta audit 5.3, board S28). The header's game code (0x0C) and version (0x1E) decide; anything else gets a
    // plain message instead of a half-working game.
    public static string? Unsupported(string romPath)
    {
        byte[] head = new byte[0x20];
        using (var f = File.OpenRead(romPath))
        {
            if (f.Read(head, 0, head.Length) < head.Length) return "That file is too small to be a DS ROM.";
        }
        string code = Encoding.ASCII.GetString(head, 0x0C, 4);
        byte version = head[0x1E];
        if (code == "AMHE" && version <= 1) return null;
        string what = code switch
        {
            "AMHE" => $"Metroid Prime Hunters (USA) with an unknown revision ({version})",
            "AMHP" => "Metroid Prime Hunters (Europe)",
            "AMHJ" => "Metroid Prime Hunters (Japan)",
            "AMHK" => "Metroid Prime Hunters (Korea)",
            "A76E" => "Metroid Prime Hunters (USA kiosk demo)",
            "AMFE" or "AMFP" => "Metroid Prime Hunters: First Hunt",
            _ => $"not Metroid Prime Hunters (game code {(code.All(c => c >= 0x20 && c < 0x7F) ? code : "?")})",
        };
        return $"This file is {what}.\n\nThis beta supports Metroid Prime Hunters (USA), version 1.0 or 1.1 (game code "
            + "AMHE). Other regions aren't supported yet. Please pick a USA copy of the game.";
    }

    // Unpacks the ROM into filesDir (MphRead's working directory), reporting MphRead's own progress lines; true when
    // done (the marker is written). Runs on a worker thread.
    public static bool Extract(string romPath, string filesDir, Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(filesDir);
            Directory.SetCurrentDirectory(filesDir);
            TextWriter prev = Console.Out;
            Console.SetOut(new ForwardWriter(log));
            try
            {
                log("Extracting…");
                MphRead.Extract.Setup(romPath);
            }
            finally
            {
                Console.SetOut(prev);
            }
            string tree = Path.Combine(filesDir, "files");
            int count = Directory.Exists(tree) ? Directory.GetFiles(tree, "*", SearchOption.AllDirectories).Length : 0;
            if (count == 0)
            {
                log("Nothing was extracted -- is this a Metroid Prime Hunters ROM?");
                return false;
            }
            string root = Directory.GetDirectories(tree) is { Length: > 0 } dirs ? Path.GetFileName(dirs[0]) : "unknown";
            File.WriteAllText(MarkerPath(filesDir), $"version={MphRead.AppInfo.Version}\nrom={root}\nfiles={count}");
            log($"Done: {root}, {count} files.");
            return true;
        }
        catch (Exception ex)
        {
            log("Extraction failed: " + ex.Message);
            return false;
        }
    }

    sealed class ForwardWriter : TextWriter
    {
        readonly Action<string> _sink;
        readonly StringBuilder _line = new();
        public ForwardWriter(Action<string> sink) => _sink = sink;
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char c)
        {
            if (c == '\n')
            {
                _sink(_line.ToString());
                _line.Clear();
            }
            else if (c != '\r')
            {
                _line.Append(c);
            }
        }

        public override void Write(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (char c in s) Write(c);
        }
    }
}
