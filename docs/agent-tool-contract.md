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
  `{k:v,…}`, lists as `[a,b]`, strings unquoted unless ambiguous.
* **json**: the conformance `QueryResult` without stats.

Experiment E5 compared both (see `RESULTS.md`).

## Budgets applied by the sample host

`maxRows` 50 per call (configurable per run), engine defaults otherwise (spec §10); mutation
target limit 20 unless the request raises `maxMutationTargets`.
