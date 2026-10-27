# Feature matrix

Conformance status (latest local run): **.NET 496/496** golden checks (compiled and reflection
accessors) · **TypeScript 496/496** · **C++ 322 passed, 33 skipped** (text-only cases; no parser
by design, ADR-0003). Details: [TypeScript](feature-matrix-typescript.md), [C++](feature-matrix-cpp.md).

| Feature | .NET | TS | C++ |
|---|---|---|---|
| Canonical QueryPlan codec + validation | ✅ | ✅ | ✅ |
| Text parser | ✅ reference | ✅ identical plans | — (ADR-0003) |
| All core stages incl. streaming `group-by`, `recurse` (via / generic owning-only) | ✅ | ✅ | ✅ |
| Kleene logic, missing/null/error, enum/number/string comparisons | ✅ | ✅ | ✅ |
| Float (single) precision comparisons/encoding | ✅ | n/a (JS doubles) | ✅ |
| Scope chain, row/entry delegation, `$depth`, `$path`, `$index` | ✅ | ✅ | ✅ |
| `matches` regex | ✅ 50 ms timeout | ✅ no timeout | ✅ `std::regex`, no timeout |
| Case-insensitive ops | full Unicode | full Unicode | ASCII folding (spec §12 minimum) |
| Mutations: `set` all-or-nothing + rollback, struct write-back, dry-run, `call` | ✅ | ✅ | ✅ (list assignment copies) |
| Reflection model | `System.Reflection` + attributes/policy | descriptors + dynamic fallback (types erased) | explicit `TypeRegistry` descriptors |
| Refs: `Type#id`, `Type@n`, liveness, resolver hook | ✅ (weak refs) | ✅ (WeakMap) | ✅ (re-validated by reachability without a liveness hook) |
| Budgets (visited, rows, list items, depth, timeout, output bytes, mutation targets) | ✅ | ✅ | ✅ |
| Diagnostics with hints (`unknown-member`, `no-match`, …) | ✅ | ✅ | ✅ |
| `describe` / schema | ✅ full | names differ (not in fixtures) | minimal |
| Top-N `sort | first n` fusion, allocation-optimized recurse | ✅ | — (full sort, same results) | — |
| Extension sources/functions/stages | ✅ (lazy) | ✅ | ✅ (stages materialize input) |
| Host function as query source (`ns.fn(…) \| …`, spec §2) | ✅ | — pending | ✅ (0.2.0) |
| Adapter kernel, `IRqlModule` modules, module guides (`AddGuide`) | ✅ | — pending | — pending |
| `TreatAsObject` (enumerable types read as objects) | ✅ | — pending (no enumerable ambiguity in TS) | — pending |
| `MutationRecord.Owners` (enclosing-object chain for host undo) | ✅ | — pending | — pending |
| Copy-on-read write-back through objects inside the copy | ✅ | — pending | — pending |
| Error-cell messages in the agent table format (`CellErrors`, not in JSON) | ✅ | — pending | — pending |
| `NumericTupleAdapter<T>` (vector-like struct literals; tidy display, `nan`/`inf` input, object-literal equality) | ✅ | — pending | — pending |
| Agent table format (display numbers, raw-Unicode quoting; spec §6) | ✅ `AgentFormat` | — (JSON only) | — (JSON only; Unreal-MCP renders with the .NET sidecar) |
| `sqrt` / `pow` / `min(a, b, …)` / `max(a, b, …)`, OSA suggestions, dotted projected columns, `invalid-regex`, aggregate warnings, row `distinct`, `call-threw` / `function-threw` / `hook-threw`, read-back receipts, mutation request needs `set`/`call`, `invalid-value` (0.2.0) | ✅ | ✅ (no regex timeout warning: JS regexes have no per-match timeout; `function-threw` as a query source pending with function sources) | ✅ |
| Wave-2 (unreleased): target scope (`$index` / `$path` / `$depth`) in `set` / `call`, string functions and methods (`replace`, `replace-regex`, `substring`, `split`, `trim`, `pad`), multi-key / named `group-by`, `set` through streamed map entries, batch validation, host text coercion, budget-hint control (`BudgetsAdjustable`, `LimitHint`), `FindHint`, hints (list index, sort key, tuple) | ✅ | ✅ | ✅ (text-only hints n/a: no parser; regex via `std::regex` ECMAScript, ASCII case folding) |
| AOT-safe accessor mode | ✅ `AccessorMode.Reflection` | n/a | n/a |
