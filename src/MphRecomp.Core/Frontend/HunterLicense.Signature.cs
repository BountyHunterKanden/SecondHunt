using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MphRecomp.Config;

namespace MphRecomp.Frontend
{
    // The signed license file (owner 11-16): hunter_license.json carries "Signature", an HMAC-SHA256 over the file's own
    // JSON in a canonical form (keys sorted, no whitespace, the Signature itself left out), so a hand edit of the rank,
    // records or stats is noticed on load. The key is derived here in code, so this only deters casual edits: anyone
    // with the app can find it. The real fix is the server keeping the rank (issue log license-server-rank).
    //   - The signature covers the file's text, not the loaded object: a build that adds a field (or clamps tighter)
    //     doesn't break the signature an older build wrote.
    //   - An unsigned file (from before this, or from an older build) is signed on its first load: nobody loses a
    //     license. Once this install has a signed .bak, an unsigned file counts as edited (its signature taken out).
    //   - A file that fails the check is never wiped or saved over: it's moved aside as hunter_license.json.edited-<time>
    //     (.unreadable-<time> if it isn't a license file at all) and the newest good signed copy is loaded instead: the
    //     .bak that mirrors every save, then the copy from before the last cart import. With no good copy, an edited
    //     license keeps its profile (name, unlocks, hunter of choice) and its rank, records and stats start again.
    //   - Each of these goes in settings_changes.log beside it (RecompSettings.ChangeLog).
    public sealed partial class HunterLicense
    {
        const string SignatureField = "Signature";
        const string SignaturePrefix = "v1:";
        // one at a time in this process: the menus and ReplayLink (another thread) both load it
        static readonly object FileLock = new();
        // the file failed its check and couldn't be moved aside (or couldn't be read): never saved over
        bool _keepOff;

        // what a license file's check found
        public enum FileCheck { Signed, Unsigned, Edited, Unreadable }

        static readonly byte[] Key = DeriveKey();

        static byte[] DeriveKey()
        {
            // not a secret worth hiding (see above): a fixed mix so the key isn't one string to grep for
            byte[] seed = new byte[48];
            uint x = 0x5EC0D4A7;
            for (int i = 0; i < seed.Length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                seed[i] = (byte)((x >> 8) ^ (uint)(i * 0x3B));
            }
            return HMACSHA256.HashData(seed, Encoding.UTF8.GetBytes("SECOND HUNT|hunter license|" + FileName + "|1"));
        }

        // the file's JSON with its keys sorted and no whitespace, the root's Signature left out
        static byte[] Canonical(JsonElement root)
        {
            using MemoryStream ms = new();
            using (Utf8JsonWriter w = new(ms))
            {
                Write(w, root, true);
            }
            return ms.ToArray();

            static void Write(Utf8JsonWriter w, JsonElement e, bool root)
            {
                switch (e.ValueKind)
                {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    // (a duplicated key stays in twice: a saved file never has one, so it fails the check)
                    foreach (JsonProperty p in e.EnumerateObject().Where(p => !root || p.Name != SignatureField)
                        .OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        w.WritePropertyName(p.Name);
                        Write(w, p.Value, false);
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (JsonElement a in e.EnumerateArray()) Write(w, a, false);
                    w.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    w.WriteStringValue(e.GetString());
                    break;
                case JsonValueKind.Number:
                    w.WriteRawValue(e.GetRawText(), skipInputValidation: true);
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    w.WriteBooleanValue(e.GetBoolean());
                    break;
                default:
                    w.WriteNullValue();
                    break;
                }
            }
        }

        static string Sign(JsonElement root) => SignaturePrefix + Convert.ToHexString(HMACSHA256.HashData(Key, Canonical(root)));

        // the check of a license file's text
        public static FileCheck Check(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return FileCheck.Unreadable;
                if (JsonSerializer.Deserialize<HunterLicense>(json) == null) return FileCheck.Unreadable;
                if (!doc.RootElement.TryGetProperty(SignatureField, out JsonElement sig)) return FileCheck.Unsigned;
                if (sig.ValueKind != JsonValueKind.String) return FileCheck.Edited;
                byte[] want = Encoding.ASCII.GetBytes(Sign(doc.RootElement));
                byte[] have = Encoding.ASCII.GetBytes(sig.GetString() ?? "");
                return CryptographicOperations.FixedTimeEquals(want, have) ? FileCheck.Signed : FileCheck.Edited;
            }
            catch (Exception)
            {
                return FileCheck.Unreadable;
            }
        }

        // this license as a signed file's text
        public string SignedJson()
        {
            JsonObject node = JsonNode.Parse(JsonSerializer.Serialize(this, Options))!.AsObject();
            using (JsonDocument doc = JsonDocument.Parse(node.ToJsonString()))
            {
                node[SignatureField] = Sign(doc.RootElement);
            }
            return node.ToJsonString(Options);
        }

        static void Log(string path, string what)
        {
            Console.Error.WriteLine($"hunter license: {what}");
            RecompSettings.ChangeLog(path, "hunter license: " + what);
        }

        static bool TryReadText(string file, out string json)
        {
            json = "";
            try
            {
                if (!File.Exists(file)) return false;
                json = File.ReadAllText(file);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // the folder's license, checked; save = write it back now (signed on migration, or the good copy restored)
        static HunterLicense LoadFile(string dir, out bool save)
        {
            save = false;
            string path = Path.Combine(dir, FileName);
            if (!File.Exists(path))
            {
                // no file: a fresh install, or it went missing; the last good save's copy if there is one
                HunterLicense? restored = LoadBackup(dir, "the license file is missing");
                save = restored != null;
                return restored ?? new();
            }
            if (!TryReadText(path, out string json))
            {
                // there but can't be read (permissions): never saved over, as the settings
                Log(path, "the license file couldn't be read: not saving over it");
                return new() { _keepOff = true };
            }
            FileCheck check = Check(json);
            if (check == FileCheck.Signed) return JsonSerializer.Deserialize<HunterLicense>(json)!;
            // (not in the fixes-only release: a public build takes any file that reads, as before, until the owner has
            // checked the signature; its saves are signed all the same, so the check finds a signed file when it comes)
            if (RecompSettings.PublicBuild && check is FileCheck.Unsigned or FileCheck.Edited) return JsonSerializer.Deserialize<HunterLicense>(json)!;
            if (check == FileCheck.Unsigned)
            {
                if (!(TryReadText(path + ".bak", out string bak) && Check(bak) == FileCheck.Signed))
                {
                    Log(path, "the license file had no signature (from before signing, or an older build): signed it as it is");
                    save = true;
                    return JsonSerializer.Deserialize<HunterLicense>(json)!;
                }
                check = FileCheck.Edited; // this install signs every save: the signature was taken out
            }
            bool edited = check == FileCheck.Edited;
            bool moved = SetAside(path, edited);
            HunterLicense? license = LoadBackup(dir, edited ? "the license file was edited" : "the license file was unreadable");
            if (license == null && edited)
            {
                license = ProfileOnly(JsonSerializer.Deserialize<HunterLicense>(json)!);
                Log(path, "no good copy to go back to: kept the name, unlocks and hunter of choice; the rank, records and stats start again");
            }
            else if (license == null)
            {
                license = new();
                Log(path, "no good copy to go back to: a new license");
            }
            license._keepOff = !moved;
            save = moved;
            return license;
        }

        // move a file that failed its check out of the way (renaming needs only the folder, which is the app's own)
        static bool SetAside(string path, bool edited)
        {
            string why = edited ? "edited (its signature doesn't match)" : "unreadable (not a license file)";
            string aside = path + (edited ? ".edited-" : ".unreadable-") + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            for (int i = 2; File.Exists(aside); i++) aside = aside.Split('~')[0] + "~" + i; // (two in one second)
            try
            {
                File.Move(path, aside);
                Log(path, $"the license file was {why}: kept as {Path.GetFileName(aside)}");
                return true;
            }
            catch (Exception e)
            {
                Log(path, $"the license file was {why} and couldn't be moved aside ({e.Message}): not saving over it");
                return false;
            }
        }

        // the newest good signed copy: the .bak mirror of the last save, then the copy from before the last cart import
        static HunterLicense? LoadBackup(string dir, string why)
        {
            string path = Path.Combine(dir, FileName);
            string[] copies = { path + ".bak", Path.Combine(dir, MultiplayerMenu.BeforeImportFile) };
            foreach (string copy in copies)
            {
                if (TryReadText(copy, out string json) && Check(json) == FileCheck.Signed)
                {
                    Log(path, $"{why}: loaded the license from {Path.GetFileName(copy)}");
                    return JsonSerializer.Deserialize<HunterLicense>(json);
                }
            }
            string[] there = copies.Where(File.Exists).Select(c => Path.GetFileName(c)).ToArray();
            if (there.Length > 0) Log(path, $"{why}: no good signed copy ({string.Join(", ", there)} failed the check)");
            return null;
        }

        // an edited license with nothing to go back to: its profile, a new license's rank, records and stats
        static HunterLicense ProfileOnly(HunterLicense edited) => new()
        {
            Name = edited.Name,
            NoticeSeen = edited.NoticeSeen,
            LastAddress = edited.LastAddress,
            Arenas = edited.Arenas,
            Hunters = edited.Hunters,
            ChoiceHunter = edited.ChoiceHunter,
            ChoiceCharacter = edited.ChoiceCharacter,
            SkinChoice = edited.SkinChoice,
        };

        // the signed file: a temp file renamed over it (a crash never leaves half a license), then the .bak mirror
        void WriteSigned(string dir)
        {
            string path = Path.Combine(dir, FileName);
            if (_keepOff)
            {
                Log(path, "not saved: the license file that failed its check is still there");
                return;
            }
            Directory.CreateDirectory(dir);
            string json = SignedJson();
            Replace(path, json);
            try
            {
                Replace(path + ".bak", json);
            }
            catch (Exception)
            {
            }

            static void Replace(string file, string text)
            {
                string tmp = $"{file}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmp, text);
                    File.Move(tmp, file, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            }
        }
    }
}
