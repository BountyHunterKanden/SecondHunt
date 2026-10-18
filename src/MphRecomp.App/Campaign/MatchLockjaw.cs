using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRecomp.App;

// Beyond Sylux's Prime 4 Lockjaw (Core BeyondLockjaw; bodies/MP4SyluxLockjaw): a Sylux seat wearing the Beyond skin gets
// it, loaded with the native bodies (MatchSkins' _bodyLoads / _bodies) and drawn where MphRead draws vanilla Lockjaw, in
// its place (PlayerEntity.HostOwnAlt). Not imported: vanilla Lockjaw. Drawing only.
internal sealed partial class CampaignRenderer
{
    // the seat's Lockjaw body id (its skin's), or null
    string? LockjawId(int i) => i >= 0 && i < _seatSkin.Length ? BeyondLockjaw.IdFor(_seatSkin[i]) : null;

    // StartSkinLoads: each distinct Lockjaw body, on a worker, with the native bodies
    void StartLockjawLoads(string? bodiesRoot)
    {
        if (bodiesRoot == null) return;
        for (int i = 0; i < _seatSkin.Length; i++)
        {
            string? id = LockjawId(i);
            if (id == null || _bodyLoads.ContainsKey(id) || _bodies.ContainsKey(id) || !CampaignRetroBody.Imported(bodiesRoot, id)) continue;
            string bid = id;
            _bodyLoads[bid] = System.Threading.Tasks.Task.Factory.StartNew(() => CampaignRetroBody.TryLoad(bodiesRoot, bid),
                System.Threading.Tasks.TaskCreationOptions.LongRunning);
        }
    }

    // the seat's Lockjaw body, or null (not a Beyond Sylux seat, not imported, still loading or failed)
    CampaignRetroBody? LockjawOf(PlayerEntity p)
    {
        if (p.Hunter != Hunter.Sylux || LockjawId(p.SlotIndex) is not string id) return null;
        return _bodies.TryGetValue(id, out CampaignRetroBody? b) && !b.Failed && BeyondLockjaw.Is(b.Body) ? b : null;
    }

    // TickSkins: its clip clock while the seat is in its alt form
    void TickLockjaw(PlayerEntity p)
    {
        if (p.IsAltForm && LockjawOf(p) is CampaignRetroBody b) b.TickLockjaw(p);
    }

    // DrawSkins, for a seat in its alt form: true when its Lockjaw drew
    bool DrawLockjaw(PlayerEntity p, Scene scene, SuitPass pass)
    {
        if (LockjawOf(p) is not CampaignRetroBody b) return false;
        if (b.DrawLockjaw(p, scene.ViewMatrix, scene.PerspectiveMatrix, pass, p.HostDamageFlash) is Vector3 core)
        {
            _lockjawOrbs.Add((core, p.HostSyluxBombColor ?? LockjawOrbColour));
        }
        return true;
    }
}
