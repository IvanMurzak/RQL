using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rql.Execution;

namespace Rql.Conformance
{
    /// <summary>The test adapter set specified in fixtures/adapters/cases.json ("adapters"); every runtime implements the same behaviour.</summary>
    public static class TestAdapters
    {
        public static void Register(RqlEngine engine)
        {
            engine.AddAdapter(new Vec2Adapter());
            engine.AddAdapter(new NodeAdapter());
            engine.AddAdapter(new SpecialAdapter());
        }

        private static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

        private static string AsciiCase(string s, bool upper)
        {
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                if (upper && c[i] >= 'a' && c[i] <= 'z') c[i] = (char)(c[i] - 32);
                else if (!upper && c[i] >= 'A' && c[i] <= 'Z') c[i] = (char)(c[i] + 32);
            }
            return new string(c);
        }

        public sealed class Vec2Adapter : RqlTypeAdapter<Vec2>
        {
            private static readonly Regex Text = new Regex(@"^\(\s*([-+0-9.eE]+)\s*,\s*([-+0-9.eE]+)\s*\)$", RegexOptions.CultureInvariant);

            public override string Description => "Vec2: write as \"(x, y)\" or [x, y]";

            public override JsonNode? LiteralSchema => JsonNode.Parse(
                "{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},"
                + "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}}}]}");

            protected override void Members(MemberTable<Vec2> members) =>
                members.Add("len", v => Length(v), description: "vector length sqrt(x*x+y*y)");

            protected override JsonNode? Encode(Vec2 v, EncodeContext context) => JsonValue.Create("(" + Num(v.x) + ", " + Num(v.y) + ")");

            public override AdapterOutcome TryCoerce(object? value, Type target, RqlKernel kernel, out object? result, out string? reason)
            {
                reason = null;
                result = null;
                // Object literals {x, y} are left to the default construction (spec §4.5).
                if (value is ObjectValue) return AdapterOutcome.NotHandled;
                if (TryVec(value, out var v)) { result = v; return AdapterOutcome.Ok; }
                result = null;
                reason = "expected (x, y) or [x, y]";
                return AdapterOutcome.Failed;
            }

            protected override bool? Equal(Vec2 value, object? other, RqlKernel kernel) =>
                TryVec(other, out var o) ? Math.Abs(value.x - o.x) <= 1e-9 && Math.Abs(value.y - o.y) <= 1e-9 : (bool?)null;

            protected override int? Order(Vec2 value, object? other, RqlKernel kernel) =>
                TryVec(other, out var o) ? Length(value).CompareTo(Length(o)) : (int?)null;

            private static double Length(Vec2 v) => Math.Sqrt(v.x * v.x + v.y * v.y);

            private static bool TryVec(object? value, out Vec2 v)
            {
                v = default;
                switch (value)
                {
                    case Vec2 already:
                        v = already;
                        return true;
                    case string s:
                    {
                        var m = Text.Match(s);
                        if (!m.Success) return false;
                        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
                        if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
                        v = new Vec2 { x = x, y = y };
                        return true;
                    }
                    case IList<object?> list when list.Count == 2 && list[0] is double lx && list[1] is double ly:
                        v = new Vec2 { x = lx, y = ly };
                        return true;
                    default:
                        return false;
                }
            }
        }

        public sealed class NodeAdapter : RqlTypeAdapter<Node>
        {
            public override string Description => "Node: a conformance graph node";

            protected override void Members(MemberTable<Node> members) =>
                members
                    .Add("childCount", n => n.Children.Count, description: "number of direct children")
                    .Add("title", n => AsciiCase(n.Name, upper: true), (n, v) =>
                        {
                            if (n.Id == 3) throw new InvalidOperationException("node 3 is locked");
                            n.Name = AsciiCase(v, upper: false);
                        },
                        validate: (n, v) => string.IsNullOrEmpty(v) ? "title must not be empty" : null,
                        description: "upper-cased Name; assigning sets Name lower-cased")
                    .Hide("Note")
                    .ExplicitOnly("Children")
                    .Map("stats", n => n.Props.Keys, (n, k) => n.Props[k], (n, k, v) => n.Props[k] = v,
                        validate: (n, k, v) => !n.Props.ContainsKey(k) ? $"unknown stat '{k}'" : v < 0 ? "stats must be non-negative" : null,
                        description: "Props as a validated map")
                    .Map("labels", n => Labels, (n, k) => k == "name" ? n.Name : n.Kind.ToString(), description: "read-only labels")
                    .Function<string, string>("greet", (n, who) => "hi " + who + ", I am " + n.Name, "who", "a greeting (no state change)")
                    .Function<string, string>("fail", (n, why) => throw new InvalidOperationException(why), "why", "always throws (call-threw)")
                    .Add("tagsCopy", n => new List<string>(n.Tags), (n, v) => n.Tags = v, description: "a copy of Tags (copy-on-read)")
                    .CopyOnRead("tagsCopy")
                    .Untyped.AddMixedMap("fields", n => FieldNames, (n, k) => FieldType(k),
                        (n, k) => k == "Name" ? ((Node)n).Name : k == "Value" ? ((Node)n).Value : (object)((Node)n).Enabled,
                        (n, k, v) => { var node = (Node)n; if (k == "Name") node.Name = (string)v!; else if (k == "Value") node.Value = (double)v!; else node.Enabled = (bool)v!; },
                        description: "Name / Value / Enabled with per-key types")
                    .AddMethod("bump", new[] { new CallableParameter("by", typeof(double), true, 1.0) }, typeof(double),
                        (o, args) => { var n = (Node)o; n.Value += (double)args[0]!; return n.Value; }, "Value += by; returns the new Value");

            private static readonly string[] Labels = { "name", "kind" };
            private static readonly string[] FieldNames = { "Name", "Value", "Enabled" };
            private static Type FieldType(string k) => k == "Name" ? typeof(string) : k == "Value" ? typeof(double) : typeof(bool);

            // Destroyed nodes (Tags contain "destroyed") read as null (spec §13 liveness).
            protected override bool? Alive(Node n) => !n.Tags.Contains("destroyed");
        }

        public sealed class SpecialAdapter : RqlTypeAdapter<Special>
        {
            protected override void Members(MemberTable<Special> members) =>
                members.Add("powerLevel", s => s.Power / 1000.0, description: "Power / 1000").Display("title");
        }
    }
}
