using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    // ReflectorNet / Unity-MCP parity: the gaps found by the converter audit (docs/type-adapters.md).
    public class ParityTests
    {
        public sealed class Mat { public int Id { get; set; } = 1; public string color { get; set; } = "white"; }

        public sealed class Rend
        {
            private Mat[] _mats = { new Mat { Id = 1 }, new Mat { Id = 2 } };
            public int Id { get; set; } = 10;
            public int Reads;
            // Unity semantics: the getter returns a COPY of the array.
            public Mat[] sharedMaterials { get { Reads++; return (Mat[])_mats.Clone(); } set => _mats = (Mat[])value.Clone(); }
            public Mat sharedMaterial { get => _mats[0]; set => _mats[0] = value; }
            public Mat First => _mats[0];
        }

        // A handle struct: copies write through to the shared native state (Unity's ParticleSystem.MainModule).
        public sealed class NativeState { public float Duration = 1f; }
        public struct MainModule
        {
            internal NativeState State;
            public float duration { get => State.Duration; set => State.Duration = value; }
        }

        public sealed class Particles
        {
            private readonly NativeState _state = new NativeState();
            public int Id { get; set; } = 3;
            public MainModule main => new MainModule { State = _state };
            public float NativeDuration => _state.Duration;
        }

        public sealed class Legacy
        {
            public int Id { get; set; } = 5;
            [Obsolete("use Fresh")] public int Old { get; set; } = 1;
            public int Fresh { get; set; } = 2;
            public Action? Callback { get; set; }
            public IntPtr Native { get; set; }
            public List<IntPtr> Handles { get; } = new List<IntPtr>();
        }

        public sealed class Scene
        {
            public List<Rend> Renderers { get; } = new List<Rend> { new Rend() };
            public List<Particles> Systems { get; } = new List<Particles> { new Particles() };
            public List<Legacy> Legacies { get; } = new List<Legacy> { new Legacy() };
        }

        private sealed class RendAdapter : RqlTypeAdapter<Rend>
        {
            protected override void Members(MemberTable<Rend> m) => m.CopyOnRead(nameof(Rend.sharedMaterials)).Hide(nameof(Rend.Reads));
        }

        private sealed class MainAdapter : RqlTypeAdapter<MainModule>
        {
            protected override void Members(MemberTable<MainModule> m) =>
                m.WritesThrough().Add<float>("seconds", x => x.duration, (x, v) => { var copy = x; copy.duration = v; });
        }

        private sealed class NoObsolete : RqlTypeAdapter<object>
        {
            protected override void Members(MemberTable<object> m) =>
                m.HideWhere(x => x.Member != null && x.Member.IsDefined(typeof(ObsoleteAttribute), true));
        }

        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };

        private static RqlEngine Engine(Scene s)
        {
            var e = new RqlEngine();
            e.AddSource("s", () => s);
            e.Mutations.Enabled = true;
            e.AddAdapter(new RendAdapter()).AddAdapter(new MainAdapter());
            return e;
        }

        [Fact]
        public void WritesIntoCopyOnReadMembersAreAssignedBack()
        {
            var s = new Scene();
            var e = Engine(s);
            var replacement = new Mat { Id = 9, color = "red" };
            e.AddSource("m", () => replacement);
            var r = e.Execute("s.Renderers | set sharedMaterials[1] = ref(\"Mat#9\")", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Same(replacement, s.Renderers[0].sharedMaterials[1]);
        }

        // Like Unity's TerrainData.treePrototypes: the getter returns new copies of class objects, only the setter stores.
        public sealed class Proto { public string name = ""; public double bend; }

        public sealed class TerrainLike
        {
            private Proto[] _protos = { new Proto { name = "oak", bend = 0.1 }, new Proto { name = "pine", bend = 0.2 } };
            public int Assignments;
            public Proto[] prototypes
            {
                get => _protos.Select(p => new Proto { name = p.name, bend = p.bend }).ToArray();
                set { Assignments++; _protos = value.Select(p => new Proto { name = p.name, bend = p.bend }).ToArray(); }
            }
            public Proto[] Stored => _protos;
        }

        private sealed class TerrainLikeAdapter : RqlTypeAdapter<TerrainLike>
        {
            protected override void Members(MemberTable<TerrainLike> m) => m.CopyOnRead(nameof(TerrainLike.prototypes)).Hide(nameof(TerrainLike.Stored), nameof(TerrainLike.Assignments));
        }

        [Fact]
        public void WritesIntoObjectsInsideCopyOnReadMembersAreAssignedBack()
        {
            var t = new TerrainLike();
            var e = new RqlEngine().AddSource("t", () => t).AddAdapter(new TerrainLikeAdapter());
            e.Mutations.Enabled = true;
            var r = e.Execute("t | set prototypes[1].bend = 0.7", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(0.7, t.Stored[1].bend);
            Assert.Equal(0.1, t.Stored[0].bend);

            // All-or-nothing: a failing second assignment rolls the first back through the same write-back.
            var bad = e.Execute("t | set prototypes[0].bend = 0.9, prototypes[0].name = 5", options: Mutate);
            Assert.False(bad.Ok);
            Assert.Equal(0.1, t.Stored[0].bend);
        }

        [Fact]
        public void HandleStructsWriteThroughAndCanBeTargets()
        {
            var s = new Scene();
            var e = Engine(s);
            // main is get-only, but MainModule is a handle: the write is live, no write-back needed.
            var r = e.Execute("s.Systems | set main.duration = 4", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(4f, s.Systems[0].NativeDuration);
            var v = e.Execute("s.Systems | get main | set seconds = 6", options: Mutate);
            Assert.True(v.Ok, v.ToJson(includeStats: false));
            Assert.Equal(6f, s.Systems[0].NativeDuration);
        }

        [Fact]
        public void HostHooksSeeOwnersAndCanVeto()
        {
            var s = new Scene();
            var e = Engine(s);
            var before = new List<MutationRecord>();
            var after = new List<MutationRecord>();
            e.Mutations.BeforeWrite = recs => before.AddRange(recs);
            e.Mutations.Committed = recs => after.AddRange(recs);
            var r = e.Execute("s.Renderers | set sharedMaterial.color = \"blue\"", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Single(after);
            Assert.Same(s.Renderers[0].First, after[0].Owner);      // the Material changed, not the Renderer
            Assert.Same(s.Renderers[0], after[0].Item);
            Assert.Equal("blue", after[0].New);
            Assert.Single(before);

            after.Clear();
            var dry = e.Execute("s.Renderers | set sharedMaterial.color = \"green\"", options: new QueryOptions { AllowMutation = true, DryRun = true });
            Assert.True(dry.Ok);
            Assert.Empty(after);

            e.Mutations.BeforeWrite = recs => throw new InvalidOperationException("locked by version control");
            var vetoed = e.Execute("s.Renderers | set sharedMaterial.color = \"black\"", options: Mutate);
            Assert.Equal("permission-denied", vetoed.Diagnostics[0].Code);
            Assert.Contains("locked by version control", vetoed.Diagnostics[0].Message);
            Assert.Equal("blue", s.Renderers[0].First.color);

            e.Mutations.BeforeWrite = null;
            e.Mutations.Committed = recs => throw new InvalidOperationException("dirty failed");
            var warned = e.Execute("s.Renderers | set sharedMaterial.color = \"pink\"", options: Mutate);
            Assert.True(warned.Ok);
            Assert.Contains(warned.Diagnostics, d => d.Code == "hook-threw" && d.Message.Contains("dirty failed"));
            Assert.Equal("pink", s.Renderers[0].First.color);
        }

        public sealed class Shader { public int Id { get; set; } = 4; public Dictionary<string, double> Stats { get; } = new Dictionary<string, double> { ["hp"] = 1 }; public float Gloss = 0.5f; }

        private sealed class ShaderMap : RqlTypeAdapter<Shader>
        {
            protected override void Members(MemberTable<Shader> m) =>
                m.Map<float>("Floats", x => new[] { "_Gloss" }, (x, k) => x.Gloss, (x, k, v) => x.Gloss = v);
        }

        [Fact]
        public void OwnerIsTheNearestEnclosingObject()
        {
            var s = new Scene();
            var shader = new Shader();
            var e = Engine(s);
            e.AddSource("sh", () => shader);
            e.AddAdapter(new ShaderMap());
            var owners = new List<object>();
            e.Mutations.Committed = recs => owners.AddRange(recs.Select(r => r.Owner));
            Assert.True(e.Execute("sh | set Floats[\"_Gloss\"] = 0.25", options: Mutate).Ok);        // virtual map view → the shader
            Assert.True(e.Execute("sh | set Stats[\"hp\"] = 2", options: Mutate).Ok);               // dictionary → the shader
            Assert.True(e.Execute("s.Systems | set main.duration = 2", options: Mutate).Ok);         // handle struct → the system
            Assert.Same(shader, owners[0]);
            Assert.Same(shader, owners[1]);
            Assert.Same(s.Systems[0], owners[2]);
        }

        [Fact]
        public void VirtualMapsOnlyTakeExistingKeys()
        {
            var shader = new Shader();
            var e = new RqlEngine();
            e.AddSource("sh", () => shader);
            e.Mutations.Enabled = true;
            e.AddAdapter(new ShaderMap());
            var r = e.Execute("sh | set Floats[\"_New\"] = 1", options: Mutate);
            Assert.Equal("invalid-value", r.Diagnostics[0].Code);
            Assert.Contains("no entry '_New'", r.Diagnostics[0].Message);
        }

        [Fact]
        public void ConstructionHonoursTheHostWritePolicy()
        {
            var s = new Scene();
            var e = Engine(s);
            e.Mutations.CanWrite = (t, m) => !(t == typeof(Mat) && m.Name == "color");
            var r = e.Execute("s.Renderers | set sharedMaterial = {color: \"red\"}", options: Mutate);
            Assert.Equal("permission-denied", r.Diagnostics[0].Code);
            Assert.Equal("white", s.Renderers[0].First.color);
        }

        [Fact]
        public void ExcludedTypesAreNeverConstructed()
        {
            var s = new Scene();
            var e = Engine(s);
            e.ExcludeTypes(t => t == typeof(SecretThing));
            e.AddSource("h", () => new Holder());
            var r = e.Execute("h | set Thing = {\"$type\": \"SecretThing\"}", options: Mutate);
            Assert.Equal("coercion-failed", r.Diagnostics[0].Code);
            Assert.Contains("no concrete type named 'SecretThing'", r.Diagnostics[0].Message);
            Assert.True(e.Execute("h | set Thing = {\"$type\": \"PlainThing\"}", options: Mutate).Ok);
        }

        public class BaseThing { }
        public sealed class SecretThing : BaseThing { }
        public sealed class PlainThing : BaseThing { }
        public sealed class Holder { public int Id { get; set; } = 1; public BaseThing? Thing { get; set; } }

        [Fact]
        public void PredicateHidingAndTypeExclusion()
        {
            var s = new Scene();
            var e = Engine(s);
            e.AddAdapter(new NoObsolete());
            e.ExcludeTypes(t => t == typeof(IntPtr));
            var schema = e.Execute("s.Legacies | describe").ToJson(includeStats: false);
            Assert.DoesNotContain("\"Old\"", schema);
            Assert.DoesNotContain("\"Native\"", schema);
            Assert.DoesNotContain("\"Handles\"", schema); // List<IntPtr>: excluded through the generic argument
            Assert.DoesNotContain("\"Callback\"", schema); // delegates are never members
            Assert.Contains("\"Fresh\"", schema);
        }

        [Fact]
        public void DestroyedObjectsAreSkippedLikeNullAndNeverWritten()
        {
            var s = new Scene();
            var e = Engine(s);
            e.AddAdapter(new DeadRend());
            var r = e.Execute("s.Renderers | set Id = 11", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Empty(r.Rows!);
            Assert.Equal(10, s.Renderers[0].Id);
            // The host hook alone (no adapter) gives the same semantics: one liveness definition.
            var hooked = Engine(new Scene());
            hooked.IsAlive = o => !(o is Rend);
            Assert.Equal("true", hooked.Execute("s | select dead=Renderers.any($ == null)").Rows![0][0]!.ToJsonString());
        }

        private sealed class DeadRend : RqlTypeAdapter<Rend> { protected override bool? Alive(Rend r) => false; }

        [Fact]
        public void ObjectLiteralSubtypeErrorsAreExplained()
        {
            var s = new Scene();
            var e = Engine(s);
            var r = e.Execute("s.Renderers | set sharedMaterial = {\"$type\": \"Nope\"}", options: Mutate);
            Assert.Equal("coercion-failed", r.Diagnostics[0].Code);
            Assert.Contains("no concrete type named 'Nope'", r.Diagnostics[0].Message);
            var ok = e.Execute("s.Renderers | set sharedMaterial = {color: \"teal\"}", options: Mutate);
            Assert.True(ok.Ok, ok.ToJson(includeStats: false));
            Assert.Equal("teal", s.Renderers[0].First.color);
        }

        [Fact]
        public void RefIdsWithSpacesResolveExactlyFirst()
        {
            var assets = new List<Asset> { new Asset { Path = "Assets/My Mats/Red (1).mat" }, new Asset { Path = "Assets/My" } };
            var e = new RqlEngine();
            e.AddSource("a", () => assets);
            e.AddAdapter(new AssetIds());
            var r = e.Execute("ref(\"Asset#Assets/My Mats/Red (1).mat\") | select Path");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("\"Assets/My Mats/Red (1).mat\"", r.Rows![0][0]!.ToJsonString());
        }

        public sealed class Asset { public string Path { get; set; } = ""; }

        private sealed class AssetIds : RqlTypeAdapter<Asset> { protected override string? Id(Asset a) => a.Path; }

        [Fact]
        public void ModulesAreDiscoveredOrderedAndIsolated()
        {
            var e = new RqlEngine();
            var reports = e.AddModulesFrom(new[] { typeof(ParityTests).Assembly });
            var names = reports.Select(r => r.Module.Split('+').Last()).ToList();
            Assert.Equal(new[] { nameof(FirstModule), nameof(SecondModule), nameof(ThrowingModule) }, names.Where(n => n.EndsWith("Module")).ToArray());
            Assert.Contains(reports, r => r.Module.EndsWith(nameof(ThrowingModule)) && !r.Ok && r.Error!.Contains("broken module"));
            // The modules' adapters are registered: Second's adapter (priority 1) answers first.
            Assert.Equal(new[] { nameof(SecondDiscovered), nameof(FirstDiscovered) },
                e.Types.Explain(typeof(Discovered)).Select(x => x.Split(' ')[0]).ToArray());
            Assert.Equal(reports.Count, e.Modules.Count);
        }

        [Fact]
        public void ModulesCanBeGivenAsTypes()
        {
            var e = new RqlEngine();
            var reports = e.AddModules(new[] { typeof(SecondModule), typeof(string), typeof(FirstModule), typeof(SecondModule) });
            Assert.Equal(new[] { nameof(FirstModule), nameof(SecondModule) }, reports.Select(r => r.Module.Split('+').Last()).ToArray());
            Assert.All(reports, r => Assert.True(r.Ok));
            Assert.Equal(2, e.Types.Explain(typeof(Discovered)).Count);
        }

        public sealed class ViewHolder { public string Name { get; set; } = "h"; public ViewLike View { get; } = new ViewLike(); }
        public sealed class ViewLike { public double Tension { get; set; } }

        [Fact]
        public void MutationRecordsListEveryEnclosingObject()
        {
            var h = new ViewHolder();
            var e = new RqlEngine().AddSource("h", () => h);
            e.Mutations.Enabled = true;
            IReadOnlyList<MutationRecord>? seen = null;
            e.Mutations.BeforeWrite = records => seen = records;
            Assert.True(e.Execute("h | set View.Tension = 0.5", options: Mutate).Ok);
            Assert.Same(h.View, seen![0].Owner);
            Assert.Equal(new object[] { h.View, h }, seen[0].Owners);
        }

        [Fact]
        public void ErrorCellsExplainThemselvesInTheTableFormat()
        {
            var e = new RqlEngine().AddSource("items", () => new[] { 1, 2 })
                .AddFunction("host.check", a => new RqlError("type-mismatch", "host.check expects a string"));
            var r = e.Execute("items | select v=($) c=(host.check($))");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(new[] { "type-mismatch: host.check expects a string" }, r.CellErrors);
            Assert.DoesNotContain("expects a string", r.ToJson(includeStats: false));                 // JSON contract unchanged
            Assert.Contains("note: error cell type-mismatch: host.check expects a string", AgentFormat.Render(r, OutputFormat.Table));
        }

        public sealed class ModeHost { public ModesA.FilterMode A { get; set; } public ModesB.FilterMode B { get; set; } }

        [Fact]
        public void SchemaToleratesTypesWithTheSameShortName()
        {
            var e = new RqlEngine().AddSource("h", () => new ModeHost());
            Assert.Contains("FilterMode", AgentFormat.Schema(e));
            Assert.StartsWith("error unknown-type", AgentFormat.Schema(e, "NoSuchType"));
        }

        [Fact]
        public void GuidesAreOrderedAndReplacedByTopic()
        {
            var e = new RqlEngine().AddGuide("splines", "a").AddGuide("terrain", "b").AddGuide("splines", "c");
            Assert.Equal(new[] { "terrain:b", "splines:c" }, e.Guides.Select(g => g.Key + ":" + g.Value).ToArray());
        }

        [Fact]
        public void FunctionsCarryDescriptionsIntoTheSchema()
        {
            var e = new RqlEngine().AddSource("items", () => new[] { 1, 2 }).AddFunction("host.find", _ => new[] { 3 }, "find host things");
            var schema = AgentFormat.Schema(e);
            Assert.Contains("host.find(…) — find host things", schema);
            Assert.Equal("find host things", e.FunctionDescriptions["host.find"]);
        }

        public sealed class Discovered { }
        public sealed class FirstDiscovered : RqlTypeAdapter<Discovered> { public FirstDiscovered() : base(priority: 0) { } }
        public sealed class SecondDiscovered : RqlTypeAdapter<Discovered> { public SecondDiscovered() : base(priority: 1) { } }

        public sealed class FirstModule : IRqlModule
        {
            public int Order => 1;
            public void Configure(RqlModuleContext c) => c.AddAdapter(new FirstDiscovered());
        }

        public sealed class SecondModule : IRqlModule
        {
            public int Order => 2;
            public void Configure(RqlModuleContext c) => c.AddAdapter(new SecondDiscovered());
        }

        public sealed class ThrowingModule : IRqlModule
        {
            public int Order => 3;
            public void Configure(RqlModuleContext c) => throw new InvalidOperationException("broken module");
        }
    }
}

namespace Rql.Tests.ModesA { public enum FilterMode { Point, Bilinear } }
namespace Rql.Tests.ModesB { public enum FilterMode { Off, On } }
