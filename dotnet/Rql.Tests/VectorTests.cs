using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>ADR-0012: numeric lists as vectors, vector functions, function / ref() sources and call targets that navigate.</summary>
    public class VectorTests
    {
        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        public struct V3
        {
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }

        public struct V2
        {
            public double X { get; set; }
            public double Y { get; set; }
        }

        public sealed class V3Adapter : RqlTypeAdapter<V3>
        {
            protected override double[]? Components(V3 v) => new[] { Widen(v.X), Widen(v.Y), Widen(v.Z) };
            protected override object? FromComponents(double[] c) => c.Length == 3 ? new V3 { X = (float)c[0], Y = (float)c[1], Z = (float)c[2] } : (object?)null;
        }

        public sealed class V2Adapter : RqlTypeAdapter<V2>
        {
            protected override double[]? Components(V2 v) => new[] { v.X, v.Y };
            protected override object? FromComponents(double[] c) => c.Length == 2 ? new V2 { X = c[0], Y = c[1] } : (object?)null;
        }

        public sealed class Graph
        {
            public List<string> Events { get; } = new List<string>();

            [RqlCallable("Adds a custom event.")]
            public string AddCustomEvent(string name) { Events.Add(name); return name; }
        }

        public sealed class Blueprint
        {
            public string Path { get; set; } = "";
            public Graph EventGraph { get; } = new Graph();
        }

        public sealed class Actor
        {
            public string Name { get; set; } = "";
            public V3 Position { get; set; }
            public V2 Flat { get; set; }
        }

        private static (RqlEngine e, Actor a) Engine()
        {
            var a = new Actor { Name = "a", Position = new V3 { X = 3, Y = 0, Z = 4 } };
            var e = new RqlEngine().AddSource("actors", () => new List<Actor> { a });
            e.AddAdapter(new V3Adapter()).AddAdapter(new V2Adapter());
            e.Mutations.Enabled = true;
            return (e, a);
        }

        private static string? Cell(QueryResult r, int col = 0)
        {
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            return r.Rows![0][col]?.ToJsonString();
        }

        [Fact]
        public void NumericListsScaleAndAdd()
        {
            var (e, a) = Engine();
            Assert.Equal("[3,6,0]", Cell(e.Execute("actors | select v = ([1, 2, 0] * 3)")));
            Assert.Equal("[4,6]", Cell(e.Execute("actors | select v = ([1, 2] + [3, 4])")));
            // Written to a vector member through the member's list coercion (none here: the struct rules take the list).
            Assert.True(e.Execute("actors | set Position = Position + [1, 2, 0] * 2", options: Mutate).Ok);
            Assert.Equal(5, a.Position.X);
            Assert.Equal(4, a.Position.Y);
        }

        [Theory]
        [InlineData("[1, 2] + 1", "add a list of 2 numbers")]
        [InlineData("[1, 2] * [3, 4]", "use dot(a, b)")]
        [InlineData("[1, 2] + [1, 2, 3]", "the lists have 2 and 3 numbers")]
        [InlineData("[1, \"x\"] * 2", "every list element must be a number")]
        [InlineData("1 / [1, 2]", "a number cannot be divided by a list")]
        public void ListArithmeticMismatchesSayWhy(string expr, string why)
        {
            var (e, _) = Engine();
            var r = e.Execute($"actors | select v = ({expr})");
            Assert.True(r.Ok);
            var err = e.Execute($"actors | set Name = ({expr})", options: Mutate);
            Assert.Contains(why, err.Diagnostics[0].Message);
        }

        [Fact]
        public void VectorFunctionsOnComponentStructs()
        {
            var (e, _) = Engine();
            Assert.Equal("5", Cell(e.Execute("actors | select v = Position.length()")));
            Assert.Equal("5", Cell(e.Execute("actors | select v = magnitude(Position)")));
            Assert.Equal("{\"X\":0.6,\"Y\":0,\"Z\":0.8}", Cell(e.Execute("actors | select v = normalize(Position)")));
            Assert.Equal("5", Cell(e.Execute("actors | select v = distance(Position, [0, 0, 0])")));
            Assert.Equal("3", Cell(e.Execute("actors | select v = Position.dot([1, 0, 0])")));
            Assert.Equal("[1.5,0,2]", Cell(e.Execute("actors | select v = lerp([0, 0, 0], Position, 0.5)")));
            // Two structs of different types do not mix; a struct and a list of another width neither.
            var r = e.Execute("actors | set Name = distance(Position, Flat)", options: Mutate);
            Assert.Contains("distance() needs vectors of one type", r.Diagnostics[0].Message);
            r = e.Execute("actors | set Name = dot(Position, [1, 2])", options: Mutate);
            Assert.Contains("dot() needs vectors of the same width, got 3 and 2 components", r.Diagnostics[0].Message);
            r = e.Execute("actors | set Name = length(Name)", options: Mutate);
            Assert.Contains("length() takes a vector: a list of numbers or a struct with component-wise arithmetic, got", r.Diagnostics[0].Message);
            Assert.Equal("arity", e.Execute("actors | select v = Position.lerp([1, 1, 1])").Diagnostics[0].Code);
        }

        public sealed class Holder
        {
            public int Id { get; set; } = 7;
            public List<double> Zero { get; set; } = new List<double> { 0, 0 };
            public List<double>? Copy { get; set; }
            public string Broken => throw new System.InvalidOperationException("boom");
        }

        [Fact]
        public void NormalizeOfAZeroVectorIsANewValue()
        {
            var h = new Holder();
            var e = new RqlEngine().AddSource("hs", () => new List<Holder> { h });
            e.Mutations.Enabled = true;
            Assert.True(e.Execute("hs | set Copy = normalize(Zero)", options: Mutate).Ok);
            Assert.Equal(new double[] { 0, 0 }, h.Copy);
            Assert.NotSame(h.Zero, h.Copy);
        }

        [Fact]
        public void VectorFunctionOnASizedValuePointsToLen()
        {
            var h = new Holder();
            var e = new RqlEngine().AddSource("hs", () => new List<Holder> { h });
            e.Mutations.Enabled = true;
            foreach (var q in new[] { "length(\"abc\")", "length([\"a\"])", "length({a: 1})" })
            {
                var r = e.Execute($"hs | set Copy = {q}", options: Mutate);
                Assert.EndsWith("; for the size of a string, list or map use len(…)", r.Diagnostics[0].Message);
            }
            Assert.DoesNotContain("len(", e.Execute("hs | set Copy = length(Id)", options: Mutate).Diagnostics[0].Message);
        }

        [Fact]
        public void NavigationAfterRefFailsOnItsOwnErrorAndTracksPaths()
        {
            var h = new Holder();
            var e = new RqlEngine().AddSource("hs", () => new List<Holder> { h });
            var stale = e.Execute("ref(\"Holder#99\").Zero | count");
            Assert.Equal("stale-ref", stale.Diagnostics[0].Code); // ref's own error, reported once
            var broken = e.Execute("ref(\"Holder#7\").Broken | count");
            Assert.False(broken.Ok);
            Assert.Contains("source 'ref(…).Broken' failed", broken.Diagnostics[0].Message);
            var paths = e.Execute("ref(\"Holder#7\").Zero | select $path");
            Assert.Equal("ref(…).Zero[1]", paths.Rows![1][0]!.GetValue<string>());
        }

        [Fact]
        public void FunctionSourceAndCallTargetNavigate()
        {
            var bp = new Blueprint { Path = "/Game/BP" };
            int resolved = 0;
            var e = new RqlEngine().AddFunction("unreal.asset", args => { resolved++; return (string?)args[0] == bp.Path ? bp : null; });
            e.Mutations.Enabled = true;
            var r = e.Execute("[\"Jump\", \"Land\"] | call unreal.asset(\"/Game/BP\").EventGraph.AddCustomEvent($)", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "Jump", "Land" }, bp.EventGraph.Events);
            Assert.Equal(1, resolved); // evaluated once, called per item
            Assert.Equal("Land", e.Execute("unreal.asset(\"/Game/BP\").EventGraph.Events[1]").Items![0]!.GetValue<string>());
            var missing = e.Execute("[1] | call unreal.asset(\"/Game/Nope\").EventGraph.AddCustomEvent(\"x\")", options: Mutate);
            Assert.Equal("invalid-plan", missing.Diagnostics[0].Code);
        }
    }
}
