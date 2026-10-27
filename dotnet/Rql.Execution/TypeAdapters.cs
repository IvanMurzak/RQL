using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Rql.Execution
{
    /// <summary>Outcome of an adapter capability that may decline: not handled (defer), succeeded, or failed with a reason.</summary>
    public enum AdapterOutcome { NotHandled, Ok, Failed }

    /// <summary>Capabilities an adapter overrides (detected at registration, so hot paths skip adapters that never answer).</summary>
    [Flags]
    internal enum AdapterCaps { None = 0, Encode = 1, Coerce = 2, Equality = 4, Ordering = 8, Identity = 16, Liveness = 32 }

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
        /// which is the adapter's type or a subtype. Used for writes (<c>set</c>, <c>call</c> arguments) only; to make
        /// comparisons accept the same literals, coerce the other operand in <see cref="AreEqual"/> / <see cref="Compare"/>.
        /// Nested values go through <paramref name="kernel"/> (e.g. <c>kernel.TryCoerce(item, typeof(Color), …)</c>), which
        /// applies the adapters registered for that type without this adapter knowing them.
        /// </summary>
        public virtual AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            return AdapterOutcome.NotHandled;
        }

        /// <summary>Equality of a value of the adapter's type with any other value; null defers. Nested values: <c>kernel.Equal</c>.</summary>
        public virtual bool? AreEqual(object value, object? other, RqlKernel kernel) => null;

        /// <summary>Ordering of a value of the adapter's type with any other value; null defers (incomparable by default).</summary>
        public virtual int? Compare(object value, object? other, RqlKernel kernel) => null;

        /// <summary>Stable id for <c>$ref</c> ("Type#id"); null defers to the identity member / session handles.</summary>
        public virtual string? StableId(object value) => null;

        /// <summary>Liveness (e.g. Unity's destroyed-but-not-null objects, Unreal's pending-kill); null defers.</summary>
        public virtual bool? IsAlive(object value) => null;

        /// <summary>One line for schemas and <c>describe</c>, e.g. how agents should write literals of the type.</summary>
        public virtual string? Description => null;

        /// <summary>Optional JSON Schema of the literals <see cref="TryCoerce"/> accepts (shown by <c>describe</c> as <c>literal</c>).</summary>
        public virtual JsonNode? LiteralSchema => null;

        internal AdapterCaps Caps { get; private set; }

        internal void DetectCaps()
        {
            var caps = AdapterCaps.None;
            if (Overrides("Encode")) caps |= AdapterCaps.Encode;
            if (Overrides("TryCoerce")) caps |= AdapterCaps.Coerce;
            if (Overrides("AreEqual", "Equal")) caps |= AdapterCaps.Equality;
            if (Overrides("Compare", "Order")) caps |= AdapterCaps.Ordering;
            if (Overrides("StableId", "Id")) caps |= AdapterCaps.Identity;
            if (Overrides("IsAlive", "Alive")) caps |= AdapterCaps.Liveness;
            Caps = caps;
        }

        /// <summary>True when a class between the concrete adapter and the library base types declares one of the methods.</summary>
        private bool Overrides(params string[] names)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = GetType(); t != null && t != typeof(RqlTypeAdapter); t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(RqlTypeAdapter<>)) break;
                if (t.GetMethods(flags).Any(m => names.Contains(m.Name))) return true;
            }
            return false;
        }
    }

    /// <summary>Strongly typed convenience base: capabilities receive <typeparamref name="T"/> directly.</summary>
    public abstract class RqlTypeAdapter<T> : RqlTypeAdapter
    {
        protected RqlTypeAdapter(int priority = 0) : base(typeof(T), priority) { }

        public sealed override void ConfigureMembers(MemberTable members) => Members(new MemberTable<T>(members));
        protected virtual void Members(MemberTable<T> members) { }

        public sealed override JsonNode? Encode(object value, EncodeContext context) => value is T t ? Encode(t, context) : null;
        protected virtual JsonNode? Encode(T value, EncodeContext context) => null;

        public sealed override bool? AreEqual(object value, object? other, RqlKernel kernel) => value is T t ? Equal(t, other, kernel) : null;
        protected virtual bool? Equal(T value, object? other, RqlKernel kernel) => null;

        public sealed override int? Compare(object value, object? other, RqlKernel kernel) => value is T t ? Order(t, other, kernel) : null;
        protected virtual int? Order(T value, object? other, RqlKernel kernel) => null;

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
        private readonly List<CallableMethod> _methods;
        private readonly Type _type;
        private readonly Action<Type, List<MemberAccessor>, Func<FieldInfo, bool>> _includeFields;

        internal MemberTable(Type type, List<MemberAccessor> members, List<CallableMethod> methods,
            Action<Type, List<MemberAccessor>, Func<FieldInfo, bool>> includeFields)
        {
            _type = type;
            _members = members;
            _methods = methods;
            _includeFields = includeFields;
        }

        /// <summary>Methods the <c>call</c> stage may invoke on this type.</summary>
        public IReadOnlyList<CallableMethod> Methods => _methods;

        /// <summary>Add a callable method (replaces methods with the same name and arity).</summary>
        public MemberTable AddMethod(CallableMethod method)
        {
            _methods.RemoveAll(m => m.Name == method.Name && m.Parameters.Count == method.Parameters.Count);
            _methods.Add(method);
            return this;
        }

        /// <summary>Add a callable method from parts; parameters may carry defaults.</summary>
        public MemberTable AddMethod(string name, IReadOnlyList<CallableParameter> parameters, Type returnType,
            Func<object, object?[], object?> invoke, string? description = null, Func<object, object?[], string?>? validate = null) =>
            AddMethod(new CallableMethod(name, parameters, returnType, invoke, description, validate));

        /// <summary>
        /// Make existing public instance CLR methods callable by name (all non-generic overloads) without
        /// <c>[RqlCallable]</c> — for engine types the host cannot annotate (e.g. Unity's GameObject.SetActive).
        /// </summary>
        public MemberTable Expose(string name, string? description = null)
        {
            var found = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name && !m.IsGenericMethodDefinition && !m.IsSpecialName && m.GetParameters().All(p => !p.ParameterType.IsByRef))
                .ToList();
            if (found.Count == 0) throw new ArgumentException($"type {_type.Name} has no public instance method '{name}' to expose");
            foreach (var m in found) AddMethod(CallableMethod.FromReflection(m, description));
            return this;
        }

        /// <summary>Remove callable methods by name (all overloads).</summary>
        public MemberTable HideMethods(params string[] names)
        {
            foreach (var n in names)
                if (!_methods.Any(m => m.Name == n)) throw new ArgumentException($"type {_type.Name} has no callable method '{n}' to hide");
            _methods.RemoveAll(m => names.Contains(m.Name));
            return this;
        }

        /// <summary>
        /// Add a virtual map member: entries are the owner's <paramref name="keys"/>, read with <paramref name="get"/> and
        /// assigned with <paramref name="set"/> (null = read-only). <paramref name="validate"/> runs before any write and returns a
        /// rejection reason or null. Example: a Material's shader floats as <c>Floats["_Glossiness"]</c>.
        /// </summary>
        public MemberTable AddMap<TValue>(string name, Func<object, IEnumerable<string>> keys, Func<object, string, TValue> get,
            Action<object, string, TValue>? set = null, Func<object, string, TValue, string?>? validate = null, string? description = null,
            Func<object, string, bool>? has = null) =>
            AddMapCore(name, new VirtualMapSpec<TValue> { Keys = keys, Get = get, Set = set, Validate = validate, Has = has }, description);

        /// <summary>
        /// A virtual map whose entries have different value types (e.g. a Material's properties: floats, Colors, Textures):
        /// writes coerce each value to <paramref name="entryType"/>(owner, key) before validation (spec §13).
        /// </summary>
        public MemberTable AddMixedMap(string name, Func<object, IEnumerable<string>> keys, Func<object, string, Type> entryType,
            Func<object, string, object?> get, Action<object, string, object?>? set = null,
            Func<object, string, object?, string?>? validate = null, string? description = null, Func<object, string, bool>? has = null) =>
            AddMapCore(name, new VirtualMapSpec<object?> { Keys = keys, Get = get, Set = set, Validate = validate, EntryType = entryType, Has = has }, description);

        private MemberTable AddMapCore<TValue>(string name, VirtualMapSpec<TValue> spec, string? description)
        {
            if (spec.Set != null && _type.IsValueType && !IsWritesThrough)
                throw new ArgumentException($"virtual map setters on value type {_type.Name} would write to a copy (mark the type WritesThrough if it is a handle)");
            return Add(name, typeof(VirtualMap<TValue>), o => new VirtualMap<TValue>(o, spec), description: description);
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
            if (set != null && _type.IsValueType && !IsWritesThrough)
                throw new ArgumentException($"virtual setters on value type {_type.Name} would write to a copy; expose a writable field or property instead (or mark the type WritesThrough if it is a handle)");
            var accessor = MemberAccessor.Virtual(name, type, _type, get, set, validate, description, owning, NextOrder(name));
            int i = _members.FindIndex(m => m.Name == name);
            if (i >= 0) _members[i] = accessor;
            else _members.Add(accessor);
            return this;
        }

        /// <summary>Remove members entirely: hidden members are neither readable nor writable.</summary>
        public MemberTable Hide(params string[] names)
        {
            RequireAll(names);
            _members.RemoveAll(m => names.Contains(m.Name));
            return this;
        }

        /// <summary>Keep only the named members (whitelist), e.g. for types whose other getters are unsafe.</summary>
        public MemberTable Only(params string[] names)
        {
            RequireAll(names);
            _members.RemoveAll(m => !names.Contains(m.Name));
            return this;
        }

        // A misspelt name would silently leave a member exposed (e.g. Hide("Pasword")): configuring is all-or-error.
        private void RequireAll(string[] names)
        {
            foreach (var n in names)
                if (!_members.Any(m => m.Name == n)) throw new ArgumentException($"type {_type.Name} has no member '{n}' to configure");
        }

        public MemberTable ReadOnly(params string[] names) => Change(names, m => m.With(canWrite: false));

        /// <summary>Hide every member matching <paramref name="hide"/> (e.g. <c>[Obsolete]</c> / <c>[NonSerialized]</c> members via <see cref="MemberAccessor.Member"/>).</summary>
        public MemberTable HideWhere(Func<MemberAccessor, bool> hide)
        {
            _members.RemoveAll(m => hide(m));
            return this;
        }

        /// <summary>
        /// Getters that return a copy (e.g. Renderer.sharedMaterials, Gradient.colorKeys): writes into the value are assigned
        /// back through the member's setter, like value types, so they are not silently lost.
        /// </summary>
        public MemberTable CopyOnRead(params string[] names) => Change(names, m => m.With(copyOnRead: true));

        /// <summary>
        /// For value types that are handles to shared state (e.g. Unity's ParticleSystem.MainModule): writes to a copy are
        /// live, so no write-back to the owner happens, get-only owners are fine, virtual setters are allowed, and values of
        /// the type can be mutation targets. Call before adding virtual setters.
        /// </summary>
        public MemberTable WritesThrough()
        {
            if (!_type.IsValueType) throw new ArgumentException($"{_type.Name} is not a value type; WritesThrough applies to handle structs");
            IsWritesThrough = true;
            return this;
        }

        internal bool IsWritesThrough { get; private set; }
        internal string? IdentityName { get; private set; }
        internal string? DisplayName { get; private set; }

        /// <summary>The member used for stable refs <c>Type#id</c> (default: [RqlId], then <c>Id</c>).</summary>
        public MemberTable Identity(string name) { RequireAll(new[] { name }); IdentityName = name; return this; }

        /// <summary>The member shown as <c>name</c> in summaries (default: [RqlDisplay], then a string <c>Name</c>; e.g. Unity's lowercase <c>name</c>).</summary>
        public MemberTable Display(string name) { RequireAll(new[] { name }); DisplayName = name; return this; }

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
        public MemberTable<T> HideWhere(Func<MemberAccessor, bool> hide) { Untyped.HideWhere(hide); return this; }
        public MemberTable<T> CopyOnRead(params string[] names) { Untyped.CopyOnRead(names); return this; }
        public MemberTable<T> WritesThrough() { Untyped.WritesThrough(); return this; }
        public MemberTable<T> Identity(string name) { Untyped.Identity(name); return this; }
        public MemberTable<T> Display(string name) { Untyped.Display(name); return this; }
        public MemberTable<T> Expose(string name, string? description = null) { Untyped.Expose(name, description); return this; }
        public MemberTable<T> HideMethods(params string[] names) { Untyped.HideMethods(names); return this; }

        // Every Method / Function takes an optional validate: the rejection reason for a planned call (target, arguments), or
        // null. It runs for every target before the first call, on dry runs too (spec §8).

        /// <summary>A callable method without result.</summary>
        public MemberTable<T> Method(string name, Action<T> body, string? description = null, Func<T, string?>? validate = null) =>
            Add(name, typeof(void), (o, a) => { body((T)o); return null; }, description,
                validate == null ? null : (o, a) => validate((T)o));

        public MemberTable<T> Method<A>(string name, Action<T, A> body, string p1, string? description = null, Func<T, A, string?>? validate = null) =>
            Add(name, typeof(void), (o, a) => { body((T)o, (A)a[0]!); return null; }, description,
                validate == null ? null : (o, a) => validate((T)o, (A)a[0]!), (p1, typeof(A)));

        public MemberTable<T> Method<A, B>(string name, Action<T, A, B> body, string p1, string p2, string? description = null, Func<T, A, B, string?>? validate = null) =>
            Add(name, typeof(void), (o, a) => { body((T)o, (A)a[0]!, (B)a[1]!); return null; }, description,
                validate == null ? null : (o, a) => validate((T)o, (A)a[0]!, (B)a[1]!), (p1, typeof(A)), (p2, typeof(B)));

        /// <summary>A callable method with a result (returned in the <c>call</c> receipt).</summary>
        public MemberTable<T> Function<R>(string name, Func<T, R> body, string? description = null, Func<T, string?>? validate = null) =>
            Add(name, typeof(R), (o, a) => body((T)o), description,
                validate == null ? null : (o, a) => validate((T)o));

        public MemberTable<T> Function<A, R>(string name, Func<T, A, R> body, string p1, string? description = null, Func<T, A, string?>? validate = null) =>
            Add(name, typeof(R), (o, a) => body((T)o, (A)a[0]!), description,
                validate == null ? null : (o, a) => validate((T)o, (A)a[0]!), (p1, typeof(A)));

        public MemberTable<T> Function<A, B, R>(string name, Func<T, A, B, R> body, string p1, string p2, string? description = null, Func<T, A, B, string?>? validate = null) =>
            Add(name, typeof(R), (o, a) => body((T)o, (A)a[0]!, (B)a[1]!), description,
                validate == null ? null : (o, a) => validate((T)o, (A)a[0]!, (B)a[1]!), (p1, typeof(A)), (p2, typeof(B)));

        private MemberTable<T> Add(string name, Type returns, Func<object, object?[], object?> invoke, string? description,
            Func<object, object?[], string?>? validate, params (string name, Type type)[] ps)
        {
            Untyped.AddMethod(name, ps.Select(p => new CallableParameter(p.name, p.type)).ToArray(), returns, invoke, description, validate);
            return this;
        }

        /// <summary>Typed <see cref="MemberTable.AddMap{TValue}"/>.</summary>
        public MemberTable<T> Map<TValue>(string name, Func<T, IEnumerable<string>> keys, Func<T, string, TValue> get,
            Action<T, string, TValue>? set = null, Func<T, string, TValue, string?>? validate = null, string? description = null,
            Func<T, string, bool>? has = null)
        {
            Untyped.AddMap<TValue>(name, o => keys((T)o), (o, k) => get((T)o, k),
                set == null ? null : (Action<object, string, TValue>)((o, k, v) => set((T)o, k, v)),
                validate == null ? null : (Func<object, string, TValue, string?>)((o, k, v) => validate((T)o, k, v)), description,
                has == null ? null : (Func<object, string, bool>)((o, k) => has((T)o, k)));
            return this;
        }
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

        /// <summary>The registered adapters in registration order.</summary>
        public IReadOnlyList<RqlTypeAdapter> All => _adapters;

        /// <summary>Union of the registered adapters' capabilities.</summary>
        public AdapterCaps Caps { get; private set; }

        public void Add(RqlTypeAdapter adapter)
        {
            adapter.DetectCaps();
            lock (_adapters)
            {
                _adapters.Add(adapter);
                Caps |= adapter.Caps;
            }
            _byType.Clear();
        }

        // Engine-internal values (rows, map entries, absent markers, errors) are never adapted, whatever the target.
        private static readonly HashSet<Type> Internal = new HashSet<Type>
        {
            typeof(RowValue), typeof(MapEntry), typeof(Missing), typeof(Unknown), typeof(RqlError),
        };

        /// <summary>Applicable adapters, most specific first.</summary>
        public RqlTypeAdapter[] For(Type type)
        {
            if (_adapters.Count == 0 || Internal.Contains(type)) return Array.Empty<RqlTypeAdapter>();
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
                if (target.IsGenericTypeDefinition) return TypeModel.FindGeneric(type, target) != null ? 20000 : -1;
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
