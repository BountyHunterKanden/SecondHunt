using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: bots that can't get to their next nav node in Echoes arenas (owner 2026-10-09: "stuck rapidly moving back
    // and forth"). Our generated graphs (Core Arenas/ArenaNav) can't promise every link the way the vanilla hand-made ones
    // do: a bot knocked into Pipeline's flooded cave, perched on a ledge that isn't floor, or rolled into a lip dithers
    // on a node it never reaches, and MphRead's own way out (PlayerAi: blocked sideways for a second in alt form) rarely
    // fires. Host-guarded: Core turns HostBotUnstick on only for an Echoes arena whose nav file the fixed importer made
    // (arena.json NavVersion >= 2), so MPH's arenas and every older import play exactly as before.
    //   - "one spot" = within 2 units across the floor (XZ: a bot bobbing up and down a steep edge is still stuck there)
    //   - every 1.5 s in one spot with her node above her or out of reach: out of the ball when it is above her (twice at
    //     most); on the ground or under water a jump with a second press near the top (Echoes' double jump; held under
    //     water = the Gravity Boost), 3 per spot (ledges, railings, curbs); off the ground (held up by a steep edge that
    //     isn't floor: Spider Complex, 2026-10-10) half a second of drift toward her node, 3 per spot
    //   - 3 s in one spot while following nodes (jumps used up): MphRead's own give-up (the node goes on the avoid list),
    //     but the next node is the closest one she can see (MphRead's closest node can be behind a wall: she gave up on
    //     the same one every 3 s), and the nodes she gave up on stay off every node pick until she leaves the spot
    //   - MphRead's jump toward a node ends when she lands (Echoes' double jump left it holding L for good), and a node on
    //     a jump pad counts as reached when the pad throws her (she was never on the ground there)
    //   - the clock (jumps, drifts, give-up) runs only while she is trying to move, and the node she
    //     falls back to is never the one she stands on (2026-10-17: Kanden circling a node in Shooting Gallery)
    //   - hands off the stick from a pad's launch until she lands; held up below her node, the drift goes away from it,
    //     down off the slope (2026-10-10, once the graph had the pads and Spider Complex's top)
    // T7 (owner 2026-10-10), on with HostBotMoves (Core: an Echoes arena with the unstick rule, in a match recorded from
    // replay format 25):
    //   - Echoes' moves on the graph's links (arena.json NavLinks, ArenaNav version 3; N24 "Do bots know they have double
    //     jump and gravity boost in echoes maps"): following a jump or double-jump link she jumps as she reaches the ledge
    //     (second press at the top), a gap link from its edge, a boost link out of water held (the Gravity Boost); out of
    //     the ball first. 3 tries per link
    //   - items she can't get: one under a breakable's top (Shooting Gallery's pool items under the grates: Noxus and Sylux
    //     circled over them doing climbs, "jumping back and forth on a grate"), or one she has been beside for 3 s without
    //     picking it up (skipped for 20 s)
    public partial class PlayerEntity
    {
        public static bool HostBotUnstick { get; set; }
        public static bool HostBotMoves { get; set; }
        // recomp (HostBotStuckVertical, MatchSettings.BotStuckVertical; replays before format 67 play without it): MPH's
        // stuck fix doesn't count a step she is off the ground and moving up or down (a fall, a lift's shaft). Its "one
        // spot" is across the floor only, so a bot dropping into a lift and carried up the middle ran the clock and the
        // fix's drift pushed her out of the shaft mid-ride (link audit 10-26: 40 links vanilla gets along that the fix
        // broke, Fuel Stack's lift links among them). The clock waits once she has been in the air longer than a jump of
        // her own, nothing starts it over. And while a lift carries her up short of her node, hands off (BotLiftRide):
        // her stick and jumps steered her off the rising lift
        public static bool HostBotStuckVertical { get; set; }
        // this room's link kinds: (from node id, to node id) -> ArenaNav.Link* (1 jump, 2 double jump, 3 gap, 4 boost)
        public static System.Collections.Generic.IReadOnlyDictionary<(int, int), int>? HostNavLinks { get; set; }
        // unmorphs, climbs, give-ups, drifts, link jumps, items skipped since the start (Tools -echoesarena jitter prints them)
        public static readonly int[] HostUnstickCounts = new int[6];
        // each give-up, when set (Tools -echoesarena jitter, env GIVEUPS=1): the bot, the node it gave up on
        public static System.Action<PlayerEntity, NodeData3>? HostUnstickGiveUp { get; set; }

        public partial class PlayerAiData
        {
            private const int UnstickFrames = 45 * 2; // todo: FPS stuff
            // MPH's stuck fix: sooner (a jump every 2/3 s, 2 of them, the give-up at 2 s). Vanilla's own pinned spells run
            // 2-5 s (stuck.py, 10-25), the Echoes cadence left most of them alone
            private const int UnstickFramesMph = 20 * 2; // todo: FPS stuff
            private int UnstickStep => HostBotUnstick ? UnstickFrames : UnstickFramesMph;
            private int UnstickClimbsMax => HostBotUnstick ? 3 : 2;
            private int UnstickGiveUpFrames => HostBotUnstick ? UnstickFrames * 2 : UnstickFramesMph * 3;
            // drifts per spot: 3 left a bot wedged at the void beside Spider Complex's east ledge for 47 s, strafing back
            // into it at her destination after each one (2026-10-10)
            private const int UnstickDriftsMax = 20;
            private Vector3 _unstickAt = Vector3.Zero;
            private int _unstickFrames = 0;
            private int _unstickClimbs = 0;
            private int _unstickClimb = 0;
            private bool _unstickSecond = false;
            private int _unstickUnmorphs = 0;
            private int _unstickDrifts = 0;
            private int _unstickDrift = 0;
            private float _unstickLastY = 0; // (HostBotStuckVertical) her height at the last step
            private int _unstickAir = 0; // (HostBotStuckVertical) steps off the ground in a row
            private float _unstickLastVy = 0; // (HostBotStuckVertical) her upward speed at the last step
            private bool _unstickRide = false; // (HostBotStuckVertical) carried up and still rising since
            private int _unstickGains = 0; // (HostBotStuckVertical) steps in a row carried
            // the nodes she gave up on in this spot: MphRead's own avoid list (_field7A) is cleared whenever her behaviour
            // changes state, and the closest node comes back (node 108 in Spider Complex, 43 give-ups in 4 min)
            private readonly ushort[] _unstickAvoid = new ushort[8];
            private int _unstickAvoidCount = 0;
            private NodeData3? _unstickSeen = null;
            private bool _unstickSeenValid = false;
            private bool _unstickJumpAir = false;
            private int _unstickPadFlight = 0;
            private bool _unstickDriftDown = false;
            // T7: the link she is on (her node before this one), its kind, tries
            private NodeData3? _navLast = null;
            private NodeData3? _navFrom = null;
            private int _navKind = 0;
            private int _navTries = 0;
            private int _botTicks = 0;
            // MPH's stuck fix (HostBotStuckFix): Func2140584 ran this step (she is following nodes, not fighting)
            private long _unstickTravelAt = -100; // the last frame she followed nodes (Func2140584)
            // T7: the item she is beside without getting it, for how long; items skipped (entity id, until _botTicks)
            private ItemInstanceEntity? _itemNear = null;
            private int _itemNearFrames = 0;
            private readonly (int Id, int Until)[] _itemSkip = new (int, int)[8];
            private int _itemCoveredAt = -1;
            private readonly System.Collections.Generic.Dictionary<int, bool> _itemCovered = new();

            // Tools traces: the execution path (Func24Id per depth)
            public string HostDebugState()
            {
                var ids = new System.Collections.Generic.List<int>();
                for (int i = 0; i < _executionTree.Length && _executionTree[i] != null; i++)
                {
                    ids.Add(_executionTree[i].Func24Id);
                    if (_executionTree[i].Data1.Data1.Count == 0) break;
                }
                return string.Join(">", ids) + $" unstick {_unstickFrames}";
            }

            // Tools traces: following nodes this step (1/0), and the unstick clock
            public string HostUnstickState => $"{((long)_scene.FrameCount - _unstickTravelAt <= 2 ? 1 : 0)}:{_unstickFrames} pin {_sightPin} try {UnstickTrying} gnd {UnstickGrounded} still {_padStill}";

            public bool HostUnstickSeesFoe => UnstickSeesFoe(); // (Tools traces)

            // a live opponent in her sight (the AI's own visibility table, no ray here)
            private bool UnstickSeesFoe()
            {
                foreach (PlayerEntity other in _scene.GetPlayerEntities())
                {
                    if (other != _player && other.TeamIndex != _player.TeamIndex && other.Health != 0
                        && IsPlayerVisible(_player, other))
                    {
                        return true;
                    }
                }
                return false;
            }

            private bool UnstickAbove => _node40 != null && _node40.Position.Y - _player.Position.Y > 1;
            // her node is out of MphRead's reach (IsNodeInRange's distance, from half a unit up: 1.12 across the floor
            // but 0.2 up a curb is out of it, and no jump came: Crossfire Chaos, 2026-10-10)
            private bool UnstickAway => _node40 != null && !UnstickAt(_node40);
            private bool UnstickAt(NodeData3 node) => (node.Position - _player.Position.AddY(_player.IsAltForm
                ? Fixed.ToFloat(_player.Values.AltColYPos) - Fixed.ToFloat(_player.Values.AltColRadius) : -0.5f)).LengthSquared
                < node.MaxDistance * node.MaxDistance;
            // her node is out of reach across the floor (the drift: a jump straight over the node is not stuck)
            private bool UnstickAwayXz => _node40 != null && (_node40.Position - _player.Position).Xz.LengthSquared > 1.5f * 1.5f;
            // this step's stick (A/B/X/Y walk; in the ball without strafe the d-pad rolls) or off the ground (held up on an
            // edge, no stick: Func2140094 steers only from the ground). End of Process: ProcessInput hasn't taken them yet
            // recomp (HostBotStuckSight): how long she has tried to move from one spot while following nodes and seeing a
            // foe. The stuck fix's clock waits while she sees one, so a bot walking into a wall in sight of the human stood
            // there until vanilla's node timeout and past it (Oubliette 10-26: Spire 9.5 s, re-picking the same node).
            // After 1.5 s the clock runs anyway: give-up by about 5 s, inside vanilla's own 2-5 s pinned spells
            private const int SightPinFrames = 45 * 2; // todo: FPS stuff
            private Vector3 _sightPinAt = Vector3.Zero;
            private int _sightPin = 0;
            private int _padStill = 0;
            private Vector3 _padStillAt = Vector3.Zero;

            // (only leaving the spot starts it over: a frame not trying, off her nodes or out of sight, as when she re-picks
            // the node, only isn't counted)
            private bool SightPinned(bool counting)
            {
                if ((_player.Position - _sightPinAt).Xz.LengthSquared > 2 * 2 || _player.Health == 0)
                {
                    _sightPinAt = _player.Position;
                    _sightPin = 0;
                    return false;
                }
                if (counting && UnstickTrying && _sightPin < SightPinFrames)
                {
                    _sightPin++;
                }
                return counting && _sightPin >= SightPinFrames;
            }

            private bool UnstickTrying => _buttons.A.IsDown || _buttons.B.IsDown || _buttons.X.IsDown || _buttons.Y.IsDown
                || _player.IsAltForm && (_buttons.Up.IsDown || _buttons.Down.IsDown || _buttons.Left.IsDown || _buttons.Right.IsDown)
                || !UnstickGrounded;
            private bool UnstickGrounded => _player.Flags1.TestFlag(PlayerFlags1.Grounded) || _player.EchoesSubmerged;

            // every AI step (end of Process, whatever the bot is doing): how long she has stayed in one spot (her target
            // node can flip every few steps, so this goes by where she is), and the jumps and drifts
            private void BotUnstickTick()
            {
                bool travel = (long)_scene.FrameCount - _unstickTravelAt <= 2; // (the tree may run its travel every other frame)
                // (HostBotStuckVertical) off the ground longer than a jump of her own (up and down: 4 * jump speed / gravity
                // frames, gravity is g/2 a frame) and moving up or down since the last step, not by her own climb. A bot
                // jumping into a wall lands inside that and her clock counts (link audit 10-26: pausing every air step left
                // 30 links' wall-jumpers unrescued, Processor Core's top ring among them). Or carried: in the air her own
                // gravity only takes upward speed away, so upward speed gained since the last step is a lift's (the first
                // steps of a ride come before the jump time: a clock left over from before fired a drift there and pushed
                // her out of Fuel Stack's centre shaft). Only while she was already rising and did rise since the last step:
                // a landing's floor contact also lifts a falling speed toward zero, and a wall's edge pushes a bot wedged on
                // it up a little without moving her; read as a lift, they held her clock and her stick
                _unstickAir = UnstickGrounded ? 0 : _unstickAir + 1;
                bool carried = _unstickAir > 1 && _unstickLastVy > 0 && _player.Speed.Y > _unstickLastVy
                    && _player.Position.Y - _unstickLastY > 0.05f;
                bool vertical = !UnstickGrounded && _unstickClimb == 0 && System.MathF.Abs(_player.Position.Y - _unstickLastY) > 0.05f
                    && (carried || _unstickAir > 4 * Fixed.ToFloat(_player.Values.JumpSpeed) / System.MathF.Abs(Fixed.ToFloat(_player.Values.BipedGravity)));
                _unstickLastY = _player.Position.Y;
                _unstickLastVy = _player.Speed.Y;
                _unstickGains = carried ? _unstickGains + 1 : 0;
                if (HostBotUnstick && BotPadFlight())
                {
                    return;
                }
                if (!HostBotUnstick && BotMphPadFlight())
                {
                    if (EdgeJumpOn && HostBotEdgePadJump)
                    {
                        BotEdgePadJump(travel); // (HostBotEdgeJump) PlayerAiEdgeJump.cs
                    }
                    return;
                }
                _edgePadPress = 0;
                if (!HostBotUnstick && HostBotVanillaParity && BotLiftEscape()) // (HostBotLiftGuard) PlayerAiEdgeJump.cs
                {
                    return;
                }
                if (!HostBotUnstick && HostBotVanillaParity && HostBotStuckVertical && BotLiftRide())
                {
                    return;
                }
                if (HostBotMoves)
                {
                    _botTicks++;
                    BotItemTick();
                    BotLinkTick();
                }
                if (_unstickClimb > 0)
                {
                    BotClimb();
                }
                if (_unstickDrift > 0)
                {
                    BotDrift();
                }
                bool seesFoe = !HostBotUnstick && travel && UnstickSeesFoe();
                if (SightPinned(seesFoe && HostBotVanillaParity && HostBotStuckSight))
                {
                    // (HostBotStuckSight) pinned in one spot for 1.5 s while following nodes: the clock runs though she
                    // sees a foe
                    seesFoe = false;
                }
                if (!HostBotUnstick && (!travel || seesFoe))
                {
                    // MPH's stuck fix: only while she follows nodes (a bot strafing in a fight keeps vanilla's moves; her
                    // node can be stale then) and sees nobody to fight: bots follow nodes 2/3 of the time they see the
                    // human too, and a jump or a new node mid-fight is a combat change (L0 hit% 15.4 -> 16.2 with this,
                    // vanilla 16.1, 10-25). The clock starts again when she goes back to her nodes
                    _unstickFrames = 0;
                    return;
                }
                if ((_player.Position - _unstickAt).Xz.LengthSquared > 2 * 2 || _player.Health == 0)
                {
                    _unstickAt = _player.Position;
                    _unstickFrames = 0;
                    _unstickClimbs = 0;
                    _unstickUnmorphs = 0;
                    _unstickDrifts = 0;
                    _unstickAvoidCount = 0;
                    _unstickSeenValid = false;
                    EdgeSpotReset();
                    return;
                }
                BotUnstickSteer();
                if (EdgeJumpOn)
                {
                    BotEdgeJump(); // (HostBotEdgeJump) PlayerAiEdgeJump.cs
                }
                // only the steps she is trying to move (UnstickTrying): a bot standing to shoot isn't stuck, but her stale
                // node counted it. Shooting Gallery 2026-10-16: Kanden fired from one spot for 5 s (3
                // jumps in place), then the node picked as he landed was given up the next step and he circled the one he
                // stood on for 21 s, "shaking around wildly and firing at seemingly nothing"
                if (!UnstickTrying)
                {
                    return;
                }
                if (vertical && !HostBotUnstick && HostBotVanillaParity && HostBotStuckVertical)
                {
                    // (HostBotStuckVertical) falling or carried up: not stuck, the clock waits
                    return;
                }
                if (++_unstickFrames % UnstickStep != 0 || _unstickClimb > 0 || _unstickDrift > 0 || !(UnstickAbove || UnstickAway)
                    || EdgeJumpOn && EdgeBusy)
                {
                    return;
                }
                if (_player.IsAltForm)
                {
                    if (UnstickAbove && _unstickUnmorphs < 2)
                    {
                        // a ball can't jump: stand up first
                        _unstickUnmorphs++;
                        HostUnstickCounts[0]++;
                        CheckUnmorph();
                    }
                }
                else if (!_player.IsMorphing && UnstickGrounded)
                {
                    if (EdgeJumpOn && _unstickClimbs >= UnstickClimbsMax)
                    {
                        // (HostBotEdgeJump) the 2 climbs used: the backstop's steps in place of the give-up
                        BotEdgeRung();
                    }
                    else if (_unstickClimbs < UnstickClimbsMax)
                    {
                        _unstickClimbs++;
                        if (!LiftGuardBlocks()) // (HostBotLiftGuard) not into a lift her route doesn't go up: the climb counts as used
                        {
                            _unstickClimb = 1;
                            HostUnstickCounts[1]++;
                            BotClimb();
                        }
                    }
                }
                else if (!_player.IsMorphing && _unstickDrifts < UnstickDriftsMax && (UnstickAwayXz || UnstickAbove))
                {
                    _unstickDrifts++;
                    _unstickDrift = 1;
                    HostUnstickCounts[3]++;
                    BotDrift();
                }
            }

            // the node-following step while _node40 is out of range: 3 s in one spot (and the jumps used up) = MphRead's
            // own give-up (Func2140584, alt form held against a wall): the node goes on the avoid list, and the next one
            // is the closest she can see
            private void BotUnstick(AiContext context)
            {
                if (BotUnstickSteer())
                {
                    return;
                }
                bool edge = EdgeJumpOn;
                if (_node40 == null || _unstickFrames < (edge ? UnstickStep * 6 : UnstickGiveUpFrames) || _unstickClimb > 0
                    || _unstickDrift > 0 || (_unstickClimbs < UnstickClimbsMax || edge && !EdgeRungsDone) && UnstickGrounded
                    && !_player.IsAltForm)
                {
                    return;
                }
                _unstickFrames = 0;
                HostUnstickCounts[2]++;
                HostUnstickGiveUp?.Invoke(_player, _node40);
                if (edge)
                {
                    // (HostBotEdgeJump) marked bad for 5 s, not dropped
                    EdgeMarkBad(_node40.Id);
                    EdgeSpotReset();
                }
                else if (_unstickAvoidCount < _unstickAvoid.Length && !UnstickAvoided(_node40.Id))
                {
                    _unstickAvoid[_unstickAvoidCount++] = _node40.Id;
                }
                _unstickSeenValid = false;
                _field7A[_field78] = _node40.Id;
                if (_field78 < 9)
                {
                    _field78++;
                }
                FindEntityRef(AiEntRefType.Type1);
                _node40 = UnstickSeen() ?? _entityRefs.Field1;
                context.Field18 = _node40?.NodeType == NodeType.AltForm && _player.Hunter != Hunter.Guardian ? 2 : 0;
            }

            // MphRead re-picked a node she gave up on in this spot (its node picks run on every change of behaviour, and
            // her destination can be that node: Shooting Gallery, 8 give-ups in one notch): the closest one she can see
            // instead. Func2140094 (node following, before she moves) and every AI step
            private bool BotUnstickSteer()
            {
                if (_node40 == null || !UnstickAvoided(_node40.Id))
                {
                    return false;
                }
                _node40 = UnstickSeen() ?? _node40;
                return true;
            }

            // IsNodeInRange wants her on the ground, but a generated node can sit on a jump pad (Spider Complex's centre
            // pad, node 60): the pad throws her up the step she lands, she steered back down at the node and was thrown
            // up again for good (6% jitter, give-ups on the perch at the top). Just thrown by a pad = on the ground
            private bool BotJustLaunched() => _player._timeSinceJumpPad <= 5 * 2; // todo: FPS stuff

            // a pad's flight is solved with hands off (ArenaCannons.Tune), but she steered at her next node from the start:
            // off Spider Complex's centre pad she flew into the skirt under the top's rim and hung there at y 16, every
            // trip once the graph sent bots up there (2026-10-10). Hands off the stick until she lands (4 s at most)
            private bool BotPadFlight()
            {
                if (_player._timeSinceJumpPad <= 3)
                {
                    _unstickPadFlight = 1;
                }
                if (_unstickPadFlight == 0)
                {
                    return false;
                }
                if (_unstickPadFlight++ > 240 * 2 || _player._timeSinceJumpPad > 5 * 2 // todo: FPS stuff
                    && (_player.Flags1.TestFlag(PlayerFlags1.Grounded) || _player.EchoesSubmerged) || _player.Health == 0)
                {
                    _unstickPadFlight = 0;
                    return false;
                }
                _buttons.A.IsDown = false;
                _buttons.B.IsDown = false;
                _buttons.X.IsDown = false;
                _buttons.Y.IsDown = false;
                _unstickDrift = 0;
                _unstickDriftDown = false;
                _unstickClimb = 0;
                _unstickSecond = false;
                return true;
            }

            // (HostBotStuckVertical) riding a lift up to a node above: hands off the stick while her arc from here (top =
            // y + vy^2 / 2g, the same for vanilla's 30 Hz tick and our two halves) can't reach the node's height, so she
            // stays in the shaft until it can. Link audit 10-26, Fuel Stack's centre shaft to its y 20 ledges: she steered
            // for the ledge from y 9, left the lift's column at y 14-16 and fell under the ledge (vanilla too; the old
            // "working" runs were a stuck-fix drift that happened to keep her in the column). Physics only: a lift =
            // upward speed gained in the air two steps in a row (a jump gains once) and more than one tick of gravity (on
            // a step's edge out of "grounded", gravity's pull and the floor's reset read as gains around 0: Fault Line,
            // she stood still there and the drift took her off)
            private bool BotLiftRide()
            {
                if (UnstickGrounded || _player.Speed.Y <= 0 || _player.Health == 0)
                {
                    _unstickRide = false;
                    return false;
                }
                float g = System.MathF.Abs(Fixed.ToFloat(_player.Values.BipedGravity));
                if (_unstickGains >= 2 && _player.Speed.Y > g)
                {
                    _unstickRide = true;
                }
                if (!_unstickRide || _node40 == null)
                {
                    return false;
                }
                float vy = _player.Speed.Y;
                float top = _player.Position.Y + vy * vy / (2 * g);
                if (top >= _node40.Position.Y)
                {
                    return false;
                }
                _buttons.A.IsDown = false;
                _buttons.B.IsDown = false;
                _buttons.X.IsDown = false;
                _buttons.Y.IsDown = false;
                _unstickDrift = 0;
                _unstickDriftDown = false;
                return true;
            }

            // MPH's stuck fix: no clock, jumps or drifts from a pad's throw until she lands, her own steering untouched
            // (vanilla's). Processor Core's centre pad throws her straight up (one spot across the floor, her node above):
            // the drift pushed her off the ledge she was steering for and 35% of the throws fell back (vanilla 9%, 10-25)
            private bool BotMphPadFlight()
            {
                if (UnstickGrounded || _player._timeSinceJumpPad > 240 * 2 || _player.Health == 0) // todo: FPS stuff
                {
                    _padStill = 0;
                    return false;
                }
                // (HostBotStuckSight) off the ground but not 0.2 from one spot for half a second isn't a pad's flight: wedged on a
                // wall's lip (Oubliette 10-26: Spire, 3 s off a pad, stood there ungrounded and the stuck fix skipped her)
                if ((_player.Position - _padStillAt).LengthSquared > 0.2f * 0.2f)
                {
                    _padStillAt = _player.Position;
                    _padStill = 0;
                }
                _padStill++;
                if (HostBotVanillaParity && HostBotStuckSight && _padStill >= 15 * 2) // todo: FPS stuff
                {
                    return false;
                }
                _unstickFrames = 0;
                _unstickDrift = 0;
                _unstickDriftDown = false;
                _unstickClimb = 0;
                _unstickSecond = false;
                return true;
            }

            private bool UnstickAvoided(int id) => EdgeJumpOn ? EdgeMarked(id) : System.Array.IndexOf(_unstickAvoid, (ushort)id, 0, _unstickAvoidCount) >= 0;

            // FindEntityRef's closest node (MphRead's picks ignore walls and forget the avoid list): not one she gave up
            // on in this spot
            private NodeData3? BotUnstickPick(NodeData3? node)
            {
                return node != null && UnstickAvoided(node.Id) ? UnstickSeen() ?? node : node;
            }

            // the closest node off the avoid lists with nothing between her and it (half a unit up at both ends, then a
            // unit and a half: over a lip or a railing), else the closest one off the avoid lists (a pocket with no node in
            // sight: Spider Complex's middle ledges, 2026-10-10); kept until she leaves the spot or gives up again
            private NodeData3? UnstickSeen()
            {
                if (_unstickSeenValid)
                {
                    return _unstickSeen;
                }
                NodeData3? best = UnstickClosest(0.5f) ?? UnstickClosest(1.5f) ?? UnstickClosest(null);
                _unstickSeen = best;
                _unstickSeenValid = true;
                return best;
            }

            private NodeData3? UnstickClosest(float? up)
            {
                NodeData3? best = null;
                float bestDist = float.MaxValue;
                var result = new CollisionResult();
                Vector3 from = _player.Position.AddY(up ?? 0);
                for (int i = 0; i < _nodeList.Count; i++)
                {
                    NodeData3 node = _nodeList[i];
                    float dist = Vector3.DistanceSquared(node.Position, _player.Position);
                    // not the node she stands on: she "reaches" it at once, the route sends her on to the node she gave
                    // up on, and the avoid list sends her back (Shooting Gallery 2026-10-16: Kanden on node 82, 83 given up,
                    // a half-unit circle round 82 for 21 s)
                    if (dist >= bestDist || System.Array.IndexOf(_field7A, (ushort)i, 0, _field78) >= 0 || UnstickAvoided(i)
                        || UnstickAt(node))
                    {
                        continue;
                    }
                    if (up == null || !CollisionDetection.CheckBetweenPoints(from, node.Position.AddY(up.Value), TestFlags.Players, _scene, ref result))
                    {
                        best = node;
                        bestDist = dist;
                    }
                }
                return best;
            }

            // off the ground and stuck (a steep edge that isn't floor holds her up): half a second of drift toward her
            // node, as if she stepped off it
            private void BotDrift()
            {
                int step = _unstickDrift++;
                if (_node40 == null || step > 30 * 2 || _player.Flags1.TestFlag(PlayerFlags1.Grounded)) // todo: FPS stuff
                {
                    _unstickDrift = 0;
                    _unstickDriftDown = false;
                    return;
                }
                Vector2 to = (_node40.Position - _player.Position).Xz;
                if (_unstickDriftDown || step == 1 && UnstickAbove)
                {
                    // held up on a steep skirt below her node (Spider Complex's top, once it had nodes: 14% jitter): it
                    // can't be climbed, so off it, away from the node, and her route goes round
                    _unstickDriftDown = true;
                    to = to.LengthSquared > 0.01f ? -to.Normalized() * 0.15f : new Vector2(0.15f, 0);
                    _player.Speed = new Vector3(to.X, _player.Speed.Y, to.Y);
                }
                else if (to.LengthSquared < 1)
                {
                    // over it now (the drift went back and forth over the node: Shooting Gallery, 2026-10-10)
                    _unstickDrift = 0;
                }
                else
                {
                    to = to.Normalized() * 0.15f;
                    _player.Speed = new Vector3(to.X, _player.Speed.Y, to.Y);
                }
            }

            // Func2140584's jump toward a node holds L until the jump is used up, but in an Echoes arena a jump off the
            // ground keeps the double jump (SpaceJump: UsedJump stays clear), so she held L for good and no later press
            // (hers or BotClimb's) could jump again (Spider Complex, 2026-10-10): the jump is over once she lands from it.
            // (Over as soon as she left the ground, she was back in it in the air and spent the double jump at once:
            // Shooting Gallery 0 -> 15% jitter)
            private bool BotJumpedOffGround()
            {
                if (!_player.Flags1.TestFlag(PlayerFlags1.Grounded))
                {
                    _unstickJumpAir |= _buttons.L.FramesDown > 0;
                    return false;
                }
                bool landed = _unstickJumpAir;
                _unstickJumpAir = false;
                return landed;
            }

            // a jump off the ground, then a second press once she stops rising (double jump), held while under water (the
            // Gravity Boost lasts while the button is held); over when she lands again or after 2 s
            private void BotClimb()
            {
                int step = _unstickClimb++;
                if (step <= 4 * 2) // todo: FPS stuff
                {
                    _buttons.L.IsDown = true;
                }
                else if (step > 8 * 2 && step < 60 * 2) // todo: FPS stuff
                {
                    bool held = _buttons.L.FramesDown > 0;
                    if (!_unstickSecond && _player.Speed.Y < 0.05f || held && _player.EchoesSubmerged)
                    {
                        _unstickSecond = true;
                        _buttons.L.IsDown = true;
                    }
                }
                if (step > 60 * 2 || step > 10 * 2 && _player.Flags1.TestFlag(PlayerFlags1.Grounded)) // todo: FPS stuff
                {
                    _unstickClimb = 0;
                    _unstickSecond = false;
                }
            }

            // T7: the link she is following (from her last node to _node40) and, at its ledge, edge or under water, the jump
            private void BotLinkTick()
            {
                if (_node40 != _navLast)
                {
                    _navFrom = _navLast;
                    _navLast = _node40;
                    _navTries = 0;
                    _navKind = _navFrom != null && _node40 != null && HostNavLinks != null
                        && HostNavLinks.TryGetValue((_navFrom.Id, _node40.Id), out int kind) ? kind : 0;
                }
                if (_navKind == 0 || _node40 == null || _unstickClimb > 0 || _unstickDrift > 0 || _player.IsMorphing
                    || _navTries >= 3 || _player.Health == 0)
                {
                    return;
                }
                Vector2 to = (_node40.Position - _player.Position).Xz;
                float across = to.Length;
                if (_player.IsAltForm)
                {
                    // a ball can't take it: stand up as she nears it
                    if (across < 5)
                    {
                        _navTries++;
                        HostUnstickCounts[0]++;
                        CheckUnmorph();
                    }
                    return;
                }
                bool grounded = _player.Flags1.TestFlag(PlayerFlags1.Grounded);
                float up = _node40.Position.Y - (_player.Position.Y - 0.5f);  // her feet: half a unit under her position
                Vector2 dir = across > 0.01f ? to / across : Vector2.Zero;
                bool go = _navKind switch
                {
                    // at the ledge: close to the node, or the wall in front of her
                    1 or 2 => grounded && up > 0.6f && (across < 2 || BotWallAhead(dir)),
                    // out of the water, held (the Gravity Boost lasts while the button is down)
                    4 => (grounded || _player.EchoesSubmerged) && up > 0.6f && across < 4,
                    // the gap's edge: no floor a step ahead
                    3 => grounded && across > 1 && !BotFloorAhead(dir),
                    _ => false
                };
                if (go)
                {
                    _navTries++;
                    HostUnstickCounts[4]++;
                    _unstickClimb = 1;   // BotUnstickTick runs it this step
                }
            }

            private bool BotWallAhead(Vector2 dir)
            {
                var result = new CollisionResult();
                Vector3 from = _player.Position.AddY(0.2f);
                Vector3 to = from + new Vector3(dir.X, 0, dir.Y) * 1.2f;
                return CollisionDetection.CheckBetweenPoints(from, to, TestFlags.Players, _scene, ref result);
            }

            private bool BotFloorAhead(Vector2 dir)
            {
                var result = new CollisionResult();
                Vector3 ahead = _player.Position + new Vector3(dir.X, 0, dir.Y) * 0.9f;
                return CollisionDetection.CheckBetweenPoints(ahead, ahead.AddY(-0.5f - 1.2f), TestFlags.Players, _scene, ref result);
            }

            // T7: an item she has been beside for 3 s without getting it is skipped for 20 s
            private void BotItemTick()
            {
                ItemInstanceEntity? item = Flags2.TestFlag(AiFlags2.TargetItem) ? _itemC8 : null;
                if (item == null || item != _itemNear || (item.Position - _player.Position).Xz.LengthSquared > 2.5f * 2.5f)
                {
                    _itemNear = item;
                    _itemNearFrames = 0;
                    return;
                }
                if (++_itemNearFrames < 90 * 2) // todo: FPS stuff
                {
                    return;
                }
                _itemNearFrames = 0;
                int slot = 0;
                for (int i = 1; i < _itemSkip.Length; i++)
                {
                    if (_itemSkip[i].Until < _itemSkip[slot].Until) slot = i;
                }
                _itemSkip[slot] = (item.Id, _botTicks + 600 * 2); // todo: FPS stuff
                HostUnstickCounts[5]++;
                UpdateTargetItem(null);
            }

            // Func21377FC (MphRead's "not this item" for every item pick): skipped, or under a breakable's top
            private bool BotItemSkipped(ItemInstanceEntity item)
            {
                foreach ((int id, int until) in _itemSkip)
                {
                    if (id == item.Id && until > _botTicks) return true;
                }
                if (_itemCoveredAt < 0 || _botTicks - _itemCoveredAt >= 30)
                {
                    // grates break and come back: looked at again every half second
                    _itemCovered.Clear();
                    _itemCoveredAt = _botTicks;
                }
                if (!_itemCovered.TryGetValue(item.Id, out bool covered))
                {
                    var result = new CollisionResult();
                    covered = CollisionDetection.CheckBetweenPoints(item.Position, item.Position.AddY(1.3f), TestFlags.Players, _scene, ref result)
                        && result.EntityCollision != null;
                    _itemCovered[item.Id] = covered;
                }
                return covered;
            }
        }
    }
}
