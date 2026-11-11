using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MphRead;
using MphRead.Entities;
using MphRecomp.Frontend;
using MphRecomp.Import;
using MphRecomp.Multiplayer;

namespace MphRecomp.App;

// A custom character's HUD portrait (owner 2026-11-05: Dark Samus showed Samus's picture in the target health bar): each
// seat playing one (Dark Samus, the Zero Suit, the Trooper: MatchSkins' _seatName) gets its skin's head picture
// (CharacterPictures, the one the lobby shows) in its hunter's portrait frame (HudPortraits), for PlayerHud's target box
// and scoreboard (PlayerDraw.HostPortrait). Composed on a worker once the picture is there (looked for every second until
// then: a picture not drawn yet is queued like the menus'); none = the hunter's own portrait.
internal sealed partial class CampaignRenderer
{
    readonly string?[] _seatPictureSkin = new string?[16];
    readonly Scene.HostHudSprite?[] _seatPortrait = new Scene.HostHudSprite?[16];
    readonly bool[] _seatPortraitBusy = new bool[16];
    readonly long[] _seatPortraitLook = new long[16];

    // with the seats' skins: the skin whose picture a custom character's seat shows (its own, else its character's first)
    void SetSeatPictureSkin(int i, MatchPlayer? seat)
    {
        _seatPortrait[i] = null;
        _seatPortraitLook[i] = 0;
        _seatPictureSkin[i] = null;
        if (seat == null || _seatName[i] == null) return;
        if (!Characters.IsOriginal(seat.Skin))
        {
            _seatPictureSkin[i] = seat.Skin;
            return;
        }
        Character? c = Characters.All.FirstOrDefault(c => !c.HasOriginal && c.Base == seat.Hunter
            && string.Equals(c.Id, seat.Character, StringComparison.OrdinalIgnoreCase));
        _seatPictureSkin[i] = c != null ? Characters.Skins(c, HdRoot).FirstOrDefault(s => !Characters.IsOriginal(s)) : null;
    }

    Scene.HostHudSprite? SeatPortrait(PlayerEntity p)
    {
        int i = p.SlotIndex;
        if (i < 0 || i >= _seatPictureSkin.Length || _seatPictureSkin[i] is not string skin || _gunsRoot == null) return null;
        if (_seatPortrait[i] != null || _seatPortraitBusy[i] || _frame < _seatPortraitLook[i]) return _seatPortrait[i];
        _seatPortraitLook[i] = _frame + 60;
        string dataRoot = Path.GetDirectoryName(_gunsRoot) ?? "";
        if (CharacterPictures.For(HdRoot, CharacterPictures.Dir(dataRoot), skin) is not { } pics) return null;
        _seatPortraitBusy[i] = true;
        Hunter hunter = p.Hunter;
        Task.Run(() =>
        {
            try
            {
                byte[] head = PngOut.Read(pics.Head, out int w, out int h);
                _seatPortrait[i] = HudPortraits.Compose(hunter, head, w, h);
                Log.Info("MPHCampaign", $"skins: seat {i} HUD portrait from {Path.GetFileName(pics.Head)}");
            }
            catch (Exception ex)
            {
                Log.Info("MPHCampaign", $"skins: seat {i} no HUD portrait ({ex.Message})");
                _seatPictureSkin[i] = null;
            }
            finally
            {
                _seatPortraitBusy[i] = false;
            }
        });
        return null;
    }
}
