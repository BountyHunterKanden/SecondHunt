using MphRead;
using MphRead.Entities;
using MphRecomp.Campaign;
using MphRecomp.Render;
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
        // the seat's double damage, as MPH's alt forms (DoubleDamageLook)
        b.DdTex = DdGlTexture(p, scene);
        if (b.DdTex != 0) DoubleDamageLook.Matrix(scene.LiveFrames, p.DoubleDamageModel.Model.Recolors[0].Textures[0].Width, b.DdMtx);
        (b.SeatTint, b.SeatTint2) = (SeatTint(p), SeatTint2(p)); // ... and its team / copy colours, as her body (MatchSkins)
        try
        {
            if (b.DrawLockjaw(p, SmoothView(p, scene), scene.PerspectiveMatrix, pass, p.HostDamageFlash, p.CurAlpha) is Vector3 core)
            {
                _lockjawOrbs.Add((Vector3.TransformPosition(core, SmoothCarry(p, scene)), p.HostSyluxBombColor ?? LockjawOrbColour)); // carried as the body (smooth frames)
            }
        }
        finally { b.DdTex = 0; (b.SeatTint, b.SeatTint2) = (Vector4.Zero, Vector4.Zero); }
        return true;
    }
}
