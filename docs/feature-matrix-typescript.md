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
| Object literals (§4.5) incl. literal kinds (engine / value / instanced, factories with an outer, ADR-0011), list functions (§4.3) | full | `"$type"` matches registered classes by short / constructor name (no namespaces in JS); no abstract classes in JS, so the engine-object-before-abstract ordering has no TS counterpart; not needed: Expose, IncludeFields, adapter discovery, name-targeted adapters (.NET only) |
| Mutations (`set` all-or-nothing, coercion, dry-run, `call`) | full | int/enum/struct coercion needs descriptors |
| Reflection of undeclared objects | partial | dynamic fallback: no int/enum/struct typing (TS types are erased) |
| Float precision | n/a | JS numbers are doubles; no single-precision `float` member type |
| `describe` type names | full | C#-like spelling shared with the reference (`int`, `double`, `List<T>`, `Dictionary<K, V>`, `T?` for value types) |
| Type adapters (§13), incl. methods, virtual / mixed maps, liveness, write-back, exclusion, literal schemas, host hooks | full | tiers: exact / base-class distance / `matches` predicate (no nominal generics or interfaces in JS); member shaping requires a registered descriptor; primitives not adaptable |
| Wave-2: string functions, multi-key `group-by`, `set` through streamed map entries, batch validation, text coercion, hint controls | full | a map registered directly as a source (or reached by a computed `get` / `recurse`) has no declared value type in TS, so writes through its entries are not coerced; `contains` / `starts-with` fold with `toUpperCase` while `replace` / `split` fold per character (differs only outside ASCII) |
| Vectors (ADR-0012): numeric-list arithmetic, `length` / `magnitude` / `normalize` / `distance` / `dot` / `lerp`, navigation after `ref(…)` as a source and qualified call target | full | host function sources are still pending in TS, so `unreal.asset(…).EventGraph` style sources / call targets (navigation after a host function call) are not available; navigation after `ref(…)` is |
| Hints (ADR-0013): misspelt parameter names rank first (`klass` → `className`), `languageRules(createCall)` puts the host creation call into the shared rules, shortened receipts never suggest paging | full | `AgentFormat` (receipt note) and module reports are .NET only |
