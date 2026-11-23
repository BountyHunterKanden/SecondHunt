using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // -campaigntp [room=UNIT1_RM1] [left] [match=<arena>]: the third-person camera (CampaignRenderer.UpdateThirdPerson,
    // as ThirdPerson mirrors it) against RoomEntity's draw walk and the true aim (PlayerHostAim.cs), owner 2026-11-12;
    // match= plays a bot match in that arena instead of a campaign room.
    // Doorways: for each open side-on doorway, both ways, the eye 1.2 in front of it facing away, so the camera sits
    // through it: the room part the camera is in must be drawn (HostCameraNodeRef's walk), and how many the eye's own walk
    // (the step's, which is all that was drawn before) missed. Aim: facing walls 2.5-6 away, the shot's line from the
    // muzzle to the aim point must hit the spot under the reticle (the camera's ray through it), against vanilla's miss.
    internal static partial class CampaignSim
    {
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        public static void ThirdPersonCheck(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            bool left = args.Contains("left");
            string? matchArena = args.FirstOrDefault(a => a.StartsWith("match="))?[6..];
            args = args.Where(a => a != "left" && !a.StartsWith("match=")).ToArray();
            string roomName = args.Length >= 2 ? args[1] : "UNIT1_RM1"; // (UNIT1_LAND is too open: no doorway placements or walls in range)
            CampaignHost host;
            if (matchArena != null)
            {
                // a bot match (phase 2, owner 2026-11-13: "true aim for third person for local matches too"): the local seat's
                // code goes in through CampaignInput.TrueAim as in campaign; one level 1 bot, as a match needs two seats
                RoomMetadata arena = MatchArenas.Find(matchArena) ?? throw new ArgumentException($"no arena {matchArena}");
                MatchSettings settings = MatchSettings.Quick(GameMode.Battle, arena.Name, Hunter.Samus, 1, 1);
                settings.PointGoal = 99;
                settings.TimeLimitSeconds = 3600;
                host = CampaignHost.StartMatch(settings, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080);
                roomName = $"Battle in {arena.InGameName}";
            }
            else
            {
                host = CampaignHost.Start(roomName, setupSave: s => GiveAll(s, 0), collectDrawItems: true,
                    viewWidth: 1920, viewHeight: 1080, arriving: false);
            }
            using CampaignHost _ = host;
            Scene scene = host.Scene;
            float tpDist = Single.MaxValue;
            ushort tpCode = 0;
            bool trueAim = false;
            void Step(int count)
            {
                for (int i = 0; i < count && !host.Ended; i++)
                {
                    host.Step(new CampaignInput { TrueAim = trueAim ? tpCode : (ushort)0 }); // (as the app: last step's camera)
                    tpCode = ThirdPerson(host, ref tpDist, left);
                }
            }
            for (int i = 0; i < 3000 && (i < 60 || CameraSequence.Current != null); i++)
            {
                Step(1);
            }
            Vector3 spawn = host.Player.Position;
            RoomEntity? room = null;
            foreach (EntityBase e in scene.Entities)
            {
                room ??= e as RoomEntity;
            }
            if (room == null)
            {
                Console.WriteLine($"  FAIL: no room entity in {roomName}");
                return;
            }
            var portals = (List<Portal>)typeof(RoomEntity).GetField("_portals", Priv)!.GetValue(room)!;
            var drawParts = (bool[])typeof(RoomEntity).GetField("_hostDrawParts", Priv)!.GetValue(room)!;
            var stepParts = (bool[])typeof(RoomEntity).GetField("_activeRoomParts", Priv)!.GetValue(room)!;
            FieldInfo camWalkOn = typeof(RoomEntity).GetField("_hostCamWalkOn", Priv)!;
            MethodInfo camNodeRef = typeof(RoomEntity).GetMethod("HostCameraNodeRef", Priv)!;
            Console.WriteLine($"  {roomName}: {portals.Count} doorways, camera side {(left ? "LEFT" : "RIGHT")}");

            // doorways
            int tried = 0, crossed = 0, drawn = 0, eyeWalkHad = 0;
            foreach (Portal portal in portals)
            {
                Vector3 n = portal.Plane.Xyz;
                if (!portal.Active || portal.IsForceField || MathF.Abs(n.Y) > 0.5f)
                {
                    continue;
                }
                var flat = new Vector3(n.X, 0, n.Z).Normalized();
                foreach (float s in new[] { 1f, -1f })
                {
                    tried++;
                    // the feet below the doorway's middle; the eye then 1.2 out, after the fall
                    host.PlacePlayer(portal.Position + flat * s * 1.2f, flat * s);
                    Step(40);
                    if (host.Ended || host.Player.IsAltForm)
                    {
                        continue;
                    }
                    var camRef = (NodeRef)camNodeRef.Invoke(room, null)!;
                    NodeRef eyeRef = host.Player.CameraInfo.NodeRef;
                    if (camRef.PartIndex == -1)
                    {
                        continue; // the camera stayed in the eye's part (pulled in by a wall, or the doorway's too far)
                    }
                    crossed++;
                    bool isDrawn = (bool)camWalkOn.GetValue(room)! && drawParts[camRef.PartIndex];
                    bool hadIt = stepParts[camRef.PartIndex];
                    drawn += isDrawn ? 1 : 0;
                    eyeWalkHad += hadIt ? 1 : 0;
                    if (!isDrawn || !hadIt)
                    {
                        Console.WriteLine($"    {portal.Name,-14} side {s,2}: eye part {eyeRef.PartIndex}, camera part {camRef.PartIndex} "
                            + $"drawn {(isDrawn ? "yes" : "NO")}, eye's walk had it {(hadIt ? "yes" : "no")}, camera {tpDist:0.00} back");
                    }
                }
            }
            bool doorsOk = drawn == crossed;
            Console.WriteLine($"  {(doorsOk ? "PASS" : "FAIL")}: doorways: {tried} placements, camera through the doorway in {crossed}, "
                + $"its part drawn {drawn}/{crossed} (the eye's walk alone had {eyeWalkHad}/{crossed})");

            // true aim: walls 2.5-6 from a spot in the open
            FieldInfo aimField = typeof(PlayerEntity).GetField("_aimPosition", Priv)!;
            FieldInfo muzzleField = typeof(PlayerEntity).GetField("_muzzlePos", Priv)!;
            FieldInfo gunVecField = typeof(PlayerEntity).GetField("_gunVec1", Priv)!;
            MethodInfo rayHit = typeof(PlayerEntity).GetMethod("HostAimRayHit", Priv)!;
            var spots = new List<(Vector3 Pos, Vector3 Facing)>();
            // from where the room was entered and each open doorway's middle
            foreach (Vector3 from in portals.Where(p => p.Active && !p.IsForceField).Select(p => p.Position).Prepend(spawn))
            {
                host.PlacePlayer(from, Vector3.UnitZ);
                Step(30);
                Vector3 eye = host.Player.CameraInfo.Position;
                for (int a = 0; a < 16 && spots.Count < 24; a += 1)
                {
                    float yaw = a * MathF.PI / 8;
                    var d = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
                    CollisionResult res = default;
                    if (CollisionDetection.CheckBetweenPoints(eye, eye + d * 8, TestFlags.Beams, scene, ref res)
                        && res.Distance * 8 is > 2.5f and < 6 && Vector3.Dot(res.Plane.Xyz, d) < -0.7f)
                    {
                        spots.Add((host.Player.Position, d));
                    }
                }
            }
            int aimed = 0, aimOk = 0, close = 0, gunBlocked = 0;
            float worstNew = 0, worstOld = 0, sumOld = 0, worstAngle = 0;
            foreach ((Vector3 pos, Vector3 facing) in spots)
            {
                float? errNew = null, errOld = null;
                bool isClose = false, blocked = false;
                foreach (bool on in new[] { false, true })
                {
                    trueAim = on;
                    host.PlacePlayer(pos, facing);
                    Step(30);
                    PlayerEntity p = host.Player;
                    if (host.Ended || p.IsAltForm || scene.HostCameraOffset == Vector3.Zero)
                    {
                        break;
                    }
                    Vector3 eye = p.CameraInfo.Position;
                    Vector3 cam = eye + scene.HostCameraOffset;
                    var vanilla = eye + (Vector3)gunVecField.GetValue(p)! * Fixed.ToFloat(p.Values.AimDistance);
                    var aim = (Vector3)aimField.GetValue(p)!;
                    var muzzle = (Vector3)muzzleField.GetValue(p)!;
                    // the reticle's spot: the camera's ray through vanilla's point, from the eye's depth
                    Vector3 dir = (vanilla - cam).Normalized();
                    float start = MathF.Max(0, Vector3.Dot(eye - cam, dir));
                    // both with the game's beam stops (PlayerHostAim's test: room, closed doors, force fields, enemies, hunters)
                    Vector3? spot = StopAt(rayHit, p, cam + dir * start, vanilla);
                    Vector3? shot = StopAt(rayHit, p, muzzle, muzzle + (aim - muzzle).Normalized() * 60);
                    if (spot == null || shot == null)
                    {
                        break;
                    }
                    // nearer than 1 past the eye's depth the aim is held 1 out on purpose (a shot at a spot level with the
                    // eye goes sideways): counted apart
                    isClose = (spot.Value - (cam + dir * start)).Length < 1.05f;
                    float err = (shot.Value - spot.Value).Length;
                    if (on)
                    {
                        errNew = err;
                        // still on the camera's ray through vanilla's point: the reticle didn't move
                        float angle = MathHelper.RadiansToDegrees(Vector3.CalculateAngle(aim - cam, vanilla - cam));
                        worstAngle = MathF.Max(worstAngle, angle);
                        // the aim on the reticle's spot but the gun's own line to it stopped short: a corner between the gun
                        // and the spot that the camera, out at the shoulder, sees past (as in any over-the-shoulder view;
                        // vanilla's aim the same): counted apart
                        blocked = err >= 0.15f && (aim - spot.Value).Length < 0.15f;
                        if (err >= 0.15f && !isClose)
                        {
                            Console.WriteLine($"    {(blocked ? "gun blocked" : "miss")} at {Fmt(pos)} facing {Fmt(facing)}: shot {err:0.00} off the reticle's spot, aim {(aim - spot.Value).Length:0.00} "
                                + $"from it, muzzle {(muzzle - eye).Length:0.00} from the eye, muzzle->aim {(aim - muzzle).Length:0.00}, muzzle->shot hit "
                                + $"{(shot.Value - muzzle).Length:0.00} ({ShotStopKind(scene, muzzle, shot.Value)}), spot {(spot.Value - eye).Length:0.00} "
                                + $"from the eye, camera {Fmt(scene.HostCameraOffset)}");
                        }
                    }
                    else
                    {
                        errOld = err;
                    }
                }
                if (errNew is not float en || errOld is not float eo)
                {
                    continue;
                }
                if (isClose)
                {
                    close++;
                }
                else if (blocked)
                {
                    gunBlocked++;
                }
                else
                {
                    aimed++;
                    aimOk += en < 0.15f ? 1 : 0;
                    worstNew = MathF.Max(worstNew, en);
                    worstOld = MathF.Max(worstOld, eo);
                    sumOld += eo;
                }
            }
            trueAim = false;
            bool aimPass = aimed > 0 && aimOk == aimed && worstAngle < 0.05f;
            Console.WriteLine($"  {(aimPass ? "PASS" : "FAIL")}: true aim at {aimed} walls 2.5-6 away: the shot lands on the reticle's spot "
                + $"in {aimOk}/{aimed} (worst {worstNew:0.000} off, reticle moved {worstAngle:0.000} deg at most); vanilla's aim missed it by "
                + $"{(aimed > 0 ? sumOld / aimed : 0):0.00} on average, {worstOld:0.00} at worst; {close} more within 1 of the eye "
                + $"(aim held 1 out, by design); {gunBlocked} more with a corner between the gun and the spot (aim on it, the gun's line "
                + "stopped short)");
        }

        // what a shot's stop was: the room's collision there, or something else (a door, force field, enemy, hunter)
        private static string ShotStopKind(Scene scene, Vector3 from, Vector3 stop)
        {
            Vector3 d = stop - from;
            CollisionResult res = default;
            bool room = CollisionDetection.CheckBetweenPoints(from, from + d * 1.05f, TestFlags.Beams, scene, ref res)
                && MathF.Abs(res.Distance * 1.05f - 1) < 0.02f;
            return room ? $"the room, normal {Fmt(res.Plane.Xyz)}" : "not the room";
        }

        // the nearest beam stop on p1 -> p2, by the player's own test
        private static Vector3? StopAt(MethodInfo rayHit, PlayerEntity p, Vector3 p1, Vector3 p2)
        {
            float hit = (float)rayHit.Invoke(p, new object[] { p1, p2 })!;
            return hit < 1 ? p1 + (p2 - p1) * hit : null;
        }
    }
}
