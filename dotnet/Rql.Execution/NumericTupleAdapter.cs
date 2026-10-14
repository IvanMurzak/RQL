using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rql.Execution
{
    /// <summary>
    /// Ready-made adapter (spec §13) for small numeric structs such as vectors, quaternions and colors: compact
    /// <c>"(x, y, z)"</c> output; input as <c>"(x, y, z)"</c> or <c>[x, y, z]</c> (elements coerced by the kernel); object
    /// literals <c>{x: 1, y: 2}</c> are left to the default construction; component-wise approximate equality.
    /// Subclasses supply the components and the constructor.
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

        /// <summary>Largest per-component difference that still counts as equal.</summary>
        protected virtual float Tolerance => 1e-5f;

        /// <summary>Extra textual forms (e.g. "#rrggbb" for colors); null = not this form.</summary>
        protected virtual T? ParseExtra(string text) => null;

        protected override JsonNode? Encode(T value, EncodeContext context)
        {
            var sb = new StringBuilder("(");
            for (int i = 0; i < Width; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Part(value, i).ToString("R", CultureInfo.InvariantCulture));
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

        protected override bool? Equal(T value, object? other, RqlKernel kernel)
        {
            if (!TryParse(other, kernel, out var o)) return null;
            for (int i = 0; i < Width; i++)
                if (Math.Abs(Part(value, i) - Part(o, i)) > Tolerance) return false;
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
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return false;
            return Build(numbers, out result);
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
