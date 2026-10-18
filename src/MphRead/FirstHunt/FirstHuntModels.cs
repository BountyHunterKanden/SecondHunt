using System.Collections.Generic;
using MphRead.Formats;

namespace MphRead
{
    // recomp: First Hunt model data that MphRead reads the MPH way (docs/FIRST_HUNT_PORT.md).
    public static class FirstHuntModels
    {
        // A First Hunt texture animation's keys number the animation's own frames, not the model's textures: a material's
        // lowest key is its own texture/palette and the rest follow on in the model. bombStart (lambert2 = texture 0,
        // PlasmaMat = 1, keys 0-5 in a 7-texture model) and gunEffElectroCharge (lambertSphere = 1, keys 0-3, then
        // lambertStreak = 5) drew another material's texture and never reached their last frame; the menus' frame
        // lightning (keys 0-7 = textures 12-19) the same. Turned into model ids here, once, at load: per animation, key -
        // lowest key + the material's own id, when that stays inside the model's textures and palettes. Ranges shared by
        // two animations are moved once (by the first).
        public static void RemapTextureAnimations(IReadOnlyList<TextureAnimationGroup> groups, IReadOnlyList<RawMaterial> materials,
            int textureCount, int paletteCount)
        {
            foreach (TextureAnimationGroup group in groups)
            {
                if (group.TextureIds is not List<ushort> textureIds || group.PaletteIds is not List<ushort> paletteIds)
                {
                    continue;
                }
                var moved = new HashSet<int>();
                foreach (KeyValuePair<string, TextureAnimation> pair in group.Animations)
                {
                    TextureAnimation animation = pair.Value;
                    RawMaterial? material = null;
                    foreach (RawMaterial m in materials)
                    {
                        if (m.Name.MarshalString() == pair.Key)
                        {
                            material = m;
                            break;
                        }
                    }
                    if (material is not RawMaterial mat || mat.TextureId < 0)
                    {
                        continue;
                    }
                    int start = animation.StartIndex, end = animation.StartIndex + animation.Count;
                    if (start < 0 || end > textureIds.Count || end > paletteIds.Count)
                    {
                        continue;
                    }
                    int minTexture = int.MaxValue, minPalette = int.MaxValue, maxTexture = -1, maxPalette = -1;
                    bool shared = false;
                    for (int j = start; j < end; j++)
                    {
                        minTexture = System.Math.Min(minTexture, textureIds[j]);
                        maxTexture = System.Math.Max(maxTexture, textureIds[j]);
                        minPalette = System.Math.Min(minPalette, paletteIds[j]);
                        maxPalette = System.Math.Max(maxPalette, paletteIds[j]);
                        shared |= moved.Contains(j);
                    }
                    if (shared || maxTexture < 0)
                    {
                        continue;
                    }
                    int addTexture = mat.TextureId - minTexture;
                    int addPalette = mat.PaletteId >= 0 ? mat.PaletteId - minPalette : 0;
                    if (addTexture == 0 && addPalette == 0
                        || maxTexture + addTexture >= textureCount || minTexture + addTexture < 0
                        || maxPalette + addPalette >= paletteCount || minPalette + addPalette < 0)
                    {
                        continue;
                    }
                    for (int j = start; j < end; j++)
                    {
                        textureIds[j] = (ushort)(textureIds[j] + addTexture);
                        paletteIds[j] = (ushort)(paletteIds[j] + addPalette);
                        moved.Add(j);
                    }
                }
            }
        }
    }
}
