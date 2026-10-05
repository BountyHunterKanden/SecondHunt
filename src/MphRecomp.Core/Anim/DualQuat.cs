using System;
using OpenTK.Mathematics;

namespace MphRecomp.Anim
{
    // Dual-quaternion skinning (Kavan et al.): each bone's rigid skin transform becomes a unit dual
    // quaternion; a vertex blends them (sign-aligned, then normalised) instead of blending matrices. Unlike
    // linear blend skinning it keeps volume under large joint rotations -- which matters here because a
    // trophy's sculpted (bind) pose can be ~120 degrees from where the game's clips put a limb.
    // Conventions: OpenTK's -- Vector3.Transform(v, q) rotates like Matrix4.CreateFromQuaternion(q) does
    // for row vectors (checked by -animtest). Scale is not representable; hunter skin transforms are rigid.
    public readonly struct DualQuat
    {
        public readonly Quaternion Real, Dual;
        public DualQuat(Quaternion real, Quaternion dual) { Real = real; Dual = dual; }

        // rigid row-vector matrix (v * M = rotate(v) + t)  ->  dual quaternion
        public static DualQuat FromMatrix(Matrix4 m)
        {
            var r = m.ExtractRotation().Normalized();
            var t = m.ExtractTranslation();
            var d = new Quaternion(t, 0f) * r;
            return new DualQuat(r, new Quaternion(d.X * 0.5f, d.Y * 0.5f, d.Z * 0.5f, d.W * 0.5f));
        }

        // Blend up to 4 (weights sum ~1); signs aligned to the first so antipodal quaternions don't cancel.
        public static DualQuat Blend(ReadOnlySpan<DualQuat> dq, ReadOnlySpan<float> w)
        {
            Vector4 r = Vector4.Zero, d = Vector4.Zero;
            var r0 = dq[0].Real;
            for (int i = 0; i < dq.Length; i++)
            {
                if (w[i] == 0) continue;
                float s = Vector4.Dot(V(dq[i].Real), V(r0)) < 0 ? -w[i] : w[i];
                r += V(dq[i].Real) * s; d += V(dq[i].Dual) * s;
            }
            float len = r.Length;
            if (len < 1e-12f) return new DualQuat(Quaternion.Identity, new Quaternion(0, 0, 0, 0));
            r /= len; d /= len;
            return new DualQuat(new Quaternion(r.X, r.Y, r.Z, r.W), new Quaternion(d.X, d.Y, d.Z, d.W));
        }

        public Vector3 TransformPoint(Vector3 p)
        {
            var t = Dual * Quaternion.Conjugate(Real);
            return Vector3.Transform(p, Real) + new Vector3(t.X, t.Y, t.Z) * 2f;
        }
        public Vector3 TransformNormal(Vector3 n) => Vector3.Transform(n, Real);

        static Vector4 V(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    }
}
