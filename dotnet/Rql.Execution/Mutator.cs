using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Rql.Execution
{
    /// <summary>Permissioned, all-or-nothing `set` / `call` (spec §8).</summary>
    internal sealed class Mutator
    {
        private readonly Exec _x;
        private readonly Compiler _c;
        private readonly Encoder _enc;

        public Mutator(Exec x, Compiler c, Encoder enc) { _x = x; _c = c; _enc = enc; }

        private void CheckPolicy(string op)
        {
            if (!_x.Engine.Mutations.Enabled)
                throw new RqlException("permission-denied", $"'{op}' is not allowed: this host does not permit mutations");
            if (!_x.Options.AllowMutation)
                throw new RqlException("permission-denied", $"'{op}' requires a mutation request (use the mutate tool / options.allowMutation)");
        }

        private List<object> Targets(IEnumerable<Item> stream, string op)
        {
            var list = new List<object>();
            foreach (var it in stream)
            {
                if (it.Value == null) continue;
                if (it.Value is RowValue)
                    throw new RqlException("invalid-plan", $"'{op}' needs objects, not projected rows; remove 'select' before '{op}'");
                if (_x.Types.IsDead(it.Value)) continue; // destroyed objects read as null: skipped like null items
                if (it.Value is MapEntry)
                    throw new RqlException("invalid-plan", $"'{op}' cannot target map entries; set the map member with a key path instead (Stats[\"key\"] = ...)");
                if ((it.Value.GetType().IsValueType && !_x.Types.Info(it.Value.GetType()).WritesThrough) || TypeModel.IsScalarType(it.Value.GetType()))
                    throw new RqlException("invalid-plan", $"'{op}' needs objects with identity; '{_x.Types.Info(it.Value.GetType()).ShortName}' items are value copies, so the change would be lost",
                        null, "set the member from its owner instead, e.g. objects | where … | set Transform.Position.x = 0");
                list.Add(it.Value);
                if (list.Count > _x.MaxMutationTargets)
                {
                    throw new RqlException("mutation-limit", $"more than {_x.MaxMutationTargets} objects matched '{op}'; nothing was changed",
                        null, "narrow the query (where / first / ref(...)) or raise budgets.maxMutationTargets if the bulk change is intended");
                }
            }
            return list;
        }

        // ------------------------------------------------------------ set

        private sealed class PlannedWrite
        {
            public object Item = null!;
            public IReadOnlyList<Segment> Segments = null!;
            public string Label = "";
            public object? Old;
            public object? New;
            public object Owner = null!;
            public IReadOnlyList<object> Owners = null!;
        }

        private IReadOnlyList<MutationRecord> Records(IEnumerable<PlannedWrite> writes) =>
            writes.Select(w => new MutationRecord(w.Item, w.Owner, w.Label, w.Old, w.New, null, w.Owners)).ToList();

        /// <summary>
        /// Runs a host hook (never on dry-run). Before-write failures veto the mutation (nothing written yet);
        /// commit failures are warnings because the writes already happened.
        /// </summary>
        private void RunHook(Action<IReadOnlyList<MutationRecord>>? hook, IReadOnlyList<MutationRecord> records, bool veto)
        {
            if (hook == null || _x.Options.DryRun || records.Count == 0) return;
            try { hook(records); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                if (veto) throw new RqlException("permission-denied", $"the host rejected the change before anything was written: {ex.Message}");
                _x.Diagnostics.Add(Diagnostic.Warning("getter-threw", $"host commit hook threw {ex.GetType().Name}: {ex.Message}; the writes were applied"));
            }
        }

        private bool HooksOn => !_x.Options.DryRun && (_x.Engine.Mutations.BeforeWrite != null || _x.Engine.Mutations.Committed != null);

        public QueryResult Set(IEnumerable<Item> stream, SetStage s)
        {
            CheckPolicy("set");
            var assigns = s.Assignments.Select(a => (target: a.Target, value: _c.Compile(a.Value), label: Label(a.Target))).ToList();
            var targets = Targets(stream, "set");
            var writes = new List<PlannedWrite>();
            foreach (var item in targets)
            {
                foreach (var (target, value, label) in assigns)
                {
                    _x.Current = item;
                    _x.CurrentDepth = -1;
                    _x.CurrentPath = null;
                    var nv = value(_x);
                    if (nv is Missing || nv is Unknown)
                        throw new RqlException("coercion-failed", $"value for '{label}' on {_x.Engine.RefOf(item)} is missing");
                    if (nv is RqlError err)
                        throw new RqlException(err.Code == "stale-ref" ? "stale-ref" : "coercion-failed", $"value for '{label}' on {_x.Engine.RefOf(item)} failed: {err.Detail ?? err.Code}");
                    var slot = Resolve(item, target.Segments, out var container, out var owner, out var owners);
                    if (!slot.Writable(_x))
                        throw new RqlException(slot.DenyCode, $"'{label}' on {_x.Engine.RefOf(item)} is not writable{slot.DenyReason}");
                    if (!Coerce.TryTo(_x.Kernel, nv, slot.Type, out var coerced, out var why))
                        throw new RqlException("coercion-failed", $"cannot assign {V.Describe(nv)} to '{label}' ({TypeModel.FriendlyName(slot.Type)}): {why}");
                    if (slot.Validate(container, coerced) is string rejected)
                        throw new RqlException("coercion-failed", $"cannot assign {V.Describe(nv)} to '{label}' on {_x.Engine.RefOf(item)}: {rejected}");
                    writes.Add(new PlannedWrite { Item = item, Segments = target.Segments, Label = label, Old = SafeGet(slot, container, label), New = coerced, Owner = owner, Owners = owners });
                }
            }

            var records = HooksOn ? Records(writes) : Array.Empty<MutationRecord>();
            RunHook(_x.Engine.Mutations.BeforeWrite, records, veto: true);
            if (!_x.Options.DryRun)
            {
                int applied = 0;
                try
                {
                    for (; applied < writes.Count; applied++) Assign(writes[applied].Item, writes[applied].Segments, writes[applied].New);
                }
                catch (Exception ex)
                {
                    // The failed write itself is restored too: adapter setters may change state before throwing.
                    bool selfRestored = true;
                    try { Assign(writes[applied].Item, writes[applied].Segments, writes[applied].Old); } catch { selfRestored = false; }
                    int rollbackFailures = 0;
                    for (int k = applied - 1; k >= 0; k--)
                    {
                        try { Assign(writes[k].Item, writes[k].Segments, writes[k].Old); } catch { rollbackFailures++; }
                    }
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    var rolled = rollbackFailures == 0
                        ? $"{applied} earlier write(s) were rolled back"
                        : $"rolling back {applied} earlier write(s) FAILED for {rollbackFailures} of them — state may be partially modified";
                    if (!selfRestored) rolled += $"; restoring '{writes[applied].Label}' itself also failed, so it may be partially modified";
                    throw new RqlException("coercion-failed", $"write '{writes[applied].Label}' failed ({inner.Message}); {rolled}");
                }
            }

            RunHook(_x.Engine.Mutations.Committed, records, veto: false);
            var rows = writes.Select(w => new object?[] { _x.Engine.RefOf(w.Item), w.Label, w.Old, w.New }).ToList();
            var r = _enc.Rows(new[] { "$ref", "member", "old", "new" }, rows);
            r.DryRun = _x.Options.DryRun;
            return r;
        }

        private static string Label(PathExpr p) =>
            string.Concat(p.Segments.Select((s, i) => s.Kind == SegmentKind.Member ? (i == 0 ? "" : ".") + s.Name : s.ToString()));

        private abstract class Slot
        {
            public abstract Type Type { get; }
            public abstract object? Get(object container);
            public abstract void Set(object container, object? value);
            public virtual bool Writable(Exec x) => true;
            /// <summary>Adapter validation of a coerced value before any write: rejection reason or null.</summary>
            public virtual string? Validate(object container, object? value) => null;
            public virtual string DenyCode => "readonly-member";
            public virtual string DenyReason => "";
        }

        private sealed class MemberSlot : Slot
        {
            private readonly Type _declaring;
            public MemberSlot(Type declaring, MemberAccessor m) { _declaring = declaring; M = m; }
            public MemberAccessor M { get; }
            public override Type Type => M.Type;
            public override object? Get(object container) => M.Getter(container);
            public override void Set(object container, object? value) => M.SetValue(container, value);
            public override string? Validate(object container, object? value) => M.Validate?.Invoke(container, value);
            private bool _policyDenied;
            public override bool Writable(Exec x)
            {
                if (!M.CanWrite) return false;
                var policy = x.Engine.Mutations.CanWrite;
                if (policy != null && !policy(_declaring, M)) { _policyDenied = true; return false; }
                return true;
            }
            public override string DenyCode => _policyDenied ? "permission-denied" : "readonly-member";
            public override string DenyReason => _policyDenied ? " (denied by host policy)" : " (read-only member)";
        }

        private sealed class ListSlot : Slot
        {
            private readonly long _index;
            private readonly Type _elem;
            public ListSlot(long index, Type elem) { _index = index; _elem = elem; }
            public override Type Type => _elem;
            private int Idx(IList l) => (int)(_index < 0 ? l.Count + _index : _index);
            public override object? Get(object container) => ((IList)container)[Idx((IList)container)];
            public override void Set(object container, object? value) => ((IList)container)[Idx((IList)container)] = value;
        }

        private sealed class MapSlot : Slot
        {
            private readonly object _key;
            private readonly Type _value;
            public MapSlot(object key, Type value) { _key = key; _value = value; }
            public override Type Type => _value;
            public override object? Get(object container) => ((IDictionary)container).Contains(_key) ? ((IDictionary)container)[_key] : Missing.Value;
            // Virtual maps only take writes to existing entries: a new key could not be removed again on rollback.
            public override string? Validate(object container, object? value) =>
                container is IVirtualMap vm && _key is string k
                    ? (((IDictionary)container).Contains(k) ? vm.ValidateEntry(k, value) : $"no entry '{k}'")
                    : null;
            public override void Set(object container, object? value)
            {
                if (value is Missing) ((IDictionary)container).Remove(_key);
                else ((IDictionary)container)[_key] = value;
            }
        }

        private Slot SlotOf(object container, Segment seg, string label)
        {
            var info = _x.Types.Info(container.GetType());
            if (info.IsMap)
            {
                if (!(container is IDictionary dict))
                    throw new RqlException("unsupported", $"map type {info.FriendlyName} does not support writes");
                if (dict.IsReadOnly)
                    throw new RqlException("readonly-member", $"'{label}': map is read-only");
                if (seg.Kind == SegmentKind.Member && (string.Equals(seg.Name, "Count", StringComparison.OrdinalIgnoreCase)))
                    throw new RqlException("readonly-member", $"'{label}': Count of a map is read-only (use [\"Count\"] to address a key literally named Count)");
                object? key = seg.Kind == SegmentKind.Member ? seg.Name : seg.Kind == SegmentKind.Key ? seg.Key : seg.Kind == SegmentKind.Index ? (object)(double)seg.Index : null;
                // Writes address an existing key case-insensitively, like reads do (spec §3); new keys keep the given spelling.
                if (seg.Kind == SegmentKind.Member && key is string ks && container is IDictionary existing && !existing.Contains(ks))
                {
                    foreach (DictionaryEntry de in existing)
                        if (de.Key is string dk && string.Equals(dk, ks, StringComparison.OrdinalIgnoreCase)) { key = dk; break; }
                }
                if (key == null || !Compiler.TryCoerceKey(key, info.KeyType, out var k) || k == null)
                    throw new RqlException("coercion-failed", $"invalid map key in '{label}'");
                var entryType = container is IVirtualMap vm && k is string sk ? vm.EntryType(sk) : null;
                return new MapSlot(k, entryType ?? info.ElementType ?? typeof(object));
            }
            if (info.IsList)
            {
                if (seg.Kind != SegmentKind.Index)
                    throw new RqlException("invalid-plan", $"'{label}': lists can only be indexed ([i]) in set targets");
                if (!(container is IList list) || list.IsReadOnly)
                    throw new RqlException("readonly-member", $"'{label}': collection is not writable by index");
                long i = seg.Index < 0 ? list.Count + seg.Index : seg.Index;
                if (i < 0 || i >= list.Count) throw new RqlException("invalid-plan", $"'{label}': index {seg.Index} out of range (count {list.Count})");
                return new ListSlot(seg.Index, info.ElementType ?? typeof(object));
            }
            string? name = seg.Kind == SegmentKind.Member ? seg.Name : seg.Kind == SegmentKind.Key ? seg.Key as string : null;
            if (name == null) throw new RqlException("invalid-plan", $"'{label}': invalid segment {seg} on {info.ShortName}");
            var m = info.Find(name);
            if (m == null)
            {
                var close = Suggest.Closest(name, info.Members.Select(mm => mm.Name).ToList(), 3);
                throw new RqlException("unknown-member", $"no member '{name}' on {info.ShortName}", null,
                    close.Count > 0 ? "did you mean " + string.Join(" / ", close) + "?" : "members: " + string.Join(" ", info.Members.Select(mm => mm.Name)));
            }
            return new MemberSlot(container.GetType(), m);
        }

        private Slot Resolve(object root, IReadOnlyList<Segment> segs, out object container, out object owner, out IReadOnlyList<object> owners)
        {
            string label = string.Concat(segs.Select(s => s.ToString()));
            object cur = root;
            var chain = new List<(Slot slot, object value)>();
            for (int i = 0; i < segs.Count; i++)
            {
                var slot = SlotOf(cur, segs[i], label);
                if (i == segs.Count - 1)
                {
                    // Every copy on the way (value type or copy-on-read member) is written back into its parent: those parent
                    // slots must be writable and allowed too (readonly struct fields, get-only properties, policy).
                    // Below a copy-on-read member everything is part of the copy, objects included, so the write-back goes
                    // all the way up to that member.
                    int copyRoot = CopyRoot(chain.Select(c => c.slot).ToList());
                    for (int j = chain.Count - 1; j >= 0; j--)
                    {
                        bool writeBack = NeedsWriteBack(chain[j].slot, chain[j].value);
                        if (!writeBack)
                        {
                            if (copyRoot >= 0 && j > copyRoot) continue;
                            break;
                        }
                        if (!chain[j].slot.Writable(_x))
                            throw new RqlException(chain[j].slot.DenyCode, $"'{label}' on {_x.Engine.RefOf(root)} is not writable: '{segs[j]}' is a copy (value type or copy-on-read member) that must be written back through a non-writable member{chain[j].slot.DenyReason}");
                    }
                    container = cur;
                    // The owner whose state changes: the nearest enclosing object (never a collection, map view or struct),
                    // e.g. the Material for Uniforms["_Color"], the ParticleSystem for main.duration (spec §13). Inside a
                    // copy-on-read member it is the object holding that member (the copies are not the real state).
                    var enclosing = new List<object>();
                    for (int k = (copyRoot >= 0 ? copyRoot : chain.Count) - 1; k >= 0; k--)
                        if (_x.Types.Info(chain[k].value.GetType()).IsObject) enclosing.Add(chain[k].value);
                    enclosing.Add(root);
                    owner = enclosing[0];
                    owners = enclosing;
                    return slot;
                }
                object? next = SafeGet(slot, cur, label);
                if (next == null || next is Missing)
                    throw new RqlException("coercion-failed", $"cannot set '{label}': '{segs[i]}' is null on {_x.Engine.RefOf(root)}");
                chain.Add((slot, next));
                cur = next;
            }
            throw new InvalidOperationException("empty set target");
        }

        private static object? SafeGet(Slot slot, object container, string label)
        {
            try { return slot.Get(container); }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                throw new RqlException("getter-threw", $"reading '{label}' failed: {inner.GetType().Name}: {inner.Message}");
            }
        }

        /// <summary>Navigate fresh, write, then write back modified value-type copies up the chain.</summary>
        private void Assign(object root, IReadOnlyList<Segment> segs, object? value)
        {
            string label = string.Concat(segs.Select(s => s.ToString()));
            var containers = new List<object> { root };
            var slots = new List<Slot>();
            object cur = root;
            for (int i = 0; i < segs.Count; i++)
            {
                var slot = SlotOf(cur, segs[i], label);
                slots.Add(slot);
                if (i == segs.Count - 1) break;
                cur = slot.Get(cur)!;
                containers.Add(cur);
            }
            slots[slots.Count - 1].Set(containers[containers.Count - 1], value);
            int copyRoot = CopyRoot(slots.Take(slots.Count - 1).ToList());
            for (int j = containers.Count - 1; j >= 1; j--)
            {
                if (NeedsWriteBack(slots[j - 1], containers[j])) slots[j - 1].Set(containers[j - 1], containers[j]);
                else if (copyRoot >= 0 && j - 1 > copyRoot) continue; // an object inside the copy: changed in place, keep going up
                else break;
            }
        }

        /// <summary>Index of the outermost copy-on-read member slot on a path, or -1.</summary>
        private static int CopyRoot(IReadOnlyList<Slot> slots)
        {
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] is MemberSlot ms && ms.M.CopyOnRead) return i;
            return -1;
        }

        /// <summary>
        /// Whether a container read through <paramref name="slot"/> is a copy that must be assigned back after writing into it:
        /// value types (unless handles that write through) and members whose getter returns a copy (spec §13).
        /// </summary>
        private bool NeedsWriteBack(Slot slot, object container)
        {
            if (slot is MemberSlot ms && ms.M.CopyOnRead) return true;
            return container.GetType().IsValueType && !_x.Types.Info(container.GetType()).WritesThrough;
        }

        // ------------------------------------------------------------ call

        public QueryResult Call(IEnumerable<Item> stream, CallStage s)
        {
            CheckPolicy("call");
            var args = s.Args.Select(_c.Compile).ToArray();
            var targets = Targets(stream, "call");
            var planned = new List<(object item, CallableMethod method, object?[] args)>();
            foreach (var item in targets)
            {
                var info = _x.Types.Info(item.GetType());
                var candidates = info.Callable.Where(m => string.Equals(m.Name, s.Method, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0)
                {
                    var all = info.Callable.Select(m => m.Signature).ToList();
                    bool exists = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m => string.Equals(m.Name, s.Method, StringComparison.OrdinalIgnoreCase));
                    throw new RqlException(exists ? "permission-denied" : "unknown-function",
                        $"no callable method '{s.Method}' on {info.ShortName}", null,
                        all.Count > 0 ? "callable: " + string.Join(", ", all) : $"{info.ShortName} has no callable methods");
                }
                _x.Current = item;
                    _x.CurrentDepth = -1;
                var vals = args.Select(a => a(_x)).ToArray();
                CallableMethod? chosen = null;
                object?[]? coerced = null;
                string why = "";
                foreach (var m in candidates)
                {
                    var ps = m.Parameters;
                    if (ps.Count < vals.Length || ps.Skip(vals.Length).Any(p => !p.HasDefault)) { why = $"expects {ps.Count} argument(s)"; continue; }
                    var cv = new object?[ps.Count];
                    bool ok = true;
                    for (int i = 0; i < ps.Count; i++)
                    {
                        if (i >= vals.Length) { cv[i] = ps[i].Default; continue; }
                        if (!Coerce.TryTo(_x.Kernel, vals[i], ps[i].Type, out cv[i], out var w)) { ok = false; why = $"argument '{ps[i].Name}': {w}"; break; }
                    }
                    if (ok) { chosen = m; coerced = cv; break; }
                }
                if (chosen == null)
                    throw new RqlException("coercion-failed", $"cannot call {s.Method} on {_x.Engine.RefOf(item)}: {why}", null,
                        "signature: " + string.Join(", ", candidates.Select(m => m.Signature)));
                planned.Add((item, chosen, coerced!));
            }

            var callRecords = HooksOn
                ? planned.Select(p => new MutationRecord(p.item, p.item, p.method.Signature, null, null, p.method)).ToList()
                : new List<MutationRecord>();
            RunHook(_x.Engine.Mutations.BeforeWrite, callRecords, veto: true);
            var rows = new List<object?[]>();
            int done = 0;
            foreach (var (item, method, cargs) in planned)
            {
                object? result = "(dry-run)";
                if (!_x.Options.DryRun)
                {
                    try
                    {
                        result = method.Invoke(item, cargs);
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        // Completed calls are not rolled back, so the host still learns about them.
                        int completed = done;
                        RunHook(_x.Engine.Mutations.Committed, callRecords.Take(completed).ToList(), veto: false);
                        throw new RqlException("getter-threw", $"{method.Name} on {_x.Engine.RefOf(item)} threw {ex.GetType().Name}: {ex.Message}; {done} earlier call(s) completed and are NOT rolled back");
                    }
                }
                done++;
                rows.Add(new object?[] { _x.Engine.RefOf(item), method.Signature, result });
            }
            RunHook(_x.Engine.Mutations.Committed, callRecords, veto: false);
            var r = _enc.Rows(new[] { "$ref", "method", "result" }, rows);
            r.DryRun = _x.Options.DryRun;
            return r;
        }
    }

    internal static class Coerce
    {
        // Per target type: public concrete assignable types by full and short name (spec §4.5 "$type"); rebuilt when new
        // assemblies load (plugins, domain reloads).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (int assemblies, Dictionary<string, Type[]> index)> SubtypeIndex =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, (int, Dictionary<string, Type[]>)>();

        private static bool TryInstantiate(Type t, out object instance, out string why)
        {
            why = "";
            try { instance = Activator.CreateInstance(t)!; return true; }
            catch (Exception ex) when (ex is MissingMethodException || ex is MemberAccessException || ex is TargetInvocationException
                                       || ex is NotSupportedException || ex is ArgumentException || ex is InvalidOperationException)
            {
                instance = null!;
                why = $"cannot construct {TypeModel.FriendlyName(t)} (needs a public parameterless constructor, or an adapter that coerces literals)";
                return false;
            }
        }

        /// <summary>Build <paramref name="target"/> from an object literal: concrete type, construction, member assignment (spec §4.5).</summary>
        private static bool TryConstruct(RqlKernel kernel, ObjectValue literal, Type target, out object? result, out string why)
        {
            var model = kernel.Types;
            var policy = kernel.WritePolicy;
            result = null;
            why = "";
            var info = model.Info(target);
            if (info.IsMap)
            {
                var keyType = info.KeyType ?? typeof(string);
                var valueType = info.ElementType ?? typeof(object);
                var concreteMap = target.IsInterface || target.IsAbstract ? typeof(Dictionary<,>).MakeGenericType(keyType, valueType) : target;
                if (!target.IsAssignableFrom(concreteMap) || !typeof(IDictionary).IsAssignableFrom(concreteMap)) { why = "unsupported map type " + TypeModel.FriendlyName(target); return false; }
                if (!TryInstantiate(concreteMap, out var mapObj, out why)) return false;
                var map = (IDictionary)mapObj;
                foreach (var kv in literal)
                {
                    if (!Compiler.TryCoerceKey(kv.Key, keyType, out var key) || key == null) { why = $"invalid key '{kv.Key}' for {TypeModel.FriendlyName(target)}"; return false; }
                    if (!TryTo(kernel, kv.Value, valueType, out var mv, out var w)) { why = $"'{kv.Key}': {w}"; return false; }
                    map[key] = mv;
                }
                result = map;
                return true;
            }
            if (info.Kind != TypeKind.Object && info.Kind != TypeKind.Struct) { why = $"an object literal cannot become {TypeModel.FriendlyName(target)}"; return false; }

            var concrete = target;
            if (literal.TryGetValue("$type", out var typeName))
            {
                if (!(typeName is string tn)) { why = "\"$type\" must be a type name string"; return false; }
                int loaded = AppDomain.CurrentDomain.GetAssemblies().Length;
                var entry = SubtypeIndex.TryGetValue(target, out var cached) && cached.assemblies == loaded
                    ? cached
                    : SubtypeIndex[target] = (loaded, BuildSubtypeIndex(target));
                var found = entry.index.TryGetValue(tn, out var hits)
                    ? hits.Where(t => !model.IsExcludedType(t)).ToArray()
                    : Array.Empty<Type>();
                if (found.Length == 0) { why = $"no concrete type named '{tn}' derives from {TypeModel.FriendlyName(target)}"; return false; }
                if (found.Length > 1) { why = $"'{tn}' is ambiguous: " + string.Join(", ", found.Select(t => t.FullName)); return false; }
                concrete = found[0];
            }
            if (concrete.IsAbstract || concrete.IsInterface)
            {
                why = $"{TypeModel.FriendlyName(concrete)} is abstract; add \"$type\" to choose a concrete type";
                return false;
            }
            if (!TryInstantiate(concrete, out var instance, out why)) return false;
            var ci = model.Info(concrete);
            foreach (var kv in literal)
            {
                if (kv.Key == "$type") continue;
                var m = ci.Find(kv.Key);
                if (m == null) { why = $"no member '{kv.Key}' on {ci.ShortName}"; return false; }
                if (!m.CanWrite) { why = $"member '{m.Name}' of {ci.ShortName} is read-only"; return false; }
                // Building a value assigns members, so the host's per-member write policy applies as for `set`.
                if (policy != null && !policy(concrete, m))
                    throw new RqlException("permission-denied", $"member '{m.Name}' of {ci.ShortName} may not be written (denied by host policy)");
                if (!TryTo(kernel, kv.Value, m.Type, out var mv, out var w)) { why = $"'{m.Name}': {w}"; return false; }
                if (m.Validate?.Invoke(instance, mv) is string rejected) { why = $"'{m.Name}': {rejected}"; return false; }
                try { m.SetValue(instance, mv); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { why = $"'{m.Name}': {ex.Message}"; return false; }
            }
            result = instance;
            return true;
        }

        private static Dictionary<string, Type[]> BuildSubtypeIndex(Type target)
        {
            var byName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            void Add(string? key, Type t)
            {
                if (key == null) return;
                if (!byName.TryGetValue(key, out var list)) byName[key] = list = new List<Type>();
                if (!list.Contains(t)) list.Add(t);
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in TypeModel.LoadableTypes(asm))
                {
                    if (!t.IsVisible || t.IsAbstract || t.IsInterface || t.ContainsGenericParameters || !target.IsAssignableFrom(t)) continue;
                    Add(t.FullName, t);
                    Add(TypeModel.ShortName(t), t);
                }
            }
            // A full name is unique by construction, so it always resolves to exactly one type.
            return byName.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
        }

        /// <summary>The one coercion pipeline (spec §8, §13): adapters, object literals, lists, scalars. Nested coercions reuse the kernel.</summary>
        internal static bool TryTo(RqlKernel kernel, object? v, Type target, out object? result, out string why)
        {
            var model = kernel.Types;
            why = "";
            result = null;
            if (v is Missing || v is Unknown || v is RqlError) { why = "value is " + V.Describe(v); return false; }
            var under = Nullable.GetUnderlyingType(target);
            if (v == null)
            {
                if (!target.IsValueType || under != null) return true;
                why = "null is not allowed for " + TypeModel.FriendlyName(target);
                return false;
            }
            if (under != null) target = under;
            if (target == typeof(object) || target.IsInstanceOfType(v)) { result = v; return true; }

            if ((model.Caps & AdapterCaps.Coerce) != 0)
            {
                foreach (var ad in model.Info(target).CoerceAdapters)
                {
                    // An adapter that asks the kernel for its own target type (to reuse the default behaviour) is skipped.
                    if (!RqlKernel.Enter(ad, target, RqlKernel.OpCoerce)) continue;
                    AdapterOutcome outcome;
                    object? adapted;
                    string? reason;
                    try { outcome = ad.TryCoerce(v, target, kernel, out adapted, out reason); }
                    finally { RqlKernel.Exit(); }
                    switch (outcome)
                    {
                        case AdapterOutcome.Ok:
                            if (adapted != null && !target.IsInstanceOfType(adapted))
                                throw new InvalidOperationException($"adapter {ad.Name} coerced to {adapted.GetType().Name}, not {target.Name}");
                            result = adapted;
                            return true;
                        case AdapterOutcome.Failed:
                            why = reason ?? "rejected by " + ad.Name;
                            return false;
                    }
                }
            }

            if (v is ObjectValue literal) return TryConstruct(kernel, literal, target, out result, out why);

            if (target.IsEnum)
            {
                if (v is string s)
                {
                    try
                    {
                        var parsed = Enum.Parse(target, s, true);
                        if (Enum.IsDefined(target, parsed) || target.IsDefined(typeof(FlagsAttribute), false)) { result = parsed; return true; }
                    }
                    catch (ArgumentException) { }
                    why = "expected one of " + string.Join(", ", Enum.GetNames(target));
                    return false;
                }
                if (V.TryNumber(v, out var dn) && Math.Floor(dn) == dn)
                {
                    var e = Enum.ToObject(target, (long)dn);
                    if (Enum.IsDefined(target, e)) { result = e; return true; }
                }
                why = "expected one of " + string.Join(", ", Enum.GetNames(target));
                return false;
            }

            if (V.IsNumericType(target))
            {
                if (!V.TryNumber(v, out var d)) { why = "expected a number"; return false; }
                var code = Type.GetTypeCode(target);
                bool integral = code != TypeCode.Single && code != TypeCode.Double && code != TypeCode.Decimal;
                if (integral && Math.Floor(d) != d) { why = "expected an integer"; return false; }
                try
                {
                    result = code == TypeCode.Single ? (float)d : Convert.ChangeType(d, target, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException) { why = "out of range for " + TypeModel.FriendlyName(target); return false; }
            }

            if (target == typeof(string))
            {
                if (V.IsString(v)) { result = V.AsString(v); return true; }
                why = "expected a string";
                return false;
            }
            if (target == typeof(char) && v is string cs && cs.Length == 1) { result = cs[0]; return true; }
            if (target == typeof(bool)) { why = "expected true or false"; return false; }

            if (v is IList src && !(v is string) && v.GetType() == typeof(List<object?>))
            {
                var info = TypeModel.Get(target);
                var elem = info.ElementType ?? typeof(object);
                var items = new List<object?>();
                foreach (var el in src)
                {
                    if (!TryTo(kernel, el, elem, out var ce, out var w)) { why = $"[{items.Count}]: " + w; return false; }
                    items.Add(ce);
                }
                if (target.IsArray)
                {
                    var arr = Array.CreateInstance(elem, items.Count);
                    for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
                    result = arr;
                    return true;
                }
                var concrete = target.IsInterface ? typeof(List<>).MakeGenericType(elem) : target;
                if (!target.IsAssignableFrom(concrete)) { why = "unsupported collection type"; return false; }
                if (!TryInstantiate(concrete, out var inst, out why)) return false;
                var add = concrete.GetMethod("Add", new[] { elem });
                if (add == null) { why = "collection has no Add"; return false; }
                foreach (var it in items) add.Invoke(inst, new[] { it });
                result = inst;
                return true;
            }
            why = $"expected {TypeModel.FriendlyName(target)}";
            return false;
        }
    }
}
