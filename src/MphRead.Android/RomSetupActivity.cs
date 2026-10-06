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

    // the ROMs Beta 1 supports: the shared check (MphRecomp.App RomSetup)
    static string? Unsupported(string romPath) => RomSetup.Unsupported(romPath);

    void Extract(string romPath)
    {
        if (RomSetup.Extract(romPath, FilesDir!.AbsolutePath, Log))
        {
            RunOnUiThread(() =>
            {
                StartActivity(new Intent(this, typeof(GameActivity)));
                Finish();
            });
        }
        else
        {
            RunOnUiThread(() => _select.Enabled = true);
        }
    }

    void Log(string msg)
    {
        lock (_sb) _sb.AppendLine(msg);
        Android.Util.Log.Info("MPHExtract", msg);
        RunOnUiThread(() => { lock (_sb) _log.Text = _sb.ToString(); });
    }
}
