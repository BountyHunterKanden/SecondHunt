using MphRead;
using MphRecomp.Config;
using MphRecomp.Frontend;

namespace MphRecomp.App;

// The pause's LOOK > HUD > CUSTOMIZE HUD (owner 2026-11-15, wave 2): the HUD editor (Hud/HudEditor.cs) runs inside the
// pause overlay, over the paused game, so every host's input already reaches it. While it is up the menu stays open (the
// game stays paused) but draws nothing and takes no input; the editor draws with the menu's own drawer and font cache.
internal sealed partial class PauseOverlay
{
    // the renderer's custom HUD state (CampaignRenderer.HudLayout.cs sets it): what the editor edits
    public HudLayout? HudHost { get; set; }

    HudEditor? _hudEditor;
    public bool HudEditing => _hudEditor != null;

    void OpenHudEditor()
    {
        if (HudHost == null || _session == null)
        {
            Log.Warn("MPHHud", "editor: no HUD to edit here");
            return;
        }
        HudLayout layout = HudHost;
        _hudEditor = new HudEditor(layout, _session.DrawList.Textures, save =>
        {
            if (save)
            {
                RecompSettings settings = Settings;
                if (layout.Edits.Count == 0) settings.HudLayouts.Remove(HudLayout.AllProfile);
                else settings.HudLayouts[HudLayout.AllProfile] = HudLayout.Clone(layout.Edits);
                settings.Save(_settingsPath);
                Log.Info("MPHHud", "saved " + layout.Edits.Count + " HUD edit(s)");
            }
            _hudEditor = null;
            _last = _clock.Elapsed.TotalSeconds; // (no jump in the menu's animations)
        });
    }

    // the pause closed under the editor (the host resumed): its changes are dropped, as CANCEL would
    void EndHudEditor()
    {
        if (_hudEditor == null) return;
        HudEditor editor = _hudEditor;
        _hudEditor = null;
        editor.Press(MenuKeys.B);
    }

    void DrawHudEditor(int width, int height) => _hudEditor?.Draw(_drawer, width, height);
}
