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

        [Fact]
        public void PiTakesNoArguments()
        {
            // Values, null and type errors are in the shared fixtures (trig-functions, trig-null-and-type-mismatch).
            var e = new RqlEngine().AddSource("g", () => new List<int> { 1 });
            Assert.False(e.Execute("g | select pi(1)").Ok);
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
        public void CountIsMissingWithAWarningOutsideSetAndCall()
        {
            var e = new RqlEngine().AddSource("g", () => new List<int> { 1, 2 });
            var r = e.Execute("g | select c = $count, i = $index");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Null(r.Rows![0][0]);
            Assert.Contains(r.Diagnostics, d => d.Code == "unknown-meta" && d.Message.Contains("only defined in a set value or call argument"));
            // Inside a collection method of a set value $index is the element's, so $count is not defined there either.
            var nodes = new List<Node> { new Node { Name = "a" }, new Node { Name = "b" } };
            var m = new RqlEngine().AddSource("nodes", () => nodes);
            m.Mutations.Enabled = true;
            var w = m.Execute("nodes | set Value = Position.count($count > 0)", null, Mutate);
            Assert.True(w.Ok, w.ToJson(includeStats: false));
            Assert.Equal(new[] { 0.0, 0 }, nodes.Select(n => n.Value)); // the stage's 2 would count all 3
            Assert.DoesNotContain(w.Diagnostics, d => d.Code == "unknown-meta");
        }
    }
}
