using OpenTK.Mathematics;

namespace MphRead.Entities.FirstHunt
{
    // recomp: First Hunt's effect entity (type 7; docs/FIRST_HUNT_PORT.md P4). The cart creates one at an enemy's death
    // (create 0x202186c, param 0x17): the effWaspDeath model at the enemy's position, scale 1, no rotation, its
    // animation 0 played once (flags 8); it removes itself after the animation's frames - 1 (process 0x2021780).
    // Param 0x16 is the spawnEffect model, which the player spawn (0x200a834) creates at the spawn point every spawn.
    public class FhEffectEntity : EntityBase
    {
        private int _lifespan = 0;

        public int Lifespan => _lifespan;

        public FhEffectEntity(Scene scene) : base(EntityType.FhEffectInstance, scene)
        {
        }

        public static FhEffectEntity Create(Scene scene, Vector3 position, string name = "effWaspDeath")
        {
            var effect = new FhEffectEntity(scene);
            ModelInstance model = effect.SetUpModel(name, 0, AnimFlags.NoLoop, firstHunt: true);
            int frames = 0;
            if (model.Model.AnimationGroups.Node.Count > 0)
            {
                frames = model.Model.AnimationGroups.Node[0].FrameCount;
            }
            else if (model.Model.AnimationGroups.Material.Count > 0)
            {
                frames = model.Model.AnimationGroups.Material[0].FrameCount;
            }
            else if (model.Model.AnimationGroups.Texture.Count > 0)
            {
                frames = model.Model.AnimationGroups.Texture[0].FrameCount;
            }
            effect._lifespan = (frames - 1) * 2; // todo: FPS stuff
            effect.Position = position;
            return effect;
        }

        // the model loaded ahead of its first use (the enemy spawners' Initialize)
        public static void Preload(string name = "effWaspDeath")
        {
            Read.GetModelInstance(name, firstHunt: true);
        }

        public override bool Process()
        {
            if (_lifespan-- <= 0)
            {
                return false;
            }
            return base.Process();
        }
    }
}
