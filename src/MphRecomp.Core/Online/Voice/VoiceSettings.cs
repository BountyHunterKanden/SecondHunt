using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MphRecomp.Online.Voice
{
    public enum VoiceMode { Off, PushToTalk, OpenMic }

    // This device's voice settings, kept in online_voice.json beside online_settings.json (decision 6): push to talk or
    // open mic; voice with strangers and with friends ON by default (release 0.2.0, owner 10-11: "i want strangers to be able
    // to talk"; was strangers OFF), each changeable globally and per player (by friend code). The per-player MUTE is Social
    // O7's PlayerMutes (one mute for chat and voice), read on top of this. One setting for the lobby and the match: VoiceChat
    // reads Mode wherever it runs. Chat = typed and quick chat shown / sent at all (ONLINE SETTINGS' TYPED CHAT). Any thread.
    public sealed class VoiceSettings
    {
        public const string FileName = "online_voice.json";

        [JsonConverter(typeof(JsonStringEnumConverter<VoiceMode>))]
        public VoiceMode Mode { get; set; } = VoiceMode.PushToTalk;
        // the mode VOICE CHAT ON goes back to (the last one that wasn't Off)
        [JsonConverter(typeof(JsonStringEnumConverter<VoiceMode>))]
        public VoiceMode OnMode { get; set; } = VoiceMode.PushToTalk;
        public bool Strangers { get; set; } = true;
        public bool Friends { get; set; } = true;
        // chat (typed + quick messages) shown and sent; off = others' lines aren't shown and nothing goes out
        public bool Chat { get; set; } = true;
        // the music fades down while somebody else is heard (Thread C, C3, 2026-10-23)
        public bool DuckMusic { get; set; } = true;
        // the file's version: < 2 = written while strangers were off by default (they're turned on once, at load)
        public int Version { get; set; }
        // 0..2, everyone's voices
        public float Volume { get; set; } = 1;
        // per player (friend code): voice on / off whatever the defaults say
        public Dictionary<string, bool> Players { get; set; } = new();

        private static readonly object Gate = new();
        private string? _dir;

        public static VoiceSettings Current { get; private set; } = new();

        // fired (on the calling thread) after a change
        public static event Action? Changed;

        // the profile folder (the menus' and the hosts'); loads the file there once
        public static VoiceSettings Use(string? profileDir)
        {
            lock (Gate)
            {
                if (profileDir != null && Current._dir != profileDir)
                {
                    Current = Load(profileDir);
                }
                return Current;
            }
        }

        public static VoiceSettings Load(string? dir)
        {
            string? path = dir == null ? null : Path.Combine(dir, FileName);
            VoiceSettings s = new();
            try
            {
                if (path != null && File.Exists(path))
                {
                    s = JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path)) ?? new();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Console.Error.WriteLine($"voice: {path}: {ex.Message}");
            }
            s._dir = dir;
            if (s.Version < 2)
            {
                s.Strangers = true; // (release 0.2.0: strangers on for everyone once; a later OFF is kept)
                s.Version = 2;
            }
            if (s.OnMode == VoiceMode.Off)
            {
                s.OnMode = VoiceMode.PushToTalk;
            }
            if (s.Mode != VoiceMode.Off)
            {
                s.OnMode = s.Mode;
            }
            s.Players ??= new();
            s.Volume = Math.Clamp(s.Volume, 0, 2);
            return s;
        }

        // the per-player choice, or null = the default for friends / strangers
        public bool? Player(string code)
        {
            lock (Gate)
            {
                return Players.TryGetValue(code, out bool on) ? on : null;
            }
        }

        public void SetPlayer(string code, bool? on) => Change(() =>
        {
            if (on is bool b) Players[code] = b;
            else Players.Remove(code);
        });

        public void SetMode(VoiceMode mode) => Change(() =>
        {
            Mode = mode;
            if (mode != VoiceMode.Off)
            {
                OnMode = mode;
            }
        });

        // VOICE CHAT on / off: off keeps the push to talk / open mic choice for when it comes back on
        public void SetOn(bool on) => SetMode(on ? OnMode : VoiceMode.Off);

        // PUSH TO TALK on / off (off = open mic), whether voice is on now or not
        public void SetPushToTalk(bool ptt) => Change(() =>
        {
            OnMode = ptt ? VoiceMode.PushToTalk : VoiceMode.OpenMic;
            if (Mode != VoiceMode.Off)
            {
                Mode = OnMode;
            }
        });
        public void SetChat(bool on) => Change(() => Chat = on);
        public void SetDuckMusic(bool on) => Change(() => DuckMusic = on);
        public void SetStrangers(bool on) => Change(() => Strangers = on);
        public void SetFriends(bool on) => Change(() => Friends = on);
        public void SetVolume(float v) => Change(() => Volume = Math.Clamp(v, 0, 2));

        private void Change(Action change)
        {
            lock (Gate)
            {
                change();
                Save();
            }
            Changed?.Invoke();
        }

        private void Save()
        {
            if (_dir == null)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, FileName), JsonSerializer.Serialize(this));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"voice: settings not saved: {ex.Message}");
            }
        }
    }
}
