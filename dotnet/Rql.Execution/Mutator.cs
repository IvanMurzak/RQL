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
                if (it.Value is MapEntry)
                    throw new RqlException("invalid-plan", $"'{op}' cannot target map entries; set the map member with a key path instead (Stats[\"key\"] = ...)");
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
        }

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
                    _x.CurrentPath = null;
                    var nv = value(_x);
                    if (nv is Missing || nv is Unknown)
                        throw new RqlException("coercion-failed", $"value for '{label}' on {_x.Engine.RefOf(item)} is missing");
                    if (nv is RqlError err)
                        throw new RqlException(err.Code == "stale-ref" ? "stale-ref" : "coercion-failed", $"value for '{label}' on {_x.Engine.RefOf(item)} failed: {err.Detail ?? err.Code}");
                    var slot = Resolve(item, target.Segments, out var container);
                    if (!slot.Writable(_x))
                        throw new RqlException(slot.DenyCode, $"'{label}' on {_x.Engine.RefOf(item)} is not writable{slot.DenyReason}");
                    if (!Coerce.TryTo(nv, slot.Type, out var coerced, out var why))
                        throw new RqlException("coercion-failed", $"cannot assign {V.Describe(nv)} to '{label}' ({TypeModel.FriendlyName(slot.Type)}): {why}");
                    writes.Add(new PlannedWrite { Item = item, Segments = target.Segments, Label = label, Old = slot.Get(container), New = coerced });
                }
            }

            if (!_x.Options.DryRun)
            {
                int applied = 0;
                try
                {
                    for (; applied < writes.Count; applied++) Assign(writes[applied].Item, writes[applied].Segments, writes[applied].New);
                }
                catch (Exception ex)
                {
                    for (int k = applied - 1; k >= 0; k--)
                    {
                        try { Assign(writes[k].Item, writes[k].Segments, writes[k].Old); } catch { /* best effort */ }
                    }
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    throw new RqlException("coercion-failed", $"write '{writes[applied].Label}' failed ({inner.Message}); {applied} earlier write(s) were rolled back");
                }
            }

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
                if (!(container is IDictionary))
                    throw new RqlException("unsupported", $"map type {info.FriendlyName} does not support writes");
                object? key = seg.Kind == SegmentKind.Member ? seg.Name : seg.Kind == SegmentKind.Key ? seg.Key : seg.Kind == SegmentKind.Index ? (object)(double)seg.Index : null;
                if (key == null || !Compiler.TryCoerceKey(key, info.KeyType, out var k) || k == null)
                    throw new RqlException("coercion-failed", $"invalid map key in '{label}'");
                return new MapSlot(k, info.ElementType ?? typeof(object));
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

        private Slot Resolve(object root, IReadOnlyList<Segment> segs, out object container)
        {
            string label = string.Concat(segs.Select(s => s.ToString()));
            object cur = root;
            for (int i = 0; i < segs.Count; i++)
            {
                var slot = SlotOf(cur, segs[i], label);
                if (i == segs.Count - 1) { container = cur; return slot; }
                var next = slot.Get(cur);
                if (next == null || next is Missing)
                    throw new RqlException("coercion-failed", $"cannot set '{label}': '{segs[i]}' is null on {_x.Engine.RefOf(root)}");
                cur = next;
            }
            throw new InvalidOperationException("empty set target");
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
            for (int j = containers.Count - 1; j >= 1; j--)
            {
                if (!containers[j].GetType().IsValueType) break;
                slots[j - 1].Set(containers[j - 1], containers[j]);
            }
        }

        // ------------------------------------------------------------ call

        public QueryResult Call(IEnumerable<Item> stream, CallStage s)
        {
            CheckPolicy("call");
            var args = s.Args.Select(_c.Compile).ToArray();
            var targets = Targets(stream, "call");
            var planned = new List<(object item, MethodInfo method, object?[] args)>();
            foreach (var item in targets)
            {
                var info = _x.Types.Info(item.GetType());
                var candidates = info.Callable.Where(m => string.Equals(m.Name, s.Method, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0)
                {
                    var all = info.Callable.Select(Describe.Signature).ToList();
                    bool exists = item.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m => string.Equals(m.Name, s.Method, StringComparison.OrdinalIgnoreCase));
                    throw new RqlException(exists ? "permission-denied" : "unknown-function",
                        $"no callable method '{s.Method}' on {info.ShortName}", null,
                        all.Count > 0 ? "callable: " + string.Join(", ", all) : $"{info.ShortName} has no callable methods");
                }
                _x.Current = item;
                var vals = args.Select(a => a(_x)).ToArray();
                MethodInfo? chosen = null;
                object?[]? coerced = null;
                string why = "";
                foreach (var m in candidates)
                {
                    var ps = m.GetParameters();
                    if (ps.Length < vals.Length || ps.Skip(vals.Length).Any(p => !p.HasDefaultValue)) { why = $"expects {ps.Length} argument(s)"; continue; }
                    var cv = new object?[ps.Length];
                    bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i >= vals.Length) { cv[i] = ps[i].DefaultValue; continue; }
                        if (!Coerce.TryTo(vals[i], ps[i].ParameterType, out cv[i], out var w)) { ok = false; why = $"argument '{ps[i].Name}': {w}"; break; }
                    }
                    if (ok) { chosen = m; coerced = cv; break; }
                }
                if (chosen == null)
                    throw new RqlException("coercion-failed", $"cannot call {s.Method} on {_x.Engine.RefOf(item)}: {why}", null,
                        "signature: " + string.Join(", ", candidates.Select(Describe.Signature)));
                planned.Add((item, chosen, coerced!));
            }

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
                        if (method.ReturnType == typeof(void)) result = null;
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw new RqlException("getter-threw", $"{method.Name} on {_x.Engine.RefOf(item)} threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}; {done} earlier call(s) completed and are NOT rolled back");
                    }
                }
                done++;
                rows.Add(new object?[] { _x.Engine.RefOf(item), Describe.Signature(method), result });
            }
            var r = _enc.Rows(new[] { "$ref", "method", "result" }, rows);
            r.DryRun = _x.Options.DryRun;
            return r;
        }
    }

    internal static class Coerce
    {
        public static bool TryTo(object? v, Type target, out object? result, out string why)
        {
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
                    if (!TryTo(el, elem, out var ce, out var w)) { why = "element: " + w; return false; }
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
                object inst;
                try { inst = Activator.CreateInstance(concrete)!; }
                catch { why = "cannot construct " + TypeModel.FriendlyName(concrete); return false; }
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
