using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>Wave-4 findings: conditions, float widening, known-member cache, target-limit range, host names in hints.</summary>
    public class Wave4Tests
    {
        public class Item
        {
            public string Name { get; set; } = "";
            public float Weight { get; set; }
            public Item? Other { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
        }

        public sealed class GpuParticles3D : Item { public int Amount { get; set; } }

        public sealed class OmniLight3D : Item { public float Energy { get; set; } }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        private sealed class ItemAdapter : RqlTypeAdapter<Item> { }

        private static RqlEngine Engine(IEnumerable<object> items)
        {
            var list = items.ToList();
            var e = new RqlEngine().AddSource("items", () => list);
            e.Mutations.Enabled = true;
            return e;
        }

        // ------------------------------------------------------------ #10 conditions

        [Theory]
        [InlineData("items | where Other | count", "'Other' is a Item", "Other != null")]
        [InlineData("items | where Name | count", "the string", "Name != \"\"")]
        [InlineData("items | where Weight | count", "the number 4.2", "Weight != 0")]
        [InlineData("items | where Tags | count", "a list", "len(Tags) > 0")]
        [InlineData("items | select c = (Other ? 1 : 0)", "'Other'", "Other != null")]
        [InlineData("items | where Weight > 1 and Other | count", "'Other'", "Other != null, or has(Other)")]
        public void NonBooleanConditionFailsWithAHint(string q, string message, string hint)
        {
            var other = new Item { Name = "o" };
            var r = Engine(new object[] { new Item { Name = "a", Weight = 4.2f, Other = other, Tags = { "t" } } }).Execute(q);
            Assert.False(r.Ok);
            var d = r.Diagnostics[0];
            Assert.Equal("type-mismatch", d.Code);
            Assert.Contains(message, d.Message);
            Assert.StartsWith("a condition must be a boolean, e.g. ", d.Hint);
            Assert.Contains(hint, d.Hint);
        }

        [Fact]
        public void NullConditionIsFalse()
        {
            var r = Engine(new object[] { new Item(), new Item() }).Execute("items | where Other | count");
            Assert.True(r.Ok);
            Assert.Equal(0, r.Value!.GetValue<long>());
        }

        // ------------------------------------------------------------ #11 float widening

        [Theory]
        [InlineData("items | select w = (Weight * 1)", 4.2)]
        [InlineData("items | select w = (Weight + 0.1)", 4.3)]
        public void FloatArithmeticUsesTheShortestDecimal(string q, double expected)
        {
            var r = Engine(new object[] { new Item { Weight = 4.2f } }).Execute(q);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(expected, r.Rows![0][0]!.GetValue<double>());
        }

        [Fact]
        public void FloatAggregatesUseTheShortestDecimal()
        {
            var e = Engine(new object[] { new Item { Weight = 1f }, new Item { Weight = 4.2f } });
            Assert.Equal(2.6, e.Execute("items | avg Weight").Value!.GetValue<double>());
            Assert.Equal(5.2, e.Execute("items | sum Weight").Value!.GetValue<double>());
            Assert.Equal(2, e.Execute("items | where Weight == 1 or Weight == 4.2 | count").Value!.GetValue<long>());
            Assert.Equal("w4.2", e.Execute("items | where Weight > 2 | select t = (\"w\" + Weight)").Rows![0][0]!.GetValue<string>());
        }

        [Theory]
        [InlineData(4.2f, 4.2)]
        [InlineData(0.1f, 0.1)]
        [InlineData(1.5707964f, 1.5707964)]
        [InlineData(1e-30f, 1e-30)]
        [InlineData(123456.79f, 123456.79)]
        [InlineData(-2.5f, -2.5)]
        [InlineData(16777216f, 16777216)]
        [InlineData(123456789f, 123456790)]
        public void WidenRoundTrips(float f, double expected)
        {
            var d = V.Widen(f);
            Assert.Equal(expected, d);
            Assert.Equal(f, (float)d);
        }

        // ------------------------------------------------------------ #6 known members

        [Fact]
        public void KnownMembersAreCachedWhileASourceIsNull()
        {
            int reads = 0;
            var items = new List<object> { new Item { Name = "a" } };
            var e = new RqlEngine().AddSource("items", () => items).AddSource("big", () => { reads++; return new OmniLight3D(); }).AddSource("selection", () => null);
            e.Mutations.Enabled = true;
            for (int i = 0; i < 5; i++)
                Assert.False(e.Execute("items | where nmae == \"x\" | set Name = \"y\"", options: Mutate).Ok);
            // Known names never evaluate a source the query does not name (spec §8): a source getter may walk a whole scene.
            Assert.Equal(0, reads);
            // A type a source produced once stays known after the source turns null.
            object? selected = new GpuParticles3D();
            var e2 = new RqlEngine().AddSource("items", () => items).AddSource("selection", () => selected);
            e2.Mutations.Enabled = true;
            Assert.True(e2.Execute("selection | count").Ok);
            selected = null;
            var r = e2.Execute("items | where Amount > 1 | set Name = \"y\"", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
        }

        // ------------------------------------------------------------ #7 target limit range

        [Theory]
        [InlineData(0)]
        [InlineData(1001)]
        [InlineData(-5)]
        public void OutOfRangeTargetLimitIsRefused(int n)
        {
            var items = new List<object> { new Item { Name = "a" } };
            var e = Engine(items);
            e.Mutations.LimitArgument = "max_targets";
            var r = e.Execute("items | set Name = \"b\"", new Budgets { MaxMutationTargets = n }, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("invalid-value", r.Diagnostics[0].Code);
            Assert.Contains($"max_targets must be from 1 to 1000 (the host ceiling), got {n}", r.Diagnostics[0].Message);
            Assert.Equal("a", ((Item)items[0]).Name);
        }

        // ------------------------------------------------------------ #8 mutate hint

        [Theory]
        [InlineData(null, "the mutation tool")]
        [InlineData("rql-mutate", "rql-mutate")]
        public void PermissionDeniedNamesTheHostMutationTool(string? hint, string expected)
        {
            var e = Engine(new object[] { new Item() });
            e.MutateHint = hint;
            var d = e.Execute("items | set Name = \"x\"").Diagnostics[0];
            Assert.Equal("permission-denied", d.Code);
            Assert.Contains("run it with " + expected, d.Message);
            Assert.DoesNotContain("allowMutation", d.Message + d.Hint);
        }

        // ------------------------------------------------------------ #15 display names

        [Fact]
        public void HostDisplayNamesShowEverywhereAndBothNamesMatch()
        {
            var p = new GpuParticles3D { Name = "fx" };
            var e = Engine(new object[] { p, new Item { Name = "plain" } });
            e.TypeDisplayName = t => t == typeof(GpuParticles3D) ? "GPUParticles3D" : null;
            var r = e.Execute("items | select $type $ref");
            Assert.Equal("GPUParticles3D", r.Rows![0][0]!.GetValue<string>());
            Assert.StartsWith("GPUParticles3D@", r.Rows![0][1]!.GetValue<string>());
            Assert.Equal("Item", r.Rows![1][0]!.GetValue<string>());
            foreach (var name in new[] { "GPUParticles3D", "GpuParticles3D", "gpuparticles3d" })
                Assert.Equal(1, e.Execute($"items | where $ is {name} | count").Value!.GetValue<long>());
            Assert.Equal(1, e.Execute("items | where $type == \"GPUParticles3D\" | count").Value!.GetValue<long>());
            var handle = r.Rows![0][1]!.GetValue<string>();
            Assert.Equal("fx", e.Execute($"ref(\"{handle}\") | select Name").Rows![0][0]!.GetValue<string>());
            Assert.Equal("fx", e.Execute($"ref(\"{handle.Replace("GPUParticles3D", "GpuParticles3D")}\") | select Name").Rows![0][0]!.GetValue<string>());
            var schema = e.Execute("items | describe");
            Assert.Contains("GPUParticles3D", schema.ToJson(includeStats: false));
            var miss = e.Execute("items | where $ is GPUParticles3D | where Amout > 1").Diagnostics.First(d => d.Code == "unknown-member");
            Assert.Contains("GPUParticles3D", miss.Message);
        }

        // ------------------------------------------------------------ #16 mixed streams

        [Fact]
        public void SetOnAMixedStreamNamesTheTypesThatHaveTheMember()
        {
            var e = Engine(new object[] { new Item { Name = "a" }, new OmniLight3D { Name = "l" } });
            var d = e.Execute("items | set Energy = 2", options: Mutate).Diagnostics[0];
            Assert.Equal("unknown-member", d.Code);
            Assert.Contains("1 of 2 targets", d.Message);
            Assert.Contains("only OmniLight3D has 'Energy'", d.Hint);
            Assert.Contains("where $ is OmniLight3D", d.Hint);
        }

        [Fact]
        public void NestedSetPathDoesNotSuggestFilteringTheStream()
        {
            var e = new RqlEngine().AddSource("items", () => new List<Item> { new Item { Name = "a", Other = new Item() } })
                .AddSource("lights", () => new List<OmniLight3D>(), null, typeof(List<OmniLight3D>));
            e.Mutations.Enabled = true;
            var d = e.Execute("items | set Other.Energy = 2", options: Mutate).Diagnostics[0];
            Assert.Equal("unknown-member", d.Code);
            Assert.DoesNotContain("where $ is", d.Hint ?? "");
            var top = e.Execute("items | set Energy = 2", options: Mutate).Diagnostics[0];
            Assert.Contains("'Energy' is a member of OmniLight3D", top.Hint);
        }

        [Fact]
        public void SelfContainingListsDoNotOverflow()
        {
            var loop = new List<object?> { 1 };
            loop.Add(loop);
            var loop2 = new List<object?> { 1 };
            loop2.Add(loop2);
            var e = new RqlEngine().AddSource("ls", () => new List<object> { loop, loop2 });
            Assert.True(e.Execute("ls | where $ == $ | count").Ok);
            var g = e.Execute("ls | group-by $ | count"); // (encoding a self-containing list is a separate, pre-existing limit)
            Assert.True(g.Ok, g.ToJson(includeStats: false));
        }

        [Fact]
        public void UnknownMemberListsMembersBySimilarity()
        {
            var e = Engine(new object[] { new Item { Name = "a" } });
            var d = e.Execute("items | where Wieght_kg > 1").Diagnostics.First(x => x.Code == "unknown-member");
            Assert.StartsWith("members: Weight", d.Hint);
        }

        [Fact]
        public void ReadOfAMemberOtherKnownTypesHaveSuggestsFiltering()
        {
            var items = new List<Item> { new Item { Name = "a" } };
            int reads = 0;
            var e = new RqlEngine().AddSource("items", () => items).AddSource("lights", () => { reads++; return new List<OmniLight3D>(); }, null, typeof(List<OmniLight3D>));
            var d = e.Execute("items | where Energy > 1").Diagnostics.First(x => x.Code == "unknown-member");
            Assert.Contains("'Energy' is a member of OmniLight3D", d.Hint);
            // The declared type is known without evaluating the source (spec §8).
            Assert.Equal(0, reads);
        }

        // ------------------------------------------------------------ #12 group items, #14 no-op receipts, adapter list

        [Fact]
        public void GroupRowsAggregateTheirItems()
        {
            var e = Engine(new object[] { new Item { Name = "a", Weight = 1f }, new Item { Name = "a", Weight = 4.2f }, new Item { Name = "b", Weight = 2f } });
            var r = e.Execute("items | group-by Name | select key, count, avg = items.avg(Weight)");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(2.6, r.Rows![0][2]!.GetValue<double>());
            var miss = e.Execute("items | group-by Name | select key, avg = Weight").Diagnostics.First(d => d.Code == "unknown-member");
            Assert.Contains("items.avg(Weight)", miss.Hint);
        }

        [Fact]
        public void NoOpAssignmentsAreCountedNotListed()
        {
            var items = new List<object> { new Item { Name = "a" }, new Item { Name = "b" } };
            var e = Engine(items);
            var r = e.Execute("items | set Name = \"a\"", options: Mutate);
            Assert.True(r.Ok);
            Assert.Single(r.Rows!);
            Assert.Equal(1, r.Unchanged);
            Assert.Contains("\"unchanged\":1", r.ToJson(includeStats: false));
            Assert.Contains("(1 unchanged assignment(s) not listed)", AgentFormat.Render(r, OutputFormat.Table));
        }

        public sealed class Stubborn
        {
            private string _tag = "Untagged";
            public string Name { get; set; } = "s";
            public string Tag { get => _tag; set { if (value == "Untagged" || value == "Hero") _tag = value; } }
        }

        [Fact]
        public void AWriteTheHostRefusedStaysInTheReceipt()
        {
            var s = new Stubborn();
            var e = new RqlEngine().AddSource("s", () => s);
            e.Mutations.Enabled = true;
            var r = e.Execute("s | set Tag = \"Typo\"", options: Mutate);
            Assert.Single(r.Rows!);
            Assert.Null(r.Unchanged);
            Assert.Equal("Untagged", r.Rows![0][3]!.GetValue<string>());
            Assert.Equal(1, e.Execute("s | set Tag = \"Untagged\"", options: Mutate).Unchanged);
            Assert.False(e.Execute("s | where itmes.count() > 0 | set Name = \"x\"", options: Mutate).Ok);
            Assert.False(e.Execute("s | where items.count() > 0 | set Name = \"x\"", options: Mutate).Ok); // items is a member name only on group rows
        }

        [Fact]
        public void EngineListsItsAdapters()
        {
            var e = new RqlEngine();
            Assert.Empty(e.Adapters);
            var a = new ItemAdapter();
            e.AddAdapter(a);
            Assert.Same(a, Assert.Single(e.Adapters));
        }

        // ------------------------------------------------------------ #13 / #17 parser hints

        [Fact]
        public void SecondQueryOnANewLineHintsOneQueryPerCall()
        {
            var ex = Assert.Throws<RqlException>(() => RqlParser.Parse("items | where Name == \"a\" | set Name = \"b\"\nitems | where Name == \"c\" | count"));
            Assert.Equal("syntax-error", ex.Code);
            Assert.Contains("one query per call: line 2", ex.Hint);
            // A query continued on the next line is not a second query.
            foreach (var q in new[]
                     {
                         "items\n| where Name == \"a\" foo", "items |\n  where x | y\nfoo", "items | where x\n  and y z",
                         "items | where x > 1\ndistinct | count", "items | select Name,\n  Tags | sort Name +", "items | where Name == \"a\" or\n  Name | first 2 )",
                     })
            {
                var other = Assert.Throws<RqlException>(() => RqlParser.Parse(q));
                Assert.DoesNotContain("one query per call", other.Hint ?? "");
            }
        }

        [Theory]
        [InlineData("items | where path contains \"addons\" == false", "not (path contains \"addons\")")]
        [InlineData("items | where a == b == c", "not (a == b)")]
        [InlineData("items | where Name starts-with \"a\" contains \"b\"", "comparisons do not chain")]
        public void ChainedComparisonsExplainTheFix(string q, string hint)
        {
            var ex = Assert.Throws<RqlException>(() => RqlParser.Parse(q));
            Assert.Equal("syntax-error", ex.Code);
            Assert.Contains("comparisons do not chain", ex.Message);
            Assert.Contains(hint, ex.Message + " " + ex.Hint);
        }
    }
}
