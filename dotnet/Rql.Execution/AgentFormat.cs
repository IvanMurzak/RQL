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
        public static string Cell(JsonNode? n)
        {
            switch (n)
            {
                case null: return "null";
                case JsonValue v:
                    if (v.TryGetValue<string>(out var s))
                        return s.Length == 0 || s.IndexOfAny(new[] { '|', '\n', '\r' }) >= 0 || s == "null" || s.Trim() != s ? JsonSerializer.Serialize(s) : s;
                    return v.ToJsonString();
                case JsonObject o when o["$ref"] is JsonValue rv && rv.TryGetValue<string>(out var r):
                    return o.TryGetPropertyValue("name", out var name) && name is JsonValue nv && nv.TryGetValue<string>(out var ns) ? r + "(" + ns + ")" : r;
                case JsonObject o when o["$error"] is JsonValue ev && ev.TryGetValue<string>(out var code):
                    return "error(" + code + ")";
                case JsonObject o:
                    return "{" + string.Join(",", o.Select(kv => kv.Key + ":" + Cell(kv.Value))) + "}";
                case JsonArray a:
                    return "[" + string.Join(",", a.Select(Cell)) + "]";
                default:
                    return n.ToJsonString();
            }
        }

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

        public static string FormatNumber(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    }
}
