using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MphRecomp.Replay
{
    public sealed partial class SimSnapshot
    {
        // (instant seek always, owner 11-21: "ALWAYS HAVE INSTANT SEEKING right away") the step-0 walk named by
        // where each thing sits instead of by its place in the walk: an object or leaf by the path to it from a static or a
        // root (field by field, element by element) plus its type (and an array's lengths), a static by its field. The
        // prepare process writes its names beside its keyframes; a viewer whose own walk differs (a pool an earlier match
        // grew, a cache it filled, a static only one copy has) matches the two by name: what only the prepared copy has is
        // made fresh from the keyframe (an object) or left out (a static), what only the viewer has is left alone, and a
        // keyframe is refused only when it truly needs something the viewer can't give (a leaf: file data it never loaded).
        // Exact or REFUSED (owner 10-07) still holds: every step played after a load is checked against the recording.
        public sealed class KeyframeIdentity
        {
            internal const uint Magic = 0x3149464B; // "KFI1"
            internal ulong[] ObjectKeys = Array.Empty<ulong>();
            internal int[] ObjectTypes = Array.Empty<int>();
            internal int[]?[] ObjectLengths = Array.Empty<int[]?>(); // an array's lengths (null: not an array)
            internal string?[] ObjectResolved = Array.Empty<string?>(); // a KeyframeResolvers type's key (found by name, not made)
            internal ulong[] LeafKeys = Array.Empty<ulong>();
            internal int[] LeafTypes = Array.Empty<int>();
            internal (int Assembly, int Token)[] Statics = Array.Empty<(int, int)>();
            internal string[] TypeNames = Array.Empty<string>();
            internal string[] AssemblyNames = Array.Empty<string>();

            public int ObjectCount => ObjectKeys.Length;
            public int LeafCount => LeafKeys.Length;

            public static KeyframeIdentity Of(KeyframeBase b)
            {
                SimSnapshot s = b.Snap;
                int n = s._objects.Length;
                var types = new List<string>();
                var typeIndex = new Dictionary<Type, int>();
                var typeHash = new Dictionary<Type, ulong>();
                int TypeOf(Type t)
                {
                    if (!typeIndex.TryGetValue(t, out int i))
                    {
                        typeIndex[t] = i = types.Count;
                        types.Add(t.AssemblyQualifiedName ?? t.FullName ?? t.Name);
                    }
                    return i;
                }
                ulong HashOf(Type t)
                {
                    if (!typeHash.TryGetValue(t, out ulong h)) typeHash[t] = h = Str(t.FullName ?? t.Name);
                    return h;
                }
                var path = new ulong[n];
                var has = new bool[n];
                var leafPath = new ulong[b.Leaves.Count];
                var leafHas = new bool[b.Leaves.Count];
                void Child(ulong parent, int slot, int k, object c)
                {
                    ulong p = Mix(Mix(parent, (ulong)slot + 1), (ulong)k + 1);
                    if (b.Index.TryGetValue(c, out int id))
                    {
                        if (!has[id]) (path[id], has[id]) = (p, true);
                    }
                    else if (b.LeafIndex.TryGetValue(c, out int l) && !leafHas[l])
                    {
                        (leafPath[l], leafHas[l]) = (p, true);
                    }
                }
                var id = new KeyframeIdentity();
                var assemblies = new List<Assembly>();
                id.Statics = new (int, int)[s._statics.Length];
                for (int i = 0; i < s._statics.Length; i++)
                {
                    (FieldInfo f, object? v) = s._statics[i];
                    int a = assemblies.IndexOf(f.Module.Assembly);
                    if (a < 0)
                    {
                        a = assemblies.Count;
                        assemblies.Add(f.Module.Assembly);
                    }
                    id.Statics[i] = (a, f.MetadataToken);
                    ulong sk = Mix(Str(f.DeclaringType?.FullName ?? "?"), Str(f.Name));
                    int k = 0;
                    ForEachRef(f.FieldType, v, c => Child(sk, 0, k++, c));
                }
                id.AssemblyNames = assemblies.Select(a => a.GetName().Name ?? "").ToArray();
                var roots = new Dictionary<Type, int>();
                id.ObjectKeys = new ulong[n];
                id.ObjectTypes = new int[n];
                id.ObjectLengths = new int[]?[n];
                id.ObjectResolved = new string?[n];
                for (int i = 0; i < n; i++)
                {
                    object o = s._objects[i];
                    Type t = o.GetType();
                    if (!has[i])
                    {
                        roots.TryGetValue(t, out int r);
                        roots[t] = r + 1;
                        path[i] = Mix(Mix(0x526F6F74UL, HashOf(t)), (ulong)r + 1);
                    }
                    ulong key = Mix(path[i], HashOf(t));
                    id.ObjectTypes[i] = TypeOf(t);
                    if (KeyframeResolvers.TryGetValue(t, out var resolver)) id.ObjectResolved[i] = resolver.Key(o);
                    TypeInfo info = Info(t);
                    if (s._arrays[i] is Array arr)
                    {
                        var lengths = new int[arr.Rank];
                        for (int r = 0; r < arr.Rank; r++)
                        {
                            lengths[r] = arr.GetLength(r);
                            key = Mix(key, (ulong)lengths[r] + 1);
                        }
                        id.ObjectLengths[i] = lengths;
                        id.ObjectKeys[i] = key;
                        if (info.ElementSize > 0) continue;
                        Type e = t.GetElementType()!;
                        int slot = 0;
                        foreach (object? x in arr)
                        {
                            int k = 0;
                            ForEachRef(e, x, c => Child(path[i], slot, k++, c));
                            slot++;
                        }
                        continue;
                    }
                    id.ObjectKeys[i] = key;
                    object?[] values = s._fields[i]!;
                    for (int j = 0; j < values.Length; j++)
                    {
                        if (!info.RefField[j]) continue;
                        int k = 0;
                        ForEachRef(info.Fields[j].FieldType, values[j], c => Child(path[i], j, k++, c));
                    }
                }
                int pool = 0;
                foreach (Func<IEnumerable<object>> leaves in KeyframeLeafPools)
                {
                    int k = 0;
                    foreach (object o in leaves())
                    {
                        if (b.LeafIndex.TryGetValue(o, out int l) && !leafHas[l])
                        {
                            (leafPath[l], leafHas[l]) = (Mix(Mix(0x506F6F6CUL, (ulong)pool + 1), (ulong)k + 1), true);
                        }
                        k++;
                    }
                    pool++;
                }
                id.LeafKeys = new ulong[b.Leaves.Count];
                id.LeafTypes = new int[b.Leaves.Count];
                for (int l = 0; l < b.Leaves.Count; l++)
                {
                    Type t = b.Leaves[l].GetType();
                    id.LeafKeys[l] = Mix(leafHas[l] ? leafPath[l] : Mix(0x4C656166UL, (ulong)l + 1), HashOf(t));
                    id.LeafTypes[l] = TypeOf(t);
                }
                id.TypeNames = types.ToArray();
                return id;
            }

            static ulong Str(string s)
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in s)
                {
                    h ^= c;
                    h *= 1099511628211UL;
                }
                return h;
            }

            static ulong Mix(ulong h, ulong v)
            {
                h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
                h *= 1099511628211UL;
                return h ^ (h >> 31);
            }

            public void Write(BinaryWriter w)
            {
                w.Write(Magic);
                w.Write7BitEncodedInt(AssemblyNames.Length);
                foreach (string a in AssemblyNames) w.Write(a);
                w.Write7BitEncodedInt(TypeNames.Length);
                foreach (string t in TypeNames) w.Write(t);
                w.Write7BitEncodedInt(ObjectKeys.Length);
                for (int i = 0; i < ObjectKeys.Length; i++)
                {
                    w.Write(ObjectKeys[i]);
                    w.Write7BitEncodedInt(ObjectTypes[i]);
                    int[]? lengths = ObjectLengths[i];
                    w.Write7BitEncodedInt(lengths?.Length ?? 0);
                    if (lengths != null) foreach (int x in lengths) w.Write7BitEncodedInt(x);
                    w.Write(ObjectResolved[i] != null);
                    if (ObjectResolved[i] != null) w.Write(ObjectResolved[i]!);
                }
                w.Write7BitEncodedInt(LeafKeys.Length);
                for (int i = 0; i < LeafKeys.Length; i++)
                {
                    w.Write(LeafKeys[i]);
                    w.Write7BitEncodedInt(LeafTypes[i]);
                }
                w.Write7BitEncodedInt(Statics.Length);
                foreach ((int a, int token) in Statics)
                {
                    w.Write7BitEncodedInt(a);
                    w.Write(token);
                }
            }

            public static KeyframeIdentity Read(BinaryReader r)
            {
                if (r.ReadUInt32() != Magic) throw new InvalidDataException("no keyframe identity");
                var id = new KeyframeIdentity();
                id.AssemblyNames = new string[r.Read7BitEncodedInt()];
                for (int i = 0; i < id.AssemblyNames.Length; i++) id.AssemblyNames[i] = r.ReadString();
                id.TypeNames = new string[r.Read7BitEncodedInt()];
                for (int i = 0; i < id.TypeNames.Length; i++) id.TypeNames[i] = r.ReadString();
                int n = r.Read7BitEncodedInt();
                id.ObjectKeys = new ulong[n];
                id.ObjectTypes = new int[n];
                id.ObjectLengths = new int[]?[n];
                id.ObjectResolved = new string?[n];
                for (int i = 0; i < n; i++)
                {
                    id.ObjectKeys[i] = r.ReadUInt64();
                    id.ObjectTypes[i] = r.Read7BitEncodedInt();
                    int rank = r.Read7BitEncodedInt();
                    if (rank > 0)
                    {
                        var lengths = new int[rank];
                        for (int k = 0; k < rank; k++) lengths[k] = r.Read7BitEncodedInt();
                        id.ObjectLengths[i] = lengths;
                    }
                    if (r.ReadBoolean()) id.ObjectResolved[i] = r.ReadString();
                }
                int leaves = r.Read7BitEncodedInt();
                id.LeafKeys = new ulong[leaves];
                id.LeafTypes = new int[leaves];
                for (int i = 0; i < leaves; i++)
                {
                    id.LeafKeys[i] = r.ReadUInt64();
                    id.LeafTypes[i] = r.Read7BitEncodedInt();
                }
                id.Statics = new (int, int)[r.Read7BitEncodedInt()];
                for (int i = 0; i < id.Statics.Length; i++) id.Statics[i] = (r.Read7BitEncodedInt(), r.ReadInt32());
                return id;
            }
        }

        // the prepared copy's step-0 walk matched to this one's (KeyframeIdentity), for a KeyframeLoader
        public sealed class KeyframeMap
        {
            internal object?[] Objects = Array.Empty<object?>(); // per prepared object id: this copy's object (null: made fresh)
            internal Type[] ObjectTypes = Array.Empty<Type>();
            internal int[]?[] ObjectLengths = Array.Empty<int[]?>();
            internal object?[] Leaves = Array.Empty<object?>(); // per prepared leaf: this copy's (null: a keyframe needing it is refused)
            internal Type[] LeafTypes = Array.Empty<Type>();
            internal FieldInfo[] Statics = Array.Empty<FieldInfo>(); // per prepared static: its field here
            internal bool[] SetStatic = Array.Empty<bool>(); // ...and whether a load sets it (this copy walks it too)
            public int PreparedObjects => Objects.Length;
            public int PreparedLeaves => Leaves.Length;
            public int Matched { get; private set; }
            public int Made { get; private set; }
            public int ViewerOnly { get; private set; }
            public int MissingLeaves { get; private set; }
            public int PreparedOnlyStatics { get; private set; }
            public int ViewerOnlyStatics { get; private set; }
            public string Summary => $"objects {Matched} matched, {Made} made fresh, {ViewerOnly} only here; leaves {PreparedLeaves - MissingLeaves}"
                + $"/{PreparedLeaves} found; statics {PreparedOnlyStatics} only prepared, {ViewerOnlyStatics} only here";

            // null + why: the two walks can't be matched at all (another build's types)
            public static (KeyframeMap? Map, string? Refusal) Build(KeyframeIdentity prepared, KeyframeBase mine)
            {
                KeyframeIdentity own = KeyframeIdentity.Of(mine);
                var map = new KeyframeMap();
                var types = new Type[prepared.TypeNames.Length];
                for (int i = 0; i < types.Length; i++)
                {
                    Type? t = Type.GetType(prepared.TypeNames[i], throwOnError: false);
                    if (t == null) return (null, "unknown type " + prepared.TypeNames[i]);
                    types[i] = t;
                }
                // (a name two things share on one side matches neither)
                static Dictionary<ulong, int> Unique(ulong[] keys)
                {
                    var d = new Dictionary<ulong, int>(keys.Length);
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (!d.TryAdd(keys[i], i)) d[keys[i]] = -1;
                    }
                    return d;
                }
                Dictionary<ulong, int> ownObjects = Unique(own.ObjectKeys), theirObjects = Unique(prepared.ObjectKeys);
                int n = prepared.ObjectCount;
                map.Objects = new object?[n];
                map.ObjectTypes = new Type[n];
                map.ObjectLengths = prepared.ObjectLengths;
                var used = new HashSet<object>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < n; i++)
                {
                    Type t = types[prepared.ObjectTypes[i]];
                    map.ObjectTypes[i] = t;
                    object? o = null;
                    if (prepared.ObjectResolved[i] is string key && KeyframeResolvers.TryGetValue(t, out var r))
                    {
                        o = r.Resolve(key);
                        if (o != null && o.GetType() != t) o = null;
                    }
                    if (o == null && theirObjects[prepared.ObjectKeys[i]] == i && ownObjects.TryGetValue(prepared.ObjectKeys[i], out int mineId)
                        && mineId >= 0 && mine.Obj(mineId).GetType() == t)
                    {
                        o = mine.Obj(mineId);
                    }
                    if (o != null && !used.Add(o)) o = null;
                    map.Objects[i] = o;
                    if (o != null) map.Matched++;
                    else map.Made++;
                }
                map.ViewerOnly = mine.ObjectCount - mine.Snap._objects.Count(o => used.Contains(o));
                Dictionary<ulong, int> ownLeaves = Unique(own.LeafKeys), theirLeaves = Unique(prepared.LeafKeys);
                map.Leaves = new object?[prepared.LeafCount];
                map.LeafTypes = new Type[prepared.LeafCount];
                for (int i = 0; i < prepared.LeafCount; i++)
                {
                    Type t = types[prepared.LeafTypes[i]];
                    map.LeafTypes[i] = t;
                    if (theirLeaves[prepared.LeafKeys[i]] == i && ownLeaves.TryGetValue(prepared.LeafKeys[i], out int l) && l >= 0
                        && mine.Leaves[l].GetType() == t)
                    {
                        map.Leaves[i] = mine.Leaves[l];
                    }
                    else
                    {
                        map.MissingLeaves++;
                    }
                }
                var modules = new Module?[prepared.AssemblyNames.Length];
                for (int a = 0; a < modules.Length; a++)
                {
                    modules[a] = _gameAssemblies.FirstOrDefault(x => x.GetName().Name == prepared.AssemblyNames[a])?.ManifestModule;
                    if (modules[a] == null) return (null, "unknown assembly " + prepared.AssemblyNames[a]);
                }
                var walked = new HashSet<FieldInfo>(_staticFields);
                map.Statics = new FieldInfo[prepared.Statics.Length];
                map.SetStatic = new bool[prepared.Statics.Length];
                int found = 0;
                for (int i = 0; i < prepared.Statics.Length; i++)
                {
                    (int a, int token) = prepared.Statics[i];
                    FieldInfo? f;
                    try
                    {
                        f = modules[a]!.ResolveField(token);
                    }
                    catch (ArgumentException)
                    {
                        f = null;
                    }
                    if (f == null || !f.IsStatic) return (null, $"a static field this build doesn't have ({prepared.AssemblyNames[a]} {token:X8})");
                    map.Statics[i] = f;
                    map.SetStatic[i] = walked.Contains(f) && !f.IsInitOnly;
                    if (walked.Contains(f)) found++;
                    else map.PreparedOnlyStatics++;
                }
                map.ViewerOnlyStatics = _staticFields.Count - found;
                return (map, null);
            }
        }
    }
}
