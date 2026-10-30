using System;
using System.Linq;
using System.Runtime.CompilerServices;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Sound;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -campaignportal [room=UNIT1_LAND] [teleporterId=25] [tp]: switch a cross-room teleporter on, walk Samus onto it and
    // follow the full room load (fade out, RoomEntity.LoadRoom with a NEW main player, fade in) the way the Android
    // renderer sees it after every step: fade, camera, view matrix, draw list. "tp" also runs CampaignActivity's
    // third-person camera math after every step (copied from UpdateThirdPerson) and feeds its offset back like the app.
    // "weapon=<BeamType>" (owner queue #8b): equips that weapon before the portal and checks the new player holds it
    // after the full room load (CampaignHost.CarryWeapon), e.g. -campaignportal UNIT1_LAND 25 weapon=Judicator.
    internal static partial class CampaignSim
    {
        public static void Portal(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            bool tp = args.Contains("tp");
            bool sfx = args.Contains("sfx"); // trace teleporter/door sounds with the scene's frame count (owner queue #28)
            string? weaponArg = args.FirstOrDefault(a => a.StartsWith("weapon="))?.Substring(7);
            BeamType weapon = weaponArg != null ? Enum.Parse<BeamType>(weaponArg) : BeamType.None;
            args = args.Where(a => a != "tp" && a != "sfx" && !a.StartsWith("weapon=")).ToArray();
            string room = args.Length >= 2 ? args[1] : "UNIT1_LAND";
            int teleId = args.Length >= 3 ? Int32.Parse(args[2]) : 25;
            using CampaignHost host = CampaignHost.Start(room, setupSave: s => GiveAll(s, 0), collectDrawItems: true,
                viewWidth: 1920, viewHeight: 1080, arriving: false);
            Scene scene = host.Scene;
            float tpDist = Single.MaxValue;
            int frame = 0;
            var trace = new SfxTrace { Where = () => $"f{frame,5} room {scene.RoomId} scene frame {scene.FrameCount}" };
            void Step(CampaignInput input)
            {
                if (weapon != BeamType.None && input.SelectWeapon == BeamType.PowerBeam)
                {
                    input.SelectWeapon = BeamType.None; // default(CampaignInput) holds the Power Beam's key (BeamType 0)
                }
                if (sfx && Sfx.Instance != trace)
                {
                    Sfx.SetHost(trace);
                }
                host.Step(input);
                frame++;
                if (tp && !host.Ended)
                {
                    ThirdPerson(host, ref tpDist);
                }
            }
            // past the room's own intro camera sequence (Alinos Gateway's first visit has one)
            for (int i = 0; i < 3000 && (i < 60 || CameraSequence.Current != null); i++)
            {
                Step(default);
            }
            TeleporterEntity? tele = null;
            foreach (EntityBase e in scene.Entities)
            {
                if (e is TeleporterEntity t && t.Id == teleId)
                {
                    tele = t;
                }
            }
            if (tele == null)
            {
                Console.WriteLine($"  FAIL: no teleporter #{teleId} in {room}");
                return;
            }
            tele.Active = true;
            if (weapon != BeamType.None)
            {
                for (int i = 0; i < 40; i++)
                {
                    Step(new CampaignInput { SelectWeapon = i < 2 ? weapon : BeamType.None });
                }
                Console.WriteLine($"  equipped {host.Player.CurrentWeapon} before the portal (asked for {weapon})");
            }
            Vector3 center = tele.Position;
            var away = new Vector3(tele.FacingVector.X, 0, tele.FacingVector.Z);
            away = away.LengthSquared > 0.01f ? away.Normalized() : Vector3.UnitZ;
            // TeleporterEntity arms a player's slot only once they've been near (radius 7) without touching it, then
            // fires when the player's sphere sweeps through its 1-1.5 radius cylinder: stand off, then drop onto the pad
            host.PlacePlayer(center + away * 3 + Vector3.UnitY * 0.5f, -away);
            for (int i = 0; i < 20; i++)
            {
                Step(default);
            }
            host.PlacePlayer(center + Vector3.UnitY * 1.0f, -away);
            Console.WriteLine($"  {room}: teleporter #{teleId} at {center}, dropped onto it at {host.Player.Position} (third person {(tp ? "ON" : "off")})");
            int startRoom = scene.RoomId;
            object firstPlayer = host.Player;
            int f = 0, changedAt = -1;
            for (; f < 900 && !host.Ended; f++)
            {
                bool walking = changedAt == -1 && !GameState.InRoomTransition && scene.FadeType == FadeType.None;
                var input = new CampaignInput();
                if (walking)
                {
                    input.Move = new System.Numerics.Vector2(0, 1);
                    input.AimDelta = AimAt(host.Player, center + Vector3.UnitY);
                }
                Step(input);
                if (changedAt == -1 && scene.RoomId != startRoom)
                {
                    changedAt = f;
                }
                bool busy = GameState.InRoomTransition || scene.FadeType != FadeType.None;
                if (busy || (changedAt != -1 && (f - changedAt < 6 || (f - changedAt) % 30 == 0)))
                {
                    PortalLine(host, f, firstPlayer);
                }
                if (changedAt != -1 && f - changedAt >= 300)
                {
                    break;
                }
            }
            if (changedAt == -1)
            {
                Console.WriteLine($"  FAIL: still in room {scene.RoomId} after {f} frames (player at {host.Player.Position})");
                return;
            }
            PlayerEntity p = host.Player;
            int items = scene.OpaqueItems.Count + scene.DecalItems.Count + scene.TranslucentItems.Count;
            bool ok = Finite(scene.ViewMatrix) && scene.HostFadeAmount == 0 && items > 0 && scene.CameraMode == CameraMode.Player;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}: room {startRoom} -> {scene.RoomId}, 300 frames later: fade {scene.HostFadeAmount:0.00}, "
                + $"view finite {Finite(scene.ViewMatrix)}, items {items}, camera {scene.CameraMode}, new player {!ReferenceEquals(p, firstPlayer)}");
            if (weapon != BeamType.None)
            {
                bool kept = p.CurrentWeapon == weapon && !ReferenceEquals(p, firstPlayer);
                Console.WriteLine($"  {(kept ? "PASS" : "FAIL")}: weapon after the full room load {p.CurrentWeapon} (wanted {weapon}); "
                    + $"carry {host.LastWeaponCarry?.ToString() ?? "none"}");
            }
        }

        private static void PortalLine(CampaignHost host, int f, object firstPlayer)
        {
            Scene scene = host.Scene;
            PlayerEntity p = host.Player;
            int items = scene.OpaqueItems.Count + scene.DecalItems.Count + scene.TranslucentItems.Count;
            Console.WriteLine($"  f{f,4} room {scene.RoomId} {GameState.TransitionState,-7} fade {scene.FadeType,-12} {scene.HostFadeAmount:0.00} "
                + $"cam {scene.CameraMode} player#{RuntimeHelpers.GetHashCode(p) % 10000:0000}{(ReferenceEquals(p, firstPlayer) ? "" : "(new)")} "
                + $"hp {p.Health} weapon {p.CurrentWeapon} flags {p.LoadFlags} pos {Fmt(p.Position)} camPos {Fmt(p.CameraInfo.Position)} "
                + $"camView finite {Finite(p.CameraInfo.ViewMatrix)} det {p.CameraInfo.ViewMatrix.Determinant:0.###} "
                + $"sceneView finite {Finite(scene.ViewMatrix)} offset {Fmt(scene.HostCameraOffset)} items {items} hud {scene.HudItemCount}");
        }

        // CampaignActivity.UpdateThirdPerson (constants TpBack 2.4, TpUp 0.35, TpSide 0.5, TpMargin 0.3), host-side only
        private static void ThirdPerson(CampaignHost host, ref float tpDist)
        {
            const float TpBack = 2.4f, TpUp = 0.35f, TpSide = 0.5f, TpMargin = 0.3f;
            Scene scene = host.Scene;
            PlayerEntity p = host.Player;
            if (p.IsAltForm || CameraSequence.Current != null)
            {
                tpDist = 0;
                scene.HostCameraOffset = Vector3.Zero;
                return;
            }
            if (!(MathF.Abs(p.CameraInfo.ViewMatrix.Determinant) > 1e-3f))
            {
                tpDist = Single.MaxValue;
                scene.HostCameraOffset = Vector3.Zero;
                return;
            }
            Matrix4 cam = p.CameraInfo.ViewMatrix.Inverted();
            Vector3 eye = cam.Row3.Xyz;
            Vector3 want = cam.Row2.Xyz.Normalized() * TpBack + cam.Row1.Xyz.Normalized() * TpUp + cam.Row0.Xyz.Normalized() * TpSide;
            float len = want.Length;
            Vector3 dir = want / len;
            float allowed = len;
            CollisionResult res = default;
            if (CollisionDetection.CheckBetweenPoints(eye, eye + dir * (len + TpMargin), TestFlags.Players, scene, ref res))
            {
                allowed = Math.Clamp(res.Distance * (len + TpMargin) - TpMargin, 0, len);
            }
            tpDist = allowed < tpDist ? allowed : tpDist + (allowed - tpDist) * 0.15f;
            scene.HostCameraOffset = dir * tpDist;
        }

        private static bool Finite(Matrix4 m)
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (!Single.IsFinite(m[r, c]))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

    }
}
