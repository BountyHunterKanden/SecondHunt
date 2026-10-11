using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MphRecomp.Replay
{
    // A replay's in-memory checkpoint of the whole match (T9, owner 2026-10-10: "making rewinding more instantaneous").
    // Seeking back used to restart the match and run every step again from the start; with these, it restores the nearest
    // checkpoint at or before the target and runs only the rest.
    //
    // How: every object the match can reach -- from its roots (the CampaignHost, so its Scene and everything in it) and
    // every static field of the game's own types -- is walked once, and each one's fields (or an array's contents) are
    // copied as they are. Restoring writes them all back INTO THE SAME OBJECTS, so every reference anyone holds (the app's
    // PlayerEntity, the renderer's lists, pooled entities) stays valid and simply sees the earlier state. Objects made after
    // the checkpoint aren't reachable any more once their referrers are put back.
    //
    // What's walked: the game's own types (MphRead, the Core sim), arrays, and the BCL's generic collections (List,
    // Dictionary, HashSet, Queue, Stack...). Anything else (strings, delegates, streams, sockets, tasks...) is a leaf: the
    // reference to it is put back, its insides aren't touched. ExcludedTypes are walked past entirely (left as they are now):
    // what only the renderer / audio own, and the game's loaded files that never change (decoded models' meshes, textures).
    //
    // Exact or refused, as all playback: a restored match goes on through ReplayPlayer.Check every step like any other, so
    // a checkpoint that missed something stops at the first differing step (the caller then falls back to the restart).
    public sealed class SimSnapshot
    {
        public int Step { get; }
        public int ObjectCount => _objects.Length;
        public long Bytes { get; }
        public double TakeMs { get; }

        readonly object[] _objects;
        readonly object?[]?[] _fields; // per object: its fields' values (structs boxed = copies); null for an array
        readonly Array?[] _arrays; // per object: a copy of an array's contents; null for a class
        readonly (FieldInfo Field, object? Value)[] _statics;

        SimSnapshot(int step, object[] objects, object?[]?[] fields, Array?[] arrays, (FieldInfo, object?)[] statics, long bytes,
            double ms)
        {
            (Step, _objects, _fields, _arrays, _statics, Bytes, TakeMs) = (step, objects, fields, arrays, statics, bytes, ms);
        }

        // ---- what's walked ----

        static readonly HashSet<Assembly> _gameAssemblies = new();
        static readonly List<FieldInfo> _staticFields = new();
        static readonly HashSet<string> _excludedNamespaces = new(StringComparer.Ordinal);
        static readonly HashSet<Type> _excludedTypes = new();
        static readonly HashSet<FieldInfo> _excludedFields = new();
        static readonly Dictionary<Type, TypeInfo> _types = new();
        static bool _setUp;

        // the game assemblies (whose types are walked and whose statics are roots), plus what's left alone
        // (excludedFields: single fields left alone -- a loaded file's data held by an object whose other fields are state)
        public static void SetUp(IEnumerable<Assembly> gameAssemblies, IEnumerable<string> excludedNamespaces,
            IEnumerable<Type> excludedTypes, IEnumerable<FieldInfo>? excludedFields = null)
        {
            _gameAssemblies.Clear();
            _staticFields.Clear();
            _types.Clear();
            _excludedNamespaces.Clear();
            _excludedTypes.Clear();
            _excludedFields.Clear();
            foreach (FieldInfo f in excludedFields ?? Array.Empty<FieldInfo>()) _excludedFields.Add(f);
            foreach (string ns in excludedNamespaces) _excludedNamespaces.Add(ns);
            foreach (Type t in excludedTypes) _excludedTypes.Add(t);
            foreach (Assembly a in gameAssemblies)
            {
                _gameAssemblies.Add(a);
                Type[] types;
                try
                {
                    types = a.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }
                foreach (Type t in types)
                {
                    if (t.ContainsGenericParameters || Excluded(t)) continue;
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly))
                    {
                        if (f.IsLiteral || f.GetCustomAttribute<ThreadStaticAttribute>() != null || _excludedFields.Contains(f)) continue;
                        if (f.IsInitOnly && !Walked(f.FieldType) && !f.FieldType.IsValueType) continue; // a fixed leaf
                        _staticFields.Add(f);
                    }
                }
            }
            _setUp = true;
        }

        static bool Excluded(Type t)
        {
            // an array or a collection of something left alone is left alone too (a model's List<TextureData>...)
            if (t.IsArray) return Excluded(t.GetElementType()!);
            if (t.IsGenericType && !_gameAssemblies.Contains(t.Assembly) && t.GetGenericArguments().Any(Excluded)) return true;
            for (Type? x = t; x != null; x = x.DeclaringType)
            {
                if (_excludedTypes.Contains(x)) return true;
            }
            return t.Namespace != null && _excludedNamespaces.Contains(t.Namespace);
        }

        // a type whose insides are copied (else a leaf: only the reference to it is)
        static bool Walked(Type t)
        {
            if (t.IsArray) return !Excluded(t);
            if (t == typeof(string) || t.IsPointer || typeof(Delegate).IsAssignableFrom(t)) return false;
            if (_gameAssemblies.Contains(t.Assembly)) return !Excluded(t);
            if (t.IsGenericType && t.Namespace == "System.Collections.Generic") return !Excluded(t);
            // (the BCL's own helper types inside those collections: Dictionary's entries, List's enumerators...)
            return t.IsValueType && t.Namespace == "System.Collections.Generic";
        }

        sealed class TypeInfo
        {
            public FieldInfo[] Fields = Array.Empty<FieldInfo>();
            public bool[] RefField = Array.Empty<bool>(); // the field may hold a reference to walk (a class, or a struct with one)
            public bool HoldsRefs; // (a value type) some field may hold a reference
            public bool Walk;
            public int ElementSize; // an array of plain values: bytes per element (0: walk / clone its elements)
            public bool ElementRefs;
            // all fields at once (one compiled call per object, not one reflection call per field)
            public Action<object, object?[]>? GetAll;
            public Action<object, object?[]>? SetAll; // (classes only: a struct is put back whole by its owner)

            public void Get(object o, object?[] values)
            {
                if (GetAll != null)
                {
                    GetAll(o, values);
                    return;
                }
                for (int i = 0; i < Fields.Length; i++) values[i] = Fields[i].GetValue(o);
            }

            public void Set(object o, object?[] values)
            {
                if (SetAll != null)
                {
                    SetAll(o, values);
                    return;
                }
                for (int i = 0; i < Fields.Length; i++) Fields[i].SetValue(o, values[i]);
            }
        }

        // IL for the field copies; null (reflection then) for what IL can't do simply or where emitting isn't available
        static Action<object, object?[]>? Emit(Type t, FieldInfo[] fields, bool set)
        {
            if (fields.Length == 0 || fields.Any(f => f.FieldType.IsPointer || f.FieldType.IsByRefLike
                || f.FieldType.IsFunctionPointer))
            {
                return null;
            }
            try
            {
                var dm = new DynamicMethod((set ? "SnapSet_" : "SnapGet_") + t.Name, null, new[] { typeof(object), typeof(object?[]) },
                    typeof(SimSnapshot).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (set)
                    {
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, t);
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldc_I4, i);
                        il.Emit(OpCodes.Ldelem_Ref);
                        il.Emit(OpCodes.Unbox_Any, f.FieldType);
                        il.Emit(OpCodes.Stfld, f);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldc_I4, i);
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(t.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, t);
                        il.Emit(OpCodes.Ldfld, f);
                        if (f.FieldType.IsValueType) il.Emit(OpCodes.Box, f.FieldType);
                        il.Emit(OpCodes.Stelem_Ref);
                    }
                }
                il.Emit(OpCodes.Ret);
                var action = (Action<object, object?[]>)dm.CreateDelegate(typeof(Action<object, object?[]>));
                return action;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // (dev/test: false = reflection only, to check the IL copies against it)
        public static bool UseEmit { get; set; } = true;

        static TypeInfo Info(Type t)
        {
            if (_types.TryGetValue(t, out TypeInfo? info)) return info;
            info = new TypeInfo();
            _types[t] = info; // (before the fields: a struct can't hold itself, but a class can)
            if (t.IsArray)
            {
                Type e = t.GetElementType()!;
                info.Walk = Walked(t);
                info.ElementRefs = !e.IsValueType || Info(e).HoldsRefs;
                if (!info.ElementRefs && t.GetArrayRank() == 1)
                {
                    info.ElementSize = (int)typeof(SimSnapshot).GetMethod(nameof(SizeOf), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(e).Invoke(null, null)!;
                }
                return info;
            }
            if (t.IsPrimitive || t.IsEnum || t.IsPointer)
            {
                return info;
            }
            info.Walk = Walked(t) || t.IsValueType;
            var fields = new List<FieldInfo>();
            for (Type? x = t; x != null && x != typeof(object) && x != typeof(ValueType); x = x.BaseType)
            {
                fields.AddRange(x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => !_excludedFields.Contains(f)));
            }
            info.Fields = fields.ToArray();
            info.RefField = new bool[info.Fields.Length];
            for (int i = 0; i < info.Fields.Length; i++)
            {
                Type ft = info.Fields[i].FieldType;
                info.RefField[i] = ft.IsValueType ? Info(ft).HoldsRefs : ft != typeof(string);
            }
            if (t.IsValueType) info.HoldsRefs = info.RefField.Any(r => r);
            if (UseEmit && (info.Walk || info.HoldsRefs))
            {
                info.GetAll = Emit(t, info.Fields, set: false);
                if (!t.IsValueType) info.SetAll = Emit(t, info.Fields, set: true);
            }
            return info;
        }

        static int SizeOf<T>() => Unsafe.SizeOf<T>();

        // ---- take / restore ----

        public static SimSnapshot Take(int step, IEnumerable<object> roots, SimSnapshot? previous = null)
        {
            if (!_setUp) throw new InvalidOperationException("SimSnapshot.SetUp first");
            var sw = Stopwatch.StartNew();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var order = new List<object>(1 << 16);
            var pending = new Stack<object>();
            string owner = "(static)";
            Dictionary<object, string>? owners = TrackOwners ? new(ReferenceEqualityComparer.Instance) : null;
            void Visit(object? o)
            {
                if (o == null || o is string) return;
                Type t = o.GetType();
                if (t.IsValueType)
                {
                    VisitStruct(o, Info(t));
                    return;
                }
                TypeInfo info = Info(t);
                if (!info.Walk || !seen.Add(o)) return;
                owners?.TryAdd(o, owner);
                pending.Push(o);
            }
            void VisitStruct(object boxed, TypeInfo info)
            {
                if (!info.HoldsRefs) return;
                var values = new object?[info.Fields.Length];
                info.Get(boxed, values);
                for (int i = 0; i < values.Length; i++)
                {
                    if (!info.RefField[i]) continue;
                    if (owners != null) owner = info.Fields[i].DeclaringType!.Name + "." + info.Fields[i].Name;
                    Visit(values[i]);
                }
            }
            var statics = new (FieldInfo, object?)[_staticFields.Count];
            for (int i = 0; i < statics.Length; i++)
            {
                object? v = _staticFields[i].GetValue(null);
                statics[i] = (_staticFields[i], v);
                Visit(v);
            }
            foreach (object root in roots) Visit(root);
            // previous checkpoint's array copies, to share unchanged ones (the loaded room's big arrays rarely change)
            Dictionary<object, Array>? prevArrays = null;
            if (previous != null)
            {
                prevArrays = new Dictionary<object, Array>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < previous._objects.Length; i++)
                {
                    if (previous._arrays[i] is Array a) prevArrays[previous._objects[i]] = a;
                }
            }
            var fieldsList = new List<object?[]?>(1 << 16);
            var arraysList = new List<Array?>(1 << 16);
            long bytes = 0;
            while (pending.Count > 0)
            {
                object o = pending.Pop();
                order.Add(o);
                TypeInfo info = Info(o.GetType());
                if (o is Array array)
                {
                    fieldsList.Add(null);
                    if (info.ElementSize > 0)
                    {
                        if (prevArrays != null && prevArrays.TryGetValue(o, out Array? old) && old.Length == array.Length
                            && RawBytes(array, info.ElementSize).SequenceEqual(RawBytes(old, info.ElementSize)))
                        {
                            arraysList.Add(old);
                        }
                        else
                        {
                            arraysList.Add((Array)array.Clone());
                            bytes += (long)array.Length * info.ElementSize;
                        }
                        continue;
                    }
                    arraysList.Add((Array)array.Clone());
                    bytes += array.Length * 8L;
                    if (owners != null) owner = t0(o) + "[]";
                    foreach (object? e in array) Visit(e);
                    continue;
                }
                var values = new object?[info.Fields.Length];
                info.Get(o, values);
                for (int i = 0; i < values.Length; i++)
                {
                    if (!info.RefField[i]) continue;
                    if (owners != null) owner = info.Fields[i].DeclaringType!.Name + "." + info.Fields[i].Name;
                    Visit(values[i]);
                }
                bytes += 16 + values.Length * 8L;
                fieldsList.Add(values);
                arraysList.Add(null);
            }
            return new SimSnapshot(step, order.ToArray(), fieldsList.ToArray(), arraysList.ToArray(), statics, bytes,
                sw.Elapsed.TotalMilliseconds) { _owners = owners };
        }

        static string t0(object o) => o.GetType().GetElementType()?.Name ?? o.GetType().Name;

        // census only: which field first reached each object
        public static bool TrackOwners { get; set; }
        Dictionary<object, string>? _owners;

        static ReadOnlySpan<byte> RawBytes(Array a, int elementSize) =>
            MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(a), a.Length * elementSize);

        // every object back as it was (the same objects), and every static field
        public void Restore()
        {
            for (int i = 0; i < _statics.Length; i++)
            {
                (FieldInfo f, object? v) = _statics[i];
                if (!f.IsInitOnly) f.SetValue(null, v);
            }
            for (int i = 0; i < _objects.Length; i++)
            {
                object o = _objects[i];
                if (_arrays[i] is Array copy)
                {
                    Array.Copy(copy, (Array)o, copy.Length);
                    continue;
                }
                Info(o.GetType()).Set(o, _fields[i]!);
            }
        }

        // the walked objects by type, biggest first (for tuning what's left out)
        public IEnumerable<(string Type, int Count, long Bytes)> Census()
        {
            var byType = new Dictionary<string, (int, long)>();
            for (int i = 0; i < _objects.Length; i++)
            {
                Type t = _objects[i].GetType();
                long b = _arrays[i] is Array a ? (long)a.Length * Math.Max(8, Info(t).ElementSize) : 16 + _fields[i]!.Length * 8L;
                string key = (t.Name) + (_owners != null && _owners.TryGetValue(_objects[i], out string? ow) ? "  <- " + ow : "");
                byType[key] = byType.TryGetValue(key, out var c) ? (c.Item1 + 1, c.Item2 + b) : (1, b);
            }
            return byType.OrderByDescending(kv => kv.Value.Item2).Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2));
        }
    }
}
