using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Rql.Execution
{
    public sealed class SourceRegistration
    {
        internal SourceRegistration(string name, Func<object?> getter, string? description)
        {
            Name = name; Getter = getter; Description = description;
        }
        public string Name { get; }
        public Func<object?> Getter { get; }
        public string? Description { get; }
    }

    /// <summary>Per-item evaluator handed to extension stages: evaluates an argument expression with $ = item.</summary>
    public delegate object? ArgEvaluator(object? item);

    public delegate IEnumerable<object?> StageHandler(IEnumerable<object?> input, IReadOnlyList<ArgEvaluator> args, IReadOnlyDictionary<string, ArgEvaluator> named);

    /// <summary>One change made (or about to be made) by <c>set</c> or <c>call</c>, as seen by host hooks.</summary>
    public sealed class MutationRecord
    {
        internal MutationRecord(object item, object owner, string member, object? oldValue, object? newValue, CallableMethod? method)
        {
            Item = item; Owner = owner; Member = member; Old = oldValue; New = newValue; Method = method;
        }

        /// <summary>The mutation target (a stream item).</summary>
        public object Item { get; }
        /// <summary>
        /// The object whose state changes: the last reference object on the written path (for <c>set Renderer.sharedMaterial.color</c>
        /// it is the Material, not the Renderer). Hosts mark it dirty / record it for undo.
        /// </summary>
        public object Owner { get; }
        /// <summary>The written path (<c>set</c>) or the method signature (<c>call</c>).</summary>
        public string Member { get; }
        public object? Old { get; }
        public object? New { get; }
        /// <summary>The invoked method for <c>call</c>; null for <c>set</c>.</summary>
        public CallableMethod? Method { get; }
    }

    public sealed class MutationPolicy
    {
        /// <summary>
        /// Runs after planning and before anything is written (not on dry-run): e.g. Undo.RecordObjects on the owners.
        /// Throwing vetoes the whole mutation (→ permission-denied, nothing changed).
        /// </summary>
        public Action<IReadOnlyList<MutationRecord>>? BeforeWrite { get; set; }

        /// <summary>
        /// Runs after every write succeeded (not on dry-run or rollback): e.g. EditorUtility.SetDirty on the owners.
        /// An exception becomes a getter-threw warning; the writes stay applied.
        /// </summary>
        public Action<IReadOnlyList<MutationRecord>>? Committed { get; set; }

        /// <summary>Host-level switch. Requests must additionally set <see cref="QueryOptions.AllowMutation"/>.</summary>
        public bool Enabled { get; set; }
        /// <summary>Optional extra allow-check per (declaring object type, member).</summary>
        public Func<Type, MemberAccessor, bool>? CanWrite { get; set; }
        /// <summary>Default per-request target limit (requests may raise it up to <see cref="HardMaxTargets"/>).</summary>
        public int MaxTargets { get; set; } = 20;
        /// <summary>Host ceiling no request can exceed.</summary>
        public int HardMaxTargets { get; set; } = 1000;
    }

    public sealed class EngineDefaults
    {
        public int MaxVisited { get; set; } = 1_000_000;
        public int MaxRows { get; set; } = 50;
        public int MaxListItems { get; set; } = 20;
        public int MaxDepth { get; set; } = 16;
        public int TimeoutMs { get; set; } = 2_000;
        public int MaxOutputBytes { get; set; } = 65_536;
        public int DefaultRecurseDepth { get; set; } = 8;
        /// <summary>Hard caps applied after request budgets.</summary>
        public int HardMaxVisited { get; set; } = 5_000_000;
        public int HardMaxRows { get; set; } = 10_000;
        public int HardTimeoutMs { get; set; } = 30_000;
    }

    /// <summary>
    /// Host-facing RQL runtime over live CLR objects. Thread-safe for concurrent queries as long as the
    /// host's object graph is not mutated concurrently.
    /// </summary>
    public sealed class RqlEngine
    {
        private readonly Dictionary<string, SourceRegistration> _sources = new Dictionary<string, SourceRegistration>(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<object?[], object?>> _functions = new Dictionary<string, Func<object?[], object?>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _functionDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, StageHandler> _stages = new Dictionary<string, StageHandler>(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<object, object> _handles = new ConditionalWeakTable<object, object>();
        private readonly Dictionary<long, WeakReference> _handleTargets = new Dictionary<long, WeakReference>();
        private long _nextHandle;

        public RqlEngine(AccessorMode accessors = AccessorMode.Compiled)
        {
            Types = accessors == AccessorMode.Compiled ? TypeModel.Shared : new TypeModel(accessors);
        }

        public TypeModel Types { get; private set; }

        /// <summary>
        /// Never expose members whose type (or element / generic argument type) matches <paramref name="excluded"/>,
        /// e.g. <c>t => t.Assembly.GetName().Name == "Unity.TextMeshPro"</c> (spec §13). Configure before queries run.
        /// </summary>
        public RqlEngine ExcludeTypes(Func<Type, bool> excluded)
        {
            if (excluded == null) throw new ArgumentNullException(nameof(excluded));
            if (ReferenceEquals(Types, TypeModel.Shared)) Types = new TypeModel(Types.Mode);
            Types.ExcludeTypes(excluded);
            _knownMembers = null;
            return this;
        }

        /// <summary>
        /// Configure every <see cref="IRqlModule"/> found in <paramref name="assemblies"/> (public, concrete, parameterless),
        /// ordered by <see cref="IRqlModule.Order"/> then full name. A module (or assembly) that fails is reported and skipped;
        /// the others still configure. <paramref name="skipAssembly"/> prunes assemblies from the scan (e.g. engine / BCL ones).
        /// </summary>
        public IReadOnlyList<RqlModuleReport> AddModulesFrom(IEnumerable<System.Reflection.Assembly> assemblies, Func<System.Reflection.Assembly, bool>? skipAssembly = null)
        {
            var reports = ModuleDiscovery.Configure(this, assemblies, skipAssembly);
            _modules.AddRange(reports);
            return reports;
        }

        /// <summary>
        /// Configure the given <see cref="IRqlModule"/> types (same rules as <see cref="AddModulesFrom"/>) — for hosts that
        /// already index types (e.g. Unity's TypeCache) and need no assembly scan.
        /// </summary>
        public IReadOnlyList<RqlModuleReport> AddModules(IEnumerable<Type> moduleTypes)
        {
            var reports = ModuleDiscovery.Configure(this, moduleTypes, new List<RqlModuleReport>());
            _modules.AddRange(reports);
            return reports;
        }

        private readonly List<RqlModuleReport> _modules = new List<RqlModuleReport>();

        /// <summary>Every module configured on this engine so far, with failures.</summary>
        public IReadOnlyList<RqlModuleReport> Modules => _modules;

        /// <summary>
        /// Register a type adapter (spec §13): virtual/hidden members, methods, maps, encoding, coercion, comparison, identity,
        /// liveness. Register adapters before running queries; resolution order is deterministic (see <see cref="TypeModel.Explain"/>).
        /// </summary>
        public RqlEngine AddAdapter(RqlTypeAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            if (ReferenceEquals(Types, TypeModel.Shared)) Types = new TypeModel(Types.Mode);
            Types.AddAdapter(adapter);
            _knownMembers = null;
            return this;
        }

        public EngineDefaults Defaults { get; } = new EngineDefaults();
        public MutationPolicy Mutations { get; } = new MutationPolicy();
        public string RuntimeName { get; set; } = "dotnet";

        /// <summary>Liveness check for explicitly destroyed objects (Unity/Unreal style). Null = always alive.</summary>
        public Func<object, bool>? IsAlive
        {
            get => Types.HostAlive;
            set
            {
                if (ReferenceEquals(Types, TypeModel.Shared)) Types = new TypeModel(Types.Mode);
                Types.HostAlive = value;
            }
        }

        /// <summary>Resolve "Type#id" (type short name, id string). Null result = not found. When unset, sources are searched.</summary>
        public Func<string, string, object?>? ResolveStableRef { get; set; }

        /// <summary>When false, $path tracking is computed only for plans that reference $path (default optimization).</summary>
        public bool AlwaysTrackPaths { get; set; }

        /// <summary>Disable the sort+first top-N optimization (benchmark ablation only).</summary>
        public bool DisableTopN { get; set; }

        public IReadOnlyDictionary<string, SourceRegistration> Sources => _sources;
        public IReadOnlyDictionary<string, Func<object?[], object?>> Functions => _functions;
        public IReadOnlyDictionary<string, StageHandler> Stages => _stages;

        public RqlEngine AddSource(string name, Func<object?> getter, string? description = null)
        {
            ValidateName(name, allowDots: true);
            _sources[name] = new SourceRegistration(name, getter, description);
            _knownMembers = null;
            return this;
        }

        private HashSet<string>? _knownMembers;

        /// <summary>Every member name of every type statically reachable from the sources (case-insensitive), cached.</summary>
        internal HashSet<string> KnownMemberNames()
        {
            var known = _knownMembers;
            if (known != null) return known;
            known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "key", "value", "count", "Count", "Length" };
            var roots = new List<Type>();
            foreach (var s in _sources.Values)
            {
                try { var v = s.Getter(); if (v != null) roots.Add(v.GetType()); } catch { }
            }
            foreach (var t in Describe.Reachable(Types, roots, 1000))
                foreach (var m in Types.Info(t).Members) known.Add(m.Name);
            return _knownMembers = known;
        }

        /// <summary>Register a namespaced host function (<c>ns.fn(args)</c>); it may also start a query (spec §2).</summary>
        public RqlEngine AddFunction(string name, Func<object?[], object?> fn, string? description = null)
        {
            ValidateName(name, allowDots: true);
            if (name.IndexOf('.') < 0) throw new ArgumentException("extension functions must be namespaced (ns.name)", nameof(name));
            _functions[name] = fn;
            if (description != null) _functionDescriptions[name] = description;
            else _functionDescriptions.Remove(name);
            return this;
        }

        /// <summary>Descriptions of the host functions that have one (for schema / help output).</summary>
        public IReadOnlyDictionary<string, string> FunctionDescriptions => _functionDescriptions;

        private readonly List<KeyValuePair<string, string>> _guides = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Add agent-facing guidance (markdown) under a topic, e.g. how a module's sources and types are queried. Hosts render
        /// the guides into their help / skill files; a later guide with the same topic replaces the earlier one.
        /// </summary>
        public RqlEngine AddGuide(string topic, string markdown)
        {
            if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("topic required", nameof(topic));
            _guides.RemoveAll(g => string.Equals(g.Key, topic, StringComparison.Ordinal));
            _guides.Add(new KeyValuePair<string, string>(topic, markdown ?? ""));
            return this;
        }

        /// <summary>Registered guides (topic → markdown) in registration order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Guides => _guides;

        public RqlEngine AddStage(string name, StageHandler handler)
        {
            ValidateName(name, allowDots: true);
            if (name.IndexOf('.') < 0) throw new ArgumentException("extension stages must be namespaced (ns.name)", nameof(name));
            _stages[name] = handler;
            return this;
        }

        private static void ValidateName(string name, bool allowDots)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("name required");
            foreach (var part in name.Split('.'))
            {
                if (part.Length == 0 || !Parsing.Lexer.IsIdentStart(part[0]) || (!allowDots && name.Contains(".")))
                    throw new ArgumentException($"invalid name '{name}'");
            }
        }

        // ------------------------------------------------------------ execution

        public QueryResult Execute(string text, Budgets? budgets = null, QueryOptions? options = null)
        {
            var sw = Stopwatch.StartNew();
            QueryPlan plan;
            try { plan = RqlParser.Parse(text); }
            catch (RqlException ex)
            {
                var fail = QueryResult.Fail(new[] { ex.ToDiagnostic() });
                fail.Stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                return fail;
            }
            var r = Execute(plan, budgets, options);
            r.Stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        public QueryResult Execute(QueryPlan plan, Budgets? budgets = null, QueryOptions? options = null)
        {
            var x = new Exec(this, budgets ?? new Budgets(), options ?? new QueryOptions(), plan);
            QueryResult result;
            try
            {
                result = new Pipeline(x).Run();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Last line of defence: untrusted queries must never crash the host with a raw exception.
                result = QueryResult.Fail(new[] { Diagnostic.Error("internal-error", $"{ex.GetType().Name}: {ex.Message}", "this is an RQL runtime bug (or a host type adapter threw); please report the query") });
            }
            result.Stats.Visited = x.Visited;
            result.Stats.MembersRead = x.MembersRead;
            result.Stats.ElapsedMs = x.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Host API: run a query without a terminal stage and return the LIVE result objects (no encoding),
        /// e.g. for adapters that hand objects to existing tooling. Read-only; budgets apply.
        /// </summary>
        public IReadOnlyList<object?> EvaluateLive(string text, out IReadOnlyList<Diagnostic> diagnostics, Budgets? budgets = null)
        {
            QueryPlan plan;
            try { plan = RqlParser.Parse(text); }
            catch (RqlException ex)
            {
                diagnostics = new[] { ex.ToDiagnostic() };
                return Array.Empty<object?>();
            }
            var x = new Exec(this, budgets ?? new Budgets(), new QueryOptions(), plan);
            var items = new Pipeline(x).LiveItems(x.MaxRows, out var diags);
            diagnostics = diags;
            return items;
        }

        // ------------------------------------------------------------ references

        /// <summary>Stable reference string for an object: "Type#id" or session handle "Type@n".</summary>
        public string RefOf(object o)
        {
            var info = Types.Info(o.GetType());
            var id = StableIdOf(o, info);
            if (id != null) return info.ShortName + "#" + id;
            return info.ShortName + "@" + HandleOf(o).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The "#id" part of a stable reference: adapter identity first, then the id member; null = none.</summary>
        private static string? StableIdOf(object o, TypeInfo info)
        {
            foreach (var a in info.IdentityAdapters)
            {
                var sid = a.StableId(o);
                if (sid != null) return sid;
            }
            if (info.IdMember == null) return null;
            object? id;
            try { id = info.IdMember.Getter(o); } catch { id = null; }
            return id == null ? null : Convert.ToString(id, CultureInfo.InvariantCulture);
        }

        /// <summary>Liveness: the first adapter verdict, else the host <see cref="IsAlive"/> hook, else alive.</summary>
        internal bool Alive(object o) => Types.IsAlive(o);

        private long HandleOf(object o)
        {
            lock (_handleTargets)
            {
                if (_handles.TryGetValue(o, out var h)) return (long)h;
                long id = ++_nextHandle;
                _handles.Add(o, id);
                _handleTargets[id] = new WeakReference(o);
                if ((id & 4095) == 0)
                {
                    // Drop entries whose objects were collected so the table does not grow forever.
                    foreach (var dead in _handleTargets.Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key).ToList()) _handleTargets.Remove(dead);
                }
                return id;
            }
        }

        /// <summary>Type descriptors of everything reachable from the registered sources (schema tool).</summary>
        public List<System.Text.Json.Nodes.JsonObject> DescribeTypes()
        {
            var roots = new List<Type>();
            foreach (var s in _sources.Values)
            {
                object? v;
                try { v = s.Getter(); } catch { continue; }
                if (v != null) roots.Add(v.GetType());
            }
            return Describe.Reachable(Types, roots).Select(t => Describe.TypeDescriptor(Types, t, Mutations.Enabled)).ToList();
        }

        internal object? ResolveRef(string reference, out string? error, Exec? x = null)
        {
            error = null;
            reference = reference.Trim();
            int i = reference.IndexOfAny(new[] { '#', '@' });
            if (i <= 0 || i == reference.Length - 1)
            {
                error = $"malformed reference '{reference}' (expected Type#id or Type@handle)";
                return null;
            }
            var type = reference.Substring(0, i);
            var id = reference.Substring(i + 1);
            // Ids may contain spaces / parentheses (asset paths), so the exact id is preferred; copied display forms such as
            // "Enemy#12(Goblin_0012)" or "Enemy#12 Goblin" still resolve through the id up to the first space / "(".
            int cut = id.IndexOfAny(new[] { ' ', '(', '\t' });
            string? display = cut > 0 ? id.Substring(0, cut) : null;
            object? found = null;
            if (reference[i] == '@')
            {
                if (long.TryParse(display ?? id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                {
                    lock (_handleTargets)
                    {
                        if (_handleTargets.TryGetValue(h, out var wr)) found = wr.Target;
                    }
                }
            }
            else if (ResolveStableRef != null)
            {
                found = ResolveStableRef(type, id) ?? (display != null ? ResolveStableRef(type, display) : null);
            }
            else
            {
                found = SearchSources(type, id, display, x);
            }

            if (found == null || !Types.Info(found.GetType()).IsA(type))
            {
                error = $"reference '{reference}' does not resolve to a live object";
                return null;
            }
            if (!Alive(found))
            {
                error = $"reference '{reference}' points to a destroyed object";
                return null;
            }
            return found;
        }

        /// <summary>
        /// Fallback resolution: one bounded walk over owning members of all sources, preferring an exact id match over a
        /// display-form match. Nodes count toward the calling query's budgets; results are cached per query.
        /// </summary>
        private object? SearchSources(string type, string id, string? display, Exec? x)
        {
            var key = type + "#" + id;
            if (x != null && x.RefCache.TryGetValue(key, out var cached)) return cached;
            var found = SearchSourcesCore(type, id, display, x);
            if (x != null) x.RefCache[key] = found;
            return found;
        }

        private object? SearchSourcesCore(string type, string id, string? display, Exec? x)
        {
            object? displayMatch = null;
            var visited = new HashSet<object>(RefEq.Instance);
            var stack = new Stack<(object, int)>();
            foreach (var s in _sources.Values)
            {
                var v = s.Getter();
                if (v != null) stack.Push((v, 0));
            }
            int budget = Defaults.MaxVisited;
            while (stack.Count > 0 && budget-- > 0)
            {
                x?.Visit();
                var (o, depth) = stack.Pop();
                if (!visited.Add(o)) continue;
                var info = Types.Info(o.GetType());
                if (info.IsList || info.IsMap)
                {
                    foreach (var e in V.Enumerate(o))
                    {
                        var ev = e is MapEntry me ? me.Value : e;
                        if (ev != null && !TypeModel.IsScalarType(ev.GetType())) stack.Push((ev, depth));
                    }
                    continue;
                }
                if (!info.IsObject) continue;
                if (info.IsA(type))
                {
                    var sid = StableIdOf(o, info);
                    if (sid == id) return o;
                    if (display != null && displayMatch == null && sid == display) displayMatch = o;
                }
                if (depth >= 32) continue;
                foreach (var m in info.ImplicitMembers)
                {
                    if (!m.Owning || TypeModel.IsScalarType(m.Type)) continue;
                    object? v;
                    try { v = m.Getter(o); } catch { continue; }
                    if (v != null && !v.GetType().IsValueType) stack.Push((v, depth + 1));
                }
            }
            return displayMatch;
        }
    }

    /// <summary>Per-query execution state.</summary>
    internal sealed class Exec
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public Exec(RqlEngine engine, Budgets budgets, QueryOptions options, QueryPlan plan)
        {
            Engine = engine;
            Types = engine.Types;
            Options = options;
            Plan = plan;
            var d = engine.Defaults;
            MaxVisited = Math.Min(budgets.MaxVisited ?? d.MaxVisited, d.HardMaxVisited);
            MaxRows = Math.Min(budgets.MaxRows ?? d.MaxRows, d.HardMaxRows);
            MaxListItems = budgets.MaxListItems ?? d.MaxListItems;
            MaxDepth = budgets.MaxDepth ?? d.MaxDepth;
            TimeoutMs = Math.Min(budgets.TimeoutMs ?? d.TimeoutMs, d.HardTimeoutMs);
            MaxOutputBytes = budgets.MaxOutputBytes ?? d.MaxOutputBytes;
            // Requests may change the per-request limit, but never beyond the host's hard ceiling.
            MaxMutationTargets = Math.Min(budgets.MaxMutationTargets ?? engine.Mutations.MaxTargets, engine.Mutations.HardMaxTargets);
        }

        public RqlEngine Engine { get; }
        public TypeModel Types { get; }

        private RqlKernel? _kernel;

        /// <summary>The kernel for this execution's writes: the engine's types and the host's write policy.</summary>
        public RqlKernel Kernel => _kernel ??= new RqlKernel(Types, Engine.Mutations.CanWrite);

        public QueryOptions Options { get; }
        public QueryPlan Plan { get; }
        public int MaxVisited { get; }
        public int MaxRows { get; }
        public int MaxListItems { get; }
        public int MaxDepth { get; }
        public int TimeoutMs { get; }
        public int MaxOutputBytes { get; }
        public int MaxMutationTargets { get; }
        public bool TrackPaths { get; set; }

        // evaluation scope (mutable, saved/restored by nested scopes)
        public object? Current;
        public PathNode? CurrentPath;
        public int CurrentDepth = -1;
        /// <summary>Enclosing items while evaluating collection-method arguments (scope chain).</summary>
        public readonly List<object?> Outer = new List<object?>();
        public readonly List<IsSite> IsSites = new List<IsSite>();
        public readonly Dictionary<string, object?> RefCache = new Dictionary<string, object?>(StringComparer.Ordinal);
        public long Index;

        public long Visited;
        public long MembersRead;
        public int GetterErrors;
        public string? FirstGetterError;
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public readonly List<MemberSite> Sites = new List<MemberSite>();

        public TimeSpan Elapsed => _sw.Elapsed;

        public void Visit()
        {
            if (++Visited > MaxVisited)
                throw new RqlException("budget-exceeded", $"visited more than {MaxVisited} objects", null,
                    "narrow the source (e.g. a specific scene), filter earlier, lower recurse max-depth, or raise budgets.maxVisited");
            if ((Visited & 1023) == 0) CheckTime();
        }

        public void CheckTime()
        {
            if (_sw.ElapsedMilliseconds > TimeoutMs)
                throw new RqlException("timeout", $"query exceeded {TimeoutMs} ms", null, "narrow the query or raise budgets.timeoutMs");
        }

        public void GetterThrew(string member, Exception ex)
        {
            if (GetterErrors++ == 0) FirstGetterError = $"getter '{member}' threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Statistics of one `x is T` test, used for the no-match diagnostic.</summary>
    internal sealed class IsSite
    {
        public IsSite(string typeName, TextSpan? span) { TypeName = typeName; Span = span; }
        public string TypeName { get; }
        public TextSpan? Span { get; }
        public long Evaluated;
        public long Matched;
        public readonly HashSet<TypeInfo> Seen = new HashSet<TypeInfo>();
    }

    /// <summary>Linked navigation path; rendered lazily for $path.</summary>
    internal sealed class PathNode
    {
        public PathNode(PathNode? parent, string text) { Parent = parent; Text = text; }
        public PathNode? Parent { get; }
        public string Text { get; }

        public PathNode Member(string name) => new PathNode(this, "." + name);
        public PathNode At(long i) => new PathNode(this, "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        public PathNode Key(object? key) => new PathNode(this, "[" + (key is string s ? "\"" + s.Replace("\"", "\\\"") + "\"" : Convert.ToString(key, CultureInfo.InvariantCulture)) + "]");

        public override string ToString()
        {
            var parts = new List<string>();
            for (var n = this; n != null; n = n.Parent) parts.Add(n.Text);
            parts.Reverse();
            return string.Concat(parts);
        }
    }

    /// <summary>Per-path-segment statistics used for unknown-member diagnostics and a monomorphic inline cache.</summary>
    internal sealed class MemberSite
    {
        public MemberSite(string name, TextSpan? span) { Name = name; Span = span; }
        public string Name { get; }
        public TextSpan? Span { get; }
        public long Hits;
        public long Misses;
        public readonly HashSet<TypeInfo> MissTypes = new HashSet<TypeInfo>();
        public string[]? RowColumns;
        /// <summary>First segment of a relative bare path (eligible for the static never-evaluated check).</summary>
        public bool Bare;
        public Type? CachedType;
        public MemberAccessor? CachedMember;
    }
}
