# ADR-0011: Literal kinds (value / instanced / engine), known names without source reads, key warnings

**Status:** accepted (2026-10-29). Implemented in the spec (§4.5, §5, §8, §13), the three runtimes and the shared
fixtures. Source: wave-6 cross-engine findings 1 and 21 (literal construction), 5 (abstract engine objects), 15 (known
names), 12 and 13 (silent keys).

## Problems

1. **Identity was taken to mean "scene object".** ADR-0010 made every type with an identity or liveness adapter an
   engine object that literals never build. Godot's adapter covers every `GodotObject`, so value-like resources
   (`set Mesh = {$type: "BoxMesh"}`, materials) were refused until the host set `CanConstructFromLiteral`. Unreal's
   instanced subobjects (`EditInlineNew`: an `InputAction`'s `Triggers` / `Modifiers`) were refused as engine objects too,
   and even when allowed they need an owner (`NewObject(Outer)`) that the literal path did not know.
2. **Abstract engine objects** got "is abstract; add \"$type\"" before the engine-object refusal, so an agent added a
   `$type` and was refused again. The refusal advice ("pass an existing one (e.g. ref(…))") did not fit hosts whose
   objects are found by name or asset path.
3. **Known names evaluated sources.** The warning pass (known member names for the "empty through a typo" rule and the
   never-evaluated check, the `no-match` hint) called the getter of every source no query had read yet; Godot's groups
   source walked the whole scene tree on an unrelated query. The C++ runtime called every source on every query.
4. **Silent keys.** A sort key that errors on every item left the stream in its order with no diagnostic; a
   polymorphic `group-by type` (only lights have `type`) put 199 of 200 items in the `null` group without a word.

## Decisions

### 1. A literal kind per type: engine, value, instanced

Every object or struct type has a **literal kind**:

- **value**: built from a literal (default for plain data; hosts mark value-like engine types such as Godot
  `Resource`s and Unity assets that are values);
- **instanced**: built as a subobject of its **outer**, the object it is written into (the owner of the written member
  for `set`, the call's target for a `call` argument, the object an enclosing literal builds for a nested value); a
  literal of an instanced type with no outer is `coercion-failed`;
- **engine**: never built (scene objects).

The kind is the first answer of the host's literal-kind hook, then the adapters (most specific first), then the default
(identity / liveness adapter → engine, else value). The ADR-0010 hook `CanConstructFromLiteral` keeps working and
overrides afterwards (no → engine, yes → an engine type becomes value). Building uses a **factory** (host's, then
adapters', given the type and the outer) before the default constructor, so an Unreal host can call
`NewObject(Outer, Class)` and a Unity host `ScriptableObject.CreateInstance`.

- **Why a kind and not only the boolean hook:** the boolean answers "may this be built" but not "how": an instanced
  subobject needs its owner, and a factory needs to know which. One category per type also documents intent in the
  adapter (an engine's resource adapter says *value* once instead of every host writing a hook).
- **Why the outer is the write's owner:** it is exactly the object that will reference the value once written (§13
  owner rule), which is what `NewObject` needs; nested literals take the object being built, so a trigger inside a
  built modifier is owned by that modifier.
- **Why scene objects stay refused by default:** their constructors join the scene even on a dry run (ADR-0010).
- **Dry runs:** construction happens while planning, as for plain data, so factories run on dry runs; factories must
  create objects nothing references until the write (transient subobjects), which are discarded when not written.

### 2. Engine-object check before the abstract check; host refusal advice

A literal for an abstract type whose kind is engine is refused as an engine object. The text after the semicolon of the
refusal is replaceable per type by the host (`LiteralRefusalHint` / `literalRefusalHint` / `literal_refusal_hint`), e.g.
"use unity.find(…) or an asset path".

### 3. Known names never evaluate a source

Known names come only from the types sources produced while queries read them (already recorded per source), from a
type the host **declares** for a source at registration (new optional argument), and from the query's own source, read
once for its type when its stream never pulled it (`… | first 0`). No warning or hint evaluates any other source
getter: .NET `Known()` and the `no-match` holders hint, TypeScript `knownMemberNames()` (which now learns the members of
undeclared objects and a few collection elements when a query reads the source), C++ `reachable_types()`.

- **Why not cache the first evaluation:** the first evaluation is the expensive one (the Godot groups walk), and it ran
  on queries that never named the source.
- **Cost:** a source no query has read and that declares no type contributes no names, so a typo of one of its members
  stays a warning (with an empty receipt) instead of becoming an error until the source has been read once. Hosts close
  the gap by declaring source types or with the known-member hook (spec §8).

### 4. Keys that fail or are sparse warn

- A sort key that evaluates to an error sorts last like `null` (unchanged) and adds one warning per key with the first
  error's code: `sort key 1 failed for 3 of 40 item(s) (div-by-zero: …); they sort last` (a path key is named by its path).
- A `group-by` / `distinct` key that is a member path missing on more items than it is present on (present on at least
  one) adds an `unknown-member` warning: `member 'type' exists on 1 of 200 item(s); the other 199 group as null`, hint
  "did you mean $type (the meta member)?" for a meta member's name, otherwise `where has(type)`. The all-missing `unknown-member` hint
  offers the meta member too.

- **Why a warning, not an error:** grouping a polymorphic stream by a member some items lack is legitimate; the warning
  only states the proportion. Error codes stay a closed set, so existing codes are reused.

## Consequences

- Behaviour changes: types marked value / instanced are built from literals; an abstract engine-object type gets the
  engine-object refusal; queries no longer evaluate unrelated sources while computing warnings (a never-read source
  without a declared type adds no known names); new warnings for failing sort keys and sparse group-by / distinct keys.
- Protocol `0.1` is unchanged: no plan fields, no error codes, no JSON result fields.
- New host APIs: literal-kind hook, factory hook and refusal advice on the mutation policy; a literal-kind capability
  and a factory on adapters; an optional declared type when registering a source.
