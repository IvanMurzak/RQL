using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>
    /// ADR-0010: overload resolution without side effects, engine objects never built from literals, the best-matching
    /// overload's failure, arguments by name from one object literal, and compact receipts in the agent table format.
    /// </summary>
    public class OverloadTests
    {
        /// <summary>An engine object: its constructor registers it in the scene (Unity's <c>new GameObject()</c>).</summary>
        public sealed class EngineObject
        {
            public static int Constructed;
            public EngineObject() { Constructed++; }
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        /// <summary>Plain data whose constructions are counted too.</summary>
        public sealed class Placement
        {
            public static int Constructed;
            public Placement() { Constructed++; }
            public List<double> position { get; set; } = new List<double>();
            public string? parentName { get; set; }
        }

        public sealed class EngineObjectAdapter : RqlTypeAdapter<EngineObject>
        {
            protected override bool? Alive(EngineObject value) => true;
        }

        public sealed class Editor
        {
            public List<EngineObject> Scene { get; } = new List<EngineObject>();

            [RqlCallable("Create under an existing parent.")]
            public EngineObject CreateGameObject(string name, EngineObject parent, string primitive) => Add(name);

            [RqlCallable("Create at a placement.")]
            public EngineObject CreateGameObject(string name, Placement placement, string primitive) => Add(name);

            [RqlCallable("Create from named options.")]
            public EngineObject Spawn(string name, string primitive = "Cube", double size = 1, List<double>? position = null) =>
                Add(name + ":" + primitive + ":" + size + ":" + (position == null ? "-" : string.Join(",", position)));

            public string? ValidateSpawn(string name, string primitive, double size, List<double>? position) => size <= 0 ? "size must be positive" : null;

            [RqlCallable("Link to an existing object.")]
            public string Link(EngineObject other) => other.Name;

            private EngineObject Add(string name)
            {
                var o = new EngineObject { Id = 100 + Scene.Count, Name = name };
                Scene.Add(o);
                return o;
            }
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };
        private static readonly QueryOptions DryRun = new QueryOptions { AllowMutation = true, DryRun = true };

        private static (RqlEngine engine, Editor editor) Host(bool engineObjects = true)
        {
            var editor = new Editor();
            var e = new RqlEngine().AddSource("editor", () => editor);
            if (engineObjects) e.AddAdapter(new EngineObjectAdapter());
            e.Mutations.Enabled = true;
            e.Mutations.MaxTargets = 500;
            return (e, editor);
        }

        private static string Error(QueryResult r)
        {
            Assert.False(r.Ok, r.ToJson(includeStats: false));
            return r.Diagnostics[0].Code + ": " + r.Diagnostics[0].Message;
        }

        // ------------------------------------------------------------ side effects

        [Fact]
        public void OverloadResolutionConstructsNothingForTheOverloadsNotChosen()
        {
            var (e, editor) = Host();
            EngineObject.Constructed = 0;
            Placement.Constructed = 0;
            var r = e.Execute("[1] | call editor.CreateGameObject(\"X\", {position: [1, 2, 3]}, \"Cube\")", null, DryRun);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("CreateGameObject(string name, Placement placement, string primitive)", r.Rows![0][1]!.GetValue<string>());
            Assert.Equal(0, EngineObject.Constructed); // the engine-object overload was checked, never built
            Assert.Equal(1, Placement.Constructed);    // only the chosen overload's argument is built
            Assert.Empty(editor.Scene);
        }

        [Fact]
        public void EmptyLiteralNeverBuildsAnEngineObjectEvenWhenItIsTheOnlyOverload()
        {
            var (e, editor) = Host();
            EngineObject.Constructed = 0;
            foreach (var options in new[] { DryRun, Mutate })
            {
                var err = Error(e.Execute("[1] | call editor.Link({})", null, options));
                Assert.StartsWith("coercion-failed: ", err);
                Assert.Contains("EngineObject is an engine object and cannot be built from an object literal", err);
            }
            Assert.Equal(0, EngineObject.Constructed);
            Assert.Empty(editor.Scene);
        }

        [Fact]
        public void TheHostDecidesWhichTypesLiteralsMayBuild()
        {
            // Without an identity or liveness adapter the type is plain data by default …
            var (plain, _) = Host(engineObjects: false);
            Assert.True(plain.Execute("[1] | call editor.Link({Name: \"made\"})", null, Mutate).Ok);
            // … unless the host says otherwise; and the host can allow an engine object explicitly.
            plain.Mutations.CanConstructFromLiteral = t => t == typeof(EngineObject) ? false : (bool?)null;
            Assert.Contains("engine object", Error(plain.Execute("[1] | call editor.Link({Name: \"made\"})", null, Mutate)));
            var (allowed, _) = Host();
            allowed.Mutations.CanConstructFromLiteral = t => t == typeof(EngineObject) ? true : (bool?)null;
            var r = allowed.Execute("[1] | call editor.Link({Name: \"made\"})", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("made", r.Rows![0][2]!.GetValue<string>());
        }

        [Fact]
        public void SetRefusesEngineObjectLiteralsToo()
        {
            var editor = new Editor();
            var holder = new Holder();
            var e = new RqlEngine().AddSource("h", () => holder).AddAdapter(new EngineObjectAdapter());
            e.Mutations.Enabled = true;
            EngineObject.Constructed = 0;
            var err = Error(e.Execute("h | set Target = {Name: \"x\"}", null, DryRun));
            Assert.Contains("engine object", err);
            Assert.Equal(0, EngineObject.Constructed);
        }

        public sealed class Holder
        {
            public int Id { get; set; } = 1;
            [RqlReference] public EngineObject? Target { get; set; }
        }

        // ------------------------------------------------------------ errors across overloads

        [Fact]
        public void ATypoInsideAPlacementIsReportedAgainstThePlacementOverload()
        {
            var (e, _) = Host();
            var err = Error(e.Execute("[1] | call editor.CreateGameObject(\"X\", {positon: [1, 2, 3]}, \"Cube\")", null, DryRun));
            Assert.Equal("coercion-failed: cannot call CreateGameObject on Editor@1 for item 0: argument 'placement': no member 'positon' on Placement", err);
        }

        [Fact]
        public void OverloadsThatCameEquallyCloseAreAllListed()
        {
            var (e, _) = Host();
            var err = Error(e.Execute("[1] | call editor.CreateGameObject(\"X\", 5, \"Cube\")", null, DryRun));
            Assert.Contains("no overload fits: CreateGameObject(string name, EngineObject parent, string primitive): argument 'parent': ", err);
            Assert.Contains("; CreateGameObject(string name, Placement placement, string primitive): argument 'placement': ", err);
        }

        [Fact]
        public void AnArityMismatchNamesTheAcceptedCounts()
        {
            var (e, _) = Host();
            Assert.EndsWith("expects 3 argument(s), got 1", Error(e.Execute("[1] | call editor.CreateGameObject(\"X\")", null, DryRun)));
        }

        // ------------------------------------------------------------ arguments by name

        [Fact]
        public void OneObjectLiteralBindsByParameterName()
        {
            var (e, editor) = Host();
            var r = e.Execute("range(0, 2) | call editor.Spawn({name: \"Crate\" + $, position: [$, 0, 0], size: 2})", null, Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "Crate0:Cube:2:0,0,0", "Crate1:Cube:2:1,0,0" }, editor.Scene.Select(o => o.Name));
            Assert.Equal("Spawn(string name, string primitive, double size, List<double> position)", r.Rows![0][1]!.GetValue<string>());
        }

        [Fact]
        public void NamedArgumentsKeepValidatorsAndDryRuns()
        {
            var (e, editor) = Host();
            Assert.Equal("invalid-value: cannot call Spawn(string name, string primitive, double size, List<double> position) on Editor@1 for item 0: size must be positive; nothing was called",
                Error(e.Execute("[1] | call editor.Spawn({name: \"A\", size: 0})", null, DryRun)));
            var dry = e.Execute("[1] | call editor.Spawn({name: \"A\"})", null, DryRun);
            Assert.True(dry.Ok && dry.DryRun);
            Assert.Empty(editor.Scene);
        }

        [Fact]
        public void UnknownAndMissingParameterNamesAreExplained()
        {
            var (e, _) = Host();
            Assert.EndsWith("no parameter 'sise' (did you mean 'size'?); parameters: name, primitive, size, position",
                Error(e.Execute("[1] | call editor.Spawn({name: \"A\", sise: 2})", null, DryRun)));
            Assert.EndsWith("missing parameter 'name'; parameters: name, primitive, size, position",
                Error(e.Execute("[1] | call editor.Spawn({size: 2})", null, DryRun)));
            Assert.EndsWith("parameter 'size': expected a number",
                Error(e.Execute("[1] | call editor.Spawn({name: \"A\", size: \"big\"})", null, DryRun)));
        }

        // ------------------------------------------------------------ compact receipts

        [Fact]
        public void LongCallReceiptsPrintTheMethodOnceAndDropRepeatedRefs()
        {
            var (e, _) = Host();
            var r = e.Execute("range(0, 60) | call editor.Spawn({name: \"N\" + $})", null, Mutate);
            Assert.True(r.Ok && r.Receipt);
            var lines = AgentFormat.Render(r, OutputFormat.Table).Split('\n');
            Assert.Equal("60 results", lines[0]);
            Assert.Equal("method: Spawn(string name, string primitive, double size, List<double> position)", lines[1]);
            Assert.Equal("result", lines[2]);
            Assert.Equal("EngineObject#100(N0:Cube:1:-)", lines[3]);
            Assert.Equal(3 + AgentFormat.DefaultReceiptRows + 1, lines.Length);
            Assert.Equal("… 10 more row(s) not shown (the json format lists every row)", lines[^1]);
            // Every row stays in the JSON form, and a host can list them all in the table too.
            Assert.Equal(60, ((JsonArray)JsonNode.Parse(AgentFormat.Render(r, OutputFormat.Json))!["rows"]!).Count);
            Assert.Equal(3 + 60, AgentFormat.Render(r, OutputFormat.Table, maxReceiptRows: 0).Split('\n').Length);
        }

        [Fact]
        public void ReceiptsKeepColumnsThatCarryInformation()
        {
            var (e, _) = Host();
            // A dry run returns no objects: $ref (the receiver) stays, the shared method is printed once.
            var dry = AgentFormat.Render(e.Execute("range(0, 2) | call editor.Spawn({name: \"N\" + $})", null, DryRun), OutputFormat.Table).Split('\n');
            Assert.Equal(new[] { "DRY RUN — nothing changed; 2 results", "method: Spawn(string name, string primitive, double size, List<double> position)", "$ref|result", "Editor@1|(dry-run)", "Editor@1|(dry-run)" }, dry);
            // A single row is printed as before.
            var one = AgentFormat.Render(e.Execute("[1] | call editor.Spawn({name: \"A\"})", null, Mutate), OutputFormat.Table).Split('\n');
            Assert.Equal("$ref|method|result", one[1]);
        }
    }
}
