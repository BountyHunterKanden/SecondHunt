using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MphRecomp.Config;
using MphRecomp.Frontend;
using MphRecomp.Online;

namespace MphRecomp.Profile
{
    // CLOUD SAVE (docs/PROFILE_SYNC.md Phase 2): this device's side of the hub's sealed profile copy (Online/CloudSave.cs).
    // Push: a sync point (a campaign save, leaving SETTINGS, a match's end, the app pausing or quitting) takes a snapshot on
    // its own thread from the live license and settings and the save files (a profile pack, ProfilePack.WriteTo, sealed),
    // skipped when its ContentHash is what was last put; a background task puts the newest snapshot once the 30 s gap has
    // passed, compare-and-swap on the rev last seen. Pull (the menus, RecompMenus.Cloud.cs): at app start, at each hub
    // connect and after a put the hub refused as a conflict, the menus fetch the copy, merge it (the license by
    // MergeProfile, the settings key by key against the last synced ones, a campaign slot changed on both sides asked,
    // the replaced one in saves/before_sync/) and put the result. Each operation logs in on its own cloud-only connection
    // (it doesn't kick this player's hub login). cloud_save.json in the profile folder keeps the last synced rev and what
    // was in it; never in a pack.
    public sealed class CloudSync
    {
        public const string StateFile = "cloud_save.json";
        public const int MaxConflictsInRow = 3;

        // the app's one (FrontendSession sets it up)
        public static CloudSync? Current { get; set; }

        private readonly string _profileDir, _savesDir;
        private readonly string? _settingsPath;
        private readonly Func<HunterLicense?> _license;
        private readonly Func<RecompSettings?> _settings;
        private readonly Func<Uri> _server;
        private readonly string _app;
        private readonly object _lock = new();
        private CloudSyncState _state;
        private Snapshot? _pending;
        private string? _inFlight; // the hash of the snapshot being put now
        private Task? _worker;
        private bool _wantsSync = true; // (app start)
        private bool _held; // the copy there is from a newer game: nothing put over it this run
        private bool _oldServer; // the hub said it's older than cloud save (HubConnected)
        private int _conflicts;
        private string? _lastError;

        private sealed record Snapshot(byte[] Blob, string Hash, Dictionary<string, string> Hashes, string Settings, long BaseRev, string Why);

        public CloudSync(string profileDir, string savesDir, string? settingsPath, Func<HunterLicense?> license,
            Func<RecompSettings?> settings, Func<Uri> server, string app)
        {
            _profileDir = profileDir;
            _savesDir = savesDir;
            _settingsPath = settingsPath;
            _license = license;
            _settings = settings;
            _server = server;
            _app = app;
            _state = LoadState(profileDir);
        }

        // (not in a public build yet: its row is hidden there, RecompMenus HiddenInPublic, and this keeps a hand-edited
        // settings file from turning it on against the live hub)
        public bool Enabled => !MphRecomp.Config.RecompSettings.PublicBuild && _settings()?.CloudSave == true && CloudSeal.Available;
        public string SavesDir => _savesDir;
        public string ProfileDir => _profileDir;
        // the last synced settings (the merge's base) and save hashes (Plan's "seen"), and rev
        public string? SettingsBase { get { lock (_lock) return _state.SettingsBase; } }
        public IReadOnlyDictionary<string, string> Hashes { get { lock (_lock) return new Dictionary<string, string>(_state.Hashes); } }
        public long Rev { get { lock (_lock) return _state.Rev; } }
        public DateTime SyncedUtc { get { lock (_lock) return _state.SyncedUtc; } }
        public string? LastError { get { lock (_lock) return _lastError; } }
        public bool Held { get { lock (_lock) return _held; } }

        // ---- sync points ----

        // the hub connection came up (OnlineMenus): pull again. An older server (no cloud save) would take a cloud-only
        // login for a normal one and kick the player's: nothing is sent to it.
        public void HubConnected(bool supported)
        {
            lock (_lock)
            {
                _oldServer = !supported;
                if (supported) _wantsSync = true;
            }
        }

        // the menus ask each frame: a pull is due (app start, hub connect, a conflict); taking it clears it
        public bool TakeSyncWanted()
        {
            lock (_lock)
            {
                if (!_wantsSync || _held || _oldServer || !Enabled) return false;
                _wantsSync = false;
                return true;
            }
        }

        // a sync point: snapshot now (on the caller's thread: the live objects aren't touched from another), put it soon
        public void Push(string why) => Push(why, null);

        private void Push(string why, long? baseRev)
        {
            try
            {
                if (!Enabled || Held) return;
                lock (_lock) if (_oldServer) return;
                HunterLicense? license = _license();
                RecompSettings? settings = _settings();
                OnlineIdentity? id = OnlineIdentity.Load(_profileDir);
                if (license == null || settings == null || id == null) return; // (no online identity yet: nothing to seal with)
                string hash = ProfilePack.ContentHash(license, settings, _savesDir);
                lock (_lock)
                {
                    // (what's queued, being put or last put already holds this)
                    if (baseRev == null && hash == (_pending?.Hash ?? _inFlight ?? _state.PushedHash)) return;
                }
                using MemoryStream ms = new();
                ProfilePack.WriteTo(ms, license, settings, _savesDir, Update.UpdateHost.AppVersion);
                byte[] plain = ms.ToArray();
                if (plain.Length + CloudSaveRules.MinBlob > CloudSaveRules.MaxBlob)
                {
                    Fail($"cloud save: the profile is too big for the cloud ({plain.Length / 1024} KB)");
                    return;
                }
                Snapshot snap = new(CloudSeal.Seal(id, plain), hash, ProfilePack.SaveHashes(_savesDir), ProfileSettingsKeys.Pick(settings),
                    baseRev ?? -1, why);
                lock (_lock)
                {
                    _pending = snap; // (the newest wins)
                    if (_worker is not { IsCompleted: false }) _worker = Task.Run(PutLoop);
                }
            }
            catch (Exception ex)
            {
                // (Android's pause pushes from the UI thread while the menus' thread may be changing the license: the next
                // sync point takes it)
                Fail($"cloud save: snapshot failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        // the app pauses or quits: snapshot, then wait a little for the put (Android may stop the process soon after)
        public void Flush(string why, int waitMs = 3000)
        {
            Push(why);
            Task? w;
            lock (_lock) w = _worker;
            try
            {
                w?.Wait(waitMs);
            }
            catch (AggregateException)
            {
            }
        }

        private async Task PutLoop()
        {
            int failures = 0;
            while (true)
            {
                Snapshot? snap;
                long wait;
                lock (_lock)
                {
                    snap = _pending;
                    if (snap == null) return;
                    wait = _state.LastPutUnix + CloudSaveRules.PutGapSeconds + 1 - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, CloudSaveRules.PutGapSeconds + 1)));
                    continue;
                }
                lock (_lock)
                {
                    snap = _pending;
                    _pending = null;
                    _inFlight = snap?.Hash;
                }
                if (snap == null) return;
                string result;
                try
                {
                    result = await PutAsync(snap);
                }
                finally
                {
                    lock (_lock) _inFlight = null;
                }
                if (result == "retry")
                {
                    lock (_lock) _pending ??= snap;
                    if (++failures >= 3) return; // (the next sync point tries again: its hash differs from the last put)
                    await Task.Delay(TimeSpan.FromSeconds(10 * failures));
                    continue;
                }
                failures = 0;
            }
        }

        // one put on a cloud-only login: "done", "conflict", "retry" or "stop"
        private async Task<string> PutAsync(Snapshot snap)
        {
            long baseRev;
            lock (_lock) baseRev = snap.BaseRev >= 0 ? snap.BaseRev : _state.Rev;
            try
            {
                await using OnlineClient c = await ConnectAsync();
                CloudPutMsg r = await c.RequestAsync<CloudPutMsg>(new CloudPutReq { BaseRev = baseRev, Blob = Convert.ToBase64String(snap.Blob) });
                lock (_lock)
                {
                    _state.LastPutUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (r.Done)
                    {
                        _state.Rev = r.Rev;
                        _state.PushedHash = snap.Hash;
                        _state.Hashes = snap.Hashes;
                        _state.SettingsBase = snap.Settings;
                        _state.SyncedUtc = DateTime.UtcNow;
                        _conflicts = 0;
                        _lastError = null;
                    }
                    else if (++_conflicts <= MaxConflictsInRow)
                    {
                        _wantsSync = true; // (the menus pull, merge and push again)
                    }
                    SaveState();
                }
                Log(r.Done ? $"cloud save: put rev {r.Rev} ({snap.Why}, {snap.Blob.Length / 1024} KB)"
                    : $"cloud save: the cloud has rev {r.Rev} (this device had {baseRev}): pulling it first");
                if (!r.Done && _conflicts > MaxConflictsInRow) Fail("cloud save: kept conflicting with another device; tries again at the next sync");
                return r.Done ? "done" : "conflict";
            }
            catch (OnlineException ex)
            {
                switch (ex.Code)
                {
                case "too_soon":
                    lock (_lock) _state.LastPutUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    lock (_lock) _pending ??= snap;
                    return "done"; // (PutLoop waits the gap out)
                case "no_matches":
                    Fail("cloud save: starts after your first online match");
                    return "stop";
                case "old_server":
                    Fail("cloud save: the online server doesn't have cloud save yet");
                    return "stop";
                default:
                    Fail($"cloud save: refused ({ex.Code}: {ex.Message})");
                    return "stop";
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or TimeoutException
                or OperationCanceledException or System.Net.Http.HttpRequestException)
            {
                Fail($"cloud save: no connection ({ex.Message})");
                return "retry";
            }
        }

        // ---- pull (RecompMenus.Cloud.cs) ----

        public sealed record Pulled(long Rev, byte[]? Plain, bool Empty, bool Unopenable);

        // the copy there, opened (Empty: none; Unopenable: it isn't this identity's or is damaged)
        public async Task<Pulled> FetchAsync()
        {
            OnlineIdentity id = OnlineIdentity.Load(_profileDir) ?? throw new OnlineException("no_identity", "no online identity yet");
            await using OnlineClient c = await ConnectAsync();
            CloudSaveMsg m = await c.RequestAsync<CloudSaveMsg>(new CloudGetReq());
            if (m.Blob == "") return new Pulled(m.Rev, null, true, false);
            byte[]? plain;
            try
            {
                plain = CloudSeal.Open(id, Convert.FromBase64String(m.Blob));
            }
            catch (FormatException)
            {
                plain = null;
            }
            return new Pulled(m.Rev, plain, false, plain == null);
        }

        // after a pull was merged (or there was nothing to take): the cloud's rev, settings and save hashes are the base
        // now; then put this device's result (from that rev)
        public void Adopt(long rev, string? cloudSettings, IReadOnlyDictionary<string, string>? cloudHashes, string why)
        {
            lock (_lock)
            {
                _state.Rev = rev;
                if (cloudSettings != null) _state.SettingsBase = cloudSettings;
                if (cloudHashes != null) _state.Hashes = new Dictionary<string, string>(cloudHashes);
                _state.PushedHash = ""; // (the merge's result goes up even if it's what this device had)
                _state.SyncedUtc = DateTime.UtcNow;
                SaveState();
            }
            Push(why, rev);
        }

        // the copy there is from a newer game: leave it alone this run
        public void Hold(string why)
        {
            lock (_lock) _held = true;
            Fail(why);
        }

        // CLOUD SAVE turned on: the first sync starts from nothing (every slot both sides have and differ is asked)
        public void Restart()
        {
            lock (_lock)
            {
                _state = new CloudSyncState();
                _held = false;
                _conflicts = 0;
                _lastError = null;
                _wantsSync = true;
                SaveState();
            }
        }

        // turned off, the copy deleted (when the player said so): this device's record of it goes too
        public async Task<bool> DeleteAsync()
        {
            await using OnlineClient c = await ConnectAsync();
            OkMsg ok = await c.RequestAsync<OkMsg>(new CloudDeleteReq());
            lock (_lock)
            {
                _state = new CloudSyncState();
                _pending = null;
                SaveState();
            }
            Log("cloud save: the cloud copy deleted");
            return ok.Done;
        }

        // turned off: nothing more is put; what's queued is dropped
        public void Stop()
        {
            lock (_lock) _pending = null;
        }

        private async Task<OnlineClient> ConnectAsync()
        {
            OnlineIdentity id = OnlineIdentity.Load(_profileDir) ?? throw new OnlineException("no_identity", "no online identity yet");
            string name = OnlineNames.Clean(_license()?.Name);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
            OnlineClient c = await OnlineClient.ConnectAsync(_server(), id, name, _app, cts.Token, cloudOnly: true);
            if (!CloudSaveRules.Supported(c))
            {
                await c.DisposeAsync();
                lock (_lock) _oldServer = true;
                throw new OnlineException("old_server", $"the server is {c.ServerVersion}");
            }
            return c;
        }

        private void Fail(string line)
        {
            lock (_lock)
            {
                if (_lastError == line) return; // (once, not at every sync point)
                _lastError = line;
            }
            Log(line);
        }

        private void Log(string line)
        {
            Console.Error.WriteLine(line);
            if (_settingsPath != null) RecompSettings.ChangeLog(_settingsPath, line);
        }

        // ---- cloud_save.json ----

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static CloudSyncState LoadState(string dir)
        {
            try
            {
                string path = Path.Combine(dir, StateFile);
                if (File.Exists(path)) return JsonSerializer.Deserialize<CloudSyncState>(File.ReadAllText(path), Options) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"cloud save: {StateFile} unreadable ({ex.Message}): starting over");
            }
            return new();
        }

        // (under _lock)
        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(_profileDir);
                string path = Path.Combine(_profileDir, StateFile), tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Options));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"cloud save: {StateFile} not written ({ex.Message})");
            }
        }
    }

    public sealed class CloudSyncState
    {
        public long Rev { get; set; }
        public string PushedHash { get; set; } = "";
        public string? SettingsBase { get; set; }
        public Dictionary<string, string> Hashes { get; set; } = new();
        public long LastPutUnix { get; set; }
        public DateTime SyncedUtc { get; set; }
    }
}
