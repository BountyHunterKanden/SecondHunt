using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Entities;
using MphRead.Formats;
using MphRecomp.Campaign;
using MphRecomp.Multiplayer;
using OpenTK.Mathematics;

namespace MphRead
{
    // Bot move solver P1 (docs/BOT_MOVE_SOLVER.md): for every link of an arena's route graph, the cheapest move off a short
    // menu (walk, coast in, jumps, run-ups, roll, lift waits) that gets a player from the link's start to its end, tried from
    // a few start spots. No game change: the player driven is the human seat, through the same CampaignInput a pad gives it
    // (same physics as a bot's), and "got there" is the AI's own in-range check (IsNodeInRange) redone here. The bot seat
    // is kept only for its route graph and taken out of play.
    //
    //   -botprobe linksolve arena=vanilla hunters=samus [linklist=<tsv: arena, link#>] [linkdebug=<n,..>] [out=<path>]
    //
    // One line per link: S arena hunter link# from to fromType toType len dy move param pass valid frames.
    // move "none" = nothing on the menu got there from any start spot (the owner's list).
    internal static partial class BotProbe
    {
        private const int SolveSeconds = 8; // a try's time to get there (plus its wait and run-up)
        private static string? SolveList; // linklist=: only these links (arena<TAB>link#; a links_on.tsv works)

        private sealed record SolveMove(string Name, string Param, bool Alt = false, float RunUp = 0, int Jump = 0,
            float JumpAt = 0, bool Climb = false, int Wait = 0, bool Coast = false, bool Boost = false, int Hold = 8, bool Echoes = false);

        // cheapest first (the first move that gets there from the most start spots wins). Jump: 0 none, 1 at the ledge (no
        // floor ahead), 2 at a point along the link (JumpAt, 0 = a standing jump), 3 at the ledge else at JumpAt
        private static readonly SolveMove[] SolveMenu =
        {
            new("walk", "-"),
            new("coast", "-", Coast: true),
            new("ledgejump", "-", Jump: 1),
            new("jump", "0", Jump: 2, JumpAt: 0),
            new("jump", "0.3", Jump: 2, JumpAt: 0.3f),
            new("jump", "0.6", Jump: 2, JumpAt: 0.6f),
            new("runup", "1", RunUp: 1, Jump: 3),
            new("runup", "2", RunUp: 2, Jump: 3),
            new("runup", "3", RunUp: 3, Jump: 3),
            new("climb", "ledge", Jump: 1, Climb: true),
            new("climb", "0", Jump: 2, JumpAt: 0, Climb: true),
            new("roll", "-", Alt: true),
            new("roll", "boost", Alt: true, Boost: true),
            new("wait", "1", Wait: 1), new("wait", "2", Wait: 2), new("wait", "3", Wait: 3), new("wait", "4", Wait: 4),
            new("wait", "5", Wait: 5), new("wait", "6", Wait: 6), new("wait", "7", Wait: 7), new("wait", "8", Wait: 8),
            new("waitjump", "2", Wait: 2, Jump: 1), new("waitjump", "4", Wait: 4, Jump: 1), new("waitjump", "6", Wait: 6, Jump: 1),
            // P3, Echoes arenas only (HostEchoesMoves): "climb" above is already the double jump there (second press at the top).
            // These add the double jump off a run-up or partway along, and the Gravity Boost (second press held, under water)
            new("double", "runup2", RunUp: 2, Jump: 3, Climb: true, Echoes: true),
            new("double", "0.6", Jump: 2, JumpAt: 0.6f, Climb: true, Echoes: true),
            new("gboost", "0", Jump: 2, JumpAt: 0, Climb: true, Hold: 90, Echoes: true),
            new("gboost", "ledge", Jump: 1, Climb: true, Hold: 90, Echoes: true),
        };

        private static readonly System.Reflection.FieldInfo PadTimeField = typeof(PlayerEntity).GetField("_timeSinceJumpPad",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        private static Dictionary<string, HashSet<int>>? _solveListed;

        private static void LinkSolve(MatchSettings settings, RoomMetadata arena, List<string> lines)
        {
            if (SolveList != null && _solveListed == null)
            {
                _solveListed = new Dictionary<string, HashSet<int>>();
                foreach (string row in System.IO.File.ReadLines(SolveList))
                {
                    string[] c = row.Split('\t');
                    if (c.Length >= 2 && Int32.TryParse(c[1], out int n) && (c.Length < 5 || c[4] == "failing"))
                    {
                        if (!_solveListed.TryGetValue(c[0], out HashSet<int>? set))
                        {
                            _solveListed[c[0]] = set = new HashSet<int>();
                        }
                        set.Add(n);
                    }
                }
            }
            HashSet<int>? listed = null;
            if (_solveListed != null && !_solveListed.TryGetValue(arena.InGameName, out listed))
            {
                return; // nothing listed in this arena
            }
            CampaignHost host = CampaignHost.StartMatch(settings);
            host.Scene.HostDrawFreeSim = true;
            PlayerEntity bot = PlayerEntity.Players[1];
            PlayerEntity me = PlayerEntity.Players[0];
            var input = new CampaignInput { SelectWeapon = BeamType.None };
            try
            {
                for (int i = 0; i < 120; i++)
                {
                    host.Step(input);
                }
                IReadOnlyList<NodeData3> nodes = bot.AiData.AuditNodes;
                var padNodes = new HashSet<NodeData3>();
                foreach (JumpPadEntity pad in host.Scene.GetJumpPadEntities())
                {
                    if (pad.ClosestNode != null)
                    {
                        padNodes.Add(pad.ClosestNode);
                    }
                }
                // nodes that ride something moving (a platform's collision under them, or a floor that comes and goes): sampled
                // over 10 s of the match, for the report (a fixed move can't time a moving floor)
                var platNodes = new HashSet<NodeData3>();
                var floorSeen = new Dictionary<NodeData3, bool>();
                for (int sample = 0; sample < 20; sample++)
                {
                    for (int i = 0; i < 30; i++)
                    {
                        bot.LoadFlags &= ~LoadFlags.Active;
                        host.Step(input);
                    }
                    foreach (NodeData3 n in nodes)
                    {
                        var hit = new CollisionResult();
                        bool floor = CollisionDetection.CheckBetweenPoints(n.Position.AddY(0.5f), n.Position.AddY(-1.5f), TestFlags.None, host.Scene, ref hit);
                        if (floor && hit.EntityCollision != null || floorSeen.TryGetValue(n, out bool was) && was != floor)
                        {
                            platNodes.Add(n);
                        }
                        floorSeen[n] = floor;
                    }
                }
                float closest = Single.MaxValue; // a link's closest approach over all its tries (distance past the node's range)
                float altLift = Fixed.ToFloat(me.Values.AltColRadius) - Fixed.ToFloat(me.Values.AltColYPos);
                int PadTime() => (ushort)PadTimeField.GetValue(me)!;
                bool Grounded() => me.Flags1.TestFlag(PlayerFlags1.Grounded);
                // the AI's IsNodeInRange, with its own node (_node40) = the link's end
                bool InRange(NodeData3 node)
                {
                    // (vanilla: off the ground only an aerial node counts; the recomp's just-launched exception is left out)
                    if (!Grounded() && node.NodeType != NodeType.Aerial)
                    {
                        return false;
                    }
                    closest = MathF.Min(closest, ((node.Position - me.Position).AddY(me.IsAltForm ? altLift : 0.5f)).Length - node.MaxDistance);
                    if (PadTime() > 5 * 2 && padNodes.Contains(node))
                    {
                        return false;
                    }
                    Vector3 between = (node.Position - me.Position).AddY(me.IsAltForm ? altLift : 0.5f);
                    return between.LengthSquared < node.MaxDistance * node.MaxDistance;
                }
                void Step(CampaignInput ci)
                {
                    bot.LoadFlags &= ~LoadFlags.Active;
                    host.Step(ci);
                }
                void WaitAlive()
                {
                    for (int w = 0; w < 900 && me.Health == 0; w++)
                    {
                        Step(new CampaignInput { SelectWeapon = BeamType.None, Buttons = w % 4 < 2 ? CampaignButtons.Jump : CampaignButtons.None });
                    }
                    for (int w = 0; w < 30; w++)
                    {
                        Step(input);
                    }
                }
                void SetForm(bool alt)
                {
                    if (!alt && me.IsAltForm)
                    {
                        me.ExitAltForm();
                        for (int m = 0; m < 60 && me.IsAltForm; m++)
                        {
                            Step(input);
                        }
                    }
                    else if (alt && !me.IsAltForm)
                    {
                        me.AuditMorph();
                        for (int m = 0; m < 180 && (me.AuditMorphing || !me.IsAltForm); m++)
                        {
                            Step(input);
                        }
                    }
                }
                bool FloorAt(Vector3 p, float down)
                {
                    var hit = new CollisionResult();
                    return CollisionDetection.CheckBetweenPoints(p.AddY(0.5f), p.AddY(-down), TestFlags.None, host.Scene, ref hit);
                }
                bool Clear(Vector3 a, Vector3 b)
                {
                    var hit = new CollisionResult();
                    return !CollisionDetection.CheckBetweenPoints(a.AddY(0.6f), b.AddY(0.6f), TestFlags.None, host.Scene, ref hit);
                }
                // AimDelta.X turning the player's flat facing onto dir (PlayerInput.UpdateAimX: aimX = -delta / 4 degrees,
                // positive turns (x, z) clockwise). Turning is instant here (up to 45 degrees a frame): a P2 assumption.
                float TurnTo(Vector3 dir)
                {
                    Vector3 c = me.FacingVector.WithY(0);
                    if (c.LengthSquared < 0.0001f || dir.LengthSquared < 0.0001f)
                    {
                        return 0;
                    }
                    c = c.Normalized();
                    float phi = MathF.Atan2(c.X * dir.Z - c.Z * dir.X, c.X * dir.X + c.Z * dir.Z);
                    float amount = Math.Clamp(MathHelper.RadiansToDegrees(-phi), -45, 45);
                    return -4 * amount;
                }
                // one try: put at start (the link's start + an offset), then the move. Result: frames to get there, -1 =
                // didn't (time up / died), -2 = not a valid start spot
                int debugLink = -1;
                string lastEnd = "-";
                // path: put at path[0], then through each node in turn; graded at the last. A plain link is [from, to]; a
                // chain (a link that starts in mid-air, met only on the way through) is a way in from a ground node, then the link
                int Try(NodeData3[] path, SolveMove mv, Vector2 offset)
                {
                    NodeData3 from = path[0], to = path[1];
                    if (mv.Echoes && !PlayerEntity.HostEchoesMoves)
                    {
                        return -2; // no double jump or Gravity Boost outside an Echoes arena
                    }
                    WaitAlive();
                    bool alt = mv.Alt || from.NodeType == NodeType.AltForm;
                    if (alt != mv.Alt)
                    {
                        return -2; // a biped move from a morph-ball node
                    }
                    SetForm(alt);
                    Vector3 flat = (to.Position - from.Position).WithY(0);
                    Vector3 d = flat.LengthSquared > 0.01f ? flat.Normalized() : -Vector3.UnitX;
                    Vector3 side = new Vector3(-d.Z, 0, d.X);
                    Vector3 start = from.Position + d * (offset.Y - mv.RunUp) + side * offset.X;
                    if (start != from.Position && (!Clear(from.Position, start) || !FloorAt(start, 1.5f)))
                    {
                        return -2;
                    }
                    if (me.HostSeated)
                    {
                        me.HostSeatOff(me.Position, Vector3.Zero); // an earlier try left her in a manned turret (Shooting Gallery): out, or every start snaps back to the seat
                    }
                    me.AuditPut(from, to);
                    me.Reposition(start.AddY(alt ? 0.5f : 0), d, host.Scene.GetNodeRefContaining(start));
                    me.PrevPosition = me.Position;
                    me.Speed = Vector3.Zero;
                    me.Health = me.HealthMax;
                    for (int s = 0; s < 10; s++)
                    {
                        Step(input);
                    }
                    // (a start that throws you elsewhere, e.g. a teleporter under the node: the link can't be graded from there)
                    float putMoved = (me.Position - start).Length;
                    if (putMoved > 2 && !padNodes.Contains(from))
                    {
                        lastEnd = $"moved at start {putMoved:0.0} to {Fmt(me.Position)} hp {me.Health}";
                        return -2;
                    }
                    if (me.Health == 0 || !Grounded() || me.Position.Y < start.Y - 1
                        || start != from.Position && (me.Position - start).Xz.Length > 0.4f) // (the node itself always counts: a bot gets put there, settled or not)
                    {
                        if (debugLink >= 0)
                        {
                            Console.WriteLine($"  {debugLink} {mv.Name}/{mv.Param} off {offset.X:0.0},{offset.Y:0.0} bad start {Fmt(start)} -> {Fmt(me.Position)} gnd {Grounded()} alt {me.IsAltForm} hp {me.Health}");
                        }
                        return -2;
                    }
                    int limit = (SolveSeconds + mv.Wait + (int)mv.RunUp) * 60;
                    int jumpFrame = -1, second = -1;
                    Vector3 dir = d;
                    float len = (to.Position - from.Position).Xz.Length;
                    int at = 1;
                    NodeData3 goal = to;
                    for (int f = 0; f < limit; f++)
                    {
                        if (at < path.Length - 1)
                        {
                            float keep = closest;
                            if (InRange(goal))
                            {
                                // through a way-in node: on to the next (from here on, as a link of its own)
                                from = goal;
                                goal = path[++at];
                                d = (goal.Position - from.Position).WithY(0);
                                d = d.LengthSquared > 0.01f ? d.Normalized() : dir;
                                len = (goal.Position - from.Position).Xz.Length;
                                jumpFrame = -1;
                                second = -1;
                            }
                            closest = keep;
                        }
                        var ci = new CampaignInput { SelectWeapon = BeamType.None };
                        bool waiting = f < mv.Wait * 60;
                        Vector3 toFlat = (goal.Position - me.Position).WithY(0);
                        if (toFlat.LengthSquared > 0.09f)
                        {
                            dir = toFlat.Normalized();
                        }
                        ci.AimDelta = new System.Numerics.Vector2(TurnTo(dir), 0);
                        if (!waiting && !(mv.Coast && toFlat.Length < 1.0f))
                        {
                            ci.Move = new System.Numerics.Vector2(0, 1);
                        }
                        if (!waiting && jumpFrame < 0 && Grounded())
                        {
                            // progress along the link from its start (negative on a run-up)
                            float along = len < 0.01f ? 1 : Vector3.Dot((me.Position - from.Position).WithY(0), d) / len;
                            bool ledge = !FloorAt(me.Position + dir * 0.7f, 1.2f);
                            bool go = mv.Jump switch
                            {
                                1 => ledge,
                                2 => along >= mv.JumpAt,
                                3 => ledge || along >= 0,
                                _ => false,
                            };
                            if (go)
                            {
                                jumpFrame = f;
                            }
                        }
                        if (jumpFrame >= 0 && f - jumpFrame < 8)
                        {
                            ci.Buttons |= CampaignButtons.Jump;
                        }
                        if (mv.Climb && jumpFrame >= 0 && f - jumpFrame > 16 && second < 0 && me.Speed.Y < 0.05f)
                        {
                            second = f;
                        }
                        if (second >= 0 && f - second < mv.Hold)
                        {
                            ci.Buttons |= CampaignButtons.Jump;
                        }
                        if (mv.Boost && !waiting && f % 40 < 20)
                        {
                            ci.Buttons |= CampaignButtons.Boost;
                        }
                        Step(ci);
                        if (debugLink >= 0)
                        {
                            Console.WriteLine($"  {debugLink} {mv.Name}/{mv.Param} off {offset.X:0.0},{offset.Y:0.0} +{f} pos {Fmt(me.Position)} face {Fmt(me.FacingVector)} spd {Fmt(me.Speed)} gnd {Grounded()} alt {me.IsAltForm} hp {me.Health} pad {PadTime()} btn {ci.Buttons}");
                        }
                        if (me.Health == 0)
                        {
                            lastEnd = "died";
                            return -1;
                        }
                        if (me.HostSeated)
                        {
                            lastEnd = "seated in a turret"; // (a bot crossing a link doesn't want the seat)
                            return -1;
                        }
                        if (at == path.Length - 1)
                        {
                            if (InRange(goal))
                            {
                                return f + 1;
                            }
                        }
                        // ended a jump-landing well below both ends: fell off (saves the time left)
                        if (Grounded() && me.Position.Y < Math.Min(from.Position.Y, goal.Position.Y) - 4)
                        {
                            lastEnd = $"fell {(goal.Position - me.Position).Xz.Length:0.0}/{me.Position.Y - goal.Position.Y:0.0}";
                            return -1;
                        }
                    }
                    // time up: where it ended, flat distance / height from the target (a walk circling on a floor right above
                    // or below it = the straight line isn't the way)
                    lastEnd = $"{(Grounded() ? "stood" : "air")} {(goal.Position - me.Position).Xz.Length:0.0}/{me.Position.Y - goal.Position.Y:0.0}";
                    return -1;
                }
                var linksOf = nodes.ToDictionary(n => n, n => bot.AiData.AuditLinks(n));
                // ways into a mid-air node: back along the graph (up to 3 nodes) to a ground node; up to 3, shortest first
                List<NodeData3[]> WaysIn(NodeData3 from, NodeData3 to)
                {
                    var found = new List<NodeData3[]>();
                    var level = new List<List<NodeData3>> { new() { from } };
                    for (int depth = 0; depth < 3 && found.Count < 3; depth++)
                    {
                        var next = new List<List<NodeData3>>();
                        foreach (List<NodeData3> chain in level)
                        {
                            foreach (NodeData3 p in nodes.Where(p => !chain.Contains(p) && linksOf[p].Contains(chain[0]))
                                .OrderBy(p => (p.Position - chain[0].Position).LengthSquared))
                            {
                                var longer = new List<NodeData3> { p };
                                longer.AddRange(chain);
                                if (p.NodeType != NodeType.Aerial)
                                {
                                    if (found.Count < 3)
                                    {
                                        found.Add(longer.Append(to).ToArray());
                                    }
                                }
                                else
                                {
                                    next.Add(longer);
                                }
                            }
                        }
                        level = next;
                    }
                    if (found.Count == 0)
                    {
                        // nothing links in: a node only a jump pad throws you to (the AI's closest node in flight). In from the
                        // nearest pads' nodes
                        found.AddRange(padNodes.OrderBy(p => (p.Position - from.Position).LengthSquared).Take(3)
                            .Select(p => new[] { p, from, to }));
                    }
                    return found;
                }
                Vector2[] offsets = { new(0, 0), new(0, -0.8f), new(-0.6f, 0), new(0.6f, 0) };
                int total = 0, solved = 0, tried = 0;
                var byMove = new Dictionary<string, int>();
                foreach (NodeData3 from in nodes)
                {
                    foreach (NodeData3 to in bot.AiData.AuditLinks(from))
                    {
                        total++;
                        if (listed != null && !listed.Contains(total) || LinkDebugSet.Count > 0 && !LinkDebugSet.Contains(total))
                        {
                            continue;
                        }
                        tried++;
                        debugLink = LinkDebugSet.Contains(total) ? total : -1;
                        if (debugLink >= 0)
                        {
                            Console.WriteLine($"  {debugLink} from {from.Id} {Fmt(from.Position)} to {to.Id} {Fmt(to.Position)} md {to.MaxDistance:0.0}");
                        }
                        closest = Single.MaxValue;
                        string walkEnd = "-";
                        SolveMove? best = null;
                        int bestPass = 0, bestValid = 0;
                        float bestFrames = 0;
                        foreach (SolveMove mv in SolveMenu)
                        {
                            int pass = 0, valid = 0;
                            long frames = 0;
                            foreach (Vector2 off in offsets)
                            {
                                // a run-up's start spot is its own (behind the link's start); only the straight-on one
                                if (mv.RunUp > 0 && off != Vector2.Zero)
                                {
                                    continue;
                                }
                                int r = Try(new[] { from, to }, mv, off);
                                if (mv == SolveMenu[0] && off == Vector2.Zero)
                                {
                                    walkEnd = r == -2 ? (lastEnd.StartsWith("moved") ? lastEnd : "bad start") : r > 0 ? "ok" : lastEnd;
                                }
                                if (r == -2)
                                {
                                    continue;
                                }
                                valid++;
                                if (r > 0)
                                {
                                    pass++;
                                    frames += r;
                                }
                            }
                            if (best == null)
                            {
                                bestValid = Math.Max(bestValid, valid); // (nothing passed: the most start spots any move had)
                            }
                            if (pass > bestPass)
                            {
                                best = mv;
                                bestPass = pass;
                                bestValid = valid;
                                bestFrames = frames / 60f / pass;
                            }
                            if (pass > 0 && pass == valid)
                            {
                                break; // from every valid start spot: no cheaper move left to find
                            }
                        }
                        // a link that starts in mid-air (put there, the player just drops): tried as a chain, in through it
                        // from each ground node that links to it (up to 3, closest first)
                        string via = "-";
                        if (best == null && from.NodeType == NodeType.Aerial)
                        {
                            List<NodeData3[]> ins = WaysIn(from, to);
                            foreach (SolveMove mv in SolveMenu)
                            {
                                int pass = 0, valid = 0;
                                long frames = 0;
                                foreach (NodeData3[] path in ins)
                                {
                                    int r = Try(path, mv, Vector2.Zero);
                                    if (r == -2)
                                    {
                                        continue;
                                    }
                                    valid++;
                                    if (r > 0)
                                    {
                                        pass++;
                                        frames += r;
                                    }
                                }
                                bestValid = Math.Max(bestValid, valid);
                                if (pass > bestPass)
                                {
                                    best = mv;
                                    bestPass = pass;
                                    bestValid = valid;
                                    bestFrames = frames / 60f / pass;
                                }
                                if (pass > 0 && pass == valid)
                                {
                                    break;
                                }
                            }
                            via = best == null ? $"in {ins.Count}" : "chain";
                        }
                        string moveName = best == null ? "none" : best.Name;
                        if (best != null)
                        {
                            solved++;
                        }
                        byMove[moveName] = byMove.GetValueOrDefault(moveName) + 1;
                        lines.Add($"S\t{arena.InGameName}\t{me.Hunter}\t{total}\t{from.Id}\t{to.Id}\t{from.NodeType}\t{to.NodeType}"
                            + $"\t{(to.Position - from.Position).Length:0.0}\t{to.Position.Y - from.Position.Y:0.0}"
                            + $"\t{moveName}\t{best?.Param ?? "-"}\t{bestPass}\t{bestValid}\t{bestFrames:0.00}	{closest:0.0}	{to.MaxDistance:0.0}	{via}"
                            + $"\t{(platNodes.Contains(from) ? "F" : "")}{(platNodes.Contains(to) ? "T" : "")}{(padNodes.Contains(to) ? "P" : "")}	{walkEnd}");
                    }
                }
                debugLink = -1;
                lines.Add($"{arena.InGameName,-21} | {me.Hunter,-8} links {total}, tried {tried}, solved {solved}: "
                    + String.Join(" ", byMove.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
                Console.WriteLine(lines[^1]);
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
