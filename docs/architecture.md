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

## Integration path (M6, design only — not implemented)

* **ReflectorNet**: implement a `TypeModel`/member policy adapter on top of ReflectorNet's reflection
  and serialization so RQL reuses its converters and Unity-specific rules; RQL stays the query layer.
* **Unity-MCP**: register `unity.*` sources (scenes, GameObjects, assets) on the CLR engine; expose
  `rql_query` / `rql_mutate` / `rql_schema` tools.
* **Unreal-MCP**: the .NET sidecar parses text → plan and forwards the plan over the existing IPC;
  the C++ engine with an Unreal provider (`UClass`/`FProperty`, `TArray`, `TMap` adapters, `IsValid`
  liveness) executes on live `UObject`s. See `cpp/README.md` for the provider seam.
