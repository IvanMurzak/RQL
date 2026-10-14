using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Rql.Execution
{
    public enum AccessorMode
    {
        /// <summary>Compile getters to delegates with System.Linq.Expressions (fast; needs JIT).</summary>
        Compiled,
        /// <summary>Plain PropertyInfo/FieldInfo.GetValue (AOT/IL2CPP safe).</summary>
        Reflection,
    }

    public enum TypeKind { Scalar, Enum, Object, Struct, List, Map }

    public sealed class MemberAccessor
    {
        internal MemberAccessor(MemberInfo member, Type type, Func<object, object?> getter, bool canWrite, bool owning, int order)
        {
            Member = member; Name = member.Name; Type = type; Getter = getter; CanWrite = canWrite; Owning = owning; Order = order;
            DeclaringType = member.DeclaringType!;
            Description = member.GetCustomAttribute<RqlDescriptionAttribute>()?.Text;
        }

        private MemberAccessor(MemberAccessor m, Func<object, object?>? getter = null)
        {
            Member = m.Member; Name = m.Name; Type = m.Type; Getter = getter ?? m.Getter; CanWrite = m.CanWrite; Owning = m.Owning; Order = m.Order;
            DeclaringType = m.DeclaringType; Description = m.Description; ExplicitOnly = m.ExplicitOnly; Setter = m.Setter; Validate = m.Validate;
            CopyOnRead = m.CopyOnRead;
        }

        internal static MemberAccessor Virtual(string name, Type type, Type declaring, Func<object, object?> get, Action<object, object?>? set,
            Func<object, object?, string?>? validate, string? description, bool owning, int order)
        {
            var m = new MemberAccessor(name, type, declaring, get, order)
            {
                Setter = set, CanWrite = set != null, Validate = validate, Description = description, Owning = owning,
            };
            return m;
        }

        private MemberAccessor(string name, Type type, Type declaring, Func<object, object?> get, int order)
        {
            Name = name; Type = type; DeclaringType = declaring; Getter = get; Order = order;
        }

        internal MemberAccessor With(bool? canWrite = null, bool? owning = null, bool? explicitOnly = null, string? description = null,
            bool? copyOnRead = null, Func<object, object?>? getter = null)
        {
            return new MemberAccessor(this, getter)
            {
                CanWrite = canWrite ?? CanWrite,
                Owning = owning ?? Owning,
                ExplicitOnly = explicitOnly ?? ExplicitOnly,
                Description = description ?? Description,
                CopyOnRead = copyOnRead ?? CopyOnRead,
            };
        }

        public string Name { get; }
        /// <summary>The reflected field/property; null for adapter-defined virtual members.</summary>
        public MemberInfo? Member { get; }
        public Type DeclaringType { get; }
        public Type Type { get; }
        public Func<object, object?> Getter { get; }
        public bool CanWrite { get; private set; }
        /// <summary>False for [RqlReference] members — not followed by generic recurse.</summary>
        public bool Owning { get; private set; }
        /// <summary>Read only when a path names it: skipped by generic recurse, inline encoding and snapshots (spec §13).</summary>
        public bool ExplicitOnly { get; private set; }
        /// <summary>
        /// The getter returns a copy (e.g. Unity's Renderer.sharedMaterials): writes into the value are assigned back through
        /// the setter, like value types (spec §13).
        /// </summary>
        public bool CopyOnRead { get; private set; }
        public int Order { get; }
        public string? Description { get; private set; }
        internal Action<object, object?>? Setter { get; private set; }
        /// <summary>Adapter validation of a coerced value before any write; returns a rejection reason or null.</summary>
        internal Func<object, object?, string?>? Validate { get; private set; }

        internal bool IsDefined(Type attribute) => Member != null && Member.IsDefined(attribute);

        public void SetValue(object target, object? value)
        {
            if (Setter != null) { Setter(target, value); return; }
            if (Member == null || !CanWriteReflected) throw new InvalidOperationException($"member '{Name}' is read-only");
            try
            {
                if (Member is PropertyInfo p) p.SetValue(target, value);
                else ((FieldInfo)Member).SetValue(target, value);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private bool CanWriteReflected => Member is FieldInfo f ? !f.IsInitOnly && !f.IsLiteral : ((PropertyInfo)Member!).SetMethod != null;
    }

    public sealed class TypeInfo
    {
        internal TypeInfo(Type type) { Type = type; }

        public Type Type { get; }
        public TypeKind Kind { get; internal set; }
        public string ShortName { get; internal set; } = "";
        public string FriendlyName { get; internal set; } = "";
        public string? Description { get; internal set; }
        /// <summary>JSON Schema of accepted literals, from the most specific adapter that provides one.</summary>
        public System.Text.Json.Nodes.JsonNode? LiteralSchema { get; internal set; }
        public IReadOnlyList<MemberAccessor> Members { get; internal set; } = Array.Empty<MemberAccessor>();
        /// <summary>Members read implicitly (inline encoding, generic recurse, ref search, snapshots): all but explicit-only ones.</summary>
        public IReadOnlyList<MemberAccessor> ImplicitMembers { get; internal set; } = Array.Empty<MemberAccessor>();
        internal Dictionary<string, MemberAccessor> ByName { get; set; } = new Dictionary<string, MemberAccessor>();
        internal Dictionary<string, MemberAccessor?> ByNameCI { get; set; } = new Dictionary<string, MemberAccessor?>(StringComparer.OrdinalIgnoreCase);
        public MemberAccessor? IdMember { get; internal set; }
        public MemberAccessor? DisplayMember { get; internal set; }
        public IReadOnlyCollection<string> TypeNames { get; internal set; } = Array.Empty<string>();
        public Type? ElementType { get; internal set; }
        public Type? KeyType { get; internal set; }
        internal Func<object, int>? CountGetter { get; set; }
        /// <summary>Methods the <c>call</c> stage may invoke: [RqlCallable] plus adapter-defined / exposed methods (spec §13).</summary>
        public IReadOnlyList<CallableMethod> Callable { get; internal set; } = Array.Empty<CallableMethod>();
        /// <summary>Applicable type adapters, most specific first (spec §13).</summary>
        public IReadOnlyList<RqlTypeAdapter> Adapters => AdapterArray;
        internal RqlTypeAdapter[] AdapterArray = Array.Empty<RqlTypeAdapter>();
        // Per-capability subsets, so lookups only visit adapters that answer.
        internal RqlTypeAdapter[] EncodeAdapters = Array.Empty<RqlTypeAdapter>();
        internal RqlTypeAdapter[] CoerceAdapters = Array.Empty<RqlTypeAdapter>();
        internal RqlTypeAdapter[] EqualityAdapters = Array.Empty<RqlTypeAdapter>();
        internal RqlTypeAdapter[] OrderingAdapters = Array.Empty<RqlTypeAdapter>();
        internal RqlTypeAdapter[] IdentityAdapters = Array.Empty<RqlTypeAdapter>();
        internal RqlTypeAdapter[] LivenessAdapters = Array.Empty<RqlTypeAdapter>();

        public bool IsList => Kind == TypeKind.List;
        public bool IsMap => Kind == TypeKind.Map;
        /// <summary>A value type whose copies write through to shared state (handles, e.g. Unity's ParticleSystem.MainModule): no write-back.</summary>
        public bool WritesThrough { get; internal set; }
        public bool IsStruct => Kind == TypeKind.Struct;
        public bool IsObject => Kind == TypeKind.Object;

        /// <summary>Exact ordinal match, then unique case-insensitive match (spec §3).</summary>
        public MemberAccessor? Find(string name)
        {
            if (ByName.TryGetValue(name, out var m)) return m;
            return ByNameCI.TryGetValue(name, out var ci) ? ci : null;
        }

        public bool IsA(string typeName) => TypeNames.Contains(typeName);
    }

    /// <summary>Cached, policy-filtered reflection metadata per CLR type.</summary>
    public sealed class TypeModel
    {
        // Every live model, so a process-wide kind change (TreatAsObject) invalidates them all. Declared before Shared:
        // static initializers run in textual order and Shared's constructor registers itself here.
        private static readonly List<WeakReference<TypeModel>> Models = new List<WeakReference<TypeModel>>();

        public static readonly TypeModel Shared = new TypeModel(AccessorMode.Compiled);


        private readonly ConcurrentDictionary<Type, TypeInfo> _cache = new ConcurrentDictionary<Type, TypeInfo>();
        private RqlKernel? _readKernel;

        /// <summary>The kernel for read-only work (comparisons): no write policy. Kernels are immutable, so one is shared.</summary>
        internal RqlKernel ReadKernel => _readKernel ??= new RqlKernel(this, null);

        private readonly Func<Type, TypeInfo> _build;
        private readonly AdapterRegistry _adapters = new AdapterRegistry();

        public TypeModel(AccessorMode mode)
        {
            Mode = mode;
            _build = Build;
            lock (Models)
            {
                Models.RemoveAll(w => !w.TryGetTarget(out _));
                Models.Add(new WeakReference<TypeModel>(this));
            }
        }

        private Func<Type, bool>? _excluded;

        /// <summary>
        /// Exclude member types (spec §13): members whose type, element type or generic argument matches are dropped from
        /// every type (e.g. delegates, native handles, assemblies that must never be touched).
        /// </summary>
        internal void ExcludeTypes(Func<Type, bool> excluded)
        {
            if (ReferenceEquals(this, Shared)) throw new InvalidOperationException("the shared type model cannot be configured");
            var prev = _excluded;
            _excluded = prev == null ? excluded : t => prev(t) || excluded(t);
            _cache.Clear();
        }

        /// <summary>Whether <paramref name="t"/> is an excluded type (spec §13 exclusion); such types are never constructed either.</summary>
        internal bool IsExcludedType(Type t) => _excluded != null && IsExcluded(t);

        // Element and generic-argument types form a finite tree (no cycles), so plain recursion suffices.
        private bool IsExcluded(Type t) =>
            _excluded!(t)
            || (t.IsArray && IsExcluded(t.GetElementType()!))
            || (t.IsGenericType && t.GetGenericArguments().Any(IsExcluded));

        public AccessorMode Mode { get; }

        internal bool HasAdapters => _adapters.Any;

        /// <summary>Union of the registered adapters' capabilities: adapter-free (or capability-free) paths are one check.</summary>
        internal AdapterCaps Caps => _adapters.Caps;

        private Func<object, bool>? _hostAlive;

        /// <summary>The engine's IsAlive hook; part of the one liveness definition (adapters first, then the hook).</summary>
        internal Func<object, bool>? HostAlive
        {
            get => _hostAlive;
            set { _hostAlive = value; _cache.Clear(); }
        }

        /// <summary>Any liveness source configured: the adapter-free, hook-free path is this one check.</summary>
        internal bool LivenessOn => (Caps & AdapterCaps.Liveness) != 0 || _hostAlive != null;

        /// <summary>The one liveness verdict: first adapter that answers, then the host hook, else alive.</summary>
        internal bool IsAlive(object o)
        {
            if ((Caps & AdapterCaps.Liveness) != 0)
            {
                foreach (var a in Info(o.GetType()).LivenessAdapters)
                {
                    var alive = a.IsAlive(o);
                    if (alive.HasValue) return alive.Value;
                }
            }
            return _hostAlive == null || _hostAlive(o);
        }

        /// <summary>A destroyed object (e.g. Unity's fake-null) reads as null everywhere (spec §13).</summary>
        // Only objects (class instances, not collections, maps, structs or scalars) have liveness, so host hooks are
        // only ever asked about host objects.
        internal bool IsDead(object o) => LivenessOn && !(o is string) && !o.GetType().IsPrimitive && Info(o.GetType()).IsObject && !IsAlive(o);

        /// <summary>Applied where values enter expressions (member reads, elements, sources): destroyed objects become null.</summary>
        internal object? Live(object? v) => v != null && LivenessOn && IsDead(v) ? null : v;

        /// <summary>
        /// Register a type adapter (spec §13). Must not be called on <see cref="Shared"/>; engines own a model per adapter set.
        /// Clears cached metadata, so register adapters before queries run.
        /// </summary>
        internal void AddAdapter(RqlTypeAdapter adapter)
        {
            if (ReferenceEquals(this, Shared)) throw new InvalidOperationException("the shared type model has no adapters");
            _adapters.Add(adapter);
            _cache.Clear();
        }

        /// <summary>The ordered adapters applying to a type, most specific first, with their rank (diagnostics for hosts).</summary>
        public IReadOnlyList<string> Explain(Type t) =>
            _adapters.For(t).Select(a => $"{a.Name} (target {FriendlyName(a.Target)}, rank {AdapterRegistry.Rank(a.Target, t)}, priority {a.Priority})").ToList();

        public static TypeInfo Get(Type t) => Shared.Info(t);

        private static readonly ConcurrentDictionary<Type, bool> ObjectKinds = new ConcurrentDictionary<Type, bool>();

        /// <summary>
        /// Declare that an enumerable type (and its subclasses) is an object with members, not a collection — e.g. Unity's
        /// <c>Transform</c>, which enumerates its children. Process-wide, because a value's kind must be the same for every
        /// engine; register at startup, before queries run (spec §13).
        /// </summary>
        public static void TreatAsObject(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!ObjectKinds.TryAdd(type, true)) return;
            lock (Models)
                foreach (var w in Models)
                    if (w.TryGetTarget(out var model)) model._cache.Clear();
        }

        private static bool IsTreatedAsObject(Type t)
        {
            if (ObjectKinds.IsEmpty) return false;
            for (var b = t; b != null; b = b.BaseType)
                if (ObjectKinds.ContainsKey(b)) return true;
            return false;
        }

        // Cached delegate: a method-group argument would allocate a new delegate on every lookup.
        public TypeInfo Info(Type t) => _cache.TryGetValue(t, out var info) ? info : _cache.GetOrAdd(t, _build);

        private static readonly HashSet<Type> ScalarTypes = new HashSet<Type>
        {
            typeof(string), typeof(char), typeof(bool), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset),
            typeof(TimeSpan), typeof(Guid), typeof(Uri),
        };

        public static bool IsScalarType(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u.IsPrimitive || u.IsEnum || ScalarTypes.Contains(u);
        }

        private TypeInfo Build(Type t)
        {
            var adapters = _adapters.For(t);
            var info = new TypeInfo(t)
            {
                ShortName = ShortName(t),
                FriendlyName = FriendlyName(t),
                Description = adapters.Select(a => a.Description).FirstOrDefault(d => d != null) ?? t.GetCustomAttribute<RqlDescriptionAttribute>()?.Text,
                AdapterArray = adapters,
                LiteralSchema = adapters.Select(a => a.LiteralSchema).FirstOrDefault(s => s != null),
                EncodeAdapters = With(adapters, AdapterCaps.Encode),
                CoerceAdapters = With(adapters, AdapterCaps.Coerce),
                EqualityAdapters = With(adapters, AdapterCaps.Equality),
                OrderingAdapters = With(adapters, AdapterCaps.Ordering),
                IdentityAdapters = With(adapters, AdapterCaps.Identity),
                LivenessAdapters = With(adapters, AdapterCaps.Liveness),
            };

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var b = t; b != null && b != typeof(object); b = b.BaseType) names.Add(ShortName(b));
            foreach (var i in t.GetInterfaces()) names.Add(ShortName(i));
            info.TypeNames = names;

            if (t.IsEnum) { info.Kind = TypeKind.Enum; return info; }
            if (IsScalarType(t)) { info.Kind = TypeKind.Scalar; return info; }

            // Hosts may declare an enumerable type to be an object (Unity's Transform enumerates its children).
            bool asObject = !typeof(IEnumerable).IsAssignableFrom(t) || IsTreatedAsObject(t);
            var dict = asObject ? null : FindGeneric(t, typeof(IDictionary<,>)) ?? FindGeneric(t, typeof(IReadOnlyDictionary<,>));
            if (asObject)
            {
                info.Kind = t.IsValueType ? TypeKind.Struct : TypeKind.Object;
            }
            else if (dict != null || typeof(IDictionary).IsAssignableFrom(t))
            {
                info.Kind = TypeKind.Map;
                info.KeyType = dict?.GetGenericArguments()[0];
                info.ElementType = dict?.GetGenericArguments()[1];
            }
            else
            {
                info.Kind = TypeKind.List;
                info.ElementType = t.IsArray ? t.GetElementType() : FindGeneric(t, typeof(IEnumerable<>))?.GetGenericArguments()[0];
            }

            if (info.Kind == TypeKind.List || info.Kind == TypeKind.Map)
            {
                var countProp = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)
                                ?? FindGeneric(t, typeof(IReadOnlyCollection<>))?.GetProperty("Count")
                                ?? FindGeneric(t, typeof(ICollection<>))?.GetProperty("Count");
                if (countProp != null && countProp.PropertyType == typeof(int) && countProp.GetIndexParameters().Length == 0)
                {
                    var g = MakeGetter(countProp);
                    info.CountGetter = o => (int)g(o)!;
                }
                // Collections expose no members: .Count/.Length are handled by the evaluator (spec §3).
                return info;
            }

            BuildMembers(info, t, adapters);
            return info;
        }

        private static RqlTypeAdapter[] With(RqlTypeAdapter[] adapters, AdapterCaps cap)
        {
            if (adapters.Length == 0) return adapters;
            var subset = adapters.Where(a => (a.Caps & cap) != 0).ToArray();
            return subset.Length == 0 ? Array.Empty<RqlTypeAdapter>() : subset;
        }

        /// <summary>Declaring types from the root base down to <paramref name="t"/> (declaration order of members).</summary>
        private static List<Type> Chain(Type t)
        {
            var chain = new List<Type>();
            for (var b = t; b != null && b != typeof(object) && b != typeof(ValueType); b = b.BaseType) chain.Add(b);
            chain.Reverse();
            return chain;
        }

        /// <summary>Field members of one declaring type (public, or non-public when <paramref name="include"/> is set).</summary>
        private void AddFields(Type decl, List<MemberAccessor> members, HashSet<string> seen, Func<FieldInfo, bool>? include, ref int order)
        {
            var flags = (include == null ? BindingFlags.Public : BindingFlags.NonPublic) | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var f in decl.GetFields(flags).OrderBy(f => f.MetadataToken))
            {
                if (f.IsDefined(typeof(RqlIgnoreAttribute)) || f.FieldType.IsByRefLike || IsDelegate(f.FieldType)) continue;
                // Compiler-generated backing fields ("<Name>k__BackingField") are never members.
                if (include != null && (f.Name.IndexOf('<') >= 0 || !include(f))) continue;
                if (!seen.Add(f.Name)) continue;
                bool canWrite = !f.IsInitOnly && !f.IsLiteral && !f.IsDefined(typeof(RqlReadOnlyAttribute));
                members.Add(new MemberAccessor(f, f.FieldType, MakeGetter(f), canWrite, !f.IsDefined(typeof(RqlReferenceAttribute)), order++));
            }
        }

        private void BuildMembers(TypeInfo info, Type t, RqlTypeAdapter[] adapters)
        {
            var chain = Chain(t);
            var members = new List<MemberAccessor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int order = 0;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            foreach (var decl in chain)
            {
                AddFields(decl, members, seen, null, ref order);
                foreach (var p in decl.GetProperties(flags).OrderBy(p => p.MetadataToken))
                {
                    if (p.GetIndexParameters().Length > 0 || p.GetMethod == null || !p.GetMethod.IsPublic) continue;
                    if (p.IsDefined(typeof(RqlIgnoreAttribute)) || p.PropertyType.IsByRefLike || IsDelegate(p.PropertyType)) continue;
                    // Overridden/new properties: keep the most derived accessor, but the base declaration order.
                    var most = t.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance) ?? p;
                    if (!seen.Add(p.Name)) continue;
                    bool canWrite = most.SetMethod != null && most.SetMethod.IsPublic && !most.IsDefined(typeof(RqlReadOnlyAttribute));
                    members.Add(new MemberAccessor(most, most.PropertyType, MakeGetter(most), canWrite, !most.IsDefined(typeof(RqlReferenceAttribute)), order++));
                }
            }

            string? identityName = null, displayName = null;
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.IsDefined(typeof(RqlCallableAttribute)) && !m.IsGenericMethodDefinition)
                .Select(m => CallableMethod.FromReflection(m, m.GetCustomAttribute<RqlCallableAttribute>()?.Description))
                .ToList();
            if (_excluded != null) members.RemoveAll(m => IsExcluded(m.Type));
            if (adapters.Length > 0)
            {
                var table = new MemberTable(t, members, methods, IncludeNonPublicFields);
                for (int i = adapters.Length - 1; i >= 0; i--) adapters[i].ConfigureMembers(table);
                info.WritesThrough = table.IsWritesThrough;
                if (table.IdentityName != null) identityName = table.IdentityName;
                if (table.DisplayName != null) displayName = table.DisplayName;
            }

            // Liveness is applied once, at member reads: a destroyed value reads as null for every consumer (spec §13).
            if (LivenessOn)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    if (TypeModel.IsScalarType(m.Type) || (m.Type.IsValueType && Nullable.GetUnderlyingType(m.Type) == null)) continue;
                    var get = m.Getter;
                    members[i] = m.With(getter: o => Live(get(o)));
                }
            }

            info.Members = members;
            info.ImplicitMembers = members.Any(m => m.ExplicitOnly) ? members.Where(m => !m.ExplicitOnly).ToArray() : (IReadOnlyList<MemberAccessor>)members;
            info.ByName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var ci = new Dictionary<string, MemberAccessor?>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members) ci[m.Name] = ci.ContainsKey(m.Name) ? null : m;
            info.ByNameCI = ci;

            // Identity and display are read implicitly (every $ref and summary), so never explicit-only members (spec §13).
            info.IdMember = identityName != null
                ? members.FirstOrDefault(m => !m.ExplicitOnly && m.Name == identityName)
                : members.FirstOrDefault(m => !m.ExplicitOnly && m.IsDefined(typeof(RqlIdAttribute)))
                  ?? members.FirstOrDefault(m => !m.ExplicitOnly && m.Name == "Id" && IsIdType(m.Type));
            // Stable identities never change through RQL (refs would silently break).
            if (info.IdMember != null && info.IdMember.CanWrite)
            {
                var id = info.IdMember;
                var ro = id.With(canWrite: false);
                members[members.IndexOf(id)] = ro;
                if (!ReferenceEquals(info.ImplicitMembers, members))
                    info.ImplicitMembers = info.ImplicitMembers.Select(m => m == id ? ro : m).ToArray();
                info.ByName[ro.Name] = ro;
                info.ByNameCI[ro.Name] = info.ByNameCI.TryGetValue(ro.Name, out var prev) && prev == null ? null : ro;
                info.IdMember = ro;
            }
            info.DisplayMember = displayName != null
                ? members.FirstOrDefault(m => !m.ExplicitOnly && m.Name == displayName)
                : members.FirstOrDefault(m => !m.ExplicitOnly && m.IsDefined(typeof(RqlDisplayAttribute)))
                  ?? members.FirstOrDefault(m => !m.ExplicitOnly && m.Name == "Name" && m.Type == typeof(string));
            info.Callable = methods.OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.Parameters.Count).ToArray();
        }

        /// <summary>Adds non-public instance fields of the type chain matching <paramref name="include"/> (MemberTable.IncludeFields).</summary>
        private void IncludeNonPublicFields(Type t, List<MemberAccessor> members, Func<FieldInfo, bool> include)
        {
            var seen = new HashSet<string>(members.Select(m => m.Name), StringComparer.Ordinal);
            int order = members.Count == 0 ? 0 : members.Max(m => m.Order) + 1;
            foreach (var decl in Chain(t)) AddFields(decl, members, seen, include, ref order);
        }

        /// <summary>The loadable types of an assembly (partial on load failures, which <paramref name="onError"/> reports).</summary>
        /// <summary>The types of an assembly that load (a broken type does not hide the rest of the assembly).</summary>
        public static Type[] LoadableTypes(Assembly asm, Action<string>? onError = null)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                onError?.Invoke($"{asm.GetName().Name}: some types failed to load ({ex.LoaderExceptions.FirstOrDefault()?.Message})");
                return ex.Types.Where(t => t != null).ToArray()!;
            }
            catch (Exception ex)
            {
                onError?.Invoke($"{asm.GetName().Name}: {ex.Message}");
                return Type.EmptyTypes;
            }
        }

        private static bool IsIdType(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u == typeof(int) || u == typeof(long) || u == typeof(string) || u == typeof(Guid) || u == typeof(uint) || u == typeof(ulong);
        }

        private static bool IsDelegate(Type t) => typeof(Delegate).IsAssignableFrom(t);

        // Compiled getters depend only on the member, so every model (one per engine with adapters) shares them.
        private static readonly ConcurrentDictionary<MemberInfo, Func<object, object?>> CompiledGetters =
            new ConcurrentDictionary<MemberInfo, Func<object, object?>>();

        private Func<object, object?> MakeGetter(MemberInfo m)
        {
            if (Mode == AccessorMode.Compiled) return CompiledGetters.GetOrAdd(m, Compile);
            return ReflectionGetter(m);
        }

        private static Func<object, object?> Compile(MemberInfo m)
        {
            try
            {
                var obj = Expression.Parameter(typeof(object), "o");
                var decl = m.DeclaringType!;
                Expression target = decl.IsValueType ? Expression.Unbox(obj, decl) : (Expression)Expression.Convert(obj, decl);
                Expression access = m is PropertyInfo p ? Expression.Property(target, p) : Expression.Field(target, (FieldInfo)m);
                var body = Expression.Convert(access, typeof(object));
                return Expression.Lambda<Func<object, object?>>(body, obj).Compile();
            }
            catch (PlatformNotSupportedException) { }
            catch (NotSupportedException) { }
            catch (InvalidOperationException) { }
            return ReflectionGetter(m);
        }

        private static Func<object, object?> ReflectionGetter(MemberInfo m)
        {
            if (m is PropertyInfo pi)
            {
                return o =>
                {
                    try { return pi.GetValue(o); }
                    catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
                };
            }
            var fi = (FieldInfo)m;
            return o => fi.GetValue(o);
        }

        internal static Type? FindGeneric(Type t, Type generic)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == generic) return t;
            foreach (var i in t.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == generic) return i;
            }
            return null;
        }

        public static string ShortName(Type t)
        {
            if (t.IsArray) return ShortName(t.GetElementType()!) + "[]";
            var n = t.Name;
            int tick = n.IndexOf('`');
            return tick >= 0 ? n.Substring(0, tick) : n;
        }

        private static readonly Dictionary<Type, string> Aliases = new Dictionary<Type, string>
        {
            [typeof(int)] = "int", [typeof(long)] = "long", [typeof(short)] = "short", [typeof(byte)] = "byte",
            [typeof(uint)] = "uint", [typeof(ulong)] = "ulong", [typeof(ushort)] = "ushort", [typeof(sbyte)] = "sbyte",
            [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal", [typeof(bool)] = "bool",
            [typeof(string)] = "string", [typeof(char)] = "char", [typeof(object)] = "object",
        };

        /// <summary>C#-like readable type name: List&lt;Component&gt;, int?, Vector3.</summary>
        public static string FriendlyName(Type t)
        {
            if (Aliases.TryGetValue(t, out var a)) return a;
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) return FriendlyName(u) + "?";
            if (t.IsArray) return FriendlyName(t.GetElementType()!) + "[]";
            // Virtual maps read like any other string-keyed map in schemas (spec §13).
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(VirtualMap<>))
                return "Dictionary<string, " + FriendlyName(t.GetGenericArguments()[0]) + ">";
            if (t.IsGenericType)
            {
                var args = string.Join(", ", t.GetGenericArguments().Select(FriendlyName));
                return ShortName(t) + "<" + args + ">";
            }
            return t.Name;
        }
    }
}
