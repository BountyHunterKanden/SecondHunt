using MphRead;
using MphRead.Entities;
using MphRecomp.Render;

namespace MphRecomp.App;

// Brawl Zero Suit Samus's army crawl (MphRead PlayerCrawl.cs, docs/ZERO_SUIT_SAMUS.md section 7): a seat that crawls
// (PlayerEntity.HostCrawl, set by every peer from the seat) wearing her Brawl body draws its crouch clips where MphRead
// would draw her ball, in its place (PlayerEntity.HostOwnAlt). Drawing only.
internal sealed partial class CampaignRenderer
{
    // the seat's Brawl body when it crawls and has her crawl clips, or null
    CampaignRetroBody? CrawlOf(PlayerEntity p) => p.HostCrawl && BodyOf(p) is CampaignRetroBody b && b.HasCrawl ? b : null;

    // TickSkins: her crawl's clip clock (it resets while she stands)
    void TickCrawl(PlayerEntity p) => CrawlOf(p)?.TickCrawl(p);

    // DrawSkins, for a seat in its alt form (its tint and double damage already on the body): true when her crawl drew
    bool DrawCrawl(PlayerEntity p, Scene scene, SuitPass pass)
        => CrawlOf(p) is CampaignRetroBody b && b.DrawCrawl(p, SmoothView(p, scene), scene.PerspectiveMatrix, pass, p.CurAlpha, p.HostDamageFlash);
}
