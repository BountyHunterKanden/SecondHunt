using System.Collections.Generic;

namespace MphRecomp.Import.Retro
{
    // A Retro skin (CSKR): groups of (bone id, weight) lists, each shared by the next run of vertices, in position order.
    // Port of prime_model.py's skin read.
    public static class Cskr
    {
        public static List<(uint Bone, float Weight)[]> Load(byte[] s)
        {
            var per = new List<(uint, float)[]>();
            int o = 0;
            int ng = checked((int)Be.U32(s, o)); o += 4;
            for (int g = 0; g < ng; g++)
            {
                int nw = checked((int)Be.U32(s, o)); o += 4;
                var ws = new (uint, float)[nw];
                for (int i = 0; i < nw; i++) ws[i] = (Be.U32(s, o + 8 * i), Be.F32(s, o + 8 * i + 4));
                o += 8 * nw;
                int nv = checked((int)Be.U32(s, o)); o += 4;
                for (int v = 0; v < nv; v++) per.Add(ws);
            }
            return per;
        }
    }
}
