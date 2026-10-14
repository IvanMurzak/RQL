# Tool presentation: JSON schema vs RQL text — Unity-MCP, in the Editor

Question: if every MCP tool takes its complex arguments as an RQL literal string (type described in `rql-schema` style)
instead of a nested JSON schema, and returns compact text instead of JSON, does the agent use fewer tokens?

Same harness, scene, tasks and model as `unity-mcp-old-vs-rql-branch.md` (`deepseek-flash`, reasoning none,
temperature 0, 20 tasks × 3 repeats, 2026-10-14, Unity 6000.6.4f1). Tools are unchanged; only what the agent sees changes
(McpPlugin `IToolPresentation` hook, Unity-MCP `RqlToolPresentation`):

- **json** — the standard presentation: ReflectorNet JSON schema (`$defs`, `$ref`), JSON results.
- **text** — simple parameters (string, number, bool, enum, list of those) keep a plain JSON schema without `$ref`;
  each complex parameter is a string holding an RQL literal (`{name: "Lamp", path: "Root/Lamp"}`), its type described
  once per tool in the description (`GameObjectRef {instanceID string required (…), path string (…), …}`); no output
  schema; JSON results rendered as `key: value` lines, RQL-style literals, tables for lists of flat objects.
- **lean** — text without the member descriptions in the type text.

Runs: `20261014-191237-unity-inproc` (branch, 7 tools), `20261014-192651-unity-inproc` (0.93.4, 38 tools).

## Branch `experiment/rql` (7 tools: rql-*, script-execute, tests-run, screenshot-isolated, unity-tool-list)

| Metric | json | text | lean |
|---|---|---|---|
| Success | 59/60 | 60/60 | 60/60 |
| Mean prompt tokens per task | 24,482 | 22,045 (−10%) | 21,037 (−14%) |
| — of which cache misses | 915 | 816 | 766 |
| Mean completion tokens | 280 | 272 | 271 |
| Mean tool calls / errors | 3.6 / 0.3 | 3.4 / 0.2 | 3.5 / 0.3 |
| Mean cost per task (USD) | 0.00038 | 0.00035 (−8%) | 0.00034 (−11%) |
| Mean wall time (s) | 4.7 | 4.6 | 4.6 |
| Tool definitions (chars) | 22,799 | 21,374 | 20,379 |

## Unity-MCP 0.93.4 (38 tools: gameobject-*, assets-*, scene-*, object-*, console-*, script-*, …)

| Metric | json | text | lean |
|---|---|---|---|
| Success | 57/60 | 60/60 | 58/60 |
| Mean prompt tokens per task | 138,213 | 119,793 (−13%) | 90,790 (−34%) |
| — of which cache misses | 2,708 | 3,736 | 3,982 |
| Mean completion tokens | 748 | 816 | 745 |
| Mean tool calls / errors | 6.2 / 0.6 | 6.8 / 0.8 | 6.3 / 0.6 |
| Mean tool result chars | 6,218 | 7,720 | 8,367 |
| Mean cost per task (USD) | 0.00126 | 0.00140 (+11%) | 0.00130 (+3%) |
| Mean wall time (s) | 8.7 | 9.0 | 8.7 |
| Tool definitions (chars) | 93,194 | 80,725 | 56,836 |

## Reading

- **The format works for the agent.** No condition lost quality: text was 60/60 on both catalogs; the agent wrote RQL
  literals for complex arguments (GameObjectRef, SerializedMember lists, PathPatch) without help. Failures are not
  format-related: json/0.93.4 move-and-rotate ×2 (Euler numbers in a quaternion) and answer-low-health ×1; lean/0.93.4
  duplicate-move ×2 (an extra copy); json/branch combined-archers ×1 (read `materials`, which instantiates materials).
- **Where the weight is.** In the 0.93.4 catalog 80% of the tool definitions is JSON schema (74 k of 93 k chars), but most
  of it is the member *descriptions*, not the JSON structure. The text form removes the structure (`$defs`, `$ref` to
  `System.Boolean`, full type names): schemas shrink 74 k → 28 k, the type text adds 30 k to the descriptions. Dropping
  the member descriptions (lean) is where the big cut comes from (−39% definitions), at some loss of guidance.
- **Prompt caching decides the price.** 97–98% of the prompt tokens are cache hits (the tool definitions repeat every
  turn), billed at a fraction of a miss. Smaller definitions therefore save little money; cost follows cache misses
  (new tool results, the new turn) and completions. On 0.93.4 the text conditions made slightly more calls and read
  more console output (agent-chosen `console-get-logs` windows that include the harness' own log lines), so misses and
  cost went up although prompts were 13–34% smaller. On the branch every number moved down a little.
- **Where it would matter more:** clients or models without prompt caching, small context windows, and large tool
  catalogs (definitions are paid on every turn). On the RQL branch the catalog is already small, so the gain is ~10%.

## Verdict

Worth keeping as an option, not a big lever on its own: −8…11% cost and −10…14% prompt on the RQL branch, same
success. The large savings (6× prompt, 3.3× cost) came from replacing many tools with RQL, not from the schema format.

Implementation (local branches, not published): McpPlugin `experiment/rql-tool-format` (hook), Unity-MCP
`experiment/rql` `Editor/Scripts/Rql/ToolFormat/` (presentation + tests); mirror for the 0.93.4 project in
`unity/com.ivanmurzak.unity.mcp.rql/Editor/Scripts/ToolFormat/`. After these runs the result text stopped escaping
`+`, `<` and non-ASCII as `\u002B`-style sequences (slightly shorter results; not re-measured). Spend: $0.30 (ledger $4.19 of $25).

## Escaping check on other providers (2026-10-14)

After the result text stopped escaping `+`, `<`, `&`, `'` and non-ASCII (`ToolTextEscaping.Minimal`, now configurable:
Minimal / Ascii / Json via McpPlugin `ToolPresentation.Escaping` and Unity-MCP config `toolTextEscaping`), a short run
on two more models: 8 tasks × json / text × 1 repeat on the branch catalog, plus the 3 escaping tasks on 0.93.4+text.
Two new probes use a GameObject named `Crate #7 <A+B> — café "x"`: read its exact name back
(`answer-tricky-name`) and rename it to `Box <1+1> naïve & 'ok' — ü` (`rename-tricky-name`).

| Model (provider) | branch json | branch text | 0.93.4 text (3 escaping tasks) | Cost |
|---|---|---|---|---|
| gpt-6-luna (OpenAI) | 8/8 | 7/8 | 3/3 | $0.009 |
| claude-haiku-4-5 (DeepInfra) | 7/8 | 7/8 | 3/3 | $0.73 |

Both probes passed in every condition on both models: no provider mangled the unescaped characters, in tool results or
in arguments. The three failures were all `answer-gold-users`: the agent selected `Renderer.materials`, which in the
Editor instantiates material copies (an unintended scene change). Fixed afterwards: `material` / `materials` now read
and write the shared materials. Runs `20261014-201548`, `-201822`, `-202015`, `-202035` (`-unity-inproc`). Haiku 5.5 was
not reachable (no Anthropic inference key; DeepInfra lists Haiku 4.5 only). gpt-6-luna prices from third-party listings.
