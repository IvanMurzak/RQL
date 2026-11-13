using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>Several statements in one request, separated by ';' (spec §14).</summary>
    public class StatementTests
    {
        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        public sealed class Node
        {
            public string Name { get; set; } = "";
            public double Value { get; set; }
        }

        private static (RqlEngine engine, List<Node> nodes) Engine()
        {
            var nodes = new List<Node> { new Node { Name = "a", Value = 1 }, new Node { Name = "b", Value = 2 } };
            var e = new RqlEngine().AddSource("nodes", () => nodes);
            e.Mutations.Enabled = true;
            return (e, nodes);
        }

        private static string[] Split(string text) => RqlParser.SplitStatements(text).Select(s => s.Text).ToArray();

        [Fact]
        public void SplitsAtTopLevelSemicolonsOnly()
        {
            Assert.Equal(new[] { "nodes | count", "nodes | select Name" }, Split("nodes | count; nodes | select Name"));
            Assert.Equal(new[] { "nodes | where Name == \"a;b\"", "x" }, Split("nodes | where Name == \"a;b\";x"));
            Assert.Equal(new[] { "nodes | where Name == 'a;b'" }, Split("nodes | where Name == 'a;b'"));
            Assert.Equal(new[] { "nodes | where Name == \"a\\\";b\"" }, Split("nodes | where Name == \"a\\\";b\""));
            Assert.Equal(new[] { "nodes | select `a;b`" }, Split("nodes | select `a;b`"));
            Assert.Equal(new[] { "nodes | where (a; b)" }, Split("nodes | where (a; b)"));
            Assert.Equal(new[] { "nodes | set V = [1; 2]", "nodes | count" }, Split("nodes | set V = [1; 2]; nodes | count"));
            Assert.Equal(new[] { "nodes | where Name == \"a;b" }, Split("nodes | where Name == \"a;b"));   // unterminated: one statement
            Assert.Equal(new[] { "a", "b" }, Split(" a ;; \n b ; "));
            Assert.Equal(new[] { "nodes | count" }, Split("nodes | count;"));
        }

        [Fact]
        public void TextWithoutSeparatorIsOneStatementSpanningEverything()
        {
            var s = Assert.Single(RqlParser.SplitStatements("  nodes | count \n"));
            Assert.Equal(0, s.Span.Start);
            Assert.Equal(17, s.Span.End);
            Assert.Equal("", Assert.Single(RqlParser.SplitStatements("")).Text);
            Assert.Equal("", Assert.Single(RqlParser.SplitStatements(" ; ;")).Text);
        }

        [Fact]
        public void OneStatementKeepsTheSingleResult()
        {
            var (e, _) = Engine();
            var plain = e.Execute("nodes | select Name");
            var trailing = e.Execute("nodes | select Name;");
            Assert.Null(trailing.Statements);
            Assert.Equal(plain.ToJson(includeStats: false), trailing.ToJson(includeStats: false));
            // Errors keep their positions in the request text.
            Assert.Equal(e.Execute("nodes | where").ToJson(includeStats: false), e.Execute("nodes | where;").ToJson(includeStats: false));
            Assert.Equal("syntax-error", Assert.Single(e.Execute(";").Diagnostics).Code);
            // A leading separator does not shift them.
            var lead = e.Execute("; nodes | wher x");
            Assert.Equal(10, Assert.Single(lead.Diagnostics).At!.Value.Start);
            Assert.Equal(e.Execute("  nodes | wher x").ToJson(includeStats: false), lead.ToJson(includeStats: false));
            // A ';' inside the statement stays.
            Assert.Equal("a;b", e.Execute(";; nodes | where Name == \"a\" | select n = (Name + \";b\");").Rows![0][0]!.GetValue<string>());
        }

        [Fact]
        public void ReadsRunEveryStatementAndRenderOneBlockEach()
        {
            var (e, _) = Engine();
            var r = e.Execute("nodes | count; nodes | where Name == \"b\" | select Name Value");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(2, r.Statements!.Count);
            Assert.Null(r.Rows);
            Assert.Equal(
                "[1/2] nodes | count\n2\n\n[2/2] nodes | where Name == \"b\" | select Name Value\n1 result\nName|Value\nb|2",
                AgentFormat.Render(r, OutputFormat.Table));
        }

        [Fact]
        public void AFailingReadDoesNotStopTheOthers()
        {
            var (e, _) = Engine();
            var r = e.Execute("nodes | select Nme; nodes | where | count; nodes | count");
            Assert.False(r.Ok);
            Assert.Equal(3, r.Statements!.Count);
            Assert.All(r.Statements, s => Assert.NotNull(s.Result));
            Assert.Equal("syntax-error", r.Statements[1].Result!.Diagnostics[0].Code);
            Assert.True(r.Statements[2].Result!.Ok);
            var text = AgentFormat.Render(r, OutputFormat.Table);
            Assert.Contains("[2/3] nodes | where | count\nerror syntax-error: ", text);
            Assert.EndsWith("[3/3] nodes | count\n2", text);
        }

        [Fact]
        public void AWriteInAReadRequestFailsOnItsOwn()
        {
            var (e, nodes) = Engine();
            var r = e.Execute("nodes | set Value = 9; nodes | count");
            Assert.False(r.Ok);
            Assert.Equal("permission-denied", r.Statements![0].Result!.Diagnostics[0].Code);
            Assert.True(r.Statements[1].Result!.Ok);
            Assert.All(nodes, n => Assert.NotEqual(9, n.Value));
        }

        [Fact]
        public void ChangesRunInOrderAndSeeTheEarlierOnes()
        {
            var (e, nodes) = Engine();
            int beforeWrite = 0, committed = 0;
            e.Mutations.BeforeWrite = _ => beforeWrite++;
            e.Mutations.Committed = _ => committed++;
            var r = e.Execute("nodes | where Name == \"a\" | set Value = 10; nodes | where Value == 10 | set Name = \"ten\"", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("ten", nodes[0].Name);
            Assert.Equal("b", nodes[1].Name);
            // Each statement is its own change: the host hooks run once per statement.
            Assert.Equal(2, beforeWrite);
            Assert.Equal(2, committed);
        }

        [Fact]
        public void AFailedChangeStopsTheRestAndKeepsTheEarlierChanges()
        {
            var (e, nodes) = Engine();
            var r = e.Execute("nodes | set Value = 5; nodes | set Valu = 6; nodes | set Name = \"z\"", null, Mutate);
            Assert.False(r.Ok);
            Assert.All(nodes, n => Assert.Equal(5, n.Value));
            Assert.All(nodes, n => Assert.NotEqual("z", n.Name));
            Assert.True(r.Statements![0].Result!.Ok);
            Assert.False(r.Statements[1].Result!.Ok);
            Assert.Null(r.Statements[2].Result);
            Assert.Contains("[3/3] nodes | set Name = \"z\"\nnot run: statement 2 failed", AgentFormat.Render(r, OutputFormat.Table));
        }

        [Fact]
        public void AChangeRunsOnlyWhenEveryStatementIsValid()
        {
            var (e, nodes) = Engine();
            var syntax = e.Execute("nodes | set Value = 5; nodes | set Name =", null, Mutate);
            Assert.False(syntax.Ok);
            Assert.Null(syntax.Statements![0].Result);
            Assert.Contains("statement 2 is invalid", syntax.Statements[0].Skipped);
            Assert.Equal("syntax-error", syntax.Statements[1].Result!.Diagnostics[0].Code);
            // A read in a change request is invalid there too (a change ends with set or call).
            var read = e.Execute("nodes | set Value = 5; nodes | count", null, Mutate);
            Assert.False(read.Ok);
            Assert.Equal("invalid-plan", read.Statements![1].Result!.Diagnostics[0].Code);
            Assert.All(nodes, n => Assert.NotEqual(5, n.Value));
        }

        [Fact]
        public void DryRunMarksTheRequest()
        {
            var (e, nodes) = Engine();
            var r = e.Execute("nodes | set Value = 5; nodes | set Name = \"q\"", null, new QueryOptions { AllowMutation = true, DryRun = true });
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.True(r.DryRun);
            Assert.All(nodes, n => Assert.NotEqual(5, n.Value));
            Assert.StartsWith("[1/2] nodes | set Value = 5\nDRY RUN — nothing changed; ", AgentFormat.Render(r, OutputFormat.Table));
        }

        [Fact]
        public void TooManyStatementsFailAsAWhole()
        {
            var (e, _) = Engine();
            var r = e.Execute(string.Join(";", Enumerable.Repeat("nodes | count", RqlParser.MaxStatements + 1)));
            Assert.False(r.Ok);
            Assert.Null(r.Statements);
            Assert.Equal("syntax-error", Assert.Single(r.Diagnostics).Code);
            Assert.True(e.Execute(string.Join(";", Enumerable.Repeat("nodes | count", RqlParser.MaxStatements))).Ok);
        }

        [Fact]
        public void JsonListsEachStatement()
        {
            var (e, _) = Engine();
            var r = e.Execute("nodes | set Value = 1; nodes | count", null, Mutate);
            var json = r.ToJson(includeStats: false);
            Assert.StartsWith("{\"rql\":\"0.1\",\"ok\":false,\"statements\":[{\"text\":\"nodes | set Value = 1\",\"skipped\":\"statement 2 is invalid", json);
            Assert.Contains("{\"text\":\"nodes | count\",\"result\":{\"rql\":\"0.1\",\"ok\":false,\"diagnostics\":[{\"severity\":\"error\",\"code\":\"invalid-plan\"", json);
        }

        [Fact]
        public void HostsRunStatementsWithTheirOwnExecutor()
        {
            var (e, _) = Engine();
            var seen = new List<string>();
            var r = RqlScript.Run(RqlParser.SplitStatements("nodes | count; nodes | first"), null, plan =>
            {
                seen.Add(PlanCodec.Serialize(plan));
                return e.Execute(plan);
            });
            Assert.True(r.Ok);
            Assert.Equal(2, seen.Count);
            Assert.Contains("\"count\"", seen[0]);
        }
    }
}
