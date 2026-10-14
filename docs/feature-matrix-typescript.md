# Feature matrix — TypeScript runtime (`typescript/`)

Conformance: all golden vectors pass (`cd typescript && npm test`). Gaps vs the .NET reference:

| Feature | Status | Notes |
|---|---|---|
| Parser, canonical plans, codec, negative vectors | full | identical plans |
| Stages (where … call, streaming group-by, extension stages) | full | extension stages are lazy (pull-based) |
| Expressions, Kleene logic, comparisons, functions, methods, scope chain | full | `matches` uses JS `RegExp` (`i` flag); no regex timeout |
| Paths, maps, auto-map, `$type $ref $path $index $depth`, row/entry delegation | full | |
| Budgets, truncation, diagnostics (`no-match`, static + dynamic `unknown-member`, `internal-error`) | full | `maxOutputBytes` = UTF-8 bytes of compact JSON; method elements count as visits; `matches` checks the timeout per call (JS regexes have no per-match timeout) |
| Parser limits (nesting 100, operators 1000, finite numbers, no `$` aliases) | full | |
| Mutations (`set` all-or-nothing, coercion, dry-run, `call`) | full | int/enum/struct coercion needs descriptors |
| Reflection of undeclared objects | partial | dynamic fallback: no int/enum/struct typing (TS types are erased) |
| Float precision | n/a | JS numbers are doubles; no single-precision `float` member type |
| `describe` type names | full | C#-like spelling shared with the reference (`int`, `double`, `List<T>`, `Dictionary<K, V>`, `T?` for value types) |
| Type adapters (§13) | full | tiers: exact / base-class distance / `matches` predicate (no nominal generics or interfaces in JS); member shaping requires a registered descriptor; primitives not adaptable |
