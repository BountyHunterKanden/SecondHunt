using System.Collections.Generic;
using System.Linq;
using MphRead;
using MphRead.Entities;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // Echoes' multiplayer movement in Echoes arenas only (owner 2026-10-09: "double jump in all echoes maps", water in
    // Pipeline, gravity in water, the Gravity Boost, spider ball; brief handoffs/echoes-movement-water_2026-10-09_brief.md).
    // MPH's own arenas and the campaign stay MphRead's own: MphRead's hooks (Entities/Players/PlayerEchoesMoves.cs) are
    // switched on only while an Echoes arena's room is loaded in a match that has it on.
    //   - double jump: every hunter (MphRead's SpaceJump ability, given on spawn)
    //   - water: arena.json's "water" fluids (Pipeline's flooded cave): the Gravity Boost under their surfaces, movement
    //     otherwise her own as Echoes shipped it (MphRead PlayerEchoesMoves); the device draws their surfaces and tints the
    //     view in them
    //   - a jump just after leaving the floor (still MPH's Grounded) keeps the double jump (owner 2026-10-09: it "doesn't
    //     always fire"); replays recorded before it read EchoesLedgeJump as off (LedgeJump)
    //   - no fall damage on landing from a jump pad's flight (owner 2026-10-10, N4: "eliminate fall damage from jump pads
    //     in echoes maps"; Echoes' pads never hurt): MPH skips it only for 10 frames after a pad (PadNoFall)
    // Replays recorded before it (ReplayFile format < 13) read EchoesMoves as off, so they play exactly. Network matches
    // carry no switch (null = on on every device).
    public static class EchoesMovement
    {
        // this match has it (set by MatchStart before the arena loads)
        public static bool Enabled { get; set; }

        // this match's jumps just after leaving the floor are ground jumps (set by MatchStart with Enabled)
        public static bool LedgeJump { get; set; } = true;

        // this match's bots take double-jump / gap / boost links and skip items they can't get (T7; set by MatchStart)
        public static bool BotMoves { get; set; } = true;

        // this match's landings from a jump pad's flight take no fall damage (set by MatchStart with Enabled; T6 N4)
        public static bool PadNoFall { get; set; } = true;

        // on unless the settings turn it off
        public static bool For(MatchSettings settings) => settings.EchoesMoves ?? true;

        public static bool IsArena(int roomId) => EchoesArena.All.Any(d => d.Id == roomId);

        static readonly Dictionary<int, (Vector3 Min, Vector3 Max)[]> _water = new();

        // an arena registered (info) or gone (null): its water boxes
        internal static void SetRoom(int roomId, EchoesArena.Info? info)
        {
            lock (_water)
            {
                (Vector3, Vector3)[] boxes = (info?.Fluids ?? new()).Where(f => f.Kind == "water")
                    .Select(f => (new Vector3(f.Min[0], f.Min[1], f.Min[2]), new Vector3(f.Max[0], f.Max[1], f.Max[2]))).ToArray();
                if (boxes.Length == 0) _water.Remove(roomId);
                else _water[roomId] = boxes;
            }
        }

        // SceneSetup.HostExtraEntities, on every room load (the campaign's too): MphRead's hooks on for an Echoes arena in a
        // match with it on, off for any other room
        public static IReadOnlyList<EntityBase> Extra(Scene scene, int roomId, IReadOnlyList<EntityBase> entities)
        {
            bool on = Enabled && GameState.Multiplayer && IsArena(roomId);
            PlayerEntity.HostEchoesMoves = on;
            PlayerEntity.HostEchoesLedgeJump = on && LedgeJump;
            PlayerEntity.HostEchoesPadNoFall = on && PadNoFall;
            PlayerEntity.HostBotUnstick = GameState.Multiplayer && ArenaNav.Unstick(roomId);   // Echoes bots: get unstuck
            // T7: Echoes' moves on the graph's links + items they can't get (BotMoves: old replays off)
            PlayerEntity.HostBotMoves = PlayerEntity.HostBotUnstick && BotMoves;
            PlayerEntity.HostNavLinks = PlayerEntity.HostBotMoves ? ArenaNav.Links(roomId) : null;
            lock (_water)
            {
                PlayerEntity.HostWater = on && _water.TryGetValue(roomId, out (Vector3, Vector3)[]? boxes) ? boxes : null;
            }
            // the intro's middle line (owner 2026-10-10, N8; MphRead PlayerIntroBanner): the Gravity Boost where there's water
            PlayerEntity.HostIntroBanner = !on ? null
                : PlayerEntity.HostWater != null ? "Space Jump and Gravity Boost are active!" : "Space Jump is active!";
            return entities;
        }
    }
}
