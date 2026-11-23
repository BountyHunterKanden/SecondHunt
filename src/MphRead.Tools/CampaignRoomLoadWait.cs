using System;
using System.Diagnostics;
using MphRead.Entities;
using MphRecomp.Campaign;

namespace MphRead
{
    internal static partial class CampaignSim
    {
        // Call right after each frame step of a walk through a door. The room load runs on a worker
        // (RoomEntity.ProcessTransition) while frames keep stepping. Headless frames cost ~10 us once the old room is
        // torn down, and the worker takes 30-80 ms of wall time (its Thread.Sleep(10) drain poll alone is ~15.6 ms on
        // Windows), so a fixed frame budget ran out mid-load: Scene.RoomId already the target (SetUpRoom sets it early)
        // but not visited yet, or still the start room, depending on the machine's speed. The worker only needs frames
        // to drain Scene.LoadedEntities; any other frame just races it (RoomEntity._portals is cleared/added to on the
        // worker while the player's UpdateNodeRef reads it: a NullReferenceException in the door sweep once), so this
        // waits those out and steps only to drain, holding the stick at move (through step when the caller has its own
        // per-frame logging or tracking). The caller's budget doesn't see these frames. Returns false if the load is
        // still running after 15 s (hung); a TransitionFailure or the host ending also ends the wait.
        internal static bool WaitRoomLoad(CampaignHost host, System.Numerics.Vector2 move = default,
            Action<CampaignInput>? step = null)
        {
            var load = Stopwatch.StartNew();
            while (GameState.TransitionState == TransitionState.Process && RoomEntity.TransitionFailure == null
                && !host.Ended)
            {
                if (load.ElapsedMilliseconds >= 15000)
                {
                    return false;
                }
                if (host.Scene.LoadedEntities.IsEmpty)
                {
                    System.Threading.Thread.Sleep(1);
                }
                else if (step != null)
                {
                    step(new CampaignInput { Move = move });
                }
                else
                {
                    host.Step(new CampaignInput { Move = move });
                }
            }
            return true;
        }
    }
}
