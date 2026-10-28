using System.Collections.Generic;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Catalog "bulk": creating many objects in one call (ADR-0008: range() and the source-qualified call). A separate split,
    /// so the dev / holdout / holdout2 numbers stay comparable with earlier runs.
    /// </summary>
    public static partial class Catalog
    {
        public static readonly int[] BulkSeeds = { 41, 42 };

        private static IEnumerable<Template> TemplatesBulk()
        {
            const string S = "bulk";
            yield return new Template
            {
                Id = "bulk-create-row", Category = "create", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    int n = 10 + c.Rng.Next(15);
                    string sceneRef = "Scene#" + s.Id;
                    return T($"In scene {Q(s.Name)}, create {n} new objects named Crate_0 … Crate_{n - 1} (with its CreateObject method), "
                        + "placed in a row along x: Crate_i at x = i * 2, y = 0, z = 0.", FmtState,
                        Expected.State(sb =>
                        {
                            var scene = sb.World.Scenes.First(x => x.Id == s.Id);
                            for (int i = 0; i < n; i++) scene.CreateObject("Crate_" + i, i * 2, 0, 0);
                        }),
                        h => Mutate(h, $"range(0, {n}) | call ref({Q(sceneRef)}).CreateObject(\"Crate_\" + $, $ * 2, 0, 0)", 200));
                },
            };
            yield return new Template
            {
                Id = "bulk-create-grid", Category = "create", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    int cols = 4 + c.Rng.Next(4), rows = 3 + c.Rng.Next(3);
                    int n = cols * rows;
                    string sceneRef = "Scene#" + s.Id;
                    return T($"In scene {Q(s.Name)}, build a grid of {n} objects with its CreateObject method: {cols} per row, "
                        + $"object k (k = 0 … {n - 1}) named Tile_k at x = (k mod {cols}) * 3, y = 0, z = floor(k / {cols}) * 3.", FmtState,
                        Expected.State(sb =>
                        {
                            var scene = sb.World.Scenes.First(x => x.Id == s.Id);
                            for (int k = 0; k < n; k++) scene.CreateObject("Tile_" + k, (k % cols) * 3, 0, (k / cols) * 3);
                        }),
                        h => Mutate(h, $"range(0, {n}) | call ref({Q(sceneRef)}).CreateObject(\"Tile_\" + $, ($ % {cols}) * 3, 0, floor($ / {cols}) * 3)", 200));
                },
            };
            yield return new Template
            {
                Id = "bulk-create-from-list", Category = "create", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    var names = new[] { "Barrel", "Crate", "Lamp", "Bench", "Sign" }.Take(3 + c.Rng.Next(3)).ToList();
                    string sceneRef = "Scene#" + s.Id;
                    string list = "[" + string.Join(", ", names.Select((nm, i) => $"[{Q(nm)}, {i * 5}]")) + "]";
                    return T($"In scene {Q(s.Name)}, create one object per entry of this list of [name, x] pairs with its CreateObject method "
                        + $"(y = 0, z = 10): {list}.", FmtState,
                        Expected.State(sb =>
                        {
                            var scene = sb.World.Scenes.First(x => x.Id == s.Id);
                            for (int i = 0; i < names.Count; i++) scene.CreateObject(names[i], i * 5, 0, 10);
                        }),
                        h => Mutate(h, $"{list} | call ref({Q(sceneRef)}).CreateObject($[0], $[1], 0, 10)", 200));
                },
            };
        }
    }
}
