using System.Linq;
using System.Text.Json.Nodes;
using Rql.Conformance;
using Rql.Execution;
using Rql.Sample;
using Xunit;

namespace Rql.Tests
{
    public class ConformanceTests
    {
        [Theory]
        [InlineData(AccessorMode.Compiled)]
        [InlineData(AccessorMode.Reflection)]
        public void GoldenVectorsPass(AccessorMode mode)
        {
            var suite = new ConformanceSuite { Accessors = mode };
            suite.RunAll(regen: false);
            Assert.True(suite.Failures.Count == 0, string.Join("\n", suite.Failures.Take(10)));
            Assert.True(suite.Passed > 300);
        }
    }

    public class ParserTests
    {
        [Fact]
        public void SyntaxErrorCarriesPosition()
        {
            var ex = Assert.Throws<RqlException>(() => RqlParser.Parse("world | where (Health > 1"));
            Assert.Equal("syntax-error", ex.Code);
            Assert.NotNull(ex.At);
        }

        [Fact]
        public void SelectWithOperatorSuggestsParentheses()
        {
            var ex = Assert.Throws<RqlException>(() => RqlParser.Parse("objects | select Health / MaxHealth"));
            Assert.Contains("parentheses", ex.Hint);
        }

        [Fact]
        public void PlanRoundTripsThroughCodec()
        {
            var plan = RqlParser.Parse("objects | where Components.any($ is LightComponent and Enabled == false) | sort Name desc | first 3 | select $ref Name p=(Transform.Position.y * 2)");
            var json = PlanCodec.Serialize(plan);
            Assert.Equal(json, PlanCodec.Serialize(PlanCodec.Deserialize(json)));
        }
    }

    public class SampleRuntimeTests
    {
        private static SampleHost Host(bool mutations = false) => new SampleHost(7, WorldSize.Small, mutations);

        private static JsonNode? Scalar(QueryResult r)
        {
            Assert.True(r.Ok, string.Join("; ", r.Diagnostics));
            return r.Value;
        }

        [Fact]
        public void GeneratorIsDeterministic()
        {
            var a = GraphSnapshot.Capture(new SampleHost(42, WorldSize.Small));
            var b = GraphSnapshot.Capture(new SampleHost(42, WorldSize.Small));
            var c = GraphSnapshot.Capture(new SampleHost(43, WorldSize.Small));
            Assert.Empty(GraphSnapshot.Diff(a, b));
            Assert.NotEmpty(GraphSnapshot.Diff(a, c));
            Assert.True(a.Count > 1000);
        }

        [Fact]
        public void FlattenedObjectsSourceMatchesRecursion()
        {
            var h = Host();
            var flat = Scalar(h.Query("objects | count"))!.GetValue<long>();
            var rec = Scalar(h.Query("world.Scenes | get Objects | recurse Children include-self=true | count"))!.GetValue<long>();
            Assert.Equal(flat, rec);
            Assert.Equal(WorldGenerator.AllObjects(h.Sandbox.World).Count(), flat);
        }

        [Fact]
        public void FloatFieldComparesInFloatPrecision()
        {
            var h = Host();
            var light = WorldGenerator.AllObjects(h.Sandbox.World).SelectMany(g => g.Components).OfType<LightComponent>().First();
            var text = light.Intensity.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var n = Scalar(h.Query($"objects | get Components | where $ is LightComponent and Intensity == {text} | count"))!.GetValue<long>();
            Assert.True(n >= 1);
        }

        [Fact]
        public void StructWriteBackThroughClassAndProperty()
        {
            var h = Host(mutations: true);
            var player = h.Sandbox.World.LocalPlayer!;
            float y0 = player.Transform.Position.y;
            var r = h.Mutate($"ref(\"Player#{player.Id}\") | set Transform.Position.y = Transform.Position.y + 2.5", dryRun: false);
            Assert.True(r.Ok, string.Join("; ", r.Diagnostics));
            Assert.Equal(y0 + 2.5f, player.Transform.Position.y, 3);
        }

        [Fact]
        public void DryRunChangesNothing()
        {
            var h = Host(mutations: true);
            var before = GraphSnapshot.Capture(h);
            var r = h.Mutate("objects | where $ is Enemy | first 5 | set Health = 1", dryRun: true);
            Assert.True(r.Ok && r.DryRun);
            Assert.Equal(5, r.Rows!.Count);
            Assert.Empty(GraphSnapshot.Diff(before, GraphSnapshot.Capture(h)));
        }

        [Fact]
        public void MutationsDeniedWhenHostDisallows()
        {
            var h = Host(mutations: false);
            var r = h.Mutate("objects | first 1 | set Enabled = false", dryRun: false);
            Assert.False(r.Ok);
            Assert.Equal("permission-denied", r.Diagnostics[0].Code);
            var q = h.Query("objects | first 1 | set Enabled = false");
            Assert.Equal("permission-denied", q.Diagnostics[0].Code);
        }

        [Fact]
        public void CallAllowlistedMethodAndDetectStaleRef()
        {
            var h = Host(mutations: true);
            var enemy = WorldGenerator.AllObjects(h.Sandbox.World).OfType<Enemy>().First(e => e.Health < e.MaxHealth);
            var heal = h.Mutate($"ref(\"Enemy#{enemy.Id}\") | call Heal(1000000)", dryRun: false);
            Assert.True(heal.Ok, string.Join("; ", heal.Diagnostics));
            Assert.Equal(enemy.MaxHealth, heal.Rows![0][2]!.GetValue<long>());

            var destroy = h.Mutate($"ref(\"Enemy#{enemy.Id}\") | call Destroy", dryRun: false);
            Assert.True(destroy.Ok, string.Join("; ", destroy.Diagnostics));
            var after = h.Query($"ref(\"Enemy#{enemy.Id}\") | select Name");
            Assert.False(after.Ok);
            Assert.Equal("stale-ref", after.Diagnostics[0].Code);
            Assert.DoesNotContain(enemy, WorldGenerator.AllObjects(h.Sandbox.World));
        }

        [Fact]
        public void NonCallableMethodIsDenied()
        {
            var h = Host(mutations: true);
            var r = h.Mutate("objects | first 1 | call GetHashCode()", dryRun: false);
            Assert.False(r.Ok);
            Assert.Equal("permission-denied", r.Diagnostics[0].Code);
        }

        [Fact]
        public void ReadOnlyComputedMemberRejected()
        {
            var h = Host(mutations: true);
            var r = h.Mutate("world.Quests | first 1 | get Objectives | first 1 | set Done = true", dryRun: false);
            Assert.False(r.Ok);
            Assert.Equal("readonly-member", r.Diagnostics[0].Code);
        }

        [Fact]
        public void FirstIsLazy()
        {
            var h = new SampleHost(1, WorldSize.Large);
            var r = h.Query("objects | where $ is Enemy | first 1 | select Name");
            Assert.True(r.Ok);
            Assert.True(r.Stats.Visited < 2000, "visited " + r.Stats.Visited);
        }

        [Theory]
        [InlineData("objects | where $ is Enemy | sort Level desc | first 7 | select $ref Level")]
        [InlineData("objects | sort Layer, Name desc | first 25 | select $ref")]
        [InlineData("objects | get Components | sort $type, Enabled | first 40 | select $ref")]
        [InlineData("app.Orders | sort Total desc, Id | first 10 | select Id Total")]
        public void TopNMatchesFullSort(string rql)
        {
            var on = new SampleHost(3, WorldSize.Medium);
            var off = new SampleHost(3, WorldSize.Medium);
            off.Engine.DisableTopN = true;
            var a = on.Query(rql, new Budgets { MaxRows = 1000 });
            var b = off.Query(rql, new Budgets { MaxRows = 1000 });
            Assert.True(a.Ok && b.Ok);
            Assert.Equal(b.ToJson(includeStats: false), a.ToJson(includeStats: false));
        }

        [Fact]
        public void UnknownSourceSuggests()
        {
            var r = Host().Query("wrld.Scenes | count");
            Assert.Equal("unknown-source", r.Diagnostics[0].Code);
            Assert.Contains("world", r.Diagnostics[0].Hint);
        }

        [Fact]
        public void UnknownMemberHintsAndQuotes()
        {
            var r = Host().Query("objects | get Components | where Type == Point | count");
            var d = r.Diagnostics.Single(x => x.Code == "unknown-member" && x.Message.Contains("'Point'"));
            Assert.Contains("\"Point\"", d.Hint);
        }

        [Fact]
        public void BudgetExceededIsAnError()
        {
            var r = Host().Query("world | recurse max-depth=16 | count", new Budgets { MaxVisited = 50 });
            Assert.False(r.Ok);
            Assert.Equal("budget-exceeded", r.Diagnostics[0].Code);
        }

        [Fact]
        public void DescribeAndSchema()
        {
            var h = Host();
            var d = h.Query("objects | where $ is Enemy | first 1 | describe");
            Assert.True(d.Ok);
            Assert.Equal("Enemy", d.Types![0]!["name"]!.GetValue<string>());
            var schema = h.Schema();
            Assert.Contains("Enemy : Character", schema);
            Assert.Contains("enum LightType: Point Spot Directional Area", schema);
            Assert.Contains("objects:", schema);
        }

        [Fact]
        public void TableFormatIsCompact()
        {
            var r = Host().Query("objects | where $ is Enemy | sort Health | first 2 | select $ref Name Health");
            var text = AgentFormat.Render(r, OutputFormat.Table);
            var lines = text.Split('\n');
            Assert.Equal("$ref|Name|Health", lines[1]);
            Assert.StartsWith("Enemy#", lines[2]);
        }

        [Fact]
        public void IdentitylessObjectsInline()
        {
            var r = Host().Query("world.Settings | select Graphics");
            var g = r.Rows![0][0]!.AsObject();
            Assert.True(g.ContainsKey("Width"));
        }
    }
}
