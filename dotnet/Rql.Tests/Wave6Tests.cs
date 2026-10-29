using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    /// <summary>
    /// ADR-0011: literal kinds (engine / value / instanced with an outer), the engine-object check before the abstract check
    /// and the host's refusal advice, known names that never evaluate a source, warnings for failing sort keys and sparse
    /// group-by / distinct keys.
    /// </summary>
    public class Wave6Tests
    {
        private static readonly QueryOptions Mutate = new QueryOptions { AllowMutation = true };
        private static readonly QueryOptions DryRun = new QueryOptions { AllowMutation = true, DryRun = true };

        /// <summary>Every engine type: identity (an id) and liveness, like Godot's GodotObject.</summary>
        public abstract class EngineBase
        {
            private static int _next;
            public int Id { get; } = ++_next;
            public string Name { get; set; } = "";
        }

        /// <summary>A scene object (Godot Node): never built from a literal.</summary>
        public class SceneNode : EngineBase
        {
            public static int Constructed;
            public SceneNode() { Constructed++; }
            public AssetBase? Mesh { get; set; }
            public List<SubObject> Triggers { get; set; } = new List<SubObject>();
            public Placement Where { get; set; }
            [RqlReference] public SceneNode? Target { get; set; }

            [RqlCallable("Adds a trigger; returns its owner's name.")]
            public string AddTrigger(SubObject trigger) { Triggers.Add(trigger); return trigger.Outer?.Name ?? "-"; }

            [RqlCallable("Attaches a node.")]
            public string Attach(SceneNode node) => node.Name;
        }

        /// <summary>An abstract scene object type (Unity's Component).</summary>
        public abstract class AbstractScene : EngineBase { }

        /// <summary>A value-like asset (Godot Resource): it has an identity but is built fresh from a literal.</summary>
        public abstract class AssetBase : EngineBase { }

        public sealed class BoxMesh : AssetBase
        {
            public double Size { get; set; } = 1;
        }

        /// <summary>An instanced subobject (Unreal EditInlineNew): built for its owner.</summary>
        public class SubObject : EngineBase
        {
            public static int Built;
            public SceneNode? Outer { get; set; }
            public double Threshold { get; set; }
            public SubObject? Inner { get; set; }
            [RqlReference] public SubObject? Parent { get; set; }
        }

        public sealed class HoldTrigger : SubObject { }

        public struct Placement
        {
            public SubObject? Trigger { get; set; }
            public double X { get; set; }
        }

        public sealed class EngineAdapter : RqlTypeAdapter<EngineBase>
        {
            protected override string? Id(EngineBase value) => value.Id.ToString();
            protected override bool? Alive(EngineBase value) => true;
        }

        public sealed class AssetAdapter : RqlTypeAdapter<AssetBase>
        {
            public override LiteralKind? LiteralKindOf(Type type) => LiteralKind.Value;
        }

        public sealed class SubObjectAdapter : RqlTypeAdapter<SubObject>
        {
            public override LiteralKind? LiteralKindOf(Type type) => LiteralKind.Instanced;

            public override object? CreateForLiteral(Type type, object? outer)
            {
                SubObject.Built++;
                var o = (SubObject)Activator.CreateInstance(type)!;
                o.Outer = outer as SceneNode;
                o.Parent = outer as SubObject;
                return o;
            }
        }

        private static (RqlEngine engine, SceneNode node) Scene(bool adapters = true)
        {
            var node = new SceneNode { Name = "Player" };
            var e = new RqlEngine().AddSource("scene", () => new List<SceneNode> { node }, null, typeof(List<SceneNode>));
            e.AddAdapter(new EngineAdapter());
            if (adapters) e.AddAdapter(new AssetAdapter()).AddAdapter(new SubObjectAdapter());
            e.Mutations.Enabled = true;
            return (e, node);
        }

        private static string Error(QueryResult r) => r.Ok ? "(ok)" : r.Diagnostics[0].Message;

        // ------------------------------------------------------------ #1 value-like engine types

        [Fact]
        public void ValueKindBuildsAnIdentifiedTypeFromALiteral()
        {
            var (e, node) = Scene();
            var r = e.Execute("scene | set Mesh = {$type: \"BoxMesh\", Size: 2}", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(2, ((BoxMesh)node.Mesh!).Size);
        }

        [Fact]
        public void WithoutAKindAnIdentifiedTypeIsAnEngineObject()
        {
            var (e, node) = Scene(adapters: false);
            var r = e.Execute("scene | set Mesh = {$type: \"BoxMesh\", Size: 2}", options: Mutate);
            Assert.Equal("coercion-failed", r.Diagnostics[0].Code);
            Assert.Contains("BoxMesh is an engine object", Error(r));
            Assert.Null(node.Mesh);
        }

        [Fact]
        public void HostHooksDecideTheKind()
        {
            var (e, node) = Scene(adapters: false);
            e.Mutations.LiteralKindOf = t => typeof(AssetBase).IsAssignableFrom(t) ? LiteralKind.Value : (LiteralKind?)null;
            Assert.True(e.Execute("scene | set Mesh = {$type: \"BoxMesh\"}", options: Mutate).Ok);
            // The older boolean hook still applies afterwards: false makes a value-kind type an engine object.
            var (e2, _) = Scene();
            e2.Mutations.CanConstructFromLiteral = t => t == typeof(BoxMesh) ? false : (bool?)null;
            Assert.Contains("engine object", Error(e2.Execute("scene | set Mesh = {$type: \"BoxMesh\"}", options: Mutate)));
            // … and true makes an engine type a value.
            var (e3, n3) = Scene(adapters: false);
            e3.Mutations.CanConstructFromLiteral = t => t == typeof(BoxMesh) ? true : (bool?)null;
            Assert.True(e3.Execute("scene | set Mesh = {$type: \"BoxMesh\", Size: 3}", options: Mutate).Ok);
            Assert.Equal(3, ((BoxMesh)n3.Mesh!).Size);
        }

        [Fact]
        public void HostFactoryBuildsValues()
        {
            var (e, node) = Scene();
            object? seenOuter = null;
            e.Mutations.CreateForLiteral = (t, outer) => { seenOuter = outer; return t == typeof(BoxMesh) ? new BoxMesh { Size = 7 } : null; };
            Assert.True(e.Execute("scene | set Mesh = {$type: \"BoxMesh\"}", options: Mutate).Ok);
            Assert.Equal(7, ((BoxMesh)node.Mesh!).Size); // members not named keep what the factory built
            Assert.Same(node, seenOuter);
        }

        // ------------------------------------------------------------ #21 instanced subobjects

        [Fact]
        public void InstancedLiteralIsBuiltForTheOwnerOfTheWrittenMember()
        {
            var (e, node) = Scene();
            var r = e.Execute("scene | set Triggers = [{$type: \"HoldTrigger\", Threshold: 0.5}, {Threshold: 1}]", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(2, node.Triggers.Count);
            Assert.IsType<HoldTrigger>(node.Triggers[0]);
            Assert.All(node.Triggers, t => Assert.Same(node, t.Outer));
            Assert.Equal(0.5, node.Triggers[0].Threshold);
        }

        [Fact]
        public void NestedInstancedLiteralIsOwnedByTheObjectTheEnclosingLiteralBuilds()
        {
            var (e, node) = Scene();
            Assert.True(e.Execute("scene | set Triggers = [{Threshold: 1, Inner: {$type: \"HoldTrigger\", Threshold: 2}}]", options: Mutate).Ok);
            var outer = node.Triggers[0];
            Assert.Same(outer, outer.Inner!.Parent);
            Assert.Null(outer.Inner.Outer);
            // A struct literal passes its own outer on.
            Assert.True(e.Execute("scene | set Where = {X: 1, Trigger: {Threshold: 3}}", options: Mutate).Ok);
            Assert.Same(node, node.Where.Trigger!.Outer);
        }

        [Fact]
        public void InstancedCallArgumentIsBuiltForTheTarget()
        {
            var (e, node) = Scene();
            var r = e.Execute("scene | call AddTrigger({$type: \"HoldTrigger\", Threshold: 0.5})", options: Mutate);
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal("Player", r.Rows![0][2]!.GetValue<string>());
            // A source-qualified call builds for the receiver.
            var e2 = e.AddSource("player", () => node);
            var q = e2.Execute("range(0, 2) | call player.AddTrigger({Threshold: $})", options: Mutate);
            Assert.True(q.Ok, q.ToJson(includeStats: false));
            Assert.Equal(3, node.Triggers.Count);
            Assert.All(node.Triggers, t => Assert.Same(node, t.Outer));
        }

        [Fact]
        public void InstancedLiteralWithoutAnOwnerIsRefused()
        {
            var (e, _) = Scene();
            Assert.False(e.TryEvaluateValue("{Threshold: 1}", typeof(SubObject), out _, out var error));
            Assert.Contains("SubObject is an instanced subobject", error);
        }

        [Fact]
        public void OverloadProbeNeverRunsAFactory()
        {
            var (e, node) = Scene();
            SubObject.Built = 0;
            SceneNode.Constructed = 0;
            // Attach(SceneNode) is the only overload: the probe refuses the engine object, nothing is built.
            Assert.Contains("SceneNode is an engine object", Error(e.Execute("scene | call Attach({Name: \"x\"})", options: Mutate)));
            Assert.Equal(0, SceneNode.Constructed);
            // A dry run builds the planned value (planning) but writes nothing.
            Assert.True(e.Execute("scene | call AddTrigger({Threshold: 2})", options: DryRun).Ok);
            Assert.Equal(1, SubObject.Built);
            Assert.Empty(node.Triggers);
        }

        // ------------------------------------------------------------ #5 abstract engine objects, refusal advice

        public sealed class Holder
        {
            public AbstractScene? Part { get; set; }
            public AssetBase? Asset { get; set; }
        }

        [Fact]
        public void AbstractEngineObjectGetsTheEngineObjectRefusalFirst()
        {
            var h = new Holder();
            var e = new RqlEngine().AddSource("h", () => new List<Holder> { h });
            e.AddAdapter(new EngineAdapter()).AddAdapter(new AssetAdapter());
            e.Mutations.Enabled = true;
            var r = e.Execute("h | set Part = {Name: \"x\"}", options: Mutate);
            Assert.Contains("AbstractScene is an engine object and cannot be built from an object literal; pass an existing one", Error(r));
            // A value-kind abstract type still asks for $type.
            Assert.Contains("is abstract; add \"$type\"", Error(e.Execute("h | set Asset = {Name: \"x\"}", options: Mutate)));
            // The host replaces the advice.
            e.Mutations.LiteralRefusalHint = t => "use scene.find(…) or an asset path";
            Assert.Contains("cannot be built from an object literal; use scene.find(…) or an asset path", Error(e.Execute("h | set Part = {Name: \"x\"}", options: Mutate)));
        }

        // ------------------------------------------------------------ #15 known names never evaluate sources

        public sealed class Group { public string Label { get; set; } = ""; }

        [Fact]
        public void WarningsNeverEvaluateASourceTheQueryDoesNotName()
        {
            int walks = 0;
            var e = new RqlEngine()
                .AddSource("items", () => new List<Group> { new Group { Label = "a" } })
                .AddSource("groups", () => { walks++; return new List<SceneNode>(); });
            e.Mutations.Enabled = true;
            e.Execute("items | where Labl == \"x\"");                       // unknown-member warning
            e.Execute("items | where $ is SceneNode | count");               // no-match warning (holders hint)
            e.Execute("items | where false and Nmae == 1 | count");          // never-evaluated name check
            e.Execute("items | where Labl == \"x\" | set Label = \"y\"", options: Mutate); // empty-through-a-typo check
            Assert.Equal(0, walks);
            // The query's own source counts even when the stream never pulled it.
            int own = 0;
            var e2 = new RqlEngine().AddSource("items", () => { own++; return new List<Group> { new Group() }; });
            var r = e2.Execute("items | sort Label | first 0 | count");
            Assert.True(r.Ok);
            Assert.DoesNotContain(r.Diagnostics, d => d.Code == "unknown-member");
            Assert.Equal(1, own);
        }

        // ------------------------------------------------------------ #13 sort keys that fail, #12 sparse keys

        public sealed class Thing
        {
            public string Name { get; set; } = "";
            public double Mass { get; set; }
            public double Volume { get; set; }
        }

        public sealed class Lamp
        {
            public string Name { get; set; } = "";
            public string type { get; set; } = "Point";
        }

        [Fact]
        public void FailingSortKeyWarns()
        {
            var e = new RqlEngine().AddSource("things", () => new List<Thing> { new Thing { Name = "a", Mass = 1 }, new Thing { Name = "b", Mass = 2, Volume = 1 } });
            var r = e.Execute("things | sort (Mass / Volume) desc | select Name");
            Assert.True(r.Ok);
            var w = Assert.Single(r.Diagnostics);
            Assert.Equal("div-by-zero", w.Code);
            Assert.Contains("sort key 1 failed for 1 of 2 item(s)", w.Message);
            Assert.Contains("they sort last", w.Message);
            Assert.Equal("b", r.Rows![0][0]!.GetValue<string>());
            // TopN (sort + first) warns the same way.
            Assert.Contains(e.Execute("things | sort (Mass / Volume) | first 1").Diagnostics, d => d.Code == "div-by-zero");
            Assert.Empty(e.Execute("things | sort Mass").Diagnostics);
        }

        [Fact]
        public void SparseGroupByMemberWarnsAndOffersTheMetaMember()
        {
            var items = new List<object>();
            for (int i = 0; i < 9; i++) items.Add(new Thing { Name = "t" + i });
            items.Add(new Lamp { Name = "l" });
            var e = new RqlEngine().AddSource("items", () => items);
            var r = e.Execute("items | group-by type");
            var w = Assert.Single(r.Diagnostics);
            Assert.Equal("unknown-member", w.Code);
            Assert.Equal("member 'type' exists on 1 of 10 item(s); the other 9 group as null", w.Message);
            Assert.Equal("did you mean $type (the meta member)?", w.Hint);
            var d = e.Execute("items | distinct Name2 | count").Diagnostics.Single();
            Assert.Contains("no member 'Name2'", d.Message); // missing everywhere: the ordinary warning
            var named = e.Execute("items | group-by k=type, Name").Diagnostics.Single();
            Assert.Contains("exists on 1 of 10", named.Message);
            var distinct = e.Execute("items | distinct type | count").Diagnostics.Single();
            Assert.Equal("member 'type' exists on 1 of 10 item(s); the other 9 are kept as one", distinct.Message);
            // Present on most items: no warning.
            Assert.Empty(e.Execute("items | group-by Volume").Diagnostics);
        }
    }
}
