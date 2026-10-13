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
| AOT-safe accessor mode | ✅ `AccessorMode.Reflection` | n/a | n/a |
