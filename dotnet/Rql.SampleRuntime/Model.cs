using System;
using System.Collections.Generic;
using System.Linq;

// Miniature game world + generic application data used as the RQL test/evaluation sandbox.
// Deliberately exercises: fields vs properties, structs (write-back), enums, nullable values,
// polymorphic collections, List/array/HashSet/Dictionary, parent/child cycles, shared references,
// computed read-only members, callable methods and explicit destruction (stale refs).
namespace Rql.Sample
{
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() => $"({x}, {y}, {z})";
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; }
    }

    public enum LightType { Point, Spot, Directional, Area }
    public enum Faction { Neutral, Player, Bandits, Undead, Wildlife, Guards }
    public enum ItemRarity { Common, Uncommon, Rare, Epic, Legendary }
    public enum QuestState { Locked, Available, Active, Completed, Failed }
    public enum AssetType { Texture, Material, Mesh, Audio, Prefab, Script }
    public enum EnemyKind { Melee, Ranged, Caster, Boss }
    public enum ColliderShape { Box, Sphere, Capsule, Mesh }
    public enum Quality { Low, Medium, High, Ultra }
    public enum OrderStatus { Pending, Paid, Shipped, Delivered, Cancelled, Refunded }

    [RqlDescription("Root of the game world: scenes, settings, assets, quests and the item catalog.")]
    public sealed class World
    {
        public string Name { get; set; } = "";
        public int Seed { get; set; }
        public long Tick { get; set; }
        public List<Scene> Scenes { get; set; } = new List<Scene>();
        public Settings Settings { get; set; } = new Settings();
        public Dictionary<string, Asset> Assets { get; set; } = new Dictionary<string, Asset>();
        public List<Quest> Quests { get; set; } = new List<Quest>();
        public List<Item> ItemCatalog { get; set; } = new List<Item>();
        [RqlReference] public Player? LocalPlayer { get; set; }
    }

    public sealed class Scene
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool Loaded { get; set; }
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);
        [RqlDescription("Root game objects of the scene (children are nested under GameObject.Children).")]
        public List<GameObject> Objects { get; set; } = new List<GameObject>();
        [RqlReference] public World? World { get; set; }

        [RqlCallable("Create an empty root GameObject named name at position (x, y, z) in this scene; returns it.")]
        public GameObject CreateObject(string name, float x = 0, float y = 0, float z = 0)
        {
            int id = (World != null ? WorldGenerator.AllObjects(World) : Objects.SelectMany(WorldGenerator.Descendants)).Select(g => g.Id).DefaultIfEmpty(0).Max() + 1;
            var go = new GameObject { Id = id, Name = name, Scene = this };
            go.Transform.Position = new Vector3(x, y, z);
            Objects.Add(go);
            return go;
        }

        public string? ValidateCreateObject(string name, float x, float y, float z) => string.IsNullOrWhiteSpace(name) ? "name must not be empty" : null;

        // Spawn overloads (ADR-0010): an existing parent, a placement, or named options with defaults.
        [RqlCallable("Spawn an object named name as a child of an existing object parent (pass it with ref(...)); returns it.")]
        public GameObject Spawn(string name, GameObject parent)
        {
            var go = CreateObject(name);
            go.SetParent(parent);
            return go;
        }

        [RqlCallable("Spawn a root object named name at a placement {Position: {x, y, z}, Tag}; returns it.")]
        public GameObject Spawn(string name, Placement placement)
        {
            var go = CreateObject(name, placement.Position.x, placement.Position.y, placement.Position.z);
            go.Tag = placement.Tag;
            return go;
        }

        [RqlCallable("Spawn a root object named name with optional tag, layer and position x, y, z; pass them by name: Spawn({name: \"A\", layer: 2, y: 5}). Returns it.")]
        public GameObject Spawn(string name, string tag = "Untagged", int layer = 0, float x = 0, float y = 0, float z = 0)
        {
            var go = CreateObject(name, x, y, z);
            go.Tag = tag;
            go.Layer = layer;
            return go;
        }

        public string? ValidateSpawn(string name, string tag, int layer, float x, float y, float z) =>
            string.IsNullOrWhiteSpace(name) ? "name must not be empty" : layer < 0 || layer > 31 ? "layer must be 0 to 31" : null;
    }

    [RqlDescription("Where Scene.Spawn places a new root object.")]
    public sealed class Placement
    {
        public Vector3 Position { get; set; }
        public string Tag { get; set; } = "Untagged";
    }

    public class GameObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Tag { get; set; } = "Untagged";
        public int Layer { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsStatic { get; set; }
        public Transform Transform { get; set; } = new Transform();
        public List<Component> Components { get; set; } = new List<Component>();
        public List<GameObject> Children { get; set; } = new List<GameObject>();
        public HashSet<string> Labels { get; set; } = new HashSet<string>();
        [RqlReference] public GameObject? Parent { get; set; }
        [RqlReference] public Scene? Scene { get; set; }
        [RqlIgnore] public bool Destroyed { get; private set; }

        [RqlCallable("Destroy this object and all its children; removes it from its parent or scene.")]
        public void Destroy()
        {
            foreach (var c in Children.ToList()) c.Destroy();
            Destroyed = true;
            if (Parent != null) Parent.Children.Remove(this);
            else Scene?.Objects.Remove(this);
        }

        [RqlCallable("Re-parent this object under another object (by $ref via ref(...)).")]
        public void SetParent(GameObject newParent)
        {
            if (newParent == this) throw new InvalidOperationException("cannot parent an object to itself");
            for (var p = newParent; p != null; p = p.Parent)
                if (p == this) throw new InvalidOperationException("cannot parent an object under its own descendant");
            if (Parent != null) Parent.Children.Remove(this);
            else Scene?.Objects.Remove(this);
            Parent = newParent;
            Scene = newParent.Scene;
            newParent.Children.Add(this);
        }

        public T? GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault();
        public override string ToString() => GetType().Name + "#" + Id + " " + Name;
    }

    public sealed class Transform
    {
        public Vector3 Position { get; set; }
        [RqlDescription("Euler angles in degrees.")]
        public Vector3 Rotation { get; set; }
        public Vector3 Scale { get; set; } = new Vector3(1, 1, 1);
    }

    public abstract class Component
    {
        public int Id { get; set; }
        public bool Enabled { get; set; } = true;
        [RqlReference] public GameObject? Owner { get; set; }
    }

    public sealed class LightComponent : Component
    {
        public LightType Type { get; set; }
        public Color Color { get; set; } = new Color(1, 1, 1);
        public float Intensity { get; set; } = 1;
        public float Range { get; set; } = 10;
        public bool CastShadows { get; set; }
    }

    public sealed class RendererComponent : Component
    {
        [RqlReference] public Asset? Material { get; set; }
        [RqlReference] public Asset? Mesh { get; set; }
        public bool CastShadows { get; set; } = true;
        public int SortingOrder { get; set; }
    }

    public sealed class ColliderComponent : Component
    {
        public ColliderShape Shape { get; set; }
        public bool IsTrigger { get; set; }
        public Vector3 Size { get; set; } = new Vector3(1, 1, 1);
    }

    public sealed class AudioSourceComponent : Component
    {
        [RqlReference] public Asset? Clip { get; set; }
        public float Volume { get; set; } = 1;
        public bool Loop { get; set; }
        public bool PlayOnAwake { get; set; }
    }

    public sealed class ScriptComponent : Component
    {
        public string ScriptName { get; set; } = "";
        [RqlDescription("Serialized script fields; values are numbers, strings, bools or null.")]
        public Dictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class CameraComponent : Component
    {
        public float FieldOfView { get; set; } = 60;
        public float Near { get; set; } = 0.3f;
        public float Far { get; set; } = 1000;
        public bool IsMain { get; set; }
    }

    public sealed class RigidbodyComponent : Component
    {
        public float Mass { get; set; } = 1;
        public bool UseGravity { get; set; } = true;
        public bool IsKinematic { get; set; }
        public Vector3 Velocity { get; set; }
    }

    public class Character : GameObject
    {
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public int Level { get; set; } = 1;
        public Faction Faction { get; set; }
        public Inventory Inventory { get; set; } = new Inventory();
        [RqlDescription("Seconds since the character last took damage; null if never.")]
        public double? LastDamageTime { get; set; }

        [RqlCallable("Heal by amount, capped at MaxHealth. Returns the new Health.")]
        public int Heal(int amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "amount must be >= 0");
            Health = Math.Min(MaxHealth, Health + amount);
            return Health;
        }

        [RqlCallable("Apply damage (>= 0). Health does not go below 0. Returns the new Health.")]
        public int TakeDamage(int amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "amount must be >= 0");
            Health = Math.Max(0, Health - amount);
            LastDamageTime = 0;
            return Health;
        }
    }

    public sealed class Player : Character
    {
        public string AccountName { get; set; } = "";
        public int Gold { get; set; }
        [RqlReference] public List<Quest> ActiveQuests { get; set; } = new List<Quest>();
        public Dictionary<string, int> Stats { get; set; } = new Dictionary<string, int>();
    }

    public sealed class Enemy : Character
    {
        public EnemyKind Kind { get; set; }
        public float AggroRange { get; set; }
        public bool IsElite { get; set; }
        [RqlReference] public Character? Target { get; set; }
        public List<ItemDrop> LootTable { get; set; } = new List<ItemDrop>();
    }

    public sealed class Npc : Character
    {
        public string Dialogue { get; set; } = "";
        public bool IsMerchant { get; set; }
        [RqlReference] public Quest? OffersQuest { get; set; }
    }

    public sealed class Inventory
    {
        public int Capacity { get; set; } = 20;
        public List<ItemStack> Slots { get; set; } = new List<ItemStack>();
        [RqlDescription("Computed: number of occupied slots.")]
        public int UsedSlots => Slots.Count;
    }

    public sealed class ItemStack
    {
        [RqlReference] public Item? Item { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class ItemDrop
    {
        [RqlReference] public Item? Item { get; set; }
        [RqlDescription("Drop probability 0..1.")]
        public float Chance { get; set; }
    }

    public sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public ItemRarity Rarity { get; set; }
        public int Value { get; set; }
        public float Weight { get; set; }
        public bool Stackable { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
    }

    public sealed class Quest
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public QuestState State { get; set; }
        [RqlDescription("Minimum player level; null = no requirement.")]
        public int? RequiredLevel { get; set; }
        public List<Objective> Objectives { get; set; } = new List<Objective>();
        [RqlReference] public Item? Reward { get; set; }
        public int RewardGold { get; set; }
        [RqlReference] public Npc? Giver { get; set; }
    }

    public sealed class Objective
    {
        public string Description { get; set; } = "";
        public int Progress { get; set; }
        public int Target { get; set; } = 1;
        [RqlDescription("Computed: Progress >= Target.")]
        public bool Done => Progress >= Target;
    }

    public sealed class Asset
    {
        [RqlId] public string Id { get; set; } = "";
        public string Path { get; set; } = "";
        public AssetType Type { get; set; }
        public long SizeBytes { get; set; }
        public bool IsLoaded { get; set; }
        [RqlReference] public List<Asset> Dependencies { get; set; } = new List<Asset>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    public sealed class Settings
    {
        public GraphicsSettings Graphics { get; set; } = new GraphicsSettings();
        public AudioSettings Audio { get; set; } = new AudioSettings();
        public Dictionary<string, bool> Features { get; set; } = new Dictionary<string, bool>();
        [RqlDescription("Frame cap; null = unlimited.")]
        public int? TargetFps { get; set; }
        public string Language { get; set; } = "en";
    }

    public sealed class GraphicsSettings
    {
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public bool Fullscreen { get; set; } = true;
        public bool VSync { get; set; }
        public Quality Quality { get; set; } = Quality.High;
        public float RenderScale { get; set; } = 1;
    }

    public sealed class AudioSettings
    {
        public float MasterVolume { get; set; } = 1;
        public float MusicVolume { get; set; } = 0.8f;
        public float SfxVolume { get; set; } = 0.9f;
        public bool Muted { get; set; }
    }

    // ---------------------------------------------------------------- application data

    [RqlDescription("Generic application data: users, orders and the product catalog.")]
    public sealed class App
    {
        public string Name { get; set; } = "Shop";
        public List<User> Users { get; set; } = new List<User>();
        public List<Order> Orders { get; set; } = new List<Order>();
        public Dictionary<string, Product> Products { get; set; } = new Dictionary<string, Product>();
    }

    public sealed class User
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public HashSet<string> Roles { get; set; } = new HashSet<string>();
        public Profile Profile { get; set; } = new Profile();
        [RqlReference] public List<User> Friends { get; set; } = new List<User>();
    }

    public sealed class Profile
    {
        public string? Country { get; set; }
        public int? BirthYear { get; set; }
        public string Locale { get; set; } = "en-US";
        public bool Newsletter { get; set; }
    }

    public sealed class Order
    {
        public int Id { get; set; }
        [RqlReference] public User? Customer { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime PlacedAt { get; set; }
        public List<OrderLine> Lines { get; set; } = new List<OrderLine>();
        [RqlDescription("Computed: sum of Quantity * UnitPrice over Lines.")]
        public decimal Total => Lines.Sum(l => l.Quantity * l.UnitPrice);
    }

    public sealed class OrderLine
    {
        [RqlReference] public Product? Product { get; set; }
        public int Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; }
    }

    public sealed class Product
    {
        [RqlId] public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal Price { get; set; }
        public int Stock { get; set; }
    }
}
