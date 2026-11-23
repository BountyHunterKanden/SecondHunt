using System.Collections.Generic;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Brawl Zero Suit Samus's army crawl (Core BrawlZeroSuitCrawl; the sim's MphRead PlayerCrawl.cs): one clip clock per seat,
// stepped after each sim step, drawn in the ball's place while she crawls (CampaignRenderer's MatchCrawl.cs)
internal sealed partial class CampaignRetroBody
{
    readonly Dictionary<PlayerEntity, BrawlZeroSuitCrawl.Animator> _crawls = new();

    public bool HasCrawl => BrawlZeroSuitCrawl.Has(Body);

    public void TickCrawl(PlayerEntity p)
    {
        if (!_crawls.TryGetValue(p, out BrawlZeroSuitCrawl.Animator? a)) _crawls[p] = a = new BrawlZeroSuitCrawl.Animator(Body, Clips);
        string was = a.Playing;
        if (p.NetFrozen && p.Health > 0) return; // frozen: held, as the body's clocks (Tick)
        a.Step(p, p.HostCrawling, Dt);
        if (LogClips && a.Playing != was) Log.Info(Tag, $"body {Id}: seat {p.SlotIndex} crawl: {a.Playing}");
    }

    // alpha / pass / flash as DrawBody's; false when it has no pose yet
    public bool DrawCrawl(PlayerEntity p, Matrix4 view, Matrix4 proj, SuitPass pass, float alpha, bool flash)
    {
        if (!_crawls.TryGetValue(p, out BrawlZeroSuitCrawl.Animator? a) || a.Playing == "") return false;
        Draw(a.Clips, null, BrawlZeroSuitCrawl.ModelMatrix(Body, p), view, proj, pass, alpha, flash);
        return true;
    }
}
