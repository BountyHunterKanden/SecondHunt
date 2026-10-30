using System.Collections.Generic;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp, PC tools only (BotProbe's links option): drives a bot down every link of an arena's route graph, one at a
    // time, to find each link a bot can't get along (owner 2026-10-26: bots never get stuck AND never give up a path
    // vanilla has; fixes keep every link). No game change: nothing here runs in a match.
    public partial class PlayerEntity
    {
        public partial class PlayerAiData
        {
            // the bot's route graph (the room's first node layer, as the AI reads it)
            public IReadOnlyList<NodeData3> AuditNodes => _nodeList;

            // a node's links as the AI's own route table has them: the distinct next hops (Func213A0A4, at most 19)
            public List<NodeData3> AuditLinks(NodeData3 node)
            {
                var buf = new NodeData3?[19];
                int count = Func213A0A4(node, buf);
                var links = new List<NodeData3>(count);
                for (int i = 0; i < count; i++)
                {
                    links.Add(buf[i]!);
                }
                return links;
            }

            // the AI's own next hop from a node toward a final stop (its route table, Func213A1A8)
            public NodeData3 AuditNextHop(NodeData3 at, NodeData3 goal)
            {
                NodeData3? n3C = _node3C, n40 = _node40;
                _node3C = goal;
                _node40 = at;
                NodeData3 next = Func213A1A8();
                _node3C = n3C;
                _node40 = n40;
                return next;
            }

            // reached it as the AI judges it (the same check that moves it on to its next node)
            public bool AuditInRange(NodeData3 node) => IsNodeInRange(node);

            // every step of a link's run: the node is both where it's going and its final stop, fresh (no give-up time).
            // The lock also holds through the step: every node the AI looks up (its goal picks: items, a foe, a spot to roam
            // to, the closest node) comes back as this one (FindEntityRef -> AuditFind), so its own travel code walks the
            // link and nothing else. null = off (always, in a match).
            public void AuditHold(NodeData3? node)
            {
                _auditGoal = node;
                if (node == null)
                {
                    return;
                }
                _node3C = node;
                _node40 = node;
                _entityRefs.Field1 = node;
                // (its saved next hop too: in the air it steers by that instead of a look-up)
                _field4C[0] = node;
                Field118 = 0;
                // and no item it had already picked to go after (its item look-ups come back empty, but one chosen before
                // the hold would keep pulling it off the link)
                UpdateTargetItem(null);
            }

            private NodeData3? _auditGoal = null;
            private bool _auditFinding = false;

            // FindEntityRef's first step: with the lock on, the real look-up runs, then every node it can hand back is the held one
            private bool AuditFind(AiEntRefType type)
            {
                if (_auditGoal == null || _auditFinding)
                {
                    return false;
                }
                _auditFinding = true;
                FindEntityRef(type);
                _auditFinding = false;
                NodeData3 g = _auditGoal;
                _entityRefs.Field0 = g; _entityRefs.Field1 = g; _entityRefs.Field2 = g; _entityRefs.Field3 = g; _entityRefs.Field4 = g;
                _entityRefs.Field5 = g; _entityRefs.Field6 = g; _entityRefs.Field7 = g; _entityRefs.Field8 = g; _entityRefs.Field9 = g;
                _entityRefs.Field10 = g; _entityRefs.Field11 = g; _entityRefs.Field12 = g; _entityRefs.Field13 = g; _entityRefs.Field14 = g;
                _entityRefs.Field15 = g; _entityRefs.Field16 = g; _entityRefs.Field17 = g; _entityRefs.Field18 = g; _entityRefs.Field19 = g;
                _entityRefs.Field20 = g; _entityRefs.Field21 = g; _entityRefs.Field22 = g; _entityRefs.Field23 = g; _entityRefs.Field24 = g;
                _entityRefs.Field25 = g;
                // and no pickups to go after: an item's spawn or instance would take it off the link
                _entityRefs.Field34 = null; _entityRefs.Field35 = null; _entityRefs.Field36 = null; _entityRefs.Field37 = null; _entityRefs.Field38 = null;
                _entityRefs.Field39 = null; _entityRefs.Field40 = null; _entityRefs.Field41 = null; _entityRefs.Field42 = null; _entityRefs.Field43 = null;
                _entityRefs.Field44 = null; _entityRefs.Field45 = null; _entityRefs.Field46 = null; _entityRefs.Field47 = null; _entityRefs.Field48 = null;
                _entityRefs.Field49 = null; _entityRefs.Field50 = null; _entityRefs.Field51 = null; _entityRefs.Field52 = null; _entityRefs.Field53 = null;
                _entityRefs.Field54 = null; _entityRefs.Field55 = null; _entityRefs.Field56 = null; _entityRefs.Field57 = null; _entityRefs.Field58 = null;
                _entityRefs.Field59 = null; _entityRefs.Field60 = null; _entityRefs.Field61 = null; _entityRefs.Field62 = null; _entityRefs.Field63 = null;
                _entityRefs.Field64 = null; _entityRefs.Field65 = null; _entityRefs.Field66 = null; _entityRefs.Field67 = null; _entityRefs.Field68 = null;
                _entityRefs.Field69 = null; _entityRefs.Field70 = null; _entityRefs.Field71 = null; _entityRefs.Field72 = null; _entityRefs.Field73 = null;
                return true;
            }

            // after a step: still on the held link (its next node and final stop are the held one)
            public bool AuditOnHold => _auditGoal != null && _node40 == _auditGoal && _node3C == _auditGoal;

            // natural-play link log: the node it's heading for, the one it last reached (set as it moves on), and whether it's
            // after a player (a bot standing still while fighting isn't stuck)
            public NodeData3? AuditTarget => _node40;
            public NodeData3? AuditPrev => _node44;
            public bool AuditFighting => Flags2.TestFlag(AiFlags2.TargetPlayer);
            public bool AuditSees => UnstickSeesFoe();
            // following nodes this step (the travel driver ran), as against a fight's own moves
            public bool AuditTravel => (long)_scene.FrameCount - _unstickTravelAt <= 2;
            // pressed against a wall: the frames it has been hitting one sideways, without a break (vanilla calls 20+ stuck)
            public bool AuditWall => _player._horizColTimer > 2 * 2; // todo: FPS stuff

            public string AuditMove() => $"wall {_player._horizColTimer} grounded {_player.Flags1.TestFlag(PlayerFlags1.Grounded)} f118 {Field118} speed {_player.Speed.X:0.00},{_player.Speed.Y:0.00},{_player.Speed.Z:0.00} "
                + $"hold {AuditOnHold} n3c {_node3C?.Id} at {(long)_scene.FrameCount - _unstickTravelAt} travel {AuditTravel} unstick {BotUnstickOn} prevInRange {(_node44 != null && IsNodeInRange(_node44))} "
                + $"clock {_unstickFrames} climb {_unstickClimb} climbs {_unstickClimbs} drift {_unstickDrift} counts {HostUnstickCounts[0]},{HostUnstickCounts[1]},{HostUnstickCounts[2]},{HostUnstickCounts[3]}";

            // a teleport (the audit's Put) starts the stuck fix's clock again: in a match a bot never jumps spots, but the audit
            // puts it down within 2 units of where the last link ended, and the carried clock fired a jump at once (a test artifact)
            public void AuditUnstickReset()
            {
                _unstickAt = _player.Position;
                _unstickFrames = 0;
                _unstickClimbs = 0;
                _unstickClimb = 0;
                _unstickUnmorphs = 0;
                _unstickDrifts = 0;
                _unstickDrift = 0;
                _unstickAvoidCount = 0;
                _unstickSeenValid = false;
            }

            public string AuditState() => $"node40 {_node40?.Id} flags2 {Flags2} target {_targetPlayer?.Hunter}";
        }

        // a start at an alt-form node needs the bot already morphed (the AI morphs before it gets to one; a biped put into
        // a morph tunnel falls out of the world, which the audit would grade as a death)
        public bool AuditNeedsMorph(NodeData3 from) => from.NodeType == NodeType.AltForm && !IsAltForm;

        public bool AuditMorphing => IsMorphing;

        public void AuditMorph() => TrySwitchForms(true);

        // puts the bot at a link's start, facing along it, standing still (biped unless the start is an alt-form node)
        public void AuditPut(NodeData3 from, NodeData3 to, bool resetClock = false)
        {
            if (IsAltForm && from.NodeType != NodeType.AltForm)
            {
                ExitAltForm();
            }
            Vector3 flat = (to.Position - from.Position).WithY(0);
            Vector3 facing = flat.LengthSquared > 0.01f ? flat.Normalized() : -Vector3.UnitX;
            Vector3 putPos = from.Position;
            // a node sits at floor height; a ball rolling there rests 0.5 above it (measured), so put it there: at the node itself it starts inside the floor and falls out of the world (a test artifact)
            if (IsAltForm)
            {
                putPos = putPos.AddY(0.5f);
            }
            Teleport(putPos, facing, _scene.GetNodeRefContaining(from.Position));
            PrevPosition = Position;
            Speed = Vector3.Zero;
            if (resetClock)
            {
                AiData.AuditUnstickReset();
            }
            if (IsAltForm)
            {
                // the AI reads NoUnmorph before this frame's collision sets it, so right after a teleport it holds the old spot's
                // value and a bot put in a morph tunnel unmorphs in the first frame and falls out (a test artifact; in a match the
                // flag is always the last frame's, at the same spot)
                Flags1 |= PlayerFlags1.NoUnmorph;
            }
        }
    }
}
