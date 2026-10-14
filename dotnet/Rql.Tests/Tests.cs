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
        public void ParseValueReadsOneLiteral()
        {
            var o = Assert.IsType<ObjectExpr>(RqlParser.ParseValue("{name: \"Lamp\", \"quoted key\": -2.5, tags: [1, true, null]}"));
            Assert.Equal(new[] { "name", "quoted key", "tags" }, o.Entries.Select(e => e.Key));
            Assert.Equal(-2.5, Assert.IsType<LitExpr>(o.Entries[1].Value).Value);
            Assert.Equal(3, Assert.IsType<ListExpr>(o.Entries[2].Value).Items.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{a: 1} {b: 2}")]
        [InlineData("{a: }")]
        public void ParseValueRejectsAnythingButOneExpression(string text) =>
            Assert.Equal("syntax-error", Assert.Throws<RqlException>(() => RqlParser.ParseValue(text)).Code);

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

namespace Rql.Tests
{
    using System;
    using System.Collections.Generic;
    using Rql.Execution;
    using Xunit;

    public struct V3 { public float x, y, z; }

    public sealed class GuardHost
    {
        public readonly V3 Origin;
        [RqlReadOnly] public V3 Locked { get; set; }
        public V3 Policy { get; set; }
        public V3 GetOnly => default;
        public V3 Free { get; set; }
        public Inner Bad => throw new InvalidOperationException("boom");
        public List<GuardHost> Kids { get; set; } = new List<GuardHost>();
        public sealed class Inner { public int A { get; set; } }
    }

    /// <summary>Regression tests for the independent review findings (security + robustness).</summary>
    public class HostValueApiTests
    {
        private static SampleHost Host() => new SampleHost(7, WorldSize.Small);

        [Fact]
        public void TryEvaluateValueBuildsTypedObjectsFromLiterals()
        {
            var h = Host();
            Assert.True(h.Engine.TryEvaluateValue("{Width: 800, Quality: Low, Fullscreen: false}", typeof(GraphicsSettings), out var v, out var err), err);
            var g = Assert.IsType<GraphicsSettings>(v);
            Assert.Equal(800, g.Width);
            Assert.Equal(Quality.Low, g.Quality);
            Assert.False(g.Fullscreen);
        }

        [Fact]
        public void TryEvaluateValueResolvesRefsAndBareEnumWords()
        {
            var h = Host();
            var player = h.Sandbox.World.LocalPlayer!;
            Assert.True(h.Engine.TryEvaluateValue($"ref(\"Player#{player.Id}\")", typeof(Player), out var v, out var err), err);
            Assert.Same(player, v);
            Assert.True(h.Engine.TryEvaluateValue("Directional", typeof(LightType), out var e, out err), err);
            Assert.Equal(LightType.Directional, e);
            Assert.True(h.Engine.TryEvaluateValue("[1, 2, 3]", typeof(List<int>), out var list, out err), err);
            Assert.Equal(new List<int> { 1, 2, 3 }, list);
        }

        [Fact]
        public void TryEvaluateValueReportsWhatWentWrong()
        {
            var h = Host();
            Assert.False(h.Engine.TryEvaluateValue("{Width: }", typeof(GraphicsSettings), out _, out var syntax));
            Assert.False(string.IsNullOrEmpty(syntax));
            Assert.False(h.Engine.TryEvaluateValue("{Width: \"wide\"}", typeof(GraphicsSettings), out _, out var type));
            Assert.Contains("Width", type);
        }

        [Fact]
        public void PresentEncodesObjectsListsAndScalars()
        {
            var h = Host();
            var one = h.Engine.Present(new GraphicsSettings { Width = 640 });
            Assert.True(one.Ok);
            Assert.Contains("640", AgentFormat.Render(one, OutputFormat.Table));
            var many = h.Engine.Present(new List<AudioSettings> { new AudioSettings(), new AudioSettings { Muted = true } });
            Assert.Equal(2, many.Items!.Count);
            Assert.Equal(2, many.Total);
            var nested = AgentFormat.Render(h.Engine.Present(new Settings()), OutputFormat.Table);
            Assert.Contains("1920", nested);                       // Graphics.Width: nested plain objects written out
            var scalar = h.Engine.Present(42);
            Assert.Equal(42, scalar.Value!.GetValue<long>());
        }
    }

    public class ReviewRegressionTests
    {
        private static RqlEngine Engine(GuardHost h, int maxTargets = 20)
        {
            var e = new RqlEngine();
            e.AddSource("h", () => h);
            e.AddSource("many", () => { var l = new List<GuardHost>(); for (int i = 0; i < 50; i++) l.Add(new GuardHost()); return l; });
            e.Mutations.Enabled = true;
            e.Mutations.MaxTargets = maxTargets;
            e.Mutations.HardMaxTargets = 30;
            e.Mutations.CanWrite = (t, m) => m.Name != "Policy";
            return e;
        }

        private static QueryResult Mut(RqlEngine e, string q, Budgets? b = null) => e.Execute(q, b, new QueryOptions { AllowMutation = true });

        [Theory]
        [InlineData("h | set Origin.x = 7", "readonly-member")]
        [InlineData("h | set Locked.x = 7", "readonly-member")]
        [InlineData("h | set Policy.x = 7", "permission-denied")]
        [InlineData("h | set GetOnly.x = 7", "readonly-member")]
        public void StructWriteBackRespectsParentWritability(string q, string code)
        {
            var h = new GuardHost();
            var r = Mut(Engine(h), q);
            Assert.False(r.Ok);
            Assert.Equal(code, r.Diagnostics[0].Code);
            Assert.Equal(0f, h.Origin.x);
            Assert.Equal(0f, h.Locked.x);
            Assert.Equal(0f, h.Policy.x);
        }

        [Fact]
        public void StructWriteBackStillWorksWhenAllowed()
        {
            var h = new GuardHost();
            Assert.True(Mut(Engine(h), "h | set Free.x = 7").Ok);
            Assert.Equal(7f, h.Free.x);
        }

        [Fact]
        public void GetterFailureDuringSetIsADiagnostic()
        {
            var r = Mut(Engine(new GuardHost()), "h | set Bad.A = 1");
            Assert.False(r.Ok);
            Assert.Equal("getter-threw", r.Diagnostics[0].Code);
        }

        [Fact]
        public void RequestCannotExceedHardMutationCeiling()
        {
            var e = Engine(new GuardHost());
            var r = Mut(e, "many | set Free.x = 1", new Budgets { MaxMutationTargets = 1000 });
            Assert.False(r.Ok);
            Assert.Equal("mutation-limit", r.Diagnostics[0].Code);
        }

        [Theory]
        [InlineData(200)]
        [InlineData(20000)]
        public void DeepNestingIsASyntaxErrorNotACrash(int depth)
        {
            var q = "h | where " + new string('(', depth) + "1" + new string(')', depth) + " == 1";
            var r = Engine(new GuardHost()).Execute(q);
            Assert.False(r.Ok);
            Assert.Equal("syntax-error", r.Diagnostics[0].Code);
            var n = Engine(new GuardHost()).Execute("h | where " + string.Concat(System.Linq.Enumerable.Repeat("not ", depth)) + "true");
            Assert.Equal("syntax-error", n.Diagnostics[0].Code);
        }

        [Fact]
        public void CollectionMethodsAreBudgeted()
        {
            var sb = Rql.Sample.WorldGenerator.Create(1, Rql.Sample.WorldSize.Large);
            var host = new Rql.Sample.SampleHost(sb);
            var r = host.Query("world | select c=(ItemCatalog.count(ItemCatalog.count(ItemCatalog.count(true) > 0) > 0))", new Budgets { TimeoutMs = 500 });
            Assert.False(r.Ok);
            Assert.Contains(r.Diagnostics[0].Code, new[] { "timeout", "budget-exceeded" });
            Assert.True(r.Stats.ElapsedMs < 5000, "elapsed " + r.Stats.ElapsedMs);
        }

        [Fact]
        public void TableRenderingSurvivesNonStringRefColumns()
        {
            var r = Engine(new GuardHost()).Execute("h | select x=Free");
            Assert.True(r.Ok);
            Assert.NotNull(AgentFormat.Render(r, OutputFormat.Table));
        }
    }
}
