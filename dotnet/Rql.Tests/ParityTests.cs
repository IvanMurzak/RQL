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
            Assert.Contains(warned.Diagnostics, d => d.Code == "getter-threw" && d.Message.Contains("dirty failed"));
            Assert.Equal("pink", s.Renderers[0].First.color);
        }

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
        public void AdaptersTargetedByNameAreInertWhenTheTypeIsMissing()
        {
            var bound = new ByName("Rql.Tests.ParityTests+Mat");
            var missing = new ByName("UnityEngine.Collider");
            Assert.True(bound.IsBound);
            Assert.False(missing.IsBound);
            var e = new RqlEngine();
            e.AddAdapter(bound).AddAdapter(missing);
            Assert.Single(e.Types.Explain(typeof(Mat)));
        }

        private sealed class ByName : RqlTypeAdapter { public ByName(string name) : base(name) { } }

        [Fact]
        public void AdaptersAreDiscoveredFromAssembliesInOrder()
        {
            var e = new RqlEngine();
            var errors = e.AddAdaptersFrom(typeof(ParityTests).Assembly);
            Assert.Contains(errors, err => err.Contains(nameof(Throwing)));
            var order = e.Types.Explain(typeof(Discovered)).Select(x => x.Split(' ')[0]).ToArray();
            // Same rank: higher priority first, so the order attribute only decides registration order.
            Assert.Equal(new[] { nameof(SecondDiscovered), nameof(FirstDiscovered) }, order);
        }

        public sealed class Discovered { }

        [RqlAdapterOrder(1)] public sealed class FirstDiscovered : RqlTypeAdapter<Discovered> { public FirstDiscovered() : base(priority: 0) { } }
        [RqlAdapterOrder(2)] public sealed class SecondDiscovered : RqlTypeAdapter<Discovered> { public SecondDiscovered() : base(priority: 1) { } }
        public sealed class Throwing : RqlTypeAdapter<Discovered> { public Throwing() : base() { throw new InvalidOperationException("broken module"); } }
    }
}
