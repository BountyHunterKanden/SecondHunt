using System;
using MphRead;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Turns a BipedAnimator's two clip layers into the DS model's bone-matrix palette for one frame,
    // exactly the way the game poses a standing hunter (MphRead PlayerDraw.Draw, biped branch):
    //   1. animate root..Spine_1 with the LEGS layer, with Spine_1 carrying the aim-pitch tilt
    //      (AfterTransform = RotZ(angle), angle clamped to +/-45 deg) and not recursing past it;
    //   2. animate Spine_1's children with the TORSO layer (parented onto the tilted spine);
    //   3. read the matrix stack (Model.UpdateMatrixStack order == the MTX_RESTORE slots the skinned
    //      baker tags vertices with).
    // The palette is MODEL space; the world placement is a separate matrix (Placement) so the GPU does
    // world * bone * local -- identical to the game's per-node `node.Animation *= transform`.
    //
    // Models are shared between instances (Read caches them), and posing writes node.Animation on the
    // shared Model, so Pose() copies the result out immediately: pose -> copy -> next hunter.
    public static class HunterRig
    {
        // The game's aim-pitch clamp: Fixed 2896 = 0.7071 (sin/cos of 45 degrees).
        const float PitchLimit = 2896 / 4096f;

        public static int PaletteSize(Model m) => m.NodeMatrixIds.Count;

        // The spine tilt for an aim vector's Y (PlayerDraw): angle of (sqrt(1-y^2), y), clamped to 45 deg.
        public static float SpineAngle(float aimFacingY)
        {
            float cos = MathF.Sqrt(Math.Max(0f, 1 - aimFacingY * aimFacingY));
            float sin = aimFacingY;
            if (MathF.Abs(aimFacingY) > PitchLimit)
            {
                cos = PitchLimit;
                sin = aimFacingY <= 0 ? -PitchLimit : PitchLimit;
            }
            return MathF.Atan2(sin, cos);
        }

        // aimFacingY = the Y component of the unit aim vector (the game's _facingVector.Y).
        // palette: >= 16 * PaletteSize floats, OpenTK row-major (upload with transpose=false, same as
        // MphRead's desktop renderer does with mtx_stack).
        public static void Pose(BipedAnimator a, float aimFacingY, float[] palette)
        {
            Model model = a.Model;
            Node? spine = model.GetNodeByName("Spine_1");
            if (spine == null)
            {
                // not a biped rig: single-layer pose
                model.AnimateNodes(0, false, Matrix4.Identity, Vector3.One, a.Legs.AnimInfo);
            }
            else
            {
                spine.AnimIgnoreChild = true;
                spine.AfterTransform = Matrix4.CreateRotationZ(SpineAngle(aimFacingY));
                model.AnimateNodes(0, false, Matrix4.Identity, Vector3.One, a.Legs.AnimInfo);
                spine.AnimIgnoreChild = false;
                if (spine.ChildIndex != -1)
                    model.AnimateNodes(spine.ChildIndex, false, Matrix4.Identity, Vector3.One, a.Torso.AnimInfo);
                spine.AfterTransform = null;
            }
            model.UpdateMatrixStack();
            int n = model.NodeMatrixIds.Count * 16;
            for (int i = 0; i < n; i++) palette[i] = model.MatrixStackValues[i];
        }

        // World placement of a standing hunter (PlayerDraw): the body basis comes from the HORIZONTAL
        // facing -- the model's +X maps to -(up x facing), +Z maps to -facing (the DS rigs face -Z) --
        // scaled by the hunter's size factor, with the model origin (feet) dropped below the player's
        // position by the game's MinPickupHeight (so `position` is the game's player position, which
        // sits 0.5 above the floor for every hunter; -animprobe confirmed MinPickupHeight = -0.5).
        public static Matrix4 Placement(Hunter hunter, Vector3 position, Vector3 facing)
        {
            float scale = Metadata.HunterScales[hunter];
            float bottom = Fixed.ToFloat(Metadata.PlayerValues[(int)hunter].MinPickupHeight);
            var lateral = new Vector3(facing.X, 0, facing.Z);
            lateral = lateral.LengthSquared > 1e-12f ? lateral.Normalized() : -Vector3.UnitZ;
            Vector3 gunVec2 = Vector3.Cross(Vector3.UnitY, lateral).Normalized();
            Matrix4 t = Matrix4.Identity;
            t.Row0.Xyz = -gunVec2 * scale;
            t.Row1.Xyz = Vector3.Cross(lateral, gunVec2) * scale;
            t.Row2.Xyz = -lateral * scale;
            t.Row3.Xyz = position;
            t.Row3.Y += bottom + bottom * (1 - scale);
            return t;
        }

        // Where the game's player position sits for a hunter standing with its feet on `floorY`.
        public static float PositionYForFeet(Hunter hunter, float floorY)
        {
            float scale = Metadata.HunterScales[hunter];
            float bottom = Fixed.ToFloat(Metadata.PlayerValues[(int)hunter].MinPickupHeight);
            return floorY - (bottom + bottom * (1 - scale));
        }

        // Model matrix for the skinned shader: the DS model scale applied AFTER the bone matrix (the
        // baker's convention: world = (bone * local) * scale), then the placement.
        public static Matrix4 ModelMatrix(Model model, Matrix4 placement)
            => Matrix4.CreateScale(model.Scale) * placement;

        public static void ToArray(in Matrix4 m, float[] dst, int offset = 0)
        {
            dst[offset + 0] = m.M11; dst[offset + 1] = m.M12; dst[offset + 2] = m.M13; dst[offset + 3] = m.M14;
            dst[offset + 4] = m.M21; dst[offset + 5] = m.M22; dst[offset + 6] = m.M23; dst[offset + 7] = m.M24;
            dst[offset + 8] = m.M31; dst[offset + 9] = m.M32; dst[offset + 10] = m.M33; dst[offset + 11] = m.M34;
            dst[offset + 12] = m.M41; dst[offset + 13] = m.M42; dst[offset + 14] = m.M43; dst[offset + 15] = m.M44;
        }

        public static Hunter HunterFromModelName(string name)
        {
            foreach (Hunter h in new[] { Hunter.Samus, Hunter.Kanden, Hunter.Trace, Hunter.Sylux, Hunter.Noxus, Hunter.Spire, Hunter.Weavel })
                if (Metadata.HunterModels[h][0] == name || Metadata.HunterModels[h][1] == name) return h;
            throw new ArgumentException($"not a hunter body model: {name}");
        }
    }
}
