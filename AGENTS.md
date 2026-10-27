# AGENTS.md — RQL

RQL (Reflection Query Language) is a cross-runtime pipeline language + versioned QueryPlan protocol
for querying and safely mutating **live** object graphs. This repo is standalone: it must not depend
on ReflectorNet, Unity, Unreal, MCP or any hosted service.

## Layout
- `spec/` — normative grammar, semantics, JSON schemas. **The semantic contract wins over any implementation.**
- `fixtures/` — shared golden vectors (syntax, negative, execution, cross-runtime graph). All runtimes run them.
- `dotnet/` — reference implementation (Contracts, Parser, Execution, SampleRuntime, Conformance, Tests, Bench;
  `Rql.Package` = the pack-only NuGet project).
- `typescript/` — TS packages (contracts, parser, execution) + tests.
- `cpp/` — C++17 library (QueryPlan codec, provider-based executor) + tests (CMake).
- `evaluation/` — LLM agent evaluation: tasks, runner, configs, baselines, reports.
- `docs/` — architecture, ADRs (`docs/decisions/`), evaluation methodology, security, feature matrix.
- `unity/` — experiment: Unity-MCP extension package with RQL tools + Unity 6 test project (see `unity/README.md`).

## Rules
1. **Semantics change = spec change.** Edit `spec/semantics.md` (and schemas) first, then every runtime,
   then regenerate goldens with `dotnet run --project dotnet/Rql.Conformance -- regen`, review the diff
   by hand, and make all three runtimes pass. Never let a runtime silently diverge — document gaps in
   `docs/feature-matrix.md`.
2. Executors work on live objects; never serialize the whole graph before filtering/projection.
3. Writes/calls are off unless the host enables them AND the request asks for mutation. Keep
   all-or-nothing semantics, dry-run, receipts and budgets.
4. Secrets only via environment (`RQL_EVAL_API_KEY` / `RQL_EVAL_API_KEY_FILE`). Never commit, print or
   log a key; traces are redacted.
5. Evaluation discipline: fixed model/prompt/budgets per comparison, paired tasks, report sample sizes,
   provider-reported usage (mark estimates), keep held-out split unseen during tuning, never invent numbers.
   Respect the spend cap in `evaluation/configs/*.json`.
6. Publishing: only `com.IvanMurzak.Rql` to NuGet, only by dispatching `.github/workflows/release.yml` (owner decision
   2026-10-14; Trusted Publishing, no API key). The repo is public (ADR-0007); the license is PolyForm Shield 1.0.0
   (`LICENSE.md`).
   No other publishing or deployments.

## Commands
- .NET: `dotnet test dotnet/Rql.slnx` (needs .NET SDK 10; libraries target netstandard2.1 + net8.0).
- Conformance (.NET): `dotnet run --project dotnet/Rql.Conformance -- check`.
- TS: `cd typescript && npm install && npm test`.
- C++: `cmake -S cpp -B cpp/build && cmake --build cpp/build && ctest --test-dir cpp/build`.
- Everything: `scripts/test-all.sh`.
- NuGet package (pack + check, no publish): `scripts/pack.sh [out-dir]`.
- README art (`docs/assets/*.svg`): edit and run `scripts/readme-assets.py`, never hand-edit the generated SVGs.
