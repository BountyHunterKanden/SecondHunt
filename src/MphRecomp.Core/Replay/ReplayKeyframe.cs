using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MphRecomp.Replay
{
    // Portable keyframes (thread B, owner 2026-10-23: "instant seek via portable keyframes + a shadow process"): a checkpoint
    // (SimSnapshot) as bytes that ANOTHER copy of the same match can load -- a second process that started the same replay.
    //
    // Both copies number their objects the same way: the walk of the match as it starts (step 0, before any step) is
    // deterministic, so object i of one copy's step-0 checkpoint is object i of the other's (KeyframeBase; a signature of the
    // walk's types is compared and a different walk is refused). Objects made after step 0 get ids from the writer and are
    // allocated fresh by the loader. What isn't walked (a leaf: a loaded file's data, a delegate, a comparer) is found in the
    // step-0 walk's leaves too, by position; a new delegate is written as its method + target; anything else new is refused.
    // Hashed collections (Dictionary, HashSet) keep their entries exactly (order and free list included) and get their hash
    // codes and buckets recomputed (an object's default hash, a string's, differ per process).
    //
    // Exact or refused, as all playback: the loaded match goes on through ReplayPlayer.Check every step.
    public sealed partial class SimSnapshot
    {
        // fields whose array's contents a keyframe leaves out (draw-only, repainted by their owner): only the length goes,
        // a loaded copy keeps what it had (or zeros)
        public static readonly HashSet<FieldInfo> KeyframeSkipsContents = new();

        // objects made after step 0 that the loading copy gets for itself by a name (a model loaded mid-match: from its own
        // file cache) instead of allocating: Key(o) = its name (null: allocate as usual), Resolve(name) = this copy's own.
        // Their sub-objects are found along the same fields, and the file data they hold (not walked) is kept as loaded.
        public static readonly Dictionary<Type, (Func<object, string?> Key, Func<string, object?> Resolve)> KeyframeResolvers = new();

        // fixed objects not walked and not in a static the walk reads (a readonly metadata table's entries): leaves too
        public static readonly List<Func<IEnumerable<object>>> KeyframeLeafPools = new();

        static readonly object Keep = new();

        const uint KeyframeMagic = 0x4B48504D; // "MPHK"
        const int KeyframeVersion = 1;

        static readonly Dictionary<Type, int> _sizes = new();

        static int Size(Type t)
        {
            if (!_sizes.TryGetValue(t, out int s)) _sizes[t] = s = SizeOfType(t);
            return s;
        }

        // no references at all, strings included (the snapshot's HoldsRefs leaves strings out: they're shared, not walked)
        static readonly Dictionary<Type, bool> _plain = new();

        static bool Plain(Type t)
        {
            if (!_plain.TryGetValue(t, out bool p))
            {
                _plain[t] = p = t.IsValueType && !(bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
                    .MakeGenericMethod(t).Invoke(null, null)!;
            }
            return p;
        }

        static ReadOnlySpan<byte> BoxBytes(object box, int size) =>
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<RawBox>(box).Data, size);

        static Span<byte> BoxSpan(object box, int size) => MemoryMarshal.CreateSpan(ref Unsafe.As<RawBox>(box).Data, size);

        static Span<byte> ArraySpan(Array a, int elementSize) =>
            MemoryMarshal.CreateSpan(ref MemoryMarshal.GetArrayDataReference(a), a.Length * elementSize);

        // every reference a value holds (a struct's fields, recursively)
        static void ForEachRef(Type declared, object? v, Action<object> f)
        {
            if (v == null) return;
            if (declared.IsValueType)
            {
                Type? u = Nullable.GetUnderlyingType(declared);
                if (u != null)
                {
                    ForEachRef(u, v, f);
                    return;
                }
                TypeInfo info = Info(declared);
                if (!info.HoldsRefs) return;
                var values = new object?[info.Fields.Length];
                info.Get(v, values);
                for (int i = 0; i < values.Length; i++)
                {
                    if (info.RefField[i]) ForEachRef(info.Fields[i].FieldType, values[i], f);
                }
                return;
            }
            f(v);
        }

        static readonly Dictionary<Type, int[]> _skipFields = new();

        static int[] SkipFields(Type t)
        {
            if (_skipFields.TryGetValue(t, out int[]? s)) return s;
            TypeInfo info = Info(t);
            s = Enumerable.Range(0, info.Fields.Length).Where(i => KeyframeSkipsContents.Contains(info.Fields[i])).ToArray();
            _skipFields[t] = s;
            return s;
        }

        // ---- the step-0 numbering both copies share ----

        // a static list's spare room is whatever earlier matches in this process grew it to (its array is walked, so
        // two copies of a match would differ): each one trimmed to just its items, same list, same items, same order
        public static void TrimStaticLists()
        {
            foreach (FieldInfo f in _staticFields)
            {
                if (f.GetValue(null) is not System.Collections.IList list) continue;
                Type t = list.GetType();
                if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(List<>)) continue;
                PropertyInfo capacity = t.GetProperty(nameof(List<int>.Capacity))!;
                if ((int)capacity.GetValue(list)! != list.Count) capacity.SetValue(list, list.Count);
            }
        }

        public sealed class KeyframeBase
        {
            public readonly SimSnapshot Snap;
            internal readonly Dictionary<object, int> Index;
            internal readonly List<object> Leaves = new();
            internal readonly Dictionary<object, int> LeafIndex = new(ReferenceEqualityComparer.Instance);
            internal readonly List<object> Hashed = new(); // the step-0 Dictionaries / HashSets (rehashed when loaded)
            public ulong Signature { get; }
            public int ObjectCount => Snap._objects.Length;
            public int LeafCount => Leaves.Count;
            public int Step => Snap.Step;

            public KeyframeBase(SimSnapshot step0)
            {
                Snap = step0;
                Index = new Dictionary<object, int>(step0._objects.Length, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < step0._objects.Length; i++) Index[step0._objects[i]] = i;
                ulong h = 14695981039346656037UL;
                void Mix(string s)
                {
                    foreach (char c in s)
                    {
                        h ^= c;
                        h *= 1099511628211UL;
                    }
                    h ^= 0xFF;
                    h *= 1099511628211UL;
                }
                void Leaf(object o)
                {
                    if (o is string || o.GetType().IsValueType || Index.ContainsKey(o)) return;
                    if (LeafIndex.TryAdd(o, Leaves.Count)) Leaves.Add(o);
                }
                foreach ((FieldInfo f, object? v) in step0._statics) ForEachRef(f.FieldType, v, Leaf);
                for (int i = 0; i < step0._objects.Length; i++)
                {
                    object o = step0._objects[i];
                    Type t = o.GetType();
                    Mix(t.FullName ?? t.Name);
                    if (HashInfo.For(t) != null) Hashed.Add(o);
                    TypeInfo info = Info(t);
                    if (step0._arrays[i] is Array a)
                    {
                        Mix(a.Length.ToString()); // (a pool or list grown by an earlier match in this process: not the same walk)
                        if (info.ElementSize > 0) continue;
                        Type e = t.GetElementType()!;
                        foreach (object? x in a) ForEachRef(e, x, Leaf);
                        continue;
                    }
                    object?[] values = step0._fields[i]!;
                    for (int j = 0; j < values.Length; j++)
                    {
                        if (info.RefField[j]) ForEachRef(info.Fields[j].FieldType, values[j], Leaf);
                    }
                }
                foreach (Func<IEnumerable<object>> pool in KeyframeLeafPools)
                {
                    foreach (object o in pool()) Leaf(o);
                }
                foreach (object leaf in Leaves) Mix(leaf.GetType().FullName ?? "?");
                Mix(step0._statics.Length.ToString());
                Signature = h;
            }

            internal object Obj(int id) => Snap._objects[id];

            // (diagnostics) the walk as text: object types in id order, then leaf types, then the statics -- diff two copies'
            public IEnumerable<string> Describe()
            {
                // (who first refers to each object: a static, or an object id + field)
                var from = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
                foreach ((FieldInfo f, object? v) in Snap._statics)
                {
                    ForEachRef(f.FieldType, v, x => from.TryAdd(x, $"s:{f.DeclaringType?.Name}.{f.Name}"));
                }
                for (int i = 0; i < Snap._objects.Length; i++)
                {
                    TypeInfo info = Info(Snap._objects[i].GetType());
                    if (Snap._arrays[i] is Array arr)
                    {
                        if (info.ElementSize > 0) continue;
                        foreach (object? x in arr) ForEachRef(arr.GetType().GetElementType()!, x, y => from.TryAdd(y, $"o{i}[]"));
                        continue;
                    }
                    object?[] values = Snap._fields[i]!;
                    for (int j = 0; j < values.Length; j++)
                    {
                        if (info.RefField[j]) ForEachRef(info.Fields[j].FieldType, values[j], y => from.TryAdd(y, $"o{i}.{info.Fields[j].Name}"));
                    }
                }
                for (int i = 0; i < Snap._objects.Length; i++)
                {
                    object o = Snap._objects[i];
                    yield return $"o{i} {o.GetType().FullName}" + (Snap._arrays[i] is Array a ? $" [{a.Length}]" : "")
                        + (from.TryGetValue(o, out string? w) ? " <- " + w : "");
                }
                for (int i = 0; i < Leaves.Count; i++)
                {
                    yield return $"l{i} {Leaves[i].GetType().FullName}" + (from.TryGetValue(Leaves[i], out string? w) ? " <- " + w : "");
                }
                foreach ((FieldInfo f, object? _) in Snap._statics) yield return $"s {f.DeclaringType?.FullName}.{f.Name}";
            }
        }

        // ---- Dictionary / HashSet: entries kept, hashes + buckets recomputed ----

        sealed class HashInfo
        {
            public FieldInfo Buckets = null!, Entries = null!, Count = null!, Comparer = null!;
            public FieldInfo EntryHash = null!, EntryNext = null!, EntryKey = null!;
            public bool IntHash; // HashSet's entries hold an int, Dictionary's a uint
            public MethodInfo? ComparerHash;

            static readonly Dictionary<Type, HashInfo?> _all = new();

            public static HashInfo? For(Type t)
            {
                if (_all.TryGetValue(t, out HashInfo? h)) return h;
                h = null;
                if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                    || t.GetGenericTypeDefinition() == typeof(HashSet<>)))
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    bool set = t.GetGenericTypeDefinition() == typeof(HashSet<>);
                    FieldInfo entries = t.GetField("_entries", flags)!;
                    Type entry = entries.FieldType.GetElementType()!;
                    h = new HashInfo
                    {
                        Buckets = t.GetField("_buckets", flags)!,
                        Entries = entries,
                        Count = t.GetField("_count", flags)!,
                        Comparer = t.GetField("_comparer", flags)!,
                        EntryHash = entry.GetField(set ? "HashCode" : "hashCode", BindingFlags.Instance | BindingFlags.Public
                            | BindingFlags.NonPublic)!,
                        EntryNext = entry.GetField(set ? "Next" : "next", BindingFlags.Instance | BindingFlags.Public
                            | BindingFlags.NonPublic)!,
                        EntryKey = entry.GetField(set ? "Value" : "key", BindingFlags.Instance | BindingFlags.Public
                            | BindingFlags.NonPublic)!,
                        IntHash = set
                    };
                    Type key = t.GetGenericArguments()[0];
                    h.ComparerHash = typeof(IEqualityComparer<>).MakeGenericType(key).GetMethod("GetHashCode");
                }
                _all[t] = h;
                return h;
            }

            public void Rehash(object collection)
            {
                if (Buckets.GetValue(collection) is not int[] buckets || Entries.GetValue(collection) is not Array entries) return;
                int count = (int)Count.GetValue(collection)!;
                object? comparer = Comparer.GetValue(collection);
                Array.Clear(buckets);
                for (int i = 0; i < count; i++)
                {
                    object e = entries.GetValue(i)!;
                    if ((int)EntryNext.GetValue(e)! < -1) continue; // a free entry (its next is the free list's)
                    object? key = EntryKey.GetValue(e);
                    int hash = key == null ? 0
                        : comparer == null ? key.GetHashCode()
                        : comparer is IEqualityComparer plain ? plain.GetHashCode(key)
                        : (int)ComparerHash!.Invoke(comparer, new[] { key })!;
                    int b = (int)((uint)hash % (uint)buckets.Length);
                    EntryHash.SetValue(e, IntHash ? hash : (object)(uint)hash);
                    EntryNext.SetValue(e, buckets[b] - 1);
                    buckets[b] = i + 1;
                    entries.SetValue(e, i);
                }
            }
        }

        // ---- writing ----

        // Writes keyframes of one match, numbered against its step-0 base. Ids of objects made after step 0 stay the same
        // across this writer's keyframes (a delta refers to objects an earlier keyframe made).
        public sealed class KeyframeWriter
        {
            readonly KeyframeBase _base;
            readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
            int _nextId;

            // per keyframe
            BinaryWriter _w = null!;
            readonly List<Type> _types = new();
            readonly Dictionary<Type, int> _typeIndex = new();
            readonly List<(int Id, object Obj, byte Kind, string? Key, int Parent, int Slot)> _declared = new();
            readonly HashSet<object> _resolved = new(ReferenceEqualityComparer.Instance); // (resolved or found along a path)
            SimSnapshot _snap = null!;
            bool _keepOk;
            readonly HashSet<object> _declaredSet = new(ReferenceEqualityComparer.Instance);
            readonly Queue<object> _extras = new();
            readonly HashSet<object> _skip = new(ReferenceEqualityComparer.Instance);
            Dictionary<object, int> _snapIndex = null!;
            Dictionary<object, int>? _prevIndex;

            // what a keyframe couldn't write (type names; empty = all written)
            public readonly SortedSet<string> Refused = new(StringComparer.Ordinal);
            // dev numbers of the last keyframe
            public int LastRecords { get; private set; }
            public int LastDeclared { get; private set; }
            public int LastExtras { get; private set; }
            public int LastNewDelegates { get; private set; }

            public KeyframeWriter(KeyframeBase b)
            {
                _base = b;
                _nextId = b.ObjectCount;
            }

            // a whole keyframe (deltaFrom null), or only what changed since `deltaFrom` (a checkpoint taken with it as its
            // previous, whose keyframe the loader has loaded just before)
            public byte[] Write(SimSnapshot s, SimSnapshot? deltaFrom = null)
            {
                _types.Clear();
                _typeIndex.Clear();
                _declared.Clear();
                _declaredSet.Clear();
                _extras.Clear();
                _skip.Clear();
                LastExtras = LastNewDelegates = 0;
                _snap = s;
                _snapIndex = new Dictionary<object, int>(s._objects.Length, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < s._objects.Length; i++) _snapIndex[s._objects[i]] = i;
                _prevIndex = null;
                if (deltaFrom != null)
                {
                    _prevIndex = new Dictionary<object, int>(deltaFrom._objects.Length, ReferenceEqualityComparer.Instance);
                    for (int i = 0; i < deltaFrom._objects.Length; i++) _prevIndex[deltaFrom._objects[i]] = i;
                }
                for (int i = 0; i < s._objects.Length; i++)
                {
                    if (s._fields[i] is not object?[] values) continue;
                    foreach (int j in SkipFields(s._objects[i].GetType()))
                    {
                        if (values[j] is Array a) _skip.Add(a);
                    }
                }
                // resolved objects first (their sub-objects must be found along their paths, not allocated)
                for (int i = 0; i < s._objects.Length; i++)
                {
                    object o = s._objects[i];
                    if (!_base.Index.ContainsKey(o) && KeyframeResolvers.ContainsKey(o.GetType())) Id(o);
                }
                var body = new MemoryStream(1 << 20);
                _w = new BinaryWriter(body);
                _keepOk = false;
                _w.Write7BitEncodedInt(s._statics.Length);
                foreach ((FieldInfo f, object? v) in s._statics) WriteValue(f.FieldType, v);
                int records = 0;
                for (int i = 0; i < s._objects.Length; i++)
                {
                    object o = s._objects[i];
                    if (_prevIndex != null && _prevIndex.TryGetValue(o, out int p)
                        && (s._arrays[i] != null ? ReferenceEquals(s._arrays[i], deltaFrom!._arrays[p])
                            : ReferenceEquals(s._fields[i], deltaFrom!._fields[p])))
                    {
                        continue; // unchanged since the previous keyframe
                    }
                    _w.Write7BitEncodedInt(Id(o));
                    WriteContents(o, s._fields[i], s._arrays[i]);
                    records++;
                }
                _keepOk = false;
                while (_extras.Count > 0)
                {
                    object o = _extras.Dequeue();
                    _w.Write7BitEncodedInt(Id(o));
                    TypeInfo info = Info(o.GetType());
                    object?[]? values = null;
                    if (o is not Array)
                    {
                        values = new object?[info.Fields.Length];
                        info.Get(o, values);
                    }
                    WriteContents(o, values, o as Array);
                    _keepOk = false;
                    records++;
                    LastExtras++;
                }
                _w.Write7BitEncodedInt(-1);
                _w.Flush();
                LastRecords = records;
                LastDeclared = _declared.Count;

                var all = new MemoryStream((int)body.Length + 4096);
                var h = new BinaryWriter(all);
                h.Write(KeyframeMagic);
                h.Write(KeyframeVersion);
                h.Write(s.Step);
                h.Write(deltaFrom != null ? deltaFrom.Step : -1);
                h.Write(_base.ObjectCount);
                h.Write(_base.LeafCount);
                h.Write(_base.Signature);
                h.Write7BitEncodedInt(_types.Count);
                foreach (Type t in _types) h.Write(t.AssemblyQualifiedName!);
                h.Write7BitEncodedInt(_declared.Count);
                foreach ((int id, object o, byte kind, string? key, int parent, int slot) in _declared)
                {
                    h.Write7BitEncodedInt(id);
                    h.Write(kind);
                    h.Write7BitEncodedInt(TypeRef(o.GetType(), header: true));
                    if (kind == 1)
                    {
                        h.Write(key!);
                    }
                    else if (kind == 2)
                    {
                        h.Write7BitEncodedInt(parent);
                        h.Write7BitEncodedInt(slot);
                    }
                    else if (o is Array a)
                    {
                        h.Write7BitEncodedInt(a.Rank);
                        for (int r = 0; r < a.Rank; r++) h.Write7BitEncodedInt(a.GetLength(r));
                    }
                    else
                    {
                        h.Write7BitEncodedInt(0);
                    }
                }
                h.Flush();
                body.Position = 0;
                body.CopyTo(all);
                return all.ToArray();
            }

            int TypeRef(Type t, bool header = false)
            {
                if (!_typeIndex.TryGetValue(t, out int i))
                {
                    if (header) throw new InvalidOperationException("type first seen in the header: " + t);
                    _typeIndex[t] = i = _types.Count;
                    _types.Add(t);
                }
                return i;
            }

            int Id(object o)
            {
                if (_base.Index.TryGetValue(o, out int id)) return id;
                if (!_ids.TryGetValue(o, out id)) _ids[o] = id = _nextId++;
                // made after the previous keyframe (or any, for a whole one): the loader allocates it
                if ((_prevIndex == null || !_prevIndex.ContainsKey(o) || !_snapIndex.ContainsKey(o)) && _declaredSet.Add(o))
                {
                    TypeRef(o.GetType());
                    if (o is Array) TypeRef(o.GetType().GetElementType()!);
                    if (_snapIndex.ContainsKey(o) && KeyframeResolvers.TryGetValue(o.GetType(), out var r) && r.Key(o) is string key)
                    {
                        _declared.Add((id, o, 1, key, 0, 0));
                        _resolved.Add(o);
                        DeclarePaths(o, id);
                        return id;
                    }
                    _declared.Add((id, o, 0, null, 0, 0));
                    if (!_snapIndex.ContainsKey(o)) _extras.Enqueue(o); // reached only through a delegate: written too
                }
                return id;
            }

            // a resolved object's sub-objects (walked, made after step 0): each found along the field / element it's first
            // reached by, in the loading copy's own resolved object
            void DeclarePaths(object root, int rootId)
            {
                var queue = new Queue<(object, int)>();
                queue.Enqueue((root, rootId));
                while (queue.Count > 0)
                {
                    (object o, int oid) = queue.Dequeue();
                    int i = _snapIndex[o];
                    void Child(object? c, int slot)
                    {
                        if (c == null || c is string || c.GetType().IsValueType || _base.Index.ContainsKey(c) || !_snapIndex.ContainsKey(c)
                            || !_declaredSet.Add(c))
                        {
                            return;
                        }
                        if (!_ids.TryGetValue(c, out int cid)) _ids[c] = cid = _nextId++;
                        TypeRef(c.GetType());
                        _declared.Add((cid, c, 2, null, oid, slot));
                        _resolved.Add(c);
                        queue.Enqueue((c, cid));
                    }
                    if (_snap._arrays[i] is Array a)
                    {
                        if (a.Rank == 1 && !a.GetType().GetElementType()!.IsValueType)
                        {
                            object?[] refs = Unsafe.As<object?[]>(a);
                            for (int e = 0; e < refs.Length; e++) Child(refs[e], e);
                        }
                        continue;
                    }
                    TypeInfo info = Info(o.GetType());
                    object?[] values = _snap._fields[i]!;
                    for (int j = 0; j < values.Length; j++)
                    {
                        if (!info.Fields[j].FieldType.IsValueType) Child(values[j], j);
                    }
                }
            }

            string _where = "";

            void WriteContents(object o, object?[]? values, Array? array)
            {
                Type t = o.GetType();
                _where = t.Name;
                TypeInfo info = Info(t);
                _keepOk = _resolved.Contains(o);
                if (array != null)
                {
                    if (_skip.Contains(o))
                    {
                        _w.Write((byte)1);
                        return;
                    }
                    _w.Write((byte)0);
                    Type e = t.GetElementType()!;
                    if (t.GetArrayRank() != 1)
                    {
                        if (Plain(e))
                        {
                            _w.Write(ArraySpan(array, Size(e)));
                            return;
                        }
                        foreach (object? x in array) WriteValue(e, x); // (row by row: the order a flat index counts in)
                        return;
                    }
                    if (Plain(e))
                    {
                        _w.Write(ArraySpan(array, Size(e)));
                        return;
                    }
                    if (!e.IsValueType)
                    {
                        object?[] refs = Unsafe.As<object?[]>(array);
                        for (int i = 0; i < refs.Length; i++) WriteRef(refs[i]);
                        return;
                    }
                    for (int i = 0; i < array.Length; i++) WriteValue(e, array.GetValue(i));
                    return;
                }
                for (int j = 0; j < info.Fields.Length; j++)
                {
                    _where = t.Name + "." + info.Fields[j].Name;
                    WriteValue(info.Fields[j].FieldType, values![j]);
                }
            }

            void WriteValue(Type declared, object? v)
            {
                if (declared.IsValueType)
                {
                    Type? u = Nullable.GetUnderlyingType(declared);
                    if (u != null)
                    {
                        _w.Write(v != null);
                        if (v != null) WriteValue(u, v);
                        return;
                    }
                    TypeInfo info = Info(declared);
                    if (Plain(declared))
                    {
                        _w.Write(BoxBytes(v!, Size(declared)));
                        return;
                    }
                    var values = new object?[info.Fields.Length];
                    info.Get(v!, values);
                    for (int j = 0; j < values.Length; j++) WriteValue(info.Fields[j].FieldType, values[j]);
                    return;
                }
                if (declared.IsPointer)
                {
                    Refused.Add("pointer field " + declared);
                    return;
                }
                WriteRef(v);
            }

            void WriteRef(object? o)
            {
                if (o == null)
                {
                    _w.Write((byte)0);
                    return;
                }
                if (o is string s)
                {
                    _w.Write((byte)3);
                    _w.Write(s);
                    return;
                }
                if (_base.Index.TryGetValue(o, out int baseId))
                {
                    _w.Write((byte)1);
                    _w.Write7BitEncodedInt(baseId);
                    return;
                }
                if (_base.LeafIndex.TryGetValue(o, out int leaf))
                {
                    _w.Write((byte)2);
                    _w.Write7BitEncodedInt(leaf);
                    return;
                }
                Type t = o.GetType();
                if (t.IsValueType)
                {
                    _w.Write((byte)5);
                    _w.Write7BitEncodedInt(TypeRef(t));
                    WriteValue(t, o);
                    return;
                }
                if (o is Delegate d)
                {
                    LastNewDelegates++;
                    _w.Write((byte)4);
                    _w.Write7BitEncodedInt(TypeRef(t));
                    Delegate[] list = d.GetInvocationList();
                    _w.Write7BitEncodedInt(list.Length);
                    foreach (Delegate x in list)
                    {
                        MethodInfo m = x.Method;
                        _w.Write7BitEncodedInt(TypeRef(m.DeclaringType!));
                        _w.Write(m.MetadataToken);
                        Type[] args = m.IsGenericMethod ? m.GetGenericArguments() : Type.EmptyTypes;
                        _w.Write7BitEncodedInt(args.Length);
                        foreach (Type a in args) _w.Write7BitEncodedInt(TypeRef(a));
                        WriteRef(x.Target);
                    }
                    return;
                }
                if (o is Type type)
                {
                    _w.Write((byte)6);
                    _w.Write7BitEncodedInt(TypeRef(type));
                    return;
                }
                if (_snapIndex.ContainsKey(o) || Info(t).Walk || t == typeof(object))
                {
                    _w.Write((byte)1);
                    _w.Write7BitEncodedInt(Id(o));
                    return;
                }
                if (_keepOk)
                {
                    _w.Write((byte)7); // a resolved object's file data: the loading copy's own
                    return;
                }
                Refused.Add((t.FullName ?? t.Name) + " in " + _where);
                _w.Write((byte)0);
            }
        }

        // ---- loading ----

        // Loads keyframes written by another copy of the same match into this one (its step-0 base taken the same way).
        public sealed class KeyframeLoader
        {
            readonly KeyframeBase _base;
            readonly Dictionary<int, object> _made = new();
            readonly HashSet<object> _loaded = new(ReferenceEqualityComparer.Instance);
            BinaryReader _r = null!;
            Type[] _types = Array.Empty<Type>();
            static readonly Dictionary<(Type, int), MethodInfo> _methods = new();

            public KeyframeLoader(KeyframeBase b)
            {
                _base = b;
            }

            public int Step { get; private set; }
            // the arrays left out (KeyframeSkipsContents) that this load allocated: zeros, their owner repaints them
            public int LastSkipped { get; private set; }

            // null = loaded; else why not (nothing was changed when the header is refused)
            public string? Load(byte[] data)
            {
                _r = new BinaryReader(new MemoryStream(data, writable: false));
                if (_r.ReadUInt32() != KeyframeMagic || _r.ReadInt32() != KeyframeVersion) return "not a keyframe of this version";
                int step = _r.ReadInt32();
                int deltaFrom = _r.ReadInt32();
                if (_r.ReadInt32() != _base.ObjectCount || _r.ReadInt32() != _base.LeafCount || _r.ReadUInt64() != _base.Signature)
                {
                    return "the match started differently here (step-0 walk differs)";
                }
                if (deltaFrom >= 0 && deltaFrom != Step) return $"a delta from step {deltaFrom}; step {Step} is loaded";
                _types = new Type[_r.Read7BitEncodedInt()];
                for (int i = 0; i < _types.Length; i++)
                {
                    string name = _r.ReadString();
                    Type? t = Type.GetType(name, throwOnError: false);
                    if (t == null) return "unknown type " + name;
                    _types[i] = t;
                }
                if (deltaFrom < 0) _made.Clear();
                _loaded.Clear();
                int declared = _r.Read7BitEncodedInt();
                var found = new HashSet<object>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < declared; i++)
                {
                    int id = _r.Read7BitEncodedInt();
                    byte kind = _r.ReadByte();
                    Type t = _types[_r.Read7BitEncodedInt()];
                    object? o;
                    if (kind == 1)
                    {
                        string key = _r.ReadString();
                        o = KeyframeResolvers.TryGetValue(t, out var r) ? r.Resolve(key) : null;
                        if (o == null || o.GetType() != t) return $"{t.Name} '{key}' isn't here";
                    }
                    else if (kind == 2)
                    {
                        object parent = Obj(_r.Read7BitEncodedInt());
                        int slot = _r.Read7BitEncodedInt();
                        o = parent is Array a ? (slot < a.Length ? a.GetValue(slot) : null)
                            : slot < Info(parent.GetType()).Fields.Length ? Info(parent.GetType()).Fields[slot].GetValue(parent) : null;
                        if (o == null || o.GetType() != t) return $"a {t.Name} of a resolved {parent.GetType().Name} isn't here";
                    }
                    else
                    {
                        int rank = _r.Read7BitEncodedInt();
                        if (rank == 0)
                        {
                            o = RuntimeHelpers.GetUninitializedObject(t);
                        }
                        else
                        {
                            var lengths = new int[rank];
                            for (int k = 0; k < rank; k++) lengths[k] = _r.Read7BitEncodedInt();
                            o = Array.CreateInstance(t.GetElementType()!, lengths);
                        }
                    }
                    if (kind != 0 && !found.Add(o)) return $"two {t.Name}s resolve to one";
                    _made[id] = o;
                }
                int statics = _r.Read7BitEncodedInt();
                if (statics != _staticFields.Count) return "a different set of static fields";
                var staticValues = new object?[statics];
                for (int i = 0; i < statics; i++) staticValues[i] = ReadValue(_staticFields[i].FieldType);
                for (int i = 0; i < statics; i++)
                {
                    if (!_staticFields[i].IsInitOnly) _staticFields[i].SetValue(null, staticValues[i]);
                }
                LastSkipped = 0;
                while (true)
                {
                    int id = _r.Read7BitEncodedInt();
                    if (id < 0) break;
                    object o = Obj(id);
                    _loaded.Add(o);
                    ReadContents(o);
                }
                // hashes per process: every loaded Dictionary / HashSet (or one whose arrays were loaded) rehashed
                foreach (object c in _base.Hashed.Concat(_made.Values))
                {
                    HashInfo? h = HashInfo.For(c.GetType());
                    if (h == null) continue;
                    if (_loaded.Contains(c) || h.Buckets.GetValue(c) is object b && _loaded.Contains(b)
                        || h.Entries.GetValue(c) is object e && _loaded.Contains(e))
                    {
                        h.Rehash(c);
                    }
                }
                Step = step;
                return null;
            }

            object Obj(int id) => id < _base.ObjectCount ? _base.Obj(id) : _made[id];

            void ReadContents(object o)
            {
                Type t = o.GetType();
                TypeInfo info = Info(t);
                if (o is Array array)
                {
                    if (_r.ReadByte() == 1)
                    {
                        if (id0(o)) LastSkipped++;
                        return;
                    }
                    Type e = t.GetElementType()!;
                    if (array.Rank != 1)
                    {
                        if (Plain(e))
                        {
                            _r.Read(ArraySpan(array, Size(e)));
                            return;
                        }
                        var index = new int[array.Rank];
                        for (int i = 0; i < array.Length; i++)
                        {
                            for (int k = array.Rank - 1, rest = i; k >= 0; k--)
                            {
                                index[k] = rest % array.GetLength(k);
                                rest /= array.GetLength(k);
                            }
                            object? v = ReadValue(e);
                            if (v != Keep) array.SetValue(v, index);
                        }
                        return;
                    }
                    if (Plain(e))
                    {
                        _r.Read(ArraySpan(array, Size(e)));
                        return;
                    }
                    if (!e.IsValueType)
                    {
                        object?[] refs = Unsafe.As<object?[]>(array);
                        for (int i = 0; i < refs.Length; i++)
                        {
                            object? v = ReadRef();
                            if (v != Keep) refs[i] = v;
                        }
                        return;
                    }
                    for (int i = 0; i < array.Length; i++) array.SetValue(ReadValue(e), i);
                    return;
                }
                var values = new object?[info.Fields.Length];
                object?[]? current = null;
                for (int j = 0; j < values.Length; j++)
                {
                    values[j] = ReadValue(info.Fields[j].FieldType);
                    if (values[j] == Keep)
                    {
                        if (current == null)
                        {
                            current = new object?[values.Length];
                            info.Get(o, current);
                        }
                        values[j] = current[j];
                    }
                    // (the emitted setter doesn't check: a wrong type here would corrupt memory, not throw)
                    if (values[j] is object v && !info.Fields[j].FieldType.IsInstanceOfType(v))
                    {
                        throw new InvalidDataException($"{v.GetType().Name} read for {t.Name}.{info.Fields[j].Name}");
                    }
                }
                info.Set(o, values);
            }

            bool id0(object o) => !_base.Index.ContainsKey(o);

            object? ReadValue(Type declared)
            {
                if (declared.IsValueType)
                {
                    Type? u = Nullable.GetUnderlyingType(declared);
                    if (u != null) return _r.ReadBoolean() ? ReadValue(u) : null;
                    TypeInfo info = Info(declared);
                    object box = RuntimeHelpers.GetUninitializedObject(declared);
                    if (Plain(declared))
                    {
                        _r.Read(BoxSpan(box, Size(declared)));
                        return box;
                    }
                    for (int j = 0; j < info.Fields.Length; j++)
                    {
                        object? v = ReadValue(info.Fields[j].FieldType);
                        if (v == Keep) throw new InvalidDataException("kept file data inside a struct");
                        info.Fields[j].SetValue(box, v);
                    }
                    return box;
                }
                return ReadRef();
            }

            object? ReadRef()
            {
                switch (_r.ReadByte())
                {
                case 0:
                    return null;
                case 1:
                    return Obj(_r.Read7BitEncodedInt());
                case 2:
                    return _base.Leaves[_r.Read7BitEncodedInt()];
                case 3:
                    return _r.ReadString();
                case 4:
                {
                    Type type = _types[_r.Read7BitEncodedInt()];
                    int n = _r.Read7BitEncodedInt();
                    Delegate? all = null;
                    for (int i = 0; i < n; i++)
                    {
                        Type declaring = _types[_r.Read7BitEncodedInt()];
                        int token = _r.ReadInt32();
                        var args = new Type[_r.Read7BitEncodedInt()];
                        for (int a = 0; a < args.Length; a++) args[a] = _types[_r.Read7BitEncodedInt()];
                        object? target = ReadRef();
                        MethodInfo m = Method(declaring, token);
                        if (args.Length > 0) m = m.MakeGenericMethod(args);
                        Delegate d = m.IsStatic && target == null ? Delegate.CreateDelegate(type, m)
                            : Delegate.CreateDelegate(type, target, m);
                        all = Delegate.Combine(all, d);
                    }
                    return all;
                }
                case 5:
                    return ReadValue(_types[_r.Read7BitEncodedInt()]);
                case 6:
                    return _types[_r.Read7BitEncodedInt()];
                case 7:
                    return Keep;
                default:
                    throw new InvalidDataException("bad reference tag");
                }
            }

            static MethodInfo Method(Type declaring, int token)
            {
                if (_methods.TryGetValue((declaring, token), out MethodInfo? m)) return m;
                m = declaring.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly).First(x => x.MetadataToken == token);
                _methods[(declaring, token)] = m;
                return m;
            }
        }
    }
}
