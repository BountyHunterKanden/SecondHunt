using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using System.Linq;
using System.Text;

namespace MphRecomp.App;

// First launch only: nothing of the game ships with the app, so before the front end can draw anything the player
// picks their own ROM and it is extracted into app-private storage (MphRead's Extract). Then straight into the game.
// A ROM pushed to <external files>/rom.nds (development) is picked up without asking.
[Activity(Name = "com.mphrecomp.app.RomSetupActivity",
    ScreenOrientation = Android.Content.PM.ScreenOrientation.SensorLandscape)]
public class RomSetupActivity : Activity
{
    const int PickRomRequest = 1001;
    TextView _log = null!;
    Button _select = null!;
    readonly StringBuilder _sb = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(48, 48, 48, 24);
        var title = new TextView(this) { Text = ApplicationInfo?.LoadLabel(PackageManager!) ?? "", TextSize = 24f };
        root.AddView(title);
        var info = new TextView(this)
        {
            TextSize = 14f,
            Text = "Select your own Metroid Prime Hunters (USA) ROM, version 1.0 or 1.1. It is read once and unpacked into this app's private storage; " +
                   "nothing is bundled with the app or downloaded."
        };
        info.SetPadding(0, 16, 0, 24);
        root.AddView(info);
        _select = new Button(this) { Text = "Select ROM…" };
        _select.Click += (_, _) => PickRom();
        root.AddView(_select);
        var scroll = new ScrollView(this);
        _log = new TextView(this) { TextSize = 12f };
        _log.SetPadding(0, 16, 0, 8);
        scroll.AddView(_log);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(root);

        string devRom = System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "rom.nds");
        if (System.IO.File.Exists(devRom))
        {
            _select.Enabled = false;
            Log($"Found {devRom}");
            if (Unsupported(devRom) is string why)
            {
                Log(why);
                _select.Enabled = true;
            }
            else System.Threading.Tasks.Task.Run(() => Extract(devRom));
        }
    }

    void PickRom()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        StartActivityForResult(Intent.CreateChooser(intent, "Select ROM"), PickRomRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickRomRequest || resultCode != Result.Ok || data?.Data == null) return;
        Android.Net.Uri uri = data.Data;
        _select.Enabled = false;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string dest = System.IO.Path.Combine(FilesDir!.AbsolutePath, "rom.nds");
                Log("Copying ROM…");
                using (var input = ContentResolver!.OpenInputStream(uri)!)
                using (var output = System.IO.File.Create(dest))
                    input.CopyTo(output);
                if (Unsupported(dest) is string why)
                {
                    System.IO.File.Delete(dest);
                    Log(why);
                    RunOnUiThread(() => _select.Enabled = true);
                    return;
                }
                Extract(dest);
            }
            catch (Exception ex)
            {
                Log("Could not read the ROM: " + ex.Message);
                RunOnUiThread(() => _select.Enabled = true);
            }
        });
    }

    // Beta 1 supports Metroid Prime Hunters USA (AMHE), rev 0 (1.0) and rev 1 (1.1): the only ROMs tested end to end
    // (public beta audit 5.3, board S28). The header's game code (0x0C) and version (0x1E) decide; anything else gets a
    // plain message instead of a half-working game.
    static string? Unsupported(string romPath)
    {
        byte[] head = new byte[0x20];
        using (var f = System.IO.File.OpenRead(romPath))
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

    void Extract(string romPath)
    {
        try
        {
            string files = FilesDir!.AbsolutePath;
            System.IO.Directory.SetCurrentDirectory(files);
            var prev = Console.Out;
            Console.SetOut(new ForwardWriter(Log));
            try { Log("Extracting…"); MphRead.Extract.Setup(romPath); }
            finally { Console.SetOut(prev); }
            string tree = System.IO.Path.Combine(files, "files");
            int count = System.IO.Directory.Exists(tree)
                ? System.IO.Directory.GetFiles(tree, "*", System.IO.SearchOption.AllDirectories).Length : 0;
            if (count == 0)
            {
                Log("Nothing was extracted -- is this a Metroid Prime Hunters ROM?");
                RunOnUiThread(() => _select.Enabled = true);
                return;
            }
            string root = System.IO.Directory.GetDirectories(tree) is { Length: > 0 } dirs ? System.IO.Path.GetFileName(dirs[0]) : "unknown";
            System.IO.File.WriteAllText(System.IO.Path.Combine(files, ".extracted"),
                $"version={MphRead.AppInfo.Version}\nrom={root}\nfiles={count}");
            Log($"Done: {root}, {count} files.");
            RunOnUiThread(() =>
            {
                StartActivity(new Intent(this, typeof(GameActivity)));
                Finish();
            });
        }
        catch (Exception ex)
        {
            Log("Extraction failed: " + ex.Message);
            RunOnUiThread(() => _select.Enabled = true);
        }
    }

    void Log(string msg)
    {
        lock (_sb) _sb.AppendLine(msg);
        Android.Util.Log.Info("MPHExtract", msg);
        RunOnUiThread(() => { lock (_sb) _log.Text = _sb.ToString(); });
    }

    sealed class ForwardWriter : System.IO.TextWriter
    {
        readonly Action<string> _sink;
        readonly StringBuilder _line = new();
        public ForwardWriter(Action<string> sink) => _sink = sink;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char c) { if (c == '\n') { _sink(_line.ToString()); _line.Clear(); } else if (c != '\r') _line.Append(c); }
        public override void Write(string? s) { if (!string.IsNullOrEmpty(s)) foreach (char c in s!) Write(c); }
    }
}
