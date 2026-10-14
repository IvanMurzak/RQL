using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using com.IvanMurzak.ReflectorNet;
using com.IvanMurzak.ReflectorNet.Model;
using Rql.Sample;

namespace Rql.Eval.Agent
{
    /// <summary>
    /// Comparison toolset backed by the REAL ReflectorNet library (NuGet com.IvanMurzak.ReflectorNet), used with its
    /// default settings: path-based View / TryReadAt / Grep for reading and JSON-Merge-Patch TryPatch for writing.
    /// The model sees the same sandbox, the same rules and the same grader as the RQL condition.
    /// </summary>
    public static class ReflectorNetTools
    {
        /// <summary>The object ReflectorNet paths start from: world/... and app/...</summary>
        public sealed class Root
        {
            public World? world;
            public App? app;
        }

        private const int MaxChars = 40_000; // same order of magnitude as RQL's maxRows/maxOutputBytes guard

        public const string Prompt =
@"You operate on a live game world (plus application data) through ReflectorNet, a .NET reflection toolkit.
Tools:
  reflector_view(path?, max_depth?, name_pattern?)  serialized tree of the object at path (default root); max_depth limits nesting (default 2, max 6); name_pattern = case-insensitive regex keeping only matching member names
  reflector_read(path)                              serialize exactly one value at path
  reflector_grep(name_pattern, max_depth?)          search the whole live graph for members whose NAME matches the regex; returns path + value for each match (max_depth default 6, max 10)
  reflector_patch(json)                             (only when changes are allowed) apply a JSON Merge Patch from the root, e.g. {""world"":{""Settings"":{""TargetFps"":60}}}; list items use ""[i]"" keys, dictionary entries ""[key]""
  submit_answer(answer)                             end the task
Each call is limited to 30 seconds.
Paths are slash-delimited from the root: world/Scenes/[0]/Objects/[1]/Children/[2]/Name, world/Assets/[mat/wood], app/Users/[3]/Profile/Country.
The root has two members: world (scenes, settings, assets, quests, item catalog, LocalPlayer) and app (users, orders, products).
Rules: be efficient — submit as soon as you have the answer. If the object or data the task asks about does not exist, answer NONE. Only perform exactly the change requested. If no member does exactly that, do NOT approximate it with other operations: change nothing and answer ""IMPOSSIBLE: <reason>"". Always finish by calling submit_answer exactly once.";

        public static JsonArray Definitions(bool allowMutation)
        {
            var t = new JsonArray
            {
                Fn("reflector_view", "Serialized tree of the object at a path.", new[] { ("path", "string", "slash path from the root, empty = root"), ("max_depth", "integer", "nesting limit, default 2"), ("name_pattern", "string", "regex on member names") }, Array.Empty<string>()),
                Fn("reflector_read", "Serialize exactly one value at a path.", new[] { ("path", "string", "slash path from the root") }, new[] { "path" }),
                Fn("reflector_grep", "Find members whose name matches a regex anywhere in the live graph.", new[] { ("name_pattern", "string", "regex on member names"), ("max_depth", "integer", "recursion limit") }, new[] { "name_pattern" }),
            };
            if (allowMutation)
                t.Add(Fn("reflector_patch", "Apply a JSON Merge Patch from the root.", new[] { ("json", "string", "merge-patch document") }, new[] { "json" }));
            t.Add(Fn("submit_answer", "Submit the final answer and end the task.", new[] { ("answer", "string", "the final answer") }, new[] { "answer" }));
            return t;
        }

        private static JsonObject Fn(string name, string desc, (string n, string type, string d)[] ps, string[] required)
        {
            var props = new JsonObject();
            foreach (var (n, type, d) in ps) props[n] = new JsonObject { ["type"] = type, ["description"] = d };
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name, ["description"] = desc,
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) },
                },
            };
        }

        public sealed class Session
        {
            public Session(SampleHost host)
            {
                Reflector = new Reflector();
                RootObject = new Root { world = host.Sandbox.World, app = host.Sandbox.App };
            }

            public Reflector Reflector { get; }
            public object? RootObject;
        }

        private static string Cap(string s) =>
            s.Length <= MaxChars ? s : s.Substring(0, MaxChars) + $"\n…[truncated: {s.Length - MaxChars} more characters; narrow the path, lower max_depth or use name_pattern]";

        private static string LogText(Logs logs)
        {
            var text = logs.ToString();
            return string.IsNullOrWhiteSpace(text) ? "" : "\nlogs: " + text.Trim();
        }

        /// <summary>Wall-clock guard per tool call — the RQL side has timeoutMs/maxVisited, so this side gets an equivalent bound.</summary>
        public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

        public static (string text, bool error, string? code) Execute(Session s, string tool, JsonObject args, bool allowMutation)
        {
            var work = System.Threading.Tasks.Task.Run(() => ExecuteCore(s, tool, args, allowMutation));
            if (work.Wait(CallTimeout)) return work.Result;
            return ($"error: '{tool}' did not finish within {CallTimeout.TotalSeconds:0} s — narrow the path, lower max_depth or use a more specific name_pattern", true, "timeout");
        }

        private static (string text, bool error, string? code) ExecuteCore(Session s, string tool, JsonObject args, bool allowMutation)
        {
            string Str(string k) => args[k] is JsonNode n ? (n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString()) : "";
            int? Int(string k) => args[k] is JsonNode n && int.TryParse(n.ToString(), out var v) ? v : (int?)null;
            var r = s.Reflector;
            try
            {
                switch (tool)
                {
                    case "reflector_view":
                    {
                        var logs = new Logs();
                        var q = new ViewQuery { Path = Str("path"), MaxDepth = Math.Min(Int("max_depth") ?? 2, 6) };
                        var pattern = Str("name_pattern");
                        if (pattern.Length > 0) q.NamePattern = pattern;
                        var view = r.View(s.RootObject, q, logs: logs);
                        if (view == null) return ("null" + LogText(logs), true, "not-found");
                        return (Cap(r.JsonSerializer.Serialize(view)) + LogText(logs), false, null);
                    }
                    case "reflector_read":
                    {
                        var logs = new Logs();
                        if (!r.TryReadAt(s.RootObject, Str("path"), out var member, logs: logs) || member == null)
                            return ("read failed" + LogText(logs), true, "read-failed");
                        return (Cap(r.JsonSerializer.Serialize(member)) + LogText(logs), false, null);
                    }
                    case "reflector_grep":
                    {
                        var logs = new Logs();
                        var matches = r.Grep(s.RootObject, Str("name_pattern"), Math.Min(Int("max_depth") ?? 6, 10), logs: logs);
                        var sb = new StringBuilder();
                        sb.Append(matches.Count).Append(" match(es)\n");
                        foreach (var m in matches.Take(300)) sb.Append(m.Path).Append(" = ").Append(r.JsonSerializer.Serialize(m.Value)).Append('\n');
                        if (matches.Count > 300) sb.Append($"…{matches.Count - 300} more matches not shown\n");
                        return (Cap(sb.ToString()) + LogText(logs), false, null);
                    }
                    case "reflector_patch":
                    {
                        if (!allowMutation) return ("error permission-denied: changes are not allowed in this task", true, "permission-denied");
                        var logs = new Logs();
                        bool ok = r.TryPatch(ref s.RootObject, Str("json"), logs: logs);
                        return ((ok ? "ok" : "patch failed") + LogText(logs), !ok, ok ? null : "patch-failed");
                    }
                    default:
                        return ($"error: unknown tool '{tool}'", true, "unknown-tool");
                }
            }
            catch (Exception ex)
            {
                return ($"error: {ex.GetType().Name}: {ex.Message}", true, "exception");
            }
        }
    }
}
