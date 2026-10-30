using System;
using System.Numerics;

namespace MphRecomp.Game
{
    // ICollision backed by a static triangle mesh (9 floats per tri: x0,y0,z0, x1,y1,z1, x2,y2,z2,
    // world space). Uses the same barycentric down-ray floor test and Moller-Trumbore forward wall
    // test the renderer runs on its collision triangles -- lifted into the runtime lib so the sim
    // player and the renderer share one implementation and it's headlessly testable.
    public sealed class MeshCollision : ICollision
    {
        private readonly float[] _tri;
        public int TriangleCount => _tri.Length / 9;

        public MeshCollision(float[] triangles) => _tri = triangles ?? Array.Empty<float>();

        // Highest triangle surface directly under (pos.X, pos.Z) at or below pos.Y, or -inf.
        public float FloorBelow(Vector3 pos)
        {
            float ox = pos.X, oy = pos.Y, oz = pos.Z;
            float best = float.NegativeInfinity;
            float[] t = _tri;
            for (int b = 0; b + 8 < t.Length; b += 9)
            {
                float ax = t[b], ay = t[b + 1], az = t[b + 2];
                float bx = t[b + 3], by = t[b + 4], bz = t[b + 5];
                float cx = t[b + 6], cy = t[b + 7], cz = t[b + 8];
                float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (d > -1e-6f && d < 1e-6f) continue;
                float w1 = ((bz - cz) * (ox - cx) + (cx - bx) * (oz - cz)) / d;
                float w2 = ((cz - az) * (ox - cx) + (ax - cx) * (oz - cz)) / d;
                float w3 = 1f - w1 - w2;
                if (w1 < -0.02f || w2 < -0.02f || w3 < -0.02f) continue;
                float y = w1 * ay + w2 * by + w3 * cy;
                if (y <= oy && y > best) best = y;
            }
            return best;
        }

        // Is there a triangle within `dist` along the (horizontal, unit) `dir` from `pos`?
        public bool Blocked(Vector3 pos, Vector3 dir, float dist)
        {
            float len = MathF.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
            if (len < 1e-6f) return false;
            float nx = dir.X / len, nz = dir.Z / len;
            float[] t = _tri;
            for (int b = 0; b + 8 < t.Length; b += 9)
            {
                float hit = RayTri(pos.X, pos.Y, pos.Z, nx, 0f, nz, t, b);
                if (hit >= 0f && hit <= dist) return true;
            }
            return false;
        }

        // Moller-Trumbore ray/triangle intersection; distance along the ray, or -1.
        private static float RayTri(float ox, float oy, float oz, float dx, float dy, float dz, float[] t, int b)
        {
            float e1x = t[b + 3] - t[b], e1y = t[b + 4] - t[b + 1], e1z = t[b + 5] - t[b + 2];
            float e2x = t[b + 6] - t[b], e2y = t[b + 7] - t[b + 1], e2z = t[b + 8] - t[b + 2];
            float px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            float det = e1x * px + e1y * py + e1z * pz;
            if (det > -1e-6f && det < 1e-6f) return -1f;
            float inv = 1f / det;
            float sx = ox - t[b], sy = oy - t[b + 1], sz = oz - t[b + 2];
            float u = (sx * px + sy * py + sz * pz) * inv;
            if (u < 0f || u > 1f) return -1f;
            float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            float v = (dx * qx + dy * qy + dz * qz) * inv;
            if (v < 0f || u + v > 1f) return -1f;
            float tt = (e2x * qx + e2y * qy + e2z * qz) * inv;
            return tt > 1e-3f ? tt : -1f;
        }
    }
}
