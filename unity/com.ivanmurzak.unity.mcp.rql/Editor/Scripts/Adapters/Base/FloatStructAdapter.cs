/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rql.Execution;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Adapters
{
    /// <summary>
    /// Base for float structs (Vector2/3/4, Quaternion, Color): compact "(x, y, z)" output; input as "(x, y, z)" or
    /// [x, y, z]; object literals {x: 1, y: 2, z: 3} are left to the kernel's default construction; approximate equality
    /// like Unity's own == operators. Subclasses supply the components and the constructor.
    /// </summary>
    public abstract class FloatStructAdapter<T> : RqlTypeAdapter<T> where T : struct
    {
        private static readonly Regex Tuple = new Regex(@"^\s*\(([^()]*)\)\s*$", RegexOptions.CultureInvariant);

        protected abstract float[] Parts(T value);

        /// <summary>Build a value from parsed components; null when the count is not accepted.</summary>
        protected abstract T? Make(float[] parts);

        /// <summary>Human text for "expected …" reasons.</summary>
        protected abstract string Expected { get; }

        /// <summary>Extra textual forms (e.g. "#rrggbb" for colors); null = not this form.</summary>
        protected virtual T? ParseExtra(string text) => null;

        protected override JsonNode? Encode(T value, EncodeContext context) =>
            JsonValue.Create("(" + string.Join(", ", Parts(value).Select(f => f.ToString("R", CultureInfo.InvariantCulture))) + ")");

        public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
        {
            result = null;
            reason = null;
            if (value is ObjectValue) return AdapterOutcome.NotHandled; // {x, y, z}: the kernel constructs and assigns fields
            if (TryParse(value, out var v)) { result = v; return AdapterOutcome.Ok; }
            reason = "expected " + Expected;
            return AdapterOutcome.Failed;
        }

        protected override bool? Equal(T value, object? other, RqlKernel kernel)
        {
            if (!TryParse(other, out var o)) return null;
            var a = Parts(value);
            var b = Parts(o);
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        private bool TryParse(object? value, out T result)
        {
            result = default;
            switch (value)
            {
                case T t:
                    result = t;
                    return true;
                case string s:
                {
                    var extra = ParseExtra(s);
                    if (extra.HasValue) { result = extra.Value; return true; }
                    var m = Tuple.Match(s);
                    if (!m.Success) return false;
                    var numbers = new List<float>();
                    foreach (var part in m.Groups[1].Value.Split(','))
                    {
                        if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false;
                        numbers.Add(f);
                    }
                    return Build(numbers, out result);
                }
                case IList<object?> list:
                {
                    var numbers = new List<float>();
                    foreach (var e in list)
                    {
                        if (!(e is double d)) return false;
                        numbers.Add((float)d);
                    }
                    return Build(numbers, out result);
                }
                default:
                    return false;
            }
        }

        private bool Build(List<float> numbers, out T result)
        {
            var made = Make(numbers.ToArray());
            result = made ?? default;
            return made.HasValue;
        }
    }
}
