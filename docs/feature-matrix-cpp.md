# Feature matrix — C++ runtime (`cpp/`)

Conformance: `cmake -S cpp -B cpp/build && cmake --build cpp/build && ctest --test-dir cpp/build`.
Every stored-plan golden vector passes; vectors that exist only as RQL text are skipped (the C++
runtime has no text parser by design — the .NET/TS sidecar parses, C++ executes QueryPlans).

| Feature | Status | Notes |
|---|---|---|
| RQL text parser | none (by design) | `execute_request({"text":…})` → `unsupported`; text-only fixtures counted as skipped |
| QueryPlan codec (canonical encode, validation, error codes, negative vectors) | full | own JSON parser; accepts `1.0` where an integer is required (as .NET now does); duplicate keys: last wins; lone `\u` surrogates rejected |
| Stages: where select sort first skip count get recurse distinct sum/min/max/avg group-by (streaming) set call describe | full | pull-based, lazy (`first` stops upstream); `describe` is minimal (row columns typed `object`) |
| Extension stages / functions (`ns.stage`, `ns.fn`) | partial | extension stages receive a materialized input vector (not lazy) |
| Expressions, Kleene logic, comparisons, enums, float precision | full | `float` members compare/encode in single precision |
| Paths, map keys/members, auto-map, entry/row delegation, scope chain, `$type $ref $path $index $depth` | full | `$type` names mirror .NET for scalars/collections (`String`, `Double`, `Int32`, `List`, `Dictionary`) |
| `matches` | full | `std::regex` ECMAScript + icase; no regex timeout |
| Case-insensitive matching (`contains`, members, enum names), `lower`/`upper` | partial | ASCII-only case folding (.NET folds full Unicode) |
| String ordering | partial | UTF-8 byte order; differs from UTF-16 ordinal only for supplementary vs U+E000–U+FFFF |
| Budgets, truncation, diagnostics incl. `no-match`, `unknown-member` hints | full | `maxOutputBytes` counts UTF-8 bytes of compact JSON |
| Mutations (`set` all-or-nothing + rollback, coercion, struct write-back, dry-run, `call`) | full | `null` only for nullable members (`optional`, pointers); `std::string`/`std::vector` members reject `null` (.NET allows it for reference types); list assignment copies (no aliasing); `unique_ptr` members are never assignable |
| Reflection | provider-based | explicit `TypeRegistry` descriptors (no runtime reflection in C++); see `cpp/README.md` |
| References (`Type#id`, `Type@n`, liveness hook, resolver hook) | full | without `is_alive`, `@n` handles are re-validated by reachability over owning members |
