using System;
using System.Collections.Generic;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Culling;
using MphRead.Text;
using MphRecomp.Campaign;
using OpenTK.Mathematics;

namespace MphRead
{
    // -scanreach <room> [layer] [entityId]: for every entity with a scan id, stand Samus on floor spots close to it (2.5 to
    // 23 units, 8 directions, both sides of a door), face it with the scan visor on, and record what the scan code does
    // from each spot: icon shown (in the scan target list, which needs distance < 24), scannable (current target with
    // distance < 12 and a clear camera -> target line), and the failure this sweep exists for: a clear line of sight
    // within icon range but ScanVisible() false (the thing is on screen, the scan code ignores it).
    internal static class ScanReachTest
    {
        static readonly float[] Dists = { 2.5f, 5, 8, 11, 15, 19, 23 };

        public static void Run(string[] args)
        {
            if (!ToolPaths.UseDesktopExtraction() || args.Length < 2)
            {
                Console.WriteLine("usage: -scanreach <room> [layer] [entityId]");
                return;
            }
            string room = args[1];
            int layer = args.Length >= 3 ? Int32.Parse(args[2]) : 0;
            var only = new HashSet<int>();
            for (int i = 3; i < args.Length; i++) only.Add(Int32.Parse(args[i]));
            bool verbose = Environment.GetEnvironmentVariable("SCANREACHDBG") != null;
            CampaignHost Boot()
            {
                CampaignHost h = CampaignHost.Start(room, collectDrawItems: true, viewWidth: 1920, viewHeight: 1080, setupSave: s =>
                {
                    s.BossFlags = (BossFlags)(layer * 0x15555);
                    s.Weapons = 0xFF;
                    s.Health = s.HealthMax = 799;
                });
                for (int i = 0; i < 1000 && !h.Ended; i++)
                {
                    h.Step(default);
                }
                return h;
            }
            CampaignHost host = Boot();
            int roomId = host.RoomId;
            PlayerEntity p = host.Player;
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            object? Field(string name) => typeof(PlayerEntity).GetField(name, flags)?.GetValue(p);
            static object? Member(object? o, string name) => o == null ? null
                : (object?)o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);
            void CloseDialogs()
            {
                for (int k = 0; k < 600 && host.DialogPaused; k++)
                    host.Step(new CampaignInput { DialogButton = k % 10 != 0 ? HostDialogButton.None
                        : host.Dialog == DialogType.YesNo ? HostDialogButton.No : HostDialogButton.Advance });
            }
            void VisorOn()
            {
                for (int t = 0; t < 3 && (p.IsAltForm || p.IsMorphing); t++)
                {
                    for (int i = 0; i < 40; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.Morph : CampaignButtons.None });
                }
                for (int t = 0; t < 2 && !p.ScanVisor; t++)
                {
                    for (int i = 0; i < 20; i++) host.Step(new CampaignInput { Buttons = i < 2 ? CampaignButtons.ScanVisor : CampaignButtons.None });
                    CloseDialogs();
                }
            }
            bool InTargetList(EntityBase e)
            {
                var list = (System.Collections.IList)Field("_scanTargets")!;
                int count = (int)Field("_scanTargetCount")!;
                for (int i = 0; i < count; i++)
                {
                    if (Member(list[i], "Entity") == e) return true;
                }
                return false;
            }

            // after a restart a target is found again by type, id and nearest position (the entity list's order can
            // change, and enemies move); before any restart the boot reference is used as is
            var targets = new List<(EntityType Type, int Id, Vector3 Pos, EntityBase Ent)>();
            foreach (EntityBase e in host.Scene.Entities)
            {
                int sid = e.GetScanId();
                e.GetPosition(out Vector3 ep);
                if ((only.Count == 0 || only.Contains(e.Id)) && sid > 0 && Strings.GetScanEntryCategory(sid) != 5) targets.Add((e.Type, e.Id, ep, e));
            }
            int restarts = 0;
            EntityBase? Find((EntityType Type, int Id, Vector3 Pos, EntityBase Ent) key)
            {
                if (restarts == 0) return key.Ent;
                EntityBase? best = null;
                float bestDist = key.Id == -1 ? 10f : Single.MaxValue;
                foreach (EntityBase e in host.Scene.Entities)
                {
                    if (e.Type != key.Type || e.Id != key.Id) continue;
                    e.GetPosition(out Vector3 ep);
                    float d = (ep - key.Pos).Length;
                    if (d < bestDist) { best = e; bestDist = d; }
                }
                return best;
            }
            Console.WriteLine($"SRROOM {room} L{layer} targets {targets.Count}");
            VisorOn();
            foreach (var ti in targets)
            {
                EntityBase? found = Find(ti);
                if (host.Ended) break;
                if (found == null)
                {
                    Console.WriteLine($"  {ti.Type}#{ti.Id}: not found after restart, skipped");
                    continue;
                }
                EntityBase t = found;
                t.GetPosition(out Vector3 at);
                int sid = t.GetScanId();
                Vector3 face = t.FacingVector;
                bool door = t is DoorEntity;
                // per side (doors: front = the side the door faces; others: one side)
                int[] spots = new int[2], los = new int[2], icon = new int[2], scan = new int[2], hidden = new int[2], misnode = new int[2], wrongpart = new int[2];
                float[] nearIcon = { Single.MaxValue, Single.MaxValue }, nearScan = { Single.MaxValue, Single.MaxValue };
                string?[] firstHidden = new string?[2];
                foreach (float dist in Dists)
                {
                    for (int a = 0; a < 8; a++)
                    {
                        if (host.Ended || p.Health <= 0) break;
                        float ang = a * MathF.PI / 4 + (door ? MathF.PI / 8 : 0);
                        var dir = new Vector3(MathF.Cos(ang), 0, MathF.Sin(ang));
                        Vector3 spot = at + dir * dist + new Vector3(0, 2, 0);
                        CollisionResult floor = default;
                        if (!CollisionDetection.CheckBetweenPoints(spot, spot - new Vector3(0, 14, 0), TestFlags.None, host.Scene, ref floor)
                            || floor.Plane.Y < 0.7f || floor.EntityCollision != null) // platforms and lifts move and carry her: room floors only
                        {
                            continue;
                        }
                        // a floor spot with a ceiling right over it is inside geometry or a ball tunnel
                        Vector3 stand = floor.Position + new Vector3(0, 0.05f, 0);
                        CollisionResult roof = default;
                        if (CollisionDetection.CheckBetweenPoints(stand + new Vector3(0, 0.1f, 0), stand + new Vector3(0, 2.2f, 0), TestFlags.None, host.Scene, ref roof))
                        {
                            continue;
                        }
                        host.PlacePlayer(stand, -dir);
                        for (int i = 0; i < 6; i++)
                        {
                            host.Step(default);
                        }
                        CloseDialogs();
                        if (!ReferenceEquals(host.Player, p) || host.RoomId != roomId)
                        {
                            // a portal or room-load trigger at this spot: the room reloaded (new player, new entities)
                            Console.WriteLine($"    {t.Type}#{t.Id} d{dist} a{a}: room load at {Fmt(stand)} (now room {host.RoomId}), restarting");
                            host.Dispose();
                            host = Boot();
                            restarts++;
                            p = host.Player;
                            EntityBase? again = Find(ti);
                            VisorOn();
                            if (again == null)
                            {
                                Console.WriteLine($"  {ti.Type}#{ti.Id}: not found after restart, rest of its spots skipped");
                                goto nextTarget;
                            }
                            t = again;
                            continue;
                        }
                        if (!p.ScanVisor || p.IsAltForm) VisorOn();
                        Vector3 settled = p.Position;
                        for (int i = 0; i < 10; i++) host.Step(new CampaignInput { AimDelta = AimAt(p, at) });
                        CloseDialogs();
                        if (host.Ended || p.Health <= 0) break;
                        if (p.Health < 400) p.Health = 799;
                        if ((p.Position - settled).Length > 0.5f || (p.Position - stand).Length > 3) // fell or got pushed: not a standing spot
                        {
                            if (verbose) Console.WriteLine($"    {t.Type}#{t.Id} d{dist} a{a} moved: stand {Fmt(stand)} settled {Fmt(settled)} now {Fmt(p.Position)} paused {host.DialogPaused} trans {GameState.TransitionState} camseq {CameraSequence.Current?.Name ?? "-"} esc {GameState.EscapeState} dlg {p.DialogType}");
                            continue;
                        }
                        int side = door && Vector3.Dot(p.Position - at, face) < 0 ? 1 : 0;
                        spots[side]++;
                        Vector3 cam = p.CameraInfo.Position;
                        float cd = (cam - at).Length;
                        CollisionResult discard = default;
                        // the scan code's line test: camera to the entity position pulled 1 unit toward the camera
                        // (Matrix.GetProjectedValues target), the same test that drops the current target
                        bool clear = !CollisionDetection.CheckBetweenPoints(cam, at + (cam - at).Normalized(), TestFlags.Scan, host.Scene, ref discard);
                        bool vis = t.ScanVisible();
                        bool inList = InTargetList(t);
                        bool cur = Member(Field("_curScanTarget"), "Entity") == t;
                        if (clear && cd < 24)
                        {
                            los[side]++;
                            if (!vis)
                            {
                                hidden[side]++;
                                // the bug class of door 14: the room part the thing sits in is drawn, but its NodeRef
                                // names a part that isn't
                                NodeRef posRef = host.Scene.GetNodeRefContaining(at);
                                if (posRef != NodeRef.None && posRef != t.NodeRef && host.Scene.IsNodeRefVisible(posRef))
                                {
                                    misnode[side]++;
                                }
                                firstHidden[side] ??= $"stand {Fmt(p.Position)} cam {Fmt(cam)} d {cd:0.0} node {p.NodeRef.PartIndex} ent node {t.NodeRef.PartIndex} pos node {posRef.PartIndex}";
                                // harness check: the teleport picks a part from boxes, which can be wrong where parts
                                // overlap. Walking tracks the part through portals instead. The line from the target to
                                // the camera is clear, so following its portal crossings from the target's own part gives
                                // the part the camera is really in; put her back on the spot in that part and look again
                                int usedPart = p.CameraInfo.NodeRef.PartIndex;
                                NodeRef walked = t.NodeRef.PartIndex >= 0 ? t.NodeRef : posRef;
                                if (walked.PartIndex >= 0)
                                {
                                    Vector3 prev = at;
                                    int n = (int)MathF.Ceiling(cd / 0.25f);
                                    for (int k = 1; k <= n; k++)
                                    {
                                        Vector3 next = at + (cam - at) * (k / (float)n);
                                        walked = host.Scene.Room!.UpdateNodeRef(walked, prev, next);
                                        prev = next;
                                    }
                                }
                                if (walked.PartIndex >= 0 && walked.PartIndex != usedPart)
                                {
                                    Vector3 here = p.Position;
                                    p.Teleport(here, -dir, walked);
                                    for (int i = 0; i < 10; i++) host.Step(new CampaignInput { AimDelta = AimAt(p, at) });
                                    if (ReferenceEquals(host.Player, p) && host.RoomId == roomId && t.ScanVisible() && InTargetList(t))
                                    {
                                        wrongpart[side]++;
                                    }
                                    if (verbose) Console.WriteLine($"    {t.Type}#{t.Id} line part {walked.PartIndex} (teleport picked {usedPart}) visible {t.ScanVisible()}");
                                }
                            }
                        }
                        if (inList)
                        {
                            icon[side]++;
                            nearIcon[side] = MathF.Min(nearIcon[side], cd);
                        }
                        if (cur && cd < 12)
                        {
                            scan[side]++;
                            nearScan[side] = MathF.Min(nearScan[side], cd);
                        }
                        if (verbose)
                        {
                            Console.WriteLine($"    {t.Type}#{t.Id} d{dist} a{a} side {side} stand {Fmt(p.Position)} camd {cd:0.0} clear {clear} vis {vis} list {inList} cur {cur} ({(Member(Field("_curScanTarget"), "Entity") is EntityBase ce ? $"{ce.Type}#{ce.Id} scan {ce.GetScanId()}" : "-")}) visor {p.ScanVisor} alt {p.IsAltForm}");
                        }
                    }
                }
                string name = Strings.GetScanEntry(sid)?.Value1?.Replace('\n', ' ') ?? "";
                for (int s = 0; s < (door ? 2 : 1); s++)
                {
                    string verdict = spots[s] == 0 ? "NOSPOT" : hidden[s] > 0 && wrongpart[s] == hidden[s] ? "PARTPICK" : misnode[s] > 0 ? "MISNODE" : hidden[s] > 0 ? "HIDDEN" : los[s] > 0 && icon[s] == 0 ? "NOICON"
                        : los[s] > 0 && scan[s] == 0 ? "NOSCAN" : los[s] == 0 ? "NOLOS" : "ok";
                    Console.WriteLine($"SR {room} L{layer} {t.Type}#{t.Id}{(door ? (s == 0 ? " front" : " back") : "")} scan {sid} \"{name}\" "
                        + $"spots {spots[s]} los {los[s]} icon {icon[s]} scan {scan[s]} hidden {hidden[s]} misnode {misnode[s]} wrongpart {wrongpart[s]} "
                        + $"nearIcon {(nearIcon[s] < 99 ? nearIcon[s].ToString("0.0") : "-")} nearScan {(nearScan[s] < 99 ? nearScan[s].ToString("0.0") : "-")} "
                        + $"{verdict}{(firstHidden[s] != null ? " first " + firstHidden[s] : "")}");
                }
            nextTarget:;
            }
            if (host.Ended || p.Health <= 0) Console.WriteLine($"SRROOM {room} L{layer} run ended early (health {p.Health}, ended {host.Ended})");
            Console.WriteLine($"SRDONE {room} L{layer}");
            host.Dispose();
        }

        static string Fmt(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";

        // aim delta (game mouse units) turning the camera toward a point (as CampaignScanTest.AimAt)
        static System.Numerics.Vector2 AimAt(PlayerEntity p, Vector3 target)
        {
            Vector3 dir = (target - p.CameraInfo.Position).Normalized();
            Vector3 facing = p.CameraInfo.Facing.Normalized();
            Vector3 right = Vector3.Cross(facing, Vector3.UnitY).Normalized();
            var flat = new Vector3(facing.X, 0, facing.Z).Normalized();
            float yaw = MathF.Atan2(Vector3.Dot(dir, right), Vector3.Dot(new Vector3(dir.X, 0, dir.Z), flat)) * 180 / MathF.PI;
            float pitch = (MathF.Asin(Math.Clamp(dir.Y, -1, 1)) - MathF.Asin(Math.Clamp(facing.Y, -1, 1))) * 180 / MathF.PI;
            return new System.Numerics.Vector2(Math.Clamp(yaw * 2, -40, 40), Math.Clamp(-pitch * 2, -40, 40));
        }
    }
}
