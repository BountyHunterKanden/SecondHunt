using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Formats.Collision;
using MphRead.Formats.Culling;
using MphRecomp.Replay;
using OpenTK.Mathematics;

namespace MphRead
{
    // Why another player wasn't drawn on the recording device (the host's seat 0), from a replay. Owner 10-15: online,
    // Trace "keeps disappearing for the other two players ... the game doesnt draw him for periods at a time".
    //   -tracevis <file.mphreplay> [seat=N] (default: every non-main seat)
    // Per step and seat: alive, alt form, weapon, CurAlpha (stand-still camo / cloak), room part and whether the host's
    // camera has that part active (PlayerDraw culls a non-main player whose part isn't), and whether the seat sits inside
    // the host's view cone. Prints each stretch where the seat is in the view cone but wouldn't be drawn (culled) or is
    // faded (alpha < 0.5), with the cause.
    internal static class TraceVisTool
    {
        // the tree's PlayerEntity.NetSnapNodeRef (10-15), here so a 0.2.1 build can test it on 0.2.1 replays
        private static NodeRef FixedSnap(Scene scene, NodeRef current, Vector3 prev, Vector3 cur)
        {
            if (current.PartIndex != -1 && (cur - prev).LengthSquared <= 1) return scene.UpdateNodeRef(current, prev, cur);
            NodeRef best = NodeRef.None;
            float bestDist = 3 * 3;
            foreach (PlayerSpawnEntity spawn in scene.GetPlayerSpawnEntities())
            {
                float dist = (spawn.Position - cur).LengthSquared;
                if (spawn.NodeRef.PartIndex != -1 && dist < bestDist) { bestDist = dist; best = spawn.NodeRef; }
            }
            if (best.PartIndex != -1) return best;
            if (current.PartIndex != -1)
            {
                NodeRef walked = scene.UpdateNodeRef(current, prev, cur);
                if (scene.PartHasGeometryAt(walked.PartIndex, cur)) return walked;
            }
            NodeRef at = scene.GetNodeRefContaining(cur);
            return at.PartIndex != -1 || current.PartIndex == -1 ? at : scene.UpdateNodeRef(current, prev, cur);
        }

        private static object Fld(object o, string name)
            => o.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(o)!;

        private sealed class Seg
        {
            public int Seat, From, To;
            public string Why = "";
            public string Detail = "";
        }

        public static void Run(string[] args)
        {
            System.Diagnostics.Trace.Listeners.Clear();
            if (!ToolPaths.UseDesktopExtraction()) return;
            string path = args[1];
            int only = -1, watch = -1;
            bool chase = false;
            int dumpFrom = -1, dumpTo = -1;
            bool collect = false, sightList = false;
            int guest = -1, snapEvery = 2;
            bool snapCenter = false, snapFix = false;
            var gPart = new Dictionary<int, NodeRef>();
            var gPos = new Dictionary<int, Vector3>();
            var gWrong = new Dictionary<int, int>();
            var gCulled = new Dictionary<int, int>();
            var gSeen = new Dictionary<int, int>();
            var gFrom = new Dictionary<int, (int Step, string What)>();
            var gSegs = new List<string>();
            System.Reflection.PropertyInfo? toolView = typeof(RoomEntity).GetProperty("ToolViewNodeRef");
            int[]? portalParts = null;
            foreach (string a in args.Skip(2))
            {
                if (a.StartsWith("seat=")) only = Int32.Parse(a[5..]);
                // view=N: the replay viewer watching seat N (MatchReplay.PlaceReplayView): its eyes, or its ball camera
                // while it's morphed; chase = the third-person view behind it
                else if (a.StartsWith("view=")) watch = Int32.Parse(a[5..]);
                else if (a == "chase") chase = true;
                // draw = collect draw items each step as the device does (headless + CollectDrawItems), then check the seat
                // was actually handed to the draw list (biped drawn with alpha, or alt form drawn)
                else if (a == "draw") collect = true;
                else if (a == "sight") sightList = true;
                // guest=N[:k]: seat N's device as an online guest: the other seats are puppets posed from the host's snapshot
                // every k steps (2 = 30 Hz, 3 = a weak link), their room part by PlayerEntity.NetSnapNodeRef as a guest does;
                // counts where the puppet's part is off from N's eyes while the host's walked part is on and N sees it
                else if (a.StartsWith("guest=")) { string[] g = a[6..].Split(':'); guest = Int32.Parse(g[0]); watch = guest; if (g.Length > 1) snapEvery = Int32.Parse(g[1]); }
                // center = walk the puppet's snaps from its collision sphere's centre (as the host walks it), not its feet
                else if (a == "center") snapCenter = true;
                // fix = the tree's NetSnapNodeRef (a jump keeps its walked part where that part has geometry, 10-15)
                else if (a == "fix") snapFix = true; // sight = list steps (every 30) where a seat is plainly on screen
                // steps=a-b: print the seat= seat's spot, kept part and spot's part each step; portals=a,b: the portals
                // between those parts (points, plane) once
                else if (a.StartsWith("steps=")) { string[] f = a[6..].Split('-'); (dumpFrom, dumpTo) = (Int32.Parse(f[0]), Int32.Parse(f[1])); }
                else if (a.StartsWith("portals=")) portalParts = a[8..].Split(',').Select(Int32.Parse).ToArray();
            }
            ReplayFile file = ReplayFile.Load(path);
            Console.WriteLine($"  {Path.GetFileName(path)}: {file.Settings.Mode} {file.Settings.Arena}, {file.Steps.Count} steps");
            var open = new Dictionary<int, Seg>();
            var segs = new List<Seg>();
            var log = new List<string>();
            var last = new Dictionary<int, string>();
            var inCone = new Dictionary<int, int>();
            var culledInCone = new Dictionary<int, int>();
            var fadedInCone = new Dictionary<int, int>();
            var culledClear = new Dictionary<int, int>();
            var seenClear = new Dictionary<int, int>();
            // the part the game keeps for the seat (walked through portals) vs the part its spot is actually in
            var partWrong = new Dictionary<int, int>();
            var wrongFrom = new Dictionary<int, (int Step, string What)>();
            var wrongSegs = new List<string>();
            var lastPos = new Dictionary<int, Vector3>();
            var hiddenMoving = new Dictionary<int, int>();
            var hiddenFrom = new Dictionary<int, (int Step, string What)>();
            var hiddenSegs = new List<string>();
            var undrawn = new Dictionary<int, int>();
            var drawnSeen = new Dictionary<int, int>();
            var undrawnFrom = new Dictionary<int, (int Step, string What)>();
            var undrawnSegs = new List<string>();
            var badPose = new Dictionary<int, int>();
            var embedded = new Dictionary<int, int>();
            var embeddedCulled = new Dictionary<int, int>();
            var partlySeen = new Dictionary<int, int>();
            var partlyCulled = new Dictionary<int, int>();
            var pcFrom = new Dictionary<int, (int Step, string What)>();
            var pcSegs = new List<string>();
            ReplayVerify.Result r = ReplayVerify.Run(file, (step, scene) =>
            {
                if (collect) Scene.CollectDrawItems = true; // the match start sets it off for a replay check
                if (portalParts != null && step == 1)
                {
                    var portals = (List<Portal>)typeof(RoomEntity).GetField("_portals",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(scene.Room)!;
                    foreach (Portal pt in portals.Where(pt => portalParts.Length == 1 ? portalParts.Contains(pt.NodeRef1.PartIndex) || portalParts.Contains(pt.NodeRef2.PartIndex) : portalParts.Contains(pt.NodeRef1.PartIndex) && portalParts.Contains(pt.NodeRef2.PartIndex)))
                    {
                        Console.WriteLine($"  portal {pt.Name}: part {pt.NodeRef1.PartIndex} <-> {pt.NodeRef2.PartIndex}, active {pt.Active},"
                            + $" plane {pt.Plane}, points {String.Join(" ", pt.Points.Select(v => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})"))}");
                    }
                }
                if (only >= 0 && step >= dumpFrom && step <= dumpTo)
                {
                    PlayerEntity d = PlayerEntity.Players[only];
                    int idx = d.IsAltForm ? 2 : 0;
                    Vector3 sp = d.Position + PlayerEntity.PlayerVolumes[(int)d.Hunter, idx].SpherePosition;
                    Console.WriteLine($"  {step}: pos {d.Position.X:0.000},{d.Position.Y:0.000},{d.Position.Z:0.000} sphere {sp.X:0.000},{sp.Y:0.000},{sp.Z:0.000}"
                        + $" r {PlayerEntity.PlayerVolumes[(int)d.Hunter, idx].SphereRadius:0.000} kept {d.NodeRef.PartIndex} spot {scene.GetNodeRefContaining(sp).PartIndex}"
                        + $" {(d.IsAltForm ? "ALT" : "biped")}");
                }
                PlayerEntity main = PlayerEntity.Main;
                CameraInfo cam = main.CameraInfo;
                Vector3 camPos = scene.HostView is { } hv ? hv.Position : cam.Position;
                Vector3 facing = scene.HostView is { } hv2 ? hv2.Target - hv2.Position : (cam.Target - cam.Position);
                facing = facing.LengthSquared > 0 ? facing.Normalized() : Vector3.UnitZ;
                for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
                {
                    PlayerEntity p = PlayerEntity.Players[i];
                    if (p == main && watch < 0 || only >= 0 && i != only || !p.LoadFlags.TestFlag(LoadFlags.Active))
                    {
                        continue;
                    }
                    bool alive = p.LoadFlags.TestFlag(LoadFlags.Spawned) && p.Health > 0;
                    // not drawn although it moves: the hidden-model flag (death, despawn) or health 0 on a seat that's still going
                    bool hidden = p.Flags2.TestFlag(PlayerFlags2.HideModel) || p.Health <= 0;
                    bool moved = lastPos.TryGetValue(i, out Vector3 lp) && (p.Position - lp).LengthSquared > 0.0001f;
                    lastPos[i] = p.Position;
                    if (hidden && moved)
                    {
                        hiddenMoving[i] = hiddenMoving.GetValueOrDefault(i) + 1;
                        if (!hiddenFrom.ContainsKey(i)) hiddenFrom[i] = (step, $"{p.Hunter} health {p.Health} hide {p.Flags2.TestFlag(PlayerFlags2.HideModel)} spawned {p.LoadFlags.TestFlag(LoadFlags.Spawned)} pos {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}");
                    }
                    else if (!hidden && hiddenFrom.TryGetValue(i, out var hf))
                    {
                        hiddenSegs.Add($"  seat {i} steps {hf.Step}-{step - 1} ({hf.Step / 60f:0.0}-{(step - 1) / 60f:0.0}s, {(step - hf.Step) / 60f:0.0}s) HIDDEN while moving: {hf.What}");
                        hiddenFrom.Remove(i);
                    }
                    bool partOn = scene.IsNodeRefVisible(p.NodeRef);
                    int truePart = alive ? scene.GetNodeRefContaining(p.Position + new Vector3(0, p.IsAltForm ? 0 : 0.9f, 0)).PartIndex : -1;
                    bool wrong = truePart != -1 && truePart != p.NodeRef.PartIndex;
                    if (wrong)
                    {
                        partWrong[i] = partWrong.GetValueOrDefault(i) + 1;
                        if (!wrongFrom.ContainsKey(i))
                        {
                            wrongFrom[i] = (step, $"{p.Hunter} {(p.IsAltForm ? "ALT" : "biped")} kept part {p.NodeRef.PartIndex}, is in part {truePart},"
                                + $" hspeed {new Vector3(p.Speed.X, 0, p.Speed.Z).Length:0.000}, pos {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}"
                                + $", alt attack {p.Flags2.TestFlag(PlayerFlags2.AltAttack)}");
                        }
                    }
                    else if (wrongFrom.TryGetValue(i, out var wf))
                    {
                        wrongSegs.Add($"  seat {i} steps {wf.Step}-{step - 1} ({wf.Step / 60f:0.0}-{(step - 1) / 60f:0.0}s, {(step - wf.Step) / 60f:0.0}s) WRONG PART: {wf.What}");
                        wrongFrom.Remove(i);
                    }
                    Vector3 to = p.Position - camPos;
                    float dist = to.Length;
                    float angle = dist > 0 ? MathHelper.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(to / dist, facing), -1, 1))) : 0;
                    bool cone = alive && (i == watch || angle < 40 && dist < 60);
                    // line of sight from the host's camera to the seat's middle (room collision only, players ignored)
                    bool clear = false;
                    if (cone)
                    {
                        CollisionResult discard = default;
                        clear = i == watch && !(p.CameraType == CameraType.First) || !CollisionDetection.CheckBetweenPoints(camPos, p.Position + new Vector3(0, p.IsAltForm ? 0 : 0.9f, 0),
                            TestFlags.Players, scene, ref discard);
                    }
                    string why = !alive ? "" : !partOn ? $"CULLED (part {p.NodeRef.PartIndex} not active; main in part {main.NodeRef.PartIndex})"
                        : p.CurAlpha < 0.5f ? $"FADED alpha {p.CurAlpha * 31:0.#}/31" : "";
                    // any part of the body in sight (head, feet, sides as well as the middle), and how close a wall blocking the
                    // middle is to the body (a seat inside a wall or floor: "there" on radar and in its shots, body hidden)
                    if (cone && !clear && i != watch)
                    {
                        Vector3 mid = p.Position + new Vector3(0, p.IsAltForm ? 0 : 0.9f, 0);
                        CollisionResult hit = default;
                        CollisionDetection.CheckBetweenPoints(camPos, mid, TestFlags.Players, scene, ref hit);
                        float near = (hit.Position - mid).Length;
                        if (near < 0.35f) { embedded[i] = embedded.GetValueOrDefault(i) + 1; if (!partOn) embeddedCulled[i] = embeddedCulled.GetValueOrDefault(i) + 1; }
                        Vector3 side = Vector3.Cross(to.Normalized(), Vector3.UnitY);
                        side = side.LengthSquared > 0 ? side.Normalized() * 0.45f : Vector3.UnitX * 0.45f;
                        float top = p.IsAltForm ? 0.45f : 1.75f;
                        foreach (Vector3 pt in new[] { p.Position + new Vector3(0, top, 0), p.Position + new Vector3(0, 0.15f, 0), mid + side, mid - side })
                        {
                            CollisionResult d2 = default;
                            if (!CollisionDetection.CheckBetweenPoints(camPos, pt, TestFlags.Players, scene, ref d2))
                            {
                                partlySeen[i] = partlySeen.GetValueOrDefault(i) + 1;
                                if (!partOn)
                                {
                                    partlyCulled[i] = partlyCulled.GetValueOrDefault(i) + 1;
                                    if (!pcFrom.ContainsKey(i)) pcFrom[i] = (step, $"{p.Hunter} {(p.IsAltForm ? "ALT" : "biped")} part {p.NodeRef.PartIndex} (main {main.NodeRef.PartIndex}) dist {dist:0.0} pos {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}");
                                }
                                break;
                            }
                        }
                    }
                    if (pcFrom.TryGetValue(i, out var pcf) && (partOn || !cone))
                    {
                        if (step - pcf.Step >= 10) pcSegs.Add($"  seat {i} steps {pcf.Step}-{step - 1} ({pcf.Step / 60f:0.0}s, {(step - pcf.Step) / 60f:0.0}s) CULLED, body partly in sight: {pcf.What}");
                        pcFrom.Remove(i);
                    }
                    if (guest >= 0 && i != guest)
                    {
                        bool live = p.LoadFlags.TestFlag(LoadFlags.Spawned) && p.Health > 0;
                        if (!live) { gPart.Remove(i); gPos.Remove(i); }
                        else if (step % snapEvery == 0 || !gPart.ContainsKey(i))
                        {
                            Vector3 off = snapCenter ? PlayerEntity.PlayerVolumes[(int)p.Hunter, p.IsAltForm ? 2 : 0].SpherePosition : Vector3.Zero;
                            bool hadAny = gPart.TryGetValue(i, out NodeRef had);
                            gPart[i] = hadAny ? (snapFix ? FixedSnap(scene, had, gPos[i] + off, p.Position + off) : PlayerEntity.NetSnapNodeRef(scene, had, gPos[i] + off, p.Position + off)) : PlayerEntity.NetSpawnNodeRef(scene, p.Position);
                            if (i == only && (!hadAny || gPart[i].PartIndex != had.PartIndex) && (dumpFrom < 0 || step >= dumpFrom && step <= dumpTo))
                                Console.WriteLine($"  PUPPET {step}: part {(hadAny ? had.PartIndex : -1)} -> {gPart[i].PartIndex} (host {p.NodeRef.PartIndex}), move {(hadAny ? (p.Position - gPos.GetValueOrDefault(i)).Length : -1):0.00} from {gPos.GetValueOrDefault(i).X:0.00},{gPos.GetValueOrDefault(i).Y:0.00},{gPos.GetValueOrDefault(i).Z:0.00} to {p.Position.X:0.00},{p.Position.Y:0.00},{p.Position.Z:0.00}{(hadAny ? "" : " (spawn)")}");
                            gPos[i] = p.Position;
                        }
                        if (live && gPart.TryGetValue(i, out NodeRef gp))
                        {
                            if (gp.PartIndex != p.NodeRef.PartIndex) gWrong[i] = gWrong.GetValueOrDefault(i) + 1;
                            bool gOn = scene.IsNodeRefVisible(gp);
                            if (cone && clear && partOn)
                            {
                                gSeen[i] = gSeen.GetValueOrDefault(i) + 1;
                                if (!gOn)
                                {
                                    gCulled[i] = gCulled.GetValueOrDefault(i) + 1;
                                    if (!gFrom.ContainsKey(i)) gFrom[i] = (step, $"{p.Hunter} {(p.IsAltForm ? "ALT" : "biped")} puppet part {gp.PartIndex}, host part {p.NodeRef.PartIndex}, dist {dist:0.0}, angle {angle:0}, hspeed {new Vector3(p.Speed.X, 0, p.Speed.Z).Length:0.000}, pos {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}");
                                }
                            }
                            if (gFrom.TryGetValue(i, out var gf) && (gOn || !(cone && clear && partOn)))
                            {
                                gSegs.Add($"  seat {i} steps {gf.Step}-{step - 1} ({gf.Step / 60f:0.0}s, {(step - gf.Step) / 60f:0.0}s) GUEST {guest} CULLS IT IN SIGHT: {gf.What}");
                                gFrom.Remove(i);
                            }
                        }
                    }
                    if (sightList && cone && clear && partOn && i != watch && angle < 20 && step % 30 == 0)
                        Console.WriteLine($"  SIGHT seat {i} step {step} dist {dist:0.0} angle {angle:0} {(p.IsAltForm ? "ALT" : "biped")} alpha {p.CurAlpha * 31:0}");
                    if (collect && cone && clear && partOn && step > 0)
                    {
                        bool drawn = p.HostBipedAlpha > 0 || p.Flags2.TestFlag(PlayerFlags2.DrawnThirdPerson);
                        // the body's placement as drawn: a zero or NaN facing collapses the model to nothing
                        if (drawn && !p.IsAltForm)
                        {
                            float lx = (float)Fld(p, "_field70"), lz = (float)Fld(p, "_field74");
                            Vector3 g2 = (Vector3)Fld(p, "_gunVec2");
                            float lat = MathF.Sqrt(lx * lx + lz * lz);
                            Model bm = p.HostBipedModel1.Model;
                            Matrix4 root = bm.Nodes[0].Animation;
                            float det = root.Determinant;
                            bool bad = !(lat > 0.5f && lat < 1.5f) || !(g2.Length > 0.5f && g2.Length < 1.5f) || float.IsNaN(det) || MathF.Abs(det) < 1e-4f
                                || (root.Row3.Xyz - p.Position).Length > 4;
                            if (bad)
                            {
                                drawn = false;
                                badPose[i] = badPose.GetValueOrDefault(i) + 1;
                                if (badPose[i] < 6) Console.WriteLine($"  BAD POSE step {step} seat {i}: lateral {lat:0.000} gunVec2 {g2.Length:0.000} det {det:0.####} root {root.Row3.Xyz} pos {p.Position}");
                            }
                        }
                        if (!drawn)
                        {
                            undrawn[i] = undrawn.GetValueOrDefault(i) + 1;
                            if (!undrawnFrom.ContainsKey(i)) undrawnFrom[i] = (step, $"{p.Hunter} {(p.IsAltForm ? "ALT" : "biped")} {p.CurrentWeapon} hp {p.Health} alpha {p.CurAlpha * 31:0.#} biped alpha {p.HostBipedAlpha:0.00} lod1 {p.Flags2.TestFlag(PlayerFlags2.Lod1)} dist {dist:0.0}");
                        }
                        else if (undrawnFrom.TryGetValue(i, out var uf))
                        {
                            undrawnSegs.Add($"  seat {i} steps {uf.Step}-{step - 1} ({uf.Step / 60f:0.0}s, {(step - uf.Step) / 60f:0.0}s) NOT DRAWN in sight: {uf.What}");
                            undrawnFrom.Remove(i);
                        }
                        else drawnSeen[i] = drawnSeen.GetValueOrDefault(i) + 1;
                    }
                    if (cone)
                    {
                        inCone[i] = inCone.GetValueOrDefault(i) + 1;
                        if (!partOn) culledInCone[i] = culledInCone.GetValueOrDefault(i) + 1;
                        if (clear) seenClear[i] = seenClear.GetValueOrDefault(i) + 1;
                        if (!partOn && clear) culledClear[i] = culledClear.GetValueOrDefault(i) + 1;
                        else if (p.CurAlpha < 0.5f) fadedInCone[i] = fadedInCone.GetValueOrDefault(i) + 1;
                    }
                    string kind = cone && why.Length > 0 ? why.Split(' ')[0] + (clear ? " (in sight)" : " (behind a wall)") : "";
                    if (open.TryGetValue(i, out Seg? s) && (s.Why != kind))
                    {
                        s.To = step - 1;
                        segs.Add(s);
                        open.Remove(i);
                    }
                    if (kind.Length > 0 && !open.ContainsKey(i))
                    {
                        open[i] = new Seg
                        {
                            Seat = i, From = step, Why = kind,
                            Detail = $"{kind} {why}{(scene.HostView is { } hv3 ? $" [view's part by lookup {scene.GetNodeRefContaining(hv3.Position).PartIndex}, watched seat's own camera part {PlayerEntity.Players[watch].CameraInfo.NodeRef.PartIndex}]" : "")}; {p.Hunter} {(p.IsAltForm ? "ALT" : "biped")} {p.CurrentWeapon}, dist {dist:0.0}, angle {angle:0}"
                                + $", hspeed {new Vector3(p.Speed.X, 0, p.Speed.Z).Length:0.000}, pos {p.Position.X:0.0},{p.Position.Y:0.0},{p.Position.Z:0.0}"
                        };
                    }
                    string state = $"{(alive ? "alive" : "dead")}{(p.Flags2.TestFlag(PlayerFlags2.HideModel) ? " HIDE" : "")} hp {(p.Health > 0 ? "+" : "0")} {(p.IsAltForm ? "ALT" : "biped")} {p.CurrentWeapon} part {p.NodeRef.PartIndex}"
                        + $" {(partOn ? "on" : "OFF")} alpha {MathF.Round(p.CurAlpha * 31)}";
                    if (!last.TryGetValue(i, out string? prev) || prev != state)
                    {
                        if (watch < 0 || i == watch) log.Add($"{step,6} ({step / 60f,6:0.0}s) seat {i} {p.Hunter}: {state}; main part {main.NodeRef.PartIndex},"
                            + $" dist {dist:0.0}, angle {angle:0}{(cone ? " IN VIEW" : "")}");
                        last[i] = state;
                    }
                }
                if (guest >= 0) toolView?.SetValue(null, PlayerEntity.Players[guest].CameraInfo.NodeRef);
                if (watch >= 0)
                {
                    PlayerEntity w = PlayerEntity.Players[watch];
                    CameraInfo eye = w.CameraInfo;
                    if (chase)
                    {
                        PlayerNetState ns = w.NetCapture();
                        var back = new Vector3(ns.Facing.X, 0, ns.Facing.Z);
                        back = back.LengthSquared > 0.0001f ? back.Normalized() : Vector3.UnitZ;
                        scene.HostView = new HostView(ns.Position - back * 3 + Vector3.UnitY * 1.5f, ns.Position + Vector3.UnitY * 0.5f, Vector3.UnitY, 0);
                    }
                    else
                    {
                        scene.HostView = new HostView(eye.Position, eye.Target, Vector3.UnitY, eye.Fov > 0 ? eye.Fov : 0);
                    }
                }
            });
            foreach (Seg s in open.Values)
            {
                s.To = file.Steps.Count - 1;
                segs.Add(s);
            }
            Console.WriteLine($"  replay {r.Verdict}");
            foreach (var kv in wrongFrom) wrongSegs.Add($"  seat {kv.Key} steps {kv.Value.Step}-end WRONG PART: {kv.Value.What}");
            foreach (int i in partWrong.Keys.OrderBy(k => k))
            {
                Console.WriteLine($"  seat {i} {PlayerEntity.Players[i].Hunter}: kept room part differs from its spot's part {partWrong[i]} steps");
            }
            foreach (string w in wrongSegs) Console.WriteLine(w);
            foreach (var kv in hiddenFrom) hiddenSegs.Add($"  seat {kv.Key} steps {kv.Value.Step}-end HIDDEN while moving: {kv.Value.What}");
            foreach (int i in hiddenMoving.Keys.OrderBy(k => k)) Console.WriteLine($"  seat {i} {PlayerEntity.Players[i].Hunter}: hidden (hide flag or health 0) while moving {hiddenMoving[i]} steps");
            foreach (int i in drawnSeen.Keys.Union(undrawn.Keys).OrderBy(k => k)) Console.WriteLine($"  seat {i} {PlayerEntity.Players[i].Hunter}: in sight and part active {drawnSeen.GetValueOrDefault(i) + undrawn.GetValueOrDefault(i)} steps, not drawn {undrawn.GetValueOrDefault(i)}");
            foreach (string u in undrawnSegs) Console.WriteLine(u);
            foreach (int i in gSeen.Keys.Union(gWrong.Keys).OrderBy(k => k)) Console.WriteLine($"  seat {i} {PlayerEntity.Players[i].Hunter} as a puppet on guest seat {guest} (snapshot every {snapEvery} steps): part differs from the host's {gWrong.GetValueOrDefault(i)} steps; in the guest's sight {gSeen.GetValueOrDefault(i)}, culled there {gCulled.GetValueOrDefault(i)}");
            foreach (string u in gSegs.Where(g => !g.Contains(", 0.0s)"))) Console.WriteLine(u);
            foreach (int i in partlySeen.Keys.Union(embedded.Keys).OrderBy(k => k)) Console.WriteLine($"  seat {i} {PlayerEntity.Players[i].Hunter}: middle hidden but head/feet/side in sight {partlySeen.GetValueOrDefault(i)} steps (culled then {partlyCulled.GetValueOrDefault(i)}); wall within 0.35 of the middle {embedded.GetValueOrDefault(i)} (culled then {embeddedCulled.GetValueOrDefault(i)})");
            foreach (string u in pcSegs) Console.WriteLine(u);
            foreach (string h in hiddenSegs.Where(h => !h.Contains(", 0.0s)") )) Console.WriteLine(h);
            foreach (int i in inCone.Keys.OrderBy(k => k))
            {
                Console.WriteLine($"  seat {i} {PlayerEntity.Players[i].Hunter}: in the host's view cone {inCone[i]} steps ({seenClear.GetValueOrDefault(i)} with a clear line of sight);"
                    + $" culled there {culledInCone.GetValueOrDefault(i)} (with a clear line of sight {culledClear.GetValueOrDefault(i)}), faded there {fadedInCone.GetValueOrDefault(i)}");
            }
            foreach (Seg s in segs.OrderBy(s => s.From).Where(s => s.To - s.From >= 10))
            {
                Console.WriteLine($"  seat {s.Seat} steps {s.From}-{s.To} ({s.From / 60f:0.0}-{s.To / 60f:0.0}s, {(s.To - s.From + 1) / 60f:0.0}s): {s.Detail}");
            }
            string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "_tracevis.txt");
            File.WriteAllLines(outPath, log);
            Console.WriteLine($"  state changes: {outPath}");
        }
    }
}
