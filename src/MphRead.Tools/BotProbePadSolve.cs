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
    // Pad + jump survey (docs/BOT_MOVE_SOLVER.md, 10-28): for every jump pad of an arena, where a player lands riding it
    // with no input, and where it lands holding the stick one of 8 ways and pressing Jump at some point in the flight (a
    // pad launch hands the jump back: ActivateJumpPad clears UsedJump). A landing well above the plain one, on a floor
    // the route graph has no pad link to, is a way up bots never take. Same harness as linksolve; the bot seat is only
    // there for its route graph.
    //
    //   -botprobe padsolve arena=vanilla|echoes [hunter=X]
    //
    // One line per pad (P), one per higher floor found (H: steer, jump frame, tries that got there).
    internal static partial class BotProbe
    {
        private static readonly System.Reflection.FieldInfo PadBeamField = typeof(JumpPadEntity).GetField("_beamVector",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        private static int PadFine; // padfine=N: only pad N, finer steer/jump steps

        private static void PadSolve(MatchSettings settings, RoomMetadata arena, List<string> lines)
        {
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
                var linksOf = nodes.ToDictionary(n => n, n => bot.AiData.AuditLinks(n));
                int PadTime() => (ushort)PadTimeField.GetValue(me)!;
                bool Grounded() => me.Flags1.TestFlag(PlayerFlags1.Grounded);
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
                float TurnTo(Vector3 dir)
                {
                    Vector3 c = me.FacingVector.WithY(0);
                    if (c.LengthSquared < 0.0001f || dir.LengthSquared < 0.0001f)
                    {
                        return 0;
                    }
                    c = c.Normalized();
                    float phi = MathF.Atan2(c.X * dir.Z - c.Z * dir.X, c.X * dir.X + c.Z * dir.Z);
                    return -4 * Math.Clamp(MathHelper.RadiansToDegrees(-phi), -45, 45);
                }
                NodeData3? NearestFloorNode(Vector3 p)
                {
                    // the ground node nearest a landing, weighting height 3x (a floor above or below is a different floor)
                    return nodes.Where(n => n.NodeType != NodeType.Aerial)
                        .OrderBy(n => (n.Position - p).WithY(0).LengthSquared + 9 * (n.Position.Y - p.Y) * (n.Position.Y - p.Y))
                        .FirstOrDefault();
                }
                // one ride: put on the pad, hold steer (none = no stick), press Jump jumpAt frames after the launch (-1 =
                // never). Result: where it settled (null = no launch, died, or still moving after 10 s)
                string lastEnd = "-";
                float lastPeak = 0;
                int lastPeakAt = 0;
                Vector3? Ride(JumpPadEntity pad, Vector3 steer, int jumpAt, Vector3 face)
                {
                    WaitAlive();
                    if (me.IsAltForm)
                    {
                        me.ExitAltForm();
                        for (int m = 0; m < 60 && me.IsAltForm; m++)
                        {
                            Step(input);
                        }
                    }
                    if (me.HostSeated)
                    {
                        me.HostSeatOff(me.Position, Vector3.Zero);
                    }
                    // a few frames' wait first: the pad's cooldown from the last ride
                    for (int w = 0; w < 20; w++)
                    {
                        Step(input);
                    }
                    me.Reposition(pad.Position, face, host.Scene.GetNodeRefContaining(pad.Position));
                    me.PrevPosition = me.Position;
                    me.Speed = Vector3.Zero;
                    me.Health = me.HealthMax;
                    int launch = -1, settled = 0, padWas = PadTime();
                    lastPeak = Single.MinValue;
                    for (int f = 0; f < 600; f++)
                    {
                        var ci = new CampaignInput { SelectWeapon = BeamType.None };
                        if (launch >= 0 && steer != Vector3.Zero)
                        {
                            ci.AimDelta = new System.Numerics.Vector2(TurnTo(steer), 0);
                            ci.Move = new System.Numerics.Vector2(0, 1);
                        }
                        if (launch >= 0 && jumpAt >= 0 && f - launch >= jumpAt && f - launch < jumpAt + 8)
                        {
                            ci.Buttons |= CampaignButtons.Jump;
                        }
                        Step(ci);
                        if (launch >= 0 && me.Position.Y > lastPeak)
                        {
                            lastPeak = me.Position.Y;
                            lastPeakAt = f - launch;
                        }
                        if (me.Health == 0)
                        {
                            lastEnd = "died";
                            return null;
                        }
                        if (launch < 0)
                        {
                            if (PadTime() < padWas)
                            {
                                launch = f;
                            }
                            padWas = PadTime();
                            if (launch < 0 && f > 60)
                            {
                                lastEnd = $"no launch at {Fmt(me.Position)}";
                                return null;
                            }
                            continue;
                        }
                        settled = Grounded() && PadTime() > 20 ? settled + 1 : 0;
                        if (settled >= 15)
                        {
                            return me.Position;
                        }
                    }
                    lastEnd = $"still moving at {Fmt(me.Position)}";
                    return null;
                }
                var pads = new List<JumpPadEntity>();
                foreach (JumpPadEntity p in host.Scene.GetJumpPadEntities())
                {
                    pads.Add(p);
                }
                int padNo = 0, padsUp = 0;
                foreach (JumpPadEntity pad in pads)
                {
                    padNo++;
                    if (!pad.Active || PadFine > 0 && padNo != PadFine)
                    {
                        continue;
                    }
                    // floors the graph already lands on from this pad: its links, on through mid-air nodes (up to 4 deep)
                    var known = new HashSet<NodeData3>();
                    if (pad.ClosestNode != null)
                    {
                        var frontier = new List<NodeData3> { pad.ClosestNode };
                        var seen = new HashSet<NodeData3>(frontier);
                        for (int depth = 0; depth < 4 && frontier.Count > 0; depth++)
                        {
                            var next = new List<NodeData3>();
                            foreach (NodeData3 n in frontier)
                            {
                                foreach (NodeData3 t in linksOf[n])
                                {
                                    if (seen.Add(t))
                                    {
                                        if (t.NodeType == NodeType.Aerial)
                                        {
                                            next.Add(t);
                                        }
                                        else
                                        {
                                            known.Add(t);
                                        }
                                    }
                                }
                            }
                            frontier = next;
                        }
                    }
                    // the plain ride = what a bot does: stick held along the pad's push (no stick on a straight-up pad), no jump
                    Vector3 beam = ((Vector3)PadBeamField.GetValue(pad)!).WithY(0);
                    Vector3 b = beam.LengthSquared > 0.01f ? beam.Normalized() : -Vector3.UnitX;
                    Vector3? plain = Ride(pad, beam.LengthSquared > 0.01f ? b : Vector3.Zero, -1, b);
                    string plainEnd = plain == null ? lastEnd : "";
                    string plainPeak = $"peak {lastPeak:0.0} at f{lastPeakAt}";
                    NodeData3? plainNode = plain == null ? null : NearestFloorNode(plain.Value);
                    // "higher" = 2 above both the plain landing and every floor the graph already lands on from this pad
                    float baseY = Math.Max(plain?.Y ?? Single.MinValue, known.Count > 0 ? known.Max(k => k.Position.Y) : Single.MinValue);
                    if (baseY == Single.MinValue)
                    {
                        baseY = pad.Position.Y;
                    }
                    // per higher floor (its nearest node): tries that got there, and the first (steer, jump frame) seen
                    var up = new Dictionary<NodeData3, (int Count, string First, Vector3 Where)>();
                    int tries = 0, landed = 0, offGraph = 0;
                    string offAt = "", topHow = "";
                    Vector3 top = new Vector3(0, Single.MinValue, 0);
                    float stepDeg = PadFine > 0 ? 10 : 45;
                    int sCount = (int)(360 / stepDeg), jStep = PadFine > 0 ? 2 : 6;
                    for (int s = -1; s < sCount; s++)
                    {
                        float a = MathHelper.DegreesToRadians(s * stepDeg);
                        Vector3 steer = s < 0 ? Vector3.Zero
                            : new Vector3(b.X * MathF.Cos(a) - b.Z * MathF.Sin(a), 0, b.X * MathF.Sin(a) + b.Z * MathF.Cos(a));
                        for (int j = -1; j <= 120; j += j < 0 ? 1 + jStep : jStep)
                        {
                            tries++;
                            Vector3? end = Ride(pad, steer, j, b);
                            if (end == null)
                            {
                                continue;
                            }
                            landed++;
                            if (end.Value.Y > top.Y)
                            {
                                top = end.Value;
                                topHow = $"{(s < 0 ? "none" : $"{s * stepDeg:0}deg")}/j{j} peak {lastPeak:0.0}";
                            }
                            if (end.Value.Y < baseY + 2)
                            {
                                continue;
                            }
                            NodeData3? n = NearestFloorNode(end.Value);
                            if (n == null || (n.Position - end.Value).Xz.Length > 5 || MathF.Abs(n.Position.Y - end.Value.Y) > 2.5f)
                            {
                                offGraph++; // a floor up there with no route node near it
                                offAt = Fmt(end.Value);
                                continue;
                            }
                            if (known.Contains(n) || n == plainNode)
                            {
                                continue;
                            }
                            string how = $"{(s < 0 ? "none" : $"{s * stepDeg:0}deg")}/j{j}";
                            up[n] = up.TryGetValue(n, out var had) ? (had.Count + 1, had.First, had.Where) : (1, how, end.Value);
                        }
                    }
                    if (up.Count > 0)
                    {
                        padsUp++;
                    }
                    lines.Add($"P\t{arena.InGameName}\t{me.Hunter}\tpad{padNo}\t{pad.ClosestNode?.Id.ToString() ?? "-"}\t{Fmt(pad.Position)}"
                        + $"\tplain {(plain == null ? plainEnd : $"{Fmt(plain.Value)} node {plainNode?.Id.ToString() ?? "-"}")} {plainPeak}"
                        + $"\tknown {String.Join(",", known.Select(k => $"{k.Id}@{k.Position.Y:0.0}"))}\tbase {baseY:0.0}"
                        + $"\ttries {tries} landed {landed}\thigher {up.Count}\toffgraph {offGraph} {offAt}\ttop {Fmt(top)} {topHow}");
                    Console.WriteLine(lines[^1]);
                    foreach ((NodeData3 n, (int count, string first, Vector3 where)) in up.OrderByDescending(u => u.Value.Where.Y))
                    {
                        lines.Add($"H\t{arena.InGameName}\t{me.Hunter}\tpad{padNo}\tnode {n.Id} {Fmt(n.Position)}"
                            + $"\tup {where.Y - baseY:0.0}\tlanded {Fmt(where)}\t{count}/{tries}\tfirst {first}");
                        Console.WriteLine(lines[^1]);
                    }
                }
                lines.Add($"{arena.InGameName,-21} | {me.Hunter,-8} pads {padNo}, with a higher floor {padsUp}");
                Console.WriteLine(lines[^1]);
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
