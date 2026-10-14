# Unity-MCP 0.93.4 vs Unity-MCP `experiment/rql` — whole tool catalogs, in the Editor

Same harness, scene, model (`deepseek-flash`, reasoning none, temperature 0), 20 tasks × 3 repeats, 2026-10-14,
Unity 6000.6.4f1. Each condition gets exactly the tools an MCP client sees: every ENABLED tool of the installed
Unity-MCP (names, descriptions, input schemas from Unity-MCP's own list; calls through `RunCallTool`).

- **old** — Unity-MCP 0.93.4 (OpenUPM): 38 enabled tools (gameobject-*, assets-*, scene-*, object-*, console-get-logs,
  script-execute, tests-run, …). Run `20261014-111242-unity-inproc`.
- **new** — Unity-MCP branch `experiment/rql`: 7 enabled tools (rql-query, rql-mutate, rql-schema, script-execute,
  tests-run, screenshot-isolated, unity-tool-list). Run `20261014-112206-unity-inproc`.

| Metric | old (38 tools) | new (7 tools) |
|---|---|---|
| Success (task check + no unintended change) | 59/60 | 59/60 |
| Mean model turns | 5.4 | 4.1 |
| Mean tool calls / errors | 5.5 / 0.5 | 3.8 / 0.2 |
| Mean prompt tokens per task | 138,272 | 26,013 |
| Mean completion tokens | 673 | 306 |
| Mean tool result chars | 6,273 | 657 |
| Mean cost per task (USD) | 0.00119 | 0.00040 |
| Mean wall time (s) | 8.8 | 5.4 |
| Mean tool execution time (ms) | 169 | 42 |
| Tool definitions sent with every request (chars) | 93,194 | 22,439 |
| script-execute calls (all runs) | 68 | 7 |

Reading:
- Both succeed; the old catalog gets there by falling back to `script-execute` (C# code) + `console-get-logs` for
  bulk and query tasks (68 calls). With RQL the agent writes one query instead (rql-query 103, rql-mutate 105 calls).
- The cost / token gap comes mostly from the tool definitions (4.2× smaller) and from smaller results (9.5×).
- Failures: old — move-and-rotate r3 (Euler numbers written into the rotation quaternion); new — answer-low-health r3
  (answered 3 instead of 1).
- Tasks added in this run: duplicate-move, prefab-instantiate, answer-console-errors, select-archers (operations that
  moved from tools to RQL calls / sources).
- Earlier comparison against 20 hand-picked ReflectorNet tools without script-execute: `unity-inproc-reflectornet-vs-rql.md`.
