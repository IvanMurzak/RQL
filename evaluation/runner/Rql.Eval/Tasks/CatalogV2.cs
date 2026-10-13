using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Catalog v2 "holdout2": templates written AFTER all language/prompt tuning was finished and evaluated
    /// once per condition without iteration — the clean generalization check (RESULTS.md §3.4).
    /// </summary>
    public static partial class Catalog
    {
        public static readonly int[] Holdout2Seeds = { 31, 32 };

        private static IEnumerable<Template> TemplatesV2()
        {
            const string S = "holdout2";
            yield return new Template
            {
                Id = "v2-find-quiet-loops", Category = "find", Split = S,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).Any(g => g.Components.OfType<AudioSourceComponent>().Any(a => a.Loop && a.Volume < 0.4f)));
                    var exp = c.In(s).Where(g => g.Components.OfType<AudioSourceComponent>().Any(a => a.Loop && a.Volume < 0.4f)).Select(RefOf).ToList();
                    if (exp.Count == 0) return null;
                    return T($"In scene {Q(s.Name)}, find the GameObjects that have a looping AudioSourceComponent with Volume below 0.4.", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and Components.any($ is AudioSourceComponent and Loop and Volume < 0.4) | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "v2-find-empty-npcs", Category = "find", Split = S,
                Make = c =>
                {
                    var exp = c.All.OfType<Npc>().Where(n => n.Inventory.Slots.Count == 0).Select(RefOf).ToList();
                    if (exp.Count == 0 || exp.Count > 40) return null;
                    return T("Which NPCs have an empty inventory (no item slots at all)?", FmtRefs, Expected.Refs(exp),
                        h => Col0(h, "objects | where $ is Npc and Inventory.Slots.count() == 0 | select $ref"));
                },
            };
            yield return new Template
            {
                Id = "v2-read-quest-giver", Category = "read", Split = S,
                Make = c =>
                {
                    var cands = c.W.Quests.Where(q => q.Giver != null && c.W.Quests.Count(o => o.Title == q.Title) == 1).ToList();
                    if (cands.Count == 0) return null;
                    var q = c.Rng.Pick(cands);
                    return T($"What is the name of the NPC who gives the quest titled {Q(q.Title)}?", FmtText, Expected.Str(q.Giver!.Name),
                        h => Rows(h, $"world.Quests | where Title == {Q(q.Title)} | select Giver.Name")[0][0]!);
                },
            };
            yield return new Template
            {
                Id = "v2-read-item-tags", Category = "read", Split = S,
                Make = c =>
                {
                    var cands = c.W.ItemCatalog.Where(i => i.Tags.Length >= 2 && c.W.ItemCatalog.Count(o => o.Name == i.Name) == 1).ToList();
                    if (cands.Count == 0) return null;
                    var it = c.Rng.Pick(cands);
                    return T($"List the tags of the catalog item named {Q(it.Name)}.", FmtNames("tag strings"), Expected.Names(it.Tags),
                        h => Json(h, $"world.ItemCatalog | where Name == {Q(it.Name)} | get Tags | select $"));
                },
            };
            yield return new Template
            {
                Id = "v2-agg-legendary-droppers", Category = "aggregate", Split = S,
                Make = c => T("How many enemies have at least one Legendary-rarity item in their LootTable?", FmtNumber,
                    Expected.Num(c.All.OfType<Enemy>().Count(e => e.LootTable.Any(d => d.Item!.Rarity == ItemRarity.Legendary))),
                    h => Scalar(h, "objects | where $ is Enemy and LootTable.any(Item.Rarity == \"Legendary\") | count")),
            };
            yield return new Template
            {
                Id = "v2-agg-avg-lines", Category = "aggregate", Split = S,
                Make = c =>
                {
                    var o = c.A.Orders.Where(x => x.Status == OrderStatus.Shipped).ToList();
                    if (o.Count == 0) return null;
                    return T("What is the average number of order lines per order among orders with status Shipped? Round to 2 decimals.", FmtNumber,
                        Expected.Num(o.Average(x => (double)x.Lines.Count), 0.0051),
                        h => Scalar(h, "app.Orders | where Status == \"Shipped\" | avg Lines.count()"));
                },
            };
            yield return new Template
            {
                Id = "v2-agg-top-country", Category = "aggregate", Split = S,
                Make = c =>
                {
                    var g = c.A.Users.Where(u => u.Active && u.Profile.Country != null).GroupBy(u => u.Profile.Country!).Select(x => (x.Key, n: x.Count())).OrderByDescending(x => x.n).ToList();
                    if (g.Count < 2 || g[0].n == g[1].n) return null;
                    return T("Which profile country has the most active users? (Ignore users without a country.)", "Answer with just the country code.", Expected.Str(g[0].Key),
                        h => Rows(h, "app.Users | where Active and Profile.Country is not null | group-by Profile.Country | sort count desc | first 1")[0][0]!);
                },
            };
            yield return new Template
            {
                Id = "v2-trav-room-count", Category = "traverse", Split = S,
                Make = c =>
                {
                    var s = c.PickScene();
                    var houses = c.In(s).Where(g => g.Name.StartsWith("House_")).ToList();
                    if (houses.Count == 0) return null;
                    var house = c.Rng.Pick(houses);
                    int n = WorldGenerator.Descendants(house).Count(g => g.Name.StartsWith("Room_"));
                    return T($"How many rooms (objects whose name starts with \"Room_\") are inside {Q(house.Name)} in scene {Q(s.Name)}, at any nesting level?", FmtNumber, Expected.Num(n),
                        h => Scalar(h, $"objects | where Scene.Name == {Q(s.Name)} and Name == {Q(house.Name)} | recurse Children | where Name starts-with \"Room_\" | count"));
                },
            };
            yield return new Template
            {
                Id = "v2-trav-scene-textures", Category = "traverse", Split = S,
                Make = c =>
                {
                    var s = c.PickScene();
                    var tex = c.In(s).SelectMany(g => g.Components.OfType<RendererComponent>()).Where(r => r.Material != null)
                        .SelectMany(r => r.Material!.Dependencies).Where(a => a.Type == AssetType.Texture).Select(a => a.Id).Distinct().ToList();
                    if (tex.Count == 0) return null;
                    return T($"Which texture assets are used by the materials of the renderers in scene {Q(s.Name)}? Follow RendererComponent.Material, then the material's Dependencies, keep assets of type Texture.", FmtNames("distinct asset id strings"), Expected.Names(tex),
                        h => Json(h, $"objects | where Scene.Name == {Q(s.Name)} | get Components | where $ is RendererComponent | get Material | get Dependencies | where Type == \"Texture\" | distinct Id | select Id"));
                },
            };
            yield return new Template
            {
                Id = "v2-mod-mute-ambience", Category = "modify", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    var ids = c.In(s).Where(g => g.Parent != null && g.Parent.Name == "Ambience").SelectMany(g => g.Components.OfType<AudioSourceComponent>()).Select(a => a.Id).ToList();
                    if (ids.Count == 0 || ids.Count > 20) return null;
                    return T($"Mute the ambience of scene {Q(s.Name)}: set Volume to 0 on the AudioSourceComponent of every object under its \"Ambience\" root object.", FmtState,
                        Expected.State(sb => { foreach (var a in WorldGenerator.AllObjects(sb.World).SelectMany(g => g.Components).OfType<AudioSourceComponent>().Where(a => ids.Contains(a.Id))) a.Volume = 0; }),
                        h => Mutate(h, $"world.Scenes | where Name == {Q(s.Name)} | get Objects | where Name == \"Ambience\" | get Children | get Components | where $ is AudioSourceComponent | set Volume = 0"));
                },
            };
            yield return new Template
            {
                Id = "v2-mod-center-chest", Category = "modify", Split = S, Mutation = true,
                Make = c =>
                {
                    var cands = c.All.Where(g => g.Name.StartsWith("Chest_")).ToList();
                    if (cands.Count == 0) return null;
                    var go = c.Rng.Pick(cands);
                    int id = go.Id;
                    return T($"Move the object {Q(go.Name)} to x = 0 and z = 0, keeping its y position.", FmtState,
                        Expected.State(sb => { var g = ById(sb, id); var p = g.Transform.Position; p.x = 0; p.z = 0; g.Transform.Position = p; }),
                        h => Mutate(h, $"objects | where Name == {Q(go.Name)} | set Transform.Position.x = 0, Transform.Position.z = 0"));
                },
            };
            yield return new Template
            {
                Id = "v2-fm-heal-player", Category = "find-modify", Split = S, Mutation = true,
                Make = c => T("Heal the player character by 25 using its Heal method.", FmtState,
                    Expected.State(sb => sb.World.LocalPlayer!.Heal(25)),
                    h => Mutate(h, "world.LocalPlayer | call Heal(25)")),
            };
            yield return new Template
            {
                Id = "v2-neg-readonly-usedslots", Category = "negative", Split = S, Mutation = true,
                Make = c => T("Set the player's Inventory.UsedSlots value to 0.", FmtState, Expected.ImpossibleAnswer(),
                    h => { var r = h.Mutate("world.LocalPlayer | set Inventory.UsedSlots = 0", false); return r.Ok ? "done" : "IMPOSSIBLE: UsedSlots is a computed read-only member"; }),
            };
            yield return new Template
            {
                Id = "v2-neg-missing-quest", Category = "negative", Split = S,
                Make = c => T("What is the RewardGold of the quest titled \"The Last Dragon\"?", FmtNumber + " If there is no such quest, answer NONE.", Expected.NoneAnswer(),
                    h => Rows(h, "world.Quests | where Title == \"The Last Dragon\" | select RewardGold").Count == 0 ? "NONE" : "?"),
            };
            yield return new Template
            {
                Id = "v2-scale-loot-chance", Category = "scale", Split = S, Size = WorldSize.Large,
                Make = c => T("What is the sum of the Chance values over all LootTable entries of all enemies in the world? Round to 2 decimals.", FmtNumber,
                    Expected.Num(c.All.OfType<Enemy>().SelectMany(e => e.LootTable).Sum(d => (double)d.Chance), 0.0051),
                    h => Scalar(h, "objects | where $ is Enemy | get LootTable | sum Chance")),
            };
        }
    }
}
