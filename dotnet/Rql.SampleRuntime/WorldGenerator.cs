using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Rql.Sample
{
    /// <summary>SplitMix64 — tiny, fast, and identical on every platform/runtime (unlike System.Random across versions).</summary>
    public sealed class Rng
    {
        private ulong _state;
        public Rng(ulong seed) { _state = seed; }

        public ulong NextU64()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>[0, n)</summary>
        public int Next(int n) => (int)(NextU64() % (ulong)n);
        public int Range(int min, int maxInclusive) => min + Next(maxInclusive - min + 1);
        public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));
        public bool Chance(double p) => NextDouble() < p;
        public T Pick<T>(IReadOnlyList<T> list) => list[Next(list.Count)];
        /// <summary>Float rounded to 2 decimals so values are exact-ish and readable.</summary>
        public float F(double min, double max) => (float)Math.Round(min + NextDouble() * (max - min), 2);
    }

    public enum WorldSize { Small, Medium, Large, Huge }

    public sealed class Sandbox
    {
        public Sandbox(World world, App app, int seed, WorldSize size)
        {
            World = world; App = app; Seed = seed; Size = size;
        }
        public World World { get; }
        public App App { get; }
        public int Seed { get; }
        public WorldSize Size { get; }
    }

    /// <summary>Deterministic generator: (seed, size) → identical object graph every time.</summary>
    public static class WorldGenerator
    {
        private static readonly string[] SceneNames = { "Village", "Forest", "Crypt", "Castle", "Harbor", "Mines", "Swamp", "Desert", "Glacier", "Volcano", "Ruins", "Arena", "Canyon", "Temple", "Market", "Docks" };
        private static readonly string[] Species = { "Goblin", "Skeleton", "Wolf", "Bandit", "Wraith", "Spider", "Orc", "Zombie" };
        private static readonly Faction[] SpeciesFaction = { Faction.Bandits, Faction.Undead, Faction.Wildlife, Faction.Bandits, Faction.Undead, Faction.Wildlife, Faction.Bandits, Faction.Undead };
        private static readonly string[] PropKinds = { "Tree_Oak", "Tree_Pine", "Rock", "Barrel", "Crate", "Fence", "Bush", "Cart", "Statue", "Well" };
        private static readonly string[] Adjectives = { "Rusty", "Iron", "Steel", "Elven", "Dwarven", "Ancient", "Cursed", "Blessed", "Shadow", "Golden", "Crystal", "Bone" };
        private static readonly string[] Nouns = { "Sword", "Axe", "Bow", "Staff", "Shield", "Helm", "Ring", "Amulet", "Potion", "Scroll", "Dagger", "Boots", "Gem", "Key" };
        private static readonly string[] Npcs = { "Merchant", "Blacksmith", "Innkeeper", "Guard", "Priest", "Farmer", "Hunter", "Scholar" };
        private static readonly string[] Countries = { "DE", "US", "FR", "JP", "BR", "UA", "PL", "GB", "IN", "CA" };
        private static readonly string[] FirstNames = { "Alex", "Sam", "Kim", "Lee", "Max", "Ana", "Ivo", "Mia", "Noa", "Eli", "Zoe", "Jun", "Ola", "Rui", "Tim", "Uma" };
        private static readonly string[] Categories = { "weapons", "armor", "potions", "cosmetics", "bundles" };

        public static (int scenes, int scale) Dimensions(WorldSize size) => size switch
        {
            WorldSize.Small => (3, 1),
            WorldSize.Medium => (5, 6),
            WorldSize.Large => (8, 40),
            _ => (12, 160),
        };

        public static WorldSize ParseSize(string s) => (WorldSize)Enum.Parse(typeof(WorldSize), s, true);

        public static Sandbox Create(int seed, WorldSize size)
        {
            var g = new Gen(new Rng((ulong)seed * 0x2545F4914F6CDD1DUL + 1), size);
            var world = g.BuildWorld(seed);
            var app = g.BuildApp();
            return new Sandbox(world, app, seed, size);
        }

        private sealed class Gen
        {
            private readonly Rng _r;
            private readonly int _scenes;
            private readonly int _k;
            private int _goId;
            private int _compId;
            private World _world = null!;

            public Gen(Rng r, WorldSize size)
            {
                _r = r;
                (_scenes, _k) = Dimensions(size);
            }

            public World BuildWorld(int seed)
            {
                _world = new World { Name = "Realm-" + seed.ToString(CultureInfo.InvariantCulture), Seed = seed, Tick = 1000 + seed };
                BuildSettings();
                BuildAssets();
                BuildItems();
                BuildQuests();
                for (int s = 0; s < _scenes; s++) _world.Scenes.Add(BuildScene(s));
                WireQuestGivers();
                return _world;
            }

            // -------------------------------------------------- catalog data

            private void BuildSettings()
            {
                var st = _world.Settings;
                st.Graphics.Quality = (Quality)_r.Next(4);
                st.Graphics.VSync = _r.Chance(0.5);
                st.Graphics.RenderScale = _r.Pick(new[] { 0.75f, 1f, 1.25f });
                st.TargetFps = _r.Chance(0.3) ? (int?)null : _r.Pick(new[] { 30, 60, 120, 144 });
                st.Audio.MusicVolume = _r.F(0.2, 1);
                foreach (var f in new[] { "photo-mode", "hardcore", "crossplay", "telemetry", "ray-tracing", "dlss" })
                    st.Features[f] = _r.Chance(0.5);
                st.Language = _r.Pick(new[] { "en", "de", "fr", "ja", "uk" });
            }

            private readonly List<Asset> _materials = new List<Asset>();
            private readonly List<Asset> _meshes = new List<Asset>();
            private readonly List<Asset> _clips = new List<Asset>();

            private Asset AddAsset(string id, AssetType type)
            {
                var a = new Asset
                {
                    Id = id,
                    Type = type,
                    Path = "Assets/" + type + "s/" + id.Substring(id.IndexOf('/') + 1) + Ext(type),
                    SizeBytes = 1024L * _r.Range(4, type == AssetType.Texture ? 16384 : 2048),
                    IsLoaded = _r.Chance(0.6),
                };
                a.Metadata["importer"] = type + "Importer";
                if (_r.Chance(0.3)) a.Metadata["author"] = _r.Pick(FirstNames);
                _world.Assets[id] = a;
                return a;
            }

            private static string Ext(AssetType t) => t switch
            {
                AssetType.Texture => ".png", AssetType.Material => ".mat", AssetType.Mesh => ".fbx",
                AssetType.Audio => ".wav", AssetType.Prefab => ".prefab", _ => ".cs",
            };

            private void BuildAssets()
            {
                string[] surfaces = { "stone", "wood", "metal", "moss", "sand", "ice", "lava", "cloth", "glass", "bone" };
                int nMat = Math.Min(surfaces.Length * 3, 6 + _k * 2);
                for (int i = 0; i < nMat; i++)
                {
                    var name = surfaces[i % surfaces.Length] + (i >= surfaces.Length ? "_" + (i / surfaces.Length + 1) : "");
                    var tex = AddAsset("tex/" + name, AssetType.Texture);
                    var mat = AddAsset("mat/" + name, AssetType.Material);
                    mat.Dependencies.Add(tex);
                    if (_r.Chance(0.4)) mat.Dependencies.Add(AddAsset("tex/" + name + "_normal", AssetType.Texture));
                    _materials.Add(mat);
                }
                foreach (var p in PropKinds.Concat(Species).Concat(new[] { "Torch", "Lamp", "Chest", "Door", "Wall", "Floor" }))
                    _meshes.Add(AddAsset("mesh/" + p.ToLowerInvariant(), AssetType.Mesh));
                foreach (var c in new[] { "wind", "fire", "water", "birds", "drums", "choir", "rain", "crickets" })
                    _clips.Add(AddAsset("sfx/" + c, AssetType.Audio));
                // Prefabs referencing each other (a dependency cycle) to exercise cycle handling.
                var house = AddAsset("prefab/house", AssetType.Prefab);
                var village = AddAsset("prefab/village", AssetType.Prefab);
                house.Dependencies.Add(_meshes[0]);
                house.Dependencies.Add(_materials[1 % _materials.Count]);
                house.Dependencies.Add(village);
                village.Dependencies.Add(house);
                AddAsset("script/enemy_ai", AssetType.Script);
                AddAsset("script/door", AssetType.Script);
            }

            private void BuildItems()
            {
                int n = 20 + _k * 8;
                for (int i = 1; i <= n; i++)
                {
                    var rarityRoll = _r.NextDouble();
                    var rarity = rarityRoll < 0.45 ? ItemRarity.Common : rarityRoll < 0.72 ? ItemRarity.Uncommon : rarityRoll < 0.9 ? ItemRarity.Rare : rarityRoll < 0.97 ? ItemRarity.Epic : ItemRarity.Legendary;
                    var noun = _r.Pick(Nouns);
                    var item = new Item
                    {
                        Id = 5000 + i,
                        Name = _r.Pick(Adjectives) + " " + noun + (i > Adjectives.Length * Nouns.Length / 2 ? " " + ToRoman(i % 9 + 1) : ""),
                        Rarity = rarity,
                        Value = (int)((5 + _r.Next(50)) * Math.Pow(3, (int)rarity)),
                        Weight = _r.F(0.1, 15),
                        Stackable = noun == "Potion" || noun == "Scroll" || noun == "Gem" || noun == "Key",
                    };
                    var tags = new List<string>();
                    if (noun == "Sword" || noun == "Axe" || noun == "Bow" || noun == "Dagger" || noun == "Staff") tags.Add("weapon");
                    if (noun == "Shield" || noun == "Helm" || noun == "Boots") tags.Add("armor");
                    if (noun == "Potion" || noun == "Scroll") tags.Add("consumable");
                    if (rarity >= ItemRarity.Epic) tags.Add("unique");
                    if (_r.Chance(0.15)) tags.Add("quest");
                    item.Tags = tags.ToArray();
                    _world.ItemCatalog.Add(item);
                }
            }

            private static string ToRoman(int n) => new[] { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" }[n - 1];

            private void BuildQuests()
            {
                string[] verbs = { "Clear", "Find", "Escort", "Deliver", "Defend", "Explore", "Hunt", "Rescue" };
                string[] objects = { "the Crypt", "the Lost Ring", "the Caravan", "the Letter", "the Gate", "the Mines", "the Alpha Wolf", "the Scholar", "the Old Well", "the Shrine" };
                int n = 6 + _k * 3;
                for (int i = 1; i <= n; i++)
                {
                    var q = new Quest
                    {
                        Id = 100 + i,
                        Title = _r.Pick(verbs) + " " + _r.Pick(objects) + (i > 10 ? " " + ToRoman(i % 9 + 1) : ""),
                        State = (QuestState)_r.Next(5),
                        RequiredLevel = _r.Chance(0.35) ? (int?)null : _r.Range(1, 40),
                        Reward = _r.Chance(0.7) ? _r.Pick(_world.ItemCatalog) : null,
                        RewardGold = _r.Range(1, 50) * 10,
                    };
                    int objCount = _r.Range(1, 4);
                    for (int o = 0; o < objCount; o++)
                    {
                        int target = _r.Range(1, 10);
                        q.Objectives.Add(new Objective
                        {
                            Description = _r.Pick(new[] { "Defeat enemies", "Collect items", "Talk to the elder", "Reach the summit", "Light the beacons" }) + " (" + (o + 1) + ")",
                            Target = target,
                            Progress = q.State == QuestState.Completed ? target : _r.Range(0, target),
                        });
                    }
                    _world.Quests.Add(q);
                }
            }

            // -------------------------------------------------- scenes

            private T NewGo<T>(string name, Scene scene, GameObject? parent, string tag = "Untagged") where T : GameObject, new()
            {
                var go = new T { Id = ++_goId, Name = name, Tag = tag, Scene = scene, Parent = parent, Layer = 0 };
                go.Transform.Position = new Vector3(_r.F(-500, 500), _r.F(0, 40), _r.F(-500, 500));
                go.Transform.Rotation = new Vector3(0, _r.Range(0, 35) * 10, 0);
                if (parent != null) parent.Children.Add(go);
                else scene.Objects.Add(go);
                return go;
            }

            private GameObject Group(string name, Scene scene, GameObject? parent) => NewGo<GameObject>(name, scene, parent);

            private TC Add<TC>(GameObject go, TC c) where TC : Component
            {
                c.Id = ++_compId;
                c.Owner = go;
                go.Components.Add(c);
                return c;
            }

            private Scene BuildScene(int index)
            {
                var scene = new Scene { Id = index + 1, Name = SceneNames[index % SceneNames.Length] + (index >= SceneNames.Length ? "_" + (index / SceneNames.Length + 1) : ""), Loaded = index < 2 || _r.Chance(0.5), World = _world };
                if (scene.Name == "Glacier") scene.Gravity = new Vector3(0, -9.81f, 0);

                // Lighting
                var lighting = Group("Lighting", scene, null);
                var sun = NewGo<GameObject>("Sun", scene, lighting, "Light");
                Add(sun, new LightComponent { Type = LightType.Directional, Intensity = _r.F(0.5, 2), CastShadows = true, Range = 0, Color = new Color(1, 0.95f, 0.85f) });
                int nLights = 4 + _k * 4;
                for (int i = 1; i <= nLights; i++)
                {
                    bool torch = _r.Chance(0.6);
                    var lg = NewGo<GameObject>((torch ? "Torch_" : "Lamp_") + i.ToString("000", CultureInfo.InvariantCulture), scene, lighting, "Light");
                    lg.Layer = 2;
                    var lc = Add(lg, new LightComponent
                    {
                        Type = torch ? LightType.Point : (_r.Chance(0.7) ? LightType.Spot : LightType.Area),
                        Intensity = _r.F(0.2, 8),
                        Range = _r.F(2, 30),
                        CastShadows = _r.Chance(0.3),
                        Color = torch ? new Color(1, 0.6f, 0.2f) : new Color(_r.F(0.5, 1), _r.F(0.5, 1), _r.F(0.5, 1)),
                    });
                    if (_r.Chance(0.2)) lc.Enabled = false;
                    if (_r.Chance(0.1)) lg.Enabled = false;
                    if (torch) Add(lg, new RendererComponent { Mesh = _meshes.First(m => m.Id == "mesh/torch"), Material = _r.Pick(_materials) });
                    if (torch && _r.Chance(0.3)) Add(lg, new AudioSourceComponent { Clip = _clips.First(c => c.Id == "sfx/fire"), Loop = true, Volume = _r.F(0.1, 0.6) });
                }

                // Environment with nested buildings
                var env = Group("Environment", scene, null);
                env.IsStatic = true;
                int nProps = 6 + _k * 12;
                var props = Group("Props", scene, env);
                for (int i = 1; i <= nProps; i++)
                {
                    var kind = _r.Pick(PropKinds);
                    var p = NewGo<GameObject>(kind + "_" + i.ToString("000", CultureInfo.InvariantCulture), scene, props, "Prop");
                    p.IsStatic = _r.Chance(0.8);
                    p.Layer = 1;
                    var scale = _r.F(0.5, 3);
                    p.Transform.Scale = new Vector3(scale, scale, scale);
                    Add(p, new RendererComponent { Mesh = _meshes.FirstOrDefault(m => m.Id == "mesh/" + kind.ToLowerInvariant()), Material = _r.Pick(_materials), CastShadows = _r.Chance(0.8), SortingOrder = _r.Range(0, 5) });
                    if (_r.Chance(0.7)) Add(p, new ColliderComponent { Shape = (ColliderShape)_r.Next(4), Size = new Vector3(scale, scale * 2, scale) });
                    if (kind == "Barrel" && _r.Chance(0.3)) p.Labels.Add("explosive");
                    if (_r.Chance(0.1)) p.Labels.Add("destructible");
                }
                int nBuildings = 1 + _k;
                for (int b = 1; b <= nBuildings; b++)
                {
                    var house = Group("House_" + b.ToString("00", CultureInfo.InvariantCulture), scene, env);
                    house.Tag = "Building";
                    house.Labels.Add("building");
                    Add(house, new ColliderComponent { Shape = ColliderShape.Box, Size = new Vector3(10, 6, 12) });
                    int floors = _r.Range(1, 3);
                    for (int f = 1; f <= floors; f++)
                    {
                        var floor = Group("Floor_" + f, scene, house);
                        int rooms = _r.Range(1, 3);
                        for (int rm = 1; rm <= rooms; rm++)
                        {
                            var room = Group("Room_" + f + rm, scene, floor);
                            if (_r.Chance(0.6))
                            {
                                var chest = NewGo<GameObject>("Chest_" + _goId.ToString(CultureInfo.InvariantCulture), scene, room, "Interactable");
                                chest.Labels.Add("loot");
                                Add(chest, new RendererComponent { Mesh = _meshes.First(m => m.Id == "mesh/chest"), Material = _r.Pick(_materials) });
                                var sc = Add(chest, new ScriptComponent { ScriptName = "Chest" });
                                sc.Properties["locked"] = _r.Chance(0.4);
                                sc.Properties["lootTier"] = (double)_r.Range(1, 5);
                                sc.Properties["keyId"] = _r.Chance(0.3) ? (object?)("key_" + _r.Range(1, 20)) : null;
                            }
                            if (_r.Chance(0.5))
                            {
                                var lamp = NewGo<GameObject>("Candle_" + _goId.ToString(CultureInfo.InvariantCulture), scene, room, "Light");
                                var lc = Add(lamp, new LightComponent { Type = LightType.Point, Intensity = _r.F(0.1, 1.5), Range = _r.F(1, 4) });
                                if (_r.Chance(0.3)) lc.Enabled = false;
                            }
                            var door = NewGo<GameObject>("Door_" + f + rm, scene, room, "Interactable");
                            var ds = Add(door, new ScriptComponent { ScriptName = "Door" });
                            ds.Properties["open"] = _r.Chance(0.5);
                            ds.Properties["requiredKey"] = _r.Chance(0.2) ? (object?)("key_" + _r.Range(1, 20)) : null;
                        }
                    }
                }

                // Characters
                var chars = Group("Characters", scene, null);
                var enemies = Group("Enemies", scene, chars);
                int spawns = 1 + _k / 2 + _r.Next(2);
                for (int sp = 0; sp < spawns; sp++)
                {
                    var spawn = Group("Spawn_" + (char)('A' + sp % 26) + (sp >= 26 ? (sp / 26).ToString(CultureInfo.InvariantCulture) : ""), scene, enemies);
                    int n = _r.Range(2, 4 + _k);
                    int sIdx = _r.Next(Species.Length);
                    for (int e = 1; e <= n; e++)
                    {
                        int species = _r.Chance(0.75) ? sIdx : _r.Next(Species.Length);
                        var kind = (EnemyKind)_r.Next(3);
                        bool boss = _r.Chance(0.03);
                        if (boss) kind = EnemyKind.Boss;
                        var en = NewGo<Enemy>(Species[species] + "_" + (boss ? "Boss_" : "") + _goId.ToString("0000", CultureInfo.InvariantCulture), scene, spawn, "Enemy");
                        en.Layer = 8;
                        en.Kind = kind;
                        en.Faction = SpeciesFaction[species];
                        en.Level = boss ? _r.Range(30, 50) : _r.Range(1, 30);
                        en.MaxHealth = (boss ? 1000 : 50) + en.Level * (boss ? 50 : 10);
                        en.Health = _r.Chance(0.15) ? 0 : _r.Range(1, en.MaxHealth);
                        en.IsElite = !boss && _r.Chance(0.1);
                        en.AggroRange = _r.F(5, 40);
                        en.LastDamageTime = _r.Chance(0.5) ? (double?)null : Math.Round(_r.NextDouble() * 300, 1);
                        Add(en, new RendererComponent { Mesh = _meshes.FirstOrDefault(m => m.Id == "mesh/" + Species[species].ToLowerInvariant()), Material = _r.Pick(_materials) });
                        Add(en, new ColliderComponent { Shape = ColliderShape.Capsule, Size = new Vector3(1, 2, 1) });
                        if (_r.Chance(0.6)) Add(en, new RigidbodyComponent { Mass = _r.F(20, 200), IsKinematic = _r.Chance(0.2) });
                        var ai = Add(en, new ScriptComponent { ScriptName = "EnemyAI" });
                        ai.Properties["state"] = _r.Pick(new[] { "idle", "patrol", "chase", "flee" });
                        ai.Properties["alertness"] = Math.Round(_r.NextDouble(), 2);
                        int drops = _r.Range(0, 3);
                        for (int d = 0; d < drops; d++) en.LootTable.Add(new ItemDrop { Item = _r.Pick(_world.ItemCatalog), Chance = _r.F(0.01, 0.9) });
                        int inv = _r.Range(0, 2);
                        for (int iv = 0; iv < inv; iv++) en.Inventory.Slots.Add(new ItemStack { Item = _r.Pick(_world.ItemCatalog), Count = 1 });
                        if (_r.Chance(0.1)) en.Labels.Add("patrol");
                        if (en.Health == 0) en.Enabled = false;
                    }
                }

                var npcs = Group("NPCs", scene, chars);
                int nNpc = 1 + _k / 2;
                for (int i = 0; i < nNpc; i++)
                {
                    var role = _r.Pick(Npcs);
                    var npc = NewGo<Npc>(role + "_" + _goId.ToString(CultureInfo.InvariantCulture), scene, npcs, "NPC");
                    npc.Faction = role == "Guard" ? Faction.Guards : Faction.Neutral;
                    npc.Level = _r.Range(1, 20);
                    npc.MaxHealth = 100 + npc.Level * 5;
                    npc.Health = npc.MaxHealth;
                    npc.IsMerchant = role == "Merchant" || role == "Blacksmith";
                    npc.Dialogue = "Greetings, traveler. I am the " + role.ToLowerInvariant() + ".";
                    Add(npc, new RendererComponent { Material = _r.Pick(_materials) });
                    if (npc.IsMerchant)
                    {
                        int stock = _r.Range(2, 6);
                        for (int s = 0; s < stock; s++) npc.Inventory.Slots.Add(new ItemStack { Item = _r.Pick(_world.ItemCatalog), Count = _r.Range(1, 5) });
                    }
                }

                if (index == 0)
                {
                    var player = NewGo<Player>("Player", scene, chars, "Player");
                    player.AccountName = "hero_" + _r.Range(100, 999);
                    player.Faction = Faction.Player;
                    player.Level = _r.Range(10, 25);
                    player.MaxHealth = 200 + player.Level * 12;
                    player.Health = _r.Range(player.MaxHealth / 3, player.MaxHealth);
                    player.Gold = _r.Range(50, 5000);
                    player.Inventory.Capacity = 24;
                    int slots = _r.Range(6, 14);
                    for (int s = 0; s < slots; s++)
                    {
                        var item = _r.Pick(_world.ItemCatalog);
                        player.Inventory.Slots.Add(new ItemStack { Item = item, Count = item.Stackable ? _r.Range(1, 12) : 1 });
                    }
                    foreach (var stat in new[] { "str", "dex", "int", "vit", "luck" }) player.Stats[stat] = _r.Range(3, 30);
                    foreach (var q in _world.Quests.Where(q => q.State == QuestState.Active)) player.ActiveQuests.Add(q);
                    Add(player, new ColliderComponent { Shape = ColliderShape.Capsule, Size = new Vector3(1, 2, 1) });
                    Add(player, new RigidbodyComponent { Mass = 80 });
                    var camGo = NewGo<GameObject>("MainCamera", scene, player, "MainCamera");
                    Add(camGo, new CameraComponent { IsMain = true, FieldOfView = 70 });
                    Add(camGo, new AudioSourceComponent { Clip = _clips[4 % _clips.Count], Volume = 0.3f, Loop = true, PlayOnAwake = true });
                    _world.LocalPlayer = player;
                    foreach (var e in Descendants(enemies).OfType<Enemy>()) if (_r.Chance(0.05)) e.Target = player;
                }

                var audio = Group("Ambience", scene, null);
                int nAudio = 1 + _k / 3;
                for (int i = 1; i <= nAudio; i++)
                {
                    var a = NewGo<GameObject>("Ambient_" + i, scene, audio, "Audio");
                    Add(a, new AudioSourceComponent { Clip = _r.Pick(_clips), Loop = true, Volume = _r.F(0.1, 1), PlayOnAwake = _r.Chance(0.8) });
                }
                return scene;
            }

            private void WireQuestGivers()
            {
                var npcs = _world.Scenes.SelectMany(s => s.Objects).SelectMany(Descendants).OfType<Npc>().ToList();
                if (npcs.Count == 0) return;
                foreach (var q in _world.Quests)
                {
                    if (!_r.Chance(0.8)) continue;
                    var giver = _r.Pick(npcs);
                    q.Giver = giver;
                    if (giver.OffersQuest == null) giver.OffersQuest = q;
                }
            }

            // -------------------------------------------------- app data

            public App BuildApp()
            {
                var app = new App();
                int nProducts = 20 + _k * 4;
                for (int i = 1; i <= nProducts; i++)
                {
                    var cat = _r.Pick(Categories);
                    var p = new Product
                    {
                        Sku = cat.Substring(0, 3).ToUpperInvariant() + "-" + i.ToString("0000", CultureInfo.InvariantCulture),
                        Category = cat,
                        Name = _r.Pick(Adjectives) + " " + (cat == "bundles" ? "Bundle" : cat == "cosmetics" ? "Skin" : _r.Pick(Nouns)),
                        Price = Math.Round((decimal)(_r.Range(99, 4999) / 100.0), 2),
                        Stock = _r.Chance(0.1) ? 0 : _r.Range(1, 500),
                    };
                    app.Products[p.Sku] = p;
                }
                var products = app.Products.Values.ToList();
                int nUsers = 15 + _k * 25;
                var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                for (int i = 1; i <= nUsers; i++)
                {
                    var first = _r.Pick(FirstNames);
                    var u = new User
                    {
                        Id = i,
                        Name = first + " " + (char)('A' + _r.Next(26)) + ".",
                        Email = first.ToLowerInvariant() + i.ToString(CultureInfo.InvariantCulture) + "@example." + _r.Pick(new[] { "com", "org", "de", "jp" }),
                        Active = _r.Chance(0.85),
                        CreatedAt = epoch.AddDays(_r.Range(0, 900)).AddMinutes(_r.Range(0, 1439)),
                    };
                    u.Roles.Add("user");
                    if (_r.Chance(0.05)) u.Roles.Add("admin");
                    if (_r.Chance(0.1)) u.Roles.Add("moderator");
                    if (_r.Chance(0.2)) u.Roles.Add("beta");
                    u.Profile.Country = _r.Chance(0.1) ? null : _r.Pick(Countries);
                    u.Profile.BirthYear = _r.Chance(0.3) ? (int?)null : _r.Range(1960, 2008);
                    u.Profile.Newsletter = _r.Chance(0.4);
                    u.Profile.Locale = _r.Pick(new[] { "en-US", "de-DE", "fr-FR", "ja-JP", "uk-UA" });
                    app.Users.Add(u);
                }
                foreach (var u in app.Users)
                {
                    int f = _r.Range(0, 5);
                    for (int i = 0; i < f; i++)
                    {
                        var other = _r.Pick(app.Users);
                        if (other != u && !u.Friends.Contains(other)) { u.Friends.Add(other); if (!other.Friends.Contains(u)) other.Friends.Add(u); }
                    }
                }
                int nOrders = nUsers * 2;
                for (int i = 1; i <= nOrders; i++)
                {
                    var o = new Order
                    {
                        Id = 10000 + i,
                        Customer = _r.Pick(app.Users),
                        Status = (OrderStatus)_r.Next(6),
                        PlacedAt = epoch.AddDays(_r.Range(30, 1000)).AddMinutes(_r.Range(0, 1439)),
                    };
                    int lines = _r.Range(1, 4);
                    for (int l = 0; l < lines; l++)
                    {
                        var p = _r.Pick(products);
                        o.Lines.Add(new OrderLine { Product = p, Quantity = _r.Range(1, 3), UnitPrice = p.Price });
                    }
                    app.Orders.Add(o);
                }
                return app;
            }
        }

        public static IEnumerable<GameObject> Descendants(GameObject root)
        {
            var stack = new Stack<GameObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var g = stack.Pop();
                yield return g;
                for (int i = g.Children.Count - 1; i >= 0; i--) stack.Push(g.Children[i]);
            }
        }

        public static IEnumerable<GameObject> AllObjects(World w) => w.Scenes.SelectMany(s => s.Objects).SelectMany(Descendants);
    }
}
