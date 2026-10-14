# Phase 2: RQL instead of ReflectorNet as the tools' data layer (2026-10-14)

Unity-MCP `experiment/rql`, `toolFormat: Rql` (McpPlugin `IToolBinding` → Unity-MCP `RqlToolBinding`): tool parameter
types described from RQL's type model and adapters, arguments evaluated and coerced by RQL (literals, `ref("…")`,
`unity.find("…")`), results shown as RQL output. Also in this round: rql-mutate no longer repeats rql-query's full
language guide, `[RqlArgument]` gives script-execute's `parameters` an RQL description, GameObjectRef / script-execute
parameter adapters, and script-execute's body mode returns values.

Same harness and model as before (`deepseek-flash`, reasoning none, temperature 0), branch catalog (7 tools), 3 repeats.
Run `20261014-221828-unity-inproc` (25 tasks: the earlier 20 + 2 escaping probes + 3 tasks beyond RQL — script-execute
with a GameObject parameter, script-execute returning a Vector3, counting tools via unity-tool-list). Earlier baseline:
`20261014-191237-unity-inproc` (json).

| Metric (same 20 tasks) | earlier json | json now (guide dedup) | rql now |
|---|---|---|---|
| Success | 59/60 | 60/60 | 60/60 |
| Mean prompt tokens per task | 24,482 | 22,490 (−8%) | **19,543 (−20%)** |
| Cache misses | 915 | 920 | 825 |
| Mean tool calls / errors | 3.5 / 0.28 | 3.6 / 0.15 | 3.4 / 0.23 |
| Mean cost per task | $0.00038 | $0.00037 | **$0.00035 (−8%)** |
| Tool definitions (chars) | 22,799 | 20,595 | 17,707 (−22%) |

| 5 new tasks | json now | rql now |
|---|---|---|
| Success | 15/15 | 15/15 |
| Mean prompt tokens | 15,909 | 12,570 (−21%) |
| Mean cost | $0.00027 | $0.00023 (−15%) |

Per task: script-return-position json 3.0 calls / 3 errors vs rql 2.0 / 0 (the RQL result `System.Single…: (x, y, z)`
was read on the first try); script-param-children both 2 calls with 1 compile error each — script-execute's body mode
generated a void method, so `return go.transform.childCount;` failed. Fixed in script-execute (object return type when
the body returns a value); re-run of both script tasks ×3 in both conditions: 1 call, 0 errors, 12/12.

All 150 runs passed. The agent wrote RQL arguments correctly on first use (`parameters: "{go: unity.find(\"Enemies\")}"`).
Cost moves less than tokens because ~96% of prompt tokens are cache hits.
