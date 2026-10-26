using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rql.Execution
{
    public enum OutputFormat
    {
        /// <summary>QueryResult JSON (conformance form) without runtime stats.</summary>
        Json,
        /// <summary>Compact text: header line, '|'-separated columns, one row per line.</summary>
        Table,
    }

    /// <summary>Agent-facing renderings of results and schemas (tool layer, not conformance).</summary>
    public static class AgentFormat
    {
        public static OutputFormat Parse(string s) => s.Equals("json", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Json : OutputFormat.Table;

        public static string Render(QueryResult r, OutputFormat format)
        {
            if (format == OutputFormat.Json) return r.ToJson(indented: false, includeStats: false);
            var sb = new StringBuilder();
            foreach (var d in r.Diagnostics)
            {
                sb.Append(d.Severity == DiagnosticSeverity.Error ? "error " : "warning ").Append(d.Code).Append(": ").Append(d.Message);
                if (d.Hint != null) sb.Append(" — hint: ").Append(d.Hint);
                sb.Append('\n');
            }
            if (!r.Ok) return sb.ToString().TrimEnd();
            switch (r.Kind)
            {
                case ResultKind.Scalar:
                    sb.Append(Cell(r.Value));
                    break;
                case ResultKind.Rows:
                    Header(sb, r, r.Rows!.Count, r.DryRun ? "DRY RUN — nothing changed; " : "");
                    sb.Append(string.Join("|", r.Columns!)).Append('\n');
                    foreach (var row in r.Rows!) sb.Append(string.Join("|", row.Select(Cell))).Append('\n');
                    foreach (var e in r.CellErrors) sb.Append("note: error cell ").Append(e).Append('\n');
                    break;
                case ResultKind.Items:
                    Header(sb, r, r.Items!.Count, "");
                    foreach (var it in r.Items!) sb.Append(Cell(it)).Append('\n');
                    break;
                case ResultKind.Schema:
                    foreach (var t in r.Types!) AppendType(sb, (JsonObject)t!, null);
                    break;
            }
            return sb.ToString().TrimEnd();
        }

        private static void Header(StringBuilder sb, QueryResult r, int shown, string prefix)
        {
            long total = r.Total ?? shown;
            sb.Append(prefix);
            if (r.Truncated) sb.Append(shown).Append(" of ").Append(total).Append(" (truncated; page with skip N | first M, or count)");
            else sb.Append(total).Append(total == 1 ? " result" : " results");
            sb.Append('\n');
        }

        /// <summary>One value as compact text: strings raw (quoted only when ambiguous), objects "Type#id(Name)".</summary>
        public static string Cell(JsonNode? n) => Cell(n, nested: false);

        // Inside {…} / […] a string is also quoted when it holds a separator or a space, so the value reads back unambiguously
        // ({description:"List tools, filter by name"} rather than {description:List tools, filter by name}).
        // Agent-facing text, not HTML: '+', '<', '&' and non-ASCII stay as they are inside quotes.
        /// <summary>
        /// A JSON string literal that escapes only what JSON requires (<c>"</c>, <c>\</c>, control characters) and keeps every
        /// other character raw, emoji included (System.Text.Json's encoders escape non-BMP characters as surrogate pairs).
        /// </summary>
        internal static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static readonly char[] NestedSeparators = { ',', ':', '{', '}', '[', ']', '"', ' ', '\t' };

        private static string Cell(JsonNode? n, bool nested)
        {
            switch (n)
            {
                case null: return "null";
                case JsonValue v:
                    if (v.TryGetValue<string>(out var s))
                        return s.Length == 0 || s.IndexOfAny(new[] { '|', '\n', '\r' }) >= 0 || s == "null" || s.Trim() != s
                               || (nested && (s.IndexOfAny(NestedSeparators) >= 0 || s == "true" || s == "false" || LooksNumeric(s)))
                            ? Quote(s) : s;
                    var json = v.ToJsonString();
                    // Display numbers (spec §6): fractional values with at most 15 significant digits (0.1 + 0.2 → 0.3).
                    return json.Length > 0 && (json[0] == '-' || char.IsDigit(json[0])) && json.IndexOfAny(FractionMarks) >= 0
                           && double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? FormatNumber(d) : json;
                case JsonObject o when o["$ref"] is JsonValue rv && rv.TryGetValue<string>(out var r):
                    return o.TryGetPropertyValue("name", out var name) && name is JsonValue nv && nv.TryGetValue<string>(out var ns) ? r + "(" + ns + ")" : r;
                case JsonObject o when o["$error"] is JsonValue ev && ev.TryGetValue<string>(out var code):
                    return "error(" + code + ")";
                case JsonObject o:
                    return "{" + string.Join(",", o.Select(kv => Key(kv.Key) + ":" + Cell(kv.Value, nested: true))) + "}";
                case JsonArray a:
                    return "[" + string.Join(",", a.Select(x => Cell(x, nested: true))) + "]";
                default:
                    return n.ToJsonString();
            }
        }

        // Keys that are not plain identifiers ("tool-12", "a b", "1x") are quoted so the object reads back unambiguously.
        private static string Key(string key)
        {
            bool plain = key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_' || key[0] == '$');
            for (int i = 1; plain && i < key.Length; i++) plain = char.IsLetterOrDigit(key[i]) || key[i] == '_' || key[i] == '$';
            return plain ? key : Quote(key);
        }

        private static bool LooksNumeric(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

        // ------------------------------------------------------------ schema

        /// <summary>Compact schema: sources, then each type once (derived types list only their own members).</summary>
        public static string Schema(RqlEngine engine, string? typeFilter = null)
        {
            var types = engine.DescribeTypes();
            var sb = new StringBuilder();
            if (typeFilter == null)
            {
                sb.Append("sources:\n");
                foreach (var s in engine.Sources.Values)
                {
                    object? v;
                    try { v = s.Getter(); } catch { v = null; }
                    sb.Append("  ").Append(s.Name).Append(": ").Append(v == null ? "?" : SourceTypeName(engine, v.GetType()));
                    if (s.Description != null) sb.Append(" — ").Append(s.Description);
                    sb.Append('\n');
                }
                foreach (var f in engine.Functions.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    sb.Append("  ").Append(f).Append("(…)").Append(engine.FunctionDescriptions.TryGetValue(f, out var fd) ? " — " + fd : "").Append('\n');
                sb.Append("types (members: name type; ").Append(engine.Mutations.Enabled ? "ro = read-only; " : "").Append("derived types list only added members):\n");
            }
            // Short names are not unique across namespaces (two FilterMode enums): the first one wins for base lookups.
            var byName = new Dictionary<string, JsonObject>();
            foreach (var t in types) byName.TryAdd(t["name"]!.GetValue<string>(), t);
            var shown = types.Where(t => typeFilter == null || string.Equals(t["name"]!.GetValue<string>(), typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (typeFilter != null && shown.Count == 0) return UnknownTypeError(typeFilter, byName.Keys.ToList());
            foreach (var t in shown)
            {
                JsonObject? baseType = null;
                if (t.TryGetPropertyValue("base", out var b) && b != null) byName.TryGetValue(b.GetValue<string>(), out baseType);
                AppendType(sb, t, typeFilter == null ? baseType : null, engine.Mutations.Enabled);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// One type in the compact schema form: <c>Name : Base {member type ro, …} methods: … — description</c>. Members that
        /// <paramref name="inheritedFrom"/> also has are left out (hosts summarise shared base members once).
        /// </summary>
        public static string TypeSchema(RqlEngine engine, Type type, Type? inheritedFrom = null, bool markExplicitOnly = false)
        {
            var sb = new StringBuilder();
            var t = Describe.TypeDescriptor(engine.Types, type, engine.Mutations.Enabled);
            var baseType = inheritedFrom == null ? null : Describe.TypeDescriptor(engine.Types, inheritedFrom, engine.Mutations.Enabled);
            AppendType(sb, t, baseType, engine.Mutations.Enabled, markExplicitOnly);
            return sb.ToString().TrimEnd();
        }

        /// <summary>The <c>unknown-type</c> error line with the closest <paramref name="candidates"/> as suggestions.</summary>
        public static string UnknownTypeError(string typeName, IReadOnlyCollection<string> candidates)
        {
            var close = Suggest.Closest(typeName, candidates, 3);
            return $"error unknown-type: no type '{typeName}'" + (close.Count > 0 ? " — did you mean " + string.Join(" / ", close) + "?" : "");
        }

        private static string SourceTypeName(RqlEngine engine, Type t)
        {
            var info = engine.Types.Info(t);
            if (info.IsList && (!t.IsPublic || t.IsNested) && info.ElementType != null) return "list of " + TypeModel.FriendlyName(info.ElementType);
            return TypeModel.FriendlyName(t);
        }

        private static void AppendType(StringBuilder sb, JsonObject t, JsonObject? baseType, bool markReadOnly = false, bool markExplicitOnly = false)
        {
            var name = t["name"]!.GetValue<string>();
            var kind = t["kind"]!.GetValue<string>();
            if (kind == "enum")
            {
                sb.Append("enum ").Append(name).Append(": ").Append(string.Join(" ", ((JsonArray)t["values"]!).Select(v => v!.GetValue<string>()))).Append('\n');
                return;
            }
            sb.Append(kind == "struct" ? "struct " : "").Append(name);
            if (t.TryGetPropertyValue("base", out var b) && b != null) sb.Append(" : ").Append(b.GetValue<string>());
            sb.Append(" {");
            var inherited = new HashSet<string>(baseType == null ? Enumerable.Empty<string>() : AllMemberNames(baseType));
            var parts = new List<string>();
            foreach (var m in (JsonArray)t["members"]!)
            {
                var mo = (JsonObject)m!;
                var mn = mo["name"]!.GetValue<string>();
                if (baseType != null && inherited.Contains(mn)) continue;
                var part = mn + " " + mo["type"]!.GetValue<string>();
                if (markReadOnly && !(mo.TryGetPropertyValue("writable", out var w) && w != null && w.GetValue<bool>())) part += " ro";
                if (markExplicitOnly && mo.ContainsKey("explicitOnly")) part += " explicit-only";
                if (mo.TryGetPropertyValue("description", out var md) && md != null) part += " (" + md.GetValue<string>() + ")";
                parts.Add(part);
            }
            sb.Append(string.Join(", ", parts)).Append('}');
            if (t.TryGetPropertyValue("methods", out var ms) && ms is JsonArray arr && arr.Count > 0)
            {
                var inheritedMethods = new HashSet<string>(baseType != null && baseType.TryGetPropertyValue("methods", out var bm) && bm is JsonArray barr
                    ? barr.Select(x => x!.GetValue<string>()) : Enumerable.Empty<string>());
                var own = arr.Select(x => x!.GetValue<string>()).Where(m => !inheritedMethods.Contains(m)).ToList();
                if (own.Count > 0) sb.Append(baseType != null ? " +methods: " : " methods: ").Append(string.Join(" | ", own));
            }
            if (t.TryGetPropertyValue("description", out var d) && d != null) sb.Append(" — ").Append(d.GetValue<string>());
            sb.Append('\n');
        }

        private static IEnumerable<string> AllMemberNames(JsonObject t) =>
            ((JsonArray)t["members"]!).Select(m => ((JsonObject)m!)["name"]!.GetValue<string>());

        private static readonly char[] FractionMarks = { '.', 'e', 'E' };

        /// <summary>A display number (spec §6): at most 15 significant digits; NaN / Infinity / -Infinity as such.</summary>
        public static string FormatNumber(double d) => d.ToString("G15", CultureInfo.InvariantCulture);
    }
}
