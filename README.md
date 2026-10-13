# RQL — Reflection Query Language

A compact pipeline language plus a versioned, runtime-neutral **QueryPlan** protocol for querying and
safely mutating **live object graphs** (game worlds, application state) from AI agents. Implemented for
**.NET** (reference), **TypeScript** and **C++**, all checked against one set of golden vectors.

```text
objects | where $ is Enemy and Health < 20 | sort Health | first 5 | select $ref Name Health Transform.Position
world.Assets["prefab/house"] | recurse Dependencies max-depth=16 | where Id != "prefab/house" | select Id
objects | group-by Scene.Name | sort count desc | first 1
ref("Enemy#233") | set Health = MaxHealth, Transform.Position.y = Transform.Position.y + 2     (mutation tool only)
```

Results for agents are compact rows:
```text
5 results
$ref|Name|Health|MaxHealth|Faction|Transform.Position
Enemy#233|Wraith_0232|1|90|Undead|{x:207.96,y:25.52,z:1.02}
```

**Measured outcome** (DeepSeek V4.1-Flash agent, hidden-oracle tasks; details and caveats in
[`RESULTS.md`](RESULTS.md)): on **held-out** tasks the final language + prompt reaches **100% (81/81)** (optimistic: holdout informed the final config choice, see RESULTS),
up from **81.5%** for the first iteration, at **$0.00023 per successful task** and 1.9 tool calls per task.
On a **fresh task set written after tuning was frozen** (catalog v2, n=90) it scores **100%**, while a conventional
reflection-tool baseline (find / dump-object-as-JSON / set / call) scores 66.7% at **40× the cost per success**.

## Layout

| Path | What |
|---|---|
| `spec/` | grammar (EBNF), semantics (normative), JSON schemas, versioning |
| `fixtures/` | golden vectors: syntax→plan, negative cases, execution + mutation cases on a portable graph |
| `dotnet/` | `Rql.Contracts`, `Rql.Parser`, `Rql.Execution` (netstandard2.1 + net8.0), `Rql.SampleRuntime` (sandbox + CLI), `Rql.Conformance`, `Rql.Tests`, `Rql.Bench` |
| `typescript/` | `@rql/contracts`, `@rql/parser`, `@rql/execution` + tests |
| `cpp/` | C++17 library: JSON, codec, provider-based reflection, executor + tests (CMake) |
| `evaluation/` | Tier B runner (`runner/Rql.Eval`), task export, configs + price snapshot, spend ledger, reports |
| `docs/` | architecture, ADRs, evaluation methodology, security, tool contract, feature matrix, roadmap |

## Prerequisites

* .NET SDK 10 (`dotnet --list-sdks`). On macOS without admin rights:
  `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet`
  (the scripts pick up `~/.dotnet` automatically).
* Node.js ≥ 22 (tested on 26) with npm.
* CMake ≥ 3.20 and a C++17 compiler (tested: Apple clang 21; MSVC/GCC expected to work).

## Build and test

```bash
scripts/test-all.sh            # everything: .NET, TS, C++, conformance, task self-check
# or individually
dotnet test dotnet/Rql.slnx
dotnet run --project dotnet/Rql.Conformance -- check        # regen: rewrite generated fixture fields (review the diff!)
(cd typescript && npm install && npm test)
cmake -S cpp -B cpp/build && cmake --build cpp/build && ctest --test-dir cpp/build
```
Windows / PowerShell: `scripts/test-all.ps1` (same steps).

## Try it

```bash
scripts/rql-sample "objects | where \$ is Enemy | group-by Faction"
scripts/rql-sample --size large "objects | where Components.any(\$ is LightComponent and Enabled == false) | count"
scripts/rql-sample schema                 # what the agent's rql_schema tool returns
scripts/rql-sample plan "objects | where Health < 20 | first 3"    # canonical QueryPlan JSON
scripts/rql-sample --mutate --dry-run "objects | where \$ is Enemy and Health == 0 | first 3 | set Health = 1"
scripts/rql-sample repl
```

## Evaluation

```bash
scripts/rql-eval tasks --split all          # public task list (no answers) → evaluation/tasks/
scripts/rql-eval reference --split all      # scripted reference agent; must be 89/89 (no LLM, no cost)
export RQL_EVAL_API_KEY_FILE=/path/to/key   # or RQL_EVAL_API_KEY; never commit keys (.env is gitignored)
scripts/rql-eval probe --config evaluation/configs/v2.json
scripts/rql-eval run --config evaluation/configs/v2.json --split dev --repeats 3 --out evaluation/runs/my-run
scripts/rql-eval run ... --set Prompt=v3 --set SchemaInPrompt=true      # one-factor overrides
scripts/rql-eval report evaluation/runs/A evaluation/runs/B             # paired comparison
dotnet run -c Release --project dotnet/Rql.Bench -- --size large        # Tier A benchmarks
```
Every run is recorded in `evaluation/baselines/spend-ledger.json`, which also enforces a global cap.
Raw traces stay in the gitignored `evaluation/runs/`.

## Status and policy

Local and private: no package is published. License: **pending the owner's decision**
([ADR-0007](docs/decisions/0007-license.md)). Agent rules: [`AGENTS.md`](AGENTS.md).
