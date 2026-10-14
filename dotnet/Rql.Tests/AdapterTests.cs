using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Rql.Execution;
using Xunit;

namespace Rql.Tests
{
    // A Unity-shaped model: engine objects with instance ids and a destroyed flag, private serialized fields,
    // component back-pointers and a getter with side effects (Renderer.material instantiates a copy in Unity).
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeFieldLike : Attribute { }

    public interface ITagged { string Tag { get; } }

    public abstract class EngineObject
    {
        private static int _next;
        public int InstanceId { get; } = ++_next;
        public string Name { get; set; } = "";
        public bool Destroyed;
    }

    public sealed class GameObjectLike : EngineObject
    {
        public List<ComponentLike> Components { get; } = new List<ComponentLike>();
    }

    public class ComponentLike : EngineObject, ITagged
    {
        public GameObjectLike? GameObject { get; set; }
        public string Tag => "untagged";
        [SerializeFieldLike] private int _secret = 7;
        private int _notSerialized = 1;
        public int Unrelated() => _secret + _notSerialized;
    }

    public sealed class RendererLike : ComponentLike
    {
        public int MaterialInstantiations;
        public MaterialLike Material { get { MaterialInstantiations++; return new MaterialLike(); } }
        public MaterialLike SharedMaterial { get; } = new MaterialLike();
    }

    public sealed class MaterialLike
    {
        public Dictionary<string, double> Floats { get; } = new Dictionary<string, double> { ["_Glossiness"] = 0.5 };
    }

    public sealed class Scene
    {
        public List<GameObjectLike> Objects { get; } = new List<GameObjectLike>();
    }

    public class AdapterTests
    {
        private sealed class EngineObjectAdapter : RqlTypeAdapter<EngineObject>
        {
            public override string Name => "engine-object";
            protected override string? Id(EngineObject o) => o.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            protected override bool? Alive(EngineObject o) => !o.Destroyed;
            protected override void Members(MemberTable<EngineObject> m) => m.Hide("Destroyed");
        }

        private sealed class ComponentAdapter : RqlTypeAdapter<ComponentLike>
        {
            protected override void Members(MemberTable<ComponentLike> m) =>
                m.IncludeFields(f => f.IsDefined(typeof(SerializeFieldLike), false)).Reference("GameObject");
        }

        private sealed class RendererAdapter : RqlTypeAdapter<RendererLike>
        {
            protected override void Members(MemberTable<RendererLike> m) => m.ExplicitOnly("Material").Hide("MaterialInstantiations");
        }

        private sealed class MaterialAdapter : RqlTypeAdapter<MaterialLike>
        {
            protected override void Members(MemberTable<MaterialLike> m) =>
                m.Add("glossiness", x => x.Floats["_Glossiness"], (x, v) => x.Floats["_Glossiness"] = v,
                    validate: (x, v) => v < 0 || v > 1 ? "glossiness must be within [0, 1]" : null);
        }

        private sealed class Named : RqlTypeAdapter
        {
            private readonly string _name;
            public Named(Type target, string name, int priority = 0) : base(target, priority) { _name = name; }
            public override string Name => _name;
        }

        private static (RqlEngine engine, Scene scene, RendererLike renderer) Unity(AccessorMode mode = AccessorMode.Compiled)
        {
            var scene = new Scene();
            var go = new GameObjectLike { Name = "Player" };
            var renderer = new RendererLike { Name = "Renderer", GameObject = go };
            go.Components.Add(new ComponentLike { Name = "Transform", GameObject = go });
            go.Components.Add(renderer);
            scene.Objects.Add(go);
            var e = new RqlEngine(mode);
            e.AddSource("scene", () => scene);
            e.Mutations.Enabled = true;
            e.AddAdapter(new EngineObjectAdapter()).AddAdapter(new ComponentAdapter()).AddAdapter(new RendererAdapter()).AddAdapter(new MaterialAdapter());
            return (e, scene, renderer);
        }

        private static object? Cell(QueryResult r, int row = 0, int col = 0)
        {
            Assert.True(r.Ok, string.Join("; ", r.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            var n = r.Rows![row][col];
            return n?.ToJsonString().Trim('"');
        }

        [Fact]
        public void ResolutionOrderIsDeterministic()
        {
            var e = new RqlEngine();
            e.AddAdapter(new Named(typeof(object), "object"));
            e.AddAdapter(new Named(typeof(IEnumerable<>), "open-generic-interface"));
            e.AddAdapter(new Named(typeof(List<>), "open-generic"));
            e.AddAdapter(new Named(typeof(List<int>), "closed"));
            e.AddAdapter(new Named(typeof(ITagged), "interface"));
            e.AddAdapter(new Named(typeof(EngineObject), "base-2"));
            e.AddAdapter(new Named(typeof(ComponentLike), "base-1-low", priority: 0));
            e.AddAdapter(new Named(typeof(ComponentLike), "base-1-high", priority: 5));
            e.AddAdapter(new Named(typeof(RendererLike), "exact-a"));
            e.AddAdapter(new Named(typeof(RendererLike), "exact-b"));
            var order = e.Types.Explain(typeof(RendererLike)).Select(s => s.Split(' ')[0]).ToArray();
            Assert.Equal(new[] { "exact-a", "exact-b", "base-1-high", "base-1-low", "base-2", "object", "interface" }, order);
            Assert.Equal(new[] { "closed", "open-generic", "object", "open-generic-interface" }, e.Types.Explain(typeof(List<int>)).Select(s => s.Split(' ')[0]).ToArray());
        }

        [Theory]
        [InlineData(AccessorMode.Compiled)]
        [InlineData(AccessorMode.Reflection)]
        public void StableIdsLivenessAndPrivateSerializedFields(AccessorMode mode)
        {
            var (e, scene, renderer) = Unity(mode);
            var go = scene.Objects[0];
            var r = e.Execute("scene.Objects | select $ref Name");
            Assert.Equal("GameObjectLike#" + go.InstanceId, Cell(r));

            var secret = e.Execute("scene.Objects | get Components | select Name _secret");
            Assert.Equal("7", Cell(secret, 0, 1));
            var hidden = e.Execute("scene.Objects | get Components | select _notSerialized Destroyed");
            Assert.Contains(hidden.Diagnostics, d => d.Code == "unknown-member");

            var byRef = e.Execute($"ref(\"RendererLike#{renderer.InstanceId}\") | select Name");
            Assert.Equal("Renderer", Cell(byRef));
            renderer.Destroyed = true;
            var stale = e.Execute($"ref(\"RendererLike#{renderer.InstanceId}\") | select Name");
            Assert.Contains(stale.Diagnostics, d => d.Code == "stale-ref");
        }

        [Fact]
        public void ExplicitOnlyGettersRunOnlyWhenNamed()
        {
            var (e, _, renderer) = Unity();
            Assert.True(e.Execute("scene | recurse | count").Ok);
            Assert.True(e.Execute("scene.Objects | get Components | where $ is RendererLike | select $").Ok);
            Assert.True(e.Execute("scene.Objects | get Components | first 2 | describe").Ok);
            Assert.Equal(0, renderer.MaterialInstantiations);

            var named = e.Execute("scene.Objects | get Components | where $ is RendererLike | select g=Material.glossiness");
            Assert.Equal("0.5", Cell(named));
            Assert.Equal(1, renderer.MaterialInstantiations);
        }

        [Fact]
        public void ReferenceMembersAreNotFollowedByGenericRecurse()
        {
            var (e, _, _) = Unity();
            var r = e.Execute("scene | recurse | where $ is GameObjectLike | count");
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.Equal(1L, r.Value!.GetValue<long>());
        }

        [Fact]
        public void VirtualSetterValidatesBeforeWriting()
        {
            var (e, _, renderer) = Unity();
            var bad = e.Execute("scene.Objects | get Components | where $ is RendererLike | set SharedMaterial.glossiness = 2", options: new QueryOptions { AllowMutation = true });
            Assert.False(bad.Ok);
            Assert.Equal("coercion-failed", bad.Diagnostics[0].Code);
            Assert.Contains("within [0, 1]", bad.Diagnostics[0].Message);
            Assert.Equal(0.5, renderer.SharedMaterial.Floats["_Glossiness"]);

            var ok = e.Execute("scene.Objects | get Components | where $ is RendererLike | set SharedMaterial.glossiness = 0.25", options: new QueryOptions { AllowMutation = true });
            Assert.True(ok.Ok, string.Join("; ", ok.Diagnostics.Select(d => d.Message)));
            Assert.Equal(0.25, renderer.SharedMaterial.Floats["_Glossiness"]);
        }

        [Fact]
        public void AdaptersAreScopedToTheirEngine()
        {
            var (adapted, scene, _) = Unity();
            var plain = new RqlEngine();
            plain.AddSource("scene", () => scene);
            Assert.True(adapted.Execute("scene.Objects | select Name").Ok);
            var r = plain.Execute("scene.Objects | select $ref Destroyed");
            Assert.StartsWith("GameObjectLike@", (string)Cell(r)!);
            Assert.Equal("false", Cell(r, 0, 1));
            Assert.Same(TypeModel.Shared, plain.Types);
            Assert.NotSame(TypeModel.Shared, adapted.Types);
        }

        [Fact]
        public void MisconfiguredAdaptersFailLoudly()
        {
            var e = new RqlEngine();
            e.AddSource("scene", () => new Scene());
            Assert.Empty(TypeModel.Shared.Explain(typeof(Scene)));
            e.AddAdapter(new Typo());
            var r = e.Execute("scene | select Objects");
            Assert.False(r.Ok);
            Assert.Equal("internal-error", r.Diagnostics[0].Code);
            Assert.Contains("no member 'Objectz'", r.Diagnostics[0].Message);

            var s = new RqlEngine();
            s.AddSource("v", () => new KeyValuePair<int, int>(1, 2));
            s.AddAdapter(new StructSetter());
            Assert.Contains("value type", s.Execute("v | select Key").Diagnostics[0].Message);
        }

        [Fact]
        public void CapabilitiesAreDetectedSoUnusedHooksCostNothing()
        {
            var e = new RqlEngine();
            e.AddAdapter(new EngineObjectAdapter()).AddAdapter(new EncodeOnly());
            var info = e.Types.Info(typeof(RendererLike));
            Assert.Equal(2, info.Adapters.Count);
            Assert.Empty(info.EqualityAdapters);
            Assert.Empty(info.CoerceAdapters);
            Assert.Single(info.EncodeAdapters);
            Assert.Single(info.IdentityAdapters);
            Assert.Single(info.LivenessAdapters);
            Assert.Equal(AdapterCaps.Encode | AdapterCaps.Identity | AdapterCaps.Liveness, e.Types.Caps);
            // Engine-internal values are never adapted, even by an adapter for object.
            e.AddAdapter(new Named(typeof(object), "object"));
            Assert.Empty(e.Types.Info(typeof(RowValue)).Adapters);
        }

        private sealed class EncodeOnly : RqlTypeAdapter<ComponentLike>
        {
            protected override JsonNode? Encode(ComponentLike c, EncodeContext context) => JsonValue.Create("component " + c.Name);
        }

        private sealed class Typo : RqlTypeAdapter<Scene>
        {
            protected override void Members(MemberTable<Scene> m) => m.ReadOnly("Objectz");
        }

        private sealed class StructSetter : RqlTypeAdapter<KeyValuePair<int, int>>
        {
            protected override void Members(MemberTable<KeyValuePair<int, int>> m) => m.Add<int>("k", p => p.Key, (p, v) => { });
        }
    }
}
