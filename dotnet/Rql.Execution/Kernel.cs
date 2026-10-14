using System;
using System.Collections.Generic;

namespace Rql.Execution
{
    /// <summary>
    /// The engine's operations on values, handed to type adapters for nested work (spec §13): an adapter asks the kernel
    /// to coerce / compare a value of some type and never learns which adapter (if any) handles that type. The kernel runs
    /// the same pipeline as any query — adapters by priority, built-in behaviour, the host's write policy — and owns the
    /// cross-cutting rules: recursion protection (an adapter asking for its own type is skipped, so the next adapter or the
    /// built-in behaviour answers), a nesting limit, and error paths ("[1].color: expected …").
    /// </summary>
    public sealed class RqlKernel
    {
        /// <summary>Deepest nesting of kernel operations (adapters calling the kernel calling adapters …).</summary>
        public const int MaxDepth = 32;

        private readonly HashSet<(RqlTypeAdapter adapter, Type type, int op)> _active = new HashSet<(RqlTypeAdapter, Type, int)>();
        private int _depth;

        internal RqlKernel(TypeModel types, Func<Type, MemberAccessor, bool>? writePolicy)
        {
            Types = types;
            WritePolicy = writePolicy;
        }

        /// <summary>The engine's type model (member tables, kinds, adapters).</summary>
        public TypeModel Types { get; }

        /// <summary>The host's per-member write policy; applied by the kernel to every member a coercion assigns.</summary>
        internal Func<Type, MemberAccessor, bool>? WritePolicy { get; }

        /// <summary>
        /// Coerce <paramref name="value"/> to <paramref name="target"/> exactly as a <c>set</c> would (adapters, object literals,
        /// lists, numbers, enums …). On failure <paramref name="reason"/> is prefixed with <paramref name="path"/> when given.
        /// </summary>
        public bool TryCoerce(object? value, Type target, out object? result, out string? reason, string? path = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (_depth >= MaxDepth)
            {
                result = null;
                reason = Prefix(path, $"values nested deeper than {MaxDepth} levels");
                return false;
            }
            _depth++;
            try
            {
                var ok = Coerce.TryTo(this, value, target, out result, out var why);
                reason = ok ? null : Prefix(path, why);
                return ok;
            }
            finally { _depth--; }
        }

        /// <summary>Equality as in queries (§4.1 and adapters); null = unknown.</summary>
        public bool? Equal(object? a, object? b) => Nested(() => Compiler.Equal(this, a, b), null);

        /// <summary>Ordering as in queries (adapters, numbers, strings, enums); null = incomparable.</summary>
        public int? Compare(object? a, object? b) => Nested(() => Compiler.Compare(this, a, b), null);

        private T Nested<T>(Func<T> op, T tooDeep)
        {
            if (_depth >= MaxDepth) return tooDeep;
            _depth++;
            try { return op(); }
            finally { _depth--; }
        }

        internal const int OpCoerce = 0, OpEqual = 1, OpCompare = 2;

        /// <summary>Recursion guard: false when this adapter is already running this operation for this type.</summary>
        internal bool Enter(RqlTypeAdapter adapter, Type type, int op) => _active.Add((adapter, type, op));

        internal void Exit(RqlTypeAdapter adapter, Type type, int op) => _active.Remove((adapter, type, op));

        private static string Prefix(string? path, string why) => string.IsNullOrEmpty(path) ? why : path + ": " + why;
    }
}
