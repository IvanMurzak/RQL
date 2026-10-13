using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rql.Execution;

namespace Rql.Sample
{
    /// <summary>
    /// Hosts a <see cref="Sandbox"/> behind an <see cref="RqlEngine"/>: registers sources, stable-id
    /// resolution, liveness, mutation policy and the agent tool surface (query / mutate / schema).
    /// </summary>
    public sealed class SampleHost
    {
        private readonly Dictionary<string, Dictionary<string, object>> _index = new Dictionary<string, Dictionary<string, object>>();

        public SampleHost(int seed, WorldSize size, bool allowMutations = false, AccessorMode accessors = AccessorMode.Compiled)
            : this(WorldGenerator.Create(seed, size), allowMutations, accessors)
        {
        }

        public SampleHost(Sandbox sandbox, bool allowMutations = false, AccessorMode accessors = AccessorMode.Compiled)
        {
            Sandbox = sandbox;
            Engine = new RqlEngine(accessors);
            Engine.AddSource("world", () => Sandbox.World, "the game world (scenes → objects → children/components, settings, assets, quests, items)");
            Engine.AddSource("app", () => Sandbox.App, "application data (users, orders, products)");
            Engine.AddSource("objects", () => WorldGenerator.AllObjects(Sandbox.World), "every GameObject in every scene, flattened depth-first (includes Characters: Player, Enemy, Npc)");
            Engine.Mutations.Enabled = allowMutations;
            Engine.IsAlive = IsAlive;
            Engine.ResolveStableRef = Resolve;
            BuildIndex();
        }

        public Sandbox Sandbox { get; }
        public RqlEngine Engine { get; }

        private void BuildIndex()
        {
            void Add(string family, string id, object o)
            {
                if (!_index.TryGetValue(family, out var d)) _index[family] = d = new Dictionary<string, object>(StringComparer.Ordinal);
                d[id] = o;
            }
            string S(int i) => i.ToString(CultureInfo.InvariantCulture);
            var w = Sandbox.World;
            foreach (var s in w.Scenes) Add("scene", S(s.Id), s);
            foreach (var go in WorldGenerator.AllObjects(w))
            {
                Add("go", S(go.Id), go);
                foreach (var c in go.Components) Add("component", S(c.Id), c);
            }
            foreach (var i in w.ItemCatalog) Add("item", S(i.Id), i);
            foreach (var q in w.Quests) Add("quest", S(q.Id), q);
            foreach (var a in w.Assets.Values) Add("asset", a.Id, a);
            foreach (var u in Sandbox.App.Users) Add("user", S(u.Id), u);
            foreach (var o in Sandbox.App.Orders) Add("order", S(o.Id), o);
            foreach (var p in Sandbox.App.Products.Values) Add("product", p.Sku, p);
        }

        private object? Resolve(string type, string id)
        {
            foreach (var family in _index.Values)
            {
                if (family.TryGetValue(id, out var o) && Engine.Types.Info(o.GetType()).IsA(type)) return o;
            }
            return null;
        }

        private static bool IsAlive(object o) => o switch
        {
            GameObject g => !g.Destroyed,
            Component c => c.Owner == null || (!c.Owner.Destroyed && c.Owner.Components.Contains(c)),
            _ => true,
        };

        // ------------------------------------------------------------ tool surface

        public QueryResult Query(string text, Budgets? budgets = null) => Engine.Execute(text, budgets);

        public QueryResult Mutate(string text, bool dryRun, Budgets? budgets = null) =>
            Engine.Execute(text, budgets, new QueryOptions { AllowMutation = true, DryRun = dryRun });

        public string Schema(string? type = null) => AgentFormat.Schema(Engine, type);
    }
}
