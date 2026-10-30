using System;

namespace MphRecomp.Render
{
    // MPH's double damage look on a custom body (GxShader uDD / uDDMtx, the texture through uCls7): the pickup's texture as an environment
    // map off the camera-space normal, turning with time. The matrix is MphRead's PlayerDraw.GetTexcoordMatrix (the
    // flip, RotZ then RotY at the game's two speeds, 1 / (width / 2) x 16), written for a column vector: MphRead applies
    // its row-vector matrix as n * view * T, so here uv = (k * Flip * Ry * Rz) * viewNormal, k = 32 / texture width.
    public static class DoubleDamageLook
    {
        // liveFrames = Scene.LiveFrames, texWidth = the double damage model's first texture's width; m9 = column-major mat3
        public static void Matrix(ulong liveFrames, int texWidth, float[] m9)
        {
            ulong frame = liveFrames / 2;
            float rotZ = ((int)(16 * ((781874935307UL * (53248 * frame) >> 32) + 2048)) >> 20) * (360 / 4096f);
            float rotY = ((int)(16 * ((781874935307UL * (26624 * frame) + 0x80000000000) >> 32)) >> 20) * (360 / 4096f);
            float z = rotZ * MathF.PI / 180f, y = rotY * MathF.PI / 180f;
            float cz = MathF.Cos(z), sz = MathF.Sin(z), cy = MathF.Cos(y), sy = MathF.Sin(y);
            float k = 32f / Math.Max(texWidth, 1);
            // Ry * Rz (standard column-vector rotations), rows r0..r2
            float a00 = cy * cz, a01 = -cy * sz, a02 = sy;
            float a10 = sz, a11 = cz, a12 = 0f;
            float a20 = -sy * cz, a21 = sy * sz, a22 = cy;
            // Flip = diag(1, -1, -1), then k; column-major out
            m9[0] = k * a00; m9[1] = -k * a10; m9[2] = -k * a20;
            m9[3] = k * a01; m9[4] = -k * a11; m9[5] = -k * a21;
            m9[6] = k * a02; m9[7] = -k * a12; m9[8] = -k * a22;
        }
    }
}
