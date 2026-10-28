using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    // Wave-5 semantics: component-wise struct arithmetic (spec §4.2), absent map keys as null (§3), rounded struct keys for
    // distinct / group-by (§5) and nested struct members in unknown-member hints (§10).
    public class Wave5Tests
    {
        public struct Vec3 { public float x; public float y; public float z; }
        public struct Vec2f { public float x; public float y; }
        public struct Quat { public float x; public float y; public float z; public float w; }
        public struct Body3 { public string CollisionProfileName; public Inner Settings; }
        public struct Inner { public float Damping; }

        public sealed class Thing
        {
            public string Name { get; set; } = "";
            public Vec3 Scale { get; set; }
            public Vec2f Size { get; set; }
            public Quat Rotation { get; set; }
            public Body3 BodyInstance { get; set; }
            public Dictionary<string, double> Props { get; set; } = new Dictionary<string, double>();
        }

        private sealed class Vec3Adapter : NumericTupleAdapter<Vec3>
        {
            private readonly bool _arithmetic;
            public Vec3Adapter(bool arithmetic) { _arithmetic = arithmetic; }
            protected override bool Arithmetic => _arithmetic;
            protected override int Width => 3;
            protected override float Part(Vec3 v, int i) => i switch { 0 => v.x, 1 => v.y, _ => v.z };
            protected override Vec3? Make(float[] f) => f.Length == 3 ? new Vec3 { x = f[0], y = f[1], z = f[2] } : (Vec3?)null;
            protected override string Expected => "(x, y, z)";
        }

        private sealed class Vec2Adapter : NumericTupleAdapter<Vec2f>
        {
            protected override bool Arithmetic => true;
            protected override int Width => 2;
            protected override float Part(Vec2f v, int i) => i == 0 ? v.x : v.y;
            protected override Vec2f? Make(float[] f) => f.Length == 2 ? new Vec2f { x = f[0], y = f[1] } : (Vec2f?)null;
            protected override string Expected => "(x, y)";
        }

        // Quaternions are tuples too, but their product is not component-wise: no Arithmetic opt-in.
        private sealed class QuatAdapter : NumericTupleAdapter<Quat>
        {
            protected override int Width => 4;
            protected override float Part(Quat q, int i) => i switch { 0 => q.x, 1 => q.y, 2 => q.z, _ => q.w };
            protected override Quat? Make(float[] f) => f.Length == 4 ? new Quat { x = f[0], y = f[1], z = f[2], w = f[3] } : (Quat?)null;
            protected override string Expected => "(x, y, z, w)";
        }

        private static (RqlEngine engine, List<Thing> things) Engine(bool arithmetic = true, bool adapters = true)
        {
            var things = new List<Thing>
            {
                new Thing { Name = "Coin_1", Scale = new Vec3 { x = 1, y = 1, z = 1 }, Size = new Vec2f { x = 1, y = 2 }, Props = { ["mp"] = 5 } },
                new Thing { Name = "Coin_2", Scale = new Vec3 { x = 0.99999994f, y = 1, z = 1 }, Size = new Vec2f { x = 1, y = 2 } },
            };
            var e = new RqlEngine().AddSource("things", () => things);
            if (adapters) e.AddAdapter(new Vec3Adapter(arithmetic)).AddAdapter(new Vec2Adapter()).AddAdapter(new QuatAdapter());
            e.Mutations.Enabled = true;
            return (e, things);
        }

        private static string Json(QueryResult r) => r.ToJson(includeStats: false);

        private static QueryResult Mutate(RqlEngine e, string q) => e.Execute(q, null, new QueryOptions { AllowMutation = true });

        [Fact]
        public void ScalesAVectorMemberWhenTheAdapterOptsIn()
        {
            var (e, things) = Engine();
            var r = Mutate(e, "things | where Name starts-with \"Coin_\" | set Scale = (Scale * 1.5)");
            Assert.True(r.Ok, Json(r));
            Assert.Equal(1.5f, things[0].Scale.x);
            Assert.Equal(1.5f, things[0].Scale.z);
            Assert.Equal((float)(0.99999994 * 1.5), things[1].Scale.x, 6);
        }

        [Fact]
        public void ComponentWiseOperatorsAndLists()
        {
            var (e, _) = Engine();
            var r = e.Execute("things | first 1 | select a=(Scale + [0, 1, 0]) b=(-Scale) c=(2 * Scale - Scale / 2) d=(Scale * Scale) f=(Scale * 0.1 * 3)");
            Assert.True(r.Ok, Json(r));
            var row = r.Rows![0].Select(c => c!.ToJsonString().Trim('"')).ToArray();
            Assert.Equal(new[] { "(1, 2, 1)", "(-1, -1, -1)", "(1.5, 1.5, 1.5)", "(1, 1, 1)", "(0.3, 0.3, 0.3)" }, row);
        }

        [Fact]
        public void MismatchesAreClearErrors()
        {
            var (e, _) = Engine();
            var r = Mutate(e, "things | set Scale = Scale + Size");
            Assert.False(r.Ok);
            Assert.Contains("different types", Json(r));
            var q = Mutate(e, "things | set Rotation = Rotation * 2");
            Assert.False(q.Ok);
            Assert.Contains("Quat has no component-wise arithmetic", Json(q));
            var n = Mutate(e, "things | set Scale = Scale + 1");
            Assert.False(n.Ok);
            Assert.Contains("multiply or divide by a number", Json(n));
        }

        [Fact]
        public void WithoutOptInVectorsKeepTypeMismatch()
        {
            var (e, _) = Engine(arithmetic: false);
            var r = Mutate(e, "things | set Scale = Scale * 1.5");
            Assert.False(r.Ok);
            Assert.Contains("Vec3 has no component-wise arithmetic", Json(r));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DistinctMergesFloatStructsThatPrintAlike(bool adapters)
        {
            var (e, _) = Engine(adapters: adapters);
            var r = e.Execute("things | select Scale | distinct");
            Assert.True(r.Ok, Json(r));
            Assert.Single(r.Rows!);
            var g = e.Execute("things | group-by Scale");
            Assert.True(g.Ok, Json(g));
            Assert.Single(g.Rows!);
            Assert.Equal("2", g.Rows![0][1]!.ToJsonString());
        }

        [Fact]
        public void DistinctKeepsStructsThatPrintDifferently()
        {
            var (e, things) = Engine();
            things[1].Scale = new Vec3 { x = 1.00001f, y = 1, z = 1 };
            var r = e.Execute("things | select Scale | distinct");
            Assert.True(r.Ok, Json(r));
            Assert.Equal(2, r.Rows!.Count);
        }

        [Fact]
        public void AbsentMapKeyIsNullButHasTestsPresence()
        {
            var (e, _) = Engine();
            var r = e.Execute("things | select Name n=(Props[\"mp\"] is null) h=has(Props[\"mp\"])");
            Assert.True(r.Ok, Json(r));
            Assert.Equal("[[\"Coin_1\",false,true],[\"Coin_2\",true,false]]",
                "[" + string.Join(",", r.Rows!.Select(row => "[" + string.Join(",", row.Select(c => c!.ToJsonString())) + "]")) + "]");
        }

        [Fact]
        public void UnknownMemberHintFindsNestedStructMembers()
        {
            var (e, _) = Engine();
            var w = Mutate(e, "things | set CollisionProfileName = \"NoCollision\"");
            Assert.False(w.Ok);
            Assert.Contains("did you mean BodyInstance.CollisionProfileName", Json(w));
            var deep = e.Execute("things | where Damping > 0 | count");
            Assert.Contains("BodyInstance.Settings.Damping", Json(deep));
            var direct = e.Execute("things | select Nmae");
            Assert.Contains("did you mean Name", Json(direct));
        }
    }
}
