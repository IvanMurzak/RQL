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

        // Guard state is per thread, not per kernel: nested work may go through different kernels (the read kernel of a type
        // model, the write kernel of an execution) and must still see one recursion stack and one depth. Kernels themselves are
        // immutable, so one can be reused for a whole type model or execution.
        [ThreadStatic] private static Frame[]? _frames;
        [ThreadStatic] private static int _active;
        [ThreadStatic] private static int _depth;

        private struct Frame
        {
            public RqlTypeAdapter Adapter;
            public Type Type;
            public int Op;
        }

        internal RqlKernel(TypeModel types, MutationPolicy? mutations)
        {
            Types = types;
            _mutations = mutations;
        }

        private readonly MutationPolicy? _mutations;

        /// <summary>
        /// The literal kind of <paramref name="type"/> (spec §4.5, §13): the host's literal-kind hook, else the adapters (most
        /// specific first), else engine for a type with an identity or liveness adapter and value for any other; then the host's
        /// <see cref="MutationPolicy.CanConstructFromLiteral"/> (false → engine, true → an engine type becomes value).
        /// </summary>
        internal LiteralKind KindOf(Type type)
        {
            // Hooks answer per type and cannot change within one execution (this kernel's lifetime): ask them once.
            if (_kinds != null && _kinds.TryGetValue(type, out var known)) return known;
            var info = Types.Info(type);
            LiteralKind? kind = _mutations?.LiteralKindOf?.Invoke(type);
            if (kind == null)
                foreach (var a in info.LiteralAdapters)
                    if ((kind = a.LiteralKindOf(type)) != null) break;
            var k = kind ?? (info.IdentityAdapters.Length == 0 && info.LivenessAdapters.Length == 0 ? LiteralKind.Value : LiteralKind.Engine);
            if (_mutations?.CanConstructFromLiteral?.Invoke(type) is bool decided)
                k = !decided ? LiteralKind.Engine : k == LiteralKind.Engine ? LiteralKind.Value : k;
            if (_mutations != null) (_kinds ??= new Dictionary<Type, LiteralKind>())[type] = k; // write kernels belong to one execution
            return k;
        }

        private Dictionary<Type, LiteralKind>? _kinds;

        /// <summary>Whether a factory (the host's or an adapter's) may build <paramref name="type"/>: a probe then needs no constructor.</summary>
        internal bool HasFactory(Type type) => _mutations?.CreateForLiteral != null || Types.Info(type).FactoryAdapters.Length > 0;

        /// <summary>The value a factory builds for a literal of <paramref name="type"/> (host first, then adapters); null: none answered.</summary>
        internal object? Create(Type type, object? outer)
        {
            var made = _mutations?.CreateForLiteral?.Invoke(type, outer);
            if (made == null)
                foreach (var a in Types.Info(type).FactoryAdapters)
                    if ((made = a.CreateForLiteral(type, outer)) != null) break;
            return made;
        }

        /// <summary>The advice ending the engine-object refusal (spec §4.5): the host's for the type, else "pass an existing one".</summary>
        internal string RefusalAdvice(Type type) =>
            _mutations?.LiteralRefusalHint?.Invoke(type) is string hint && hint.Length > 0 ? hint : "pass an existing one (e.g. ref(\"…\"))";

        /// <summary>
        /// The outer of the coercion an adapter is running (spec §4.5): nested <see cref="TryCoerce"/> calls build instanced values
        /// for the same owner.
        /// </summary>
        [ThreadStatic] internal static object? AmbientOuter;

        /// <summary>The engine's type model (member tables, kinds, adapters).</summary>
        public TypeModel Types { get; }

        /// <summary>The host's per-member write policy; applied by the kernel to every member a coercion assigns.</summary>
        internal Func<Type, MemberAccessor, bool>? WritePolicy => _mutations?.CanWrite;

        /// <summary>The host's text coercion for struct / object types no adapter handles (spec §8); null outside writes.</summary>
        internal Func<Type, string, object?>? TextCoercion => _mutations?.CoerceText;

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
                var ok = Coerce.TryTo(this, value, target, out result, out var why, AmbientOuter);
                reason = ok ? null : Prefix(path, why);
                return ok;
            }
            finally { _depth--; }
        }

        /// <summary>Equality as in queries (§4.1 and adapters); null = unknown.</summary>
        public bool? Equal(object? a, object? b)
        {
            if (_depth >= MaxDepth) return null;
            _depth++;
            try { return Compiler.Equal(this, a, b); }
            finally { _depth--; }
        }

        /// <summary>Ordering as in queries (adapters, numbers, strings, enums); null = incomparable.</summary>
        public int? Compare(object? a, object? b)
        {
            if (_depth >= MaxDepth) return null;
            _depth++;
            try { return Compiler.Compare(this, a, b); }
            finally { _depth--; }
        }

        internal const int OpCoerce = 0, OpEqual = 1, OpCompare = 2;

        /// <summary>Recursion guard: false when this adapter is already running this operation for this type.</summary>
        internal static bool Enter(RqlTypeAdapter adapter, Type type, int op)
        {
            var frames = _frames ??= new Frame[8];
            for (int i = 0; i < _active; i++)
                if (ReferenceEquals(frames[i].Adapter, adapter) && frames[i].Type == type && frames[i].Op == op) return false;
            if (_active == frames.Length) Array.Resize(ref _frames, frames.Length * 2);
            _frames![_active++] = new Frame { Adapter = adapter, Type = type, Op = op };
            return true;
        }

        /// <summary>Leaves the innermost frame (Enter / Exit pair up in try / finally, so frames are strictly nested).</summary>
        internal static void Exit() => _frames![--_active] = default;

        private static string Prefix(string? path, string why) => string.IsNullOrEmpty(path) ? why : path + ": " + why;
    }
}
