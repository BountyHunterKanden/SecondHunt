using System.Collections.Generic;

namespace MphRecomp.Import.Retro
{
    // A Retro skeleton (CINF): bones (id, parent id, bind position in model space -- Prime binds carry no rotation),
    // then the bone names. Port of prime_model.py's skeleton() (Echoes bones carry 32 more bytes, told apart by the
    // same heuristic).
    public sealed class Cinf
    {
        public sealed class Bone
        {
            public uint Id, Parent;
            public string? Name;
            public double X, Y, Z;   // bind position
        }

        public readonly Dictionary<uint, Bone> Bones = new();

        public bool Contains(uint id) => Bones.ContainsKey(id);

        // bone ids ascending (Python's sorted(skel))
        public List<uint> SortedIds()
        {
            var ids = new List<uint>(Bones.Keys);
            ids.Sort();
            return ids;
        }

        public static Cinf Load(byte[] c)
        {
            var sk = new Cinf();
            int o = 0;
            int nb = checked((int)Be.U32(c, o)); o += 4;
            for (int i = 0; i < nb; i++)
            {
                uint bid = Be.U32(c, o), par = Be.U32(c, o + 4);
                float x = Be.F32(c, o + 8), y = Be.F32(c, o + 12), z = Be.F32(c, o + 16);
                o += 20;
                uint check = Be.U32(c, o);
                if (check > 100 || check == 0) o += 32;
                int nl = checked((int)Be.U32(c, o));
                o += 4 + 4 * nl;
                sk.Bones[bid] = new Bone { Id = bid, Parent = par, X = x, Y = y, Z = z };
            }
            int nids = checked((int)Be.U32(c, o));
            o += 4 + 4 * nids;
            int nn = checked((int)Be.U32(c, o)); o += 4;
            for (int i = 0; i < nn; i++)
            {
                string nm = Be.CString(c, o, out int e);
                o = e + 1;
                uint bid = Be.U32(c, o); o += 4;
                if (sk.Bones.TryGetValue(bid, out Bone? b)) b.Name = nm;
            }
            return sk;
        }
    }
}
