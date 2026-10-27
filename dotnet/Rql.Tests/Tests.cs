using System.Collections.Generic;
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

    /// <summary>Wave-2 findings: writes through streamed map entries (U5) and host text coercion (U6).</summary>
    public class MapEntryWriteTests
    {
        public struct Row { public float Health; public bool IsElite; }
        public struct Anchors { public float Min, Max; }
        public sealed class Table
        {
            public Dictionary<string, Row> Rows { get; } = new Dictionary<string, Row>
            {
                ["Orc"] = new Row { Health = 100, IsElite = true },
                ["Rat"] = new Row { Health = 10, IsElite = false },
                ["Troll"] = new Row { Health = 300, IsElite = true },
            };
            public Anchors Slot { get; set; }
        }

        private static RqlEngine Engine(Table t, out List<MutationRecord> before)
        {
            var e = new RqlEngine().AddSource("table", () => t);
            e.Mutations.Enabled = true;
            var seen = before = new List<MutationRecord>();
            e.Mutations.BeforeWrite = records => seen.AddRange(records);
            return e;
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        [Fact]
        public void SetThroughStreamedEntriesWritesBackStructValuesAndNamesTheHolderAsOwner()
        {
            var t = new Table();
            var r = Engine(t, out var before).Execute("table | get Rows | where Value.IsElite | set Value.Health = Value.Health * 1.1", null, Mutate);
            Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
            Assert.Equal(110f, t.Rows["Orc"].Health, 3);
            Assert.Equal(330f, t.Rows["Troll"].Health, 3);
            Assert.Equal(10f, t.Rows["Rat"].Health);
            Assert.Equal(new[] { "Rows[\"Orc\"].Health", "Rows[\"Troll\"].Health" }, before.Select(b => b.Member));
            Assert.All(before, b => Assert.Same(t, b.Owner));
            Assert.All(before, b => Assert.Same(t, b.Owners[b.Owners.Count - 1]));
        }

        [Fact]
        public void HostTextCoercionConvertsStringsForStructsNoAdapterHandles()
        {
            var t = new Table();
            var e = Engine(t, out _);
            Assert.Equal("coercion-failed", e.Execute("table | set Slot = \"(Min=0.5,Max=1)\"", null, Mutate).Diagnostics[0].Code);
            e.Mutations.CoerceText = (type, text) =>
            {
                if (type != typeof(Anchors)) return null;
                var m = System.Text.RegularExpressions.Regex.Match(text, @"^\(Min=([\d.]+),Max=([\d.]+)\)$");
                if (!m.Success) throw new FormatException("expected (Min=…,Max=…)");
                return new Anchors { Min = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), Max = float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) };
            };
            Assert.True(e.Execute("table | set Slot = \"(Min=0.5,Max=1)\"", null, Mutate).Ok);
            Assert.Equal(0.5f, t.Slot.Min);
            Assert.Equal(1f, t.Slot.Max);
            var bad = e.Execute("table | set Slot = \"nope\"", null, Mutate).Diagnostics[0];
            Assert.Equal("coercion-failed", bad.Code);
            Assert.Contains("expected (Min=…,Max=…)", bad.Message);
        }

        [Fact]
        public void ParenthesizedTupleHintsAtObjectLiterals()
        {
            var r = Engine(new Table(), out _).Execute("table | set Slot = {Min: (0, 0)}", null, Mutate);
            Assert.Equal("syntax-error", r.Diagnostics[0].Code);
            Assert.Contains("object literal {X: 0, Y: 0}", r.Diagnostics[0].Hint);
        }
    }

    /// <summary>Regression tests for the independent review findings (security + robustness).</summary>
    public class HostValueApiTests
    {
        public sealed class NamedThing
        {
            [RqlDisplay] public string Name { get; set; } = "";
            public int Count { get; set; }
        }

        [Fact]
        public void PresentShowsListsOfPlainObjects()
        {
            var text = AgentFormat.Render(new SampleHost(7, WorldSize.Small).Engine.Present(new List<NamedThing> { new NamedThing { Name = "A", Count = 1 }, new NamedThing { Name = "B", Count = 2 } }), OutputFormat.Table);
            Assert.Contains("B", text);
        }

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
            // Plain objects with a display member (Name) are written out in full, not as summaries.
            var named = AgentFormat.Render(h.Engine.Present(new List<NamedThing> { new NamedThing { Name = "Sword", Count = 12 }, new NamedThing { Name = "Shield", Count = 30 } }), OutputFormat.Table);
            Assert.Contains("Sword", named);
            Assert.Contains("30", named);
            Assert.DoesNotContain("NamedThing@", named);
            // Identity objects stay references.
            var product = AgentFormat.Render(h.Engine.Present(new List<Product> { new Product { Sku = "S-1", Name = "Sword" } }), OutputFormat.Table);
            Assert.Contains("S-1", product);
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
            e.AddSource("some", () => { var l = new List<GuardHost?>(); for (int i = 0; i < 25; i++) l.Add(new GuardHost()); l.Add(null); return l; });
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
        [InlineData(true)]
        [InlineData(false)]
        public void MutationLimitHintSuggestsBatchesAndRaisesOnlyAdjustableBudgets(bool adjustable)
        {
            var e = Engine(new GuardHost());
            e.Defaults.BudgetsAdjustable = adjustable;
            var d = Mut(e, "many | set Free.x = 1").Diagnostics[0];
            Assert.Equal("mutation-limit", d.Code);
            // 50 items, ceiling 30: counting stops past the ceiling.
            Assert.Equal("more than 30 objects matched 'set' (the limit is 20); nothing was changed", d.Message);
            int batch = adjustable ? 30 : 20;
            Assert.Contains($"first {batch} | set", d.Hint);
            Assert.Contains($"skip {batch} | first {batch} | set", d.Hint);
            Assert.Equal(adjustable, d.Hint!.StartsWith("pass budgets.maxMutationTargets = 30 (the host ceiling) and split"));
            if (!adjustable) Assert.DoesNotContain("budgets", d.Hint);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MutationLimitCountsTheMatchesAndNamesTheHostLimitArgument(bool adjustable)
        {
            var e = Engine(new GuardHost());
            e.Defaults.BudgetsAdjustable = adjustable;
            e.Mutations.LimitArgument = "max_targets";
            var d = Mut(e, "some | set Free.x = 1").Diagnostics[0];
            Assert.Equal("mutation-limit", d.Code);
            Assert.Equal("25 objects matched 'set', more than the limit of 20; nothing was changed", d.Message);
            Assert.Equal("pass max_targets = 25 (host ceiling 30) to change all 25 in one all-or-nothing call (a dry run previews it); "
                + "or narrow the query (where / ref(...))", d.Hint);
            // The raise works whatever BudgetsAdjustable says: it only shapes hints.
            Assert.True(Mut(e, "some | set Free.x = 1", new Budgets { MaxMutationTargets = 25 }).Ok);
        }

        [Fact]
        public void BudgetErrorWhileCountingIsReportedAndCeilingAtLimitAdvisesBatches()
        {
            var e = Engine(new GuardHost());
            var r = Mut(e, "many | set Free.x = 1", new Budgets { MaxVisited = 25 });
            Assert.Equal("budget-exceeded", r.Diagnostics[0].Code);
            e.Mutations.HardMaxTargets = 20; // ceiling == limit: no raise to advise
            e.Mutations.LimitArgument = "";  // empty counts as unset
            var d = Mut(e, "some | set Free.x = 1").Diagnostics[0];
            Assert.Equal("more than 20 objects matched 'set' (the limit is 20); nothing was changed", d.Message);
            Assert.StartsWith("narrow the query (where / ref(...)) or split the change into batches of 20", d.Hint);
        }

        [Fact]
        public void HostDefaultTargetLimitApplies()
        {
            var e = Engine(new GuardHost(), maxTargets: 25);
            Assert.True(Mut(e, "some | set Free.x = 1").Ok);
            var d = Mut(e, "many | set Free.x = 1").Diagnostics[0];
            Assert.Equal("more than 30 objects matched 'set' (the limit is 25); nothing was changed", d.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void VisitedBudgetHintMentionsBudgetsOnlyWhenAdjustable(bool adjustable)
        {
            var e = Engine(new GuardHost());
            e.Defaults.BudgetsAdjustable = adjustable;
            var r = e.Execute("many | count", new Budgets { MaxVisited = 3 });
            Assert.Equal("budget-exceeded", r.Diagnostics[0].Code);
            Assert.Equal(adjustable, r.Diagnostics[0].Hint!.Contains("budgets.maxVisited"));
        }

        [Fact]
        public void HostReplacesTheMutationLimitHint()
        {
            var e = Engine(new GuardHost());
            e.Mutations.LimitHint = (op, limit) => $"pass max_targets (now {limit}) to change more than {limit} objects per {op}";
            var d = Mut(e, "many | set Free.x = 1").Diagnostics[0];
            Assert.Equal("mutation-limit", d.Code);
            Assert.Equal("pass max_targets (now 20) to change more than 20 objects per set", d.Hint);
        }

        [Fact]
        public void BatchValidatorSeesEveryCallWithArgumentsAndAThrowRefuses()
        {
            var e = Engine(new GuardHost());
            IReadOnlyList<MutationRecord>? seen = null;
            e.Mutations.ValidateBatch = b => { seen = b; throw new InvalidOperationException("nope"); };
            var r = e.Execute("h | set Free.x = $index + 5", null, new QueryOptions { AllowMutation = true, DryRun = true });
            Assert.Equal("invalid-value", r.Diagnostics[0].Code);
            Assert.Contains("nope", r.Diagnostics[0].Message);
            Assert.Single(seen!);
            Assert.Equal("Free.x", seen![0].Member);
            Assert.Equal(5f, seen[0].New);
            Assert.Empty(seen[0].Args);
        }

        [Theory]
        [InlineData(null, "look it up by name")]
        [InlineData("unity.find(\"Floor\")", "look it up with unity.find(\"Floor\")")]
        public void RefOfANameHintsAtFindingByName(string? find, string expected)
        {
            var e = Engine(new GuardHost());
            e.FindHint = find;
            var r = e.Execute("ref(\"Floor\")");
            var d = r.Diagnostics.First(x => x.Code == "stale-ref");
            Assert.Contains("not a name", d.Hint);
            Assert.Contains(expected, d.Hint);
        }

        [Fact]
        public void UnparenthesizedComputedSortKeyHintsAtParentheses()
        {
            var r = Engine(new GuardHost()).Execute("many | sort Free.x * 2 desc");
            Assert.Equal("syntax-error", r.Diagnostics[0].Code);
            Assert.Contains("sort (Mass * Speed) desc", r.Diagnostics[0].Hint);
        }

        [Fact]
        public void ListIndexOutOfRangeHintsAtAssigningTheWholeList()
        {
            var r = Mut(Engine(new GuardHost()), "h | set Kids[0] = null");
            Assert.Equal("invalid-plan", r.Diagnostics[0].Code);
            Assert.Contains("index 0 out of range (count 0)", r.Diagnostics[0].Message);
            Assert.Contains("set Kids = append(Kids, value)", r.Diagnostics[0].Hint);
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

    // Live-battery findings (Unreal / Unity / Godot, 0.2.0).
    public class BatteryFindingTests
    {
        public sealed class Light { public string Label { get; set; } = ""; public float Intensity { get; set; } public string Text { get; set; } = ""; public Light? Parent { get; set; } }

        public sealed class Body
        {
            private float _mass = 1;
            public string Name { get; set; } = "";
            /// <summary>Clamps like Unity's Rigidbody.mass.</summary>
            public float Mass { get => _mass; set => _mass = value < 1e-7f ? 1e-7f : value; }
            public string Tag { get; set; } = "Player";
        }

        private static RqlEngine Engine(out List<Light> lights, out List<Body> bodies)
        {
            var ls = lights = new List<Light> { new Light { Label = "Lamp", Intensity = 2 }, new Light { Label = "Sun", Intensity = 10 } };
            ls[0].Parent = ls[1];
            ls[1].Parent = ls[0];
            var bs = bodies = new List<Body> { new Body { Name = "Crate" } };
            var e = new RqlEngine().AddSource("lights", () => ls).AddSource("bodies", () => bs);
            e.Mutations.Enabled = true;
            return e;
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        [Theory]
        [InlineData("Lable", new[] { "Label", "Labels" }, "Label")]          // adjacent transposition costs 1
        [InlineData("Intensty", new[] { "Intensity", "Label" }, "Intensity")]
        [InlineData("LabelText", new[] { "Label" }, "Label")]                // contains a candidate at least half its length
        public void SuggestionsFindTranspositions(string name, string[] candidates, string first)
        {
            var close = Suggest.Closest(name, candidates, 3);
            Assert.Equal(first, close.First());
        }

        [Fact]
        public void SuggestionsSkipFarCandidates()
        {
            Assert.DoesNotContain("Type", Suggest.Closest("NoSuchType", new[] { "Type", "Transform" }, 3));
            Assert.Equal(1, Suggest.Distance("lable", "label"));
            Assert.Equal(3, Suggest.Distance("ca", "abc")); // optimal string alignment (unrestricted Damerau would say 2)
        }

        [Fact]
        public void UnknownMemberHintSuggestsTheTransposedName()
        {
            var e = Engine(out _, out _);
            var r = e.Execute("lights | where Lable == \"Lamp\"");
            var d = r.Diagnostics.Single(x => x.Code == "unknown-member");
            Assert.Equal("did you mean Label?", d.Hint);
        }

        [Fact]
        public void MemberListingsAreCapped()
        {
            var names = Enumerable.Range(0, 40).Select(i => "Member" + i).ToList();
            var hint = Suggest.MemberList(names);
            Assert.Equal(Suggest.MaxListed, hint.Substring("members: ".Length).Split(' ').TakeWhile(w => w != "…").Count());
            Assert.EndsWith("… (28 more)", hint);
        }

        [Fact]
        public void SortAfterSelectByDottedColumnHasNoWarning()
        {
            var e = Engine(out _, out _);
            var r = e.Execute("lights | select Parent.Label Label | sort Parent.Label desc");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Empty(r.Diagnostics);
            Assert.Equal("Lamp", r.Rows![0][1]!.GetValue<string>());
        }

        [Fact]
        public void MathFunctions()
        {
            var e = Engine(out _, out _);
            var r = e.Execute("lights | select d=(sqrt(Intensity)) p=(pow(Intensity, 2)) m=(max(Intensity, 5)) n=(min(Intensity, 5, 3))");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("[[1.4142135623730951,4,5,2],[3.1622776601683795,100,10,3]]", new JsonArray(r.Rows!.Select(row => (JsonNode)new JsonArray(row.Select(c => c?.DeepClone()).ToArray())).ToArray()).ToJsonString());
        }

        [Fact]
        public void ReceiptReportsTheValueReadBack()
        {
            var e = Engine(out _, out var bodies);
            var r = e.Execute("bodies | set Mass = -1", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(1e-7f, bodies[0].Mass);
            Assert.Equal(1e-7, r.Rows![0][3]!.GetValue<double>(), 12);
            var dry = e.Execute("bodies | set Mass = -5", options: new QueryOptions { AllowMutation = true, DryRun = true });
            Assert.Equal("-5", dry.Rows![0][3]!.ToJsonString()); // dry run: the planned value
        }

        [Fact]
        public void MutationRequestNeedsSetOrCall()
        {
            var e = Engine(out _, out _);
            var r = e.Execute("bodies | select Name", options: Mutate);
            Assert.False(r.Ok);
            Assert.Equal("invalid-plan", r.Diagnostics[0].Code);
        }

        [Fact]
        public void ThrowingExtensionFunctionIsFunctionThrew()
        {
            var e = Engine(out _, out _);
            e.AddFunction("app.find", args => throw new System.ArgumentException("no such object"));
            var r = e.Execute("lights | select f=(app.find(Label))");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("function-threw", r.Rows![0][0]!["$error"]!.GetValue<string>());
            Assert.Single(r.Diagnostics, d => d.Code == "function-threw" && d.Message.Contains("no such object"));
            var source = e.Execute("app.find(\"x\") | count");
            Assert.False(source.Ok);
            Assert.Equal("function-threw", source.Diagnostics.Single().Code);
        }

        [Fact]
        public void TableShowsDisplayNumbersAndRawUnicode()
        {
            var e = Engine(out var lights, out _);
            lights[0].Text = "Привет \"мир\" 🎮\nline2";
            var r = e.Execute("lights | where Label == \"Lamp\" | select x=(0.1 + 0.2) Text");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("1 result\nx|Text\n0.3|\"Привет \\\"мир\\\" 🎮\\nline2\"", AgentFormat.Render(r, OutputFormat.Table));
            Assert.Contains("0.30000000000000004", r.ToJson(includeStats: false)); // the JSON form stays exact
        }

        public sealed class Door
        {
            public string Name { get; set; } = "";
            public bool Locked { get; set; }
            public int Opened { get; private set; }
            [RqlCallable] public void Open(int force) => Opened += force;
            /// <summary>Companion validator of Open (spec §8): runs for every target before the first call.</summary>
            public string? ValidateOpen(int force) => Locked ? $"{Name} is locked" : force > 10 ? "too much force" : null;
        }

        [Fact]
        public void CallValidatorsMakeCallsAllOrNothing()
        {
            var doors = new List<Door> { new Door { Name = "front" }, new Door { Name = "back", Locked = true } };
            var e = new RqlEngine().AddSource("doors", () => doors);
            e.Mutations.Enabled = true;
            var refused = e.Execute("doors | call Open(1)", options: Mutate);
            Assert.False(refused.Ok);
            Assert.Equal("invalid-value", refused.Diagnostics[0].Code);
            Assert.Contains("back is locked", refused.Diagnostics[0].Message);
            Assert.All(doors, d => Assert.Equal(0, d.Opened));   // nothing was called, not even the first door
            var dry = e.Execute("doors | where Name == \"front\" | call Open(20)", options: new QueryOptions { AllowMutation = true, DryRun = true });
            Assert.Equal("invalid-value", dry.Diagnostics[0].Code);           // a dry run reports the refusal
            Assert.True(e.Execute("doors | where Name == \"front\" | call Open(2)", options: Mutate).Ok);
            Assert.Equal(2, doors[0].Opened);
        }

        [Fact]
        public void AggregatesReportErrorValuesTheyIgnore()
        {
            var e = Engine(out _, out _);
            var r = e.Execute("lights | sum (Intensity / 0)");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Contains(r.Diagnostics, d => d.Code == "type-mismatch" && d.Message.Contains("ignored 2"));
        }

        [Fact]
        public void DottedColumnOnEmptyStreamIsNotAnUnknownMember()
        {
            var e = Engine(out _, out _);
            e.AddSource("nothing", () => new List<Body>());
            var r = e.Execute("nothing | select Owner.Label | sort Owner.Label");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.DoesNotContain(r.Diagnostics, d => d.Code == "unknown-member");
        }

        [Fact]
        public void InvalidLiteralRegexFailsBeforeRunning()
        {
            var e = Engine(out _, out _);
            var r = e.Execute("lights | where Label matches \"([\" | count");
            Assert.False(r.Ok);
            Assert.Equal("invalid-regex", r.Diagnostics[0].Code);
            Assert.Contains("([", r.Diagnostics[0].Message);
        }
    }

    /// <summary>Wave-3: enum name resolution (spec §8) — qualified names, entry prefixes, ambiguity, host aliases.</summary>
    public class EnumNameResolutionTests
    {
        public enum EAlign { HAlign_Fill, HAlign_Left, VAlign_Fill, VAlign_Top, Center }
        [System.Flags] public enum Layers { None = 0, Layer_Ground = 1, Layer_Water = 2 }
        public enum Small : byte { X = 0, Y = 44 }
        public enum Huge : ulong { Zero = 0, Max = ulong.MaxValue }

        public sealed class Sized
        {
            public Small S { get; set; }
            public Huge H { get; set; }
        }

        [Fact]
        public void IntegerTextRespectsTheUnderlyingType()
        {
            var z = new Sized();
            var e = new RqlEngine();
            e.AddSource("z", () => z);
            e.Mutations.Enabled = true;
            var o = new QueryOptions { AllowMutation = true };
            Assert.Equal("coercion-failed", e.Execute("z | set S = \"300\"", null, o).Diagnostics[0].Code); // not 300 mod 256 = Y
            Assert.True(e.Execute("z | set H = \"18446744073709551615\"", null, o).Ok);
            Assert.Equal(Huge.Max, z.H);
            Assert.Equal(0, e.Execute("z | where S == \"256\" | count").Value!.GetValue<long>());
        }

        public sealed class Widget
        {
            public EAlign Align { get; set; } = EAlign.HAlign_Left;
            public Layers Mask { get; set; }
            public Dictionary<EAlign, int> Weights { get; set; } = new Dictionary<EAlign, int> { [EAlign.VAlign_Top] = 1 };
        }

        private static (RqlEngine, Widget) Engine()
        {
            var w = new Widget();
            var e = new RqlEngine();
            e.AddSource("w", () => w);
            e.Mutations.Enabled = true;
            return (e, w);
        }

        private static QueryResult Set(RqlEngine e, string assign) =>
            e.Execute("w | set " + assign, null, new QueryOptions { AllowMutation = true });

        [Theory]
        [InlineData("\"VAlign_Top\"", EAlign.VAlign_Top)]
        [InlineData("\"valign_top\"", EAlign.VAlign_Top)]
        [InlineData("\"EAlign::VAlign_Fill\"", EAlign.VAlign_Fill)]
        [InlineData("\"EWhatever::Left\"", EAlign.HAlign_Left)]
        [InlineData("\"EAlign.center\"", EAlign.Center)]
        [InlineData("\"Top\"", EAlign.VAlign_Top)]
        [InlineData("\" top \"", EAlign.VAlign_Top)]
        [InlineData("\"3\"", EAlign.VAlign_Top)]
        public void LenientNamesResolve(string literal, EAlign expected)
        {
            var (e, w) = Engine();
            var r = Set(e, "Align = " + literal);
            Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
            Assert.Equal(expected, w.Align);
        }

        [Fact]
        public void AmbiguousNameIsRefusedWithTheCandidates()
        {
            var (e, w) = Engine();
            var r = Set(e, "Align = \"fill\"");
            Assert.Equal("coercion-failed", r.Diagnostics[0].Code);
            Assert.Contains("\"fill\" is ambiguous for EAlign: HAlign_Fill, VAlign_Fill", r.Diagnostics[0].Message);
            Assert.Equal(EAlign.HAlign_Left, w.Align);
            Assert.Equal("coercion-failed", Set(e, "Align = \"Bottom\"").Diagnostics[0].Code);
        }

        [Fact]
        public void HostAliasesResolveLikeEntryNames()
        {
            var (e, w) = Engine();
            e.EnumAliases = t => t == typeof(EAlign) ? new Dictionary<string, string> { ["Fill"] = "HAlign_Fill", ["Middle"] = "Center" } : null;
            Assert.True(Set(e, "Align = \"Fill\"").Ok); // the alias decides at step 1, before the ambiguous prefix step
            Assert.Equal(EAlign.HAlign_Fill, w.Align);
            Assert.True(Set(e, "Align = \"middle\"").Ok);
            Assert.Equal(EAlign.Center, w.Align);
            var rows = e.Execute("w | where Align == \"MIDDLE\" | count");
            Assert.Equal(1, rows.Value!.GetValue<long>());
            Assert.Equal("Center", e.Execute("w | select Align").Rows![0][0]!.GetValue<string>()); // encoding keeps the entry name
        }

        [Fact]
        public void ComparisonsAndMapKeysUseTheSameResolution()
        {
            var (e, w) = Engine();
            Assert.Equal(1, e.Execute("w | where Align == \"EAlign::Left\" | count").Value!.GetValue<long>());
            Assert.Equal(0, e.Execute("w | where Align == \"Fill\" | count").Value!.GetValue<long>()); // ambiguous: not equal
            Assert.Equal(1, e.Execute("w | select v = Weights[\"Top\"]").Rows![0][0]!.GetValue<long>());
        }

        [Fact]
        public void FlagsListsResolveEachName()
        {
            var (e, w) = Engine();
            Assert.True(Set(e, "Mask = \"Ground, water\"").Ok);
            Assert.Equal(Layers.Layer_Ground | Layers.Layer_Water, w.Mask);
        }
    }

    /// <summary>Wave-3b findings: host-neutral hints, empty mutations through typos, conditionals.</summary>
    public class Wave3bTests
    {
        public sealed class Door
        {
            public string Name { get; set; } = "";
            public int Opened { get; private set; }
            [RqlCallable] public void Open(int force) => Opened += force;
        }

        public sealed class Editor
        {
            [RqlCallable] public string CreateThing(string name) => name;
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        private static RqlEngine Engine(List<Door> doors)
        {
            var e = new RqlEngine().AddSource("doors", () => doors).AddSource("editor", () => new Editor());
            e.Mutations.Enabled = true;
            return e;
        }

        [Theory]
        [InlineData("[0, 1, 2] | select $")]
        [InlineData("range(0, 3)")]
        public void UnknownSourceExampleNamesARegisteredSource(string q)
        {
            var r = Engine(new List<Door>()).Execute(q);
            var d = r.Diagnostics[0];
            Assert.Equal("unknown-source", d.Code);
            Assert.Contains("(e.g. doors)", d.Message);
            Assert.DoesNotContain("world", d.Message + d.Hint);
            Assert.Contains("sources: doors editor", d.Hint);
        }

        [Fact]
        public void RowColumnHintNamesNoInventedSource()
        {
            var r = Engine(new List<Door> { new Door() }).Execute("doors | select Name | where Opend > 0");
            var d = r.Diagnostics.First(x => x.Code == "unknown-member");
            Assert.DoesNotContain("world", d.Hint);
        }

        [Theory]
        [InlineData(null, "list the types with the schema tool")]
        [InlineData("rql-schema", "list the types with rql-schema")]
        public void NoMatchHintNamesTheHostSchemaTool(string? schema, string expected)
        {
            var e = Engine(new List<Door> { new Door() });
            e.SchemaHint = schema;
            var d = e.Execute("doors | where $ is Window").Diagnostics.First(x => x.Code == "no-match");
            Assert.Contains(expected, d.Hint);
            Assert.DoesNotContain("rql_schema", d.Hint);
        }

        [Fact]
        public void MutationEmptiedByATypoFails()
        {
            var doors = new List<Door> { new Door { Name = "X" } };
            var r = Engine(doors).Execute("doors | where Name == \"X\" | get Nmae | set Name = \"Y\"", options: Mutate);
            Assert.False(r.Ok);
            var d = r.Diagnostics[0];
            Assert.Equal("unknown-member", d.Code);
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.Contains("nothing was changed", d.Message);
            Assert.Contains("Name", d.Hint);
            Assert.Equal("X", doors[0].Name);
        }

        [Fact]
        public void HostKnownMemberKeepsAnEmptyMutationOk()
        {
            var doors = new List<Door> { new Door { Name = "X" } };
            var e = Engine(doors);
            e.IsKnownMember = n => n == "intensity";
            var r = e.Execute("doors | where intensity > 1 | set Name = \"Y\"", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(0, r.Total);
            Assert.Contains(r.Diagnostics, x => x.Code == "unknown-member" && x.Severity == DiagnosticSeverity.Warning);
            // The same typo without the hook is an error.
            e.IsKnownMember = null;
            Assert.False(e.Execute("doors | where intensity > 1 | set Name = \"Y\"", options: Mutate).Ok);
        }

        [Fact]
        public void UnknownCallableHintNamesASourceThatHasIt()
        {
            var r = Engine(new List<Door> { new Door() }).Execute("doors | call CreateThing(\"a\")", options: Mutate);
            var d = r.Diagnostics[0];
            Assert.Equal("unknown-function", d.Code);
            Assert.Contains("callable: Open(", d.Hint);
            Assert.Contains("the source 'editor' has CreateThing: editor | call CreateThing(", d.Hint);
        }

        [Fact]
        public void ConditionalIsLazyAndRightAssociative()
        {
            var e = Engine(new List<Door> { new Door { Name = "a" }, new Door { Name = "b" } });
            var r = e.Execute("doors | select v = (Name == \"a\" ? 1 : Name == \"b\" ? 2 : 3), w = if(true, Name, ref(\"Door#99\"))");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(1, r.Rows![0][0]!.GetValue<long>());
            Assert.Equal(2, r.Rows![1][0]!.GetValue<long>());
            Assert.Equal("b", r.Rows![1][1]!.GetValue<string>());
        }

        public sealed class Light { public string Name { get; set; } = ""; public float intensity { get; set; } }

        [Fact]
        public void EmptyMutationEdgesStayOk()
        {
            var doors = new List<Door> { new Door { Name = "X" } };
            var e = Engine(doors);
            // A throwing hook counts as "not known".
            e.IsKnownMember = _ => throw new InvalidOperationException("boom");
            Assert.False(e.Execute("doors | where intensity > 1 | set Name = \"Y\"", options: Mutate).Ok);
            // The hook also silences the static "not evaluated" warning.
            e.IsKnownMember = n => n == "Shine";
            var q = e.Execute("doors | where false and Shine > 1");
            Assert.DoesNotContain(q.Diagnostics, d => d.Code == "unknown-member");
            // has() is a guard, not a typo.
            Assert.True(e.Execute("doors | where has(glow) | set Name = \"Y\"", options: Mutate).Ok);
            // A name another read found is no typo, even where it was never evaluated.
            var things = new List<object> { new Light { Name = "l", intensity = 2 } };
            var e2 = new RqlEngine().AddSource("things", () => things);
            e2.Mutations.Enabled = true;
            var r = e2.Execute("things | where intensity > 100 | set intensity = intensity + 1", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.DoesNotContain(r.Diagnostics, d => d.Code == "unknown-member");
        }

        [Fact]
        public void UnknownCallableHintListsSeveralSources()
        {
            var e = Engine(new List<Door> { new Door() }).AddSource("editor2", () => new Editor());
            var d = e.Execute("doors | call CreateThing(\"a\")", options: Mutate).Diagnostics[0];
            Assert.Contains("sources with CreateThing: editor, editor2 (e.g. editor | call CreateThing(…))", d.Hint);
        }

        [Fact]
        public void IncompleteConditionalHintsAtTheForm()
        {
            var d = Engine(new List<Door>()).Execute("doors | select v = (Name ? 1)").Diagnostics[0];
            Assert.Equal("syntax-error", d.Code);
            Assert.Contains("cond ? then : else", d.Hint);
        }
    }
}
