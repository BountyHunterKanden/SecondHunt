using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp dev (release test checklist "Trooper standing body hitbox", owner 2026-10-10: "can we make a dev option in
    // the launcher to be able to damage the players own trooper and test this"): in the Solo arena, with the launcher's
    // "Hit your own Trooper body" on, the Scout Drone's shots (and any of the drone's) hit his own standing body, as an
    // enemy's would (BeamProjectileEntity), so chest and head hits can be checked alone. Each hit is noted for the solo
    // status line: where on the body it landed and the damage it took. Off unless the app sets it (never in a real match).
    public partial class PlayerEntity
    {
        public static bool HostSelfBodyHits { get; set; }

        public int BodyHits { get; private set; }
        public int BodyHeadHits { get; private set; } // landed in MPH's head band (the top 0.3 of his height)
        public string BodyLastHit { get; private set; } = "";

        // BeamProjectileEntity, a shot hitting this player's standing body (Halfturret): at = the contact point
        public void HostBodyHitNote(Vector3 at, BeamType beam)
        {
            float height = at.Y - Halfturret.Position.Y;
            bool head = height >= Fixed.ToFloat(Values.MaxPickupHeight) - 0.3f;
            BodyHits++;
            if (head)
            {
                BodyHeadHits++;
            }
            BodyLastHit = $"{(head ? "HEAD" : height >= Fixed.ToFloat(Values.MaxPickupHeight) * 0.45f ? "CHEST" : "LEGS")} {height:0.00} ({beam})";
        }
    }
}
