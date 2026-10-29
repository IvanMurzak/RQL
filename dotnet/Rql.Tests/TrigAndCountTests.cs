using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>Trigonometric functions, pi(), deg() / rad() and the $count meta member (spec §3, §4.3, §8).</summary>
    public class TrigAndCountTests
    {
        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        public sealed class Node
        {
            public string Name { get; set; } = "";
            public List<double> Position { get; set; } = new List<double> { 0, 0, 0 };
            public double Value { get; set; }
        }

        public sealed class Editor
        {
            public List<(string name, double x, double z)> Created { get; } = new List<(string, double, double)>();

            [RqlCallable("Create a thing at (x, z).")]
            public string Create(string name, double x = 0, double z = 0) { Created.Add((name, x, z)); return name; }
        }

        private static object?[] Row(RqlEngine e, string rql)
        {
            var r = e.Execute(rql);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            return r.Rows![0].Select(n => n == null ? null : (object?)n.ToJsonString()).ToArray();
        }

        [Fact]
        public void TrigFunctionsWorkInRadiansAndRoundTo12Places()
        {
            var e = new RqlEngine().AddSource("g", () => new List<int> { 1 });
            var row = Row(e, "g | select s = sin(pi() / 2), c = cos(pi()), z = sin(pi()), t = tan(pi() / 4), a = asin(1), b = acos(0), n = atan(1), q = atan2(1, -1), p = pi(), d = deg(pi()), r = rad(180)");
            Assert.Equal(new object?[] { "1", "-1", "0", "1", "1.570796326795", "1.570796326795", "0.785398163397", "2.356194490192", "3.141592653589793", "180", "3.141592653589793" }, row);
        }

        [Fact]
        public void TrigFunctionsPassNullThroughAndRejectOtherValues()
        {
            var e = new RqlEngine().AddSource("g", () => new List<int> { 1 });
            var row = Row(e, "g | select a = sin(null), b = atan2(1, null), c = deg(null)");
            Assert.Equal(new object?[] { null, null, null }, row);
            var bad = e.Execute("g | select a = cos(\"x\"), b = atan2(\"y\", 1)");
            Assert.Contains("type-mismatch", bad.ToJson(includeStats: false));
            Assert.False(e.Execute("g | select pi(1)").Ok); // arity
        }

        [Fact]
        public void CountIsTheStageInputInSetValues()
        {
            var nodes = Enumerable.Range(0, 4).Select(i => new Node { Name = "n" + i }).ToList();
            var e = new RqlEngine().AddSource("nodes", () => nodes);
            e.Mutations.Enabled = true;
            var r = e.Execute("nodes | set Position = [2 * cos(2 * pi() * $index / $count), 0, 2 * sin(2 * pi() * $index / $count)], Value = $count", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.All(nodes, n => Assert.Equal(4, n.Value));
            Assert.Equal(new[] { 2.0, 0, 0 }, nodes[0].Position);
            Assert.Equal(new[] { 0.0, 0, 2 }, nodes[1].Position);
            Assert.Equal(new[] { -2.0, 0, 0 }, nodes[2].Position);
            Assert.Equal(new[] { 0.0, 0, -2 }, nodes[3].Position);
            // After a filter it counts the filtered stream; skipped null items count, like their $index.
            e.Execute("nodes | where Name != \"n0\" | set Value = $count", null, Mutate);
            Assert.Equal(new[] { 4.0, 3, 3, 3 }, nodes.Select(n => n.Value));
        }

        [Fact]
        public void CountIsTheItemCountInSourceQualifiedCalls()
        {
            var editor = new Editor();
            var e = new RqlEngine().AddSource("editor", () => editor);
            e.Mutations.Enabled = true;
            var r = e.Execute("range(0, 8) | call editor.Create(\"P\" + $index + \"of\" + $count, round(cos(2 * pi() * $ / $count), 3), round(sin(2 * pi() * $ / $count), 3))", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(8, editor.Created.Count);
            Assert.Equal(("P2of8", 0.0, 1.0), editor.Created[2]);
            Assert.Equal(("P5of8", -0.707, -0.707), editor.Created[5]);
        }

        [Fact]
        public void CountIsMissingOutsideSetAndCall()
        {
            var e = new RqlEngine().AddSource("g", () => new List<int> { 1, 2 });
            var r = e.Execute("g | select c = $count, i = $index");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Null(r.Rows![0][0]);
            Assert.DoesNotContain(r.Diagnostics, d => d.Code == "unknown-meta");
        }
    }
}
