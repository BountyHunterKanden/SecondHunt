using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Echoes arenas' static actor models (T6 N3, owner 2026-10-10 "invisible map objects in spires"): actors whose solid
    // shape is in the area's collision but whose model isn't in its render mesh (Spires' pillars). The importer places each
    // in its own arena.bin pass (EchoesArena.Info.Statics); the device draws them every frame with Echoes' lighting at their
    // centre (CampaignArena.DrawCrates). Draw-only: the collision is the area's, as before.
    public static class EchoesStatics
    {
        static readonly Dictionary<int, (int Pass, CrateLight? Light, EchoesArena.StaticSpot? Spin)[]> _rooms = new();

        // an arena registered (info) or gone (null)
        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_rooms)
            {
                if (info?.Statics is not { Count: > 0 } statics)
                {
                    _rooms.Remove(roomId);
                    return;
                }
                static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);
                static CrateLight? Light(EchoesArena.StaticSpot s)
                {
                    if (s.Ambient is not { Length: 3 } a || s.Lights == null) return null;
                    float[][] ls = s.Lights.Where(l => l.Length == 6).Take(4).ToArray();
                    return new CrateLight(V(a), ls.Select(l => new Vector3(l[0], l[1], l[2])).ToArray(), ls.Select(l => new Vector3(l[3], l[4], l[5])).ToArray());
                }
                _rooms[roomId] = statics.Select(s => (s.Pass, Light(s), s.SpinXf is { Length: >= 9 } && s.SpinPeriod > 0 ? s : null)).ToArray();
            }
        }

        // the room's statics (pass, lighting, the spin of one that turns); empty when it has none
        public static IReadOnlyList<(int Pass, CrateLight? Light, EchoesArena.StaticSpot? Spin)> For(int roomId)
        {
            lock (_rooms)
            {
                return _rooms.TryGetValue(roomId, out var s) ? s : System.Array.Empty<(int, CrateLight?, EchoesArena.StaticSpot?)>();
            }
        }

        // a spinning static's turn `clock` seconds in: its still place to where it has turned. Echoes' ActorRotate turns the
        // actor about its own axis (its transform times the turn), a steady loop of SpinDegrees every SpinPeriod
        public static Matrix4 Model(EchoesArena.StaticSpot s, double clock)
        {
            if (s.SpinXf is not { Length: >= 9 } xf || s.SpinPeriod <= 0) return Matrix4.Identity;
            float a = MathHelper.DegreesToRadians(s.SpinDegrees * (float)(clock / s.SpinPeriod % 1.0));
            Matrix4 turn = s.SpinAxis switch { 3 => Matrix4.CreateRotationX(a), 4 => Matrix4.CreateRotationY(a), _ => Matrix4.CreateRotationZ(a) };
            Matrix4 frame = MiningPlazaGate.Frame(xf, s.Scale);
            return frame.Inverted() * turn * frame;
        }
    }
}
