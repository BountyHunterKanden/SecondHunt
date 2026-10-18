using System;
using System.Collections.Generic;

namespace MphRecomp.Import.Beyond
{
    // The skinned-bone table a Beyond model's skin slot numbers index (spec 4.6): from the model's character file (CHPR),
    // every render context in order, each of its bone ids in order, named by the abs (skeleton) node whose id's low 16
    // bits equal the bone id. A bone id no abs node matches gets the name "?" and is listed in Unmatched. Sylux's CHPR
    // 91605ed3-3323-519e-9862-f84fbe77f781 gives 86 entries.
    public sealed class BeyondSkinTable
    {
        public string[] Names = Array.Empty<string>();
        public ushort[] BoneIds = Array.Empty<ushort>();
        // positions in Names whose bone id matched no abs node (named "?")
        public readonly List<int> Unmatched = new();

        public int Count => Names.Length;

        public static BeyondSkinTable FromChpr(Chpr chpr)
        {
            // the first abs node per low-16-bit id (an id listed twice keeps its first node)
            var byLow = new Dictionary<int, string>();
            for (int i = 0; i < chpr.AbsNodes.Ids.Length; i++)
            {
                byLow.TryAdd((int)(chpr.AbsNodes.Ids[i] & 0xffff), chpr.AbsNodes.Names[i]);
            }
            var names = new List<string>();
            var ids = new List<ushort>();
            var t = new BeyondSkinTable();
            foreach (Chpr.RenderContext rc in chpr.RenderContexts)
            {
                foreach (ushort b in rc.BoneIds)
                {
                    if (byLow.TryGetValue(b, out string? nm))
                    {
                        names.Add(nm);
                    }
                    else
                    {
                        t.Unmatched.Add(names.Count);
                        names.Add("?");
                    }
                    ids.Add(b);
                }
            }
            t.Names = names.ToArray();
            t.BoneIds = ids.ToArray();
            return t;
        }

        public static BeyondSkinTable FromChpr(byte[] chprAsset) => FromChpr(Chpr.Parse(chprAsset));
    }
}
