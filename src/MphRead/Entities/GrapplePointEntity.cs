using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: one of an Echoes arena's grapple points (docs/GRAPPLE.md G0; Core Arenas/EchoesGrapple.cs adds them to a
    // match's room). Holds where the point is; the swing is the player's (PlayerGrapple.cs). Not drawn yet (G2: the ring
    // and the beam), not hit by shots.
    public sealed class GrapplePointEntity : EnemyInstanceEntity
    {
        readonly string _node;

        public bool Usable { get; set; } = true;

        public GrapplePointEntity(int id, Vector3 position, string node, Scene scene)
            : base(new EnemyInstanceEntityData(EnemyType.HitZone, null!), Formats.Culling.NodeRef.None, scene)
        {
            Id = id;
            _node = node;
            Position = position;
        }

        protected override void EnemyInitialize()
        {
            NodeRef = _scene.GetNodeRefByName(_node);
            Flags = EnemyFlags.NoMaxDistance | EnemyFlags.Static | EnemyFlags.Invincible;
            _hurtVolumeInit = _hurtVolume = new CollisionVolume(Position.AddY(-10000), 0);
        }

        public override bool GetTargetable()
        {
            return false;
        }

        protected override bool EnemyGetDrawInfo()
        {
            return true;
        }
    }
}
