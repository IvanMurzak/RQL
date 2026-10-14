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

        public sealed class Counter
        {
            public int Id { get; set; } = 1;
            public int Value { get; set; }
            public int NameReads;
            public string Name { get { NameReads++; return "counter"; } }
        }

        private sealed class Holder { public List<Counter> Items { get; } = new List<Counter> { new Counter(), new Counter { Id = 2 } }; }

        private sealed class HalfWritingSetter : RqlTypeAdapter<Counter>
        {
            // Changes state, then throws: the failed write itself must be rolled back too.
            protected override void Members(MemberTable<Counter> m) =>
                m.Add<int>("v", c => c.Value, (c, v) => { c.Value = v; if (c.Id == 2) throw new InvalidOperationException("boom"); });
        }

        [Fact]
        public void RollbackAlsoRestoresTheWriteWhoseSetterThrew()
        {
            var h = new Holder();
            var e = new RqlEngine();
            e.AddSource("h", () => h);
            e.Mutations.Enabled = true;
            e.AddAdapter(new HalfWritingSetter());
            var r = e.Execute("h.Items | set v = 5", options: new QueryOptions { AllowMutation = true });
            Assert.False(r.Ok);
            Assert.Contains("rolled back", r.Diagnostics[0].Message);
            Assert.All(h.Items, c => Assert.Equal(0, c.Value));
        }

        private sealed class ExplicitName : RqlTypeAdapter<Counter>
        {
            protected override void Members(MemberTable<Counter> m) => m.ExplicitOnly("Name");
        }

        [Fact]
        public void ExplicitOnlyMembersAreNeverIdOrDisplay()
        {
            var h = new Holder();
            var e = new RqlEngine();
            e.AddSource("h", () => h);
            e.AddAdapter(new ExplicitName());
            var r = e.Execute("h.Items | select $");
            Assert.True(r.Ok);
            Assert.Equal(0, h.Items[0].NameReads);
            Assert.Equal("counter", Cell(e.Execute("h.Items | select Name")));
        }

        private sealed class HideTypo : RqlTypeAdapter<Counter>
        {
            protected override void Members(MemberTable<Counter> m) => m.Hide("Nmae");
        }

        [Fact]
        public void HidingAMisspeltMemberFailsLoudly()
        {
            var e = new RqlEngine();
            e.AddSource("h", () => new Holder());
            e.AddAdapter(new HideTypo());
            var r = e.Execute("h.Items | select Name");
            Assert.Equal("internal-error", r.Diagnostics[0].Code);
            Assert.Contains("'Nmae'", r.Diagnostics[0].Message);
        }

        public enum Urgency { Low, Medium, High }

        public sealed class Task { public string Name { get; set; } = ""; public Urgency P { get; set; } }

        private sealed class UrgencyOrder : RqlTypeAdapter<Urgency>
        {
            // Host order differs from the numeric enum order: Medium is the most urgent.
            private static int Rank(Urgency p) => p == Urgency.Medium ? 2 : p == Urgency.High ? 1 : 0;
            protected override int? Order(Urgency p, object? other) => other is Urgency o ? Rank(p).CompareTo(Rank(o)) : (int?)null;
        }

        [Fact]
        public void MinMaxUseOrderingAdaptersLikeSort()
        {
            var tasks = new List<Task> { new Task { Name = "a", P = Urgency.High }, new Task { Name = "b", P = Urgency.Medium }, new Task { Name = "c", P = Urgency.Low } };
            var e = new RqlEngine();
            e.AddSource("t", () => tasks);
            e.AddAdapter(new UrgencyOrder());
            Assert.Equal("b", Cell(e.Execute("t | sort P desc | first 1 | select Name")));
            var max = e.Execute("t | max P");
            Assert.True(max.Ok, max.ToJson(includeStats: false));
            Assert.Equal("\"Medium\"", max.Value!.ToJsonString());
        }

        private sealed class ByIdDesc : RqlTypeAdapter<Counter>
        {
            protected override int? Order(Counter c, object? other) => other is Counter o ? o.Id.CompareTo(c.Id) : (int?)null;
        }

        [Fact]
        public void MinMaxAcceptAdapterOrderedObjects()
        {
            var e = new RqlEngine();
            e.AddSource("h", () => new Holder());
            e.AddAdapter(new ByIdDesc());
            var max = e.Execute("h.Items | max $");
            Assert.True(max.Ok, max.ToJson(includeStats: false));
            Assert.Equal("Counter#1", max.Value!["$ref"]!.GetValue<string>());
        }

        // Unity-shaped engine types: no RQL attributes possible, state behind methods.
        public sealed class GameObjectApi
        {
            public int Id { get; set; } = 1;
            public bool activeSelf { get; private set; } = true;
            public void SetActive(bool value) => activeSelf = value;
            public void Destroy() { }
        }

        public sealed class ShaderMaterial
        {
            private readonly Dictionary<string, float> _floats = new Dictionary<string, float> { ["_Glossiness"] = 0.5f, ["_Metallic"] = 0f };
            public int Id { get; set; } = 7;
            public IEnumerable<string> FloatNames => _floats.Keys;
            public float GetFloat(string name) => _floats[name];
            public void SetFloat(string name, float value) { if (name == "_Metallic" && value > 0.9f) throw new InvalidOperationException("driver rejects"); _floats[name] = value; }
            public bool HasFloat(string name) => _floats.ContainsKey(name);
        }

        private sealed class UnityApi { public List<GameObjectApi> Objects { get; } = new List<GameObjectApi> { new GameObjectApi() }; public List<ShaderMaterial> Materials { get; } = new List<ShaderMaterial> { new ShaderMaterial() }; }

        private sealed class GameObjectApiAdapter : RqlTypeAdapter<GameObjectApi>
        {
            protected override void Members(MemberTable<GameObjectApi> m) => m.Expose(nameof(GameObjectApi.SetActive), "activate / deactivate");
        }

        private sealed class MaterialFloats : RqlTypeAdapter<ShaderMaterial>
        {
            protected override void Members(MemberTable<ShaderMaterial> m) =>
                m.Hide(nameof(ShaderMaterial.FloatNames))
                 .Map<float>("floats", x => x.FloatNames, (x, k) => x.GetFloat(k), (x, k, v) => x.SetFloat(k, v),
                     validate: (x, k, v) => !x.HasFloat(k) ? $"shader has no float '{k}'" : v < 0 || v > 1 ? "must be within [0, 1]" : null);
        }

        private static RqlEngine UnityApiEngine(UnityApi api)
        {
            var e = new RqlEngine();
            e.AddSource("u", () => api);
            e.Mutations.Enabled = true;
            e.AddAdapter(new GameObjectApiAdapter()).AddAdapter(new MaterialFloats());
            return e;
        }

        [Fact]
        public void ExposedEngineMethodsAreCallableWithoutAttributes()
        {
            var api = new UnityApi();
            var e = UnityApiEngine(api);
            var r = e.Execute("u.Objects | call SetActive(false)", options: new QueryOptions { AllowMutation = true });
            Assert.True(r.Ok, r.ToJson(includeStats: false));
            Assert.False(api.Objects[0].activeSelf);
            var hidden = e.Execute("u.Objects | call Destroy()", options: new QueryOptions { AllowMutation = true });
            Assert.Equal("permission-denied", hidden.Diagnostics[0].Code);
            var schema = e.Execute("u.Objects | describe");
            Assert.Contains("SetActive(bool value) — activate / deactivate", schema.ToJson(includeStats: false));
        }

        [Fact]
        public void VirtualMapsReadWriteValidateAndRollBack()
        {
            var api = new UnityApi();
            var e = UnityApiEngine(api);
            var mat = api.Materials[0];
            Assert.Equal("0.5", Cell(e.Execute("u.Materials | select g=floats[\"_Glossiness\"]")));

            var ok = e.Execute("u.Materials | set floats[\"_Glossiness\"] = 0.25", options: new QueryOptions { AllowMutation = true });
            Assert.True(ok.Ok, ok.ToJson(includeStats: false));
            Assert.Equal(0.25f, mat.GetFloat("_Glossiness"));

            var invalid = e.Execute("u.Materials | set floats[\"_Glossiness\"] = 3", options: new QueryOptions { AllowMutation = true });
            Assert.Equal("coercion-failed", invalid.Diagnostics[0].Code);
            Assert.Contains("within [0, 1]", invalid.Diagnostics[0].Message);
            var unknown = e.Execute("u.Materials | set floats[\"_Nope\"] = 0.1", options: new QueryOptions { AllowMutation = true });
            Assert.Contains("no entry '_Nope'", unknown.Diagnostics[0].Message); // virtual maps only take existing keys

            // The second write throws inside the engine: the first one is rolled back.
            var failed = e.Execute("u.Materials | set floats[\"_Glossiness\"] = 0.75, floats[\"_Metallic\"] = 0.95", options: new QueryOptions { AllowMutation = true });
            Assert.False(failed.Ok);
            Assert.Equal(0.25f, mat.GetFloat("_Glossiness"));
            Assert.Equal(0f, mat.GetFloat("_Metallic"));
        }

        // Unity's Transform is IEnumerable (its children) but must be queried as an object.
        public class TransformLike : System.Collections.IEnumerable
        {
            public double x { get; set; } = 1.5;
            public List<TransformLike> Kids { get; } = new List<TransformLike>();
            public System.Collections.IEnumerator GetEnumerator() => Kids.GetEnumerator();
        }

        [Fact]
        public void EnumerableTypesCanBeTreatedAsObjects()
        {
            TypeModel.TreatAsObject(typeof(TransformLike));
            var e = new RqlEngine();
            e.AddSource("t", () => new List<TransformLike> { new TransformLike() });
            var r = e.Execute("t | select x");
            Assert.Equal("1.5", Cell(r));
            Assert.True(TypeModel.Get(typeof(TransformLike)).IsObject);
        }

        [Fact]
        public void HostFunctionsCanStartAQuery()
        {
            var e = new RqlEngine();
            e.AddSource("s", () => new Scene());
            e.AddFunction("host.items", args => new List<object?> { "a" + args[0], "b" + args[0] });
            var r = e.Execute("host.items(\"1\") | where $ starts-with \"b\" | select v=$");
            Assert.Equal("b1", Cell(r));
            var unknown = e.Execute("nope.items(1) | count");
            Assert.Equal("unknown-source", unknown.Diagnostics[0].Code);
            Assert.Contains("functions: host.items", unknown.Diagnostics[0].Hint);
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
