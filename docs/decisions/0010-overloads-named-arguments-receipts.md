# ADR-0010: Overload resolution without side effects, arguments by name, compact receipts

**Status:** accepted (2026-10-28). Implemented in the spec (§4.5, §8 "Overloads" and receipts, §13), the three runtimes,
the shared fixtures and `docs/agent-tool-contract.md`. Source: plugin integration findings 42 (Godot) and 43 (Unity).

## Problems

1. **Side effects while choosing an overload.** `editor | call CreateGameObject("X", {}, "Cube")` left a stray
   `new GameObject()` in the Unity scene, on a dry run too. The .NET runtime coerced the arguments of every overload in
   turn, and an object literal reached `Activator.CreateInstance(GameObject)` for the overload whose parameter was a
   `GameObject parent`. The TypeScript (`new ctor()`) and C++ (`create()`) runtimes had the same loop.
2. **Misleading errors.** Only the last overload's failure was kept, so a typo inside a placement literal was reported
   against another overload's `parent` parameter.
3. **Boilerplate for bulk create.** Every host wrote a "placement" overload (an options class) plus a
   `Validate<Name>` companion so agents could pass named values; the method's own optional parameters were unusable
   from one literal.
4. **Receipt size.** A bulk `call` repeated the signature and a `$ref` equal to the result's on every row:
   `MeshInstance3D#123|CreateNode(string className, NodeOptions options)|MeshInstance3D#123(G0)`; 300 rows took about
   29 KB (~8k tokens).

## Decisions

### 1. Select the overload with a side-effect-free check, then coerce only the chosen one

Each arity-fitting overload is checked with a **probe** of the coercion pipeline (.NET `Coerce.CanConvert`, TS
`coerce(…, {probe: true})`, C++ `can_coerce`): the same rules, but nothing is constructed, assigned or validated.
Adapters and the host's text coercion still answer (they convert values; spec §13 already treats them as conversions).
Object literals are checked for a concrete, constructible type, known writable members, the write policy and the
member values, recursively. The first overload that passes is coerced for real; if the real coercion fails (a member
validator, a throwing constructor), the next passing overload is tried.

- **Why not keep coercing every candidate and only stop constructing engine objects (decision 2)?** Constructors of
  plain data can have side effects too (a host may allow a type whose constructor registers itself), and a pure check
  is cheaper than building and discarding values. The probe makes "an overload not chosen runs nothing" a guarantee.
- The probe cannot see member validators (they need the instance), so a value that only a validator rejects is found
  in the real coercion, which then falls back to the next overload as before.

### 2. Engine objects are never built from object literals

A type with an **identity or liveness adapter** is an engine object (Unity's `UnityEngine.Object`, Godot's
`GodotObject`, Unreal's `UObject`): object literals never construct it, on any path (`set`, `call`, nested members).
The host's **literal-construction hook** (`MutationPolicy.CanConstructFromLiteral` / `mutations.canConstructFromLiteral`
/ `MutationPolicy::can_construct_from_literal`: type → yes / no / no answer) decides per type in either direction.

- **Why identity / liveness adapters as the default signal:** they are exactly how a host marks objects the engine owns
  (stable ids, destroyed-but-not-null). An identity *member* alone (a data record with an `Id`) does not count, so
  `set Peer = {"$type": "Special", Name: "made"}` on plain data keeps working.
- **Why refuse instead of constructing silently:** an engine object built by its constructor joins the scene; there is
  no generic way to undo that or to keep it out of a dry run. The error says to pass an existing object (`ref(…)`).

### 3. Report the best-matching overload

Every failing candidate is ranked: arity fits, then the number of arguments that convert, then how deep inside a value
the failure is (a misspelt member inside a literal is closer than a literal the type cannot take at all); a candidate
that passed the probe and failed only in the real coercion ranks first. One best candidate → its reason, as before;
several equally close → `no overload fits: Sig1: …; Sig2: …`. No arity fits → `expects 2 or 3 argument(s), got 4`.

- **Why depth as the tie-break:** in the Unity case both overloads match two of three arguments; the depth is what tells
  "this is a placement with a typo" from "a literal is not a GameObject".

### 4. One object literal binds parameters by name

When the only argument is an object literal and no overload takes it positionally, its keys name parameters (exact,
else the only case-insensitive match); parameters left out take their defaults. Unknown keys fail with
`no parameter 'k' (did you mean …?); parameters: …`, an unnamed required parameter with `missing parameter 'p'`.
Validators, batch validation, hooks, receipts and dry runs are unchanged (they see the coerced arguments in parameter
order).

- **Why only a lone literal, and only after positional binding fails:** an overload that takes an options object keeps
  its meaning (`CreateNode({…})` with `CreateNode(NodeOptions options)` still builds the options), and mixing
  positional and named arguments would need a syntax RQL does not have.
- **Effect:** hosts can drop the placement overloads and their `Validate` companions; the method's own parameter list
  (with defaults) is the bulk-create contract.

### 5. Compact receipts in the agent table format

`AgentFormat` (the .NET tool-layer text, used by the Unity, Godot and Unreal hosts) compacts receipts
(`QueryResult.Receipt`, not part of the JSON): a method shared by every row of a `call` receipt is printed once
(`method: …`), a `$ref` column that only repeats each result's reference is left out, and only the first 50 rows are
listed, followed by `… N more row(s) not shown (the json format lists every row)`. `Render(result, format,
maxReceiptRows)` changes the limit (0 = all). Receipts of one row print as before. The JSON receipt is unchanged.

- **Why 50:** the default `maxRows` of queries; the header still states the total, and the json form keeps every row
  for hosts that post-process receipts.

## Consequences

- Behaviour changes: a literal written to an engine-object type is now `coercion-failed` (it constructed before);
  overload failures read differently (best candidate, listed ties, accepted counts); a lone object literal that no
  overload takes positionally now binds by name instead of failing; long table receipts are shortened.
- Protocol `0.1` is unchanged: no plan fields, no error codes, no JSON result fields.
- Adapter methods are still keyed by name and arity (a second same-arity overload replaces the first); reflected
  methods (.NET `[RqlCallable]`, C++ `ClassBuilder::method`) may share an arity.
