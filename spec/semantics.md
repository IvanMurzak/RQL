# RQL v0.1 — Semantics (normative)

This document is the single semantic contract for every RQL runtime (.NET, TypeScript, C++).
Where a runtime cannot implement a rule it MUST report a diagnostic instead of silently
behaving differently, and the gap MUST appear in [`docs/feature-matrix.md`](../docs/feature-matrix.md).

Keywords MUST / SHOULD / MAY are used in the RFC 2119 sense.

## 1. Model

A query is a **source** followed by a pipeline of **stages**. Every stage consumes a stream of
**items** and produces a stream of items (or a single terminal value). Items are *live values of
the owning runtime* — never a pre-serialized JSON copy of the graph. Serialization happens only
at the very end (output encoding), and only for what survived filtering/projection.

```
world.scenes[0].objects | where Health < 20 and Enabled | sort Health | first 5 | select $ref Name Health
```

### 1.1 Values

Every expression evaluates to one of:

| Kind | Meaning |
|---|---|
| `missing` | The path does not exist on this value (no such member/key/index). |
| `null` | The member exists and its value is null / empty optional / nullptr. |
| `error` | Evaluation failed (getter threw, division by zero, type mismatch in arithmetic). Carries a code. |
| `bool`, `number`, `string` | Scalars. Numbers are compared numerically regardless of the runtime's int/float types. |
| `enum` | Runtime enum value; compares to strings by name (case-insensitive) and to numbers by underlying value. |
| `object` | A reference-like runtime object with identity. |
| `struct` | A value type (e.g. `Vector3`); no identity, compared member-wise is NOT supported in v0 (use members). |
| `list` | Ordered collection (array, list, set, generic enumerable that is not a string or map). |
| `map` | Key→value collection (dictionary/map). |

`missing`, `null` and `error` are distinct and MUST NOT be conflated.

### 1.2 Truth (three-valued logic)

Predicates evaluate to `true`, `false` or `unknown`. As a predicate, a boolean is itself, `null` is
`false`, and `missing`, `error` and every non-boolean value are `unknown`. Comparisons involving `missing`, `error`,
or incomparable kinds yield `unknown`. Logic is Kleene:
`not unknown = unknown`, `false and unknown = false`, `true or unknown = true`, otherwise unknown.
`where` keeps an item only when its predicate is `true`.

Consequence: `where not (Health > 5)` does **not** return items without a `Health` member. Use
`has(Health)` to test existence and `X is null` to test null.

## 2. Sources

The source is a `postfix` expression evaluated in **root scope**:

* A path whose leading dotted segments match a registered source name (longest match wins,
  e.g. `unity.gameObjects` before `unity`) resolves to that source's live value; remaining
  segments navigate from it. Unknown root → error `unknown-source` (with suggestions).
* `ref("Type#id", ...)` resolves stable object references (§7). Unresolvable → `stale-ref`.
  `ref()` may also be used inside expressions (e.g. `set Target = ref("Player#1")`).

If the source value is a `list` its elements become the item stream; a `map` yields its entries
(§4.4); anything else yields a single item.

## 3. Paths and member access

`a.b.c`, `a[0]`, `a[-1]` (from end), `a["key"]`, `$type`, `$`.

* **Member lookup** — exact (ordinal, case-sensitive) name first; if absent, a *unique*
  case-insensitive match; otherwise `missing`. Visible members are public instance fields and
  readable properties (excluding indexers) that the runtime's descriptor policy exposes.
  Getter side effects: runtimes MUST only read through getters the policy allows; the sample
  runtimes expose plain auto-properties only.
* **Maps** — `m.key` and `m["key"]` look up the key (string keys compare ordinally; for non-string
  key types the key literal is coerced). A real member of the map type with that name (e.g.
  `Count`) takes precedence for `.name` syntax; `["name"]` is always a key lookup.
* **Lists** — `[i]` indexes (`missing` when out of range). `.name` on a list: if the list type
  itself has that member (`Count`, `Length`) it is used; otherwise the access **maps** over the
  elements, skipping `missing` results and flattening one level of nested lists. So
  `Components.$type` is the list of component type names. `.count()` is the portable way to
  get a size; `.Count` / `.Length` (any case) on a list or map always return its size.
* **Projected rows** (after `select`/`group-by`) have exactly their columns as members. A row with a
  **single** column stands for that column's value: member access, `get $`, `recurse` and `$ref`
  delegate to it, so `world | select LocalPlayer.Inventory | get Slots` navigates as expected.
* `$` is the current item. Inside a method argument (`Components.any(...)`) `$` and bare names
  refer to the current *element*. **Scope chain:** when the first segment of a bare path is
  `missing` on the element, it is resolved against the enclosing item(s), innermost first — so
  `world | select Quests.count(RequiredLevel > LocalPlayer.Level)` compares with the world's player.
* **Meta members** (portable): `$type` — runtime short type name (no namespace / generic arity);
  `$ref` — stable reference string (§7); `$path` — the navigation path by which the item was
  reached (§3.1); `$index` — zero-based position of the item in the stream entering the current
  stage; `$depth` — inside/after a `recurse` stage, the item's depth relative to the item the
  recursion started from (0 = start item), otherwise `missing`. Unknown meta → `missing` +
  warning `unknown-meta`. On projected rows a column literally named `$name` (from
  `select $path`) shadows the meta member, so `select $path Name | where $path contains "x"` works.

### 3.1 `$path`

Canonical form: source text, then `.Member`, `[i]` for list positions, `["key"]` for map keys,
e.g. `world.scenes[0].objects[3].Children[1]`. Stages that create new items (`get`, `recurse`)
extend the path. Projection/aggregation results have no `$path`.

## 4. Operators and functions

### 4.1 Comparison

* `==`/`=` and `!=`: numbers numerically; strings ordinal **case-sensitive**; bools; enums vs
  string (name, case-insensitive) or number; objects by identity; `null == null` is `true`;
  `null` vs non-null is `false` for `==` and `true` for `!=`. Lists/structs/maps: `unknown`.
* `<`, `<=`, `>`, `>=`: numbers, strings (ordinal), enums (underlying value). Otherwise `unknown`.
* `contains`, `starts-with`, `ends-with`: on strings — ordinal **case-insensitive** substring test.
  `contains` on a list — membership using `==`. Otherwise `unknown`.
* `matches`: .NET-compatible regex subset (literal chars, `. * + ? ^ $ [] () |` and `\d \w \s`),
  case-insensitive. Runtimes without a regex engine report `unsupported`.
* `x in [a, b]` / `x not in [...]`: `==` against each list element.
* `x is null`, `x is not null`: `true`/`false` (missing → `unknown`).
* `x is TypeName`: `true` if the runtime type of `x` or any base type/implemented interface has
  short name `TypeName` (ordinal, case-insensitive); `false` for other objects; `unknown` for
  missing/error; `false` for null.

### 4.2 Arithmetic

`+ - * / %` and unary `-` on numbers; `+` concatenates when either side is a string. Division or
modulo by zero → `error(div-by-zero)`. Other operand kinds → `error(type-mismatch)`.
`missing` operands propagate `missing`.

### 4.3 Functions (portable core)

| Function | Result |
|---|---|
| `has(path)` | `true` if `path` is not `missing` (argument is not evaluated beyond the path). |
| `is-null(x)` | same as `x is null`. |
| `len(x)` | string length, list/map size; otherwise `missing`. |
| `lower(s)`, `upper(s)` | invariant case conversion. |
| `abs(n)`, `round(n[, digits])`, `floor(n)`, `ceil(n)` | numeric. |
| `ref(s, ...)` | root scope only: resolve object references. |
| `append(list, v, ...)` | a new list: the elements of `list` followed by the values. |
| `insert(list, i, v)` | a new list with `v` inserted before index `i` (negative `i` counts from the end, `-1` appends). |
| `remove-at(list, i)` | a new list without the element at `i` (negative from the end). |
| `without(list, v)` | a new list without the elements equal to `v` (§4.1). |

List functions never modify their argument; assign the result (`set Tags = append(Tags, "x")`). A first argument that is
not a list, a non-integral index or an index out of range yields a `type-mismatch` error value.

Collection **methods** (`list.method(args)`), args evaluated per element with `$` = element:

| Method | Result |
|---|---|
| `any([pred])` | `true` if some element's pred is `true` (no pred: non-empty). |
| `all(pred)` | `true` if every element's pred is `true` (empty → `true`). |
| `count([pred])` | number of elements (whose pred is `true`). |
| `sum(e)`, `min(e)`, `max(e)`, `avg(e)` | aggregate of `e` over elements (non-numbers ignored; `min`/`max` also order strings). Empty: `sum` → `0`, others → `null`. |
| `first([pred])` | first element (matching), or `missing`. |
| `keys()`, `values()` | for maps. |

Methods/functions not in this table are **extensions** (§9).

### 4.4 Map entries

Iterating a map (as a source, in `get`, or in methods) yields entry values with members `key`
and `value` (member lookup is case-insensitive, so `Key` works too), in the map's own iteration
order (runtimes with unordered maps MUST use insertion order or sort entries by key for
determinism). Any other member accessed on an entry is looked up on its value, so
`world.Assets | where Type == "Material"` filters assets directly. Entries encode as `{"key": ..., "value": ...}`. `m[1]` on a map is a key lookup.

### 4.5 Object literals

`{ key: expr, … }` builds a value: an ordered map from string keys to values. Keys are names or strings, non-empty and
unique (`syntax-error` / `invalid-plan` otherwise); `{}` is empty. It behaves as a map everywhere (`{a: 1}.a`,
`len({…})`, encoding as a JSON object, equality is unknown). Its purpose is writes: when coerced to a member type
(§8),

* type adapters' coercion (§13) runs first;
* a **map** type is built entry by entry (keys coerced to the key type, values to the value type);
* an **object or struct** type is constructed and its members assigned: the concrete type is the target type, or the
  type named by the key `"$type"` (short or full name) among concrete types assignable to the target (no match or an
  ambiguous name → `coercion-failed`); the type must be constructible (a default/parameterless constructor or an adapter
  that coerces object literals); every other key must name a writable member of that type (unknown or read-only →
  `coercion-failed`), the host's per-member write policy applies to it exactly as to `set` (`permission-denied`), and
  its value is coerced recursively to the member type and validated like a `set`. Excluded types (§13) are never
  constructed. Construction happens while the mutation is planned, so constructors run even for a dry-run or a
  vetoed mutation; hosts whose constructors have side effects construct through an adapter instead. Members not
  named keep the type's defaults (numbers `0`, strings `""`, booleans `false`, empty lists/maps, null references) —
  an object literal builds a new value, it does not patch the old one (use member paths for that);
* any other target type → `coercion-failed`.

## 5. Stages

| Stage | Semantics |
|---|---|
| `where e` | keep items whose `e` is `true`. |
| `select [n=]e ...` | project each item to a row with the listed columns. Column name = alias, or the item's source text. |
| `sort k [asc\|desc], ...` | stable sort. `null`/`missing` last regardless of direction; mixed kinds ordered number < string < bool < other. |
| `first [n]` | first n items (default 1). MUST stop pulling upstream items afterwards (lazy). |
| `skip n` | drop first n. |
| `count` | terminal: number of items. |
| `get e` | replace each item by `e`; `list` results are flattened into separate items; `missing`/`null` dropped. |
| `recurse [via...] [max-depth=N] [include-self=false]` | depth-first pre-order expansion of each item into **itself (depth 0) and** its descendants; `include-self=false` emits descendants only. With `via` members (e.g. `recurse Children`) only those members are followed; without, every **owning** object-valued member and list/map element of objects is followed (runtime member order). Members the descriptor marks as non-owning references (parent links, targets, shared assets) are not followed by generic recursion. Cycle-safe: each object identity is emitted at most once per stage. `max-depth` default 8, hard cap per budget. |
| `distinct [e]` | keep the first item for each distinct value of `e` (default `$`; objects by identity). |
| `sum/min/max/avg e` | terminal aggregate, same rules as the collection methods (`sum` of nothing → `0`). |
| `group-by e` | replaces the stream by rows `{key, count}` (members `key`, `count`) in order of first appearance of the key; `null`/`missing` keys form one `null` group. Can be followed by `sort count desc`, `where count > 1`, etc. |
| `describe` | terminal: schema of the distinct runtime types of the items (members, kinds, writability). |
| `set p = e, ...` | mutation (§8). |
| `call m(args)` | mutation: invoke an allowlisted method (§8). |

Terminal stages (`count`, aggregates, `describe`, `set`, `call`) MUST be last;
otherwise `invalid-plan`.

## 6. Output encoding

The result of a query is a `QueryResult` ([schema](query-result.schema.json)):

* `kind: "rows"` — when the stream carries projected rows (`select` / `group-by` not followed by `get`/`recurse`) or a mutation receipt: `columns` +
  `rows` (array of arrays, column order).
* `kind: "items"` — no projection: each item is encoded as a **value** (below), `columns` absent.
* `kind: "scalar"` — `count` and aggregates: `value`.
* `kind: "schema"` — `describe`.

Value encoding (JSON): scalars as JSON; enums as their name string; `null` → JSON `null`;
`missing` → JSON `null` as well (the JSON form is lossy here by design; use `has()` to
distinguish); `error` → `{"$error": "<code>"}`. Objects → **summary** `{"$ref": "...",
"name": "..."}` (`name` only when the type has a display member, default: a string `Name`
member). Objects with **neither** an identity member nor a display member are value-like
(settings blocks, inventory slots) and are encoded **inline** at depth ≤ 1 as an object of their
members, where nested identified objects become summaries and collections become the string
`"<Type>[<count>]"`; structs → object of their public members (nested structs up to depth 2, deeper →
`"…"`); lists → JSON array of encoded values (max `budgets.maxListItems`, default 20, then a
trailing string `"…+N"`); maps → JSON object (string keys) of encoded values with the same limit.
Numbers: integral values without fraction; floats in shortest round-trip form of the *runtime's
own precision* (a C# `float` 0.1f encodes as `0.1`, not `0.10000000149`).

`total` is the number of items/rows the pipeline produced; when it exceeds `budgets.maxRows`
(default 50) only that many are returned and `truncated: true`.

Runtimes MAY offer additional encodings at the tool layer (e.g. the compact text `table`
format used for agents, see `docs/agent-tool-contract.md`); the JSON form above is the
conformance form.

## 7. References and identity

* `ref()` resolves the exact text first, so ids may contain spaces and parentheses (asset paths such as
  `"Material#Assets/My Mats/Red (1).mat"`). An exact match is final (a destroyed exact match is `stale-ref`). Only when
  nothing matches exactly does it tolerate copied display forms: anything from the first whitespace or `(` on is then
  ignored (`"Enemy#12(Goblin_0012)"` resolves `Enemy#12`).
* `$ref` = `"<Type>#<id>"` where `<id>` is a stable id from the runtime's identity provider
  (e.g. a declared `Id` member). Objects without a stable id get a session handle id
  `"<Type>@<n>"`, valid for the lifetime of the runtime session.
* `ref("...")` resolves a reference. If the object no longer exists (destroyed/collected/removed
  from every registered source) → `error(stale-ref)` and diagnostic `stale-ref`. Runtimes with
  explicit destruction (Unity/Unreal) MUST check liveness, not just a cached pointer.
* Object identity (for cycles, `distinct`, `==`) is reference identity in the owning runtime.
* The identity member that backs `$ref` is always read-only through RQL (`readonly-member`).

## 8. Mutations

Mutations are disabled unless the caller's policy enables them (`permission-denied` otherwise).

* Targets must be objects with identity; value-type or scalar items (e.g. from `get Transform.Position`)
  are rejected with `invalid-plan` because a change to a copy would be lost.
* Writing through value types requires every value-type parent member on the path to be writable and
  allowed by policy, since the modified copy is written back into it.
* A request's `maxMutationTargets` may not exceed the host's hard ceiling.
* `set p = e`: for each item, evaluate `e` against the item, coerce to the member type of `p`
  (number→integral only if integral and in range; string→enum by name; null only for nullable /
  reference types; list literal → list/array type; object literal → map / constructed object, §4.5), then write. Writing through value types
  (`Transform.Position.x = 1`) MUST write back the modified copy to every enclosing value-type
  member up to the first reference object. Target member must be writable and allowed by
  policy (`readonly-member`, `permission-denied`).
* `call m(args)`: only methods allowlisted by the runtime policy; arguments coerced as above.
* **All-or-nothing**: every target and assignment is resolved and coerced before any write. If
  more than `budgets.maxMutationTargets` (default 20) items reach the stage → `mutation-limit`,
  nothing written. If a write fails midway the runtime MUST restore already-written values.
* **Dry-run** (`options.dryRun`) computes and returns the receipt without writing.
* Receipt: `kind: "rows"`, columns `["$ref", "member", "old", "new"]` (`call`: `["$ref", "method", "result"]`).

## 9. Extensions

Hosts register **sources**, **functions** (`ns.fn(args)`), **methods**, **stages** (`ns.stage args`)
and **type descriptors**. Names of non-core extensions MUST be namespaced (`unity.`, `unreal.`,
`myapp.`). The parser accepts unknown names structurally; binding fails with `unknown-stage`/
`unknown-function` if no extension is registered. In expressions `ns.fn(x)` parses as method `fn`
on path `ns`; when the path is a bare dotted identifier chain that matches a registered function
namespace, the binder treats it as the function `ns.fn`.

## 10. Budgets and errors

Budgets (request-level, all optional, runtime may clamp to its own hard caps):

| Budget | Default | On exceed |
|---|---|---|
| `maxVisited` (items, recursion nodes and collection-method elements touched) | 1 000 000 | `budget-exceeded` (error, partial results discarded) |
| `maxRows` (returned rows/items) | 50 | truncation (`truncated: true`), not an error |
| `maxListItems` (per encoded list) | 20 | truncation marker |
| `maxDepth` (recurse cap) | 16 | clamp + warning |
| `timeoutMs` | 2 000 | `timeout` |
| `maxOutputBytes` | 65 536 | truncation of rows + warning `output-truncated` |
| `maxMutationTargets` | 20 | `mutation-limit` |

Diagnostics: `{severity: "error"|"warning", code, message, at?: {start, end}, hint?}`. Error codes
are a closed set in v0.1: `syntax-error`, `invalid-plan`, `version-unsupported`, `unknown-source`,
`unknown-stage`, `unknown-function`, `unknown-member` (warning: no evaluated item had it),
`unknown-meta`, `type-mismatch`, `arity`, `div-by-zero`, `getter-threw`, `stale-ref`,
`permission-denied`, `readonly-member`, `coercion-failed`, `mutation-limit`, `budget-exceeded`,
`timeout`, `output-truncated`, `unsupported`, `internal-error` (a runtime bug surfaced as a diagnostic instead of
a crash), `no-match` (warning: an `x is T` test was false
for every tested item; the hint names where `T` objects are reachable).

`unknown-member` SHOULD carry a `hint` with the closest member names of the observed types —
this is the main self-correction channel for agents.
Runtimes SHOULD also report `unknown-member` for a bare name that was never evaluated (short-circuit,
empty stream) when no type statically reachable from the sources declares it and no `select` alias
produces it.

## 11. Lexical conveniences (normative)

Parsers MUST bound expression nesting (reference: 100 levels) and operator count (reference: 1000) and
report `syntax-error` beyond them; numeric literals must be finite; select aliases may not start with `$`.

Numbers in RQL text use ASCII digits only. In QueryPlan JSON an integer field accepts any JSON number with an integral value (`2` and `2.0`), never `2.5`.


To reduce agent errors the parser accepts, and canonicalizes away:

* Keywords `and or not in is null true false` and core stage names in any letter case.
* `=` as equality in expressions (`where Name = "x"`), `&&`, `||`, `!` for `and`, `or`, `not`.
* `limit N` as an alias of `first N`; `expr as name` as an alias form in `select`.
* `x is null` / `x is not null` canonicalize to `==`/`!=` with a null literal; `x not in L` and
  `x is not T` to `not(...)`; a negated numeric literal folds into the literal.

These never appear in canonical plans; plans only contain the core operators.

## 12. Portability decisions (resolving runtime questions)

Recorded from the TypeScript and C++ ports (`typescript/SPEC-QUESTIONS.md`, `cpp/SPEC-QUESTIONS.md`).

| Topic | Decision |
|---|---|
| Null assignment | `null` may be assigned only to members the runtime descriptor marks nullable. In runtimes with implicit reference nullability (.NET reference types) every reference-typed member counts as nullable. Portable plans should only null members declared optional. |
| Case-insensitive matching | Normative minimum is ASCII case folding; runtimes SHOULD apply simple Unicode case folding. Golden vectors use ASCII only. |
| Ordinal string order | UTF-16 code-unit order (as in .NET and JS). A UTF-8 byte-order runtime differs only between supplementary characters and U+E000–U+FFFF; such deviation must be listed in its feature matrix. |
| Number → text (`+` concatenation, map-key text) | Shortest round-trip digits. Exponent formatting is runtime-specific (.NET/C++ `1E+15`, JS `1000000000000000`); portable plans must not depend on it. |
| `$type` of scalars/collections | Portable only for objects, structs and enums (the short declared type name). For scalars and collections it is runtime-specific. |
| Session handles without a liveness hook | Hosts SHOULD provide liveness. Without it a runtime MAY re-validate `Type@n` by reachability over owning members (an object only reachable through references then reports `stale-ref`). |
| Integers in JSON | Any JSON number with an integral value (`2`, `2.0`) — in plans and budgets. |
| List assignment | Uses the runtime's natural semantics (alias in .NET/JS, copy in C++). Portable callers must not rely on aliasing. |
| Extension stages | SHOULD be lazy (pull-based) so `first` bounds work; a runtime whose extension contract materializes input must say so. |
| `maxOutputBytes` | Advisory size budget in UTF-8 bytes of the compact JSON encoding; runtimes may approximate within ±10%. |

## 13. Type adapters (host extension, normative behaviour)

Hosts customise how a runtime type is **read, shown, written and compared** with *type adapters*. Adapters change the
reflection model and value semantics; they never change the language or the plan format, so plans stay portable.

**Applicability and order.** An adapter targets an exact type, a base class (applies to subclasses), an interface, or
an open generic type (e.g. `List<>`). For a runtime type, every applicable adapter participates, ordered by specificity:
walking the base chain from the type itself, at each level the closed type before its open generic definition (so
`List<int>`, then `List<>`, then `object`); then interfaces; then open generic interfaces. Ties are broken by the
adapter's priority (higher first), then registration order — never by an unspecified order. Runtimes SHOULD expose an
"explain" call listing the ordered adapters for a type. Runtimes without nominal generics (TypeScript, C++ without
templates in the reflection layer) only implement the exact / base / interface tiers they can express.

**Capabilities** (each optional; an adapter that does not implement one defers to the next adapter, then to the default):

| Capability | Contract |
|---|---|
| Members | Applied from the least to the most specific adapter, so a subclass adapter refines a base adapter. Can **add** virtual members (name, declared type, getter, optional setter with validation, description), **hide** members, keep **only** a whitelist, include **non-public fields** by predicate (e.g. Unity `[SerializeField]`), mark members read-only, non-owning reference, or **explicit-only** (never read implicitly: not by generic `recurse`, inline encoding, ref search or snapshots — only when a path names it; `describe` lists it with `"explicitOnly": true`). Hidden members are unreadable AND unwritable (reads → `unknown-member` warning, writes → `unknown-member` error). Virtual members are non-owning by default (computed values are not structural children). Identity and display members are chosen among non-explicit-only members (they are read for every `$ref` and summary). Configuring a member that does not exist — including `hide` / `only` with an unknown name — is a host bug and fails the query with `internal-error`. |
| Methods | Add callable methods (name, typed parameters with optional defaults, result type, description) invoked by the `call` stage exactly like host-allowlisted methods: arguments are coerced to the parameter types (adapter coercion applies), overloads are chosen by arity then coercibility, results go to the receipt, `dryRun` invokes nothing, and calls are not rolled back. Runtimes with method reflection MAY also expose existing host methods by name (for engine types that cannot be annotated) and hide methods. `describe` lists every callable method as `name(type param, …)` plus the description. |
| Virtual maps | A member whose value is a string-keyed map defined by the adapter: the owner's keys, per-key get, optional per-key set and validation. An optional per-key existence test avoids scanning the keys on lookups. Reads and writes use the ordinary map paths (`M["k"]`, `set M["k"] = v`): values are coerced to the declared value type, validation runs before any write (`coercion-failed` with the reason), writes take part in rollback, a map without setter is `readonly-member`, writes go to existing entries only (an absent key is `coercion-failed` "no entry", because a new entry could not be
removed again on rollback), and entries are never removed. Schemas show the type as `Dictionary<string, T>`. |
| Encode | Replace the output encoding of values of the type (first adapter that encodes wins). |
| Coerce | Convert a value supplied by a query (literal, list, other type) to the type on writes (`set`, `call` arguments). Values already of the target type bypass it. A failure carries a reason → `coercion-failed`, reason in the message. |
| Compare | Equality and/or ordering of a value of the type with any other value (first adapter of the left operand that answers wins, then of the right operand with the result mirrored). Ordering applies everywhere values are ordered: comparison operators, `sort`, `min` / `max` (which also accept values whose type has an ordering adapter). An adapter typically coerces the other operand, so `Pos == "(1, -1)"` works. |
| Identity / liveness | Stable id for `$ref`, resolution of `Type#id`, and an `IsAlive` verdict. A destroyed object (the liveness verdict — first answering adapter, then the host's liveness hook; only objects, never collections, maps, structs or scalars, are asked — is "not alive", e.g. Unity's fake-null) **reads as null everywhere**: the conversion happens where values enter expressions (member reads, list / map elements, sources), so `== null` / `is null` / `coalesce` / sort / `distinct` / encoding all see `null`, members of it read as members of null (no getter runs), stream expansion skips it like a null element (collection methods still see the `null`, e.g. `targets.any($ == null)`), it is therefore never a mutation target, and `ref()` to it is `stale-ref`. Adapters may choose the **identity** and **display** members by name (e.g. Unity's lowercase `name`). |
| Write-back | A member can be marked **copy-on-read** (its getter returns a copy, e.g. Unity's `Renderer.sharedMaterials`, `Gradient.colorKeys`): writes into its value are assigned back through the member's setter exactly like value types (§8), with the same writability rules and rollback. A value type can be marked **writes-through** (a handle to shared state, e.g. Unity's `ParticleSystem.MainModule`): writes to its copies are live, so no write-back to the owner happens, get-only owners are fine, virtual setters on it are allowed, and its values may be mutation targets. |
| Exclusion | Hide members by predicate (e.g. every `[Obsolete]` member), and engine-wide exclude member **types** (a member is dropped when its type, element type or a generic argument matches), e.g. delegates, native pointers or assemblies that must never be touched. |
| Literals | Coercion receives object literals (§4.5) and may build values itself; an adapter may publish a JSON Schema of the literals it accepts, shown by `describe` as `literal`. |
| Describe | A one-line description of the type for schemas and `describe`, e.g. how to write literals. |

Writes through adapter members keep the all-or-nothing guarantee: old values are read through the member getter before
any write and restored through its setter on failure — including the write whose setter threw, since a setter may change
state before throwing. Adapters SHOULD make virtual setters round-trip their getter;
a lossy member (`set(get())` changes state, e.g. the fixture's case-folding `title`) restores only what its getter
exposes. Validation runs while the mutation is planned, before any write, and rejects with a reason
(→ `coercion-failed`); a setter that throws at write time triggers the rollback. Virtual setters on value types are
rejected at configuration (they would write to a copy).

**Host write hooks.** Hosts may observe mutations: a *before-write* hook receives every planned change (item, owner,
member, old, new; for `call` the method) after planning and before any write, and may veto by failing
(`permission-denied`, nothing changed); a *committed* hook receives the changes after every write succeeded (not on
dry-run or after a rollback; completed calls are reported even when a later call fails). The **owner** is the object
whose state changes: the nearest enclosing object on the written path — never a list, map (including virtual-map
views) or struct (including writes-through handles); the item when there is none. For `set Renderer.sharedMaterial.color`
it is the Material, for `set Uniforms["_Color"]` the material owning the virtual map, for `set main.duration` the particle
system. That is what hosts mark dirty or record for undo. A failing committed hook is a
`getter-threw` warning; the writes stay applied.

**Discovery and optional types (non-normative, runtime convenience).** Runtimes with assembly/module reflection MAY
register every public adapter found in given modules (failures isolated and reported) and MAY let an adapter target a
type by name, being inert when no loaded module defines it.

**Scope.** Adapters belong to an engine (a host may run engines with different adapter sets over one type registry);
engines without adapters behave exactly as without this section. An explicit-only member is also not searched by
`ref("Type#id")` fallback resolution, so objects reachable only through one need a host stable-ref resolver.
`distinct`, `group-by` keys and `in`/`contains` over maps hash by built-in value identity and do not consult adapter
equality in 0.1.

**Conformance.** `fixtures/adapters/cases.json` specifies a test adapter set over the conformance graph that every runtime
implements in its test harness (see the file's `adapters` section); its cases must pass in every runtime.
