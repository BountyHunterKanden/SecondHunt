using MphRead;
using MphRead.Entities;
using MphRead.Hud;
using MphRecomp.Multiplayer.Net;

namespace MphRecomp.App;

// An online joiner whose own connection dropped mid-match (Wi-Fi off and on): "reconnecting..." in the middle of the HUD
// while NetMatchSession.Reconnecting, then "back in the match" for a moment (release test T1, 2026-10-10). Drawing only.
internal sealed partial class CampaignRenderer
{
    const float ReconnectY = 64;
    int _reconnectsShown;

    // MatchChatWheel, each step of an online match
    void ShowReconnecting(OnlineMatch m, PlayerEntity p)
    {
        NetMatchSession net = m.Net;
        if (net.Reconnecting)
        {
            p.QueueHudMessage(128, ReconnectY, Align.Center, 200, 8, new ColorRgba(0x7FFF), 1, 0.25f, 0, "reconnecting...");
        }
        else if (net.Reconnects != _reconnectsShown)
        {
            _reconnectsShown = net.Reconnects;
            p.QueueHudMessage(128, ReconnectY, Align.Center, 200, 8, new ColorRgba(0x3FEF), 1, 2f, 0, "back in the match");
        }
    }
}
