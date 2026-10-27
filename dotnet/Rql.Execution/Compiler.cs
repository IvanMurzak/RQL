using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Rql.Execution
{
    internal delegate object? Eval(Exec x);

    /// <summary>Compiles plan expressions into closures over <see cref="Exec"/> (spec §3–4).</summary>
    internal sealed class Compiler
    {
        private readonly Exec _x;

        public Compiler(Exec x) { _x = x; }

        public static readonly string[] CoreFunctions =
        {
            "has", "is-null", "len", "lower", "upper", "abs", "round", "floor", "ceil", "sqrt", "pow", "min", "max", "coalesce", "ref",
            "append", "insert", "remove-at", "without",
            "replace", "replace-regex", "substring", "split", "trim", "pad",
            "contains", "starts-with", "ends-with", "matches",
        };

        public static readonly string[] CoreMethods =
        {
            "any", "all", "count", "sum", "min", "max", "avg", "first", "keys", "values",
            "contains", "starts-with", "ends-with", "matches", "lower", "upper", "len",
            "replace", "replace-regex", "substring", "split", "trim", "pad",
        };

        public Eval Compile(Expr e)
        {
            switch (e)
            {
                case LitExpr l:
                {
                    var v = l.Value;
                    return _ => v;
                }
                case ObjectExpr o:
                {
                    var keys = o.Entries.Select(kv => kv.Key).ToArray();
                    var values = o.Entries.Select(kv => Compile(kv.Value)).ToArray();
                    return x =>
                    {
                        var obj = new ObjectValue();
                        for (int i = 0; i < keys.Length; i++) obj[keys[i]] = values[i](x);
                        return obj;
                    };
                }
                case ListExpr l:
                {
                    var items = l.Items.Select(Compile).ToArray();
                    return x =>
                    {
                        var list = new List<object?>(items.Length);
                        foreach (var i in items) list.Add(i(x));
                        return list;
                    };
                }
                case PathExpr p: return CompilePath(p);
                case OpExpr o: return CompileOp(o);
                case CallExpr c: return CompileCall(c);
                default: throw new RqlException("invalid-plan", "unknown expression node " + e.GetType().Name);
            }
        }

        // ------------------------------------------------------------ paths

        private Eval CompilePath(PathExpr p)
        {
            Eval baseEval = p.On != null ? Compile(p.On) : (x => x.Current);
            bool relativeToItem = p.On == null;
            Eval cur = baseEval;
            for (int i = 0; i < p.Segments.Count; i++)
            {
                var seg = p.Segments[i];
                var prev = cur;
                bool first = i == 0 && relativeToItem;
                switch (seg.Kind)
                {
                    case SegmentKind.Member:
                    {
                        var site = new MemberSite(seg.Name!, p.Span) { Bare = first };
                        _x.Sites.Add(site);
                        if (first)
                        {
                            // Lexical scope chain: inside method arguments a bare name missing on the element
                            // resolves against the enclosing item(s) (spec §3).
                            cur = x =>
                            {
                                var r = Member(x, x.Current, site);
                                if (!(r is Missing) || x.Outer.Count == 0) return r;
                                for (int k = x.Outer.Count - 1; k >= 0; k--)
                                {
                                    var o = Member(x, x.Outer[k], site);
                                    if (!(o is Missing)) return o;
                                }
                                return r;
                            };
                        }
                        else cur = x => Member(x, prev(x), site);
                        break;
                    }
                    case SegmentKind.Index:
                    {
                        long idx = seg.Index;
                        object key = (double)idx;
                        cur = x =>
                        {
                            var v = prev(x);
                            if (v != null && !V.IsAbsent(v) && !(v is string))
                            {
                                var info = x.Types.Info(v.GetType());
                                if (info.IsMap) return MapLookup(x, v, info, key, null);
                            }
                            return IndexAt(x, v, idx);
                        };
                        break;
                    }
                    case SegmentKind.Key:
                    {
                        var key = seg.Key;
                        var site = new MemberSite(key as string ?? "", p.Span);
                        cur = x => KeyAt(x, prev(x), key, site);
                        break;
                    }
                    case SegmentKind.Meta:
                        cur = CompileMeta(seg.Name!, prev, first);
                        break;
                }
            }
            if (relativeToItem && p.Segments.Count > 1 && p.Segments.All(s => s.Kind == SegmentKind.Member))
            {
                // After `select Owner.Label`, the path Owner.Label names that column (spec §3), not Owner on the row.
                var column = string.Join(".", p.Segments.Select(s => s.Name));
                var navigate = cur;
                string[]? seenColumns = null; // the rows of one stage share their column array: look the name up once
                int at = -1;
                return x =>
                {
                    if (x.Current is RowValue row)
                    {
                        if (!ReferenceEquals(row.Columns, seenColumns)) { seenColumns = row.Columns; at = row.IndexOf(column); }
                        if (at >= 0) return row.Values[at];
                    }
                    return navigate(x);
                };
            }
            return cur;
        }

        private Eval CompileMeta(string name, Eval prev, bool ofCurrentItem)
        {
            var inner = CompileMetaCore(name, prev, ofCurrentItem);
            if (!ofCurrentItem) return inner;
            // After `select`, a column literally named "$name" (e.g. select $path) shadows the meta member.
            var column = "$" + name;
            return x =>
            {
                if (x.Current is RowValue row)
                {
                    int i = row.IndexOf(column);
                    if (i >= 0) return row.Values[i];
                }
                return inner(x);
            };
        }

        private Eval CompileMetaCore(string name, Eval prev, bool ofCurrentItem)
        {
            switch (name)
            {
                case "type":
                    return x =>
                    {
                        var v = prev(x);
                        if (v == null) return null;
                        if (V.IsAbsent(v)) return v;
                        if (!ofCurrentItem && V.IsList(v)) return MapMeta(v, e => e == null ? null : x.Types.Info(e.GetType()).ShortName);
                        return x.Types.Info(v.GetType()).ShortName;
                    };
                case "ref":
                    return x =>
                    {
                        var v = prev(x);
                        if (v == null) return null;
                        if (V.IsAbsent(v)) return v;
                        if (v is RowValue rv) return rv.Columns.Length == 1 && rv.Values[0] != null && !V.IsAbsent(rv.Values[0]) && x.Types.Info(rv.Values[0]!.GetType()).IsObject ? x.Engine.RefOf(rv.Values[0]!) : (object)Missing.Value;
                        if (!ofCurrentItem && V.IsList(v)) return MapMeta(v, e => e != null && x.Types.Info(e.GetType()).IsObject ? x.Engine.RefOf(e) : (object)Missing.Value);
                        var info = x.Types.Info(v.GetType());
                        return info.IsObject ? x.Engine.RefOf(v) : (object)Missing.Value;
                    };
                case "path":
                    _x.TrackPaths = true;
                    if (!ofCurrentItem) return _ => Missing.Value;
                    return x => x.CurrentPath?.ToString() ?? (object)Missing.Value;
                case "index":
                    if (!ofCurrentItem) return _ => Missing.Value;
                    return x => (double)x.Index;
                case "depth":
                    if (!ofCurrentItem) return _ => Missing.Value;
                    return x => x.CurrentDepth >= 0 ? (double)x.CurrentDepth : (object)Missing.Value;
                default:
                    _x.Diagnostics.Add(Diagnostic.Warning("unknown-meta", $"unknown meta member '${name}'", "meta members: $type $ref $path $index $depth"));
                    return _ => Missing.Value;
            }
        }

        /// <summary>Meta members applied to a navigated list map over its elements (spec §3), skipping missing.</summary>
        private static object MapMeta(object list, Func<object?, object?> f)
        {
            var r = new List<object?>();
            foreach (var e in (IEnumerable)list)
            {
                var v = f(e);
                if (!(v is Missing)) r.Add(v);
            }
            return r;
        }

        internal static object? Member(Exec x, object? v, MemberSite site)
        {
            if (v == null) return Missing.Value;
            if (V.IsAbsent(v)) return v;
            if (x.Types.IsDead(v)) return Missing.Value; // members of a destroyed object read like members of null
            var type = v.GetType();
            MemberAccessor? m;
            if (ReferenceEquals(type, site.CachedType))
            {
                m = site.CachedMember;
            }
            else
            {
                var info = x.Types.Info(type);
                if (info.IsList)
                {
                    if (string.Equals(site.Name, "Count", StringComparison.OrdinalIgnoreCase) || string.Equals(site.Name, "Length", StringComparison.OrdinalIgnoreCase))
                    {
                        site.Hits++;
                        return (double)(V.Count(v) ?? 0);
                    }
                    // Implicit map over elements, flattening one level (spec §3).
                    var list = new List<object?>();
                    foreach (var raw in V.Iterate(v))
                    {
                        x.Visit();
                        var r = Member(x, x.Types.Live(raw), site);
                        if (r is Missing) continue;
                        if (r != null && !(r is string) && V.IsList(r))
                        {
                            foreach (var inner in (IEnumerable)r) list.Add(inner);
                        }
                        else list.Add(r);
                    }
                    return list;
                }
                if (info.IsMap)
                {
                    if (string.Equals(site.Name, "Count", StringComparison.OrdinalIgnoreCase))
                    {
                        site.Hits++;
                        return (double)(V.Count(v) ?? 0);
                    }
                    return MapLookup(x, v, info, site.Name, site);
                }
                if (v is RowValue row)
                {
                    int ci = row.IndexOf(site.Name);
                    if (ci >= 0) { site.Hits++; return row.Values[ci]; }
                    // A single-column row stands for its value (`select A.B | get Item` style navigation).
                    if (row.Columns.Length == 1) return Member(x, row.Values[0], site);
                    site.Misses++;
                    site.RowColumns ??= row.Columns;
                    return Missing.Value;
                }
                m = info.Find(site.Name);
                if (m == null && v is MapEntry entry)
                {
                    // Entries expose key/value; any other member is looked up on the value (spec §4.4).
                    return Member(x, entry.Value, site);
                }
                if (m == null)
                {
                    site.Misses++;
                    if (site.MissTypes.Count < 8) site.MissTypes.Add(info);
                    return Missing.Value;
                }
                site.CachedType = type;
                site.CachedMember = m;
            }
            site.Hits++;
            x.MembersRead++;
            try
            {
                return m!.Getter(v);
            }
            catch (Exception ex)
            {
                x.GetterThrew(m!.Name, ex);
                return new RqlError("getter-threw", ex.Message);
            }
        }

        private static object? MapLookup(Exec x, object map, TypeInfo info, object? key, MemberSite? site) =>
            x.Types.Live(MapLookupRaw(x, map, info, key, site));

        private static object? MapLookupRaw(Exec x, object map, TypeInfo info, object? key, MemberSite? site)
        {
            if (!TryCoerceKey(key, info.KeyType, out var k))
            {
                if (site != null) site.Misses++;
                return Missing.Value;
            }
            x.MembersRead++;
            if (map is IDictionary d)
            {
                if (k != null && d.Contains(k)) { if (site != null) site.Hits++; return d[k]; }
                // case-insensitive fallback for string keys
                if (k is string ks)
                {
                    foreach (DictionaryEntry e in d)
                    {
                        if (e.Key is string s && string.Equals(s, ks, StringComparison.OrdinalIgnoreCase)) { if (site != null) site.Hits++; return e.Value; }
                    }
                }
                return Missing.Value;
            }
            foreach (var e in V.EnumerateMap(map, info))
            {
                var me = (MapEntry)e!;
                if (Equals(me.Key, k) || (me.Key is string s && k is string ks2 && string.Equals(s, ks2, StringComparison.OrdinalIgnoreCase)))
                {
                    if (site != null) site.Hits++;
                    return me.Value;
                }
            }
            return Missing.Value;
        }

        internal static bool TryCoerceKey(object? key, Type? keyType, out object? result)
        {
            result = key;
            if (keyType == null || key == null || keyType.IsInstanceOfType(key)) return true;
            if (key is double d)
            {
                if (keyType.IsEnum) { result = Enum.ToObject(keyType, (long)d); return true; }
                try { result = Convert.ChangeType(d, keyType, CultureInfo.InvariantCulture); return true; }
                catch { return false; }
            }
            if (key is string s)
            {
                if (keyType.IsEnum)
                {
                    try { result = Enum.Parse(keyType, s, true); return true; } catch { return false; }
                }
                if (V.IsNumericType(keyType) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                {
                    try { result = Convert.ChangeType(dv, keyType, CultureInfo.InvariantCulture); return true; } catch { return false; }
                }
            }
            return false;
        }

        internal static object? IndexAt(Exec x, object? v, long idx) => x.Types.Live(IndexAtRaw(v, idx));

        private static object? IndexAtRaw(object? v, long idx)
        {
            if (v == null) return Missing.Value;
            if (V.IsAbsent(v)) return v;
            if (v is string) return Missing.Value;
            var info = TypeModel.Get(v.GetType());
            if (info.IsMap) return Missing.Value;
            if (!info.IsList) return Missing.Value;
            if (v is IList list)
            {
                long i = idx < 0 ? list.Count + idx : idx;
                return i >= 0 && i < list.Count ? list[(int)i] : Missing.Value;
            }
            var all = ((IEnumerable)v).Cast<object?>().ToList();
            long j = idx < 0 ? all.Count + idx : idx;
            return j >= 0 && j < all.Count ? all[(int)j] : Missing.Value;
        }

        private static object? KeyAt(Exec x, object? v, object? key, MemberSite site)
        {
            if (v == null) return Missing.Value;
            if (V.IsAbsent(v)) return v;
            var info = x.Types.Info(v.GetType());
            if (info.IsMap) return MapLookup(x, v, info, key, null);
            if (key is string s && !info.IsList)
            {
                var ms = new MemberSite(s, site.Span);
                return Member(x, v, ms);
            }
            if (key is double d && Math.Floor(d) == d) return IndexAt(x, v, (long)d);
            return Missing.Value;
        }

        // ------------------------------------------------------------ operators

        private Eval CompileOp(OpExpr o)
        {
            var a = Compile(o.Args[0]);
            Eval? b = o.Args.Count > 1 ? Compile(o.Args[1]) : null;
            switch (o.Op)
            {
                case "and":
                    return x =>
                    {
                        var l = Truth(a(x));
                        if (l == false) return V.False;
                        var r = Truth(b!(x));
                        if (r == false) return V.False;
                        return l == true && r == true ? V.True : Unknown.Value;
                    };
                case "or":
                    return x =>
                    {
                        var l = Truth(a(x));
                        if (l == true) return V.True;
                        var r = Truth(b!(x));
                        if (r == true) return V.True;
                        return l == false && r == false ? V.False : Unknown.Value;
                    };
                case "not":
                    return x =>
                    {
                        var t = Truth(a(x));
                        return t == null ? Unknown.Value : V.Bool(!t.Value);
                    };
                case "==": return x => Tri(Equal(x.Types, a(x), b!(x)));
                case "!=":
                    return x =>
                    {
                        var t = Equal(x.Types, a(x), b!(x));
                        return t == null ? Unknown.Value : V.Bool(!t.Value);
                    };
                case "<": return x => Ord(x.Types, a(x), b!(x), c => c < 0);
                case "<=": return x => Ord(x.Types, a(x), b!(x), c => c <= 0);
                case ">": return x => Ord(x.Types, a(x), b!(x), c => c > 0);
                case ">=": return x => Ord(x.Types, a(x), b!(x), c => c >= 0);
                case "contains": return x => Tri(Contains(x.Types, a(x), b!(x)));
                case "starts-with": return x => Tri(StringTest(a(x), b!(x), (s, t) => s.StartsWith(t, StringComparison.OrdinalIgnoreCase)));
                case "ends-with": return x => Tri(StringTest(a(x), b!(x), (s, t) => s.EndsWith(t, StringComparison.OrdinalIgnoreCase)));
                case "matches":
                    CheckLiteralRegex(o.Args[1]);
                    return x =>
                    {
                        var r = Tri(Matches(x, a(x), b!(x)));
                        x.CheckTime(); // the per-match regex timeout alone does not bound the whole query
                        return r;
                    };
                case "in": return x => Tri(In(x.Types, a(x), b!(x)));
                case "is":
                {
                    var site = o.Args[1] is LitExpr tl && tl.Value is string tn ? new IsSite(tn, o.Span) : null;
                    if (site != null) _x.IsSites.Add(site);
                    return x =>
                    {
                        var v = a(x);
                        var r = IsType(x, v, b!(x));
                        if (site != null && r != null)
                        {
                            site.Evaluated++;
                            if (r == true) site.Matched++;
                            else if (v != null && site.Seen.Count < 6) site.Seen.Add(x.Types.Info(v.GetType()));
                        }
                        return Tri(r);
                    };
                }
                case "neg": return x => Arith("neg", a(x), 0d);
                case "+":
                case "-":
                case "*":
                case "/":
                case "%":
                {
                    var op = o.Op;
                    return x => Arith(op, a(x), b!(x));
                }
                case "index":
                    return x =>
                    {
                        var v = a(x);
                        var k = b!(x);
                        if (V.IsAbsent(k)) return k;
                        if (v == null) return Missing.Value;
                        if (V.IsAbsent(v)) return v;
                        var info = x.Types.Info(v.GetType());
                        if (info.IsMap) return MapLookup(x, v, info, k is double || k is string ? k : Convert.ToString(k, CultureInfo.InvariantCulture), null);
                        if (V.TryNumber(k, out var d) && Math.Floor(d) == d) return IndexAt(x, v, (long)d);
                        if (k is string s) return Member(x, v, new MemberSite(s, null));
                        return Missing.Value;
                    };
                default:
                    throw new RqlException("invalid-plan", $"unknown operator '{o.Op}'");
            }
        }

        internal static bool? Truth(object? v) => v switch
        {
            bool b => b,
            null => false,
            _ => null,
        };

        private static object Tri(bool? b) => b == null ? Unknown.Value : V.Bool(b.Value);

        /// <summary>Equality per spec §4.1 (adapters per §13); null = unknown.</summary>
        internal static bool? Equal(TypeModel? model, object? a, object? b) => Equal(model, model?.ReadKernel, a, b);

        internal static bool? Equal(RqlKernel kernel, object? a, object? b) => Equal(kernel.Types, kernel, a, b);

        private static bool? Equal(TypeModel? model, RqlKernel? kernel, object? a, object? b)
        {
            if (V.IsAbsent(a) || V.IsAbsent(b)) return null;
            if (a == null || b == null) return a == null && b == null;
            if (model != null && (model.Caps & AdapterCaps.Equality) != 0 && AdapterEqual(model, kernel!, a, b) is bool adapted) return adapted;
            if (V.TryNumber(a, out var da, out var sa))
            {
                if (V.TryNumber(b, out var db, out var sb))
                {
                    if (sa || sb) return (float)da == (float)db;
                    return da == db;
                }
                if (b is Enum eb) return Convert.ToDouble(eb, CultureInfo.InvariantCulture) == da;
                return null;
            }
            if (a is Enum ea) return EnumEqual(ea, b);
            if (b is Enum eb2) return EnumEqual(eb2, a);
            if (V.IsString(a)) return V.IsString(b) ? string.Equals(V.AsString(a), V.AsString(b), StringComparison.Ordinal) : (bool?)null;
            if (a is bool ba) return b is bool bb ? ba == bb : (bool?)null;
            var ta = a.GetType();
            if (ta.IsValueType || b.GetType().IsValueType)
            {
                // DateTime / Guid etc: same-type equality; other structs are not comparable in v0.
                if (TypeModel.IsScalarType(ta) && ta == b.GetType()) return a.Equals(b);
                return null;
            }
            if (b is string) return null;
            if (V.IsList(a) || V.IsList(b) || TypeModel.Get(ta).IsMap || TypeModel.Get(b.GetType()).IsMap) return null;
            return ReferenceEquals(a, b);
        }

        // First adapter answer for the left operand, then for the right operand (spec §13); ordering is mirrored. An adapter
        // already running this operation for the type (it asked the kernel about its own type) is skipped.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool? AdapterEqual(TypeModel model, RqlKernel kernel, object a, object b)
        {
            for (int side = 0; side < 2; side++)
            {
                var (x, y) = side == 0 ? (a, b) : (b, a);
                var type = x.GetType();
                foreach (var ad in model.Info(type).EqualityAdapters)
                {
                    if (!RqlKernel.Enter(ad, type, RqlKernel.OpEqual)) continue;
                    try { var r = ad.AreEqual(x, y, kernel); if (r.HasValue) return r; }
                    finally { RqlKernel.Exit(); }
                }
            }
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int? AdapterCompare(TypeModel model, RqlKernel kernel, object a, object b)
        {
            for (int side = 0; side < 2; side++)
            {
                var (x, y) = side == 0 ? (a, b) : (b, a);
                var type = x.GetType();
                foreach (var ad in model.Info(type).OrderingAdapters)
                {
                    if (!RqlKernel.Enter(ad, type, RqlKernel.OpCompare)) continue;
                    try { var r = ad.Compare(x, y, kernel); if (r.HasValue) return side == 0 ? Math.Sign(r.Value) : -Math.Sign(r.Value); }
                    finally { RqlKernel.Exit(); }
                }
            }
            return null;
        }

        private static bool? EnumEqual(Enum e, object? other)
        {
            if (other is string s) return string.Equals(e.ToString(), s, StringComparison.OrdinalIgnoreCase);
            if (V.TryNumber(other, out var d)) return Convert.ToDouble(e, CultureInfo.InvariantCulture) == d;
            if (other is Enum o) return e.GetType() == o.GetType() && e.Equals(o);
            return null;
        }

        /// <summary>Ordering comparison; null = incomparable.</summary>
        internal static int? Compare(TypeModel? model, object? a, object? b) => Compare(model, model?.ReadKernel, a, b);

        internal static int? Compare(RqlKernel kernel, object? a, object? b) => Compare(kernel.Types, kernel, a, b);

        private static int? Compare(TypeModel? model, RqlKernel? kernel, object? a, object? b)
        {
            if (a == null || b == null || V.IsAbsent(a) || V.IsAbsent(b)) return null;
            if (model != null && (model.Caps & AdapterCaps.Ordering) != 0 && AdapterCompare(model, kernel!, a, b) is int adapted) return adapted;
            if (V.TryNumber(a, out var da, out var sa))
            {
                if (V.TryNumber(b, out var db, out var sb))
                {
                    if (sa || sb) return ((float)da).CompareTo((float)db);
                    return da.CompareTo(db);
                }
                if (b is Enum eb) return da.CompareTo(Convert.ToDouble(eb, CultureInfo.InvariantCulture));
                return null;
            }
            if (a is Enum ea)
            {
                var va = Convert.ToDouble(ea, CultureInfo.InvariantCulture);
                if (b is Enum eb && eb.GetType() == ea.GetType()) return va.CompareTo(Convert.ToDouble(eb, CultureInfo.InvariantCulture));
                if (V.TryNumber(b, out var db2)) return va.CompareTo(db2);
                if (b is string bs && TryParseEnum(ea.GetType(), bs, out var ev)) return va.CompareTo(Convert.ToDouble(ev, CultureInfo.InvariantCulture));
                return null;
            }
            if (V.IsString(a) && V.IsString(b)) return Math.Sign(string.CompareOrdinal(V.AsString(a), V.AsString(b)));
            if (a.GetType() == b.GetType() && a is IComparable c && TypeModel.IsScalarType(a.GetType())) return Math.Sign(c.CompareTo(b));
            return null;
        }

        private static bool TryParseEnum(Type t, string s, out object? value)
        {
            try { value = Enum.Parse(t, s, true); return true; }
            catch { value = null; return false; }
        }

        private static object Ord(TypeModel model, object? a, object? b, Func<int, bool> test)
        {
            var c = Compare(model, a, b);
            return c == null ? Unknown.Value : V.Bool(test(c.Value));
        }

        private static bool? Contains(TypeModel model, object? a, object? b)
        {
            if (V.IsAbsent(a) || V.IsAbsent(b) || a == null) return null;
            if (V.IsString(a)) return b != null && V.IsString(b) ? V.AsString(a).IndexOf(V.AsString(b), StringComparison.OrdinalIgnoreCase) >= 0 : (bool?)null;
            if (V.IsList(a))
            {
                foreach (var el in (IEnumerable)a!) if (Equal(model, model.Live(el), b) == true) return true;
                return false;
            }
            var info = TypeModel.Get(a.GetType());
            if (info.IsMap)
            {
                foreach (var e in V.EnumerateMap(a, info)) if (Equal(model, ((MapEntry)e!).Key, b) == true) return true;
                return false;
            }
            return null;
        }

        private static bool? StringTest(object? a, object? b, Func<string, string, bool> test)
        {
            if (a == null || b == null || !V.IsString(a) || !V.IsString(b)) return null;
            return test(V.AsString(a), V.AsString(b));
        }

        // Compiled patterns, or the compile error message of an invalid one.
        private static readonly Dictionary<string, (Regex? re, string? error)> RegexCache = new Dictionary<string, (Regex?, string?)>();

        private static (Regex? re, string? error) GetRegex(string pattern)
        {
            lock (RegexCache)
            {
                if (RegexCache.TryGetValue(pattern, out var hit)) return hit;
                (Regex?, string?) made;
                try { made = (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), null); }
                catch (ArgumentException ex) { made = (null, ex.Message); }
                if (RegexCache.Count < 256) RegexCache[pattern] = made;
                return made;
            }
        }

        /// <summary>A literal pattern that does not compile fails the query before it runs (spec §4.1).</summary>
        private static void CheckLiteralRegex(Expr pattern)
        {
            if (pattern is LitExpr l && l.Value is string p && GetRegex(p).error is string error)
                throw new RqlException("invalid-regex", $"invalid regex \"{p}\": {error}", pattern.Span);
        }

        private static bool? Matches(Exec x, object? a, object? b)
        {
            if (a == null || b == null || !V.IsString(a) || !(b is string pattern)) return null;
            var (re, error) = GetRegex(pattern);
            if (re == null)
            {
                x.WarnOnce("invalid-regex", $"invalid regex \"{pattern}\": {error}; such values count as unknown", "invalid-regex");
                return null;
            }
            try { return re.IsMatch(V.AsString(a)); }
            catch (RegexMatchTimeoutException)
            {
                x.WarnOnce("timeout", $"regex \"{pattern}\" timed out on a value; such values count as unknown (simplify the pattern)", "regex-timeout");
                return null;
            }
        }

        private static bool? In(TypeModel model, object? a, object? b)
        {
            if (V.IsAbsent(a) || V.IsAbsent(b) || b == null) return null;
            if (!V.IsList(b)) return Equal(model, a, b);
            foreach (var el in (IEnumerable)b) if (Equal(model, a, model.Live(el)) == true) return true;
            return false;
        }

        private static bool? IsType(Exec x, object? a, object? b)
        {
            if (V.IsAbsent(a)) return null;
            if (a == null) return false;
            if (!(b is string name)) return null;
            return x.Types.Info(a.GetType()).IsA(name);
        }

        internal static object? Arith(string op, object? a, object? b)
        {
            if (a is Missing || b is Missing) return Missing.Value;
            if (a is RqlError) return a;
            if (b is RqlError) return b;
            if (op == "+" && ((a != null && V.IsString(a)) || (b != null && V.IsString(b))))
                return ToText(a) + ToText(b);
            if (!V.TryNumber(a, out var x) || !V.TryNumber(b, out var y)) return new RqlError("type-mismatch", $"cannot apply '{op}' to {V.Describe(a)} and {V.Describe(b)}");
            switch (op)
            {
                case "neg": return -x;
                case "+": return x + y;
                case "-": return x - y;
                case "*": return x * y;
                case "/": return y == 0 ? new RqlError("div-by-zero") : (object)(x / y);
                case "%": return y == 0 ? new RqlError("div-by-zero") : (object)(x % y);
                default: return new RqlError("type-mismatch");
            }
        }

        internal static string ToText(object? v) => v switch
        {
            null => "",
            string s => s,
            char c => c.ToString(),
            bool b => b ? "true" : "false",
            _ when V.TryNumber(v, out var d) => V.FormatNumber(d),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };

        // ------------------------------------------------------------ calls

        private Eval CompileCall(CallExpr c)
        {
            if (ExtensionName(c, _x.Engine.Functions) is string ext) return CompileExtension(ext, _x.Engine.Functions[ext], c.Args);
            if (c.On == null) return CompileFunction(c);
            return CompileMethod(c);
        }

        /// <summary>
        /// The registered extension function a call names, or null (spec §9). <c>ns.fn(x)</c> parses as method fn on path ns;
        /// a namespaced name may also arrive whole (QueryPlan).
        /// </summary>
        internal static string? ExtensionName(CallExpr c, IReadOnlyDictionary<string, Func<object?[], object?>> functions)
        {
            if (functions.Count == 0) return null;
            string? name = c.On == null ? (c.Name.IndexOf('.') >= 0 ? c.Name : null)
                : c.On is PathExpr p && p.On == null && p.Segments.Count > 0 && p.Segments.All(s => s.Kind == SegmentKind.Member)
                    ? string.Join(".", p.Segments.Select(s => s.Name)) + "." + c.Name
                    : null;
            return name != null && functions.ContainsKey(name) ? name : null;
        }

        private Eval CompileExtension(string name, Func<object?[], object?> fn, IReadOnlyList<Expr> argExprs)
        {
            var args = argExprs.Select(Compile).ToArray();
            return x =>
            {
                var vals = new object?[args.Length];
                for (int i = 0; i < args.Length; i++) vals[i] = args[i](x);
                try { return fn(vals); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    var detail = $"{name}() threw {ex.GetType().Name}: {ex.Message}";
                    x.WarnOnce("function-threw", detail, "function-threw:" + name);
                    return new RqlError("function-threw", detail);
                }
            };
        }

        private void Arity(CallExpr c, int min, int max)
        {
            if (c.Args.Count < min || c.Args.Count > max)
            {
                var expect = min == max ? min.ToString(CultureInfo.InvariantCulture) : $"{min}-{max}";
                throw new RqlException("arity", $"'{c.Name}' takes {expect} argument(s), got {c.Args.Count}", c.Span);
            }
        }

        private Eval CompileFunction(CallExpr c)
        {
            var name = c.Name.ToLowerInvariant();
            switch (name)
            {
                case "has":
                {
                    Arity(c, 1, 1);
                    var a = Compile(c.Args[0]);
                    return x => V.Bool(!(a(x) is Missing));
                }
                case "is-null":
                {
                    Arity(c, 1, 1);
                    var a = Compile(c.Args[0]);
                    return x => Tri(Equal(x.Types, a(x), null));
                }
                case "len":
                {
                    Arity(c, 1, 1);
                    var a = Compile(c.Args[0]);
                    return x => Len(a(x));
                }
                case "lower":
                case "upper":
                {
                    Arity(c, 1, 1);
                    var a = Compile(c.Args[0]);
                    bool up = name == "upper";
                    return x => CaseOf(a(x), up);
                }
                case "abs":
                case "floor":
                case "ceil":
                case "sqrt":
                {
                    Arity(c, 1, 1);
                    var a = Compile(c.Args[0]);
                    Func<double, double> f = name switch
                    {
                        "abs" => Math.Abs,
                        "floor" => Math.Floor,
                        "ceil" => Math.Ceiling,
                        _ => Math.Sqrt,
                    };
                    return x => Num(a(x), f);
                }
                case "pow":
                case "min":
                case "max":
                {
                    if (name == "pow") Arity(c, 2, 2);
                    else Arity(c, 2, 64);
                    var args = c.Args.Select(Compile).ToArray();
                    Func<double, double, double> f = name switch
                    {
                        "pow" => Math.Pow,
                        "min" => Math.Min,
                        _ => Math.Max,
                    };
                    return x =>
                    {
                        double acc = 0;
                        for (int i = 0; i < args.Length; i++)
                        {
                            var v = args[i](x);
                            if (v == null || V.IsAbsent(v)) return v;
                            if (!V.TryNumber(v, out var d)) return new RqlError("type-mismatch", $"{name}() expects numbers, got {V.Describe(v)}");
                            acc = i == 0 ? d : f(acc, d);
                        }
                        return acc;
                    };
                }
                case "round":
                {
                    Arity(c, 1, 2);
                    var a = Compile(c.Args[0]);
                    var digits = c.Args.Count > 1 ? Compile(c.Args[1]) : null;
                    return x =>
                    {
                        int dg = 0;
                        if (digits != null && V.TryNumber(digits(x), out var dd)) dg = Math.Max(0, Math.Min(15, (int)dd));
                        return Num(a(x), v => Math.Round(v, dg, MidpointRounding.AwayFromZero));
                    };
                }
                // List editing (spec §4.3): pure functions returning a new list; assign the result with `set`.
                case "append":
                {
                    Arity(c, 2, 64);
                    var args = c.Args.Select(Compile).ToArray();
                    return x =>
                    {
                        var list = ListArg(x, args[0](x));
                        if (list == null) return new RqlError("type-mismatch", "append() expects a list first");
                        for (int i = 1; i < args.Length; i++) list.Add(args[i](x));
                        return list;
                    };
                }
                case "insert":
                {
                    Arity(c, 3, 3);
                    var args = c.Args.Select(Compile).ToArray();
                    return x =>
                    {
                        var list = ListArg(x, args[0](x));
                        if (list == null) return new RqlError("type-mismatch", "insert() expects a list first");
                        if (ListIndex("insert", args[1](x), list.Count, inserting: true, out var i) is RqlError bad) return bad;
                        list.Insert(i, args[2](x));
                        return list;
                    };
                }
                case "remove-at":
                {
                    Arity(c, 2, 2);
                    var args = c.Args.Select(Compile).ToArray();
                    return x =>
                    {
                        var list = ListArg(x, args[0](x));
                        if (list == null) return new RqlError("type-mismatch", "remove-at() expects a list first");
                        if (ListIndex("remove-at", args[1](x), list.Count, inserting: false, out var i) is RqlError bad) return bad;
                        list.RemoveAt(i);
                        return list;
                    };
                }
                case "without":
                {
                    Arity(c, 2, 2);
                    var args = c.Args.Select(Compile).ToArray();
                    return x =>
                    {
                        var list = ListArg(x, args[0](x));
                        if (list == null) return new RqlError("type-mismatch", "without() expects a list first");
                        var drop = args[1](x);
                        list.RemoveAll(e => Equal(x.Types, e, drop) == true);
                        return list;
                    };
                }
                case "coalesce":
                {
                    Arity(c, 1, 16);
                    var args = c.Args.Select(Compile).ToArray();
                    return x =>
                    {
                        foreach (var a in args)
                        {
                            var v = a(x);
                            if (v != null && !V.IsAbsent(v)) return v;
                        }
                        return null;
                    };
                }
                case "ref":
                {
                    Arity(c, 1, 64);
                    var args = c.Args.Select(Compile).ToArray();
                    return x =>
                    {
                        var found = new List<object?>();
                        foreach (var a in args)
                        {
                            var v = a(x);
                            var refs = V.IsList(v) ? ((IEnumerable)v!).Cast<object?>() : new[] { v };
                            foreach (var r in refs)
                            {
                                if (!(r is string s)) return new RqlError("type-mismatch", "ref() expects reference strings");
                                var o = x.Engine.ResolveRef(s, out var err, x);
                                if (o == null)
                                {
                                    x.Diagnostics.Add(Diagnostic.Error("stale-ref", err!, x.Engine.RefHint(s)));
                                    return new RqlError("stale-ref", err);
                                }
                                found.Add(o);
                            }
                        }
                        return found.Count == 1 ? found[0] : found;
                    };
                }
                case "contains":
                case "starts-with":
                case "ends-with":
                case "matches":
                {
                    Arity(c, 2, 2);
                    return CompileOp(new OpExpr(name, c.Args) { Span = c.Span });
                }
                case "replace":
                case "replace-regex":
                case "substring":
                case "split":
                case "trim":
                case "pad":
                    return CompileStringFunction(c, name, c.Args, 0);
            }
            var known = CoreFunctions.Concat(_x.Engine.Functions.Keys);
            throw new RqlException("unknown-function", $"unknown function '{c.Name}'", c.Span, "functions: " + string.Join(" ", known) + "; collection methods: list.any(pred) list.count() ...");
        }

        /// <summary>
        /// String functions (spec §4.3), as functions (<c>replace(s, a, b)</c>) or methods on the string (<c>s.replace(a, b)</c>,
        /// <paramref name="receiver"/> = 1: the first argument is the receiver and the arity excludes it).
        /// </summary>
        private Eval CompileStringFunction(CallExpr c, string name, IReadOnlyList<Expr> argExprs, int receiver)
        {
            var (min, max) = name switch { "replace" => (3, 3), "replace-regex" => (3, 3), "substring" => (2, 3), "pad" => (2, 3), "split" => (2, 2), _ => (1, 1) };
            Arity(c, min - receiver, max - receiver);
            if (name == "replace-regex") CheckLiteralRegex(argExprs[1]);
            var args = argExprs.Select(Compile).ToArray();
            return x =>
            {
                var vals = new object?[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    vals[i] = args[i](x);
                    if (vals[i] == null || V.IsAbsent(vals[i])) return vals[i]; // null / missing / error propagate (first one)
                }
                var r = StringFunction(x, name, vals);
                if (name == "replace-regex") x.CheckTime();
                return r;
            };
        }

        private static readonly char[] TrimChars = { ' ', '\t', '\n', '\r', '\v', '\f' };

        /// <summary>The widest result <c>pad</c> builds (spec §4.3).</summary>
        public const int MaxPadWidth = 1024;

        private static object? StringFunction(Exec x, string name, object?[] vals)
        {
            if (name == "pad")
            {
                // A number pads its text form: pad(3, 2) → "03".
                var text = V.IsString(vals[0]) ? V.AsString(vals[0]!) : V.TryNumber(vals[0], out var num) ? V.FormatNumber(num) : null;
                if (text == null) return new RqlError("type-mismatch", $"pad() needs a string or a number, got {V.Describe(vals[0])}");
                if (!Integral(vals[1], out var width) || width < 0 || width > MaxPadWidth)
                    return new RqlError("type-mismatch", $"pad() width must be an integer from 0 to {MaxPadWidth}");
                var fill = vals.Length < 3 ? "0" : V.IsString(vals[2]) ? V.AsString(vals[2]!) : null;
                if (fill == null || fill.Length != 1) return new RqlError("type-mismatch", "pad() fill must be a one-character string");
                return text.PadLeft((int)width, fill[0]);
            }
            if (!V.IsString(vals[0])) return new RqlError("type-mismatch", $"{name}() needs a string, got {V.Describe(vals[0])}");
            var s = V.AsString(vals[0]!);
            switch (name)
            {
                case "trim":
                    return s.Trim(TrimChars);
                case "replace":
                case "split":
                {
                    if (!V.IsString(vals[1]) || (name == "replace" && !V.IsString(vals[2])))
                        return new RqlError("type-mismatch", $"{name}() needs string arguments");
                    var from = V.AsString(vals[1]!);
                    if (name == "split")
                    {
                        if (from.Length == 0) return new RqlError("type-mismatch", "split() needs a non-empty separator");
                        var parts = new List<object?>();
                        int start = 0, at;
                        while ((at = s.IndexOf(from, start, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            parts.Add(s.Substring(start, at - start));
                            start = at + from.Length;
                        }
                        parts.Add(s.Substring(start));
                        return parts;
                    }
                    if (from.Length == 0) return s;
                    var to = V.AsString(vals[2]!);
                    var sb = new System.Text.StringBuilder(s.Length);
                    int pos = 0, hit;
                    while ((hit = s.IndexOf(from, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        sb.Append(s, pos, hit - pos).Append(to);
                        pos = hit + from.Length;
                    }
                    return sb.Append(s, pos, s.Length - pos).ToString();
                }
                case "substring":
                {
                    long want = long.MaxValue;
                    if (!Integral(vals[1], out var start) || (vals.Length > 2 && !Integral(vals[2], out want)))
                        return new RqlError("type-mismatch", "substring() needs integral start and length");
                    if (want < 0) return new RqlError("type-mismatch", "substring() length must not be negative");
                    long len = s.Length;
                    if (start < 0) start += len;
                    start = Math.Max(0, Math.Min(len, start));
                    return s.Substring((int)start, (int)Math.Min(len - start, want));
                }
                default: // replace-regex
                {
                    if (!(vals[1] is string pattern) || !V.IsString(vals[2])) return new RqlError("type-mismatch", "replace-regex() needs string arguments");
                    var (re, error) = GetRegex(pattern);
                    if (re == null)
                    {
                        var detail = $"invalid regex \"{pattern}\": {error}";
                        x.WarnOnce("invalid-regex", detail, "invalid-regex");
                        return new RqlError("invalid-regex", detail);
                    }
                    var repl = V.AsString(vals[2]!);
                    try { return re.Replace(s, m => Expand(m, repl)); }
                    catch (RegexMatchTimeoutException)
                    {
                        var detail = $"regex \"{pattern}\" timed out on a value (simplify the pattern)";
                        x.WarnOnce("timeout", detail, "regex-timeout");
                        return new RqlError("timeout", detail);
                    }
                }
            }
        }

        /// <summary>Portable replacement text (spec §4.3): <c>$0</c>–<c>$9</c> insert a group (empty when absent), <c>$$</c> a dollar.</summary>
        private static string Expand(Match m, string repl)
        {
            if (repl.IndexOf('$') < 0) return repl;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < repl.Length; i++)
            {
                char ch = repl[i];
                if (ch == '$' && i + 1 < repl.Length)
                {
                    char next = repl[i + 1];
                    if (next == '$') { sb.Append('$'); i++; continue; }
                    if (next >= '0' && next <= '9')
                    {
                        int g = next - '0';
                        if (g < m.Groups.Count && m.Groups[g].Success) sb.Append(m.Groups[g].Value);
                        i++;
                        continue;
                    }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>The largest integer a double holds exactly (2^53): integral arguments up to it are accepted.</summary>
        private const double MaxExactInteger = 9007199254740992;

        private static bool Integral(object? v, out long n)
        {
            n = 0;
            if (!V.TryNumber(v, out var d) || double.IsNaN(d) || Math.Floor(d) != d || Math.Abs(d) > MaxExactInteger) return false; // positions beyond the text are clamped
            n = (long)d;
            return true;
        }

        private static object? Len(object? v)
        {
            if (v == null) return Missing.Value;
            if (V.IsAbsent(v)) return v;
            var n = V.Count(v);
            return n.HasValue && (V.IsString(v) || V.IsList(v) || TypeModel.Get(v.GetType()).IsMap) ? (double)n.Value : (object)Missing.Value;
        }

        private static object? CaseOf(object? v, bool upper)
        {
            if (v == null || V.IsAbsent(v)) return v;
            if (!V.IsString(v)) return new RqlError("type-mismatch");
            var s = V.AsString(v);
            return upper ? s.ToUpperInvariant() : s.ToLowerInvariant();
        }

        private static object? Num(object? v, Func<double, double> f)
        {
            if (v == null || V.IsAbsent(v)) return v;
            return V.TryNumber(v, out var d) ? f(d) : (object)new RqlError("type-mismatch");
        }

        private Eval CompileMethod(CallExpr c)
        {
            var on = Compile(c.On!);
            var name = c.Name.ToLowerInvariant();
            switch (name)
            {
                case "any":
                case "all":
                case "count":
                case "first":
                {
                    Arity(c, name == "all" ? 1 : 0, 1);
                    var pred = c.Args.Count > 0 ? Compile(c.Args[0]) : null;
                    return x => Quantify(x, on(x), pred, name);
                }
                case "sum":
                case "min":
                case "max":
                case "avg":
                {
                    Arity(c, 0, 1);
                    var sel = c.Args.Count > 0 ? Compile(c.Args[0]) : null;
                    return x => Aggregate(x, on(x), sel, name);
                }
                case "keys":
                case "values":
                {
                    Arity(c, 0, 0);
                    bool keys = name == "keys";
                    return x =>
                    {
                        var v = on(x);
                        if (v == null || V.IsAbsent(v)) return Missing.Value;
                        var info = x.Types.Info(v.GetType());
                        if (!info.IsMap) return Missing.Value;
                        return V.EnumerateMap(v, info).Select(e => keys ? ((MapEntry)e!).Key : x.Types.Live(((MapEntry)e!).Value)).ToList();
                    };
                }
                case "contains":
                case "starts-with":
                case "ends-with":
                case "matches":
                    Arity(c, 1, 1);
                    return CompileOp(new OpExpr(name, new[] { c.On!, c.Args[0] }) { Span = c.Span });
                case "lower":
                case "upper":
                {
                    Arity(c, 0, 0);
                    bool up = name == "upper";
                    return x => CaseOf(on(x), up);
                }
                case "len":
                    Arity(c, 0, 0);
                    return x => Len(on(x));
                case "replace":
                case "replace-regex":
                case "substring":
                case "split":
                case "trim":
                case "pad":
                    return CompileStringFunction(c, name, new[] { c.On! }.Concat(c.Args).ToList(), 1);
            }
            throw new RqlException("unknown-function", $"unknown method '{c.Name}'", c.Span, "methods: " + string.Join(" ", CoreMethods));
        }

        private static IEnumerable<object?>? Elements(Exec x, object? v)
        {
            if (v == null || V.IsAbsent(v) || v is string) return null;
            var info = x.Types.Info(v.GetType());
            if (info.IsMap) return V.EnumerateMap(v, info);
            if (info.IsList) return x.Types.LivenessOn ? V.Iterate(v).Select(x.Types.Live) : V.Iterate(v);
            return null;
        }

        private static object? Quantify(Exec x, object? v, Eval? pred, string kind)
        {
            if (v is Missing || v is RqlError) return kind == "count" || kind == "first" ? Missing.Value : (object)Unknown.Value;
            var els = Elements(x, v);
            if (els == null)
            {
                if (v == null) return kind == "count" ? 0d : kind == "first" ? Missing.Value : (object)(kind == "all" ? V.True : V.False);
                // A single object behaves as a one-element collection.
                els = new[] { v };
            }
            var saved = x.Current;
            var savedPath = x.CurrentPath;
            var savedIndex = x.Index;
            x.Outer.Add(saved);
            try
            {
                double count = 0;
                bool sawUnknown = false;
                long i = 0;
                foreach (var el in els)
                {
                    x.Visit(); // collection-method elements count toward maxVisited/timeout (spec §10)
                    bool? t = true;
                    if (pred != null)
                    {
                        x.Current = el;
                        x.CurrentPath = null;
                        x.Index = i;
                        t = Truth(pred(x));
                    }
                    i++;
                    switch (kind)
                    {
                        case "any":
                            if (t == true) return V.True;
                            if (t == null) sawUnknown = true;
                            break;
                        case "all":
                            if (t == false) return V.False;
                            if (t == null) sawUnknown = true;
                            break;
                        case "first":
                            if (t == true) return el;
                            break;
                        default:
                            if (t == true) count++;
                            break;
                    }
                }
                switch (kind)
                {
                    case "any": return sawUnknown ? Unknown.Value : V.False;
                    case "all": return sawUnknown ? Unknown.Value : V.True;
                    case "first": return Missing.Value;
                    default: return count;
                }
            }
            finally
            {
                x.Outer.RemoveAt(x.Outer.Count - 1);
                x.Current = saved;
                x.CurrentPath = savedPath;
                x.Index = savedIndex;
            }
        }

        private static object? Aggregate(Exec x, object? v, Eval? sel, string kind)
        {
            if (v is Missing || v is RqlError) return v;
            var els = Elements(x, v);
            if (els == null) return v == null ? null : Missing.Value;
            var saved = x.Current;
            var savedPath = x.CurrentPath;
            x.Outer.Add(saved);
            try
            {
                var values = new List<object?>();
                foreach (var el in els)
                {
                    x.Visit();
                    object? val = el;
                    if (sel != null)
                    {
                        x.Current = el;
                        x.CurrentPath = null;
                        val = sel(x);
                    }
                    values.Add(val);
                }
                return Reduce(x, values, kind);
            }
            finally
            {
                x.Outer.RemoveAt(x.Outer.Count - 1);
                x.Current = saved;
                x.CurrentPath = savedPath;
            }
        }

        private static bool AdapterOrdered(TypeModel model, object val) =>
            (model.Caps & AdapterCaps.Ordering) != 0 && model.Info(val.GetType()).OrderingAdapters.Length > 0;

        /// <summary>
        /// Resolves an integral, possibly negative index (from the end) for insert (0..count, -1 appends) or element access
        /// (0..count-1); returns an error value when it is not an integer or out of range.
        /// </summary>
        private static RqlError? ListIndex(string fn, object? index, int count, bool inserting, out int i)
        {
            i = 0;
            if (!(index is double d) || Math.Floor(d) != d) return new RqlError("type-mismatch", fn + "() index must be an integer");
            int limit = inserting ? count + 1 : count;
            long r = d < 0 ? limit + (long)d : (long)d;
            if (r < 0 || r >= limit) return new RqlError("type-mismatch", $"{fn}() index {d} out of range (count {count})");
            i = (int)r;
            return null;
        }

        /// <summary>A fresh list copy of a list value (never the live collection), or null when the value is not a list.</summary>
        private static List<object?>? ListArg(Exec x, object? v)
        {
            if (v == null || V.IsAbsent(v) || !V.IsList(v)) return null;
            var copy = new List<object?>();
            foreach (var e in (IEnumerable)v) copy.Add(x.Types.Live(e));
            return copy;
        }

        /// <summary>
        /// Aggregates values (spec §4.3): null / missing are skipped silently; any other value that cannot take part is
        /// skipped too and reported once per aggregate kind as a type-mismatch warning on <paramref name="x"/>.
        /// </summary>
        internal static object? Reduce(Exec x, IEnumerable<object?> values, string kind)
        {
            var model = x.Types;
            double sum = 0;
            int n = 0, ignored = 0;
            string? firstIgnored = null;
            object? best = null;
            foreach (var val in values)
            {
                if (val == null || val is Missing) continue; // error / unknown values are counted as ignored below
                bool used;
                if (kind == "min" || kind == "max")
                {
                    used = !V.IsAbsent(val) && (V.IsNumber(val) || V.IsString(val) || val is Enum || AdapterOrdered(model, val));
                    if (used && best == null) best = val;
                    else if (used)
                    {
                        var c = Compare(model, val, best);
                        if (c == null) used = false;
                        else if ((kind == "min" && c < 0) || (kind == "max" && c > 0)) best = val;
                    }
                }
                else
                {
                    used = V.TryNumber(val, out var d);
                    if (used) { sum += d; n++; }
                }
                if (!used && ignored++ == 0) firstIgnored = V.Describe(val);
            }
            if (ignored > 0)
            {
                var what = kind == "min" || kind == "max" ? "value(s) not comparable with the others" : "non-numeric value(s)";
                x.WarnOnce("type-mismatch", $"{kind} ignored {ignored} {what} (first: {firstIgnored})", "aggregate:" + kind);
            }
            switch (kind)
            {
                case "sum": return sum;
                case "avg": return n == 0 ? null : (object)(sum / n);
                default: return best;
            }
        }
    }
}
