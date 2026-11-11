using System;
using System.Collections.Generic;

namespace MphRecomp.App;

// A mod's error on screen (docs/MOD_API.md §4.12): LiveMods collects "MOD <id>: <what>" lines as mods fail, and the
// status line shows the newest for a few seconds on top of whatever it says. The MODS page lists them too.
internal sealed partial class CampaignRenderer
{
    const long ModToastMs = 6000;
    string? _modToast;
    long _modToastUntil;

    // once a frame: mod.save flushes / PC hot reload, and any new mod errors
    void PollMods()
    {
        MphRecomp.Mods.LiveMods.Poll();
        List<string> toasts = MphRecomp.Mods.LiveMods.TakeToasts();
        if (toasts.Count == 0) return;
        _modToast = toasts.Count == 1 ? toasts[0] : $"{toasts[^1]} (+{toasts.Count - 1} more on the MODS page)";
        _modToastUntil = Environment.TickCount64 + ModToastMs;
        _statusCountdown = 0; // show it now
    }

    string WithModToast(string status) => _modToast != null && Environment.TickCount64 < _modToastUntil
        ? _modToast + (status.Length > 0 ? "\n" + status : "") : status;
}
