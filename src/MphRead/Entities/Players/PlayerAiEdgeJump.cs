using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp (HostBotEdgeJump, MatchSettings.BotEdgeJump; replays before format 68 play without it), MPH's stuck fix on:
    // P2 of docs/BOT_MOVE_SOLVER.md, the runtime side of the link solver. Rules from the node and the floor only, no
    // arena or link ids (G1).
    //  - edge jump: following nodes, at the ledge toward her node (no floor a step ahead) where walking off lands below
    //    the node or nowhere (a gap, a step across to a ledge, a pit short of a lower ledge), or against a wall with her
    //    node above her: a jump. The P1 solver's "ledgejump" move, the one move that got 269 of the 357 links vanilla
    //    bots fail (link audit 10-26). A drop-off onto a floor at the node's height is walked, as vanilla. 3 per node
    //  - backstop (her clock, the stuck fix's 2/3 s steps, after its 2 climbs, in place of its give-up): step aside,
    //    replay the move (a jump toward the node), the next-best move (a run-up: back off and jump at the ledge or the
    //    spot), then the node is marked bad for 5 s and she replans. Marked, not dropped: after the 5 s it is hers again.
    //    The 2 climbs stay first: on some links (Weapons Complex 150->168) the first one at 2/3 s is what carries her
    //  - walked, not jumped, at a ledge: a gravity lift ahead, a jump pad below on the way, a node whose range takes in the lip, or a node no higher
    //    than her feet with no floor under it (out past the edge)
    //  - pad-launch jump: thrown by a jump pad, she gets her jump back once the pad's control lock is over (ActivateJumpPad
    //    clears UsedJump; the jump sets her upward speed, about 2.4 of height). Coming down from the throw, short of her
    //    node, when a jump pressed now reaches it: she jumps, as a player does (HostBotEdgePadJump, its own switch). Left
    //    alone: an aerial node (a point on the throw's arc), a throw that reaches her node, a fall that lands on a floor at
    //    the node's height, a jump whose arc meets a ledge face, and while she sees a foe. Careful rule (owner 10-27, link
    //    audit p2_pad7: 4 links fixed, none worse; a looser one jumped 7x as often and sent bots off walkways)
    public partial class PlayerEntity
    {
        public static bool HostBotEdgeJump { get; set; }
        // edge jumps, step-asides, run-ups, link marks since the start (Tools -botprobe prints them)
        public static readonly int[] HostEdgeCounts = new int[5];
        // the pad-launch jump (MatchSettings.BotEdgePadJump; replays before format 69 play without it), with the edge jump on
        public static bool HostBotEdgePadJump { get; set; }
        // recomp: no jump of the stuck fix's into a lift her route doesn't go up (owner GO 2026-10-28, Fuel Stack): her node
        // was the lift's base (175, inside its gravity volume) with her 4 units out on the floor; the climb threw her in over
        // the node, the lift carried her past it (never reached, so no next node up the shaft to steer by) and on into the
        // death volume over the top. Blocked climbs count as used, so her give-up can take the node as vanilla's would.
        // Covers the stuck fix's climbs, the backstop's jump rung and the edge jump's wall/run-up jump (its ledge jump has
        // EdgeLiftAhead). Also no edge jump while something throws her up faster than her own jump would (Harvester's
        // launch ramps to the far ends, 131->179: the jump cut her rise from 0.53 to 0.28, she clipped the wall at z -48.7
        // and fell short into the pit). Off = before (MatchSettings.BotLiftGuard; replays before format 71 play without it)
        public static bool HostBotLiftGuard { get; set; }
        // recomp: only a gravity volume that pushes up is a lift to the edge jump and the lift guard (owner GO 2026-10-29).
        // Weapons Complex's centre and north pits sit in low-gravity volumes (gravity -38: floaty jumps across, no lift), and
        // as lifts they held back every ledge jump over them: bots walked off the ledge and fell in (2.28 falls per bot in
        // 4 min, vanilla 1.44). Off = every gravity volume (MatchSettings.BotUpLifts; replays before format 72 play without it)
        public static bool HostBotUpLifts { get; set; }
        // recomp: vanilla's bot sees it reached its next node once per 30 Hz tick (its position moves a tick's step at a
        // time); MphRead checked every 60 Hz frame, so a bot took a node's jump about a frame early. On Weapons Complex's
        // north ledge (node 40 -> 222) the early jump bumps the lip and drops her into the pit: 15% of tries, vanilla 6%.
        // On: the arrival (PlayerAi.cs Func2140584) is checked on tick frames only (TickFrame, the same phase as the rest
        // of the tick pace): WC falls per bot 1.90 -> 1.38, vanilla 1.44 (2026-10-29). Off = every frame
        // (MatchSettings.BotArriveTick; replays before format 72 play without it)
        public static bool HostBotArriveTick { get; set; }
        // recomp: an edge jump keeps its node until she lands (owner GO 2026-10-29, Harvester). In the air her tree can hand
        // her a new node: MPH's nearest-node pick takes the first of the 10 closest it sees (UpdateGlobals), and from the air
        // over Harvester's top platform that was an aerial node of the launch arc out over the gap (147), not one she can
        // walk to; she steered for it and fell to the floor (4 of 23 falls in 20 matches; with the edge jump off bots stay on
        // the platform). Vanilla's bot is never up there in a jump of its own. Held until she lands (or a pad throws her,
        // or she dies). Off = before (MatchSettings.BotEdgeHold; replays before format 73 play without it)
        public static bool HostBotEdgeHold { get; set; }
        // nodes an edge jump's flight kept since the start (Tools -botprobe prints it)
        public static int HostEdgeHoldCount;
        // climbs and jumps the lift guard held back since the start (Tools -botprobe prints it)
        public static int HostLiftGuardCount;
        // lift escapes since the start: steps a lift carried a bot toward a death volume and she pushed out for a ledge
        public static int HostLiftEscapeCount;

        public partial class PlayerAiData
        {
            private const int EdgeTriesMax = 3;
            private const int EdgeAsideFrames = 6 * 2; // todo: FPS stuff
            private const int EdgeBackFrames = 8 * 2; // todo: FPS stuff
            private const int EdgeBadFrames = 150 * 2; // todo: FPS stuff
            private NodeData3? _edgeNode = null;
            private int _edgeTries = 0;
            private bool _edgeJumped = false; // an edge jump on the way to _edgeNode
            private bool _edgeFlight = false; // (HostBotEdgeHold) in the air from an edge jump: _edgeNode stays her node
            private int _edgeFlightSteps = 0; // AI steps since the edge jump (EdgeHold)
            private bool _edgeAirborne = false; // she has left the ground since the edge jump (EdgeHold)
            private int _edgeRung = 0; // the backstop's next step in this spot
            private int _edgeAside = 0; // stepping aside: steps so far (0 = not)
            private float _edgeSide = 1; // which side the next step aside goes
            private int _edgeBack = 0; // the run-up's backing off: steps so far (0 = not)
            private bool _edgeRunUp = false; // backed off: a jump at the ledge, the wall or the spot she backed off from
            private Vector3 _edgeRunFrom = Vector3.Zero;
            private readonly (ushort Id, long Until)[] _edgeBad = new (ushort, long)[8];
            private int _edgePadPress = 0; // the pad-launch jump's press: frames so far (0 = not this throw)
            private NodeData3? _liftEscapeNode = null; // (HostBotLiftGuard) the ledge a lift escape pushes her out for
            private int _liftEscapeHold = 0; // (HostBotLiftGuard) steps the push goes on once she's out of the lift

            private bool EdgeJumpOn => !HostBotUnstick && HostBotVanillaParity && HostBotEdgeJump;

            private bool EdgeWalking => _buttons.A.IsDown || _buttons.B.IsDown || _buttons.X.IsDown || _buttons.Y.IsDown;

            // every AI step while she follows nodes and sees no foe (BotUnstickTick, where the stuck fix's clock runs)
            private void BotEdgeJump()
            {
                if (_node40 != _edgeNode)
                {
                    if (_edgeJumped && _edgeNode != null && (_edgeNode.Position - _player.Position).LengthSquared
                        < (_edgeNode.MaxDistance + 0.5f) * (_edgeNode.MaxDistance + 0.5f))
                    {
                        // she jumped her way to her node: the stuck fix's clock starts again here. Its spot was where
                        // she stood before the jump, and a climb 2/3 s into the next link threw her past it (Oubliette
                        // 22->121). Walked links keep MPH's clock as it runs (Weapons Complex 150->168 needs its climb)
                        _unstickAt = _player.Position;
                        _unstickFrames = 0;
                        _unstickClimbs = 0;
                        _unstickDrifts = 0;
                        EdgeSpotReset();
                    }
                    _edgeNode = _node40;
                    _edgeJumped = false;
                    _edgeTries = 0;
                    _edgeRunUp = false;
                }
                if (_edgeAside > 0)
                {
                    BotEdgeAside();
                    return;
                }
                if (_edgeBack > 0)
                {
                    BotEdgeBack();
                    return;
                }
                if (_node40 == null || _unstickClimb > 0 || _unstickDrift > 0 || _player.IsAltForm || _player.IsMorphing
                    || _player.Health == 0 || !_player.Flags1.TestFlag(PlayerFlags1.Grounded) || !EdgeWalking
                    || _edgeTries >= EdgeTriesMax && !_edgeRunUp)
                {
                    return;
                }
                if (HostBotLiftGuard && (_player.Flags1.TestFlag(PlayerFlags1.UsedJumpPad)
                    || _player.Speed.Y > Fixed.ToFloat(_player.Values.JumpSpeed)))
                {
                    return; // already thrown up past a jump's speed: a jump now only cuts her rise
                }
                Vector2 to = (_node40.Position - _player.Position).Xz;
                float across = to.Length;
                if (across < 0.5f)
                {
                    return;
                }
                Vector2 dir = to / across;
                float up = _node40.Position.Y - (_player.Position.Y - 0.5f); // her feet: half a unit under her position
                bool go;
                if (!EdgeFloor(dir, 0.7f, _player.Position.Y - 1.2f))
                {
                    // at the ledge: jump unless a step off lands on a floor no lower than a unit under her node
                    float low = System.MathF.Min(_player.Position.Y - 1.2f, _node40.Position.Y - 1);
                    // and not when her node's own range takes in the lip (a node out over the edge: reached by walking on)
                    Vector3 lip = _player.Position + new Vector3(dir.X, 0, dir.Y) * 0.7f;
                    bool reach = (_node40.Position - lip).AddY(0.5f).LengthSquared < _node40.MaxDistance * _node40.MaxDistance
                        || up < 0.3f && !EdgeNodeFloor(); // a node no higher than her feet, in the air past the edge
                    go = !reach && !EdgeFloor(dir, 0.7f, low) && !EdgeFloor(dir, 1.5f, low) && !EdgeLiftAhead(dir) && !EdgePadBelow();
                }
                else
                {
                    go = (up > 0.6f && BotWallAhead(dir)
                        || _edgeRunUp && (_player.Position - _edgeRunFrom).Xz.LengthSquared < 0.4f * 0.4f) && !LiftGuardBlocks();
                }
                if (go)
                {
                    _edgeTries++;
                    _edgeJumped = true;
                    _edgeFlight = true;
                    _edgeFlightSteps = 0;
                    _edgeAirborne = false;
                    _edgeRunUp = false;
                    HostEdgeCounts[0]++;
                    _unstickClimb = 1;
                    _unstickSecond = true; // one press (no second in the air), as the solver's move
                    BotClimb();
                }
            }


            // every AI step, before her tree runs (PlayerAi.cs): (HostBotEdgeHold) an edge jump's flight keeps its node, from
            // the jump until she is back on the ground. Not only a climb's flight: on Harvester's top platform her edge jumps
            // from node to node are short hops, and the pick in the air took the launch arc's node all the same (2 of 4
            // falls). A pad's throw, death or 4 s end it
            private void EdgeHold()
            {
                if (!_edgeFlight)
                {
                    return;
                }
                bool grounded = _player.Flags1.TestFlag(PlayerFlags1.Grounded);
                if (!grounded)
                {
                    _edgeAirborne = true;
                }
                if (!HostBotEdgeHold || !BotUnstickOn || !EdgeJumpOn || _edgeNode == null || _player.Health == 0
                    || grounded && (_edgeAirborne || _edgeFlightSteps > 10 * 2) || ++_edgeFlightSteps > 120 * 2 // todo: FPS stuff
                    || _player.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                {
                    _edgeFlight = false;
                    return;
                }
                if (_node40 != _edgeNode)
                {
                    _node40 = _edgeNode;
                    HostEdgeHoldCount++;
                }
            }

            // every AI step of a jump pad's throw (BotUnstickTick): the pad-launch jump. Coming down from the throw with
            // her node out of reach, when a jump pressed now still reaches it: she jumps, as a player does
            private void BotEdgePadJump(bool travel)
            {
                if (_edgePadPress > 0)
                {
                    if (_edgePadPress++ <= 4 * 2) // todo: FPS stuff
                    {
                        _buttons.L.IsDown = true;
                    }
                    return;
                }
                // an aerial node is a point on the throw's own arc (vanilla reaches it in the air): the pad carries her there
                if (_node40 == null || _node40.NodeType == NodeType.Aerial || !travel || _player.IsAltForm
                    || _player.IsMorphing || _player.Health == 0)
                {
                    return;
                }
                if (!_player.Flags1.TestFlag(PlayerFlags1.UsedJumpPad))
                {
                    return;
                }
                if (_player.Flags1.TestFlag(PlayerFlags1.UsedJump))
                {
                    return;
                }
                if (_player._jumpPadControlLockMin > 0)
                {
                    return;
                }
                if (_player.Speed.Y > 0)
                {
                    return;
                }
                // in ticks (speed per tick, gravity per tick squared): when her fall reaches the node's height, and how far
                // toward the node she has gone by then, as she is and with the jump pressed now (the jump sets her upward speed)
                Vector3 to = _node40.Position - _player.Position;
                float across = System.MathF.Sqrt(to.X * to.X + to.Z * to.Z);
                if (across < 0.5f)
                {
                    return;
                }
                float toward = (_player.Speed.X * to.X + _player.Speed.Z * to.Z) / across;
                float dy = _node40.Position.Y - (_player.Position.Y - 0.5f); // her feet: half a unit under her position
                float g = Fixed.ToFloat(_player.Values.BipedGravity);
                float jump = Fixed.ToFloat(_player.Values.JumpSpeed);
                float fall = EdgeFallTicks(_player.Speed.Y, dy, g);
                float fallJump = EdgeFallTicks(jump, dy, g);
                if (toward <= 0 || fall >= 0 && toward * fall >= across - 1)
                {
                    return;
                }
                if (fallJump < 0 || toward * fallJump < across - 1)
                {
                    return;
                }
                if (EdgePadArcLands(_player.Speed.Y, g))
                {
                    return; // the fall as she is comes down on a floor at the node's height: she walks the rest
                }
                if (!EdgePadArcClear(jump, dy, g))
                {
                    return;
                }
                if (UnstickSeesFoe())
                {
                    return;
                }
                _edgePadPress = 1;
                HostEdgeCounts[4]++;
                _buttons.L.IsDown = true;
            }

            // ticks until a fall starting at this upward speed comes down to dy over her feet (-1: it never gets that high)
            private static float EdgeFallTicks(float vy, float dy, float g)
            {
                float disc = vy * vy + 2 * g * dy;
                return disc < 0 ? -1 : (-vy - System.MathF.Sqrt(disc)) / g;
            }

            // her arc at this upward speed, tick by tick (horizontal speed as she is): the first thing it meets is a floor at
            // about her node's height, coming down
            private bool EdgePadArcLands(float vy, float g)
            {
                var result = new CollisionResult();
                Vector3 prev = _player.Position;
                for (int t = 2; t <= 160; t += 2)
                {
                    Vector3 at = _player.Position + new Vector3(_player.Speed.X * t, vy * t + 0.5f * g * t * t, _player.Speed.Z * t);
                    if (CollisionDetection.CheckBetweenPoints(prev, at, TestFlags.Players, _scene, ref result))
                    {
                        return vy + g * t < 0 && result.Plane.Y >= 0.5f && result.Position.Y >= _node40!.Position.Y - 1.5f;
                    }
                    prev = at;
                }
                return false;
            }

            // the jump's arc, tick by tick, while she is still under her node's height: nothing in the way (a ledge's face
            // she would rise into short of its top)
            private bool EdgePadArcClear(float jump, float dy, float g)
            {
                var result = new CollisionResult();
                Vector3 prev = _player.Position;
                for (int t = 2; t <= 120; t += 2)
                {
                    float up = jump * t + 0.5f * g * t * t;
                    Vector3 at = _player.Position + new Vector3(_player.Speed.X * t, up, _player.Speed.Z * t);
                    if (CollisionDetection.CheckBetweenPoints(prev, at, TestFlags.Players, _scene, ref result))
                    {
                        return false;
                    }
                    if (up >= dy + 0.5f || jump + g * t <= 0)
                    {
                        return true; // over the node's height (what comes down after is the landing), or the top of the arc
                    }
                    prev = at;
                }
                return true;
            }

            // a floor under the point this far ahead, between half a unit over her position and down to y
            private bool EdgeFloor(Vector2 dir, float ahead, float y)
            {
                var result = new CollisionResult();
                Vector3 at = _player.Position + new Vector3(dir.X, 0, dir.Y) * ahead;
                return CollisionDetection.CheckBetweenPoints(at.AddY(0.5f), at.WithY(y), TestFlags.Players, _scene, ref result);
            }

            // a floor under her node (from half a unit over it to 1.5 under): a ledge to land on, not a node over the edge
            private bool EdgeNodeFloor()
            {
                var result = new CollisionResult();
                Vector3 at = _node40!.Position;
                return CollisionDetection.CheckBetweenPoints(at.AddY(0.5f), at.AddY(-1.5f), TestFlags.Players, _scene, ref result);
            }

            // a gravity volume that's a lift: one pushing up (HostBotUpLifts), or any (before)
            private static bool IsLift(AreaVolumeEntity av)
            {
                return av.Data.InsideMessage == Message.Gravity && (!HostBotUpLifts || av.Data.InsideMsgParam1 > 0);
            }

            // a gravity volume (a lift) past the ledge: walked into, as vanilla. Jumped into, she rides it in the air with
            // her climb's steering and can be carried past the exit (Fuel Stack's shaft into its death volume)
            private bool EdgeLiftAhead(Vector2 dir)
            {
                foreach (EntityBase entity in _scene.Entities)
                {
                    if (entity is AreaVolumeEntity av && av.Active && IsLift(av))
                    {
                        CollisionVolume vol = CollisionVolume.Move(av.Data.Volume, av.Position);
                        for (float ahead = 0.7f; ahead < 2.4f; ahead += 0.8f)
                        {
                            Vector3 at = _player.Position + new Vector3(dir.X, 0, dir.Y) * ahead;
                            if (vol.TestPoint(at) || vol.TestPoint(at.AddY(-1.5f)))
                            {
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            // (HostBotLiftGuard) a jump from here toward her node comes down in a lift (a gravity volume), and her node isn't
            // above her (a node up the shaft is a route up the lift: let her jump in). The whole way to her node, not only a
            // jump's length: Fuel Stack's ledges beside the shaft, with her node in the shaft below, a climb carried her off
            // the ledge into it and up into the death volume over the top
            private bool LiftGuardBlocks()
            {
                if (!HostBotLiftGuard || _node40 == null || _node40.Position.Y > _player.Position.Y + 2)
                {
                    return false;
                }
                Vector2 to = (_node40.Position - _player.Position).Xz;
                float len = to.Length;
                Vector2 dir = len > 0.1f ? to / len : Vector2.Zero;
                float reach = System.MathF.Max(3.2f, len);
                foreach (EntityBase entity in _scene.Entities)
                {
                    if (entity is AreaVolumeEntity av && av.Active && IsLift(av))
                    {
                        CollisionVolume vol = CollisionVolume.Move(av.Data.Volume, av.Position);
                        for (float ahead = 0; ahead < reach; ahead += 0.8f)
                        {
                            Vector3 at = _player.Position + new Vector3(dir.X, 0, dir.Y) * ahead;
                            if (vol.TestPoint(at) || vol.TestPoint(at.AddY(1.5f)) || vol.TestPoint(at.AddY(3)))
                            {
                                HostLiftGuardCount++;
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            // (HostBotLiftGuard) a lift carrying her up into a death volume over its top (Fuel Stack's centre shaft): out
            // for the nearest node off the shaft that her rise still reaches, or straight out once the volume is close.
            // Vanilla's bots steer for the top exit only once the shaft's last node is behind them, a few units under the
            // volume, and most rides end in it; with the stuck fix keeping them moving they ride it twice as often
            // (10-28: 31 deaths over the top in 15 bot-matches, vanilla 16)
            private bool BotLiftEscape()
            {
                if (!HostBotLiftGuard || UnstickGrounded || _player.Speed.Y <= 0 || _player.Health == 0)
                {
                    return false;
                }
                Vector3 pos = _player.Position;
                CollisionVolume? lift = null;
                float deathY = System.Single.MaxValue;
                foreach (EntityBase entity in _scene.Entities)
                {
                    if (entity is AreaVolumeEntity av && av.Active)
                    {
                        CollisionVolume vol = CollisionVolume.Move(av.Data.Volume, av.Position);
                        if (IsLift(av) && vol.TestPoint(pos))
                        {
                            lift = vol;
                        }
                        else if (av.Data.InsideMessage == Message.Death)
                        {
                            for (float up = 0; up < 16; up += 1)
                            {
                                if (vol.TestPoint(pos.AddY(up)))
                                {
                                    deathY = System.MathF.Min(deathY, pos.Y + up);
                                    break;
                                }
                            }
                        }
                    }
                }
                if (lift == null || deathY == System.Single.MaxValue)
                {
                    // out of the shaft: the push goes on a little longer (her own steering for a node across the shaft
                    // took her back into it, 10-28)
                    if (lift == null && _liftEscapeHold > 0 && _liftEscapeNode != null)
                    {
                        _liftEscapeHold--;
                        LiftEscapePush(_liftEscapeNode);
                        return true;
                    }
                    _liftEscapeHold = 0;
                    return false;
                }
                float g = System.MathF.Abs(Fixed.ToFloat(_player.Values.BipedGravity));
                float vy = _player.Speed.Y;
                // the lift carries her on up to its top and gives her at least this speed there
                float liftTop = pos.Y;
                while (liftTop < pos.Y + 40 && lift.Value.TestPoint(new Vector3(pos.X, liftTop + 0.5f, pos.Z)))
                {
                    liftTop += 0.5f;
                }
                float top = liftTop + vy * vy / (2 * g);
                bool Ledge(NodeData3 node) => node.Position.Y <= deathY - 0.5f && node.Position.Y >= pos.Y - 4
                    && !lift.Value.TestPoint(node.Position) && !lift.Value.TestPoint(node.Position.AddY(1));
                // her own node when it's off the shaft (the top exit), or the nearest one
                NodeData3? best = _node40 != null && Ledge(_node40) ? _node40 : null;
                float bestDist = System.Single.MaxValue;
                if (best == null && _nodeList != null)
                {
                    foreach (NodeData3 node in _nodeList)
                    {
                        float dist = (node.Position - pos).Xz.LengthSquared;
                        if (dist < bestDist && Ledge(node))
                        {
                            best = node;
                            bestDist = dist;
                        }
                    }
                }
                bool close = deathY - pos.Y < 6;
                if (best == null || !close && top < best.Position.Y + 1)
                {
                    return false; // the lift carries her on until her rise reaches the ledge
                }
                _liftEscapeNode = best;
                _liftEscapeHold = 15 * 2; // todo: FPS stuff
                LiftEscapePush(best);
                return true;
            }

            private void LiftEscapePush(NodeData3 node)
            {
                Vector2 to = (node.Position - _player.Position).Xz;
                to = to.LengthSquared > 0.01f ? to.Normalized() * 0.2f : new Vector2(0.2f, 0);
                _player.Speed = new Vector3(to.X, _player.Speed.Y, to.Y);
                _buttons.A.IsDown = false;
                _buttons.B.IsDown = false;
                _buttons.X.IsDown = false;
                _buttons.Y.IsDown = false;
                _unstickDrift = 0;
                _unstickDriftDown = false;
                _unstickClimb = 0;
                HostLiftEscapeCount++;
            }

            // an active jump pad below her, on the line to her node: walked off onto, as vanilla (Oubliette's pad under
            // the walkway's gap: jumped, she flies over it and falls short)
            private bool EdgePadBelow()
            {
                Vector2 from = _player.Position.Xz;
                Vector2 line = _node40!.Position.Xz - from;
                float len2 = line.LengthSquared;
                foreach (EntityBase entity in _scene.Entities)
                {
                    if (entity is JumpPadEntity pad && pad.Active && pad.Position.Y < _player.Position.Y - 1
                        && pad.Position.Y > _node40.Position.Y - 8)
                    {
                        Vector2 at = pad.Position.Xz - from;
                        float t = len2 > 0 ? System.Math.Clamp(Vector2.Dot(at, line) / len2, 0, 1) : 0;
                        if ((at - line * t).LengthSquared < 1.5f * 1.5f)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }

            // the backstop's step on her clock (on the ground, stuck short of her node), in place of the stuck fix's climbs
            private void BotEdgeRung()
            {
                int rung = _edgeRung++;
                if (rung == 0)
                {
                    // step aside: half a second across the link (a lip or a rail's end in front of her), sides in turn
                    _edgeAside = 1;
                    HostEdgeCounts[1]++;
                    BotEdgeAside();
                }
                else if (rung == 1)
                {
                    // replay the move: a jump toward the node now (not into a lift: HostBotLiftGuard)
                    _unstickClimbs++;
                    if (!LiftGuardBlocks())
                    {
                        _unstickClimb = 1;
                        HostUnstickCounts[1]++;
                        BotClimb();
                    }
                }
                else if (rung == 2)
                {
                    // the next-best move: a run-up, then the jump at the ledge, the wall or the spot she backed off from
                    _edgeRunFrom = _player.Position;
                    _edgeBack = 1;
                    HostEdgeCounts[2]++;
                    BotEdgeBack();
                }
            }

            // all three rungs tried in this spot: BotUnstick's give-up may run (it marks the node instead of dropping it)
            private bool EdgeRungsDone => _edgeRung >= 3 && _edgeAside == 0 && _edgeBack == 0;

            private bool EdgeBusy => _edgeAside > 0 || _edgeBack > 0;

            private void BotEdgeAside()
            {
                if (_node40 == null || _edgeAside++ > EdgeAsideFrames || _player.Health == 0)
                {
                    _edgeAside = 0;
                    _edgeSide = -_edgeSide;
                    return;
                }
                Vector2 to = (_node40.Position - _player.Position).Xz;
                Vector2 side = to.LengthSquared > 0.01f ? new Vector2(-to.Y, to.X).Normalized() * _edgeSide : new Vector2(_edgeSide, 0);
                BotEdgeSteer(side);
            }

            private void BotEdgeBack()
            {
                if (_node40 == null || _edgeBack++ > EdgeBackFrames || _player.Health == 0)
                {
                    _edgeBack = 0;
                    _edgeRunUp = _node40 != null && _player.Health != 0;
                    return;
                }
                Vector2 to = (_node40.Position - _player.Position).Xz;
                Vector2 away = to.LengthSquared > 0.01f ? -to.Normalized() : new Vector2(1, 0);
                // not off a ledge behind her
                if (_player.Flags1.TestFlag(PlayerFlags1.Grounded) && !EdgeFloor(away, 0.7f, _player.Position.Y - 1.2f))
                {
                    _edgeBack = 0;
                    _edgeRunUp = true;
                    return;
                }
                BotEdgeSteer(away);
            }

            // hands off the stick, her speed across the floor this way (the drift's way of moving her)
            private void BotEdgeSteer(Vector2 dir)
            {
                _buttons.A.IsDown = false;
                _buttons.B.IsDown = false;
                _buttons.X.IsDown = false;
                _buttons.Y.IsDown = false;
                Vector2 v = dir * 0.1f; // 1.2 aside, 1.6 back: inside the stuck fix's 2-unit spot
                _player.Speed = new Vector3(v.X, _player.Speed.Y, v.Y);
            }

            // the give-up's mark: this node is off her picks for 5 s, from anywhere, then hers again
            private void EdgeMarkBad(ushort id)
            {
                long now = (long)_scene.FrameCount;
                int slot = 0;
                for (int i = 0; i < _edgeBad.Length; i++)
                {
                    if (_edgeBad[i].Id == id && _edgeBad[i].Until > now)
                    {
                        slot = i;
                        break;
                    }
                    if (_edgeBad[i].Until < _edgeBad[slot].Until)
                    {
                        slot = i;
                    }
                }
                _edgeBad[slot] = (id, now + EdgeBadFrames);
                HostEdgeCounts[3]++;
            }

            private bool EdgeMarked(int id)
            {
                long now = (long)_scene.FrameCount;
                foreach ((ushort bad, long until) in _edgeBad)
                {
                    if (bad == id && until > now)
                    {
                        return true;
                    }
                }
                return false;
            }

            private void EdgeSpotReset()
            {
                _edgeRung = 0;
                _edgeAside = 0;
                _edgeBack = 0;
                _edgeRunUp = false;
            }
        }
    }
}
