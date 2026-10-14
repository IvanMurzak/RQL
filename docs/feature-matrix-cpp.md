# Feature matrix — C++ runtime (`cpp/`)

Conformance: `cmake -S cpp -B cpp/build && cmake --build cpp/build && ctest --test-dir cpp/build`.
Every stored-plan golden vector passes; vectors that exist only as RQL text are skipped (the C++
runtime has no text parser by design — the .NET/TS sidecar parses, C++ executes QueryPlans).

| Feature | Status | Notes |
|---|---|---|
| RQL text parser | none (by design) | `execute_request({"text":…})` → `unsupported`; text-only fixtures counted as skipped |
| QueryPlan codec (canonical encode, validation, error codes, negative vectors) | full | own JSON parser; integral doubles (`1.0`) accepted for integers; non-finite numbers rejected; JSON nesting capped at 256 and expression nesting at 128 (→ `invalid-plan`); duplicate keys: last wins; lone `\u` surrogates rejected |
| Stages: where select sort first skip count get recurse distinct sum/min/max/avg group-by (streaming) set call describe | full | pull-based, lazy (`first` stops upstream); `describe` is minimal (row columns typed `object`) |
| Extension stages / functions (`ns.stage`, `ns.fn`) | partial | extension stages receive a materialized input vector (not lazy); a throwing stage handler or source getter → `internal-error` |
| `internal-error` safety net | full | any unexpected `std::exception` during execution becomes `ok=false` + `internal-error` |
| Expressions, Kleene logic, comparisons, enums, float precision | full | `float` members compare/encode in single precision |
| Object literals `{k: v}` (§4.5), list functions `append` / `insert` / `remove-at` / `without` | full | construction needs a factory: structs automatically, objects via `ClassBuilder::constructible()`; `"$type"` matches registered short type names only (no full names in C++); objects constructed into raw-pointer links are kept alive by the engine that built them (freed with it), `shared_ptr` members share ownership |
| Paths, map keys/members, auto-map, entry/row delegation, scope chain, `$type $ref $path $index $depth` | full | `$type` names mirror .NET for scalars/collections (`String`, `Double`, `Int32`, `List`, `Dictionary`) |
| `matches` | full | `std::regex` ECMAScript + icase; no regex timeout |
| Case-insensitive matching (`contains`, members, enum names), `lower`/`upper` | partial | ASCII-only case folding (.NET folds full Unicode) |
| String ordering | deviation (§12) | UTF-8 byte order; differs from the normative UTF-16 code-unit order only between supplementary characters and U+E000–U+FFFF |
| Budgets, truncation, diagnostics incl. `no-match`, `unknown-member` hints | full | default `maxVisited` 1 000 000; collection-method elements, implicit list mapping and `ref()` fallback-search nodes count as visits; timeout checked after every `matches`; `maxOutputBytes` counts UTF-8 bytes of compact JSON; budgets accept integral doubles (`2.0`, §12) |
| Static `unknown-member` for never-evaluated bare names (§10) | full | known names = members of types reachable through the `TypeRegistry` (member/element/key types + registered subtypes), `select` field names, key/value/count/Count/Length |
| Mutations (`set` all-or-nothing + rollback, coercion, struct write-back, dry-run, `call`) | full | value-type items rejected as targets; struct parents on the path must be writable; `maxMutationTargets` capped by `hard_max_targets` (1000); map `.Count` read-only; member-syntax map writes reuse an existing key case-insensitively; rollback failures reported; `null` only for nullable members (`optional`, pointers); `std::string`/`std::vector` members reject `null` (.NET allows it for reference types); list assignment copies (no aliasing); `unique_ptr` members are never assignable |
| Reflection | provider-based | explicit `TypeRegistry` descriptors (no runtime reflection in C++); RTTI-free (compile-time type keys, `rql_dynamic_type()` / `dynamic_type` hook for polymorphism); ctest covers a `-fno-rtti` build; see `cpp/README.md` |
| Type adapters (§13): members (virtual / hide / only / read-only / reference / explicit-only / describe), methods (`method`, `hide_methods`), virtual maps (`map`, mixed-type `mixed_map`), copy-on-read / writes-through write-back, `hide_where`, engine `exclude_types`, identity / display by name, dead objects read as null, literal schemas, host write hooks (`before_write` / `committed` with owners), encode, coerce, compare, stable id, liveness, description, explain | full | exact / base / interface tiers (no open-generic tier); per-engine adapter sets over a shared `TypeRegistry`; no `IncludeFields` / `Expose` (C++ has no reflection of non-public fields or methods — register them as members / methods instead) |
| Exception boundary | full | exceptions are internal; every `Engine` member is `noexcept`; codec/JSON helpers have `noexcept` `try_` variants |
| References (`Type#id`, `Type@n`, liveness hook, resolver hook; exact text before display-form trimming) | full | without `is_alive`, `@n` handles are re-validated by reachability over owning members |
