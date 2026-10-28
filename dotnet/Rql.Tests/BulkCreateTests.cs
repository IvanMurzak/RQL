using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>ADR-0008 bulk create: range(), list sources and source-qualified call; plus the encoder cycle guard, adapter
    /// members as known names and the shared language rules.</summary>
    public class BulkCreateTests
    {
        public sealed class Thing
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public double X { get; set; }
            public double Z { get; set; }
            public List<string> Tags { get; set; } = new List<string>();
        }

        public sealed class Placement
        {
            public List<double> position { get; set; } = new List<double>();
        }

        public sealed class Editor
        {
            public List<Thing> Created { get; } = new List<Thing>();
            public int Calls;

            [RqlCallable("Create a thing at (x, z).")]
            public Thing Create(string name, double x = 0, double z = 0)
            {
                Calls++;
                if (name == "boom") throw new InvalidOperationException("cannot create boom");
                var t = new Thing { Id = 100 + Created.Count, Name = name, X = x, Z = z };
                Created.Add(t);
                return t;
            }

            public string? ValidateCreate(string name, double x, double z) => name.Length == 0 ? "name must not be empty" : null;

            [RqlCallable("Create a thing from a placement literal.")]
            public Thing CreateObject(string name, Placement at) => Create(name, at.position.Count > 0 ? at.position[0] : 0, at.position.Count > 2 ? at.position[2] : 0);

            [RqlCallable("Logs a value; returns nothing.")]
            public void Log(string text) => Calls++;
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };
        private static readonly QueryOptions DryRun = new QueryOptions { AllowMutation = true, DryRun = true };

        private static string Items(QueryResult r)
        {
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            return "[" + string.Join(",", r.Items!.Select(i => i?.ToJsonString() ?? "null")) + "]";
        }

        private static (RqlEngine engine, Editor editor, List<Thing> things) Host()
        {
            var editor = new Editor();
            var things = new List<Thing> { new Thing { Id = 1, Name = "a" }, new Thing { Id = 2, Name = "b" } };
            var e = new RqlEngine().AddSource("editor", () => editor).AddSource("things", () => things);
            e.Mutations.Enabled = true;
            e.Mutations.MaxTargets = 200;
            return (e, editor, things);
        }

        // ------------------------------------------------------------ range()

        [Fact]
        public void RangeStreamsNumbers()
        {
            var (e, _, _) = Host();
            Assert.Equal("[0,1,2,3,4]", Items(e.Execute("range(0, 5)")));
            Assert.Equal("[10,8,6]", Items(e.Execute("range(10, 4, -2)")));
            Assert.Equal("[0,0.25,0.5,0.75]", Items(e.Execute("range(0, 1, 0.25)")));
            Assert.Equal("[]", Items(e.Execute("range(3, 3)")));
            Assert.Equal("[]", Items(e.Execute("range(5, 0)")));
            var r = e.Execute("range(0, 4) | select $index, $path, sq = ($ * $)");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("range(…)[3]", r.Rows![3][1]!.GetValue<string>());
            Assert.Equal(9, r.Rows[3][2]!.GetValue<long>());
        }

        [Fact]
        public void RangeIsLazyAndBoundedByMaxVisited()
        {
            var (e, _, _) = Host();
            var r = e.Execute("range(0, 1000000000) | first 3");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(3, r.Items!.Count);
            var big = e.Execute("range(0, 1000000000) | count", new Budgets { MaxVisited = 1000 });
            Assert.False(big.Ok);
            Assert.Equal("budget-exceeded", big.Diagnostics[0].Code);
        }

        [Theory]
        [InlineData("range(0, 5, 0)", "type-mismatch", "step must not be 0")]
        [InlineData("range(0, \"5\")", "type-mismatch", "expects numbers")]
        [InlineData("range(0)", "arity", "2-3 argument")]
        public void RangeRejectsBadArguments(string q, string code, string message)
        {
            var (e, _, _) = Host();
            var r = e.Execute(q);
            Assert.False(r.Ok);
            Assert.Equal(code, r.Diagnostics[0].Code);
            Assert.Contains(message, r.Diagnostics[0].Message);
        }

        [Fact]
        public void RangeIsAFunctionInExpressions()
        {
            var (e, _, _) = Host();
            var r = e.Execute("things | first 1 | select xs = range(1, 4), n = range(0, 10).count()");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("[1,2,3]", r.Rows![0][0]!.ToJsonString());
            Assert.Equal(10, r.Rows[0][1]!.GetValue<long>());
        }

        [Fact]
        public void ListLiteralIsASource()
        {
            var (e, _, _) = Host();
            Assert.Equal("[\"a\",2,[3,4]]", Items(e.Execute("[\"a\", 2, [3, 4]]")));
            var r = e.Execute("[[\"x\", 1], [\"y\", 2]] | select n = $[0], v = $[1]");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("y", r.Rows![1][0]!.GetValue<string>());
        }

        // ------------------------------------------------------------ source-qualified call

        [Fact]
        public void QualifiedCallRunsOncePerItem()
        {
            var (e, editor, _) = Host();
            var r = e.Execute("range(0, 30) | call editor.CreateObject(\"Cube\" + $index, {position: [($ % 15) * 2, 0, floor($ / 15) * 2]})", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(30, editor.Created.Count);
            Assert.Equal(new[] { "$ref", "method", "result" }, r.Columns);
            Assert.Equal(30, r.Rows!.Count);
            Assert.Equal("Thing#116", r.Rows[16][0]!.GetValue<string>());
            var t = editor.Created[16];
            Assert.Equal(("Cube16", 2.0, 2.0), (t.Name, t.X, t.Z));
        }

        [Fact]
        public void QualifiedCallTakesListsAndOtherStreams()
        {
            var (e, editor, _) = Host();
            var r = e.Execute("[[\"A\", 1], [\"B\", 2]] | call editor.Create($[0], $[1])", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "A", "B" }, editor.Created.Select(c => c.Name));
            r = e.Execute("things | call editor.Create(Name + \"_copy\", Id)", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "A", "B", "a_copy", "b_copy" }, editor.Created.Select(c => c.Name));
            r = e.Execute("things | select n = Name | call editor.Create(n + \"!\")", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("b!", editor.Created.Last().Name);
        }

        [Fact]
        public void QualifiedCallResultWithoutIdentityKeepsTheReceiverRef()
        {
            var (e, editor, _) = Host();
            var r = e.Execute("range(0, 2) | call editor.Log(\"x\" + $)", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.StartsWith("Editor@", r.Rows![0][0]!.GetValue<string>());
            Assert.Equal(2, editor.Calls);
        }

        [Fact]
        public void QualifiedCallChecksEveryCallBeforeTheFirst()
        {
            var (e, editor, _) = Host();
            var r = e.Execute("[\"a\", \"\", \"c\"] | call editor.Create($)", null, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("invalid-value", r.Diagnostics[0].Code);
            Assert.Contains("for item 1", r.Diagnostics[0].Message);
            Assert.Contains("name must not be empty", r.Diagnostics[0].Message);
            Assert.Equal(0, editor.Calls);
            // dry runs validate too, and call nothing
            r = e.Execute("[\"a\", \"\"] | call editor.Create($)", null, DryRun);
            Assert.False(r.Ok);
            r = e.Execute("[\"a\", \"b\"] | call editor.Create($)", null, DryRun);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.True(r.DryRun);
            Assert.Equal(2, r.Rows!.Count);
            Assert.Equal(0, editor.Calls);
        }

        [Fact]
        public void QualifiedCallGivesTheHooksEveryRecordAtOnce()
        {
            var (e, editor, _) = Host();
            var batches = new List<int>();
            var before = new List<int>();
            e.Mutations.ValidateBatch = b => { batches.Add(b.Count); return null; };
            e.Mutations.BeforeWrite = b => before.Add(b.Count);
            var r = e.Execute("range(0, 12) | call editor.Create(\"n\" + $)", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { 12 }, batches);
            Assert.Equal(new[] { 12 }, before);
            e.Mutations.ValidateBatch = b => b.Count > 5 ? "too many" : null;
            r = e.Execute("range(0, 12) | call editor.Create(\"n\" + $)", null, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("invalid-value", r.Diagnostics[0].Code);
            Assert.Equal(12, editor.Created.Count);
        }

        [Fact]
        public void QualifiedCallCountsAgainstTheTargetLimit()
        {
            var (e, editor, _) = Host();
            var r = e.Execute("range(0, 300) | call editor.Create(\"n\" + $)", null, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("mutation-limit", r.Diagnostics[0].Code);
            Assert.Contains("300 items reached 'call'", r.Diagnostics[0].Message);
            Assert.Contains("= 300", r.Diagnostics[0].Hint);
            Assert.Equal(0, editor.Calls);
            r = e.Execute("range(0, 300) | call editor.Create(\"n\" + $)", new Budgets { MaxMutationTargets = 300 }, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(300, editor.Created.Count);
            // a huge range stops counting at the ceiling
            r = e.Execute("range(0, 1000000000) | call editor.Create(\"n\" + $)", null, Mutate);
            Assert.Equal("mutation-limit", r.Diagnostics[0].Code);
            Assert.Contains("more than 1000 items reached", r.Diagnostics[0].Message);
        }

        [Fact]
        public void QualifiedCallThatThrowsStopsWithEarlierCallsDone()
        {
            var (e, editor, _) = Host();
            var r = e.Execute("[\"a\", \"boom\", \"c\"] | call editor.Create($)", null, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("call-threw", r.Diagnostics[0].Code);
            Assert.Contains("for item 1", r.Diagnostics[0].Message);
            Assert.Single(editor.Created);
        }

        [Fact]
        public void QualifiedCallTargetErrors()
        {
            var (e, _, _) = Host();
            var r = e.Execute("range(0, 2) | call things.Create($)", null, Mutate);
            Assert.Equal("invalid-plan", r.Diagnostics[0].Code);
            Assert.Contains("is a list of 2", r.Diagnostics[0].Message);
            r = e.Execute("things | call Transform.Translate(1)", null, Mutate);
            Assert.Equal("unknown-source", r.Diagnostics[0].Code);
            Assert.Contains("get Transform | call Translate(…)", r.Diagnostics[0].Hint);
            r = e.Execute("range(0, 0) | call editor.Craete($)", null, Mutate);
            Assert.Equal("unknown-function", r.Diagnostics[0].Code);
            Assert.Contains("Create(", r.Diagnostics[0].Hint);
            r = e.Execute("range(0, 2) | call editor.Create($)", null, null);
            Assert.Equal("permission-denied", r.Diagnostics[0].Code);
        }

        [Fact]
        public void UnqualifiedCallOnNumbersHintsTheQualifiedForm()
        {
            var (e, _, _) = Host();
            var r = e.Execute("range(0, 3) | call Create(\"x\")", null, Mutate);
            Assert.False(r.Ok);
            Assert.Equal("invalid-plan", r.Diagnostics[0].Code);
            Assert.Contains("… | call editor.Create(… $ …)", r.Diagnostics[0].Hint);
            r = e.Execute("things | call Create(\"x\")", null, Mutate);
            Assert.Equal("unknown-function", r.Diagnostics[0].Code);
            Assert.Contains("call editor.Create(… $ …) once per item", r.Diagnostics[0].Hint);
        }

        [Fact]
        public void QualifiedCallRoundTripsThroughThePlanCodec()
        {
            var plan = RqlParser.Parse("range(0, 3) | call editor.Create(\"n\" + $)");
            var json = PlanCodec.Serialize(plan);
            Assert.Contains("\"on\":{\"path\":[\"editor\"]}", json);
            var back = PlanCodec.Deserialize(json);
            Assert.Equal(json, PlanCodec.Serialize(back));
            var refPlan = RqlParser.Parse("[1] | call ref(\"Editor#1\").Create($)");
            Assert.Contains("\"on\":{\"call\":\"ref\"", PlanCodec.Serialize(refPlan));
            var ex = Assert.Throws<RqlException>(() => RqlParser.Parse("[1] | call editor.Create"));
            Assert.Contains("needs parentheses", ex.Message);
        }

        // ------------------------------------------------------------ encoder, known members, language rules

        [Fact]
        public void SelfContainingListsEncode()
        {
            var a = new List<object?> { 1 };
            a.Add(a);
            var m = new Dictionary<string, object?> { ["k"] = 1 };
            m["self"] = m;
            var e = new RqlEngine().AddSource("ls", () => new List<object?> { a, m });
            var r = e.Execute("ls");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("[[1,\"\\u2026\"],{\"k\":1,\"self\":\"\\u2026\"}]", Items(r)); // "…"
        }

        public sealed class Widget { public string Name { get; set; } = ""; }

        private sealed class WidgetAdapter : RqlTypeAdapter<Widget>
        {
            protected override void Members(MemberTable<Widget> members) => members.Add("glow", w => 1.0);
        }

        [Fact]
        public void AdapterTargetsAreKnownMembers()
        {
            var things = new List<object>();
            var e = new RqlEngine().AddSource("objs", () => things);
            e.Mutations.Enabled = true;
            // Widget is not reachable from the sources: before its adapter, 'glow' emptying a set is a typo
            var r = e.Execute("objs | where glow > 0 | set Name = \"x\"", null, Mutate);
            Assert.True(r.Ok); // an empty stream never evaluates glow: a warning at most
            Assert.Contains(r.Diagnostics, d => d.Code == "unknown-member");
            e.AddAdapter(new WidgetAdapter());
            r = e.Execute("objs | where glow > 0 | set Name = \"x\"", null, Mutate);
            Assert.True(r.Ok);
            Assert.DoesNotContain(r.Diagnostics, d => d.Code == "unknown-member");
        }

        [Fact]
        public void GroupRowHintNamesTheKeyColumns()
        {
            var (e, _, _) = Host();
            var d = e.Execute("things | group-by n=Name | select key").Diagnostics.First(x => x.Code == "unknown-member");
            Assert.Contains("select n, count, avg = items.avg(…)", d.Hint);
            Assert.DoesNotContain("items.avg(key)", d.Hint);
            d = e.Execute("things | group-by Name | select key, Z").Diagnostics.First(x => x.Code == "unknown-member");
            Assert.Contains("select key, count, avg = items.avg(Z)", d.Hint);
        }

        [Fact]
        public void LanguageRulesMatchTheSharedFixture()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "fixtures", "guide", "language-rules.txt"))) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            var text = File.ReadAllText(Path.Combine(dir!, "fixtures", "guide", "language-rules.txt")).Replace("\r\n", "\n");
            Assert.Equal(RqlLanguageGuide.Rules, text);
        }
    }
}
