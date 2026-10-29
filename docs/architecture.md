# Architecture

```text
RQL text ──► Parser (.NET / TS) ──► canonical QueryPlan JSON ──► Executor (.NET | TS | C++)
               syntax only            versioned, transport-neutral      binds names against LIVE objects
                                      spec/query-plan.schema.json        through a reflection model
                                                                          │
                                       QueryResult JSON ◄── Encoder ◄────┘ (only surviving items are encoded)
                                       spec/query-result.schema.json
```

| Layer | Responsibility | .NET | TypeScript | C++ |
|---|---|---|---|---|
| Contracts + codec | QueryPlan/QueryResult model, canonical serialization, validation, error codes | `Rql.Contracts` | `@rql/contracts` | `rql/codec.hpp`, own `rql/json.hpp` |
| Parser | text → canonical plan; knows no domain names | `Rql.Parser` | `@rql/parser` | none (by design, see ADR-0003) |
| Binder | resolves sources (longest dotted prefix), functions, stages, members — at execution time against runtime types | inside `Rql.Execution` | `@rql/execution` | `rql/engine.hpp` |
| Reflection model | which members exist, their types, ownership, identity, writability | `System.Reflection` + attributes / policy (`TypeModel`) | registered descriptors + dynamic fallback (TS types are erased) | explicit `TypeRegistry` descriptors (no runtime reflection in C++) |
| Executor | lazy pipeline over live values; Kleene logic; budgets; mutations | `Pipeline`, `Compiler` (closures), `Mutator` | same structure | same structure |
| Encoder | JSON form (conformance) + compact agent formats | `Encoder`, `AgentFormat` | `encoder.ts` | `encoder.cpp` |

## Key design points

* **The plan is the contract.** Text syntax is a convenience over the plan; every runtime is tested on
  the same plans (`fixtures/`). A remote host (e.g. an Unreal C++ executor behind a .NET MCP sidecar)
  receives plans, never text.
* **Live objects, late encoding.** Sources return live references; stages filter/sort/project on them;
  only the final rows are encoded. `first N` stops pulling upstream. A naive "serialize the world then
  filter" approach is not viable on real graphs: on the Large sandbox world `System.Text.Json` with
  `IgnoreCycles` ran out of memory because shared references re-serialize whole subgraphs
  (see `evaluation/reports/tierA-*.json`).
* **Identity.** `$ref` = `Type#id` from an identity member (read-only through RQL) or a session handle
  `Type@n`. Liveness hooks catch destroyed objects (`stale-ref`).
* **Ownership.** Descriptors mark non-owning references (parent links, targets, shared assets). Generic
  `recurse` and the snapshot/grader follow owning edges only, so traversal is bounded and meaningful.
* **Three-valued semantics.** `missing` ≠ `null` ≠ `error`; predicates are Kleene. Diagnostics are a
  closed code set with hints that are the agent's self-correction channel (`unknown-member` with
  suggestions, `no-match` naming where a type is reachable).
* **Safety.** Mutations need the host switch AND a mutation request; all-or-nothing with rollback,
  dry-run receipts, target limits, read-only identity members, allowlisted `call` methods
  (`[RqlCallable]`). See `security-and-permissions.md`.

## Projects

* `dotnet/Rql.SampleRuntime` — deterministic sandbox (SplitMix64 generator: game world + app data,
  small/medium/large/huge), `SampleHost` tool surface, `GraphSnapshot` for exact state diffs, CLI.
* `dotnet/Rql.Conformance` — golden-vector runner/regenerator; conformance graph as native CLR types.
* `dotnet/Rql.Bench` — Tier A benchmarks. `evaluation/runner/Rql.Eval` — Tier B agent evaluation.

## Integrations

RQL is the data layer of the [AI Game Dev](https://ai-game.dev) engine plugins; each exposes `rql-query`,
`rql-mutate` and `rql-schema` tools ([`agent-tool-contract.md`](agent-tool-contract.md)) and configures the host hooks of
[`host-integration.md`](host-integration.md).

* **Unity-MCP**: the .NET engine runs in the Unity Editor. Unity types are shaped by type adapters (the ReflectorNet
  converter parity table is in [`type-adapters.md`](type-adapters.md)), and the plugin and its extensions contribute
  `IRqlModule`s discovered through Unity's `TypeCache`.
* **Godot-MCP**: the .NET engine (NuGet `com.IvanMurzak.Rql`) runs in Godot editor builds.
* **Unreal-MCP**: the C++ engine runs inside the editor plugin over an Unreal provider (`UClass` / `FProperty`
  descriptors, `TArray` / `TMap` adapters, `IsValid` liveness); a .NET
  sidecar parses the text into a plan and renders the result. See `cpp/README.md` for the provider seam.
