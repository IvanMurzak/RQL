using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Rql.Eval.Agent
{
    /// <summary>Versioned system prompts and tool contracts. Changing a variant's text = new variant id.</summary>
    public static class Prompts
    {
        private const string Rules =
            "Rules: be efficient — prefer one precise query over many exploratory ones. If the object or data the task asks about does not exist, answer NONE. " +
            "If a requested change is impossible (read-only member, no such operation), change nothing and answer \"IMPOSSIBLE: <reason>\". " +
            "Always finish by calling submit_answer exactly once.";

        /// <summary>v1 — baseline: short cheat sheet; schema available via tool.</summary>
        public const string V1 =
@"You operate on a live game world (plus application data) through RQL, a pipeline query language for live object graphs.
Tools: rql_query(query) runs a read-only query; rql_mutate(query, dry_run) runs set/call stages (only when the task allows changes); rql_schema(type?) lists sources and types with their members; submit_answer(answer) ends the task.

RQL: source | stage | stage ...
Sources: world, app, objects (every GameObject flattened). ref(""Type#id"") resolves a $ref.
Stages:
  where <predicate>                  filter
  select a b.c alias=(expr)          project columns (wrap computed expressions in parentheses)
  sort <expr> [desc], ...            stable sort
  first N | skip N | count
  get <path>                         replace each item by a member (lists are flattened)
  recurse Children [max-depth=N] [include-self=true]   walk a hierarchy (cycle-safe)
  distinct [expr] | group-by expr | sum expr | min expr | max expr | avg expr
  set Path = expr, ...               (rql_mutate) assign members, e.g. set Transform.Position.y = Transform.Position.y + 1
  call Method(args)                  (rql_mutate) invoke an allowed method
Expressions: == != < <= > >= and or not + - * / %; contains, starts-with, ends-with (case-insensitive); matches ""regex""; x in [a, b]; x is null; x is not null; $ is TypeName (includes subclasses); has(Member).
Paths: A.B.C, List[0], Map[""key""]; .Member on a list maps over its elements. List methods: .any(pred) .all(pred) .count([pred]) .sum(e) .min(e) .max(e) .first([pred]).
Meta: $ (current item), $ref (stable id such as Enemy#12), $type, $path. Strings use double quotes; enums compare to strings (Kind == ""Boss""); == on strings is case-sensitive.
Example: objects | where $ is Enemy and Health < 20 | sort Health | first 5 | select $ref Name Health
Results come back as compact rows (header line, then col|col). Long results are truncated — refine the query or use count.

" + Rules;

        public static string Get(string variant) => variant switch
        {
            "v1" => V1,
            _ => throw new ArgumentException("unknown prompt variant " + variant),
        };

        public static JsonArray Tools(bool allowMutation, string toolset)
        {
            var tools = new JsonArray();
            if (toolset == "rql")
            {
                tools.Add(Fn("rql_query", "Run a read-only RQL query against the live world and return the result.",
                    Props(("query", "string", "RQL query text"))));
                if (allowMutation)
                    tools.Add(Fn("rql_mutate", "Run an RQL query whose last stage is set or call. Changes are all-or-nothing; dry_run=true previews the receipt without changing anything.",
                        Props(("query", "string", "RQL query ending in a set or call stage"), ("dry_run", "boolean", "preview only")), required: new[] { "query" }));
                tools.Add(Fn("rql_schema", "List RQL sources and object types with their members. Pass a type name to see one type.",
                    Props(("type", "string", "optional type name")), required: Array.Empty<string>()));
            }
            tools.Add(Fn("submit_answer", "Submit the final answer and end the task.", Props(("answer", "string", "the final answer"))));
            return tools;
        }

        private static JsonObject Props(params (string name, string type, string desc)[] ps)
        {
            var o = new JsonObject();
            foreach (var (n, t, d) in ps) o[n] = new JsonObject { ["type"] = t, ["description"] = d };
            return o;
        }

        private static JsonObject Fn(string name, string desc, JsonObject props, string[]? required = null)
        {
            var req = new JsonArray();
            foreach (var r in required ?? new[] { NameOfFirst(props) }) req.Add(r);
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req },
                },
            };
        }

        private static string NameOfFirst(JsonObject o)
        {
            foreach (var kv in o) return kv.Key;
            return "";
        }
    }
}
