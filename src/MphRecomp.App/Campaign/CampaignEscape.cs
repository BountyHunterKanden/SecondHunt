namespace MphRecomp.App;

// Dev launcher "Escape at the ship" (owner 2026-10-26, to test EXIT SHIP being off after boarding in an escape): --es escape 1
// starts the first room with the escape already running (a long timer, the game's own start: voice, music, trigger bit), so
// the ship is right there. Boarding it ends the escape and the ship menu's EXIT SHIP is off for that visit (CampaignShip.cs).
internal sealed partial class CampaignRenderer
{
    bool _escapeAtStart;

    public void UseEscapeStart()
    {
        _escapeAtStart = true;
        Log.Info("MPHCampaign", "escape start: the escape runs from the first room (--es escape 1)");
    }

    // once, after the first room (a plain room start: CampaignRenderer.cs)'s session starts
    void StartDevEscape()
    {
        if (!_escapeAtStart)
        {
            return;
        }
        _escapeAtStart = false;
        MphRead.GameState.StartEscapeForTesting(900);
    }
}
