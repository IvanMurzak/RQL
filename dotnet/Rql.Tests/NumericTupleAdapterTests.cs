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

        [Theory]
        [InlineData(0.1, "0.1")]
        [InlineData(1.0 / 3, "0.333333333333333")]
        [InlineData(123456789.125, "123456789.125")]
        [InlineData(1e-7, "1E-07")]
        public void NumbersPrintShortestRoundTrip(double d, string expectedPrefix)
        {
            var s = V.FormatNumber(d);
            Assert.StartsWith(expectedPrefix, s);
            Assert.Equal(d, double.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("0.1", V.FormatNumber(0.1f));
        }

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
        public void FloatsPrintShortestNotNineDigits()
        {
            Assert.Equal("1.5707964", V.FormatNumber((float)(System.Math.PI / 2)));
            Assert.Equal("0.53333336", V.FormatNumber(136f / 255f));
            Assert.Equal("0.30000000000000004", V.FormatNumber(0.1 + 0.2)); // the exact JSON form keeps double noise
        }

        [Theory]
        [InlineData(9.999998f, 45f, 29.9999981f, "(10, 45, 30)")]
        [InlineData(-8.742278E-08f, 0f, -2f, "(0, 0, -2)")]
        [InlineData(-0f, 1.5707964f, 12345.67f, "(0, 1.5708, 12345.7)")]
        [InlineData(float.NaN, float.PositiveInfinity, float.NegativeInfinity, "(NaN, Infinity, -Infinity)")]
        public void DisplayTextIsTidy(float x, float y, float z, string expected)
        {
            var (e, bodies) = Engine();
            bodies[0].Position = new Vec3 { x = x, y = y, z = z };
            var r = e.Execute("bodies | where Name == \"a\" | select Position");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(expected, Cell(r, 0, 0));
        }

        [Fact]
        public void DisplayedValuesCompareEqualToTheirSource()
        {
            var (e, bodies) = Engine();
            bodies[0].Position = new Vec3 { x = 12345.67f, y = 9.999998f, z = 0 };
            var shown = Cell(e.Execute("bodies | where Name == \"a\" | select Position"), 0, 0);
            var r = e.Execute("bodies | where Position == \"" + shown + "\" | select Name");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("a", Cell(r, 0, 0));
        }

        [Theory]
        [InlineData("(inf, 0, 0)", float.PositiveInfinity)]
        [InlineData("(-INF, 0, 0)", float.NegativeInfinity)]
        [InlineData("(+Infinity, 0, 0)", float.PositiveInfinity)]
        [InlineData("(-infinity, 0, 0)", float.NegativeInfinity)]
        [InlineData("(nan, 0, 0)", float.NaN)]
        [InlineData("(Infinity, 0, 0)", float.PositiveInfinity)]
        public void ParsesNonFiniteTokens(string text, float expected)
        {
            var (e, bodies) = Engine();
            var r = e.Execute("bodies | where Name == \"a\" | set Position = \"" + text + "\"", options: new QueryOptions { AllowMutation = true });
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(expected, bodies[0].Position.x);
            Assert.False(e.Execute("bodies | where Name == \"a\" | set Position = \"(--inf, 0, 0)\"", options: new QueryOptions { AllowMutation = true }).Ok);
        }

        [Fact]
        public void ComparesWithObjectLiteralsByMemberName()
        {
            var (e, _) = Engine();
            string Names(string q)
            {
                var r = e.Execute(q);
                Assert.True(r.Ok, r.ToJson(includeStats: false));
                return string.Join(",", Enumerable.Range(0, r.Rows!.Count).Select(i => Cell(r, i, 0)));
            }
            Assert.Equal("a", Names("bodies | where Position == {x: 1, y: 2, z: 3} | select Name"));
            Assert.Equal("b,c", Names("bodies | where Position == {x: 0, y: 1, z: 0} | select Name"));
            Assert.Equal("b,c", Names("bodies | where Position == {Y: 1} | select Name"));     // left-out members match
            Assert.Equal("a", Names("bodies | where Position != {y: 1} | select Name"));
            Assert.Equal("", Names("bodies | where Position == {w: 1} | select Name"));       // unknown member: unknown
            Assert.Equal("", Names("bodies | where Position != {w: 1} | select Name"));
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
