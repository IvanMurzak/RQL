using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Rql.Conformance
{
    // Native CLR mirror of fixtures/cross-runtime/graph.json "types" (declaration order matters).
    public enum NodeKind { Alpha, Beta, Gamma }

    public struct Vec2
    {
        public double x;
        public double y;
    }

    public class Node
    {
        [RqlReadOnly] public int Id { get; set; }
        public string Name { get; set; } = "";
        public NodeKind Kind { get; set; }
        public double Value { get; set; }
        public bool Enabled { get; set; }
        public string? Note { get; set; }
        public Vec2 Pos { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, double> Props { get; set; } = new Dictionary<string, double>();
        public List<Node> Children { get; set; } = new List<Node>();
        [RqlReference] public Node? Parent { get; set; }
        [RqlReference] public Node? Peer { get; set; }
    }

    public sealed class Special : Node
    {
        public int Power { get; set; }
    }

    public sealed class Graph
    {
        public string Name { get; set; } = "";
        public int Version { get; set; }
        public List<Node> Nodes { get; set; } = new List<Node>();
        [RqlReference] public Dictionary<string, Node> Index { get; set; } = new Dictionary<string, Node>();
        public List<Node> Empty { get; set; } = new List<Node>();
        [RqlReference] public Node? Nothing { get; set; }
    }

    /// <summary>Builds the native graph from the portable JSON description (two passes: objects, then links).</summary>
    public static class GraphLoader
    {
        public static Graph Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement.GetProperty("root");
            var byId = new Dictionary<int, Node>();
            var links = new List<(JsonElement el, Node node)>();
            var g = new Graph
            {
                Name = root.GetProperty("Name").GetString()!,
                Version = root.GetProperty("Version").GetInt32(),
            };
            foreach (var n in root.GetProperty("Nodes").EnumerateArray()) g.Nodes.Add(BuildNode(n, byId, links));
            foreach (var (el, node) in links)
            {
                node.Parent = Link(el.GetProperty("Parent"), byId);
                node.Peer = Link(el.GetProperty("Peer"), byId);
            }
            foreach (var p in root.GetProperty("Index").EnumerateObject()) g.Index[p.Name] = Link(p.Value, byId)!;
            g.Nothing = Link(root.GetProperty("Nothing"), byId);
            return g;
        }

        private static Node? Link(JsonElement e, Dictionary<int, Node> byId) =>
            e.ValueKind == JsonValueKind.Null ? null : byId[e.GetProperty("$link").GetInt32()];

        private static Node BuildNode(JsonElement e, Dictionary<int, Node> byId, List<(JsonElement, Node)> links)
        {
            var type = e.GetProperty("$type").GetString();
            Node n = type == "Special" ? new Special { Power = e.GetProperty("Power").GetInt32() } : new Node();
            n.Id = e.GetProperty("Id").GetInt32();
            n.Name = e.GetProperty("Name").GetString()!;
            n.Kind = (NodeKind)System.Enum.Parse(typeof(NodeKind), e.GetProperty("Kind").GetString()!);
            n.Value = e.GetProperty("Value").GetDouble();
            n.Enabled = e.GetProperty("Enabled").GetBoolean();
            var note = e.GetProperty("Note");
            n.Note = note.ValueKind == JsonValueKind.Null ? null : note.GetString();
            var pos = e.GetProperty("Pos");
            n.Pos = new Vec2 { x = pos.GetProperty("x").GetDouble(), y = pos.GetProperty("y").GetDouble() };
            n.Tags = e.GetProperty("Tags").EnumerateArray().Select(t => t.GetString()!).ToList();
            foreach (var p in e.GetProperty("Props").EnumerateObject()) n.Props[p.Name] = p.Value.GetDouble();
            foreach (var c in e.GetProperty("Children").EnumerateArray()) n.Children.Add(BuildNode(c, byId, links));
            byId[e.GetProperty("$id").GetInt32()] = n;
            links.Add((e, n));
            return n;
        }
    }
}
