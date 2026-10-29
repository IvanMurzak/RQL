# Agent tool contract (sample host / evaluation)

The tool layer is host-specific; this is the contract used by `SampleHost` and the evaluation runner,
recommended for MCP integrations.

| Tool | Arguments | Behaviour |
|---|---|---|
| `rql_query` | `query: string` | Read-only execution. A plan containing `set`/`call` fails with `permission-denied`. |
| `rql_mutate` | `query: string`, `dry_run?: bool` | Only offered when the task/host allows changes. Last stage must be `set` or `call`. All-or-nothing; returns a receipt. |
| `rql_schema` | `type?: string` | Sources plus every reachable type with members (`name type`, `ro` when read-only under mutation, descriptions), methods, enum values. Derived types list only their added members. |

## Result rendering

* **table** (default for agents): optional diagnostic lines (`error|warning <code>: <message> — hint: <hint>`),
  a header line (`N results` or `N of M (truncated; …)`, `DRY RUN — nothing changed;` for dry runs),
  then `col|col|…` and one row per line. Objects render as `Type#id(Name)`, structs/maps as
  `{k:v,…}`, lists as `[a,b]`, strings unquoted unless ambiguous (quoted strings escape only `"`, `\` and control
  characters; other Unicode, emoji included, stays raw). Fractional numbers show at most 15 significant digits
  (display numbers, spec §6: `0.1 + 0.2` shows `0.3`); the json rendering keeps the exact values.
  **Receipts** (`set` / `call` rows, spec §8) are compacted: when a `call` receipt has two or more rows that all share
  one method, a `method: <signature>` line follows the header and the `method` column is left out; when every row's
  `$ref` equals the reference of its result (a source-qualified call that created objects), the `$ref` column is left
  out too (the result still shows `Type#id(Name)`); and only the first 50 rows are listed, followed by
  `… N more row(s) not shown (the json format lists every row)`. So `range(0, 300) | call editor.CreateNode(…)`
  prints the signature once and 50 short result cells instead of 300 rows of `$ref|signature|result`. Hosts that need
  every row pass a larger limit (`AgentFormat.Render(result, format, maxReceiptRows)`, 0 = all) or use json. Hosts
  whose tools offer no json format replace the note after the count (`AgentFormat.Render(result, format,
  maxReceiptRows, moreRowsNote)`; `""` leaves it out). A receipt the byte budget shortens says "N of M receipt rows" and its
  hint says every change was made; it never suggests paging.
  **Shared language rules**: hosts append `RqlLanguageGuide.Rules` (TS `LANGUAGE_RULES`, C++ `rql::language_rules()`)
  to their guide; `RqlLanguageGuide.RulesFor("editor.CreateGameObject")` (TS `languageRules(createCall)`, C++
  `rql::language_rules_for(create_call)`) puts the host's own creation call into the examples instead of the
  placeholder `editor.CreateObject`.
* **json**: the conformance `QueryResult` without stats.

Experiment E5 compared both (see `RESULTS.md`).

## Budgets applied by the sample host

`maxRows` 50 per call (configurable per run), engine defaults otherwise (spec §10); mutation
target limit 20 unless the request raises `maxMutationTargets` (up to the host ceiling, 1000; spec §8 target limit:
editor hosts with undo should default higher, e.g. 200, and name their raise argument in `LimitArgument`; a request
outside 1 … the ceiling is refused with `invalid-value`, never clamped).
