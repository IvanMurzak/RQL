using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace Rql.Execution
{
    /// <summary>The path does not exist (spec §1.1). Distinct from null.</summary>
    public sealed class Missing
    {
        public static readonly Missing Value = new Missing();
        private Missing() { }
        public override string ToString() => "missing";
    }

    /// <summary>Third truth value of Kleene logic (spec §1.2).</summary>
    public sealed class Unknown
    {
        public static readonly Unknown Value = new Unknown();
        private Unknown() { }
        public override string ToString() => "unknown";
    }

    /// <summary>An evaluation error value (getter threw, division by zero...).</summary>
    public sealed class RqlError
    {
        public RqlError(string code, string? detail = null) { Code = code; Detail = detail; }
        public string Code { get; }
        public string? Detail { get; }
        public override string ToString() => "error(" + Code + ")";
    }

    /// <summary>A key/value pair yielded when iterating a map (spec §4.4).</summary>
    public sealed class MapEntry
    {
        public MapEntry(object? key, object? value) { Key = key; Value = value; }

        internal MapEntry(object? key, object? value, EntryOrigin origin) : this(key, value) { Origin = origin; }

        public object? Key { get; }
        public object? Value { get; }
        /// <summary>Where the map this entry was streamed from lives: <c>set Value… = …</c> writes through it (spec §8).</summary>
        internal EntryOrigin? Origin { get; }
    }

    /// <summary>
    /// The map of streamed entries as a write path (spec §8): <see cref="Root"/> plus <see cref="Prefix"/> reach the map, so
    /// <c>set Value.p</c> on an entry is the ordinary <c>set</c> of <c>Prefix[key].p</c> on the root (owners, value types and
    /// write-back of copies come with the path). When `get` used a computed expression the root is the map itself.
    /// </summary>
    internal sealed class EntryOrigin
    {
        public EntryOrigin(object root, IReadOnlyList<Segment> prefix, object? holder) { Root = root; Prefix = prefix; Holder = holder; }
        public object Root { get; }
        public IReadOnlyList<Segment> Prefix { get; }
        /// <summary>The object the map was read from (receipts' $ref, owner fallback); null for a map source.</summary>
        public object? Holder { get; }
    }

    internal static class V
    {
        public static readonly object True = true;
        public static readonly object False = false;
        public static object Bool(bool b) => b ? True : False;

        public static bool IsAbsent(object? v) => v is Missing || v is RqlError || v is Unknown;

        public static bool IsNumber(object? v) => v != null && IsNumericType(v.GetType());

        public static bool IsNumericType(Type t)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return !t.IsEnum;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Numeric value; <paramref name="single"/> is true when the source was a float (compare in float precision). The float
        /// is not widened: comparisons cast it back anyway, so only the two-argument overload (arithmetic, aggregates) pays for it.
        /// </summary>
        public static bool TryNumber(object? v, out double d, out bool single)
        {
            single = false;
            switch (v)
            {
                case double x: d = x; return true;
                case int x: d = x; return true;
                case float x: d = x; single = true; return true;
                case long x: d = x; return true;
                case decimal x: d = (double)x; return true;
                case short x: d = x; return true;
                case byte x: d = x; return true;
                case uint x: d = x; return true;
                case ulong x: d = x; return true;
                case ushort x: d = x; return true;
                case sbyte x: d = x; return true;
                default: d = 0; return false;
            }
        }

        /// <summary>Numeric value as computations see it: a float is widened to its shortest decimal (<see cref="Widen"/>).</summary>
        public static bool TryNumber(object? v, out double d)
        {
            if (!TryNumber(v, out d, out var single)) return false;
            if (single) d = Widen((float)d);
            return true;
        }

        private static readonly double[] Pow10 =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
        };

        /// <summary>
        /// A float as the double of its shortest round-trip decimal (spec §1.1 number normalization): 4.2f becomes 4.2, not
        /// 4.19999980926514, so arithmetic and aggregates over single-precision members compute with the value the member shows.
        /// The result converts back to the same float, so float-precision comparisons are unchanged.
        /// </summary>
        public static double Widen(float f)
        {
            double d = f;
            // Integers below 2^24 are exact; larger floats are spaced wider than 1, so their shortest decimal may differ.
            if (float.IsNaN(f) || float.IsInfinity(f) || (Math.Abs(d) < 16777216 && d == Math.Floor(d))) return d;
            int e = (int)Math.Floor(Math.Log10(Math.Abs(d)));
            // Allocation-free fast path for the formatter's answer (the fallback below): 6 to 9 significant digits (fewer never need
            // a separate try: rounding to 6 already drops trailing zeros). An integer divided (or multiplied) by an exact power of
            // ten is the correctly rounded double of that decimal.
            for (int p = 6; p <= 9; p++)
            {
                int k = p - 1 - e;
                if (k > 22 || k < -22) break;
                double c = k >= 0 ? Math.Round(d * Pow10[k]) / Pow10[k] : Math.Round(d / Pow10[-k]) * Pow10[-k];
                if ((float)c == f) return c;
            }
            return double.Parse(FormatNumber(f), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public static bool IsString(object? v) => v is string || v is char;
        public static string AsString(object v) => v is char c ? c.ToString() : (string)v;

        public static bool IsMap(object v) => v is IDictionary || TypeModel.Get(v.GetType()).IsMap;

        /// <summary>A list-like collection (not string, not map).</summary>
        public static bool IsList(object? v) => v != null && !(v is string) && TypeModel.Get(v.GetType()).IsList;

        public static IEnumerable<object?> Enumerate(object v)
        {
            var info = TypeModel.Get(v.GetType());
            if (info.IsMap) return EnumerateMap(v, info);
            return Cast((IEnumerable)v);
        }

        /// <summary>Elements of a list value; index-based for IList (no boxed enumerator, no LINQ).</summary>
        public static IEnumerable<object?> Iterate(object list) =>
            // Reference-type lists are covariantly IEnumerable<object>: no wrapper allocation at all.
            list as IEnumerable<object?> ?? IterateSlow(list);

        private static IEnumerable<object?> IterateSlow(object list)
        {
            if (list is IList l)
            {
                for (int i = 0; i < l.Count; i++) yield return l[i];
                yield break;
            }
            foreach (var x in (IEnumerable)list) yield return x;
        }

        private static IEnumerable<object?> Cast(IEnumerable e)
        {
            foreach (var x in e) yield return x;
        }

        public static IEnumerable<object?> EnumerateMap(object v, TypeInfo info)
        {
            if (v is IDictionary d)
            {
                foreach (DictionaryEntry e in d) yield return new MapEntry(e.Key, e.Value);
                yield break;
            }
            foreach (var kv in (IEnumerable)v)
            {
                var t = kv!.GetType();
                yield return new MapEntry(t.GetProperty("Key")!.GetValue(kv), t.GetProperty("Value")!.GetValue(kv));
            }
        }

        public static int? Count(object v)
        {
            if (v is string s) return s.Length;
            if (v is ICollection c) return c.Count;
            var info = TypeModel.Get(v.GetType());
            if (info.CountGetter != null) return info.CountGetter(v);
            if (info.IsList || info.IsMap)
            {
                int n = 0;
                foreach (var _ in (IEnumerable)v) n++;
                return n;
            }
            return null;
        }

        public static string Describe(object? v) => v switch
        {
            null => "null",
            Missing _ => "missing",
            Unknown _ => "unknown",
            RqlError e => e.ToString(),
            string s => "\"" + s + "\"",
            _ => TypeModel.Get(v.GetType()).ShortName,
        };

        /// <summary>
        /// Shortest text that parses back to the same double. "R" is not shortest on every runtime (Mono, used by Unity,
        /// prints 0.1 as 0.10000000000000001), so try 15 significant digits first.
        /// </summary>
        public static string FormatNumber(double d)
        {
            string s = "";
            for (int p = 15; p <= 17; p++)
            {
                s = d.ToString("G" + p, CultureInfo.InvariantCulture);
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var back) && back.Equals(d)) break;
            }
            return s;
        }

        /// <summary>
        /// Shortest text that parses back to the same float: 0.1f prints as 0.1 (not 0.100000001) and (float)Math.PI / 2 as
        /// 1.5707964 (not 1.57079637). Fewer than 6 digits never need a separate try: rounding to 6 digits already drops them.
        /// </summary>
        public static string FormatNumber(float f)
        {
            string s = "";
            for (int p = 6; p <= 9; p++)
            {
                s = f.ToString("G" + p, CultureInfo.InvariantCulture);
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var back) && back.Equals(f)) break;
            }
            return s;
        }
    }

    /// <summary>Reference equality for identity sets (cycles, distinct).</summary>
    internal sealed class RefEq : IEqualityComparer<object>
    {
        public static readonly RefEq Instance = new RefEq();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Value of an object literal <c>{ key: expr, … }</c> (spec §4.5): an ordered string-keyed map. Writes coerce it into the
    /// target type: adapters first, then construction of the concrete type (<c>"$type"</c> selects a subtype) and member assignment.
    /// </summary>
    public sealed class ObjectValue : Dictionary<string, object?>
    {
        public ObjectValue() : base(StringComparer.Ordinal) { }
    }
}
