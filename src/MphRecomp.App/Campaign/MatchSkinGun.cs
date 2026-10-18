using System.IO;
using MphRead;
using MphRead.Entities;

namespace MphRecomp.App;

// A skin's own first-person gun (owner 2026-10-17: Prime 4 Sylux's Shock Coil, "the arm cannon slice and all the
// animations from third person into that first person perspective"): when the main seat of a match wears a skin with a gun
// of its own (<external files>/guns/<skin>/, from recipes/guns/<skin>.json: guns/MP4Sylux, cut from the Beyond body's
// forearm), CampaignGun draws it in place of that hunter's MPH gun (Metadata.HunterModels[hunter][3]: SyluxGun), as the
// Prime gun replaces SamusGun. Samus's seats keep MPH's gun in matches (DropSettingsSuitInMatch). A public build draws it
// too once imported (owner 2026-10-17; IMPORT GAMES offers it: ImportGames.OfferedGuns), while the Prime arm cannons stay held.
internal sealed partial class CampaignRenderer
{
    string? _skinGunSeen;
    bool _skinGunOn; // the main seat's gun is its skin's (else the Prime gun only ever replaces SamusGun)

    // Render, before the gun's draw is decided: the main seat's skin gun loads once its skin is known (and again if the
    // seat changes skin)
    void UpdateSkinGun()
    {
        if (_matchSettings == null || _gunsRoot == null || _host?.Player is not PlayerEntity p || p.Hunter == Hunter.Samus) return;
        int i = p.SlotIndex;
        string? id = i >= 0 && i < _seatSkin.Length ? _seatSkin[i] : null;
        if (id == _skinGunSeen) return;
        _skinGunSeen = id;
        string? dir = id != null ? Path.Combine(_gunsRoot, id) : null;
        // only a gun the import made (a public build; a dev build also takes a hand-pushed one): ImportedContent
        if (dir != null && (!File.Exists(Path.Combine(dir, "gun.bin")) || !MphRecomp.Import.ImportedContent.Usable(_gunsRoot, id!))) dir = null;
        if (dir == _gunDir) return;
        _gunDir = dir;
        _skinGunOn = dir != null;
        if (dir != null) Log.Info("MPHCampaign", $"skin gun: {id} in place of {MphGunModel}");
        StartGearLoad(gun: true);
    }

    // the MPH first-person gun model the Prime gun replaces: the main player's own (SyluxGun, ...) under a skin's gun,
    // else SamusGun (another hunter keeps MPH's gun, as before skin guns)
    string MphGunModel => _skinGunOn && _host?.Player is PlayerEntity p ? Metadata.HunterModels[p.Hunter][3] : "SamusGun";
}
