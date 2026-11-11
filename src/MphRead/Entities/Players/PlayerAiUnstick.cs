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
        // recomp (HostBotLedgeLand, MatchSettings.BotLedgeLand; replays before format 78 play without it): a climb toward a
        // node above her (a link jump, the stuck fix's jumps) steers her in the air onto floor a body's width past the node,
        // not at the node. Nodes are raw floor samples, some right at a lip (Mining Plaza's west stairs, node 67 0.05 inside
        // it): her steering and the drift both stop within a unit of the node, so she hung in the air beside the lip, feet
        // above it, bobbing jump after jump (owner's Team Defender 2026-11-01: Kanden, Trace, Spire, Weavel, minutes of it)
        public static bool HostBotLedgeLand { get; set; }
        // recomp (HostBotItemWalled, MatchSettings.BotItemWalled; replays before format 80 play without it): an item with a
        // closed door-like entity (Mining Plaza's gate) between it and its closest node is skipped while it stays shut. Her
        // path ends at the node, the item lies past the gate, and she hopped against it until killed (owner's Battle 11-04:
        // Spire and Sylux after the Deathalt behind the shut gate, 1:03-1:13); T7's "beside it 3 s" never fired at 3.4 apart.
        // Also: T7's skips and the covered looks go by the item itself, not its entity id (every placed pickup's is -1, so
        // the first placed pickup looked at decided for them all). -botprobe Mining Plaza, 4 lineups: bot-seconds at the shut
        // gate 156 -> 3
        public static bool HostBotItemWalled { get; set; }
        // recomp (HostBotOutOfReach, MatchSettings.BotOutOfReach; replays before format 81 play without it): on the ground,
        // a node further above her than a double jump (OutOfReachRise) and steeper than any floor (more up than across)
        // has no link from here; it is given up at once (the give-up below, avoid list and all), before she walks under
        // it. MphRead's closest-node pick is a straight distance, made at any time: stepping off Mining Plaza's gate
        // ledge it picked the ledge node right above the shut gate, and Kanden walked under it into the gate's face and
        // jumped there for 2 s until the stuck fix gave it up (seeds 11 and 16 of the gate sweep, 11-05)
        public static bool HostBotOutOfReach { get; set; }
        // recomp (HostBotUnderGiveUp, MatchSettings.BotUnderGiveUp; replays before format 82 play without it): on the
        // ground with her node right above her (1+ up, under 3 across) and half a second in one spot trying to move, the
        // node is given up then (the out-of-reach give-up), not after the stuck fix's 2 climbs and 3 s. Mining Plaza's
        // stairs: the climbs from under the landing never got her up, and Trooper, Spire, Noxus, Sylux, Weavel and Kanden
        // stood there 3.1 s each time (owner's Battle 11-05, 0:29-0:37; -botprobe 12 seeds: 19 spells of 1.5 s+)
        public static bool HostBotUnderGiveUp { get; set; }
        // recomp (HostBotGrateItems, MatchSettings.BotGrateItems; replays before format 83 play without it): an item inside a
        // grate's footprint (Shooting Gallery's pool items: 2 small health, 1 small missile) is skipped, grate up or broken.
        // T7's covered look casts up from the item, but those items sit inside the grate's own slab (y 2.2 and 1.8 in a slab
        // 1.8-2.35), so the ray started inside the grate and never hit it; Samus stood and hopped on the grates for 4-6 s at
        // a time (owner's Battle 11-06). Broken, the item is in the acid: not worth the damage (owner: "just have them ignore
        // these pickups")
        public static bool HostBotGrateItems { get; set; }
        // the grates' boxes in the room (EchoesBreakables.Extra, set with HostWallList), for HostBotGrateItems
        public static System.Collections.Generic.IReadOnlyList<(Vector3 Min, Vector3 Max)>? HostGrateBoxes { get; set; }
        // items the grate check skipped, one per half-second look (Tools -botprobe prints it)
        public static int HostGrateItemCount;
        // items the walled check skipped since the start, one per half-second look (Tools -botprobe prints it)
        public static int HostItemWalledCount;
        // recomp (HostBotItemNoGain, MatchSettings.BotItemNoGain; replays before format 84 play without it): T7's "beside it
        // 3 s" counts while she stays within 3.5 of the item (not 2.5), and an item she has got no nearer for a second
        // beside it is skipped then. Mining Plaza's west stairs: a small missile 2.4 past a step she can't get past, she
        // ran at it, slid off, and wobbled in and out of 2.5, so the 3 s count kept starting over and she shook on the
        // spot until killed (owner's Battle 11-06, Samus 3:34-3:38)
        public static bool HostBotItemNoGain { get; set; }
        // items the no-gain check gave up (Tools -botprobe prints it)
        public static int HostItemNoGainCount;
        // nodes given up at once as out of reach (HostBotOutOfReach) since the start (Tools -botprobe prints it)
        public static int HostOutOfReachCount;
        // (HostBotOutOfReach) a shut wall bots may never jump at (Mining Plaza's gate, MiningPlazaGate.Extra; null in other
        // rooms): true for a bot next to it or headed at it (its node, or where its run carries it). Every bot jump (PlayerAi.ProcessInput's L, alt forms aside) is dropped there,
        // the stuck fix's, the combat dodge's and the cling's alike. Owner 11-05: Dark Samus double-jumped at the shut gate
        // in combat (~1:34), "make sure this never happened, not reduced it"
        public static System.Func<PlayerEntity, bool>? HostBotNoJumpAt { get; set; }
        // jumps HostBotNoJumpAt dropped since the start (Tools -botprobe prints it)
        public static int HostNoJumpCount;
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
            private Vector3? _climbLand = null; // (HostBotLedgeLand) where this climb steers her once she is up
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
            // (HostBotItemNoGain) her closest distance to it so far, steps since she got nearer
            private float _itemNearBest = 0;
            private int _itemNoGainFrames = 0;
            private readonly (int Id, int Until)[] _itemSkip = new (int, int)[8];
            // (HostBotItemWalled) the skipped items themselves: placed pickups (PlacedPickups.cs) all have entity id -1, so
            // by id alone one skip or one "covered" look counted for every placed pickup in the arena
            private readonly ItemInstanceEntity?[] _itemSkipItem = new ItemInstanceEntity?[8];
            private readonly System.Collections.Generic.Dictionary<ItemInstanceEntity, bool> _itemCoveredBy = new();
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
            public string HostLinkState => $"link {_navFrom?.Id}>{_navLast?.Id} kind {_navKind} tries {_navTries} land {(_climbLand is { } l ? $"{l.X:0.0},{l.Y:0.0},{l.Z:0.0}" : "-")} climb {_unstickClimb} 2nd {_unstickSecond}";
            public bool HostButtonL => _buttons.L.IsDown;
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
            // (HostBotOutOfReach) her node can't be reached from where she stands: above by more than a double jump with
            // a margin (ArenaNav's DoubleRise 3.6 from the feet, her position is ~0.6 up), and more up than across (no
            // walkable slope climbs that). Not in water (the Gravity Boost climbs 7) or in a pad's throw
            private const float OutOfReachRise = 4f;
            private bool UnstickOutOfReach
            {
                get
                {
                    if (_node40 == null || !_player.Flags1.TestFlag(PlayerFlags1.Grounded) || _player.EchoesSubmerged
                        || _unstickPadFlight != 0 || BotJustLaunched())
                    {
                        return false;
                    }
                    float up = _node40.Position.Y - _player.Position.Y;
                    return up > OutOfReachRise && up * up > (_node40.Position - _player.Position).Xz.LengthSquared;
                }
            }
            // (HostBotUnderGiveUp) her node right above her where she stands on the ground
            private const int UnderGiveUpFrames = 15 * 2; // todo: FPS stuff
            private bool UnstickUnder => _node40 != null && UnstickAbove && _player.Flags1.TestFlag(PlayerFlags1.Grounded)
                && !_player.EchoesSubmerged && _unstickPadFlight == 0 && !BotJustLaunched()
                && (_node40.Position - _player.Position).Xz.LengthSquared < 3 * 3;
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
                if (travel && HostBotVanillaParity)
                {
                    BotEdgeGuard(); // (HostBotEdgeGuard) PlayerAiEdgeJump.cs: Echoes arenas' unstick too
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
                // (HostBotUnderGiveUp) under her node: the first climb at half a second, not 1.5 s (a stair step 1.1 up was
                // given up before any jump, and she paced between its two nodes: Mining Plaza 11-05)
                _unstickFrames++;
                bool underClimb = HostBotUnderGiveUp && HostBotUnstick && _unstickClimbs == 0 && _unstickFrames >= UnderGiveUpFrames
                    && !_player.IsAltForm && UnstickUnder;
                if (!underClimb && _unstickFrames % UnstickStep != 0 || _unstickClimb > 0 || _unstickDrift > 0 || !(UnstickAbove || UnstickAway)
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
                if (HostIdleBots) // recomp: a frozen practice target never moves, so it isn't stuck (its give-ups cost frames)
                {
                    return;
                }
                if (BotUnstickSteer())
                {
                    return;
                }
                bool edge = EdgeJumpOn;
                bool outOfReach = HostBotUnstick && HostBotOutOfReach && (UnstickOutOfReach
                    || HostBotUnderGiveUp && _unstickFrames >= UnderGiveUpFrames && _unstickClimbs > 0 && _unstickClimb == 0 && UnstickUnder);
                if (outOfReach)
                {
                    HostOutOfReachCount++;
                }
                else if (_node40 == null || _unstickFrames < (edge ? UnstickStep * 6 : UnstickGiveUpFrames) || _unstickClimb > 0
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
                _climbLand = null;
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
                _climbLand = null;
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

            // nearest first, so the first clear sight line is the answer, and only the nearest UnstickRays: a spot that sees
            // no node at all (Mining Plaza's stand-in spawn at a locked door, 2026-10-31) tested every node twice on each
            // give-up, 280 rays and 130 ms on the Odin; past them she takes the closest one, as with no node in sight
            private const int UnstickRays = 12;
            private readonly System.Collections.Generic.List<(float Dist, int Index)> _unstickOrder = new();

            private NodeData3? UnstickClosest(float? up)
            {
                _unstickOrder.Clear();
                for (int i = 0; i < _nodeList.Count; i++)
                {
                    NodeData3 node = _nodeList[i];
                    // not the node she stands on: she "reaches" it at once, the route sends her on to the node she gave
                    // up on, and the avoid list sends her back (Shooting Gallery 2026-10-16: Kanden on node 82, 83 given up,
                    // a half-unit circle round 82 for 21 s)
                    if (System.Array.IndexOf(_field7A, (ushort)i, 0, _field78) >= 0 || UnstickAvoided(i) || UnstickAt(node))
                    {
                        continue;
                    }
                    _unstickOrder.Add((Vector3.DistanceSquared(node.Position, _player.Position), i));
                }
                if (_unstickOrder.Count == 0)
                {
                    return null;
                }
                _unstickOrder.Sort((a, b) => a.Dist != b.Dist ? a.Dist.CompareTo(b.Dist) : a.Index.CompareTo(b.Index));
                if (up == null)
                {
                    return _nodeList[_unstickOrder[0].Index];
                }
                var result = new CollisionResult();
                Vector3 from = _player.Position.AddY(up.Value);
                for (int k = 0; k < _unstickOrder.Count && k < UnstickRays; k++)
                {
                    NodeData3 node = _nodeList[_unstickOrder[k].Index];
                    if (!CollisionDetection.CheckBetweenPoints(from, node.Position.AddY(up.Value), TestFlags.Players, _scene, ref result))
                    {
                        return node;
                    }
                }
                return null;
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
                if (step == 1)
                {
                    _climbLand = HostBotLedgeLand && _navKind != 3 && _navKind != 4 ? BotLandingSpot() : null; // up a ledge, not across a gap
                }
                if (_climbLand != null && step > 4 * 2 && !_player.Flags1.TestFlag(PlayerFlags1.Grounded) // todo: FPS stuff
                    && _player.Position.Y - 0.5f > _climbLand.Value.Y + 0.05f)
                {
                    // up there, feet over the lip: onto the floor past it (not before: pressed against the face under the lip,
                    // a steep face takes her rise away, ArenaNav sim 7)
                    Vector2 to = (_climbLand.Value - _player.Position).Xz;
                    if (to.LengthSquared > 0.2f * 0.2f)
                    {
                        to = to.Normalized() * 0.15f;
                        _player.Speed = new Vector3(to.X, _player.Speed.Y, to.Y);
                    }
                }
                else if (_climbLand != null && _unstickSecond && _player.Speed.Y < -0.02f)
                {
                    _climbLand = null; // both jumps spent and falling short: normal steering again, not pinned in the air
                }
                else if (_climbLand != null && !_player.Flags1.TestFlag(PlayerFlags1.Grounded) && _player.Speed.Y > 0.01f)
                {
                    // feet still under the lip: straight up, off the face (held against a steep face under a lip, it stops
                    // her rise: Mining Plaza's edge steps, 11-01)
                    _player.Speed = new Vector3(0, _player.Speed.Y, 0);
                }
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
                    _climbLand = null;
                }
            }

            // (HostBotLedgeLand) floor at her node's height a body's width past it, with room around it: of 8 ways out of the
            // node, the one most along her way in (the node itself when she is beside it already, or none above her)
            private static readonly Vector2[] _landWays =
            {
                new Vector2(1, 0), new Vector2(0.70710677f, 0.70710677f), new Vector2(0, 1), new Vector2(-0.70710677f, 0.70710677f),
                new Vector2(-1, 0), new Vector2(-0.70710677f, -0.70710677f), new Vector2(0, -1), new Vector2(0.70710677f, -0.70710677f)
            };

            private Vector3? BotLandingSpot()
            {
                if (_node40 == null)
                {
                    return null;
                }
                Vector3 node = _node40.Position;
                if (node.Y - (_player.Position.Y - 0.5f) < 0.6f)
                {
                    return null;
                }
                Vector2 way = (node - _player.Position).Xz;
                way = way.LengthSquared > 0.0001f ? way.Normalized() : Vector2.UnitX;
                Vector3? best = null;
                float bestScore = float.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    Vector2 d = _landWays[i];
                    var perp = new Vector2(-d.Y, d.X);
                    Vector2 spot = node.Xz + d * 1.2f;
                    if (!BotFloorAt(spot, node.Y) || !BotFloorAt(spot + perp * 0.5f, node.Y)
                        || !BotFloorAt(spot - perp * 0.5f, node.Y) || !BotFloorAt(spot + d * 0.5f, node.Y)
                        || !BotFloorAt(node.Xz + d * 0.6f, node.Y))
                    {
                        continue;
                    }
                    float score = Vector2.Dot(d, way);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new Vector3(spot.X, node.Y, spot.Y);
                    }
                }
                return best;
            }

            private bool BotFloorAt(Vector2 at, float y)
            {
                var result = new CollisionResult();
                var from = new Vector3(at.X, y + 1.5f, at.Y);
                return CollisionDetection.CheckBetweenPoints(from, from.WithY(y - 0.6f), TestFlags.Players, _scene, ref result)
                    && result.Position.Y > y - 0.4f && result.Plane.Y > 0.5f;
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
                    // (HostBotLedgeLand: close to it only once it is in reach, a ramp under a ledge took her jumps low)
                    1 or 2 => grounded && up > 0.6f && (across < 2 && (!HostBotLedgeLand || up < (_navKind == 1 ? 2.2f : 3.8f))
                        || BotWallAhead(dir)),
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
            private const float ItemOutOfReachRise = 6;
            private void BotItemTick()
            {
                ItemInstanceEntity? item = Flags2.TestFlag(AiFlags2.TargetItem) ? _itemC8 : null;
                float nearRadius = HostBotItemNoGain ? 3.5f : 2.5f;
                if (item == null || item != _itemNear || (item.Position - _player.Position).Xz.LengthSquared > nearRadius * nearRadius)
                {
                    _itemNear = item;
                    _itemNearFrames = 0;
                    _itemNearBest = float.MaxValue;
                    _itemNoGainFrames = 0;
                    return;
                }
                bool noGain = false;
                if (HostBotItemNoGain)
                {
                    // nearer by a quarter unit counts as getting there (a lift or a pad under it does, straight up)
                    float dist = (item.Position - _player.Position).Length;
                    if (dist < _itemNearBest - 0.25f || BotJustLaunched())
                    {
                        _itemNearBest = System.MathF.Min(dist, _itemNearBest);
                        _itemNoGainFrames = 0;
                    }
                    noGain = ++_itemNoGainFrames >= 30 * 2; // todo: FPS stuff
                }
                // (HostBotUnderGiveUp) right under an item far above her on the ground (a ledge over Mining Plaza's stairs,
                // 12 up): the item walk only steers at it, so she stood under it until the 3 s give-up (11-05). No jump reaches it;
                // half a second in that spot first (a jump pad under an item throws her up it)
                bool under = HostBotUnderGiveUp && UnstickGrounded && _unstickFrames >= UnderGiveUpFrames && !BotJustLaunched() && item.Position.Y - _player.Position.Y > ItemOutOfReachRise
                    && (item.Position - _player.Position).Xz.LengthSquared < 2.5f * 2.5f;
                if (++_itemNearFrames < 90 * 2 && !under && !noGain) // todo: FPS stuff
                {
                    return;
                }
                if (noGain) HostItemNoGainCount++;
                _itemNearFrames = 0;
                _itemNearBest = float.MaxValue;
                _itemNoGainFrames = 0;
                int slot = 0;
                for (int i = 1; i < _itemSkip.Length; i++)
                {
                    if (_itemSkip[i].Until < _itemSkip[slot].Until) slot = i;
                }
                _itemSkip[slot] = (item.Id, _botTicks + 600 * 2); // todo: FPS stuff
                _itemSkipItem[slot] = item;
                HostUnstickCounts[5]++;
                UpdateTargetItem(null);
            }

            // Func21377FC (MphRead's "not this item" for every item pick): skipped, or under a breakable's top
            private bool BotItemSkipped(ItemInstanceEntity item)
            {
                for (int i = 0; i < _itemSkip.Length; i++)
                {
                    (int id, int until) = _itemSkip[i];
                    if (id == item.Id && until > _botTicks && (!HostBotItemWalled || _itemSkipItem[i] == item)) return true;
                }
                if (_itemCoveredAt < 0 || _botTicks - _itemCoveredAt >= 30)
                {
                    // grates break and come back: looked at again every half second
                    _itemCovered.Clear();
                    _itemCoveredBy.Clear();
                    _itemCoveredAt = _botTicks;
                }
                bool covered;
                if (HostBotItemWalled ? !_itemCoveredBy.TryGetValue(item, out covered) : !_itemCovered.TryGetValue(item.Id, out covered))
                {
                    var result = new CollisionResult();
                    covered = CollisionDetection.CheckBetweenPoints(item.Position, item.Position.AddY(1.3f), TestFlags.Players, _scene, ref result)
                        && result.EntityCollision != null;
                    if (!covered && HostBotGrateItems && HostGrateBoxes != null)
                    {
                        Vector3 p = item.Position;
                        foreach ((Vector3 min, Vector3 max) in HostGrateBoxes)
                        {
                            if (p.X > min.X - 0.5f && p.X < max.X + 0.5f && p.Z > min.Z - 0.5f && p.Z < max.Z + 0.5f
                                && p.Y > min.Y - 2 && p.Y < max.Y + 1.3f)
                            {
                                covered = true;
                                HostGrateItemCount++;
                                break;
                            }
                        }
                    }
                    if (!covered && HostBotItemWalled && _nodeList.Count > 0)
                    {
                        // walled off from its node by an entity's collision (a shut gate): a level wall isn't an entity
                        NodeData3 node = item.ClosestNode ?? FindClosestNonHazardNodeToPosition(item.Position);
                        result = new CollisionResult();
                        covered = CollisionDetection.CheckBetweenPoints(item.Position.AddY(0.5f), node.Position.AddY(0.5f), TestFlags.Players, _scene, ref result)
                            && result.EntityCollision != null;
                        if (covered) HostItemWalledCount++;
                    }
                    if (HostBotItemWalled) _itemCoveredBy[item] = covered;
                    else _itemCovered[item.Id] = covered;
                }
                return covered;
            }
        }
    }
}
