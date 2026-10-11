using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MphRecomp.Online
{
    // The one per-player mute setting (docs/ONLINE_PLAY.md section 8, Social O7; Voice O8 reads the same list): a player
    // is muted by friend code, for typed chat and/or voice, kept on this device in online_mutes.json beside
    // online_settings.json. Nothing here goes to the server; muting only changes what this device shows and plays.
    // Chat/Voice/Set may be called from any thread (the game thread, the network thread, voice playback).
    public sealed class PlayerMutes
    {
        public const string FileName = "online_mutes.json";

        public sealed class Entry
        {
            public bool Chat { get; set; }
            public bool Voice { get; set; }
        }

        private readonly object _lock = new();
        private readonly Dictionary<string, Entry> _entries = new();
        private readonly string? _dir;

        // the device's list: set once by the menus (the profile folder), the match overlay and Voice read it
        public static PlayerMutes Current { get; private set; } = new(null);

        // fired (on the calling thread) after a change
        public event Action? Changed;

        public PlayerMutes(string? profileDir)
        {
            _dir = profileDir;
        }

        public static PlayerMutes Use(string? profileDir)
        {
            if (Current._dir != profileDir || profileDir == null)
            {
                Current = Load(profileDir);
            }
            return Current;
        }

        public static PlayerMutes Load(string? profileDir)
        {
            var mutes = new PlayerMutes(profileDir);
            string? path = profileDir == null ? null : Path.Combine(profileDir, FileName);
            try
            {
                if (path != null && File.Exists(path))
                {
                    var read = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                    if (read != null)
                    {
                        foreach ((string code, Entry e) in read)
                        {
                            if (e.Chat || e.Voice)
                            {
                                mutes._entries[code] = e;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Console.Error.WriteLine($"online: {path}: {ex.Message}");
            }
            return mutes;
        }

        public bool Chat(string code)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(code, out Entry? e) && e.Chat;
            }
        }

        public bool Voice(string code)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(code, out Entry? e) && e.Voice;
            }
        }

        public bool Any(string code) => Chat(code) || Voice(code);

        // the friend codes muted in either way
        public IReadOnlyList<string> Codes()
        {
            lock (_lock)
            {
                return _entries.Keys.ToList();
            }
        }

        // mute / unmute; false for either clears that half. Both false removes the entry.
        public void Set(string code, bool chat, bool voice)
        {
            lock (_lock)
            {
                if (chat || voice)
                {
                    _entries[code] = new Entry { Chat = chat, Voice = voice };
                }
                else
                {
                    _entries.Remove(code);
                }
                Save();
            }
            Changed?.Invoke();
        }

        public void SetChat(string code, bool muted) => Set(code, muted, Voice(code));

        public void SetVoice(string code, bool muted) => Set(code, Chat(code), muted);

        // the simple switch the menus use: both chat and voice
        public void Toggle(string code) => Set(code, !Any(code), !Any(code));

        private void Save()
        {
            if (_dir == null)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, FileName), JsonSerializer.Serialize(_entries));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"online: mutes not saved: {ex.Message}");
            }
        }
    }
}
