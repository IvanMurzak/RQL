using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Versioned task catalog (v1). Each template is instantiated per seed; parameters are drawn from the
    /// generated world with a template-specific RNG, the oracle computes the expected result with plain
    /// LINQ over the live sandbox (hidden from the model), and <c>Reference</c> is a scripted RQL solution
    /// used to validate solvability through the agent tool surface.
    /// </summary>
    public static partial class Catalog
    {
        public const string Version = "v1";

        public static readonly int[] DevSeeds = { 11, 12 };
        public static readonly int[] HoldoutSeeds = { 21, 22 };

        private sealed class Template
        {
            public string Id = "";
            public string Category = "";
            public string Split = "dev";
            public WorldSize Size = WorldSize.Medium;
            public bool Mutation;
            public Func<Ctx, TaskInstance?> Make = null!;
        }

        private sealed class Ctx
        {
            public Ctx(Sandbox sb, Rng rng) { Sb = sb; Rng = rng; }
            public Sandbox Sb { get; }
            public Rng Rng { get; }
            public World W => Sb.World;
            public App A => Sb.App;
            public IEnumerable<GameObject> All => WorldGenerator.AllObjects(W);
            public IEnumerable<GameObject> In(Scene s) => s.Objects.SelectMany(WorldGenerator.Descendants);
            public Scene PickScene(Func<Scene, bool>? ok = null)
            {
                var list = W.Scenes.Where(s => ok == null || ok(s)).ToList();
                return list.Count == 0 ? W.Scenes[0] : Rng.Pick(list);
            }
        }

        // ------------------------------------------------------------ answer formats

        private const string FmtRefs = "Answer with a comma-separated list of the $ref values (e.g. Enemy#12, Enemy#40).";
        private const string FmtNumber = "Answer with just the number.";
        private const string FmtText = "Answer with just the text.";
        private const string FmtBool = "Answer true or false.";
        private const string FmtState = "Make the change in the live world, then call submit_answer with a short summary.";
        private static string FmtNames(string what) => $"Answer with a JSON array of {what}.";
        private static string FmtObj(params string[] keys) => "Answer with a JSON object with keys " + string.Join(", ", keys.Select(k => "\"" + k + "\"")) + ".";

        private static string Q(string s) => "\"" + s + "\"";
        private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        private static string RefOf(object o) => o switch
        {
            GameObject g => g.GetType().Name + "#" + g.Id,
            Item i => "Item#" + i.Id,
            Quest q => "Quest#" + q.Id,
            User u => "User#" + u.Id,
            _ => throw new ArgumentException(o.GetType().Name),
        };

        private static TaskInstance T(string goal, string format, Expected e, Func<SampleHost, string>? reference = null) =>
            new TaskInstance { Goal = goal, AnswerFormat = format, Expected = e, Reference = reference };

        // reference-solution helpers (run RQL through the same host the agent would use)
        private static string Col0(SampleHost h, string rql) => string.Join(", ", Rows(h, rql).Select(r => r[0]));
        private static string Json(SampleHost h, string rql) => "[" + string.Join(",", Rows(h, rql).Select(r => System.Text.Json.JsonSerializer.Serialize(r[0]))) + "]";
        private static List<string?[]> Rows(SampleHost h, string rql)
        {
            var r = h.Query(rql, new Rql.Budgets { MaxRows = 10000, MaxOutputBytes = 10_000_000 });
            if (!r.Ok) throw new InvalidOperationException("reference query failed: " + rql + " → " + string.Join("; ", r.Diagnostics));
            if (r.Rows != null) return r.Rows.Select(row => row.Select(c => c == null ? null : c is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : c.ToJsonString()).ToArray()).ToList();
            return r.Items!.Select(i => new[] { i?.ToJsonString() }).ToList();
        }
        private static string Scalar(SampleHost h, string rql)
        {
            var r = h.Query(rql);
            if (!r.Ok) throw new InvalidOperationException("reference query failed: " + rql + " → " + string.Join("; ", r.Diagnostics));
            return r.Value?.ToJsonString() ?? "null";
        }
        private static string Mutate(SampleHost h, string rql, int maxTargets = 20)
        {
            var r = h.Mutate(rql, dryRun: false, new Rql.Budgets { MaxMutationTargets = maxTargets });
            if (!r.Ok) throw new InvalidOperationException("reference mutation failed: " + rql + " → " + string.Join("; ", r.Diagnostics));
            return "done";
        }

        private static GameObject ById(Sandbox sb, int id) => WorldGenerator.AllObjects(sb.World).First(g => g.Id == id);

        // ------------------------------------------------------------ templates

        private static IEnumerable<Template> Templates()
        {
            // ======================= FIND
            yield return new Template
            {
                Id = "find-disabled-lights", Category = "find",
                Make = c =>
                {
                    var s = c.PickScene();
                    var exp = c.In(s).Where(g => g.Components.OfType<LightComponent>().Any(l => !l.Enabled)).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T($"In scene {Q(s.Name)}, find every GameObject that has a LightComponent whose Enabled flag is false.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and Components.any($ is LightComponent and Enabled == false) | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-boss-enemies", Category = "find",
                Make = c =>
                {
                    var exp = c.All.OfType<Enemy>().Where(e => e.Kind == EnemyKind.Boss).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T("Find all enemies of kind Boss anywhere in the world.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, "objects | where $ is Enemy and Kind == \"Boss\" | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-labeled-props", Category = "find", Split = "holdout",
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).Any(g => g.Labels.Contains("explosive")));
                    var exp = c.In(s).Where(g => g.Labels.Contains("explosive")).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T($"Which objects in scene {Q(s.Name)} carry the label \"explosive\"?", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and Labels contains \"explosive\" | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-by-material", Category = "find",
                Make = c =>
                {
                    var s = c.PickScene();
                    var mats = c.In(s).SelectMany(g => g.Components.OfType<RendererComponent>()).Where(r => r.Material != null).Select(r => r.Material!.Id).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                    var counts = mats.Select(m => (m, n: c.In(s).Count(g => g.Components.OfType<RendererComponent>().Any(r => r.Material?.Id == m)))).Where(t => t.n >= 2 && t.n <= 25).ToList();
                    if (counts.Count == 0) return null;
                    var mat = c.Rng.Pick(counts).m;
                    var exp = c.In(s).Where(g => g.Components.OfType<RendererComponent>().Any(r => r.Material?.Id == mat)).Select(RefOf).ToList();
                    return T($"In scene {Q(s.Name)}, find all GameObjects with a RendererComponent that uses the material asset {Q(mat)}.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and Components.any($ is RendererComponent and Material.Id == {Q(mat)}) | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-targeting-player", Category = "find", Split = "holdout",
                Make = c =>
                {
                    var exp = c.All.OfType<Enemy>().Where(e => e.Target == c.W.LocalPlayer).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T("Find every enemy whose current target is the player character.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, "objects | where $ is Enemy and Target is Player | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-locked-chests", Category = "find",
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).Any(IsLockedChest));
                    var exp = c.In(s).Where(IsLockedChest).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T($"Find the chests in scene {Q(s.Name)} that are locked (their \"Chest\" script has the property locked = true).", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and Components.any($ is ScriptComponent and ScriptName == \"Chest\" and Properties.locked == true) | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-merchant-questgivers", Category = "find", Split = "holdout",
                Make = c =>
                {
                    var exp = c.All.OfType<Npc>().Where(n => n.IsMerchant && n.OffersQuest != null).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T("Find all NPCs that are merchants and also offer a quest.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, "objects | where $ is Npc and IsMerchant and OffersQuest is not null | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "find-characters-polymorphic", Category = "find",
                Make = c =>
                {
                    var s = c.PickScene();
                    int lvl = c.Rng.Range(8, 18);
                    var exp = c.In(s).OfType<Character>().Where(ch => ch.Level >= lvl && (ch.Faction == Faction.Guards || ch.Faction == Faction.Neutral || ch.Faction == Faction.Player)).Select(RefOf).ToList();
                    if (exp.Count == 0 || exp.Count > 30) return null;
                    return T($"In scene {Q(s.Name)}, find every character (player, enemy or NPC) with Level >= {lvl} whose faction is Neutral, Guards or Player.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Character and Level >= {lvl} and Faction in [\"Neutral\", \"Guards\", \"Player\"] | select $ref"));
                },
            };

            // ======================= READ
            yield return new Template
            {
                Id = "read-player-vitals", Category = "read",
                Make = c =>
                {
                    var p = c.W.LocalPlayer!;
                    return T("What are the player's Health, MaxHealth, Level and Gold?", FmtObj("Health", "MaxHealth", "Level", "Gold"),
                        Expected.Obj(new Dictionary<string, object?> { ["Health"] = p.Health, ["MaxHealth"] = p.MaxHealth, ["Level"] = p.Level, ["Gold"] = p.Gold }),
                        h =>
                        {
                            var r = Rows(h, "world.LocalPlayer | select Health MaxHealth Level Gold")[0];
                            return $"{{\"Health\":{r[0]},\"MaxHealth\":{r[1]},\"Level\":{r[2]},\"Gold\":{r[3]}}}";
                        });
                },
            };
            yield return new Template
            {
                Id = "read-object-position", Category = "read",
                Make = c =>
                {
                    var cands = c.All.Where(g => g.Name.StartsWith("Chest_") || g.Name.StartsWith("Statue_") || g.Name.StartsWith("Well_")).ToList();
                    var go = c.Rng.Pick(cands);
                    if (c.All.Count(g => g.Name == go.Name) != 1) return null;
                    var pos = go.Transform.Position;
                    return T($"What is the position (x, y, z) of the object named {Q(go.Name)}?", FmtObj("x", "y", "z"),
                        Expected.Obj(new Dictionary<string, object?> { ["x"] = (double)pos.x, ["y"] = (double)pos.y, ["z"] = (double)pos.z }),
                        h =>
                        {
                            var r = Rows(h, $"objects | where Name == {Q(go.Name)} | select Transform.Position.x Transform.Position.y Transform.Position.z")[0];
                            return $"{{\"x\":{r[0]},\"y\":{r[1]},\"z\":{r[2]}}}";
                        });
                },
            };
            yield return new Template
            {
                Id = "read-active-quest-gold", Category = "read", Split = "holdout",
                Make = c =>
                {
                    var total = c.W.LocalPlayer!.ActiveQuests.Sum(q => q.RewardGold);
                    return T("What is the total RewardGold of all quests the player currently has active (Player.ActiveQuests)?", FmtNumber, Expected.Num(total),
                        h => Scalar(h, "world.LocalPlayer | get ActiveQuests | sum RewardGold"));
                },
            };
            yield return new Template
            {
                Id = "read-rare-inventory", Category = "read",
                Make = c =>
                {
                    var exp = c.W.LocalPlayer!.Inventory.Slots.Where(s => s.Item!.Rarity >= ItemRarity.Rare).Select(s => s.Item!.Name).Distinct().ToList();
                    if (exp.Count == 0) return null;
                    return T("Which items in the player's inventory have rarity Rare, Epic or Legendary?", FmtNames("item names (no duplicates)"), Expected.Names(exp),
                        h => Json(h, "world.LocalPlayer.Inventory.Slots | where Item.Rarity >= \"Rare\" | distinct Item | select Item.Name"));
                },
            };
            yield return new Template
            {
                Id = "read-asset-deps", Category = "read", Split = "holdout",
                Make = c =>
                {
                    var cands = c.W.Assets.Values.Where(a => a.Type == AssetType.Material && a.Dependencies.Count >= 2).ToList();
                    if (cands.Count == 0) return null;
                    var a = c.Rng.Pick(cands);
                    return T($"List the ids of the direct dependencies of the asset {Q(a.Id)}.", FmtNames("asset id strings"), Expected.Names(a.Dependencies.Select(d => d.Id)),
                        h => Json(h, $"world.Assets[{Q(a.Id)}].Dependencies | select Id"));
                },
            };
            yield return new Template
            {
                Id = "read-settings", Category = "read",
                Make = c =>
                {
                    var st = c.W.Settings;
                    return T("Is VSync enabled, what is the graphics Quality, and what is TargetFps (null if unlimited)?", FmtObj("VSync", "Quality", "TargetFps"),
                        Expected.Obj(new Dictionary<string, object?> { ["VSync"] = st.Graphics.VSync, ["Quality"] = st.Graphics.Quality.ToString(), ["TargetFps"] = st.TargetFps }),
                        h =>
                        {
                            var r = Rows(h, "world.Settings | select Graphics.VSync Graphics.Quality TargetFps")[0];
                            return $"{{\"VSync\":{r[0]},\"Quality\":\"{r[1]}\",\"TargetFps\":{r[2] ?? "null"}}}";
                        });
                },
            };

            // ======================= AGGREGATE
            yield return new Template
            {
                Id = "agg-dead-enemies", Category = "aggregate",
                Make = c => T("How many enemies in the whole world are dead (Health equal to 0)?", FmtNumber,
                    Expected.Num(c.All.OfType<Enemy>().Count(e => e.Health == 0)),
                    h => Scalar(h, "objects | where $ is Enemy and Health == 0 | count")),
            };
            yield return new Template
            {
                Id = "agg-avg-level", Category = "aggregate",
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).OfType<Enemy>().Any(e => e.Faction == Faction.Undead));
                    var lv = c.In(s).OfType<Enemy>().Where(e => e.Faction == Faction.Undead).Select(e => (double)e.Level).ToList();
                    if (lv.Count == 0) return null;
                    return T($"What is the average Level of Undead-faction enemies in scene {Q(s.Name)}? Round to 2 decimals.", FmtNumber, Expected.Num(lv.Average(), 0.0051),
                        h => Scalar(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Enemy and Faction == \"Undead\" | avg Level"));
                },
            };
            yield return new Template
            {
                Id = "agg-most-enemies-scene", Category = "aggregate", Split = "holdout",
                Make = c =>
                {
                    var counts = c.W.Scenes.Select(s => (s.Name, n: c.In(s).OfType<Enemy>().Count())).OrderByDescending(t => t.n).ToList();
                    if (counts.Count > 1 && counts[0].n == counts[1].n) return null;
                    return T("Which scene contains the most enemies?", "Answer with just the scene name.", Expected.Str(counts[0].Name),
                        h => Rows(h, "objects | where $ is Enemy | group-by Scene.Name | sort count desc | first 1")[0][0]!);
                },
            };
            yield return new Template
            {
                Id = "agg-bright-point-lights", Category = "aggregate",
                Make = c =>
                {
                    double x = c.Rng.Range(3, 6);
                    var n = c.All.SelectMany(g => g.Components.OfType<LightComponent>()).Count(l => l.Type == LightType.Point && l.Intensity > x);
                    return T($"How many point lights (LightComponent of type Point) with Intensity greater than {F(x)} exist across all scenes?", FmtNumber, Expected.Num(n),
                        h => Scalar(h, $"objects | get Components | where $ is LightComponent and Type == \"Point\" and Intensity > {F(x)} | count"));
                },
            };
            yield return new Template
            {
                Id = "agg-inventory-value", Category = "aggregate", Split = "holdout",
                Make = c => T("What is the total value of the player's inventory, i.e. the sum over all slots of Item.Value × Count?", FmtNumber,
                    Expected.Num(c.W.LocalPlayer!.Inventory.Slots.Sum(s => s.Item!.Value * s.Count)),
                    h => Scalar(h, "world.LocalPlayer.Inventory.Slots | sum (Item.Value * Count)")),
            };
            yield return new Template
            {
                Id = "agg-quest-level-gate", Category = "aggregate",
                Make = c =>
                {
                    var p = c.W.LocalPlayer!;
                    bool any = c.W.Quests.Any(q => q.State == QuestState.Active && q.RequiredLevel > p.Level);
                    return T("Is there any quest in state Active whose RequiredLevel is greater than the player's Level?", FmtBool, Expected.Flag(any),
                        h =>
                        {
                            var lvl = Scalar(h, "world.LocalPlayer | max Level");
                            return Scalar(h, $"world.Quests | where State == \"Active\" and RequiredLevel > {lvl} | count") == "0" ? "false" : "true";
                        });
                },
            };
            yield return new Template
            {
                Id = "agg-orders-by-country", Category = "aggregate",
                Make = c =>
                {
                    var country = c.Rng.Pick(new[] { "DE", "US", "FR", "JP", "BR", "UA" });
                    var n = c.A.Orders.Count(o => o.Status == OrderStatus.Delivered && o.Customer!.Profile.Country == country);
                    return T($"How many orders with status Delivered were placed by users whose profile country is {Q(country)}?", FmtNumber, Expected.Num(n),
                        h => Scalar(h, $"app.Orders | where Status == \"Delivered\" and Customer.Profile.Country == {Q(country)} | count"));
                },
            };
            yield return new Template
            {
                Id = "agg-paid-revenue", Category = "aggregate", Split = "holdout",
                Make = c => T("What is the total revenue (sum of order Total) of all orders with status Paid? Round to 2 decimals.", FmtNumber,
                    Expected.Num((double)Math.Round(c.A.Orders.Where(o => o.Status == OrderStatus.Paid).Sum(o => o.Total), 2), 0.011),
                    h => Scalar(h, "app.Orders | where Status == \"Paid\" | sum Total")),
            };
            yield return new Template
            {
                Id = "agg-top-level-enemy", Category = "aggregate",
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).OfType<Enemy>().Any());
                    var en = c.In(s).OfType<Enemy>().ToList();
                    if (en.Count == 0) return null;
                    int max = en.Max(e => e.Level);
                    return T($"Which enemy in scene {Q(s.Name)} has the highest Level? If several tie, list all of them.", FmtRefs, Expected.Refs(en.Where(e => e.Level == max).Select(RefOf)),
                        h =>
                        {
                            var top = Scalar(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Enemy | max Level");
                            return Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Enemy and Level == {top} | select $ref");
                        });
                },
            };

            // ======================= TRAVERSE
            yield return new Template
            {
                Id = "trav-hierarchy-path", Category = "traverse",
                Make = c =>
                {
                    var cands = c.All.Where(g => g.Name.StartsWith("Chest_") || g.Name.StartsWith("Candle_")).ToList();
                    if (cands.Count == 0) return null;
                    var go = c.Rng.Pick(cands);
                    var names = new List<string>();
                    for (var p = go; p != null; p = p.Parent) names.Add(p.Name);
                    names.Reverse();
                    var path = string.Join("/", names);
                    return T($"What is the full hierarchy path of the object named {Q(go.Name)}: the names from its scene root object down to the object itself, joined with \"/\"?", FmtText, Expected.Str(path),
                        h =>
                        {
                            var parts = new List<string>();
                            var cur = $"objects | where Name == {Q(go.Name)}";
                            while (true)
                            {
                                var r = Rows(h, cur + " | select Name");
                                if (r.Count == 0) break;
                                parts.Add(r[0][0]!);
                                cur += " | get Parent";
                            }
                            parts.Reverse();
                            return string.Join("/", parts);
                        });
                },
            };
            yield return new Template
            {
                Id = "trav-descendant-count", Category = "traverse", Split = "holdout",
                Make = c =>
                {
                    var s = c.PickScene();
                    var houses = c.In(s).Where(g => g.Name.StartsWith("House_")).ToList();
                    if (houses.Count == 0) return null;
                    var house = c.Rng.Pick(houses);
                    int n = WorldGenerator.Descendants(house).Count() - 1;
                    return T($"How many GameObjects are nested anywhere under (descendants of) the object {Q(house.Name)} in scene {Q(s.Name)}? Do not count the object itself.", FmtNumber, Expected.Num(n),
                        h => Scalar(h, $"objects | where Scene.Name == {Q(s.Name)} and Name == {Q(house.Name)} | recurse Children max-depth=16 include-self=false | count"));
                },
            };
            yield return new Template
            {
                Id = "trav-max-depth", Category = "traverse",
                Make = c =>
                {
                    var s = c.PickScene();
                    int Depth(GameObject g) { int d = 0; for (var p = g.Parent; p != null; p = p.Parent) d++; return d; }
                    int max = c.In(s).Max(Depth);
                    return T($"What is the maximum nesting depth of the object hierarchy in scene {Q(s.Name)}? Scene root objects have depth 0, their children depth 1, and so on.", FmtNumber, Expected.Num(max),
                        h =>
                        {
                            string Count(int depth) => depth == 0 ? "0" : Scalar(h, $"world.Scenes | where Name == {Q(s.Name)} | get Objects | recurse Children max-depth={depth} | count");
                            int d = 0;
                            while (Count(d + 1) != Count(d)) d++;
                            return d.ToString(CultureInfo.InvariantCulture);
                        });
                },
            };
            yield return new Template
            {
                Id = "trav-asset-closure", Category = "traverse",
                Make = c =>
                {
                    var start = c.W.Assets["prefab/house"];
                    var seen = new HashSet<Asset>();
                    var stack = new Stack<Asset>(start.Dependencies);
                    while (stack.Count > 0) { var a = stack.Pop(); if (a != start && seen.Add(a)) foreach (var d in a.Dependencies) stack.Push(d); }
                    return T("List the ids of ALL assets that \"prefab/house\" depends on, directly or transitively (follow Dependencies recursively; beware of cycles). Do not include prefab/house itself.", FmtNames("asset id strings"),
                        Expected.Names(seen.Select(a => a.Id)),
                        h => Json(h, "world.Assets[\"prefab/house\"] | recurse Dependencies max-depth=16 | where Id != \"prefab/house\" | select Id"));
                },
            };
            yield return new Template
            {
                Id = "trav-friends-of-friends", Category = "traverse", Split = "holdout",
                Make = c =>
                {
                    var cands = c.A.Users.Where(u => u.Friends.Count >= 2).ToList();
                    var u = c.Rng.Pick(cands);
                    var fof = u.Friends.SelectMany(f => f.Friends).Where(x => x != u && !u.Friends.Contains(x)).Select(x => x.Id.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
                    if (fof.Count == 0) return null;
                    return T($"Find the friends-of-friends of the user with Id {u.Id}: users who are friends of their friends, excluding the user and the user's direct friends.", FmtNames("user Ids (numbers)"),
                        Expected.Names(fof),
                        h =>
                        {
                            var direct = Rows(h, $"app.Users | where Id == {u.Id} | get Friends | select Id").Select(r => r[0]).ToHashSet();
                            var two = Rows(h, $"app.Users | where Id == {u.Id} | get Friends | get Friends | distinct | select Id").Select(r => r[0]).Where(x => x != u.Id.ToString(CultureInfo.InvariantCulture) && !direct.Contains(x));
                            return "[" + string.Join(",", two) + "]";
                        });
                },
            };
            yield return new Template
            {
                Id = "trav-camera-parent", Category = "traverse",
                Make = c =>
                {
                    var cam = c.All.First(g => g.Components.OfType<CameraComponent>().Any(k => k.IsMain));
                    return T("Which object is the parent of the GameObject that holds the main camera (CameraComponent with IsMain = true)?", "Answer with the parent's $ref.",
                        Expected.Refs(new[] { RefOf(cam.Parent!) }),
                        h => Col0(h, "objects | where Components.any($ is CameraComponent and IsMain) | get Parent | select $ref"));
                },
            };

            // ======================= MODIFY
            yield return new Template
            {
                Id = "mod-disable-bright-lights", Category = "modify", Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    double x = c.Rng.Range(5, 7);
                    var ids = c.In(s).SelectMany(g => g.Components.OfType<LightComponent>()).Where(l => l.Type == LightType.Point && l.Intensity > x).Select(l => l.Id).ToList();
                    if (ids.Count == 0 || ids.Count > 20) return null;
                    return T($"In scene {Q(s.Name)}, disable (Enabled = false) the LightComponent of every point light whose Intensity is greater than {F(x)}. Change nothing else.", FmtState,
                        Expected.State(sb => { foreach (var l in WorldGenerator.AllObjects(sb.World).SelectMany(g => g.Components).OfType<LightComponent>().Where(l => ids.Contains(l.Id))) l.Enabled = false; }),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(s.Name)} | get Components | where $ is LightComponent and Type == \"Point\" and Intensity > {F(x)} | set Enabled = false"));
                },
            };
            yield return new Template
            {
                Id = "mod-raise-object", Category = "modify", Mutation = true,
                Make = c =>
                {
                    var cands = c.All.Where(g => g.Name.StartsWith("Statue_") || g.Name.StartsWith("Well_") || g.Name.StartsWith("Cart_")).ToList();
                    if (cands.Count == 0) return null;
                    var go = c.Rng.Pick(cands);
                    int id = go.Id;
                    return T($"Move the object named {Q(go.Name)} up by 2 units: increase its Transform.Position.y by 2 and leave x and z unchanged.", FmtState,
                        Expected.State(sb => { var g = ById(sb, id); var p = g.Transform.Position; p.y += 2; g.Transform.Position = p; }),
                        h => Mutate(h, $"objects | where Name == {Q(go.Name)} | set Transform.Position.y = Transform.Position.y + 2"));
                },
            };
            yield return new Template
            {
                Id = "mod-heal-bandits", Category = "modify", Split = "holdout", Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).OfType<Enemy>().Count(e => e.Faction == Faction.Bandits) is int n && n > 0 && n <= 20);
                    var ids = c.In(s).OfType<Enemy>().Where(e => e.Faction == Faction.Bandits).Select(e => e.Id).ToList();
                    if (ids.Count == 0 || ids.Count > 20) return null;
                    return T($"Set the Health of every Bandits-faction enemy in scene {Q(s.Name)} to that enemy's MaxHealth.", FmtState,
                        Expected.State(sb => { foreach (var e in WorldGenerator.AllObjects(sb.World).OfType<Enemy>().Where(e => ids.Contains(e.Id))) e.Health = e.MaxHealth; }),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Enemy and Faction == \"Bandits\" | set Health = MaxHealth"));
                },
            };
            yield return new Template
            {
                Id = "mod-settings", Category = "modify", Mutation = true,
                Make = c => T("In the game settings, enable VSync and set TargetFps to 60.", FmtState,
                    Expected.State(sb => { sb.World.Settings.Graphics.VSync = true; sb.World.Settings.TargetFps = 60; }),
                    h => { Mutate(h, "world.Settings | set Graphics.VSync = true, TargetFps = 60"); return "done"; }),
            };
            yield return new Template
            {
                Id = "mod-complete-quest", Category = "modify", Split = "holdout", Mutation = true,
                Make = c =>
                {
                    var cands = c.W.Quests.Where(q => q.State != QuestState.Completed && c.W.Quests.Count(o => o.Title == q.Title) == 1).ToList();
                    if (cands.Count == 0) return null;
                    var q = c.Rng.Pick(cands);
                    int id = q.Id;
                    return T($"Mark the quest titled {Q(q.Title)} as Completed (set its State).", FmtState,
                        Expected.State(sb => sb.World.Quests.First(x => x.Id == id).State = QuestState.Completed),
                        h => Mutate(h, $"world.Quests | where Title == {Q(q.Title)} | set State = \"Completed\""));
                },
            };
            yield return new Template
            {
                Id = "mod-unlock-chests", Category = "modify", Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).Count(IsLockedChest) is int n && n > 0 && n <= 20);
                    var ids = c.In(s).Where(g => g.Name.StartsWith("Chest_")).Select(g => g.Id).ToList();
                    if (!c.In(s).Any(IsLockedChest) || ids.Count > 20) return null;
                    return T($"Unlock every chest in scene {Q(s.Name)}: set the \"locked\" property of each chest's \"Chest\" script to false.", FmtState,
                        Expected.State(sb =>
                        {
                            foreach (var g in WorldGenerator.AllObjects(sb.World).Where(g => ids.Contains(g.Id)))
                                foreach (var sc in g.Components.OfType<ScriptComponent>().Where(x => x.ScriptName == "Chest")) sc.Properties["locked"] = false;
                        }),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(s.Name)} | get Components | where $ is ScriptComponent and ScriptName == \"Chest\" | set Properties[\"locked\"] = false"));
                },
            };
            yield return new Template
            {
                Id = "mod-rename", Category = "modify", Mutation = true,
                Make = c =>
                {
                    var cands = c.All.Where(g => g.Name.StartsWith("Ambient_")).ToList();
                    var go = c.Rng.Pick(cands);
                    int id = go.Id;
                    var scene = go.Scene!.Name;
                    return T($"Rename the object {Q(go.Name)} in scene {Q(scene)} to {Q(go.Name + "_old")}.", FmtState,
                        Expected.State(sb => ById(sb, id).Name += "_old"),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(scene)} and Name == {Q(go.Name)} | set Name = {Q(go.Name + "_old")}"));
                },
            };

            // ======================= FIND-THEN-MODIFY
            yield return new Template
            {
                Id = "fm-heal-weakest", Category = "find-modify", Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).OfType<Enemy>().Any(e => e.Health > 0));
                    var alive = c.In(s).OfType<Enemy>().Where(e => e.Health > 0).OrderBy(e => e.Health).ToList();
                    if (alive.Count < 2 || alive[0].Health == alive[1].Health) return null;
                    int id = alive[0].Id;
                    return T($"Find the living enemy (Health > 0) with the lowest Health in scene {Q(s.Name)} and heal it by 50 using its Heal method.", FmtState,
                        Expected.State(sb => ((Enemy)ById(sb, id)).Heal(50)),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Enemy and Health > 0 | sort Health | first 1 | call Heal(50)"));
                },
            };
            yield return new Template
            {
                Id = "fm-destroy-dead", Category = "find-modify", Split = "holdout", Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).OfType<Enemy>().Any(e => e.Health == 0));
                    var ids = c.In(s).OfType<Enemy>().Where(e => e.Health == 0).Select(e => e.Id).ToList();
                    if (ids.Count == 0) return null;
                    return T($"Destroy (using the Destroy method) every enemy in scene {Q(s.Name)} whose Health is 0.", FmtState,
                        Expected.State(sb => { foreach (var id in ids) ById(sb, id).Destroy(); }),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(s.Name)} and $ is Enemy and Health == 0 | call Destroy()", 200));
                },
            };
            yield return new Template
            {
                Id = "fm-retarget-spawn", Category = "find-modify", Mutation = true,
                Make = c =>
                {
                    var spawns = c.All.Where(g => g.Name.StartsWith("Spawn_") && g.Children.OfType<Enemy>().Count() is int n && n > 0 && n <= 20).ToList();
                    var sp = c.Rng.Pick(spawns);
                    var scene = sp.Scene!.Name;
                    var ids = sp.Children.OfType<Enemy>().Select(e => e.Id).ToList();
                    return T($"Make every enemy in the spawn group {Q(sp.Name)} of scene {Q(scene)} target the player (set their Target to the player character).", FmtState,
                        Expected.State(sb => { foreach (var e in WorldGenerator.AllObjects(sb.World).OfType<Enemy>().Where(e => ids.Contains(e.Id))) e.Target = sb.World.LocalPlayer; }),
                        h =>
                        {
                            var pref = Col0(h, "world.LocalPlayer | select $ref");
                            return Mutate(h, $"objects | where Scene.Name == {Q(scene)} and Name == {Q(sp.Name)} | get Children | where $ is Enemy | set Target = ref({Q(pref)})");
                        });
                },
            };

            // ======================= NEGATIVE
            yield return new Template
            {
                Id = "neg-missing-object", Category = "negative",
                Make = c =>
                {
                    var name = c.Rng.Pick(new[] { "Dragon_King", "Lich_0000", "Enemy_Phantom_9999", "Golem_0001" });
                    return T($"What is the Health of the enemy named {Q(name)}?", FmtNumber + " If no such enemy exists, answer NONE.", Expected.NoneAnswer(),
                        h => Rows(h, $"objects | where Name == {Q(name)} | select Health").Count == 0 ? "NONE" : "?");
                },
            };
            yield return new Template
            {
                Id = "neg-readonly-id", Category = "negative", Mutation = true,
                Make = c =>
                {
                    var go = c.Rng.Pick(c.All.Where(g => g.Name.StartsWith("Torch_")).ToList());
                    return T($"Change the Id of the object {Q(go.Name)} in scene {Q(go.Scene!.Name)} to 99999.", FmtState, Expected.ImpossibleAnswer(),
                        h => { var r = h.Mutate($"objects | where Name == {Q(go.Name)} | set Id = 99999", false); return r.Ok ? "done" : "IMPOSSIBLE: Id is read-only"; });
                },
            };
            yield return new Template
            {
                Id = "neg-delete-scene", Category = "negative", Split = "holdout", Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    return T($"Delete the entire scene {Q(s.Name)} from the world.", FmtState, Expected.ImpossibleAnswer(),
                        h => { var r = h.Mutate($"world.Scenes | where Name == {Q(s.Name)} | call Destroy()", false); return r.Ok ? "done" : "IMPOSSIBLE: scenes have no Destroy method"; });
                },
            };
            yield return new Template
            {
                Id = "neg-missing-scene", Category = "negative",
                Make = c => T("How many objects are in the scene named \"Atlantis\"?", FmtNumber + " If there is no such scene, answer NONE.", Expected.NoneAnswer(),
                    h => Scalar(h, "world.Scenes | where Name == \"Atlantis\" | count") == "0" ? "NONE" : "?"),
            };

            // ======================= SCALE
            yield return new Template
            {
                Id = "scale-kinematic-count", Category = "scale", Size = WorldSize.Large,
                Make = c => T("How many GameObjects in the whole world have a RigidbodyComponent with IsKinematic = true?", FmtNumber,
                    Expected.Num(c.All.Count(g => g.Components.OfType<RigidbodyComponent>().Any(r => r.IsKinematic))),
                    h => Scalar(h, "objects | where Components.any($ is RigidbodyComponent and IsKinematic) | count")),
            };
            yield return new Template
            {
                Id = "scale-top3-enemies", Category = "scale", Split = "holdout", Size = WorldSize.Huge,
                Make = c =>
                {
                    var en = c.All.OfType<Enemy>().OrderByDescending(e => e.Level).ToList();
                    if (en[2].Level == en[3].Level) return null;
                    return T("Find the 3 enemies with the highest Level in the whole world.", FmtRefs, Expected.Refs(en.Take(3).Select(RefOf)),
                        h => Col0(h, "objects | where $ is Enemy | sort Level desc | first 3 | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "scale-locked-chests", Category = "scale", Size = WorldSize.Large,
                Make = c => T("How many chests in the whole world are locked (their \"Chest\" script property locked = true)?", FmtNumber,
                    Expected.Num(c.All.Count(IsLockedChest)),
                    h => Scalar(h, "objects | where Components.any($ is ScriptComponent and ScriptName == \"Chest\" and Properties.locked == true) | count")),
            };
        }

        private static bool IsLockedChest(GameObject g) =>
            g.Components.OfType<ScriptComponent>().Any(s => s.ScriptName == "Chest" && s.Properties.TryGetValue("locked", out var v) && v is bool b && b);

        // ------------------------------------------------------------ instantiation

        private static int[] SeedsFor(string split) => split switch
        {
            "holdout" => HoldoutSeeds,
            "holdout2" => Holdout2Seeds,
            "bulk" => BulkSeeds,
            "wave5" => Wave5Seeds,
            "wave6" => Wave6Seeds,
            _ => DevSeeds,
        };

        public static List<TaskInstance> Build(string split)
        {
            var list = new List<TaskInstance>();
            foreach (var t in Templates().Concat(TemplatesV2()).Concat(TemplatesBulk()).Concat(TemplatesWave5()).Concat(TemplatesWave6()).Where(t => t.Split == split || split == "all"))
            {
                foreach (var seed in SeedsFor(t.Split))
                {
                    var inst = Instantiate(t, seed);
                    if (inst != null) list.Add(inst);
                }
            }
            return list;
        }

        private static TaskInstance? Instantiate(Template t, int seed)
        {
            var sb = WorldGenerator.Create(seed, t.Size);
            // Template RNG: retry a few draws if parameters produce a degenerate task.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var rng = new Rng((ulong)seed * 7919UL + (ulong)Hash(t.Id) + (ulong)attempt * 104729UL);
                var inst = t.Make(new Ctx(sb, rng));
                if (inst == null) continue;
                inst.Id = $"{t.Id}@{seed}";
                inst.Template = t.Id;
                inst.Category = t.Category;
                inst.Split = t.Split;
                inst.Seed = seed;
                inst.Size = t.Size;
                inst.AllowMutation = t.Mutation;
                return inst;
            }
            return null;
        }

        private static uint Hash(string s)
        {
            uint h = 2166136261;
            foreach (var ch in s) { h ^= ch; h *= 16777619; }
            return h;
        }
    }
}
