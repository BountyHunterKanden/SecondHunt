using System;
using System.Numerics;
using MphRecomp.Sim;

namespace MphRead
{
    // Headless regression tests for the mph-recomp sim core (MphRecomp.Sim). Run:
    // MphRead.Tools.dll -simtest  (no ROM/device). Covers the fixed-timestep loop (accumulate,
    // catch-up cap, spiral protection, alpha), determinism, entity integration, render-snapshot
    // interpolation, spawn/despawn, and zero-alloc snapshot reuse.
    internal static class SimTest
    {
        static int _pass, _fail;
        static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        }
        static bool Near(float a, float b, float e = 1e-4f) => Math.Abs(a - b) < e;

        public static void Run(string[] args)
        {
            // --- GameLoop ---
            var loop = new GameLoop(60.0);
            int ticks = loop.Advance(0.005, _ => { });            // < one tick
            Check("loop: sub-tick dt runs 0 ticks", ticks == 0);
            Check("loop: alpha accumulates (~0.3)", Near(loop.Alpha, 0.005f * 60f, 0.02f));
            ticks = loop.Advance(0.02, _ => { });                  // now crosses a tick boundary
            Check("loop: crossing boundary runs 1 tick", ticks == 1);
            Check("loop: alpha in [0,1] after tick", loop.Alpha is >= 0f and <= 1f);

            var loop2 = new GameLoop(60.0);
            int total = 0;
            for (int i = 0; i < 60; i++) total += loop2.Advance(1.0 / 60.0, _ => { });
            Check("loop: 60 frames @ 1/60 -> 60 ticks (frame-rate independent)", total == 60 && loop2.TotalTicks == 60);

            var loop3 = new GameLoop(60.0) { MaxCatchUpTicks = 5 };
            int big = loop3.Advance(1.0, _ => { });                // 1s hitch
            Check("loop: huge dt capped at MaxCatchUpTicks", big == 5);
            Check("loop: backlog dropped after cap (alpha reset)", loop3.Alpha < 1f);

            Check("loop: rejects non-positive tick rate", Throws(() => new GameLoop(0)));

            // --- entity integration + determinism ---
            var w1 = MakeWorld(out var e1);
            var w2 = MakeWorld(out var e2);
            var input = new InputState { Move = new Vector2(1, 0) };
            for (int i = 0; i < 30; i++) { w1.Tick(1f / 60f, input); w2.Tick(1f / 60f, input); }
            Check("entity: velocity integrated (x = 5 * 0.5s)",
                Near(e1.Position.X, 5f * (30f / 60f), 1e-3f) && Near(e1.Position.Y, 0f) && Near(e1.Position.Z, 0f));
            Check("sim: deterministic (identical worlds match)",
                e1.Position == e2.Position && w1.TickCount == w2.TickCount);

            // --- render snapshot interpolation (zero-alloc reuse) ---
            var w = new SimWorld();
            var mover = w.Spawn(new KinematicEntity { ModelName = "pick_health_A", Velocity = new Vector3(12, 0, 0) });
            var snap = new RenderSnapshot();
            w.Tick(1f / 60f, default);                             // prev=(0,0,0), pos=(0.2,0,0)
            w.Snapshot(0.5f, snap);
            Check("snapshot: one render item for the entity", snap.Items.Count == 1);
            Check("snapshot: interpolated halfway between ticks",
                Near(snap.Items[0].Position.X, mover.Position.X * 0.5f, 1e-4f));
            Check("snapshot: alpha=1 gives current position",
                RunSnap(w, 1f, snap) && Near(snap.Items[0].Position.X, mover.Position.X, 1e-4f));
            var before = snap.Items; w.Snapshot(0f, snap);
            Check("snapshot: reuses the same list (zero-alloc)", ReferenceEquals(before, snap.Items));

            // --- spawn timing + despawn by lifetime ---
            var w4 = new SimWorld();
            w4.Spawn(new KinematicEntity { ModelName = "x" });
            Check("spawn: queued, not visible until first tick", w4.Entities.Count == 0);
            w4.Tick(1f / 60f, default);
            Check("spawn: integrated at tick boundary", w4.Entities.Count == 1);
            w4.Spawn(new KinematicEntity { ModelName = "y", LifeSeconds = 0.1f });
            for (int i = 0; i < 12; i++) w4.Tick(1f / 60f, default); // 0.2s > 0.1s lifetime
            Check("despawn: entity removed after its lifetime", w4.Entities.Count == 1);

            Console.WriteLine($"\nSIM TESTS: {_pass} passed, {_fail} failed  =>  {(_fail == 0 ? "ALL GREEN" : "FAILURES")}");
            Environment.ExitCode = _fail == 0 ? 0 : 1;
        }

        static SimWorld MakeWorld(out KinematicEntity e)
        {
            var w = new SimWorld();
            // spawn only; the first Tick in the test loop integrates it, then it moves each tick,
            // so N ticks => N steps of movement (x = speed * N/60).
            e = w.Spawn(new KinematicEntity { ModelName = "m", Velocity = new Vector3(5, 0, 0) });
            return w;
        }

        static bool RunSnap(SimWorld w, float alpha, RenderSnapshot snap) { w.Snapshot(alpha, snap); return true; }
        static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
    }
}
