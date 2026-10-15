# RQL — Reflection Query Language

A compact pipeline language for AI agents to query and safely change **live .NET object graphs**
(game worlds, editor state, application data) — one precise call instead of many tool calls.

```
objects | where name starts-with "Enemy" and active | select $ref name transform.position
objects | get components | where $ is Light | set intensity = intensity * 2
```

- Sources, filters, projections, sorting, hierarchy walks, aggregates.
- `set` / `call` mutations: all-or-nothing, validated before anything is written, dry-run receipts.
- Type adapters and modules let hosts shape how their types are read, written and shown.
- Compact agent-facing output (rows, compact literals) and a JSON result contract.

Used by Unity-MCP and the AI Game Dev tools (https://ai-game.dev).

License: PolyForm Shield 1.0.0 — free to use, including in commercial products, except to provide a
product that competes with the licensor's products. See LICENSE.md.
