using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    // Adapters reuse each other only through the kernel (spec §13): GradientAdapter never references TintAdapter.
    public class KernelTests
    {
        public struct Tint { public float r; public float g; public float b; }

        public sealed class Key { public Tint color; public float time; }

        public sealed class Gradient { public List<Key> keys = new List<Key>(); }

        public sealed class Fx { public int Id { get; set; } = 1; public Gradient gradient { get; set; } = new Gradient(); public Tint tint { get; set; } }

        /// <summary>Tint literals: "#rrggbb" (its own format); everything else is left to the kernel's default.</summary>
        private sealed class TintAdapter : RqlTypeAdapter<Tint>
        {
            public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
            {
                result = null; reason = null;
                if (value is string s && s.StartsWith("#", StringComparison.Ordinal) && s.Length == 7)
                {
                    float C(int i) => int.Parse(s.Substring(i, 2), NumberStyles.HexNumber) / 255f;
                    result = new Tint { r = C(1), g = C(3), b = C(5) };
                    return AdapterOutcome.Ok;
                }
                if (value is string) { reason = "expected #rrggbb"; return AdapterOutcome.Failed; }
                // Recursion protection: asking the kernel for our own type falls through to the built-in behaviour
                // (object-literal construction), not back into this adapter.
                return kernel.TryCoerce(value, typeof(Tint), out result, out reason) ? AdapterOutcome.Ok : AdapterOutcome.Failed;
            }

            protected override bool? Equal(Tint value, object? other, RqlKernel kernel) =>
                kernel.TryCoerce(other, typeof(Tint), out var o, out _) && o is Tint t
                    ? Math.Abs(t.r - value.r) < 1e-3 && Math.Abs(t.g - value.g) < 1e-3 && Math.Abs(t.b - value.b) < 1e-3
                    : (bool?)null;
        }

        /// <summary>Gradient literals: [[color, time], ...]; each color is coerced by the kernel (whatever adapter handles Tint).</summary>
        private sealed class GradientAdapter : RqlTypeAdapter<Gradient>
        {
            public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
            {
                result = null; reason = null;
                if (!(value is IList<object?> list)) return AdapterOutcome.NotHandled;
                var g = new Gradient();
                for (int i = 0; i < list.Count; i++)
                {
                    if (!(list[i] is IList<object?> pair) || pair.Count != 2) { reason = $"[{i}]: expected [color, time]"; return AdapterOutcome.Failed; }
                    if (!kernel.TryCoerce(pair[0], typeof(Tint), out var c, out reason, path: $"[{i}].color")) return AdapterOutcome.Failed;
                    if (!kernel.TryCoerce(pair[1], typeof(float), out var t, out reason, path: $"[{i}].time")) return AdapterOutcome.Failed;
                    g.keys.Add(new Key { color = (Tint)c!, time = (float)t! });
                }
                result = g;
                return AdapterOutcome.Ok;
            }

            protected override bool? Equal(Gradient value, object? other, RqlKernel kernel)
            {
                if (!(other is Gradient o) || o.keys.Count != value.keys.Count) return null;
                for (int i = 0; i < value.keys.Count; i++)
                    if (kernel.Equal(value.keys[i].color, o.keys[i].color) != true) return false;
                return true;
            }
        }

        private static (RqlEngine engine, Fx fx) Engine()
        {
            var fx = new Fx();
            var e = new RqlEngine();
            e.AddSource("fx", () => fx);
            e.Mutations.Enabled = true;
            e.AddAdapter(new GradientAdapter()).AddAdapter(new TintAdapter());
            return (e, fx);
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        [Fact]
        public void CompositeAdaptersCoerceNestedValuesThroughTheKernel()
        {
            var (e, fx) = Engine();
            var r = e.Execute("fx | set gradient = [[\"#ff0000\", 0], [{r: 0, g: 0, b: 1}, 1]]", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(2, fx.gradient.keys.Count);
            Assert.Equal(1f, fx.gradient.keys[0].color.r);   // via TintAdapter ("#ff0000")
            Assert.Equal(1f, fx.gradient.keys[1].color.b);   // via the kernel's default construction ({r, g, b})
        }

        [Fact]
        public void NestedFailuresCarryTheirPath()
        {
            var (e, _) = Engine();
            var r = e.Execute("fx | set gradient = [[\"#ff0000\", 0], [\"red\", 1]]", options: Mutate);
            Assert.Equal("coercion-failed", r.Diagnostics[0].Code);
            Assert.Contains("[1].color: expected #rrggbb", r.Diagnostics[0].Message);
        }

        [Fact]
        public void AnAdapterAskingForItsOwnTypeGetsTheDefaultNotItself()
        {
            var (e, fx) = Engine();
            var r = e.Execute("fx | set tint = {r: 0.5, g: 0.25, b: 0}", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(0.5f, fx.tint.r);
        }

        [Fact]
        public void TheWritePolicyAppliesToValuesBuiltForAdapters()
        {
            var (e, fx) = Engine();
            e.Mutations.CanWrite = (t, m) => !(t == typeof(Tint) && m.Name == "b");
            var r = e.Execute("fx | set gradient = [[{r: 0, g: 0, b: 1}, 1]]", options: Mutate);
            Assert.Equal("permission-denied", r.Diagnostics[0].Code);
            Assert.Empty(fx.gradient.keys);
        }

        [Fact]
        public void CompositeEqualityComparesNestedValuesThroughTheKernel()
        {
            var (e, fx) = Engine();
            Assert.True(e.Execute("fx | set gradient = [[\"#ff0000\", 0]]", options: Mutate).Ok);
            var same = e.Execute("fx | where gradient == gradient | count");
            Assert.Equal(1L, same.Value!.GetValue<long>());
            var byTint = e.Execute("fx | where gradient.keys[0].color == \"#ff0000\" | count");
            Assert.Equal(1L, byTint.Value!.GetValue<long>());
        }
    }
}
