using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    // NumericTupleAdapter: the ready-made adapter for vector / quaternion / color structs (spec §13).
    public class NumericTupleAdapterTests
    {
        public struct Vec3 { public float x; public float y; public float z; }

        public sealed class Body { public string Name { get; set; } = ""; public Vec3 Position { get; set; } }

        private sealed class Vec3Adapter : NumericTupleAdapter<Vec3>
        {
            protected override int Width => 3;
            protected override float Part(Vec3 v, int i) => i switch { 0 => v.x, 1 => v.y, _ => v.z };
            protected override Vec3? Make(float[] f) => f.Length == 3 ? new Vec3 { x = f[0], y = f[1], z = f[2] } : (Vec3?)null;
            protected override string Expected => "(x, y, z) or [x, y, z]";
        }

        private static (RqlEngine engine, List<Body> bodies) Engine()
        {
            var bodies = new List<Body>
            {
                new Body { Name = "a", Position = new Vec3 { x = 1, y = 2, z = 3 } },
                new Body { Name = "b", Position = new Vec3 { x = 0, y = 1, z = 0 } },
                new Body { Name = "c", Position = new Vec3 { x = 0, y = 1.000001f, z = 0 } },
            };
            var e = new RqlEngine().AddSource("bodies", () => bodies).AddAdapter(new Vec3Adapter());
            e.Mutations.Enabled = true;
            return (e, bodies);
        }

        private static string Cell(QueryResult r, int row, int col) => r.Rows![row][col]!.ToJsonString().Trim('"');

        [Fact]
        public void EncodesCompactly()
        {
            var (e, _) = Engine();
            var r = e.Execute("bodies | where Name == \"a\" | select Position");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("(1, 2, 3)", Cell(r, 0, 0));
        }

        [Fact]
        public void ComparesApproximatelyWithTextAndListLiterals()
        {
            var (e, _) = Engine();
            var text = e.Execute("bodies | where Position == \"(0, 1, 0)\" | select Name");
            Assert.True(text.Ok, text.ToJson(includeStats: false));
            Assert.Equal(new[] { "b", "c" }, Enumerable.Range(0, text.Rows!.Count).Select(i => Cell(text, i, 0)));
            var list = e.Execute("bodies | where Position == [1, 2, 3] | select Name");
            Assert.True(list.Ok, list.ToJson(includeStats: false));
            Assert.Equal("a", Cell(list, 0, 0));
            Assert.Single(list.Rows!);
        }

        [Fact]
        public void NaNComponentsAreNeverEqual()
        {
            var (e, bodies) = Engine();
            bodies[1].Position = new Vec3 { x = float.NaN, y = 1, z = 0 };
            var r = e.Execute("bodies | where Position == \"(0, 1, 0)\" | select Name");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "c" }, Enumerable.Range(0, r.Rows!.Count).Select(i => Cell(r, i, 0)));
        }

        [Fact]
        public void WritesTextListAndObjectLiterals()
        {
            var (e, bodies) = Engine();
            Assert.True(e.Execute("bodies | where Name == \"a\" | set Position = \"(4, 5, 6)\"", options: new QueryOptions { AllowMutation = true }).Ok);
            Assert.Equal(5, bodies[0].Position.y);
            Assert.True(e.Execute("bodies | where Name == \"a\" | set Position = [7, 8, 9]", options: new QueryOptions { AllowMutation = true }).Ok);
            Assert.Equal(9, bodies[0].Position.z);
            Assert.True(e.Execute("bodies | where Name == \"a\" | set Position = {x: 1, y: 1, z: 1}", options: new QueryOptions { AllowMutation = true }).Ok);
            Assert.Equal(1, bodies[0].Position.x);
        }

        [Fact]
        public void RejectsWrongShapesWithTheExpectedForm()
        {
            var (e, bodies) = Engine();
            var r = e.Execute("bodies | where Name == \"a\" | set Position = \"(1, 2)\"", options: new QueryOptions { AllowMutation = true });
            Assert.False(r.Ok);
            Assert.Contains("(x, y, z) or [x, y, z]", r.ToJson(includeStats: false));
            var words = e.Execute("bodies | where Name == \"a\" | set Position = [1, \"two\", 3]", options: new QueryOptions { AllowMutation = true });
            Assert.False(words.Ok);
            Assert.Equal(3, bodies[0].Position.z);
        }
    }
}
