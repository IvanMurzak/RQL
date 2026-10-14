using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Rql.Execution
{
    /// <summary>Outcome of an adapter capability that may decline: not handled (defer), succeeded, or failed with a reason.</summary>
    public enum AdapterOutcome { NotHandled, Ok, Failed }

    /// <summary>
    /// A host extension that customises how values of a type family are read, shown, written and compared (spec §13).
    /// Every capability is optional; the default implementation of each one defers to the next applicable adapter and
    /// finally to RQL's built-in behaviour. Target may be an exact type, a base class (applies to subclasses), an
    /// interface, or an open generic type definition such as <c>typeof(List&lt;&gt;)</c>.
    /// </summary>
    public abstract class RqlTypeAdapter
    {
        protected RqlTypeAdapter(Type target, int priority = 0)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Priority = priority;
        }

        /// <summary>The type (family) this adapter applies to.</summary>
        public Type Target { get; }

        /// <summary>Tie-break only: among adapters of equal specificity, higher priority runs first.</summary>
        public int Priority { get; }

        /// <summary>Human-readable adapter name for <see cref="TypeModel.Explain"/>.</summary>
        public virtual string Name => GetType().Name;

        /// <summary>
        /// Shape the member table of a type this adapter applies to. Called from the least to the most specific adapter,
        /// so an adapter for a subclass refines what a base-class adapter configured.
        /// </summary>
        public virtual void ConfigureMembers(MemberTable members) { }

        /// <summary>Replace the output encoding of <paramref name="value"/>. Return null to defer.</summary>
        public virtual JsonNode? Encode(object value, EncodeContext context) => null;

        /// <summary>
        /// Convert <paramref name="value"/> (a query literal, list, or another runtime value) to <paramref name="target"/>,
        /// which is the adapter's type or a subtype. Used for writes and for comparisons against values of the type.
        /// </summary>
        public virtual AdapterOutcome TryCoerce(object? value, Type target, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            return AdapterOutcome.NotHandled;
        }

        /// <summary>Equality of a value of the adapter's type with any other value; null defers.</summary>
        public virtual bool? AreEqual(object value, object? other) => null;

        /// <summary>Ordering of a value of the adapter's type with any other value; null defers (incomparable by default).</summary>
        public virtual int? Compare(object value, object? other) => null;

        /// <summary>Stable id for <c>$ref</c> ("Type#id"); null defers to the identity member / session handles.</summary>
        public virtual string? StableId(object value) => null;

        /// <summary>Liveness (e.g. Unity's destroyed-but-not-null objects, Unreal's pending-kill); null defers.</summary>
        public virtual bool? IsAlive(object value) => null;

        /// <summary>One line for schemas and <c>describe</c>, e.g. how agents should write literals of the type.</summary>
        public virtual string? Description => null;
    }

    /// <summary>Strongly typed convenience base: capabilities receive <typeparamref name="T"/> directly.</summary>
    public abstract class RqlTypeAdapter<T> : RqlTypeAdapter
    {
        protected RqlTypeAdapter(int priority = 0) : base(typeof(T), priority) { }

        public sealed override void ConfigureMembers(MemberTable members) => Members(new MemberTable<T>(members));
        protected virtual void Members(MemberTable<T> members) { }

        public sealed override JsonNode? Encode(object value, EncodeContext context) => value is T t ? Encode(t, context) : null;
        protected virtual JsonNode? Encode(T value, EncodeContext context) => null;

        public sealed override bool? AreEqual(object value, object? other) => value is T t ? Equal(t, other) : null;
        protected virtual bool? Equal(T value, object? other) => null;

        public sealed override int? Compare(object value, object? other) => value is T t ? Order(t, other) : null;
        protected virtual int? Order(T value, object? other) => null;

        public sealed override string? StableId(object value) => value is T t ? Id(t) : null;
        protected virtual string? Id(T value) => null;

        public sealed override bool? IsAlive(object value) => value is T t ? Alive(t) : null;
        protected virtual bool? Alive(T value) => null;
    }

    /// <summary>What an <see cref="RqlTypeAdapter.Encode"/> implementation can use.</summary>
    public sealed class EncodeContext
    {
        private readonly Func<object?, int, JsonNode?> _encode;
        private readonly Func<object, JsonObject> _summary;

        internal EncodeContext(Func<object?, int, JsonNode?> encode, Func<object, JsonObject> summary, int depth)
        {
            _encode = encode;
            _summary = summary;
            Depth = depth;
        }

        /// <summary>Nesting depth of the value being encoded (0 = a top-level cell).</summary>
        public int Depth { get; }

        /// <summary>Encode another value with full adapter support (e.g. a member of this value).</summary>
        public JsonNode? EncodeValue(object? value) => _encode(value, Depth + 1);

        /// <summary>The standard object summary <c>{"$ref": …, "name": …}</c>.</summary>
        public JsonObject Summary(object value) => _summary(value);
    }

    /// <summary>The member table of one runtime type while adapters shape it (untyped view).</summary>
    public sealed class MemberTable
    {
        private readonly List<MemberAccessor> _members;
        private readonly Type _type;
        private readonly Action<Type, List<MemberAccessor>, Func<FieldInfo, bool>> _includeFields;

        internal MemberTable(Type type, List<MemberAccessor> members, Action<Type, List<MemberAccessor>, Func<FieldInfo, bool>> includeFields)
        {
            _type = type;
            _members = members;
            _includeFields = includeFields;
        }

        /// <summary>The runtime type whose members are being configured.</summary>
        public Type Type => _type;

        public IReadOnlyList<MemberAccessor> Members => _members;

        public bool Contains(string name) => _members.Any(m => m.Name == name);

        /// <summary>
        /// Add (or replace) a virtual member. <paramref name="set"/> null = read-only. <paramref name="validate"/> runs while
        /// a mutation is planned (before any write) and returns a rejection reason or null. Virtual members are computed
        /// values, so by default generic <c>recurse</c> does not follow them (<paramref name="owning"/> = false).
        /// </summary>
        public MemberTable Add(string name, Type type, Func<object, object?> get, Action<object, object?>? set = null,
            Func<object, object?, string?>? validate = null, string? description = null, bool owning = false)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("member name required", nameof(name));
            if (set != null && _type.IsValueType)
                throw new ArgumentException($"virtual setters on value type {_type.Name} would write to a copy; expose a writable field or property instead");
            var accessor = MemberAccessor.Virtual(name, type, _type, get, set, validate, description, owning, NextOrder(name));
            int i = _members.FindIndex(m => m.Name == name);
            if (i >= 0) _members[i] = accessor;
            else _members.Add(accessor);
            return this;
        }

        /// <summary>Remove members entirely: hidden members are neither readable nor writable. Absent names are ignored.</summary>
        public MemberTable Hide(params string[] names)
        {
            _members.RemoveAll(m => names.Contains(m.Name));
            return this;
        }

        /// <summary>Keep only the named members (whitelist), e.g. for types whose other getters are unsafe.</summary>
        public MemberTable Only(params string[] names)
        {
            _members.RemoveAll(m => !names.Contains(m.Name));
            return this;
        }

        public MemberTable ReadOnly(params string[] names) => Change(names, m => m.With(canWrite: false));

        /// <summary>Mark non-owning references (generic recurse and snapshots do not follow them).</summary>
        public MemberTable Reference(params string[] names) => Change(names, m => m.With(owning: false));

        /// <summary>
        /// Read only when a query names the member explicitly — never implicitly (generic recurse, inline encoding of the
        /// owner, describe). For getters that are expensive or have side effects (e.g. Unity's Renderer.material).
        /// </summary>
        public MemberTable ExplicitOnly(params string[] names) => Change(names, m => m.With(explicitOnly: true));

        public MemberTable Describe(string name, string description) => Change(new[] { name }, m => m.With(description: description));

        /// <summary>
        /// Additionally expose non-public instance fields of the type chain matching <paramref name="include"/>
        /// (e.g. Unity's [SerializeField] privates). Compiler-generated backing fields are never included.
        /// </summary>
        public MemberTable IncludeFields(Func<FieldInfo, bool> include)
        {
            _includeFields(_type, _members, include);
            return this;
        }

        private MemberTable Change(IEnumerable<string> names, Func<MemberAccessor, MemberAccessor> change)
        {
            foreach (var n in names)
            {
                int i = _members.FindIndex(m => m.Name == n);
                if (i < 0) throw new ArgumentException($"type {_type.Name} has no member '{n}' to configure");
                _members[i] = change(_members[i]);
            }
            return this;
        }

        private int NextOrder(string name)
        {
            var existing = _members.FirstOrDefault(m => m.Name == name);
            return existing?.Order ?? (_members.Count == 0 ? 0 : _members.Max(m => m.Order) + 1);
        }
    }

    /// <summary>Strongly typed view of <see cref="MemberTable"/> for <see cref="RqlTypeAdapter{T}"/>.</summary>
    public sealed class MemberTable<T>
    {
        public MemberTable(MemberTable table) { Untyped = table; }

        public MemberTable Untyped { get; }

        public MemberTable<T> Add<TValue>(string name, Func<T, TValue> get, Action<T, TValue>? set = null,
            Func<T, TValue, string?>? validate = null, string? description = null, bool owning = false)
        {
            Action<object, object?>? s = set == null ? null : (o, v) => set((T)o, (TValue)v!);
            Func<object, object?, string?>? val = validate == null ? null : (o, v) => validate((T)o, (TValue)v!);
            Untyped.Add(name, typeof(TValue), o => get((T)o), s, val, description, owning);
            return this;
        }

        public MemberTable<T> Hide(params string[] names) { Untyped.Hide(names); return this; }
        public MemberTable<T> Only(params string[] names) { Untyped.Only(names); return this; }
        public MemberTable<T> ReadOnly(params string[] names) { Untyped.ReadOnly(names); return this; }
        public MemberTable<T> Reference(params string[] names) { Untyped.Reference(names); return this; }
        public MemberTable<T> ExplicitOnly(params string[] names) { Untyped.ExplicitOnly(names); return this; }
        public MemberTable<T> Describe(string name, string description) { Untyped.Describe(name, description); return this; }
        public MemberTable<T> IncludeFields(Func<FieldInfo, bool> include) { Untyped.IncludeFields(include); return this; }
    }

    /// <summary>Adapter registry and deterministic resolution (spec §13).</summary>
    internal sealed class AdapterRegistry
    {
        private readonly List<RqlTypeAdapter> _adapters = new List<RqlTypeAdapter>();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, RqlTypeAdapter[]> _byType =
            new System.Collections.Concurrent.ConcurrentDictionary<Type, RqlTypeAdapter[]>();

        public bool Any => _adapters.Count > 0;

        public void Add(RqlTypeAdapter adapter)
        {
            lock (_adapters) _adapters.Add(adapter);
            _byType.Clear();
        }

        /// <summary>Applicable adapters, most specific first.</summary>
        public RqlTypeAdapter[] For(Type type)
        {
            if (_adapters.Count == 0) return Array.Empty<RqlTypeAdapter>();
            return _byType.GetOrAdd(type, Resolve);
        }

        private RqlTypeAdapter[] Resolve(Type type)
        {
            List<(RqlTypeAdapter a, int rank, int index)> hits;
            lock (_adapters)
            {
                hits = _adapters.Select((a, i) => (a, rank: Rank(a.Target, type), index: i)).Where(h => h.rank >= 0).ToList();
            }
            return hits.OrderBy(h => h.rank).ThenByDescending(h => h.a.Priority).ThenBy(h => h.index).Select(h => h.a).ToArray();
        }

        /// <summary>
        /// Specificity rank (lower = more specific), -1 = not applicable. Walking the base chain from the type itself
        /// (distance d): the closed type at d ranks 2d, its open generic definition 2d+1; then interfaces (10000), then
        /// open generic interfaces (20000).
        /// </summary>
        internal static int Rank(Type target, Type type)
        {
            if (target.IsInterface)
            {
                if (target.IsGenericTypeDefinition)
                    return type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == target) ? 20000 : -1;
                return target.IsAssignableFrom(type) ? 10000 : -1;
            }
            int d = 0;
            for (var b = type; b != null; b = b.BaseType, d++)
            {
                if (b == target) return 2 * d;
                if (target.IsGenericTypeDefinition && b.IsGenericType && b.GetGenericTypeDefinition() == target) return 2 * d + 1;
            }
            return -1;
        }
    }
}
