# Feature matrix — TypeScript runtime (`typescript/`)

Conformance: all golden vectors pass (`cd typescript && npm test`). Gaps vs the .NET reference:

| Feature | Status | Notes |
|---|---|---|
| Parser, canonical plans, codec, negative vectors | full | identical plans; codec accepts `1.0` where an integer is required (see `typescript/SPEC-QUESTIONS.md` #6) |
| Stages (where … call, streaming group-by, extension stages) | full | |
| Expressions, Kleene logic, comparisons, functions, methods | full | `matches` uses JS `RegExp` (`i` flag); no regex timeout |
| Paths, maps, auto-map, `$type $ref $path $index $depth` | full | `$type`/`$ref` on a list map over elements (spec), unlike .NET (SPEC-QUESTIONS #1) |
| Budgets, truncation, diagnostics incl. `no-match`, `unknown-member` hints | full | `maxOutputBytes` approximates .NET's escaped JSON length |
| Mutations (`set` all-or-nothing, coercion, dry-run, `call`) | full | needs descriptors for int/enum/struct coercion |
| Reflection of undeclared objects | partial | dynamic fallback: no int/enum/struct typing (TS types are erased) |
| Float precision | n/a | JS numbers are doubles; no single-precision `float` member type |
| `describe` type names | differs | TS-style names (`int`, `number`, `list<T>`, `map<K, V>`, `T?`); not covered by fixtures |
