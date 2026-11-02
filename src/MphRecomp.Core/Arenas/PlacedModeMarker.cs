using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Arenas
{
    // What the dev placement editor's mode choices place (owner 2026-11-01, Mining Plaza: "place all of the different mode
    // elements"): the octolith modes' octoliths and bases and the node modes' nodes. A match builds MphRead's own entities
    // from them (PickupPlacement.ModeEntities, the map maker's sizes: MapCompiler); the solo run plays Battle, where those
    // stay invisible, so the editor shows this stand-in with the same models instead
    public static class ModeKinds
    {
        public const string Capture = "Capture";   // a team's octolith and its base (Team 0 / 1)
        public const string Octolith = "Octolith"; // Bounty's octolith
        public const string Drop = "Drop";         // Bounty's drop-off base
        public const string Node = "Node";         // a Nodes node
        public const string Defend = "Defend";     // Defender's ring

        // the mode a kind belongs to (Bounty's octolith and drop-off go together)
        public static string Mode(string kind) => kind is Octolith or Drop ? "Bounty" : kind;

        // one of these a match (placing another moves it); nodes: as many as wanted
        public static bool Single(string kind) => kind != Node;

        public static string Label(string kind, int team) => kind switch
        {
            Capture => $"Capture base TEAM {team + 1}",
            Octolith => "Bounty octolith",
            Drop => "Bounty drop-off",
            Node => "Node",
            Defend => "Defender ring",
            _ => kind,
        };

        // MapCompiler's volumes: base r1 h1, node r2.5 h4, defender ring r4 h4
        public static float Radius(string kind) => kind switch { Node => 2.5f, Defend => 4f, _ => 1f };
        public static float Height(string kind) => kind is Node or Defend ? 4f : 1f;
    }

    // the editor's stand-in: the models the real entity draws in its mode, where it draws them (octolith 1.25 over its
    // base, OctolithFlagEntity; the node's ring scaled to its radius 0.7 up, turning, NodeDefenseEntity)
    public sealed class PlacedModeMarker : EntityBase
    {
        public string Kind { get; }
        public int Team { get; }
        readonly float _radius;
        float _turn;

        public PlacedModeMarker(string kind, int team, Vector3 floor, Scene scene) : base(EntityType.Model, scene)
        {
            Kind = kind;
            Team = team;
            _radius = ModeKinds.Radius(kind);
            Position = floor;
            switch (kind)
            {
            case ModeKinds.Capture:
                SetUpModel("octolith_ctf");
                SetUpModel("flagbase_ctf");
                Recolor = team;
                break;
            case ModeKinds.Octolith:
                SetUpModel("octolith_ctf");
                SetUpModel("flagbase_bounty");
                Recolor = 2;
                break;
            case ModeKinds.Drop:
                SetUpModel("flagbase_cap");
                break;
            default:
                SetUpModel("koth_data_flow");
                SetUpModel("koth_terminal");
                break;
            }
        }

        public override bool GetTargetable() => false;

        public override bool Process()
        {
            _turn = (_turn + 1) % 360;
            return base.Process();
        }

        protected override Matrix4 GetModelTransform(ModelInstance inst, int index)
        {
            Matrix4 transform = base.GetModelTransform(inst, index);
            if (Kind is ModeKinds.Capture or ModeKinds.Octolith && index == 0)
            {
                transform.Row3.Xyz = Position.AddY(1.25f);
            }
            else if (Kind is ModeKinds.Node or ModeKinds.Defend && index == 1)
            {
                transform = Matrix4.CreateScale(_radius) * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(_turn)) * transform;
                transform.Row3.Xyz = Position.AddY(0.7f);
            }
            return transform;
        }

        protected override int GetModelRecolor(ModelInstance inst, int index) =>
            Kind == ModeKinds.Octolith && index == 1 ? 0 : base.GetModelRecolor(inst, index);
    }
}
