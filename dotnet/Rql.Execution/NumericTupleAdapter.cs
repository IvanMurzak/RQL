using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rql.Execution
{
    /// <summary>
    /// Ready-made adapter (spec §13 "Numeric tuples") for small numeric structs such as vectors, quaternions and colors:
    /// compact display text <c>"(x, y, z)"</c> (at most 6 significant digits, float noise below 1e-6 as 0); input as
    /// <c>"(x, y, z)"</c> (also <c>nan</c> / <c>inf</c>) or <c>[x, y, z]</c> (elements coerced by the kernel); object literals
    /// <c>{x: 1, y: 2}</c> are left to the default construction on writes; component-wise approximate equality with the
    /// text, list and object-literal forms. Subclasses supply the components and the constructor.
    /// </summary>
    public abstract class NumericTupleAdapter<T> : RqlTypeAdapter<T> where T : struct
    {
        private static readonly Regex Tuple = new Regex(@"^\s*\(([^()]*)\)\s*$", RegexOptions.CultureInvariant);

        // One-entry memo of the last parsed text: a `where position == "(0, 1, 0)"` compares every row against the same
        // string literal, which is then parsed once instead of per row. Strings are immutable and the memo is swapped whole,
        // so it is thread-safe.
        private sealed class Parsed
        {
            public Parsed(object key, bool ok, T value) { Key = key; Ok = ok; Value = value; }
            public readonly object Key;
            public readonly bool Ok;
            public readonly T Value;
        }

        private volatile Parsed? _last;

        /// <summary>Number of components written by <see cref="Encode"/>.</summary>
        protected abstract int Width { get; }

        /// <summary>Component <paramref name="index"/> (0 ≤ index &lt; <see cref="Width"/>).</summary>
        protected abstract float Part(T value, int index);

        /// <summary>Build a value from parsed components; null when the count is not accepted.</summary>
        protected abstract T? Make(float[] parts);

        /// <summary>Human text for "expected …" reasons.</summary>
        protected abstract string Expected { get; }

        /// <summary>
        /// Largest per-component difference that still counts as equal near zero; for larger components the tolerance is
        /// relative (<see cref="RelativeTolerance"/> of the larger magnitude), so a displayed value equals its source.
        /// </summary>
        protected virtual float Tolerance => 1e-5f;

        /// <summary>Relative per-component tolerance (spec §13: 1e-5).</summary>
        protected const double RelativeTolerance = 1e-5;

        /// <summary>Display format: 6 significant digits for single-precision components (spec §13).</summary>
        private const string DisplayFormat = "G6";

        /// <summary>Components smaller than this print as 0 (float noise such as cos(90°) = -4.371139E-08).</summary>
        protected const double DisplayZero = 1e-6;

        /// <summary>
        /// Opt in to component-wise arithmetic (spec §4.2): <c>Scale * 1.5</c>, <c>Position + [0, 1, 0]</c>, <c>-Velocity</c> with the
        /// components of <see cref="Part"/> / <see cref="Make"/>. True for vectors and colors; leave false for quaternions, whose
        /// product is not component-wise.
        /// </summary>
        protected virtual bool Arithmetic => false;

        protected override double[]? Components(T value)
        {
            if (!Arithmetic) return null;
            var parts = new double[Width];
            for (int i = 0; i < parts.Length; i++) parts[i] = Widen(Part(value, i));
            return parts;
        }

        protected override object? FromComponents(double[] components)
        {
            if (!Arithmetic) return null;
            var parts = new float[components.Length];
            for (int i = 0; i < parts.Length; i++) parts[i] = (float)components[i];
            return Make(parts);
        }

        /// <summary>Extra textual forms (e.g. "#rrggbb" for colors); null = not this form.</summary>
        protected virtual T? ParseExtra(string text) => null;

        protected override JsonNode? Encode(T value, EncodeContext context)
        {
            var sb = new StringBuilder("(");
            for (int i = 0; i < Width; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(FormatComponent(Part(value, i)));
            }
            return JsonValue.Create(sb.Append(')').ToString());
        }

        public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            if (value is ObjectValue) return AdapterOutcome.NotHandled; // {x, y, z}: the kernel constructs and assigns fields
            if (TryParse(value, kernel, out var v)) { result = v; return AdapterOutcome.Ok; }
            reason = "expected " + Expected;
            return AdapterOutcome.Failed;
        }

        /// <summary>One component of the display text (spec §13): NaN / Infinity / -Infinity, noise as 0, 6 significant digits.</summary>
        public static string FormatComponent(float f)
        {
            if (float.IsNaN(f)) return "NaN";
            if (float.IsInfinity(f)) return f > 0 ? "Infinity" : "-Infinity";
            if (Math.Abs(f) < DisplayZero) return "0";
            return ((double)f).ToString(DisplayFormat, CultureInfo.InvariantCulture);
        }

        protected override bool? Equal(T value, object? other, RqlKernel kernel)
        {
            T o;
            bool literal = other is ObjectValue;
            if (literal)
            {
                if (!TryApply(value, (ObjectValue)other!, kernel, out o)) return null;
            }
            else if (!TryParse(other, kernel, out o)) return null;
            for (int i = 0; i < Width; i++)
            {
                double a = Part(value, i), b = Part(o, i);
                // A member the literal leaves out keeps the value's own bits: it is not compared (NaN / infinity included).
                if (literal && BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b)) continue;
                // Infinities are equal only to themselves (a relative tolerance of infinity would match everything).
                if (double.IsInfinity(a) || double.IsInfinity(b))
                {
                    if (a != b) return false;
                    continue;
                }
                double tolerance = Math.Max(Tolerance, RelativeTolerance * Math.Max(Math.Abs(a), Math.Abs(b)));
                // Written as !(d <= tol) so a NaN component is unequal to everything (NaN > tol is false).
                if (!(Math.Abs(a - b) <= tolerance)) return false;
            }
            return true;
        }

        /// <summary>
        /// <paramref name="value"/> with the members an object literal names (case-insensitively) replaced by its values, so
        /// <c>Position == {x: 0, y: 3}</c> tests x and y only. False when a key is not a writable member or a value does not
        /// coerce to the member type (the comparison is then unknown).
        /// </summary>
        private static bool TryApply(T value, ObjectValue literal, RqlKernel kernel, out T result)
        {
            result = value;
            object boxed = value;
            var info = kernel.Types.Info(typeof(T));
            foreach (var kv in literal)
            {
                var m = info.Find(kv.Key);
                if (m == null || !m.CanWrite || !kernel.TryCoerce(kv.Value, m.Type, out var coerced, out _)) return false;
                try { m.SetValue(boxed, coerced); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { return false; }
            }
            result = (T)boxed;
            return true;
        }

        private bool TryParse(object? value, RqlKernel kernel, out T result)
        {
            switch (value)
            {
                case T t:
                    result = t;
                    return true;
                case string s:
                {
                    var last = _last;
                    if (last != null && ReferenceEquals(last.Key, s)) { result = last.Value; return last.Ok; }
                    var ok = TryParseText(s, out result);
                    _last = new Parsed(s, ok, result);
                    return ok;
                }
                case IList<object?> list:
                    return TryParseList(list, kernel, out result);
                default:
                    result = default;
                    return false;
            }
        }

        private bool TryParseText(string s, out T result)
        {
            result = default;
            var extra = ParseExtra(s);
            if (extra.HasValue) { result = extra.Value; return true; }
            var m = Tuple.Match(s);
            if (!m.Success) return false;
            var parts = m.Groups[1].Value.Split(',');
            var numbers = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!TryParseComponent(parts[i].Trim(), out numbers[i])) return false;
            return Build(numbers, out result);
        }

        /// <summary>A number, or nan / inf / infinity with an optional sign in any letter case (every printed form parses back).</summary>
        private static bool TryParseComponent(string text, out float value)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            bool signed = text.Length > 0 && (text[0] == '+' || text[0] == '-');
            bool negative = signed && text[0] == '-';
            var body = signed ? text.Substring(1) : text;
            if (string.Equals(body, "nan", StringComparison.OrdinalIgnoreCase)) { value = float.NaN; return true; }
            if (string.Equals(body, "inf", StringComparison.OrdinalIgnoreCase) || string.Equals(body, "infinity", StringComparison.OrdinalIgnoreCase))
            {
                value = negative ? float.NegativeInfinity : float.PositiveInfinity;
                return true;
            }
            return false;
        }

        private bool TryParseList(IList<object?> list, RqlKernel kernel, out T result)
        {
            result = default;
            var numbers = new float[list.Count];
            for (int i = 0; i < numbers.Length; i++)
            {
                if (!kernel.TryCoerce(list[i], typeof(float), out var f, out _) || !(f is float x)) return false;
                numbers[i] = x;
            }
            return Build(numbers, out result);
        }

        private bool Build(float[] numbers, out T result)
        {
            var made = Make(numbers);
            result = made ?? default;
            return made.HasValue;
        }
    }
}
