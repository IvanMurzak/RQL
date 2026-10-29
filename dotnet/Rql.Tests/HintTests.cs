using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>ADR-0013: named-argument typos, host rule examples, receipt notes, module registrations, receipts never paged.</summary>
    [Collection("module scans")]
    public class HintTests
    {
        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        public sealed class NodeOptions
        {
            public string name { get; set; } = "";
            public List<double> position { get; set; } = new List<double>();
        }

        public sealed class Godot
        {
            public List<string> Made { get; } = new List<string>();

            [RqlCallable("Create from options.")]
            public string CreateNode(NodeOptions options) { Made.Add(options.name); return options.name; }

            [RqlCallable("Create a node of a class.")]
            public string CreateNode(string className, string name = "", List<double>? position = null) { Made.Add(className); return className; }
        }

        [Fact]
        public void MisspeltParameterNameBeatsTheOptionsOverload()
        {
            var g = new Godot();
            var e = new RqlEngine().AddSource("godot", () => g);
            e.Mutations.Enabled = true;
            var r = e.Execute("[1] | call godot.CreateNode({klass: \"MeshInstance3D\"})", options: Mutate);
            Assert.Equal("coercion-failed", r.Diagnostics[0].Code);
            Assert.Contains("no parameter 'klass' (did you mean 'className'?)", r.Diagnostics[0].Message);
            // A key no parameter resembles still reports the options overload's member.
            var other = e.Execute("[1] | call godot.CreateNode({zzzzzz: 1})", options: Mutate);
            Assert.DoesNotContain("did you mean", other.Diagnostics[0].Message);
            Assert.Empty(g.Made);
        }

        [Fact]
        public void RulesTakeTheHostsCreationCall()
        {
            var text = RqlLanguageGuide.RulesFor("editor.CreateGameObject");
            Assert.DoesNotContain("editor.CreateObject", text);
            Assert.DoesNotContain("stands for", text);
            Assert.Contains("call editor.CreateGameObject(\"Cube\"", text);
            Assert.Contains("call editor.CreateGameObject($[0], $[1])", text);
            Assert.Equal(RqlLanguageGuide.Rules, RqlLanguageGuide.RulesFor(null));
            Assert.Equal(RqlLanguageGuide.Rules, RqlLanguageGuide.RulesFor("  "));
        }

        [Fact]
        public void RulesTakeTheHostsExamples()
        {
            var spawn = new RqlRuleExamples
            {
                CreateCall = "editor.SpawnActor",
                Grid = "editor.SpawnActor(\"StaticMeshActor\", \"Tile\" + $, [($ % 15) * 200, floor($ / 15) * 200, 0])",
                PerEntry = "[[\"A\", [0, 0, 0]], [\"B\", [200, 0, 0]]] | call editor.SpawnActor(\"PointLight\", $[0], $[1])",
            };
            var text = RqlLanguageGuide.RulesWith(spawn);
            Assert.Contains("range(0, 300) | call " + spawn.Grid + " builds a 15-wide grid", text);
            Assert.Contains(spawn.PerEntry + " one object per entry", text);
            Assert.Contains("call editor.SpawnActor({name: \"Tile\" + $", text); // left out: the default with the host call
            Assert.DoesNotContain("editor.CreateObject", text);
            Assert.DoesNotContain("stands for", text);
            Assert.Equal(RqlLanguageGuide.Rules, RqlLanguageGuide.RulesWith(null));
            Assert.Equal(RqlLanguageGuide.Rules, RqlLanguageGuide.RulesWith(new RqlRuleExamples { Grid = " " }));
            Assert.Contains("stands for", RqlLanguageGuide.RulesWith(new RqlRuleExamples { Grid = spawn.Grid })); // a placeholder is left
            // The call-only form is the old one exactly: the note goes, every placeholder becomes the call.
            Assert.Equal(RqlLanguageGuide.Rules.Replace(" (editor.CreateObject stands for the host's source and creation method)", "").Replace("editor.CreateObject", "x$'y"),
                RqlLanguageGuide.RulesFor(" x$'y "));
        }

        [Fact]
        public void AGridWithAPipeIsTheWholeExample()
        {
            // The host sets the range too, e.g. to stay within its target limit (Unreal: 200).
            var whole = "range(0, 150) | call editor.SpawnActor(\"StaticMeshActor\", \"Tile\" + $, [($ % 15) * 200, floor($ / 15) * 200, 0])";
            var text = RqlLanguageGuide.RulesWith(new RqlRuleExamples { CreateCall = "editor.SpawnActor", Grid = " " + whole + " " });
            Assert.Contains("one call creates many objects: " + whole + " builds a 15-wide grid", text);
            Assert.DoesNotContain("range(0, 300)", text);
            Assert.DoesNotContain("editor.CreateObject", text);
            Assert.Contains("stands for", RqlLanguageGuide.RulesWith(new RqlRuleExamples { Grid = whole })); // other placeholders are left
        }

        public sealed class Painter
        {
            [RqlCallable("By number.")] public string Set(int color) => "int";
            [RqlCallable("By name.")] public string Set(string colour) => "string";
            [RqlCallable("One parameter.")] public string Make(string className) => className;
            [RqlCallable("Two parameters.")] public string Make(string className, int klass) => className + klass;
            [RqlCallable("Acronym.")] public string Open(string URLPath) => URLPath;
        }

        [Fact]
        public void ExactNamesOutrankMisspeltOnesAndBoundParametersAreNotSuggested()
        {
            var p = new Painter();
            var e = new RqlEngine().AddSource("p", () => p);
            e.Mutations.Enabled = true;
            // An overload that has the key's exact name (its value fails) is closer than one whose name it misspells.
            var r = e.Execute("[1] | call p.Set({color: \"red\"})", options: Mutate);
            Assert.Contains("parameter 'color'", r.Diagnostics[0].Message);
            Assert.DoesNotContain("did you mean 'colour'", r.Diagnostics[0].Message);
            // A parameter already named is never the suggestion for another key.
            var m = e.Execute("[1] | call p.Make({className: \"X\", klass: \"Y\"})", options: Mutate);
            Assert.DoesNotContain("did you mean 'className'", m.Diagnostics[0].Message);
            Assert.Contains("parameter 'klass'", m.Diagnostics[0].Message);
            // Acronyms split into words: pth → URLPath.
            Assert.Contains("did you mean 'URLPath'", e.Execute("[1] | call p.Open({pth: \"/x\"})", options: Mutate).Diagnostics[0].Message);
        }

        public sealed class Thing { public int Id { get; set; } public string Name { get; set; } = ""; }

        [Fact]
        public void ReceiptNoteCanBeReplacedOrLeftOut()
        {
            var things = Enumerable.Range(1, 5).Select(i => new Thing { Id = i, Name = "t" + i }).ToList();
            var e = new RqlEngine().AddSource("things", () => things);
            e.Mutations.Enabled = true;
            var r = e.Execute("things | set Name = Name + \"x\"", options: Mutate);
            Assert.Contains("3 more row(s) not shown (the json format lists every row)", AgentFormat.Render(r, OutputFormat.Table, 2));
            Assert.Contains("3 more row(s) not shown (ask for fewer targets)", AgentFormat.Render(r, OutputFormat.Table, 2, "(ask for fewer targets)"));
            var none = AgentFormat.Render(r, OutputFormat.Table, 2, "");
            Assert.Contains("3 more row(s) not shown\n", none + "\n");
            Assert.DoesNotContain("json", none);
        }

        [Fact]
        public void ShortenedReceiptsNeverSuggestPaging()
        {
            var things = Enumerable.Range(1, 200).Select(i => new Thing { Id = i, Name = "t" + i }).ToList();
            var e = new RqlEngine().AddSource("things", () => things);
            e.Mutations.Enabled = true;
            e.Mutations.MaxTargets = 500;
            var r = e.Execute("things | set Name = Name + \"-a-fairly-long-suffix-to-fill-the-receipt\"", new Rql.Budgets { MaxOutputBytes = 2000 }, Mutate);
            Assert.True(r.Ok);
            Assert.True(r.Truncated);
            var w = r.Diagnostics.Single(d => d.Code == "output-truncated");
            Assert.Contains("the receipt is shortened, not the change: all 200 changes were made", w.Hint);
            Assert.DoesNotContain("page", w.Hint);
            var table = AgentFormat.Render(r, OutputFormat.Table, 0);
            Assert.Contains("receipt rows", table);
            Assert.DoesNotContain("page with", table);
            Assert.Equal(200, things.Count(t => t.Name.EndsWith("suffix-to-fill-the-receipt")));
            var dry = e.Execute("things | set Name = Name + \"-b-fairly-long-suffix-to-fill-the-receipt\"", new Rql.Budgets { MaxOutputBytes = 2000 },
                new QueryOptions { AllowMutation = true, DryRun = true });
            Assert.Contains("planned changes were checked (dry run)", dry.Diagnostics.Single(d => d.Code == "output-truncated").Hint);
            // A receipt the byte budget cut gives no pointer to json (which is cut too); the count is of every row.
            var capped = AgentFormat.Render(r, OutputFormat.Table, 5);
            Assert.Contains("195 more row(s) not shown", capped);
            Assert.DoesNotContain("json format", capped);
        }

        public sealed class ModuleA : IRqlModule
        {
            public int Order => 0;
            public void Configure(RqlModuleContext c) =>
                c.AddSource("a.things", () => null).AddFunction("a.find", _ => null).AddGuide("a", "guide A");
        }

        public sealed class ModuleB : IRqlModule
        {
            public int Order => 1;
            public void Configure(RqlModuleContext c)
            {
                c.Engine.AddSource("b.items", () => null); // a direct engine call counts too
                c.AddAdapter(new BAdapter());
                c.AddSource("a.things", () => 1);          // registered again: now B's
            }
        }

        public sealed class BAdapter : RqlTypeAdapter<Thing> { }

        public sealed class DeclaringSources : IRqlModule
        {
            public int Order => 0;
            public void Configure(RqlModuleContext c) =>
                c.AddSource("d.things", () => null, "typed things", typeof(List<Thing>)).AddGuide("d", "guide D");
        }

        [Fact]
        public void ModuleContextDeclaresSourceTypes()
        {
            var e = new RqlEngine();
            var report = e.AddModules(new[] { typeof(DeclaringSources) }).Single();
            Assert.Equal(new[] { "d.things" }, report.Sources);
            Assert.Equal(typeof(List<Thing>), e.Sources["d.things"].DeclaredType);
            Assert.Equal("typed things", e.Sources["d.things"].Description);
        }

        public sealed class Outer : IRqlModule
        {
            /// <summary>Only the nested-configuration test runs it (assembly scans in other tests find it too).</summary>
            public static bool Active;
            public int Order => 0;
            public void Configure(RqlModuleContext c)
            {
                if (!Active) return;
                c.AddSource("o.first", () => null);
                c.Engine.AddModules(new[] { typeof(ModuleA) });
                c.AddSource("o.after", () => null);
            }
        }

        [Fact]
        public void NestedModuleConfigurationKeepsTheOuterRecording()
        {
            var e = new RqlEngine();
            Outer.Active = true;
            RqlModuleReport outer;
            try { outer = e.AddModules(new[] { typeof(Outer) }).Single(r => r.Module.EndsWith("Outer")); }
            finally { Outer.Active = false; }
            Assert.Equal(new[] { "o.first", "o.after" }, outer.Sources);
            Assert.Contains(e.Modules, r => r.Module.EndsWith("ModuleA") && r.Sources.Contains("a.things"));
        }

        [Fact]
        public void ModuleReportsRecordRegistrations()
        {
            var e = new RqlEngine();
            var reports = e.AddModules(new[] { typeof(ModuleA), typeof(ModuleB) });
            var a = reports.Single(r => r.Module.EndsWith("ModuleA"));
            var b = reports.Single(r => r.Module.EndsWith("ModuleB"));
            Assert.Equal(new[] { "a.things" }, a.Sources);
            Assert.Equal(new[] { "a.find" }, a.Functions);
            Assert.Equal(new[] { "a" }, a.Guides);
            Assert.Equal(new[] { "a.things", "b.items" }, b.Sources.OrderBy(s => s, StringComparer.Ordinal));
            Assert.IsType<BAdapter>(Assert.Single(b.Adapters));
            Assert.Empty(b.Functions);
        }
    }
}
