using MphRead;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;

namespace MphRecomp.App;

// The Trooper's Scout Drone on its own Prime 1 body (Core DroneBody; docs/CHARACTERS_AND_SKINS.md D10/D22): each seat
// that can fly the drone gets its skin's drone (bodies/MP1GammaDrone with the Wave Trooper, MP1AlphaDrone otherwise),
// loaded with the native bodies (MatchSkins' _bodyLoads / _bodies) and drawn where MphRead flies the drone, in place of its
// Psycho Bit stand-in (PlayerEntity.HostOwnAlt). A drone body that isn't imported leaves the stand-in. Drawing only.
internal sealed partial class CampaignRenderer
{
    readonly string?[] _seatDrone = new string?[16];

    // ConfigureSkins: the seats that fly a drone (a Trooper seat with abilities on), and which drone each one gets
    void ConfigureDrones(MatchSettings settings)
    {
        bool abilities = Characters.AbilitiesFor(settings);
        for (int i = 0; i < _seatDrone.Length; i++)
        {
            _seatDrone[i] = abilities && i < settings.Players.Count && Characters.HasScoutDrone(settings.Players[i])
                ? DroneBody.IdFor(settings.Players[i].Skin) : null;
            if (_seatDrone[i] != null) Log.Info("MPHCampaign", $"skins: seat {i}'s Scout Drone wears {_seatDrone[i]} (if imported)");
        }
    }

    // StartSkinLoads: each distinct drone body, on a worker, with the native bodies
    void StartDroneLoads(string? bodiesRoot)
    {
        if (bodiesRoot == null) return;
        foreach (string? id in _seatDrone)
        {
            if (id == null || _bodyLoads.ContainsKey(id) || _bodies.ContainsKey(id) || !CampaignRetroBody.Imported(bodiesRoot, id)) continue;
            string bid = id;
            _bodyLoads[bid] = System.Threading.Tasks.Task.Factory.StartNew(() => CampaignRetroBody.TryLoad(bodiesRoot, bid),
                System.Threading.Tasks.TaskCreationOptions.LongRunning);
        }
    }

    // the seat's drone body, or null (not a drone seat, not imported, still loading or failed)
    CampaignRetroBody? DroneOf(PlayerEntity p)
    {
        int i = p.SlotIndex;
        if (!p.HostScoutDrone || i < 0 || i >= _seatDrone.Length || _seatDrone[i] is not string id) return null;
        return _bodies.TryGetValue(id, out CampaignRetroBody? b) && !b.Failed && DroneBody.Is(b.Body) ? b : null;
    }

    // TickSkins: the drone's clip clock while it flies
    void TickDrone(PlayerEntity p)
    {
        if (p.IsAltForm && DroneOf(p) is CampaignRetroBody d) d.TickDrone(p);
    }

    // DrawSkins, for a seat in its alt form: true when its drone body drew (or is faded out)
    bool DrawDrone(PlayerEntity p, Scene scene, SuitPass pass)
    {
        if (DroneOf(p) is not CampaignRetroBody d) return false;
        d.DrawDrone(p, scene.ViewMatrix, scene.PerspectiveMatrix, pass, DroneAlpha(scene, p), p.HostDamageFlash);
        return true;
    }

    // the local player's own drone fades out as the alt form's close camera reaches it (backing up flies its spike and
    // open blaster arms into the lens): gone at DroneFadeNear world units from its centre, whole from DroneFadeFar
    const float DroneFadeNear = 0.55f, DroneFadeFar = 1.1f;

    static float DroneAlpha(Scene scene, PlayerEntity p)
    {
        if (!p.IsMainPlayer || CameraSequence.Current != null) return 1;
        float dist = (scene.CameraPosition - p.DroneDrawCenter).Length;
        return System.Math.Clamp((dist - DroneFadeNear) / (DroneFadeFar - DroneFadeNear), 0, 1);
    }
}
