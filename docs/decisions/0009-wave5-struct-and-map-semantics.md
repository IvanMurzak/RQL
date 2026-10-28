# ADR-0009: Struct arithmetic, absent map keys, struct keys and nested member hints

**Status:** accepted (2026-10-27). Implemented in the spec (§1.1, §3, §4.1–§4.3, §5, §10, §13), the three runtimes
and the shared fixtures. Source: wave-5 testing findings 13–18 (Unreal, Godot).

## Problems

1. **Vector arithmetic.** `set Scale = Scale * 1.5` ("make the coins bigger") failed in Unreal and Godot with
   `cannot apply '*' to Vector and Double`. Agents write this naturally, and bulk create computes positions.
2. **Nested members.** `set CollisionProfileName = …` on an Unreal component, whose real path is
   `BodyInstance.CollisionProfileName`, only listed the component's own members.
3. **Absent map keys.** `props["surface_material_override/0"] == null` and `… is null` were `unknown` on a Godot node
   without that entry, so `where … == null` dropped the node silently.
4. **Float structs in `distinct` / `group-by`.** `select Scale | distinct` returned `(1, 1, 1)` twice: one Scale was
   `0.99999994`, and the tuple display hides the difference.

## Decisions

### 1. Component-wise arithmetic is a host opt-in (adapter capability "Arithmetic")

A type adapter declares a struct's numeric **components** and **builds** a value from them (.NET
`GetComponents` / `MakeFromComponents`, typed `Components` / `FromComponents`, `NumericTupleAdapter.Arithmetic`; TS
`components` / `fromComponents`; C++ `TypeAdapter::components` / `from_components`). Supported: `v * n`, `n * v`,
`v / n`, `v ± w`, `v * w`, `v / w` (same type), the same with a list of exactly N numbers on either side
(`Position + [0, 1, 0]`), and `-v`. The result has the struct's type, so `set` needs no coercion.

- **Why opt-in, not a generic rule for "all-numeric structs":** quaternions and matrices are all-numeric too, but their
  products are not component-wise. A generic rule would silently give wrong rotations. The host knows which types are
  vectors. The ready-made `NumericTupleAdapter` makes it one line (`Arithmetic => true`).
- **Mixed operands are `type-mismatch` with a reason:** two struct types (`different types`), `v + n` (add a vector
  or a list instead), `n / v`, `%`, a list of the wrong length. A struct type without components says
  "`<T>` has no component-wise arithmetic". Division by zero (a number or a component) is `div-by-zero`.
- **Lists alone are not vectors:** `[1, 2, 3] * 2` stays `type-mismatch`. A list only gets a meaning next to a struct,
  whose type says how many components it has and what to build. Element-wise list arithmetic would be a separate
  feature with its own questions (lengths, nesting, strings).
- Components are computed as doubles (single-precision components widened as in §1.1). Every component is one
  operation, so there is no fused multiply-add to diverge across runtimes.

### 2. Unknown-member hints search struct members two levels down

Candidates include dotted paths of the members of struct-typed members, one and two levels deep. They come from
static type information only (no getter runs), with at most 200 paths per type. A nested path qualifies and ranks by
its last segment, with the existing rule (containment, then distance). Among equal ranks a direct member comes first,
then a shallower path. Only struct-typed members are searched: object members can be shared, cyclic or large, and the
finding was about value-type nesting (`BodyInstance`). The member list shown when nothing qualifies is unchanged.

### 3. An absent map key reads as `null`; a missing member stays `missing`

`m["k"]` and `m.k` on a map read `null` when the key is absent. Map keys are data, not schema: "no entry" and "entry
is null" mean the same thing to a filter. So `== null`, `is null`, `!= 5` and `coalesce` behave as expected, and
`has(m["k"])` still tests whether the key is present (inside `has()` the lookup reads `missing`).

`is null` therefore returns a boolean for every value that exists, absent map keys included. It stays `unknown` for
`missing` (an unknown member, an index out of range) and for `error`. **Not** making every `missing` read as null is
deliberate: `where Parnet is null | set …` would then change every object instead of failing with the
typo-empties-mutation error (§8). Typos keep their `unknown-member` warning and that safety net. A key that cannot be
converted to the map's key type also stays `missing`. TS gap: for an enum-keyed map, such a key reads `null` (see the
feature matrix).

Consequence: arithmetic on an absent key is now `null` arithmetic (`type-mismatch` instead of `missing`). Use
`coalesce(m["k"], 0)`.

### 4. Struct keys are rounded like the tuple display; `==` is unchanged

`distinct` and `group-by` key a struct by its type and members. Every number is rounded as numeric tuples print it:
6 significant digits for single precision and 12 otherwise, magnitudes below 1e-6 / 1e-9 become 0, and -0 becomes 0.
Two structs share a key exactly when they print alike.

- A key must be transitive, so it cannot be the tolerance equality of numeric tuples. Rounding is transitive. The cost
  is a boundary effect: two values that `==` calls equal can land on either side of a rounding step.
- Float precision alone (the c5fed20 rule for scalars) does not merge `1` and `0.99999994`, because these are
  different floats. Display precision does.
- `==` does not change: tuple adapters keep their tolerance equality, and structs without an adapter stay `unknown`.
  Scalars keep exact numeric keys. Their display shows 15 digits, so differences there stay visible.

## Consequences

- Hosts opt in their vector types. Unity: Vector2/3/4 and Color, not Quaternion. Godot: Vector2/3/4 (and the `I`
  variants if exposed) and Color, not Quaternion, Basis or Transform. Unreal: FVector, FVector2D, FVector4, FRotator
  (component-wise on Pitch/Yaw/Roll) and FLinearColor, not FQuat or FTransform. See the integration note for the code.
- The shared language rules gain two lines (vectors, missing map entries). They are appended to hosts' guides
  automatically.
- No plan or protocol change, no new error codes. The evaluation catalog gains a `wave5` split (vector and map tasks).
