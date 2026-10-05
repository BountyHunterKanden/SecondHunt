using System;
using System.Linq;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

// The view out of the gunship's window at the planet select: the ROM's deepspace model (shipSpace/deepspace_Model.bin,
// its 181-frame animation looping: the Oubliette's counter-rotating quads, the stars' twinkle) in a headless MphRead
// scene of its own -- no room, no player -- and the game's own camera: an orthographic view, 1 model unit = 1 DS pixel,
// the model moved by the camera position (overlay9_0 0x21075a0 / 0x21065d4; re3_planetselect/spec.md section 1).
// The host's renderer draws it like a room scene (draw items, HostMeshes / HostTextures).
// One MphRead scene at a time: the constructor resets the game state's statics, so no CampaignHost may be alive, and
// the GPU copies of the previous scene must be released first (their ids restart).
namespace MphRecomp.Frontend
{
    public sealed class DeepSpaceView : IDisposable
    {
        // a perspective camera this far back stands in for culling; the frame is drawn orthographic
        private const float Distance = 400;
        private readonly EntityBase _model;
        private double _accum;
        private Vector3 _centre;

        public Scene Scene { get; }

        private DeepSpaceView(Scene scene, EntityBase model)
        {
            Scene = scene;
            _model = model;
        }

        public static DeepSpaceView Start(int width, int height)
        {
            Scene.Headless = true;
            Scene.CollectDrawItems = true;
            Menu.SaveSlot = 0; // GameState.Reset reads no save file
            var scene = new Scene(new Vector2i(width, height), null!, null!, _ => { }, () => { });
            EntityBase model = scene.AddModel("deepspace");
            scene.OnLoad();
            var view = new DeepSpaceView(scene, model);
            view.Look(128, 96);
            return view;
        }

        // the model point at the middle of the DS top screen (pixel (128, 96)); vanilla draws the model translated by the
        // camera position P, so this is (128 - P.x, 96 - P.y)
        public void Look(float x, float y)
        {
            _centre = new Vector3(x, y, 0);
            float fovY = MathHelper.RadiansToDegrees(2 * MathF.Atan(96 / Distance));
            Scene.SetHostCamera(_centre + new Vector3(0, 0, Distance), _centre, Vector3.UnitY, fovY);
        }

        // the Oubliette (nodes prison, prison2, prison3) shows only once it's been found (Areas bit 0x100)
        public void ShowOubliette(bool show)
        {
            foreach (ModelInstance inst in _model.GetModels())
            {
                foreach (Node node in inst.Model.Nodes.Where(n => n.Name.StartsWith("prison", StringComparison.Ordinal)))
                {
                    node.Enabled = show;
                }
            }
        }

        // once per rendered frame (GL thread): the 60 Hz steps that are due, then the frame's orthographic projection
        // (192 units top to bottom, as the DS top screen's 192 rows; the sides widen with the screen)
        public void Step(double seconds, int width, int height)
        {
            Scene.Size = new Vector2i(width, height);
            _accum = Math.Min(_accum + seconds, 4 / 60.0);
            while (_accum >= 1 / 60.0)
            {
                _accum -= 1 / 60.0;
                // with no room the scene doesn't process its entities: the model's animation advances here
                _model.Process();
                Scene.OnUpdateFrame();
            }
            Scene.SetHostProjection(Matrix4.CreateOrthographic(192f * width / height, 192, 1, Distance * 2));
        }

        public void Dispose() => Scene.DoCleanup();
    }
}
