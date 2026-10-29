# ADR-0012: Numeric lists as vectors, vector functions, qualified call targets with navigation

**Status:** accepted (2026-10-29). Implemented in the spec (§2, §4.2, §4.3, §8), the three runtimes and the shared
fixtures. Source: wave-6 cross-engine findings 7, 10 and 14 (list arithmetic, vector functions) and 24 (qualified call
target with a member path).

## Problems

1. **Lists were not vectors.** ADR-0009 made lists take part in arithmetic only next to a struct, so `[X, Y, 0] * k`
   (Unreal, Unity) failed with "cannot apply '*' to List and Double" while `Position + [0, 1, 0]` worked. Agents build
   offsets and directions as lists first.
2. **No vector functions.** Agents wrote `Position.length()`, `normalize(dir)`, `distance(a, b)`; each engine has these
   under its own name (`Size`, `Length`, `magnitude`), and RQL had none.
3. **A qualified call target could not navigate from a function or `ref()`.** `call unreal.asset("/Game/BP")
   .EventGraph.AddCustomEvent(…)` was refused although `call world.Scenes[0].Spawn(…)` worked.

## Decisions

### 1. A numeric list is a vector when no struct is involved

`L * n`, `n * L`, `L / n`, `L ± M` (same length) and `-L` work element by element on lists whose elements are all
numbers and give a list of numbers. `L + n`, `L * M`, `L / M`, `%` and length mismatches stay `type-mismatch`, each with
a reason.

- **Why not `L * M` element-wise:** `*` between two vectors reads as a dot or cross product to most agents; the
  explicit `dot(a, b)` avoids a silent wrong meaning. Structs keep their existing component-wise `v * w` (ADR-0009),
  where the type gives the meaning (a scale).
- **Why not `L + n` (broadcast):** it is the same refusal as `v + n` for structs, so lists and vectors behave alike.
- **Writes:** the result is a plain list, so writing it to a vector member goes through the ordinary list coercion.

### 2. Core vector functions on lists and component structs

`length` / `magnitude`, `normalize`, `distance`, `dot`, `lerp`, as functions and as methods. Vectors are numeric lists
or structs whose adapter declares arithmetic components (the same opt-in as arithmetic, so quaternions and matrices are
excluded). Results keep the kind of the first vector (a struct is rebuilt through its adapter).

- **Why `length` next to `len`:** `len` is the size of a string or collection and cannot change; `length` is the name
  agents use for a vector (Godot `Length()`, the requests in the findings). `magnitude` covers Unity's name.
- **Determinism:** components are combined in order with each product and sum rounded separately (C++ uses `volatile`
  intermediates against fused multiply-add on arm64), so the shared fixtures can compare digits.
- **Zero vector:** `normalize` of a zero vector returns it unchanged, like the engines do, instead of NaNs.

### 3. Source expressions navigate after a function call or `ref()`

A source expression is a registered source, `ref(…)` or a host function, each followed by optional navigation. The
navigation is applied to the value once (for a qualified call: before any item is read, after the mutation policy
check); everything else about receivers is unchanged (one object with identity).

## Consequences

- Behaviour changes: arithmetic on numeric lists now succeeds where it was `type-mismatch`; five new core functions /
  methods; function and `ref()` sources and call targets may navigate.
- Protocol `0.1` is unchanged (functions are plan calls by name; navigation was already expressible in plans).
