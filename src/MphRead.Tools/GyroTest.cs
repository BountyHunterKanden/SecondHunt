using System;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Input;
using OpenTK.Mathematics;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace MphRead
{
    // Gyro aim checks (MphRecomp.Core Input/GyroAim.cs), PC only:
    //   -gyrotest        the math: synthetic device rotations at 200 Hz -> degrees (player space / yaw / roll, pitch,
    //                    tightening on a still hand, sensitivity, gravity tracking)
    //   -gyrotest sim    the same rotations through MphRead's own campaign sim (CampaignHost, as on the Odin): turning
    //                    the device N degrees must turn Samus's aim N degrees x sensitivity, alone and on top of the stick
    internal static class GyroTest
    {
        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
        }

        public static void Run(string[] args)
        {
            if (args.Length >= 2 && args[1] == "sim")
            {
                RunSim();
            }
            else
            {
                RunMath();
            }
            Console.WriteLine($"gyrotest: {_pass} passed, {_fail} failed");
        }

        private const float Hz = 200f;
        private static readonly NVec3 Upright = new(0, 1, 0);

        // feeds `seconds` of a constant rotation (deg/s about the screen's axes), gravity held at `up`
        private static NVec2 Rotate(GyroAim aim, NVec3 dps, float seconds, NVec3 up)
        {
            float dt = 1f / Hz;
            for (int i = 0; i < (int)MathF.Round(seconds * Hz); i++)
            {
                aim.AddAccelerometer(up * 9.81f, dt);
                aim.AddSample(dps * (MathF.PI / 180f), dt);
            }
            return aim.TakeDegrees();
        }

        private static GyroAim NewAim(GyroTurnAxis axis, float sens = 1f)
        {
            var aim = new GyroAim(new GyroAimOptions { TurnAxis = axis, Sensitivity = sens });
            aim.AddAccelerometer(Upright * 9.81f, 0f);
            return aim;
        }

        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        private static void RunMath()
        {
            Console.WriteLine("gyro math (200 Hz samples; +turn = left, +pitch = up):");
            // 1. body turn with the device upright: 90 deg/s for 1 s about the screen's vertical
            NVec2 d = Rotate(NewAim(GyroTurnAxis.Player), new NVec3(0, 90, 0), 1f, Upright);
            Check("upright, turn left 90 -> 90 (player space)", Near(d.X, 90, 0.5f) && Near(d.Y, 0, 0.01f), $"got {d.X:0.00}, {d.Y:0.00}");
            d = Rotate(NewAim(GyroTurnAxis.Player), new NVec3(0, -45, 0), 1f, Upright);
            Check("upright, turn right 45 -> -45", Near(d.X, -45, 0.5f), $"got {d.X:0.00}");

            // 2. the device held tilted back 45 degrees (screen facing up toward the face), body turns about true vertical
            var tilted = NVec3.Normalize(new NVec3(0, 1, 1));
            var bodyTurn = tilted * 90f; // world-vertical rotation seen in the screen's axes
            d = Rotate(NewAim(GyroTurnAxis.Player), bodyTurn, 1f, tilted);
            Check("tilted 45, body turn 90 -> 90 (player space)", Near(d.X, 90, 1f), $"got {d.X:0.00}");
            var yawOnly = NewAim(GyroTurnAxis.Yaw);
            yawOnly.AddAccelerometer(tilted * 9.81f, 0f);
            d = Rotate(yawOnly, bodyTurn, 1f, tilted);
            Check("tilted 45, body turn 90 -> 63.6 (yaw only loses the part on the roll axis)", Near(d.X, 63.6f, 1f), $"got {d.X:0.00}");
            // the local yaw axis still turns at full rate in player space when tilted (relax factor 1.41)
            var tiltedPlayer = NewAim(GyroTurnAxis.Player);
            tiltedPlayer.AddAccelerometer(tilted * 9.81f, 0f);
            d = Rotate(tiltedPlayer, new NVec3(0, 90, 0), 1f, tilted);
            Check("tilted 45, wrist yaw 90 -> ~90 (player space)", Near(d.X, 90, 1.5f), $"got {d.X:0.00}");

            // 3. roll (steering-wheel tilt)
            d = Rotate(NewAim(GyroTurnAxis.Player), new NVec3(0, 0, 60), 0.5f, Upright);
            Check("upright, roll 30 -> no turn (player space)", Near(d.X, 0, 0.5f), $"got {d.X:0.00}");
            d = Rotate(NewAim(GyroTurnAxis.Roll), new NVec3(0, 0, 60), 0.5f, Upright);
            Check("upright, roll left 30 -> turn left 30 (roll)", Near(d.X, 30, 0.5f), $"got {d.X:0.00}");

            // 4. pitch
            d = Rotate(NewAim(GyroTurnAxis.Player), new NVec3(60, 0, 0), 0.5f, Upright);
            Check("tilt top toward you 30 -> look up 30", Near(d.Y, 30, 0.5f) && Near(d.X, 0, 0.01f), $"got {d.X:0.00}, {d.Y:0.00}");

            // 5. sensitivity
            d = Rotate(NewAim(GyroTurnAxis.Player, 2f), new NVec3(0, 90, 0), 1f, Upright);
            Check("sensitivity 2.0: turn 90 -> 180", Near(d.X, 180, 1f), $"got {d.X:0.00}");

            // 6. a hand held still: +-0.6 deg/s jitter for 3 s must not walk the reticle
            var still = NewAim(GyroTurnAxis.Player);
            var rng = new Random(1);
            NVec2 total = NVec2.Zero;
            for (int i = 0; i < 600; i++)
            {
                var jitter = new NVec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 1.2f;
                still.AddSample(jitter * (MathF.PI / 180f), 1f / Hz);
                total += still.TakeDegrees();
            }
            Check("still hand (+-0.6 deg/s noise, 3 s) drifts < 0.1 deg", total.Length() < 0.1f, $"moved {total.Length():0.000} deg");

            // 7. slow deliberate aim is kept (only scaled down under the tightening threshold)
            d = Rotate(NewAim(GyroTurnAxis.Player), new NVec3(0, 10, 0), 2f, Upright);
            Check("slow track 10 deg/s for 2 s -> ~20", Near(d.X, 20, 0.6f), $"got {d.X:0.00}");

            // 8. gravity follows the gyro between accelerometer samples
            var track = NewAim(GyroTurnAxis.Player);
            for (int i = 0; i < 200; i++) track.AddSample(new NVec3(45, 0, 0) * (MathF.PI / 180f), 1f / Hz);
            NVec3 up = track.Up;
            Check("pitch up 45 without accel: up -> (0, .71, -.71)", Near(up.Y, 0.707f, 0.02f) && Near(up.Z, -0.707f, 0.02f),
                $"up ({up.X:0.00}, {up.Y:0.00}, {up.Z:0.00})");

            // 9. MphRead's units
            NVec2 m = GyroAim.ToMphAim(new NVec2(10, 5));
            Check("10 deg left / 5 up -> mouse (-40, -20)", m == new NVec2(-40, -20), $"got {m}");
        }

        // ------------------------------------------------------------ through MphRead's sim

        private static float Yaw(Vector3 v) => MathHelper.RadiansToDegrees(MathF.Atan2(v.X, v.Z));
        private static float Pitch(Vector3 v) => MathHelper.RadiansToDegrees(MathF.Atan2(v.Y, new Vector2(v.X, v.Z).Length));

        private static float Wrap(float a) => ((a + 540f) % 360f) - 180f;

        private static void RunSim()
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            Cheats.NoRandomEncounters = true;
            // every weapon (the Imperialist zooms), full ammo
            using CampaignHost host = CampaignHost.Start("UNIT2_LAND", arriving: false, setupSave: sv =>
            {
                sv.Weapons = 0xFF;
                sv.Ammo[0] = sv.AmmoMax[0] = 4000;
                sv.Ammo[1] = sv.AmmoMax[1] = 950;
            });
            PlayerEntity p = host.Player;
            FieldInfo gunVec = typeof(PlayerEntity).GetField("_gunVec1", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Vector3 Aim() => (Vector3)gunVec.GetValue(p)!;
            void Idle(int frames)
            {
                for (int i = 0; i < frames; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None });
            }
            // wait until the game takes aim input (an arrival camera sequence blocks it for a while)
            Idle(30); // the sequence starts on the first frames
            int waited = 30;
            while (waited < 3000 && (CameraSequence.Current?.Flags.TestFlag(CamSeqFlags.BlockInput) == true
                || p.Flags1.TestFlag(PlayerFlags1.NoAimInput)))
            {
                host.Step(new CampaignInput { SelectWeapon = BeamType.None });
                waited++;
            }
            Idle(10);
            Console.WriteLine($"  aim input live after {waited} frames (camseq {CameraSequence.Current?.Flags.ToString() ?? "none"}, "
                + $"NoAimInput {p.Flags1.TestFlag(PlayerFlags1.NoAimInput)})");
            Console.WriteLine($"campaign sim (UNIT2_LAND, 60 steps/s): Samus at {p.Position}, aim yaw {Yaw(Aim()):0.00} pitch {Pitch(Aim()):0.00}");

            // the device turns at `dps` for `seconds`; the gyro is sampled at 200 Hz and taken once per 60 Hz step, as on
            // the Odin; `stick` is the right stick held at the same time (CampaignActivity: AimSpeed 12 per unit)
            (float yaw, float pitch) Drive(GyroAim aim, NVec3 dps, float seconds, float stickX = 0, float stickY = 0)
            {
                Vector3 before = Aim();
                float sampleAcc = 0;
                for (int step = 0; step < (int)MathF.Round(seconds * 60); step++)
                {
                    sampleAcc += Hz / 60f;
                    while (sampleAcc >= 1f)
                    {
                        sampleAcc -= 1f;
                        aim.AddSample(dps * (MathF.PI / 180f), 1f / Hz);
                    }
                    NVec2 delta = GyroAim.ToMphAim(aim.TakeDegrees()) + new NVec2(stickX * 12f, stickY * 12f);
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None, AimDelta = delta });
                }
                Vector3 after = Aim();
                return (Wrap(Yaw(after) - Yaw(before)), Pitch(after) - Pitch(before));
            }

            var a = NewAim(GyroTurnAxis.Player);
            var r = Drive(a, new NVec3(0, 60, 0), 0.5f);
            Check("device turns left 30 -> Samus aims 30 left", Near(r.yaw, 30, 0.6f), $"yaw {r.yaw:0.00}");
            r = Drive(a, new NVec3(0, -60, 0), 0.5f);
            Check("device turns right 30 -> back 30 right", Near(r.yaw, -30, 0.6f), $"yaw {r.yaw:0.00}");
            r = Drive(a, new NVec3(40, 0, 0), 0.5f);
            Check("device tilts up 20 -> Samus aims 20 up", Near(r.pitch, 20, 0.6f), $"pitch {r.pitch:0.00}");
            r = Drive(a, new NVec3(-40, 0, 0), 0.5f);
            Check("device tilts down 20 -> back level", Near(r.pitch, -20, 0.6f), $"pitch {r.pitch:0.00}");
            var a2 = NewAim(GyroTurnAxis.Player, 2f);
            r = Drive(a2, new NVec3(0, 20, 0), 0.5f);
            Check("sensitivity 2.0: device 10 left -> 20", Near(r.yaw, 20, 0.6f), $"yaw {r.yaw:0.00}");
            // stick alone: MPH's own rate -- right stick half right for 0.5 s = 0.5 * 12 / 4 = 1.5 deg/frame x 30
            r = Drive(a, NVec3.Zero, 0.5f, stickX: 0.5f);
            Check("stick alone (half right, 0.5 s) -> 45 right", Near(r.yaw, -45, 0.6f), $"yaw {r.yaw:0.00}");
            // both at once: they add
            r = Drive(a, new NVec3(0, -20, 0), 0.5f, stickX: 0.5f);
            Check("stick 45 right + device 10 right -> 55 right", Near(r.yaw, -55, 0.8f), $"yaw {r.yaw:0.00}");
            r = Drive(a, new NVec3(0, 20, 0), 0.5f, stickX: 0.5f);
            Check("stick 45 right + device 10 LEFT -> 35 right (gyro corrects the stick)", Near(r.yaw, -35, 0.8f), $"yaw {r.yaw:0.00}");
            // zoomed (Imperialist-style zoom or the Power Beam's L2 zoom): MphRead scales every aim delta itself while zoomed
            // (PlayerInput.UpdateAimX/Y). Compare with focal-length scaling tan(zoomFov/2)/tan(fov/2), which keeps the
            // world under the reticle moving at the same on-screen speed per degree of wrist.
            for (int i = 0; i < 90; i++) host.Step(new CampaignInput { SelectWeapon = i < 20 ? BeamType.Imperialist : BeamType.None });
            float normalFov = p.CameraInfo.Fov;
            // Zoom toggles on press; the FOV then eases in over several frames
            for (int i = 0; i < 60; i++) host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 2 ? CampaignButtons.Zoom : CampaignButtons.None });
            float zoomFov = p.CameraInfo.Fov;
            float ideal = 10f * MathF.Tan(MathHelper.DegreesToRadians(zoomFov / 2)) / MathF.Tan(MathHelper.DegreesToRadians(normalFov / 2));
            float ZoomedTurn(bool corrected)
            {
                Vector3 z0 = Aim();
                for (int step = 0; step < 30; step++)
                {
                    for (int s = 0; s < 3; s++) a.AddSample(new NVec3(0, 20, 0) * (MathF.PI / 180f), 1f / 180f); // 20 deg/s, 0.5 s
                    NVec2 deg = a.TakeDegrees() * (corrected ? GyroMph.ZoomScale(p) : 1f);
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None, AimDelta = GyroAim.ToMphAim(deg) });
                }
                return Wrap(Yaw(Aim()) - Yaw(z0));
            }
            float raw = ZoomedTurn(false);
            Console.WriteLine($"  info  zoom ({p.CurrentWeapon}): fov {normalFov:0.0} -> {zoomFov:0.0}; MphRead's own zoom scaling alone: "
                + $"device 10 left -> {raw:0.00} (x{raw / 10:0.000})");
            float fixedTurn = ZoomedTurn(true);
            Check($"zoomed, device 10 left -> {ideal:0.00} (focal-length scaling, x{ideal / 10:0.000})", p.EquipInfo.Zoomed && Near(fixedTurn, ideal, 0.1f),
                $"yaw {fixedTurn:0.00}");
            for (int i = 0; i < 60; i++) host.Step(new CampaignInput { SelectWeapon = i < 2 ? BeamType.PowerBeam : BeamType.None,
                Buttons = i < 2 ? CampaignButtons.Zoom : CampaignButtons.None }); // unzoom, back to the Power Beam
            // ZOOM STEP (owner 2026-10-17, PlayerInput.HostZoomStepPresses): vanilla's two zooms, the second (the target zoom)
            // only when asked. zoom = first zoom -> target zoom when one is ready (else out) -> out; zoom in takes the target
            // zoom only when it's ready; zoom out steps back; the target leaving the cone = back to the first zoom, no auto
            // zoom after
            Action? pin = null; // holds the target in place before every step (below)
            // keeps the aim on a moving target (the enemy walks about) while a step has no aim input of its own, as a player
            // tracks it; the rates per unit of aim input are Face's unzoomed ones, slowed by the zoom's aim factor
            Func<Vector3>? track = null;
            float trackRx = 0, trackRy = 0;
            void Step(CampaignInput input)
            {
                pin?.Invoke();
                if (track != null && input.AimDelta == default && trackRx != 0 && trackRy != 0)
                {
                    Vector3 dir = track();
                    float half = p.CameraInfo.Fov / 2, slow = 1 - 0.7f * Math.Clamp((39 - half) / 27, 0, 1);
                    input.AimDelta = new NVec2(0.3f * Wrap(Yaw(dir) - Yaw(Aim())) / (trackRx * slow),
                        0.3f * (Pitch(dir) - Pitch(Aim())) / (trackRy * slow));
                }
                host.Step(input);
                for (int d = 0; d < 600 && host.DialogPaused; d++) // a message box (the room's area messages): closed
                {
                    host.Step(new CampaignInput { SelectWeapon = BeamType.None, DialogButton = d % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance });
                }
            }
            float StepFov(CampaignButtons press, BeamType weapon = BeamType.None, bool step = true)
            {
                for (int i = 0; i < 60; i++) Step(new CampaignInput { SelectWeapon = weapon,
                    Buttons = (step ? CampaignButtons.ZoomStep : CampaignButtons.None) | (i < 2 ? press : CampaignButtons.None) });
                return p.CameraInfo.Fov;
            }
            StepFov(CampaignButtons.None, BeamType.Imperialist);
            float stepNormal = p.CameraInfo.Fov;
            float tier1 = 2 * Fixed.ToFloat(p.EquipInfo.Weapon.ZoomFov);
            FieldInfo facingVec = typeof(PlayerEntity).GetField("_facingVector", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Vector3 Facing() => (Vector3)facingVec.GetValue(p)!; // the facing the game's zoom check uses
            Vector3 spawnAim = Aim(), spawnPos = p.Position;
            // turns the aim onto `dir` with aim input, as a player would (a teleport's facing doesn't hold: the game eases the
            // facing back toward the aim), then lets the facing settle
            void Face(Vector3 dir, bool step = true)
            {
                CampaignButtons hold = step ? CampaignButtons.ZoomStep : CampaignButtons.None;
                Func<Vector3>? tracking = track;
                track = null; // (its own aim input only)
                // degrees of aim per unit of AimDelta (signed): 8 steps of input, then 8 to let it land
                float Probe(NVec2 delta, bool yaw)
                {
                    Vector3 before = Aim();
                    for (int i = 0; i < 8; i++) Step(new CampaignInput { SelectWeapon = BeamType.None, AimDelta = delta, Buttons = hold });
                    for (int i = 0; i < 8; i++) Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = hold });
                    return (yaw ? Wrap(Yaw(Aim()) - Yaw(before)) : Pitch(Aim()) - Pitch(before)) / (8 * (yaw ? delta.X : delta.Y));
                }
                float rx = Probe(new NVec2(1, 0), true), ry = Probe(new NVec2(0, 1), false);
                for (int round = 0; round < 12; round++)
                {
                    float dy = Wrap(Yaw(dir) - Yaw(Aim())), dp = Pitch(dir) - Pitch(Aim());
                    if (MathF.Abs(dy) < 0.02f && MathF.Abs(dp) < 0.02f) break;
                    // the error spread over 10 steps, then 10 to land
                    var delta = new NVec2(rx != 0 ? dy / rx / 10 : 0, ry != 0 ? dp / ry / 10 : 0);
                    Vector3 before = Aim();
                    for (int i = 0; i < 10; i++) Step(new CampaignInput { SelectWeapon = BeamType.None, AimDelta = delta, Buttons = hold });
                    for (int i = 0; i < 10; i++) Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = hold });
                    // the rates re-measured from what this round did (the aim isn't linear over big turns)
                    float my = Wrap(Yaw(Aim()) - Yaw(before)), mp = Pitch(Aim()) - Pitch(before);
                    if (MathF.Abs(delta.X) > 1e-4f && MathF.Abs(my) > 0.05f) rx = my / (10 * delta.X);
                    if (MathF.Abs(delta.Y) > 1e-4f && MathF.Abs(mp) > 0.05f) ry = mp / (10 * delta.Y);
                }
                for (int i = 0; i < 40; i++) Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = hold });
                if (!p.EquipInfo.Zoomed) { trackRx = rx; trackRy = ry; }
                track = tracking;
            }
            // ZOOM STEP's reticle cue (PlayerZoomReady.cs): faint in the first zoom until the target zoom is ready
            PlayerEntity.HostZoomReadyIcon = true;
            MethodInfo reticleAlpha = typeof(PlayerEntity).GetMethod("ZoomReadyReticleAlpha", BindingFlags.NonPublic | BindingFlags.Instance)!;
            float RetAlpha() => (float)reticleAlpha.Invoke(p, null)!;
            float f = StepFov(CampaignButtons.ZoomIn);
            Check("zoom step: zoom in while not zoomed does nothing", Near(f, stepNormal, 0.05f) && !p.EquipInfo.Zoomed, $"fov {f:0.00}");
            f = StepFov(CampaignButtons.Zoom);
            Check($"zoom step: 1st zoom press -> first zoom ({tier1:0.0})", Near(f, tier1, 0.05f), $"fov {f:0.00}");
            bool readyHere = p.HostZoomTargetReady;
            f = StepFov(CampaignButtons.ZoomIn);
            Check("zoom step: no target in sight -> zoom in refused (first zoom)", !readyHere && Near(f, tier1, 0.05f),
                $"ready {readyHere}, fov {f:0.00}");
            Check("zoom step: first zoom, no target -> reticle faint (0.4)", Near(RetAlpha(), 0.4f, 0.001f), $"alpha {RetAlpha():0.00}");
            f = StepFov(CampaignButtons.Zoom);
            Check($"zoom step: 2nd press, no target -> normal ({stepNormal:0.0})", Near(f, stepNormal, 0.05f) && !p.EquipInfo.Zoomed,
                $"fov {f:0.00}");
            Check("zoom step: unzoomed -> reticle solid", Near(RetAlpha(), 1, 0.001f), $"alpha {RetAlpha():0.00}");
            // a target: the farthest targetable enemy / player / weapon-zoom object, faced from where Samus stands
            EntityBase? target = null;
            float targetDist = 0;
            foreach (EntityBase e in host.Scene.Entities)
            {
                if (e == p || !e.GetTargetable() || e.Type is not (EntityType.EnemyInstance or EntityType.Player or EntityType.Object))
                {
                    continue;
                }
                if (e is ObjectEntity obj && !obj.Data.EffectFlags.TestFlag(ObjEffFlags.WeaponZoom))
                {
                    continue;
                }
                e.GetPosition(out Vector3 at);
                float d = (at - p.Position).Length;
                if (d > targetDist && d < 200)
                {
                    target = e;
                    targetDist = d;
                }
            }
            // the room's only target (an enemy that stays put) is nearer than a target zoom reaches (vanilla zooms past the
            // first zoom only beyond 3 / tan 12 = 14.1 units): Samus is backed off from it, on her spawn side, to a spot where
            // she stands (the room has pits), and kept at full health
            Vector3 home = p.Position;
            if (target != null && targetDist < 16)
            {
                pin = () => p.Health = p.HealthMax;
                target.GetPosition(out Vector3 at);
                Vector3 away = (home - at).WithY(0).Normalized();
                bool placed = false;
                foreach (int dist in new[] { 20, 18, 16 })
                {
                    foreach (int deg in new[] { 0, 20, -20, 40, -40 })
                    {
                        if (placed) break;
                        float rad = MathHelper.DegreesToRadians(deg);
                        var dir = new Vector3(away.X * MathF.Cos(rad) - away.Z * MathF.Sin(rad), 0, away.X * MathF.Sin(rad) + away.Z * MathF.Cos(rad));
                        Vector3 spot = new Vector3(at.X, home.Y + 0.3f, at.Z) + dir * dist;
                        p.Teleport(spot, -dir, host.Scene.GetNodeRefContaining(spot));
                        p.PrevPosition = spot;
                        for (int k = 0; k < 40; k++) Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = CampaignButtons.ZoomStep });
                        placed = MathF.Abs(p.Position.Y - home.Y) < 1.5f && (p.Position - spot).WithY(0).Length < 1;
                    }
                }
                target.GetPosition(out at);
                targetDist = (at - p.Position).Length;
            }
            Check("zoom step: a target past the first zoom's reach (> 14.1 units)", target != null && targetDist > 15,
                target == null ? "none" : $"{target.Type} at {targetDist:0.0}, Samus at {p.Position}");
            if (target != null && targetDist > 15)
            {
                Vector3 TargetDir() { target.GetPosition(out Vector3 at); return at - p.Position; }
                float Expected() => 2 * MathHelper.RadiansToDegrees(MathF.Atan2(3, Vector3.Dot(TargetDir(), Facing())));
                Face(TargetDir());
                track = TargetDir;
                f = StepFov(CampaignButtons.Zoom);
                Check("zoom step: facing the target, 1st press -> first zoom, reticle solid", Near(f, tier1, 0.05f) && p.HostZoomTargetReady
                    && Near(RetAlpha(), 1, 0.001f), $"fov {f:0.00}, ready {p.HostZoomTargetReady}, alpha {RetAlpha():0.00}");
                Face(TargetDir());
                f = StepFov(CampaignButtons.Zoom);
                float want = Expected();
                Check($"zoom step: 2nd press -> vanilla's target zoom ({want:0.00}), reticle solid", Near(f, want, 0.1f) && f < tier1 - 1
                    && Near(RetAlpha(), 1, 0.001f), $"fov {f:0.00}, alpha {RetAlpha():0.00}");
                track = null;
                Face(new Vector3(-TargetDir().Z, 0, TargetDir().X)); // a quarter turn away: out of the cone
                f = StepFov(CampaignButtons.None);
                Check("zoom step: target leaves the cone -> back to the first zoom, reticle faint", Near(f, tier1, 0.05f) && p.EquipInfo.Zoomed
                    && Near(RetAlpha(), 0.4f, 0.001f), $"fov {f:0.00}, alpha {RetAlpha():0.00}");
                Face(TargetDir());
                track = TargetDir;
                f = StepFov(CampaignButtons.None);
                Check("zoom step: target back in sight -> no auto zoom (stays first zoom)", Near(f, tier1, 0.05f), $"fov {f:0.00}");
                Face(TargetDir());
                f = StepFov(CampaignButtons.ZoomIn);
                want = Expected();
                Check($"zoom step: zoom in -> target zoom again ({want:0.00})", Near(f, want, 0.1f), $"fov {f:0.00}");
                f = StepFov(CampaignButtons.ZoomOut);
                Check("zoom step: zoom out from the target zoom -> first zoom", Near(f, tier1, 0.05f), $"fov {f:0.00}");
                f = StepFov(CampaignButtons.ZoomOut);
                Check("zoom step: zoom out from the first zoom -> normal", Near(f, stepNormal, 0.05f) && !p.EquipInfo.Zoomed, $"fov {f:0.00}");
                // the same target with ZOOM STEP off: vanilla zooms onto it by itself, to the same depth
                Face(TargetDir(), step: false);
                f = StepFov(CampaignButtons.Zoom, step: false);
                want = Expected();
                Check($"zoom step off: vanilla auto zoom onto the target, same depth ({want:0.00})", Near(f, want, 0.1f), $"fov {f:0.00}");
                f = StepFov(CampaignButtons.Zoom, step: false);
                track = null;
                pin = null;
                p.Teleport(home, Facing(), host.Scene.GetNodeRefContaining(home));
                p.PrevPosition = home;
                Face(spawnAim, step: false);
            }
            // VANILLA zoom look draws this FOV as it is, so it must move at the ROM's speed (rev 1 arm9): in = 2 deg half per
            // 30 Hz tick = 2 full per 60 Hz frame (78 -> 24 in 27 frames); out = a quarter of the gap per tick, 0.134 per frame
            var fovs = new System.Collections.Generic.List<float>();
            for (int i = 0; i < 40; i++)
            {
                host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 2 ? CampaignButtons.Zoom : CampaignButtons.None });
                fovs.Add(p.CameraInfo.Fov);
            }
            int inFrames = fovs.FindIndex(v => v <= tier1 + 0.01f) + 1;
            Check("vanilla zoom in: 2 deg (full) per frame, 78 -> 24 in 27 frames", inFrames >= 27 && inFrames <= 28
                && Near(fovs[0] - fovs[1], 2f, 0.01f), $"{inFrames} frames, step {fovs[0] - fovs[1]:0.00}");
            fovs.Clear();
            for (int i = 0; i < 40; i++)
            {
                host.Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = i < 2 ? CampaignButtons.Zoom : CampaignButtons.None });
                fovs.Add(p.CameraInfo.Fov);
            }
            float outFrac = (fovs[2] - fovs[1]) / (stepNormal - fovs[1]);
            Check("vanilla zoom out: eases 0.134 of the gap per frame", Near(outFrac, 0.134f, 0.01f), $"x{outFrac:0.000} per frame");
            StepFov(CampaignButtons.None, BeamType.PowerBeam);
            // a still hand for 2 s
            var still = NewAim(GyroTurnAxis.Player);
            var rng = new Random(2);
            Vector3 before = Aim();
            for (int step = 0; step < 120; step++)
            {
                for (int s = 0; s < 3; s++)
                {
                    var jitter = new NVec3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 1.2f;
                    still.AddSample(jitter * (MathF.PI / 180f), 1f / Hz);
                }
                host.Step(new CampaignInput { SelectWeapon = BeamType.None, AimDelta = GyroAim.ToMphAim(still.TakeDegrees()) });
            }
            float moved = Vector3.CalculateAngle(before, Aim()) * 180f / MathF.PI;
            Check("still hand for 2 s -> reticle holds (< 0.1 deg)", moved < 0.1f, $"moved {moved:0.000} deg");
        }
    }
}
