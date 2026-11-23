using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MphRecomp.Frontend
{
    // The license in a profile pack (docs/PROFILE_SYNC.md Phase 1, §4): two devices' licenses merged so nothing counts
    // twice and nothing goes down, however often the same pack goes back and forth.
    //   - Counters (matches, wins, kills, seconds, the per-hunter/weapon/mode/arena/character/skin counts) are kept per
    //     device: ProfileShares holds the other devices' shares as last seen, this device's share is the rest. A merge
    //     keeps each device's larger share and the counters become the sum, so re-importing a pack adds nothing.
    //   - Rank points and the best streaks: the larger. Unlocks, the beaten/100% marks, the notice: either side's.
    //   - The name: the side saved last (SavedAt). The favourites and the hunter of choice are worked out again from
    //     the merged counts. The current streaks, the quit penalty, the cart import's undo record and the last address
    //     stay this device's.
    //   - A pack whose license fails its signature check (hand-edited) gives only its name and unlocks, as an edited
    //     file on load does (ProfileOnly).
    // None of this goes to the server: the online card (OnlineMenus.LicenseJson) is its own list of fields.
    public sealed partial class HunterLicense
    {
        // this install's id in ProfileShares (random, made on the first export or import; not the ban DeviceTag)
        public string ProfileDevice { get; set; } = "";
        // the other devices' counter shares (device id -> counter key -> count)
        public Dictionary<string, Dictionary<string, double>> ProfileShares { get; set; } = new();
        // when this license was last saved (UTC): the name's tie-break
        public DateTime SavedAt { get; set; }

        public string EnsureProfileDevice()
        {
            if (string.IsNullOrEmpty(ProfileDevice)) ProfileDevice = Guid.NewGuid().ToString("N")[..16];
            return ProfileDevice;
        }

        // the license as it goes in a pack: signed, without what stays on this device (the last address, the cart
        // import's undo record)
        public string PackJson()
        {
            EnsureProfileDevice();
            HunterLicense copy = JsonSerializer.Deserialize<HunterLicense>(JsonSerializer.Serialize(this, Options))!;
            copy.LastAddress = "";
            copy.Imported = null;
            return copy.SignedJson();
        }

        // every counter by a flat key
        public Dictionary<string, double> Counters()
        {
            Dictionary<string, double> c = new()
            {
                ["LocalMatches"] = LocalMatches, ["OnlineMatches"] = OnlineMatches, ["WifiWins"] = WifiWins,
                ["WirelessWins"] = WirelessWins, ["BipedKills"] = BipedKills, ["AltFormKills"] = AltFormKills,
                ["HeadshotKills"] = HeadshotKills, ["Disconnects"] = Disconnects, ["WirelessSeconds"] = WirelessSeconds,
                ["WifiSeconds"] = WifiSeconds, ["TotalSeconds"] = TotalSeconds,
            };
            for (int i = 0; i < HunterMatches.Length; i++) c[$"HunterMatches.{i}"] = HunterMatches[i];
            for (int i = 0; i < WeaponKills.Length; i++) c[$"WeaponKills.{i}"] = WeaponKills[i];
            for (int i = 0; i < ModeMatches.Length; i++) c[$"ModeMatches.{i}"] = ModeMatches[i];
            foreach (var kv in ArenaWins) c[$"ArenaWins.{kv.Key}"] = kv.Value;
            foreach (var kv in ArenaMatches) c[$"ArenaMatches.{kv.Key}"] = kv.Value;
            foreach (var kv in CharacterMatches) c[$"CharacterMatches.{kv.Key}"] = kv.Value;
            foreach (var kv in SkinMatches) c[$"SkinMatches.{kv.Key}"] = kv.Value;
            foreach (string k in c.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) c.Remove(k);
            return c;
        }

        private void SetCounters(Dictionary<string, double> c)
        {
            int I(string k) => (int)Math.Min(int.MaxValue, Math.Round(c.GetValueOrDefault(k)));
            LocalMatches = I("LocalMatches");
            OnlineMatches = I("OnlineMatches");
            WifiWins = I("WifiWins");
            WirelessWins = I("WirelessWins");
            BipedKills = I("BipedKills");
            AltFormKills = I("AltFormKills");
            HeadshotKills = I("HeadshotKills");
            Disconnects = I("Disconnects");
            WirelessSeconds = c.GetValueOrDefault("WirelessSeconds");
            WifiSeconds = c.GetValueOrDefault("WifiSeconds");
            TotalSeconds = c.GetValueOrDefault("TotalSeconds");
            for (int i = 0; i < HunterMatches.Length; i++) HunterMatches[i] = I($"HunterMatches.{i}");
            for (int i = 0; i < WeaponKills.Length; i++) WeaponKills[i] = I($"WeaponKills.{i}");
            for (int i = 0; i < ModeMatches.Length; i++) ModeMatches[i] = I($"ModeMatches.{i}");
            ArenaWins = Keyed("ArenaWins.").Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
            ArenaMatches = Keyed("ArenaMatches.").Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
            CharacterMatches = Keyed("CharacterMatches.").ToDictionary(kv => kv.Key, kv => kv.Value);
            SkinMatches = Keyed("SkinMatches.").ToDictionary(kv => kv.Key, kv => kv.Value);

            IEnumerable<KeyValuePair<string, int>> Keyed(string prefix) => c.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value > 0)
                .Select(kv => new KeyValuePair<string, int>(kv.Key[prefix.Length..], I(kv.Key)));
        }

        // this device's own share: what it has less what the other devices are known to have added
        private Dictionary<string, double> OwnShare()
        {
            Dictionary<string, double> own = Counters();
            foreach (var share in ProfileShares.Where(s => s.Key != ProfileDevice).Select(s => s.Value))
            {
                foreach (var kv in share) own[kv.Key] = own.GetValueOrDefault(kv.Key) - kv.Value;
            }
            foreach (string k in own.Keys.ToList())
            {
                if (own[k] <= 0) own.Remove(k);
            }
            return own;
        }

        // Another device's license (a pack's) into this one. signed = its file passed Check. Returns a log line.
        public string MergeProfile(HunterLicense other, bool signed)
        {
            string me = EnsureProfileDevice();
            other.Name = Clean(other.Name);
            if (!signed)
            {
                Arenas |= other.Arenas;
                Hunters |= other.Hunters;
                if (other.SavedAt > SavedAt && other.Name != MultiplayerMenu.DefaultNickname) Name = other.Name;
                return $"profile: merged an unsigned or edited license: name and unlocks only (name {Name})";
            }
            int pointsBefore = RankPoints, matchesBefore = Matches;
            // every device's share: ours, the ones we knew of, the incoming one's and the ones it knew of; each the larger
            Dictionary<string, Dictionary<string, double>> shares = new();
            void Take(string device, Dictionary<string, double> share)
            {
                if (string.IsNullOrEmpty(device)) device = "unknown";
                if (!shares.TryGetValue(device, out var have)) shares[device] = have = new();
                foreach (var kv in share)
                {
                    if (kv.Value > have.GetValueOrDefault(kv.Key)) have[kv.Key] = kv.Value;
                }
            }
            Take(me, OwnShare());
            foreach (var s in ProfileShares) Take(s.Key, s.Value);
            other.ProfileShares ??= new();
            other.HunterMatches = Sized(other.HunterMatches, 7);
            other.WeaponKills = Sized(other.WeaponKills, 9);
            other.ModeMatches = Sized(other.ModeMatches, 7);
            other.ArenaWins ??= new();
            other.ArenaMatches ??= new();
            other.CharacterMatches ??= new();
            other.SkinMatches ??= new();
            Take(other.ProfileDevice, other.OwnShare());
            foreach (var s in other.ProfileShares) Take(s.Key, s.Value);
            Dictionary<string, double> total = new();
            foreach (var kv in shares.Values.SelectMany(s => s))
            {
                total[kv.Key] = total.GetValueOrDefault(kv.Key) + kv.Value;
            }
            SetCounters(total);
            shares.Remove(me);
            ProfileShares = shares;

            RankPoints = Math.Clamp(Math.Max(RankPoints, other.RankPoints), 0, RankThresholds[4] - 1);
            RankLevel = RankThresholds.Count(t => RankPoints >= t);
            WifiBestWinStreak = Math.Max(WifiBestWinStreak, other.WifiBestWinStreak);
            WifiBestKillStreak = Math.Max(WifiBestKillStreak, other.WifiBestKillStreak);
            WirelessBestWinStreak = Math.Max(WirelessBestWinStreak, other.WirelessBestWinStreak);
            WirelessBestKillStreak = Math.Max(WirelessBestKillStreak, other.WirelessBestKillStreak);
            Arenas |= other.Arenas;
            Hunters |= other.Hunters;
            BeatGame |= other.BeatGame;
            Completed |= other.Completed;
            NoticeSeen |= other.NoticeSeen;
            bool theirs = other.SavedAt > SavedAt;
            if (theirs && other.Name != MultiplayerMenu.DefaultNickname) Name = other.Name;
            foreach (var kv in other.SkinChoice ?? new())
            {
                if (theirs || !SkinChoice.ContainsKey(kv.Key)) SkinChoice[kv.Key] = kv.Value;
            }

            // the favourites from the merged counts (as UndoImport works them out)
            if (HunterMatches.Any(n => n > 0)) FavoriteHunter = TopOwnHunter(); // own counts only: a character's matches aren't its base hunter's
            if (ModeMatches.Any(n => n > 0)) FavoriteMode = Array.IndexOf(ModeMatches, ModeMatches.Max());
            if (WeaponKills.Any(n => n > 0)) FavoriteWeapon = Array.IndexOf(WeaponKills, WeaponKills.Max());
            long Ratio(int room) => ((long)ArenaWins.GetValueOrDefault(room) << 12) / (ArenaMatches.GetValueOrDefault(room) + 1);
            if (ArenaMatches.Count > 0)
            {
                LuckyArena = FirstArenaRoom;
                foreach (int room in ArenaMatches.Keys.Order())
                {
                    if (room is >= FirstArenaRoom and <= LastArenaRoom && Ratio(room) > Ratio(LuckyArena)) LuckyArena = room;
                }
            }
            PickChoice(reset: false);
            ClampChoice();
            return $"profile: merged a license from device {other.ProfileDevice}: rank {RankLevel} ({pointsBefore} -> {RankPoints} pts), "
                + $"matches {matchesBefore} -> {Matches}, name {Name}";

            static int[] Sized(int[]? a, int n) => a?.Length == n ? a : (a ?? Array.Empty<int>()).Concat(new int[n]).Take(n).ToArray();
        }
    }
}
