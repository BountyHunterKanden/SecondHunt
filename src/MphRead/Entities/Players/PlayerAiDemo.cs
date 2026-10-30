using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp, dev builds only: puts a bot where a bot-fix switch's symptom shows, so the owner can watch it with the
    // switch off and on without waiting for it to happen in a match (owner 2026-10-26: "We should do this for any of
    // these fix tests"). Host-side, bots this device runs; called on the sim thread (the pause's touch line, BotProbe's
    // demo= option).
    public partial class PlayerEntity
    {
        // one per bot-fix switch: the arena (room meta name), where the bot is put, the node it's made to head for, and
        // what the pause line says it shows
        //  1 flat node check: High Ground's walkway right under the deck-edge node it wants (node 135, 5.3 above). Vanilla
        //    drops a node it stands under (rev1 0x21408F4, horizontal); with the switch off the bot hops on the spot until
        //    the ~5 s timeout
        //  2 stuck fix: High Ground's spot the stuck fix gave up on most there (4 of 177 give-ups, all arenas, level 1;
        //    Shooting Gallery's 29 are bots in the turret). Stasis Bunker's ledge at (12, 9.4, -18.9) wanting node 42 below
        //    it: switch off, pinned 4-15 s some of the time; on, out within about 2 s
        private static readonly (string Room, Vector3 Spot, Vector3 Node, string Where)[] _demos =
        {
            default,
            ("MP4 HIGHGROUND", new Vector3(12.98f, 11.6f, -8.59f), new Vector3(12.98f, 16.89f, -8.59f), "UNDER THE DECK EDGE"),
            ("E3 FIRST HUNT", new Vector3(12.0f, 9.4f, -18.9f), new Vector3(12.1f, 6.8f, -22.8f), "ON THE STUCK SPOT"),
        };

        public static int DemoCount => _demos.Length - 1;
        public static string DemoRoom(int demo) => _demos[demo].Room;
        public static Vector3 DemoSpot { get; private set; }
        private static Vector3 _demoNode;

        // the teleport itself can make the AI re-plan from where it landed (a node it sees from there), which isn't the
        // case being shown: for the first quarter second the node is put back if anything but its own give-up
        // (Field118's timeout, which the flat node check brings forward) changed it
        private const int DemoHoldFrames = 15 * 2; // todo: FPS stuff
        private static PlayerEntity? _demoBot;
        private static int _demoHold;

        // the bot a demo put somewhere, for the host's watch camera (BotStuckToggle.cs), and from which side to watch it
        // (behind it as it was put); null once it's over (DemoWatchFrames, or the bot died)
        public static PlayerEntity? DemoWatchBot { get; private set; }
        public static Vector3 DemoWatchFrom { get; private set; }
        private const int DemoWatchFrames = 20 * 60; // todo: FPS stuff
        private static int _demoWatch;

        // every sim step after a demo started (host)
        public static void HostBotDemoStep()
        {
            HostBotAltStatsStep();
            if (DemoWatchBot != null && (--_demoWatch <= 0 || DemoWatchBot.Health <= 0))
            {
                DemoWatchBot = null;
            }
            if (_demoBot == null)
            {
                return;
            }
            if (--_demoHold <= 0 || _demoBot.Health <= 0 || !_demoBot.AiData.DemoHold(_demoNode))
            {
                _demoBot = null;
            }
        }

        // returns what it did (shown + logged), or why it couldn't
        public static string HostBotDemo(Scene scene, int demo)
        {
            if (demo < 1 || demo > DemoCount)
            {
                return "DEMO: NONE";
            }
            (string room, Vector3 spot, Vector3 node, string where) = _demos[demo];
            if (scene.Room?.Meta.Name != room)
            {
                return $"DEMO: {room} ONLY";
            }
            PlayerEntity? bot = null;
            for (int i = 0; i < PlayerCount; i++)
            {
                PlayerEntity p = Players[i];
                if (p.IsBot && p.Health > 0 && p.AiData.HasNodes)
                {
                    bot = p;
                    break;
                }
            }
            if (bot == null)
            {
                return "DEMO: NO LIVE BOT";
            }
            if (bot.IsAltForm)
            {
                bot.ExitAltForm(); // (the pins are a biped's)
            }
            Vector3 facing = (node - spot).WithY(0).LengthSquared > 0.01f
                ? (node - spot).WithY(0).Normalized() : -Vector3.UnitX;
            bot.Teleport(spot, facing, scene.GetNodeRefContaining(spot));
            bot.PrevPosition = bot.Position;
            bot.Speed = Vector3.Zero;
            if (!bot.AiData.DemoHeadFor(node))
            {
                return "DEMO: NO NODE";
            }
            DemoSpot = spot;
            _demoNode = node;
            _demoBot = bot;
            _demoHold = DemoHoldFrames;
            DemoWatchBot = bot;
            DemoWatchFrom = -facing;
            _demoWatch = DemoWatchFrames;
            return $"DEMO: {bot.Hunter.ToString().ToUpperInvariant()} PUT {where}";
        }

        // exit order has no spot of its own (it's how long Kanden/Spire stay in alt form across a fight), so the pause
        // shows their alt-form runs instead, counted since the switch last flipped (BotStuckToggle.cs); vanilla chains
        // alt periods, so runs of 12 s or more are common with it on and rare with it off (40-seed lineups 10-25: Kanden
        // 25 vs 9 per 40 matches, vanilla 37)
        private static readonly int[] _altRun = new int[16]; // (MaxPlayers can change after this is made)
        public static int DemoAltLongest { get; private set; }
        public static int DemoAltLong { get; private set; }

        public static void HostBotAltStatsReset()
        {
            System.Array.Clear(_altRun);
            DemoAltLongest = 0;
            DemoAltLong = 0;
        }

        private static void HostBotAltStatsStep()
        {
            for (int i = 0; i < PlayerCount && i < _altRun.Length; i++)
            {
                PlayerEntity p = Players[i];
                if (p.IsBot && (p.Hunter == Hunter.Kanden || p.Hunter == Hunter.Spire) && p.IsAltForm && p.Health > 0)
                {
                    if (++_altRun[i] == 12 * 60) // todo: FPS stuff
                    {
                        DemoAltLong++;
                    }
                    DemoAltLongest = System.Math.Max(DemoAltLongest, _altRun[i]);
                }
                else
                {
                    _altRun[i] = 0;
                }
            }
        }

        public partial class PlayerAiData
        {
            internal bool HasNodes => _nodeData != null;

            // false = stop holding (the AI gave the node up itself)
            internal bool DemoHold(Vector3 near)
            {
                if (Field118 >= 151 * 2) // todo: FPS stuff
                {
                    return false;
                }
                if (_node40 == null || (_node40.Position - near).LengthSquared > 0.01f)
                {
                    int f118 = Field118;
                    DemoHeadFor(near);
                    Field118 = f118;
                }
                return true;
            }

            public string DemoState() => $"node40 {_node40?.Id} {_node40?.Position} node44 {_node44?.Id} {_node44?.Position} f118 {Field118} flags2 {Flags2} target {_targetPlayer?.Hunter}";

            // the nav node nearest to near becomes the one the bot is walking to, fresh (no time on it yet)
            internal bool DemoHeadFor(Vector3 near)
            {
                NodeData3? node = ReadNodeData.FindClosestNode(_nodeData, near);
                if (node == null)
                {
                    return false;
                }
                _node40 = node;
                _entityRefs.Field1 = node;
                Field118 = 0;
                return true;
            }
        }
    }
}
