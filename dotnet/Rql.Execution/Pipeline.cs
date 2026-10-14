using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Rql.Execution
{
    internal readonly struct Item
    {
        public Item(object? value, PathNode? path, int depth = -1) { Value = value; Path = path; Depth = depth; }
        public object? Value { get; }
        public PathNode? Path { get; }
        /// <summary>Depth within the most recent recurse stage (0 = start item); -1 outside recurse.</summary>
        public int Depth { get; }
    }

    /// <summary>Projected row flowing between stages after `select`; members are the column names.</summary>
    internal sealed class RowValue
    {
        public RowValue(string[] columns, object?[] values) { Columns = columns; Values = values; }
        public string[] Columns { get; }
        public object?[] Values { get; }

        public int IndexOf(string name)
        {
            for (int i = 0; i < Columns.Length; i++) if (string.Equals(Columns[i], name, StringComparison.Ordinal)) return i;
            for (int i = 0; i < Columns.Length; i++) if (string.Equals(Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    internal sealed class Pipeline
    {
        private readonly Exec _x;
        private readonly Compiler _c;

        public Pipeline(Exec x)
        {
            _x = x;
            _c = new Compiler(x);
            x.TrackPaths = x.Engine.AlwaysTrackPaths || PlanScan.UsesMeta(x.Plan, "path");
        }

        public QueryResult Run()
        {
            QueryResult result;
            try
            {
                result = RunCore();
            }
            catch (RqlException ex)
            {
                result = QueryResult.Fail(_x.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Code != ex.Code));
                result.Diagnostics.Insert(0, ex.ToDiagnostic());
                AddWarnings(result, onlyGeneral: true);
                return result;
            }
            if (_x.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                var fail = QueryResult.Fail(_x.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).GroupBy(d => d.Message).Select(g => g.First()));
                return fail;
            }
            AddWarnings(result, onlyGeneral: false);
            return result;
        }

        private void AddWarnings(QueryResult result, bool onlyGeneral)
        {
            foreach (var d in _x.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning)) result.Diagnostics.Add(d);
            if (_x.GetterErrors > 0)
                result.Diagnostics.Add(Diagnostic.Warning("getter-threw", $"{_x.GetterErrors} member read(s) failed; first: {_x.FirstGetterError}"));
            if (onlyGeneral) return;
            foreach (var site in _x.IsSites)
            {
                if (site.Matched > 0 || site.Evaluated == 0) continue;
                var holders = Holders(site.TypeName);
                string hint = holders.Count > 0
                    ? $"{site.TypeName} objects are reached via {string.Join(", ", holders)}"
                    : $"no reachable type is named '{site.TypeName}'; use rql_schema / describe to list types";
                result.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "no-match",
                    $"none of the {site.Evaluated} tested item(s) is a {site.TypeName} (saw {string.Join(", ", site.Seen.Select(t => t.ShortName))})", site.Span, hint));
            }
            var reported = new HashSet<string>();
            // Static check for never-evaluated bare names (short-circuit `and`/`or`, empty streams):
            // a name that no reachable type declares and no select/group-by produces is certainly a typo.
            HashSet<string>? known = null;
            foreach (var site in _x.Sites)
            {
                if (!site.Bare || site.Hits > 0 || site.Misses > 0 || reported.Contains(site.Name)) continue;
                known ??= KnownNames();
                if (known.Contains(site.Name)) continue;
                reported.Add(site.Name);
                var close = Suggest.Closest(site.Name, known.ToList(), 3);
                result.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-member",
                    $"no type reachable from the sources has a member '{site.Name}' (not evaluated)", site.Span,
                    close.Count > 0 ? "did you mean " + string.Join(" / ", close) + "?" : $"if '{site.Name}' is a value, quote it: \"{site.Name}\""));
            }
            foreach (var site in _x.Sites)
            {
                if (site.Hits > 0 || site.Misses == 0 || !reported.Add(site.Name)) continue;
                if (site.RowColumns != null && site.MissTypes.Count == 0)
                {
                    result.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-member",
                        $"no column '{site.Name}' in the projected rows (columns: {string.Join(", ", site.RowColumns)})", site.Span,
                        "select produces rows with only the listed columns; to navigate into objects use the path in the source or `get Path` (e.g. world.LocalPlayer.Inventory.Slots | select Item.Name)"));
                    continue;
                }
                var types = site.MissTypes.ToList();
                var typeNames = string.Join(", ", types.Select(t => t.ShortName).Distinct().Take(4));
                var candidates = types.SelectMany(t => t.Members.Select(m => m.Name)).Distinct().ToList();
                var close = Suggest.Closest(site.Name, candidates, 3);
                string hint = close.Count > 0
                    ? "did you mean " + string.Join(" / ", close) + "?"
                    : (candidates.Count > 0 ? "members: " + string.Join(" ", candidates.Take(24)) : "");
                if (close.Count == 0 && char.IsUpper(site.Name[0]))
                    hint += (hint.Length > 0 ? "; " : "") + $"if '{site.Name}' is a value, quote it: \"{site.Name}\"";
                result.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "unknown-member",
                    $"no member '{site.Name}' on {typeNames}", site.Span, hint));
            }
        }

        private HashSet<string> KnownNames()
        {
            var names = new HashSet<string>(_x.Engine.KnownMemberNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var st in _x.Plan.Stages)
                if (st is SelectStage sel) foreach (var f in sel.Fields) names.Add(f.Name);
            return names;
        }

        /// <summary>Where objects of a type live: "Type.Member" paths and sources, from static reachability.</summary>
        private List<string> Holders(string typeName)
        {
            var roots = new List<(string source, Type type)>();
            foreach (var s in _x.Engine.Sources.Values)
            {
                object? v;
                try { v = s.Getter(); } catch { continue; }
                if (v != null) roots.Add((s.Name, v.GetType()));
            }
            var types = Describe.Reachable(_x.Types, roots.Select(r => r.type));
            var targets = types.Where(t => _x.Types.Info(t).IsA(typeName)).ToList();
            if (targets.Count == 0) return new List<string>();
            bool Holds(Type declared)
            {
                var info = _x.Types.Info(declared);
                var el = info.IsList || info.IsMap ? info.ElementType : declared;
                return el != null && targets.Any(t => el.IsAssignableFrom(t));
            }
            var result = new List<string>();
            foreach (var (source, type) in roots) if (Holds(type)) result.Add("source " + source);
            foreach (var t in types)
            {
                var info = _x.Types.Info(t);
                foreach (var m in info.Members)
                {
                    if (m.DeclaringType != t) continue;
                    if (Holds(m.Type)) result.Add(info.ShortName + "." + m.Name);
                }
            }
            return result.Distinct().Take(4).ToList();
        }

        /// <summary>Live items produced by a plan without a terminal stage (host API, see RqlEngine.EvaluateLive).</summary>
        public List<object?> LiveItems(int max, out List<Diagnostic> diagnostics)
        {
            var items = new List<object?>();
            try
            {
                var (stream, terminal) = BuildStream();
                if (terminal != null) throw new RqlException("invalid-plan", $"EvaluateLive needs a plan without a terminal stage (found '{terminal.Op}')");
                foreach (var it in stream)
                {
                    if (items.Count >= max) break;
                    items.Add(it.Value);
                }
                diagnostics = new List<Diagnostic>(_x.Diagnostics);
            }
            catch (RqlException ex)
            {
                diagnostics = new List<Diagnostic> { ex.ToDiagnostic() };
                items.Clear();
            }
            return items;
        }

        private (IEnumerable<Item> stream, Stage? terminal) BuildStream()
        {
            var plan = _x.Plan;
            var stream = CompileSource(plan.Source);
            for (int i = 0; i < plan.Stages.Count; i++)
            {
                var st = plan.Stages[i];
                if (st.IsTerminal) return (stream, st);
                if (st is SortStage ss && i + 1 < plan.Stages.Count && plan.Stages[i + 1] is LimitStage ls && ls.Op == "first" && ls.N <= 1000 && !_x.Engine.DisableTopN)
                {
                    stream = TopN(stream, ss.Keys.Select(k => (eval: _c.Compile(k.Expr), desc: k.Desc)).ToArray(), ls.N);
                    i++;
                    continue;
                }
                stream = Apply(stream, st);
            }
            return (stream, null);
        }

        private QueryResult RunCore()
        {
            var plan = _x.Plan;
            // Pre-compile all stages (binding errors surface before any traversal).
            var source = CompileSource(plan.Source);
            IEnumerable<Item> stream = source;
            Stage? terminal = null;
            for (int i = 0; i < plan.Stages.Count; i++)
            {
                var st = plan.Stages[i];
                if (st.IsTerminal) { terminal = st; break; }
                // Peephole: `sort … | first n` keeps only the n best items (bounded insertion, same stable order).
                if (st is SortStage ss && i + 1 < plan.Stages.Count && plan.Stages[i + 1] is LimitStage ls && ls.Op == "first" && ls.N <= 1000 && !_x.Engine.DisableTopN)
                {
                    stream = TopN(stream, ss.Keys.Select(k => (eval: _c.Compile(k.Expr), desc: k.Desc)).ToArray(), ls.N);
                    i++;
                    continue;
                }
                stream = Apply(stream, st);
            }
            var encoder = new Encoder(_x);
            if (terminal == null) return Collect(stream, encoder);
            switch (terminal)
            {
                case SimpleTerminalStage s when s.Op == "count":
                {
                    long n = 0;
                    foreach (var _ in stream) n++;
                    return Scalar(n);
                }
                case SimpleTerminalStage _:
                    return Describe.Run(_x, stream.Select(i => i.Value));
                case ExprStage e:
                {
                    var sel = _c.Compile(e.Expr!);
                    var values = Values(stream, sel);
                    var r = Compiler.Reduce(_x.Types, values, e.Op);
                    var res = new QueryResult { Ok = true, Kind = ResultKind.Scalar, HasValue = true, Value = encoder.Encode(r) };
                    return res;
                }
                case SetStage s:
                    return new Mutator(_x, _c, encoder).Set(stream, s);
                case CallStage s:
                    return new Mutator(_x, _c, encoder).Call(stream, s);
                default:
                    throw new RqlException("unsupported", $"stage '{terminal.Op}' is not supported");
            }
        }

        private static QueryResult Scalar(double v) => new QueryResult
        {
            Ok = true, Kind = ResultKind.Scalar, HasValue = true, Value = JsonValue.Create((long)v),
        };

        private IEnumerable<object?> Values(IEnumerable<Item> stream, Eval sel)
        {
            long i = 0;
            foreach (var it in stream)
            {
                _x.Current = it.Value;
                _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                _x.Index = i++;
                yield return sel(_x);
            }
        }

        // ------------------------------------------------------------ source

        private IEnumerable<Item> CompileSource(Expr src)
        {
            if (src is PathExpr p && p.On == null && p.Segments.Count > 0 && p.Segments[0].Kind == SegmentKind.Member)
            {
                // Longest dotted prefix that matches a registered source (exact, then case-insensitive).
                int memberPrefix = p.Segments.TakeWhile(s => s.Kind == SegmentKind.Member).Count();
                for (int len = memberPrefix; len >= 1; len--)
                {
                    var name = string.Join(".", p.Segments.Take(len).Select(s => s.Name));
                    var reg = FindSource(name);
                    if (reg == null) continue;
                    var rest = new PathExpr(p.Segments.Skip(len).ToList()) { Span = p.Span };
                    var nav = rest.Segments.Count > 0 ? _c.Compile(rest) : null;
                    var restText = string.Concat(rest.Segments.Select(s => s.Kind == SegmentKind.Member ? "." + s.Name : s.ToString()));
                    return SourceItems(reg, nav, reg.Name + restText);
                }
                var head = p.Segments[0].Name!;
                var close = Suggest.Closest(head, _x.Engine.Sources.Keys.ToList(), 3);
                throw new RqlException("unknown-source", $"unknown source '{head}'", p.Span,
                    (close.Count > 0 ? "did you mean " + string.Join(" / ", close) + "? " : "") + "sources: " + string.Join(" ", _x.Engine.Sources.Keys));
            }
            if (src is CallExpr c && c.On == null && string.Equals(c.Name, "ref", StringComparison.OrdinalIgnoreCase))
            {
                var ev = _c.Compile(src);
                return RefItems(ev);
            }
            // A host extension function may produce the input too, e.g. unity.assets("t:Material") (spec §2).
            if (src is CallExpr fn && Compiler.ExtensionName(fn, _x.Engine.Functions) is string fnName)
                return FunctionItems(_c.Compile(src), fnName);
            throw new RqlException("unknown-source", "query must start with a source name (e.g. world), ref(\"Type#id\") or a host function", src.Span,
                "sources: " + string.Join(" ", _x.Engine.Sources.Keys)
                + (_x.Engine.Functions.Count > 0 ? "; functions: " + string.Join(" ", _x.Engine.Functions.Keys) : ""));
        }

        private SourceRegistration? FindSource(string name)
        {
            if (_x.Engine.Sources.TryGetValue(name, out var reg)) return reg;
            var ci = _x.Engine.Sources.Values.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            return ci.Count == 1 ? ci[0] : null;
        }

        private IEnumerable<Item> SourceItems(SourceRegistration reg, Eval? nav, string pathText)
        {
            var root = _x.Types.Live(reg.Getter());
            object? value = root;
            if (nav != null)
            {
                _x.Current = root;
                _x.CurrentPath = null;
                value = nav(_x);
            }
            if (value is RqlError err) throw new RqlException(err.Code, $"source '{pathText}' failed: {err.Detail ?? err.Code}");
            if (value is Missing) yield break;
            var basePath = _x.TrackPaths ? new PathNode(null, pathText) : null;
            foreach (var it in Expand(value, basePath, true)) yield return it;
        }

        private IEnumerable<Item> FunctionItems(Eval ev, string name)
        {
            _x.Current = null;
            _x.CurrentPath = null;
            var v = _x.Types.Live(ev(_x));
            if (v is RqlError err) throw new RqlException(err.Code, $"source {name}(...) failed: {err.Detail ?? err.Code}");
            var basePath = _x.TrackPaths ? new PathNode(null, name + "(…)") : null;
            foreach (var it in Expand(v, basePath, true)) yield return it;
        }

        private IEnumerable<Item> RefItems(Eval ev)
        {
            _x.Current = null;
            var v = ev(_x);
            if (v is RqlError) yield break; // diagnostic already recorded
            foreach (var it in Expand(v, null, true)) yield return it;
        }

        /// <summary>Lists → elements, maps → entries, else the value itself; null/missing dropped.</summary>
        private IEnumerable<Item> Expand(object? value, PathNode? path, bool countVisit)
        {
            if (value == null || value is Missing || value is Unknown) yield break;
            if (value is RqlError) yield break;
            if (value is RowValue row && row.Columns.Length == 1)
            {
                foreach (var it in Expand(row.Values[0], path, countVisit)) yield return it;
                yield break;
            }
            var info = _x.Types.Info(value.GetType());
            if (info.IsList)
            {
                if (value is IList list)
                {
                    // Index loop: avoids boxing the collection enumerator per list.
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (countVisit) _x.Visit();
                        var el = _x.Types.Live(list[i]);
                        if (el == null) continue;
                        yield return new Item(el, path?.At(i));
                    }
                    yield break;
                }
                long j = 0;
                foreach (var raw in (IEnumerable)value)
                {
                    if (countVisit) _x.Visit();
                    var p = path != null ? path.At(j) : null;
                    j++;
                    var el = _x.Types.Live(raw);
                    if (el == null) continue;
                    yield return new Item(el, p);
                }
                yield break;
            }
            if (info.IsMap)
            {
                foreach (var e in V.EnumerateMap(value, info))
                {
                    if (countVisit) _x.Visit();
                    var me = (MapEntry)e!;
                    yield return new Item(me, path?.Key(me.Key));
                }
                yield break;
            }
            if (countVisit) _x.Visit();
            yield return new Item(value, path);
        }

        // ------------------------------------------------------------ stages

        private IEnumerable<Item> Apply(IEnumerable<Item> input, Stage st)
        {
            switch (st)
            {
                case WhereStage w: return Where(input, _c.Compile(w.Pred));
                case SelectStage s: return Select(input, s);
                case SortStage s: return Sort(input, s);
                case LimitStage l when l.Op == "first": return First(input, l.N);
                case LimitStage l: return input.Skip(l.N);
                case ExprStage e when e.Op == "get": return Get(input, e.Expr!);
                case ExprStage e when e.Op == "distinct": return Distinct(input, e.Expr == null ? null : _c.Compile(e.Expr));
                case ExprStage e when e.Op == "group-by": return GroupBy(input, _c.Compile(e.Expr!));
                case RecurseStage r: return Recurse(input, r);
                case GenericStage g: return Generic(input, g);
                default: throw new RqlException("unsupported", $"stage '{st.Op}' is not supported here", st.Span);
            }
        }

        private static IEnumerable<Item> First(IEnumerable<Item> input, int n)
        {
            if (n <= 0) yield break;
            int i = 0;
            foreach (var it in input)
            {
                yield return it;
                if (++i >= n) yield break;
            }
        }

        private IEnumerable<Item> Where(IEnumerable<Item> input, Eval pred)
        {
            long i = 0;
            foreach (var it in input)
            {
                _x.Current = it.Value;
                _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                _x.Index = i++;
                if (pred(_x) is bool b && b) yield return it;
            }
        }

        private IEnumerable<Item> Select(IEnumerable<Item> input, SelectStage s) =>
            SelectIter(input, s.Fields.Select(f => f.Name).ToArray(), s.Fields.Select(f => _c.Compile(f.Expr)).ToArray());

        private IEnumerable<Item> SelectIter(IEnumerable<Item> input, string[] cols, Eval[] evals)
        {
            long i = 0;
            foreach (var it in input)
            {
                _x.Current = it.Value;
                _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                _x.Index = i++;
                var vals = new object?[evals.Length];
                for (int k = 0; k < evals.Length; k++) vals[k] = evals[k](_x);
                yield return new Item(new RowValue(cols, vals), null);
            }
        }

        private IEnumerable<Item> Sort(IEnumerable<Item> input, SortStage s) =>
            SortIter(input, s.Keys.Select(k => (eval: _c.Compile(k.Expr), desc: k.Desc)).ToArray());

        private IEnumerable<Item> SortIter(IEnumerable<Item> input, (Eval eval, bool desc)[] keys)
        {
            var rows = new List<(Item item, object?[] keys)>();
            long i = 0;
            foreach (var it in input)
            {
                _x.Current = it.Value;
                _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                _x.Index = i++;
                var kv = new object?[keys.Length];
                for (int k = 0; k < keys.Length; k++) kv[k] = keys[k].eval(_x);
                rows.Add((it, kv));
            }
            _x.CheckTime();
            var cmp = new SortComparer(keys.Select(k => k.desc).ToArray(), _x.Types);
            // OrderBy is a stable sort.
            foreach (var r in rows.OrderBy(r => r.keys, cmp)) yield return r.item;
        }

        private IEnumerable<Item> TopN(IEnumerable<Item> input, (Eval eval, bool desc)[] keys, int n)
        {
            if (n <= 0) yield break;
            var cmp = new SortComparer(keys.Select(k => k.desc).ToArray(), _x.Types);
            var best = new List<(Item item, object?[] keys)>(n + 1);
            var scratch = new object?[keys.Length];
            long i = 0;
            foreach (var it in input)
            {
                _x.Current = it.Value;
                _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                _x.Index = i++;
                for (int k = 0; k < keys.Length; k++) scratch[k] = keys[k].eval(_x);
                // Strictly-better-than-last test keeps stability: earlier items win ties.
                if (best.Count == n && cmp.Compare(scratch, best[n - 1].keys) >= 0) continue;
                int lo = 0, hi = best.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (cmp.Compare(best[mid].keys, scratch) <= 0) lo = mid + 1; else hi = mid;
                }
                best.Insert(lo, (it, (object?[])scratch.Clone()));
                if (best.Count > n) best.RemoveAt(n);
            }
            _x.CheckTime();
            foreach (var b in best) yield return b.item;
        }

        private IEnumerable<Item> Get(IEnumerable<Item> input, Expr e) => GetIter(input, _c.Compile(e), PathSuffix(e));

        private IEnumerable<Item> GetIter(IEnumerable<Item> input, Eval eval, string? suffix)
        {
            long i = 0;
            foreach (var it in input)
            {
                _x.Current = it.Value;
                _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                _x.Index = i++;
                var v = eval(_x);
                if (v is RqlError err) { _x.GetterErrors++; _x.FirstGetterError ??= err.Detail ?? err.Code; continue; }
                PathNode? p = null;
                if (_x.TrackPaths && it.Path != null && suffix != null) p = new PathNode(it.Path, suffix);
                foreach (var sub in Expand(v, p, true)) yield return sub;
            }
        }

        /// <summary>Path text appended by `get`/`recurse via` when the expression is a plain member/index path.</summary>
        private static string? PathSuffix(Expr e)
        {
            if (!(e is PathExpr p) || p.On != null || p.Segments.Any(s => s.Kind == SegmentKind.Meta)) return null;
            return string.Concat(p.Segments.Select(s => s.Kind == SegmentKind.Member ? "." + s.Name : s.ToString()));
        }

        private IEnumerable<Item> Distinct(IEnumerable<Item> input, Eval? key)
        {
            var seen = new HashSet<object?>(DistinctComparer.Instance);
            long i = 0;
            foreach (var it in input)
            {
                object? k = it.Value;
                if (key != null)
                {
                    _x.Current = it.Value;
                    _x.CurrentPath = it.Path;
                _x.CurrentDepth = it.Depth;
                    _x.Index = i;
                    k = key(_x);
                }
                i++;
                if (seen.Add(k)) yield return it;
            }
        }

        private IEnumerable<Item> Recurse(IEnumerable<Item> input, RecurseStage r)
        {
            var via = r.Via?.Select(v => (eval: _c.Compile(v), suffix: PathSuffix(v))).ToArray();
            return RecurseIter(input, r, via);
        }

        private IEnumerable<Item> RecurseIter(IEnumerable<Item> input, RecurseStage r, (Eval eval, string? suffix)[]? via)
        {
            int depthLimit = r.MaxDepth ?? _x.Engine.Defaults.DefaultRecurseDepth;
            if (depthLimit > _x.MaxDepth)
            {
                _x.Diagnostics.Add(Diagnostic.Warning("budget-exceeded", $"recurse max-depth {depthLimit} clamped to {_x.MaxDepth}"));
                depthLimit = _x.MaxDepth;
            }
            var visited = new HashSet<object>(RefEq.Instance);
            // Buffers reused for every node of this stage (no per-node allocation).
            var stack = new Stack<(object? value, PathNode? path, int depth, bool emit)>();
            var children = new List<(object? value, PathNode? path)>();
            foreach (var it in input)
            {
                if (it.Value == null) continue;
                stack.Clear();
                stack.Push((it.Value, it.Path, 0, r.IncludeSelf));
                while (stack.Count > 0)
                {
                    var (v, path, depth, emit) = stack.Pop();
                    if (v == null) continue;
                    bool isRef = !v.GetType().IsValueType;
                    if (isRef && !visited.Add(v)) continue;
                    if (emit) yield return new Item(v, path, depth);
                    if (depth >= depthLimit) continue;
                    CollectChildren(v, path, depth, via, children);
                    // Push in reverse so the first child is visited first (pre-order).
                    for (int k = children.Count - 1; k >= 0; k--) stack.Push((children[k].value, children[k].path, depth + 1, true));
                }
            }
        }

        /// <summary>Kept out of the iterator so the state machine stays small (measurably faster JIT code).</summary>
        private void CollectChildren(object v, PathNode? path, int depth, (Eval eval, string? suffix)[]? via, List<(object? value, PathNode? path)> children)
        {
            children.Clear();
            if (via == null)
            {
                GenericChildren(v, path, children);
                return;
            }
            foreach (var (eval, suffix) in via)
            {
                _x.Current = v;
                _x.CurrentPath = path;
                _x.CurrentDepth = depth;
                var cv = eval(_x);
                var cp = _x.TrackPaths && path != null && suffix != null ? new PathNode(path, suffix) : null;
                // Measured: the iterator-based Expand has a lower median here than AppendExpanded
                // (0.95 vs 1.75 ms on recurse-children, Large world) despite allocating more; keep it.
                foreach (var child in Expand(cv, cp, true)) children.Add((child.Value, child.Path));
            }
        }

        private void GenericChildren(object v, PathNode? path, List<(object?, PathNode?)> into)
        {
            var info = _x.Types.Info(v.GetType());
            if (info.IsList || info.IsMap)
            {
                AppendExpanded(v, path, into, objectsOnly: true);
                return;
            }
            if (!info.IsObject) return;
            foreach (var m in info.ImplicitMembers)
            {
                if (!m.Owning || TypeModel.IsScalarType(m.Type) || m.Type.IsValueType) continue;
                object? cv;
                _x.MembersRead++;
                try { cv = m.Getter(v); }
                catch (Exception ex) { _x.GetterThrew(m.Name, ex); continue; }
                if (cv == null || cv is string) continue;
                var p = _x.TrackPaths ? path?.Member(m.Name) : null;
                var ci = _x.Types.Info(cv.GetType());
                if (ci.IsObject) { _x.Visit(); into.Add((cv, p)); }
                else if (ci.IsList || ci.IsMap) AppendExpanded(cv, p, into, objectsOnly: true);
            }
        }

        /// <summary>Non-iterator equivalent of <see cref="Expand"/> used by recurse (no iterator allocation).</summary>
        private void AppendExpanded(object? value, PathNode? path, List<(object?, PathNode?)> into, bool objectsOnly)
        {
            if (value == null || value is Missing || value is Unknown || value is RqlError) return;
            if (value is RowValue row && row.Columns.Length == 1) { AppendExpanded(row.Values[0], path, into, objectsOnly); return; }
            var info = _x.Types.Info(value.GetType());
            if (info.IsList)
            {
                int i = 0;
                foreach (var raw in V.Iterate(value))
                {
                    _x.Visit();
                    int idx = i++;
                    var el = _x.Types.Live(raw);
                    if (el == null || (objectsOnly && !_x.Types.Info(el.GetType()).IsObject)) continue;
                    into.Add((el, _x.TrackPaths ? path?.At(idx) : null));
                }
                return;
            }
            foreach (var c in Expand(value, path, true))
            {
                var cv = objectsOnly && c.Value is MapEntry me ? me.Value : c.Value;
                if (cv == null || (objectsOnly && !_x.Types.Info(cv.GetType()).IsObject)) continue;
                into.Add((cv, c.Path));
            }
        }

        private IEnumerable<Item> Generic(IEnumerable<Item> input, GenericStage g)
        {
            var (handler, args, named) = BindGeneric(g);
            return GenericIter(input, handler, args, named);
        }

        private IEnumerable<Item> GenericIter(IEnumerable<Item> input, StageHandler handler, List<ArgEvaluator> args, Dictionary<string, ArgEvaluator> named)
        {
            foreach (var v in handler(input.Select(i => i.Value), args, named))
            {
                _x.Visit();
                yield return new Item(v, null);
            }
        }

        private (StageHandler, List<ArgEvaluator>, Dictionary<string, ArgEvaluator>) BindGeneric(GenericStage g)
        {
            if (!_x.Engine.Stages.TryGetValue(g.Op, out var handler))
            {
                var close = Suggest.Closest(g.Op, _x.Engine.Stages.Keys.ToList(), 3);
                throw new RqlException("unknown-stage", $"unknown stage '{g.Op}'", g.Span,
                    close.Count > 0 ? "did you mean " + string.Join(" / ", close) + "?" : "registered extension stages: " + string.Join(" ", _x.Engine.Stages.Keys));
            }
            ArgEvaluator Make(Expr e)
            {
                var ev = _c.Compile(e);
                return item =>
                {
                    var saved = _x.Current;
                    _x.Current = item;
                    try { return ev(_x); }
                    finally { _x.Current = saved; }
                };
            }
            var args = g.Args.Select(Make).ToList();
            var named = g.Named.ToDictionary(kv => kv.Key, kv => Make(kv.Value));
            return (handler, args, named);
        }

        // ------------------------------------------------------------ terminals / output

        /// <summary>group-by: rows {key, count} in order of first appearance (null/missing keys grouped as null).</summary>
        private IEnumerable<Item> GroupBy(IEnumerable<Item> input, Eval key)
        {
            var order = new List<object?>();
            var counts = new Dictionary<object, long>(DistinctComparer.Instance);
            long nullCount = 0;
            bool sawNull = false;
            foreach (var v in Values(input, key))
            {
                var k = v is Missing || v is Unknown ? null : v;
                if (k == null)
                {
                    if (!sawNull) { sawNull = true; order.Add(null); }
                    nullCount++;
                    continue;
                }
                if (counts.TryGetValue(k, out var c)) counts[k] = c + 1;
                else { counts[k] = 1; order.Add(k); }
            }
            var cols = new[] { "key", "count" };
            foreach (var k in order)
                yield return new Item(new RowValue(cols, new object?[] { k, (double)(k == null ? nullCount : counts[k]) }), null);
        }

        private QueryResult Collect(IEnumerable<Item> stream, Encoder enc)
        {
            var collected = new List<object?>();
            long total = 0;
            foreach (var it in stream)
            {
                if (total < _x.MaxRows) collected.Add(it.Value);
                total++;
            }
            // Rows only when EVERY item is a projected row (a later stage can mix rows and plain values).
            bool rows = collected.Count > 0 ? collected.All(c => c is RowValue) : LastIsSelect();
            QueryResult r;
            if (rows)
            {
                var cols = collected.Count > 0 ? ((RowValue)collected[0]!).Columns : SelectColumns();
                r = enc.Rows(cols, collected.Select(c => ((RowValue)c!).Values).ToList());
            }
            else
            {
                r = enc.Items(collected);
            }
            r.Total = total;
            if (total > collected.Count) r.Truncated = true;
            return r;
        }

        /// <summary>Whether the (empty) stream would have carried projected rows: a select not followed by an item-producing stage.</summary>
        private bool LastIsSelect()
        {
            bool rows = false;
            foreach (var st in _x.Plan.Stages)
            {
                if (st is SelectStage || (st is ExprStage ge && ge.Op == "group-by")) rows = true;
                else if ((st is ExprStage ee && ee.Op != "distinct") || st is RecurseStage || st is GenericStage) rows = false;
            }
            return rows;
        }

        private string[] SelectColumns()
        {
            var last = _x.Plan.Stages.Last(s => s is SelectStage || (s is ExprStage e && e.Op == "group-by"));
            return last is SelectStage sel ? sel.Fields.Select(f => f.Name).ToArray() : new[] { "key", "count" };
        }
    }

    /// <summary>Sort order: null/missing last; number &lt; string &lt; bool &lt; other (spec §5).</summary>
    internal sealed class SortComparer : IComparer<object?[]>
    {
        private readonly bool[] _desc;
        private readonly TypeModel _model;
        public SortComparer(bool[] desc, TypeModel model) { _desc = desc; _model = model; }

        public int Compare(object?[]? a, object?[]? b)
        {
            for (int i = 0; i < _desc.Length; i++)
            {
                int c = CompareOne(a![i], b![i], _desc[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        private static int Rank(object? v)
        {
            if (v == null || V.IsAbsent(v)) return 9;
            if (V.IsNumber(v) || v is Enum) return 0;
            if (V.IsString(v)) return 1;
            if (v is bool) return 2;
            return 3;
        }

        private int CompareOne(object? a, object? b, bool desc)
        {
            int ra = Rank(a), rb = Rank(b);
            if (ra == 9 || rb == 9) return ra.CompareTo(rb); // nulls last in both directions
            if (ra != rb) return desc ? rb.CompareTo(ra) : ra.CompareTo(rb);
            int c;
            if (ra == 2) c = ((bool)a!).CompareTo((bool)b!);
            else c = Compiler.Compare(_model, a, b) ?? string.CompareOrdinal(Compiler.ToText(a), Compiler.ToText(b));
            return desc ? -c : c;
        }
    }

    /// <summary>Equality for distinct/group-by: numbers numerically, strings ordinal, enums by name, objects by identity.</summary>
    internal sealed class DistinctComparer : IEqualityComparer<object?>
    {
        public static readonly DistinctComparer Instance = new DistinctComparer();

        public new bool Equals(object? a, object? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (V.TryNumber(a, out var da) && V.TryNumber(b, out var db)) return da.Equals(db); // NaN groups with NaN
            if (a is Enum && b is Enum) return a.Equals(b);
            if (V.IsString(a) && V.IsString(b)) return V.AsString(a) == V.AsString(b);
            if (a is bool ba && b is bool bb) return ba == bb;
            if (a.GetType().IsValueType && a.GetType() == b.GetType()) return a.Equals(b);
            return ReferenceEquals(a, b);
        }

        public int GetHashCode(object? o)
        {
            if (o == null) return 0;
            if (V.TryNumber(o, out var d)) return d.GetHashCode();
            if (V.IsString(o)) return V.AsString(o).GetHashCode();
            if (o.GetType().IsValueType) return o.GetHashCode();
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }

    internal static class Suggest
    {
        public static List<string> Closest(string name, IReadOnlyCollection<string> candidates, int max)
        {
            var lower = name.ToLowerInvariant();
            return candidates
                .Select(c => (c, d: Distance(lower, c.ToLowerInvariant()), sub: c.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                .Where(t => t.sub || t.d <= Math.Max(1, name.Length / 3))
                .OrderBy(t => t.sub ? 0 : 1).ThenBy(t => t.d).ThenBy(t => t.c, StringComparer.Ordinal)
                .Select(t => t.c).Distinct().Take(max).ToList();
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
