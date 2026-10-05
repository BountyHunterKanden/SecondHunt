using System;

// mph-recomp simulation core (sim side of the sim/presentation split). This is the bridge from
// "static geometry viewer" to "running game": a fixed-timestep loop drives a world of dynamic
// entities; the renderer consumes an interpolated snapshot each frame. Pure logic, no rendering
// or engine deps -> headlessly testable. Nothing here touches GLES.
namespace MphRecomp.Sim
{
    // Fixed-timestep game loop decoupled from the render frame rate. The sim advances in fixed
    // steps so gameplay is deterministic and frame-rate-independent; rendering interpolates between
    // the two most recent sim states by Alpha for smoothness. A catch-up cap prevents a long hitch
    // (e.g. the app was paused) from avalanching into a "spiral of death".
    public sealed class GameLoop
    {
        public double SecondsPerTick { get; }
        public double TickRate => 1.0 / SecondsPerTick;
        // Max fixed ticks to run in one Advance() before dropping the backlog.
        public int MaxCatchUpTicks { get; set; } = 5;
        public long TotalTicks { get; private set; }
        private double _accumulator;

        // Interpolation factor in [0,1]: how far we are between the last completed tick and the next.
        // Render entity transforms as Lerp(prev, current, Alpha).
        public float Alpha => (float)Math.Clamp(_accumulator / SecondsPerTick, 0.0, 1.0);

        public GameLoop(double tickRate)
        {
            if (tickRate <= 0 || !double.IsFinite(tickRate))
                throw new ArgumentOutOfRangeException(nameof(tickRate));
            SecondsPerTick = 1.0 / tickRate;
        }

        // Accumulate the real elapsed time and run as many fixed ticks as fit, calling `tick(dt)`
        // for each with the fixed dt. Caps catch-up at MaxCatchUpTicks and drops the residual
        // backlog beyond the cap so a pause/hitch can't avalanche. Returns how many ticks ran.
        public int Advance(double realDeltaSeconds, Action<float> tick)
        {
            if (realDeltaSeconds > 0 && double.IsFinite(realDeltaSeconds))
                _accumulator += realDeltaSeconds;
            int ran = 0;
            while (_accumulator >= SecondsPerTick && ran < MaxCatchUpTicks)
            {
                tick((float)SecondsPerTick);
                _accumulator -= SecondsPerTick;
                ran++;
                TotalTicks++;
            }
            if (ran >= MaxCatchUpTicks && _accumulator > SecondsPerTick)
                _accumulator = 0.0; // dropped backlog -> avoid spiral of death
            return ran;
        }

        public void Reset()
        {
            _accumulator = 0.0;
            TotalTicks = 0;
        }
    }
}
