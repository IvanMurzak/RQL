using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rql.Execution;
using Rql.Sample;

namespace Rql.Eval.Agent
{
    /// <summary>
    /// Comparison baseline: a conventional reflection tool surface in the style of ReflectorNet / Unity-MCP
    /// object tools — find by name, dump an object as JSON to a depth, set a member, call a method,
    /// describe a type. No query language is exposed to the model; paths address objects.
    /// </summary>
    public static class LegacyTools
    {
        public const string Prompt =
@"You operate on a live game world (plus application data) through reflection tools.
Tools:
  find_objects(name_contains, type?)   search every GameObject (incl. Player/Enemy/Npc) by name substring (case-insensitive); returns path, $ref, name, type (max 100)
  get_object(path, depth?)             JSON dump of the object/collection at path; depth (default 1, max 3) controls how far nested objects are expanded; collections show up to 100 elements
  describe_type(type?)                 members of a type (no argument: roots and all types)
  set_value(path, member, value)       (only when changes are allowed) set member (dotted path allowed, e.g. Transform.Position.y) on the object at path; value is JSON
  call_method(path, method, args)      (only when changes are allowed) invoke an allowed method on the object at path; args is a JSON array
  submit_answer(answer)                end the task
Paths: roots world, app, objects (list of every GameObject); member access with dots, list/map indexing with [0] / [""key""], e.g. world.Scenes[0].Objects[2].Children[1], world.Assets[""mat/wood""], or ref(""Enemy#12"") for an object by its $ref.
Rules: be efficient — submit as soon as you have the answer. If the object or data the task asks about does not exist, answer NONE. Only perform exactly the change requested. If no member or method does exactly that, do NOT approximate it with other or destructive operations: change nothing and answer ""IMPOSSIBLE: <reason>"". Always finish by calling submit_answer exactly once.";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static JsonArray Definitions(bool allowMutation)
        {
            var t = new JsonArray
            {
                Fn("find_objects", "Find GameObjects by name substring.", ("name_contains", "string", "substring of the name"), ("type", "string", "optional type name filter, e.g. Enemy")),
                Fn("get_object", "JSON dump of the object or collection at a path.", ("path", "string", "object path"), ("depth", "integer", "1-3, default 1")),
                Fn("describe_type", "Members of a type (no argument: everything).", ("type", "string", "type name")),
            };
            if (allowMutation)
            {
                t.Add(Fn("set_value", "Set a member on the object at path.", ("path", "string", "object path"), ("member", "string", "member, dotted for nested"), ("value", "string", "JSON value")));
                t.Add(Fn("call_method", "Call an allowed method on the object at path.", ("path", "string", "object path"), ("method", "string", "method name"), ("args", "string", "JSON array of arguments")));
            }
            t.Add(Fn("submit_answer", "Submit the final answer and end the task.", ("answer", "string", "the final answer")));
            return t;
        }

        private static JsonObject Fn(string name, string desc, params (string n, string type, string d)[] ps)
        {
            var props = new JsonObject();
            foreach (var (n, type, d) in ps) props[n] = new JsonObject { ["type"] = type, ["description"] = d };
            var req = new JsonArray();
            if (ps.Length > 0 && name != "describe_type") req.Add(ps[0].n);
            if (name == "set_value") { req.Add("member"); req.Add("value"); }
            if (name == "call_method") req.Add("method");
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["description"] = desc, ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req } },
            };
        }

        public static (string text, bool error, string? code) Execute(SampleHost host, string tool, JsonObject args, bool allowMutation)
        {
            string S(string k) => args[k] is JsonNode n ? (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) : "";
            switch (tool)
            {
                case "find_objects":
                {
                    var name = S("name_contains");
                    var type = S("type");
                    var q = $"objects | where Name contains {Lit(name)}" + (type.Length > 0 ? $" and $ is {Lit(type)}" : "") + " | first 100 | select $path $ref Name $type";
                    var r = host.Query(q, new Rql.Budgets { MaxRows = 100 });
                    if (!r.Ok) return ("error: " + string.Join("; ", r.Diagnostics), true, r.Diagnostics[0].Code);
                    var arr = new JsonArray();
                    foreach (var row in r.Rows!) arr.Add(new JsonObject { ["path"] = row[0]?.DeepClone(), ["$ref"] = row[1]?.DeepClone(), ["name"] = row[2]?.DeepClone(), ["type"] = row[3]?.DeepClone() });
                    return (arr.ToJsonString(Json), false, null);
                }
                case "get_object":
                {
                    int depth = 1;
                    if (args["depth"] is JsonNode dn && int.TryParse(dn.ToString(), out var dv)) depth = Math.Max(0, Math.Min(3, dv));
                    // A path that names a collection is dumped as the collection itself.
                    var whole = host.Engine.EvaluateLive(S("path") + " | first 1000", out var diags, new Rql.Budgets { MaxRows = 1000 });
                    if (diags.Any(d => d.Severity == Rql.DiagnosticSeverity.Error)) return ("error: " + string.Join("; ", diags), true, diags[0].Code);
                    if (whole.Count == 0) return ("null (nothing at this path)", false, null);
                    object? target = whole.Count == 1 ? whole[0] : whole.ToList();
                    return (Dump(host.Engine, target, depth)?.ToJsonString(Json) ?? "null", false, null);
                }
                case "describe_type":
                {
                    var t = S("type");
                    return (host.Schema(t.Length == 0 ? null : t), false, null);
                }
                case "set_value":
                {
                    if (!allowMutation) return ("error permission-denied: changes are not allowed in this task", true, "permission-denied");
                    var lit = JsonLiteral(S("value"));
                    var r = host.Mutate($"{S("path")} | set {S("member")} = {lit}", dryRun: false);
                    return (r.Ok ? "ok: " + string.Join("; ", r.Rows!.Select(row => $"{row[0]} {row[1]}: {row[2]?.ToJsonString()} → {row[3]?.ToJsonString()}")) : "error: " + string.Join("; ", r.Diagnostics), !r.Ok, r.Ok ? null : r.Diagnostics[0].Code);
                }
                case "call_method":
                {
                    if (!allowMutation) return ("error permission-denied: changes are not allowed in this task", true, "permission-denied");
                    var argList = "";
                    var raw = S("args");
                    if (raw.Length > 0)
                    {
                        try { argList = string.Join(", ", JsonNode.Parse(raw)!.AsArray().Select(a => JsonLiteral(a?.ToJsonString() ?? "null"))); }
                        catch (Exception) { return ("error: args must be a JSON array", true, "bad-arguments"); }
                    }
                    var r = host.Mutate($"{S("path")} | call {S("method")}({argList})", dryRun: false);
                    return (r.Ok ? "ok: " + string.Join("; ", r.Rows!.Select(row => $"{row[0]} {row[1]} → {row[2]?.ToJsonString()}")) : "error: " + string.Join("; ", r.Diagnostics), !r.Ok, r.Ok ? null : r.Diagnostics[0].Code);
                }
                default:
                    return ($"error: unknown tool '{tool}'", true, "unknown-tool");
            }
        }

        private static string Lit(string s) => JsonSerializer.Serialize(s, Json);

        /// <summary>JSON value → RQL literal (strings, numbers, bools, null, arrays; ref("…") passes through).</summary>
        private static string JsonLiteral(string json)
        {
            var t = json.Trim();
            if (t.StartsWith("ref(", StringComparison.Ordinal)) return t;
            try
            {
                var n = JsonNode.Parse(t);
                return n == null ? "null" : n.ToJsonString(Json);
            }
            catch (JsonException) { return Lit(t); }
        }

        /// <summary>ReflectorNet-style object dump: members inline, nested objects expanded to <paramref name="depth"/>.</summary>
        public static JsonNode? Dump(RqlEngine engine, object? v, int depth)
        {
            if (v == null) return null;
            switch (v)
            {
                case string s: return JsonValue.Create(s);
                case bool b: return JsonValue.Create(b);
                case Enum e: return JsonValue.Create(e.ToString());
                case float f: return JsonValue.Create(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                case DateTime dt: return JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture));
                case MapEntry me: return new JsonObject { ["key"] = Dump(engine, me.Key, depth), ["value"] = Dump(engine, me.Value, depth) };
            }
            var t = v.GetType();
            if (TypeModel.IsScalarType(t)) return JsonValue.Create(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            var info = engine.Types.Info(t);
            if (info.IsList || info.IsMap)
            {
                int count = V2Count(v);
                if (depth < 0) return new JsonObject { ["$count"] = count };
                if (info.IsMap)
                {
                    var mo = new JsonObject();
                    int i = 0;
                    foreach (DictionaryEntry de in (IDictionary)v)
                    {
                        if (i++ >= 100) { mo["…"] = $"+{count - 100}"; break; }
                        mo[Convert.ToString(de.Key, CultureInfo.InvariantCulture)!] = Dump(engine, de.Value, depth - 1);
                    }
                    return mo;
                }
                var arr = new JsonArray();
                int k = 0;
                foreach (var el in (IEnumerable)v)
                {
                    if (k++ >= 100) { arr.Add($"…+{count - 100}"); break; }
                    arr.Add(Dump(engine, el, depth - 1));
                }
                return arr;
            }
            var o = new JsonObject { ["$type"] = info.ShortName };
            if (info.IsObject) o["$ref"] = engine.RefOf(v);
            if (info.IsObject && depth < 0) { if (info.DisplayMember != null) o["Name"] = Dump(engine, info.DisplayMember.Getter(v), 0); return o; }
            foreach (var m in info.Members)
            {
                object? mv;
                try { mv = m.Getter(v); } catch (Exception ex) { o[m.Name] = "error: " + ex.Message; continue; }
                o[m.Name] = Dump(engine, mv, m.Type.IsValueType || TypeModel.IsScalarType(m.Type) ? depth : depth - 1);
            }
            return o;
        }

        private static int V2Count(object v) => v is ICollection c ? c.Count : ((IEnumerable)v).Cast<object>().Count();
    }
}
