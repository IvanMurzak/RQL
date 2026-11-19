using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>Read stages after a call of read-only methods (spec §8 "Reading call results").</summary>
    public class CallReadTests
    {
        public sealed class Ground
        {
            public bool Inside { get; set; }
            public double Height { get; set; }
            public List<double> Location { get; set; } = new List<double>();
        }

        public sealed class Terrain
        {
            public string Name { get; set; } = "Island";
            public int Reads;
            public int Sculpts;

            /// <summary>A hill whose top is at (150, 100).</summary>
            [RqlCallable("The ground at a point.", ReadOnly = true)]
            public Ground Surface(List<double> point)
            {
                Reads++;
                double dx = point[0] - 150, dy = point[1] - 100;
                return new Ground { Inside = true, Height = 500 - (dx * dx + dy * dy) / 100, Location = new List<double> { point[0], point[1] } };
            }

            [RqlCallable("Raise the ground.")]
            public double Sculpt(double height) { Sculpts++; return height; }
        }

        private sealed class ProbeAdapter : RqlTypeAdapter<Terrain>
        {
            protected override void Members(MemberTable<Terrain> m) => m.Function<double, double>("Probe", (t, h) => h * 2, "h").ReadOnlyMethods("Probe");
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        private static (RqlEngine engine, Terrain terrain) Host()
        {
            var terrain = new Terrain();
            var e = new RqlEngine().AddSource("landscape", () => terrain).AddSource("terrains", () => new List<Terrain> { terrain });
            e.Mutations.Enabled = true;
            return (e, terrain);
        }

        private static string Rows(QueryResult r)
        {
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(ResultKind.Rows, r.Kind);
            return string.Join(", ", r.Columns!) + ": " + string.Join(" ", r.Rows!.Select(row => "[" + string.Join(",", row.Select(c => c?.ToJsonString() ?? "null")) + "]"));
        }

        [Fact]
        public void HighestPointOfAGridOfReadOnlyCalls()
        {
            var (e, terrain) = Host();
            var r = e.Execute("range(0, 961) | call landscape.Surface([($ % 31) * 10, floor($ / 31) * 10]) | sort result.Height desc | first 1 | select x = result.Location[0], y = result.Location[1], h = result.Height",
                new Budgets { MaxMutationTargets = 1000 }, Mutate);
            Assert.Equal("x, y, h: [150,100,500]", Rows(r));
            Assert.Equal(961, terrain.Reads);
            Assert.False(r.Receipt);
            Assert.Equal(1, r.Total);
        }

        [Fact]
        public void ReadStagesKeepTheCallColumnsUntilASelect()
        {
            var (e, _) = Host();
            var r = e.Execute("[[150, 100], [1000, 0], [160, 100]] | call landscape.Surface($) | where result.Height > 0 | skip 1", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "$ref", "method", "result" }, r.Columns);
            var row = Assert.Single(r.Rows!);
            Assert.Equal("Surface(List<double> point)", row[1]!.GetValue<string>());
            Assert.False(r.Receipt);
        }

        [Fact]
        public void AMutatingCallStaysTerminalAndNothingIsCalled()
        {
            var (e, terrain) = Host();
            foreach (var options in new[] { Mutate, new QueryOptions { AllowMutation = true, DryRun = true } })
            {
                var r = e.Execute("terrains | call Sculpt(5) | first 1", null, options);
                Assert.False(r.Ok);
                Assert.Equal("invalid-plan", r.Diagnostics[0].Code);
                Assert.Contains("stage 'call' is terminal and must be the last stage", r.Diagnostics[0].Message);
                Assert.Contains("Sculpt(double height)", r.Diagnostics[0].Hint);
            }
            Assert.Equal(0, terrain.Sculpts);
            // Alone it still runs and returns its receipt.
            var receipt = e.Execute("terrains | call Sculpt(5)", null, Mutate);
            Assert.True(receipt.Ok && receipt.Receipt);
            Assert.Equal(1, terrain.Sculpts);
        }

        [Fact]
        public void TheCallStillNeedsAMutationRequestAndOtherStagesStayRefused()
        {
            var (e, terrain) = Host();
            var read = e.Execute("range(0, 2) | call landscape.Surface([$, 0]) | first 1");
            Assert.Equal("permission-denied", read.Diagnostics[0].Code);
            var count = e.Execute("range(0, 2) | call landscape.Surface([$, 0]) | count", null, Mutate);
            Assert.Equal("invalid-plan", count.Diagnostics[0].Code);
            Assert.Equal(0, terrain.Reads);
        }

        [Fact]
        public void ABindingErrorInAReadStageFailsBeforeAnyCallRuns()
        {
            var (e, terrain) = Host();
            int commits = 0;
            e.Mutations.Committed = _ => commits++;
            var r = e.Execute("range(0, 3) | call landscape.Surface([$, 0]) | where nosuchfn(result)", null, Mutate);
            Assert.Equal("unknown-function", r.Diagnostics[0].Code);
            Assert.Equal(0, terrain.Reads);
            Assert.Equal(0, commits);
        }

        [Fact]
        public void APlanThatEndsWithTheCallKeepsTheTypoRuleForResultAndMethod()
        {
            var (e, terrain) = Host();
            var r = e.Execute("terrains | where result > 1 | call Sculpt(1)", null, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("unknown-member", r.Diagnostics[0].Code);
            Assert.Equal(0, terrain.Sculpts);
        }

        [Fact]
        public void HostBuiltPlansAreCheckedLikeDecodedOnes()
        {
            var (e, terrain) = Host();
            var parsed = RqlParser.Parse("terrains | call Sculpt(1)");
            var plan = new QueryPlan(parsed.Source, parsed.Stages.Append(new SimpleTerminalStage("count")).ToList(), parsed.Version);
            var r = e.Execute(plan, null, Mutate);
            Assert.Equal("invalid-plan", r.Diagnostics[0].Code);
            Assert.Equal(0, terrain.Sculpts);
        }

        [Fact]
        public void AdaptersMarkMethodsReadOnly()
        {
            var terrain = new Terrain();
            var e = new RqlEngine().AddSource("landscape", () => terrain)
                .AddAdapter(new ProbeAdapter());
            e.Mutations.Enabled = true;
            Assert.Equal("result: [6] [4]", Rows(e.Execute("[2, 3] | call landscape.Probe($) | sort result desc | select result", null, Mutate)));
        }
    }
}
