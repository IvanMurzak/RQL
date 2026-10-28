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
| `missing` | The path does not exist on this value (no such member or list index). An absent map key is not `missing`: it reads as `null` (§3). |
| `null` | The member exists and its value is null / empty optional / nullptr. |
| `error` | Evaluation failed (getter threw, division by zero, type mismatch in arithmetic). Carries a code. |
| `bool`, `number`, `string` | Scalars. Numbers are compared numerically regardless of the runtime's int/float types. A single-precision (float) value is the double of its shortest round-trip decimal wherever it is used as a number (`4.2f` is `4.2`, not `4.19999980926514`), so arithmetic, functions and aggregates over float members compute with the value the member shows; the widened value converts back to the same float, so comparisons in float precision (§4.1) are unchanged. |
| `enum` | Runtime enum value; compares to strings by name (enum name resolution, §8) and to numbers by underlying value. |
| `object` | A reference-like runtime object with identity. |
| `struct` | A value type (e.g. `Vector3`); no identity. `==` compares two values of one type member by member, or through a type adapter (§4.1, §13); arithmetic is component-wise when a type adapter declares its components (§4.2); `distinct` / `group-by` key it by its members with numbers rounded for display (§5). |
| `list` | Ordered collection (array, list, set, generic enumerable that is not a string or map). |
| `map` | Key→value collection (dictionary/map). |

`missing`, `null` and `error` are distinct and MUST NOT be conflated.

### 1.2 Truth (three-valued logic)

Predicates evaluate to `true`, `false` or `unknown`. A **predicate position** is the condition of `where`, the operands
of `and` / `or` / `not`, the condition of `if` / `?:` and the predicate argument of `any` / `all` / `count` / `first`.
There, a boolean is itself, `null` is `false`, and `missing`, `error` and `unknown` are `unknown` (a one-column row stands
for its value, §3). **Any other value** (a number, string, enum, object, struct, list or map) is not a condition: there is
no implicit truthiness, and evaluating it fails the query with a `type-mismatch` error naming the value and showing a
boolean test, e.g. "condition must be a boolean, e.g. `sharedMaterial != null`" (otherwise `where sharedMaterial` would
silently drop every item). A null value still reads as `false`, so `where Parent` keeps no item when every `Parent` is
null; a branch or operand that is never evaluated (short-circuit, the untaken branch of `if`) is never checked.
Comparisons involving `missing`, `error`, or incomparable kinds yield `unknown`. Logic is Kleene:
`not unknown = unknown`, `false and unknown = false`, `true or unknown = true`, otherwise unknown.
`where` keeps an item only when its predicate is `true`.

Consequence: `where not (Health > 5)` does **not** return items without a `Health` member. Use
`has(Health)` to test existence and `X is null` to test null.

## 2. Sources

The source is a `postfix` expression evaluated in **root scope**:

* A path whose leading dotted segments match a registered source name (longest match wins,
  e.g. `unity.gameObjects` before `unity`) resolves to that source's live value; remaining
  segments navigate from it. Unknown root → error `unknown-source` (with suggestions). Any other source text (a scalar
  literal, an unknown function, an operator) is `unknown-source` too; its message gives a registered source as the example
  (the first one the host registered, never an invented name) and its hint lists the sources and source functions.
* `ref("Type#id", ...)` resolves stable object references (§7). Unresolvable → `stale-ref`.
  `ref()` may also be used inside expressions (e.g. `set Target = ref("Player#1")`).
* A call to a registered host extension function (§9), e.g. `unity.assets("t:Material")`, evaluated with no current
  item; its value is the source value. An error value fails the query with its code. (Implemented in .NET; TS/C++
  parity pending.)
* The core function **`range(start, end[, step])`** streams numbers: `start + k × step` for k = 0, 1, … while the
  number is below `end` (`step` > 0) or above it (`step` < 0); `step` defaults to 1 and `end` is excluded, so
  `range(0, 300)` gives 0 … 299 (`$index` is the same as the number) and `range(10, 0, -2)` gives 10, 8, 6, 4, 2. The
  arguments are evaluated with no current item; a non-number or non-finite argument, or a `step` of 0, fails the query
  with `type-mismatch`, and a wrong argument count with `arity`. The numbers are produced one at a time and each counts
  against `maxVisited` like any streamed item, so `range(0, 1e9) | first 3` is cheap and an unbounded range ends in
  `budget-exceeded`; when the range feeds a `set` / `call`, the target limit stops it first (§8). `$path` of an item is
  `range(…)[k]`. A host function registered as `range` takes precedence. In an expression `range(…)` is the list of
  those numbers (§4.3).
* A **list literal** `[a, b, …]` streams its elements, evaluated with no current item (bare paths in it read nothing and
  their elements are dropped like null: use `ref(…)` for objects; an error element fails the query with its code); `$path` of an element is `[…][i]`. `[["Crate", 1], ["Barrel", 2]] | call editor.Create($[0], $[1])`
  creates one object per entry (§8).

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
  `Count`) takes precedence for `.name` syntax; `["name"]` is always a key lookup. **An absent key reads as `null`**,
  not `missing`: a map's keys are data, not members, so `Props["mp"] == null`, `Props["mp"] is null` and
  `coalesce(Props["mp"], 0)` treat an object without the entry like one whose entry is null, and `where Props["mp"] != 5`
  keeps it; `set Nick = Props["nick"]` assigns null to an object without the entry (guard with `where has(…)` or
  `coalesce`). For the scope chain (below) an absent key on the element counts as missing, so `Entries.any(v >
  Threshold)` still finds the item's `Threshold` when the entry maps have no such key. `has(Props["mp"])` still tests whether the key is present (§4.3). A key that cannot be converted to the map's
  key type (an enum name the key type does not have) stays `missing`, and so do a list index out of range and an
  unknown member (a typo such as `Prosp["mp"]`, which keeps its `unknown-member` warning).
* **Lists** — `[i]` indexes (`missing` when out of range). `.name` on a list: if the list type
  itself has that member (`Count`, `Length`) it is used; otherwise the access **maps** over the
  elements, skipping `missing` results and flattening one level of nested lists. So
  `Components.$type` is the list of component type names. `.count()` is the portable way to
  get a size; `.Count` / `.Length` (any case) on a list or map always return its size.
* **Projected rows** (after `select`/`group-by`) have exactly their columns as members. A row with a
  **single** column stands for that column's value: member access, `get $`, `recurse` and `$ref`
  delegate to it, so `world | select LocalPlayer.Inventory | get Slots` navigates as expected.
  A bare path of two or more member names whose dotted text equals a column name (matched like a single-name column:
  ordinal first, else case-insensitive) resolves to that column before any member navigation, so
  `select Owner.Label Intensity | sort Owner.Label` sorts by the projected `Owner.Label` column (the same in `where`,
  `select`, `distinct`, `group-by` and every other expression after the projection).
* `$` is the current item. Inside a method argument (`Components.any(...)`) `$` and bare names
  refer to the current *element*. **Scope chain:** when the first segment of a bare path is
  `missing` on the element, it is resolved against the enclosing item(s), innermost first — so
  `world | select Quests.count(RequiredLevel > LocalPlayer.Level)` compares with the world's player.
* **Meta members** (portable): `$type` — runtime short type name (no namespace / generic arity);
  `$ref` — stable reference string (§7); `$path` — the navigation path by which the item was
  reached (§3.1); `$index` — zero-based position of the item in the stream entering the current
  stage (in a `set` value or `call` argument: the target's position in the stream entering the `set` / `call` stage,
  §8); `$depth` — inside/after a `recurse` stage, the item's depth relative to the item the
  recursion started from (0 = start item), otherwise `missing`. Unknown meta → `missing` +
  warning `unknown-meta`. On projected rows a column literally named `$name` (from
  `select $path`) shadows the meta member, so `select $path Name | where $path contains "x"` works.

### 3.1 `$path`

Canonical form: source text, then `.Member`, `[i]` for list positions, `["key"]` for map keys,
e.g. `world.scenes[0].objects[3].Children[1]`. Stages that create new items (`get`, `recurse`)
extend the path. Projection/aggregation results have no `$path`.

`$path` is the **query path**: how this query reached the item, starting from its source (`objects[5]`,
`unity.find("Crate").Children[0]`). It is not a host location such as a scene-hierarchy path, a node path or an asset
path; the same object reached by another query has another `$path`. Hosts expose their own locations as members.

## 4. Operators and functions

### 4.1 Comparison

* `==`/`=` and `!=`: numbers numerically; strings ordinal **case-sensitive**; bools; enums vs
  string (the entry the string resolves to by enum name resolution, §8; a string that resolves to no entry or to
  several is not equal) or number; objects by identity; `null == null` is `true`;
  `null` vs non-null is `false` for `==` and `true` for `!=`. **Lists by value**: two lists are equal when they have the
  same length and their elements are pairwise equal by these rules (`Tags == ["root", "red"]`; order matters); `false` as
  soon as one pair is not equal, `unknown` when no pair differs but one is `unknown` (lists nested more than 32 deep, or
  containing themselves, compare as `unknown`; `distinct` / `group-by` then key them by identity). **Structs of one type
  by their members**: the members their encoding shows (§6; members of the struct's own type, derived values such as
  `normalized`, left out), each compared by these rules — recursively, so a struct of structs (`Anchors` holding two
  `Vector2D`) compares its nested values, through their adapters when they have one — `false` as soon as one member
  differs, `unknown` when none differs but one is `unknown` (or a getter throws, or structs nest more than 8 levels); a
  struct type with no member left to compare uses the runtime's own equality where it has one (.NET), otherwise
  `unknown`. A
  type adapter's comparison answers first (§13). A list against a non-list, structs of different types and maps:
  `unknown` — including a struct against an object literal (§4.5), unless a type adapter's comparison answers. So
  `set` receipts (§8) count a struct written with the value it already has as unchanged. `in` / `contains` use the same equality, so `Tags in [["root"], []]` works.
* `<`, `<=`, `>`, `>=`: numbers, strings (ordinal), enums (underlying value). Otherwise `unknown`.
* `contains`, `starts-with`, `ends-with`: on strings — ordinal **case-insensitive** substring test.
  `contains` on a list — membership using `==`. Otherwise `unknown`.
* `matches`: .NET-compatible regex subset (literal chars, `. * + ? ^ $ [] () |` and `\d \w \s`),
  case-insensitive (like `contains` / `starts-with` / `ends-with`). Runtimes without a regex engine report `unsupported`.
  A pattern that does not compile is an `invalid-regex` error naming the pattern: when the pattern is a literal, the
  query fails before it runs; a computed pattern yields `unknown` for that item plus one `invalid-regex` warning.
  A match that exceeds the runtime's per-match regex time limit yields `unknown` plus one `timeout` warning.
* `x in [a, b]` / `x not in [...]`: `==` against each list element.
* `x is null`, `x is not null`: `true`/`false` for every value, an absent map key included (it reads as `null`, §3);
  `missing` (an unknown member, an index out of range) and `error` → `unknown`, so a misspelt member never makes a filter
  keep every item (`where Parnet is null | set …` reports the typo instead of changing every object, §8).
* `x is TypeName`: `true` if the runtime type of `x` or any base type/implemented interface has
  short name `TypeName` (ordinal, case-insensitive); `false` for other objects; `unknown` for
  missing/error; `false` for null.

### 4.2 Arithmetic

`+ - * / %` and unary `-` on numbers; `+` concatenates when either side is a string. Division or
modulo by zero → `error(div-by-zero)`. Other operand kinds → `error(type-mismatch)`.
`missing` operands propagate `missing`.

**Component-wise arithmetic on structs.** A struct whose type adapter declares its numeric **components** (the
*Arithmetic* capability, §13: vectors, rotators, colors — never quaternions or matrices, whose products are not
component-wise) takes part in arithmetic component by component, and the result is a new value of the same type:

| Expression | Result |
|---|---|
| `v * n`, `n * v`, `v / n` | every component multiplied / divided by the number `n` (`Scale * 1.5`). |
| `v + w`, `v - w`, `v * w`, `v / w` | `w` of the same type: component with component (`Position + Offset`). |
| `v + [a, b, c]` (any of `+ - * /`, either side) | a list of exactly as many numbers as `v` has components is read as a value of `v`'s type (`Position + [0, 1, 0]`). |
| `-v` | every component negated. |

Components are computed as numbers (single-precision components widened as in §1.1) and the adapter builds the
result. Everything else is `error(type-mismatch)` naming both operands: two different struct types (`Vector3 + Vector2`),
`v + n` / `n - v` / `n / v`, `v % …`, a list whose length differs from the component count or that holds a non-number,
and a struct whose type declares no components (the message then says so). Division by a zero number or a zero
component is `error(div-by-zero)`. A list alone is not a vector: `[1, 2, 3] * 2` stays `type-mismatch` (lists only take
part next to a struct, whose type gives them their meaning). `+` with a string still concatenates. Writes need no
coercion: `set Scale = Scale * 1.5` assigns a value of the member's own type.

### 4.3 Functions (portable core)

| Function | Result |
|---|---|
| `has(path)` | `true` if `path` is not `missing` and every map lookup in it finds its key (an absent key reads as `null` elsewhere, §3, but `has(Props["mp"])` is `false` without the entry); the argument is not evaluated beyond the path. |
| `is-null(x)` | same as `x is null`. |
| `len(x)` | string length, list/map size; otherwise `missing`. |
| `lower(s)`, `upper(s)` | invariant case conversion. |
| `abs(n)`, `round(n[, digits])`, `floor(n)`, `ceil(n)` | numeric. |
| `sqrt(n)`, `pow(base, exponent)` | numeric (IEEE: `sqrt` of a negative number is NaN). |
| `min(a, b, ...)`, `max(a, b, ...)` | the smallest / largest of two or more numbers (the `min` / `max` *stages* and *methods* aggregate a stream or a collection instead; with one argument the call is an `arity` error). |
| `if(cond, then, else)` | `then` when `cond` is `true` as a predicate (§1.2), otherwise (`false`, `null`, unknown, an error value) `else`; any other `cond` value fails the query (§1.2). **Lazy**: only the taken branch is evaluated, so it can guard an error (`if(Count > 0, Total / Count, 0)`). The text form `cond ? then : else` (lowest precedence, right-associative, §11) is the same function. |
| `ref(s, ...)` | root scope only: resolve object references. |
| `append(list, v, ...)` | a new list: the elements of `list` followed by the values. |
| `insert(list, i, v)` | a new list with `v` inserted before index `i` (negative `i` counts from the end, `-1` appends). |
| `remove-at(list, i)` | a new list without the element at `i` (negative from the end). |
| `without(list, v)` | a new list without the elements equal to `v` (§4.1). |
| `range(start, end[, step])` | the list of the numbers `range` streams as a source (§2); each number counts against `maxVisited`. Errors (a non-number, a zero step) are `type-mismatch` error values. |
| `replace(s, from, to)` | `s` with every occurrence of `from` replaced by `to`, left to right, non-overlapping. `from` matches **ordinal case-insensitive** (like `contains`); an empty `from` returns `s`. |
| `replace-regex(s, pattern, repl)` | `s` with every match of `pattern` (the `matches` dialect and case-insensitivity, §4.1) replaced by `repl`, in which `$0`–`$9` insert the match / group text (empty when the group did not take part or does not exist), `$$` inserts `$`, and any other `$` is literal. |
| `substring(s, start[, length])` | the part of `s` from `start` (negative counts from the end), at most `length` characters (default: to the end). `start` and the end are clamped to the string, so out-of-range positions give a shorter or empty string; a negative `length` is a `type-mismatch` error value. |
| `split(s, sep)` | the list of the parts of `s` between occurrences of `sep` (ordinal case-insensitive; empty parts kept: `split("a,,b", ",")` → `["a", "", "b"]`). An empty `sep` is a `type-mismatch` error value. |
| `trim(s)` | `s` without leading and trailing whitespace (space, tab, LF, CR, vertical tab, form feed). |
| `pad(v, width[, fill])` | the text of `v` (a string, or a number written as `+` concatenation writes it) left-padded with `fill` (a one-character string, default `"0"`) to at least `width` characters (an integer from 0 to 1024): `pad(3, 2)` → `"03"`, `pad("ab", 4, " ")` → `"  ab"`. Text already that long is returned unchanged; a sign is text like any other (`pad(-3, 4)` → `"00-3"`). |

Numeric functions return `null` / `missing` / an error value unchanged when an argument is one (the first such argument),
and a `type-mismatch` error value for any other non-number argument.

List functions never modify their argument; assign the result (`set Tags = append(Tags, "x")`). A first argument that is
not a list, a non-integral index or an index out of range yields a `type-mismatch` error value.

**String functions** (`replace`, `replace-regex`, `substring`, `split`, `trim`, `pad`) also return the first `null` /
`missing` / error argument unchanged; any other argument of the wrong kind (a non-string text argument, a non-integral
position) yields a `type-mismatch` error value. Positions and lengths count UTF-16 code units, like `len` (a cut
inside a surrogate pair is not portable). They are also methods on the string: `Name.replace("Pillar_", "Column_")`,
`Name.substring(0, 4)`, `Label.trim()`, `Index.pad(3)`. `replace-regex` with a literal pattern that does not compile
fails the query (`invalid-regex`) before it runs; a computed one yields an `invalid-regex` error value plus one warning,
and a match over the regex time limit a `timeout` error value plus one warning. Renaming by pattern:
`… | set Name = replace-regex(Name, " \((\d+)\)$", "") + "_" + pad(replace-regex(Name, "^.*\((\d+)\)$", "$1"), 2)` turns
`Tree (3)` into `Tree_03`.

Collection **methods** (`list.method(args)`), args evaluated per element with `$` = element:

| Method | Result |
|---|---|
| `any([pred])` | `true` if some element's pred is `true` (no pred: non-empty). |
| `all(pred)` | `true` if every element's pred is `true` (empty → `true`). |
| `count([pred])` | number of elements (whose pred is `true`). |
| `sum(e)`, `min(e)`, `max(e)`, `avg(e)` | aggregate of `e` over elements (non-numbers ignored; `min`/`max` also order strings). Empty: `sum` → `0`, others → `null`. |
| `first([pred])` | first element (matching), or `missing`. |
| `keys()`, `values()` | for maps. |

Aggregates (methods and stages) skip `null` and `missing` silently. Any other value they skip — a non-number for
`sum` / `avg`, a value that is not orderable against the others for `min` / `max` (e.g. a bool, an object, or a
number among strings), an error value such as `Mass / 0` — is counted, and the query gets one `type-mismatch`
**warning** per aggregate kind naming how many values were ignored (for a method evaluated per item: in the first item
that ignored any), so `sum Name` reports why its answer is `0`. Values whose member read threw are reported by
`getter-threw` instead.

Methods/functions not in this table are **extensions** (§9).

### 4.4 Map entries

Iterating a map (as a source, in `get`, or in methods) yields entry values with members `key`
and `value` (member lookup is case-insensitive, so `Key` works too), in the map's own iteration
order (runtimes with unordered maps MUST use insertion order or sort entries by key for
determinism). Any other member accessed on an entry is looked up on its value, so
`world.Assets | where Type == "Material"` filters assets directly. Entries encode as `{"key": ..., "value": ...}`. `m[1]` on a map is a key lookup.

### 4.5 Object literals

`{ key: expr, … }` builds a value: an ordered map from string keys to values. Keys are names, strings or `$`-names (`{$type: "PlaneMesh"}` is `{"$type": "PlaneMesh"}`), non-empty and
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
| `distinct [e]` | keep the first item for each distinct value of `e` (default `$`; objects by identity; projected rows by their column values, so `select Tag \| distinct` keeps one row per tag; lists by their elements, like `==` (§4.1), so `distinct Tags` keeps one item per tag list; structs by their type and member values with numbers rounded for display, below; other values by value). It keeps **items**, not the values of `e`: `distinct Tag` returns one whole item per tag; to list the values project first (`select Tag \| distinct`) or count them (`group-by Tag`). |
| `sum/min/max/avg e` | terminal aggregate, same rules as the collection methods (`sum` of nothing → `0`). |
| `group-by e` | replaces the stream by rows `{key, count}` (members `key`, `count`) in order of first appearance of the key; `null`/`missing` keys form one `null` group. Can be followed by `sort count desc`, `where count > 1`, etc. **Group items**: every group row also has the member `items`, the list of the group's items in stream order, so per-group aggregates are collection methods (§4.3): `group-by Category \| select key, count, avg = items.avg(Weight)`, `items.sum(Mass)`, `items.count(Enabled)`, `items.first().Name`. `items` is not a column (not encoded with the row); a key column named `items` takes precedence. Runtimes MAY keep the items only when the plan reads a member named `items`. A member missing on group rows gets a hint that shows `items.avg(…)` with the rows' own key columns (`group-by mat=(…) | select key` → `select mat, count, avg = items.avg(…)`; a missing key column is never offered as the member to aggregate). |
| `group-by [n=]e, [n=]e ...` | several keys, or a named key: one row per distinct **combination** of key values, in order of first appearance, with one column per key — named like `select` columns (alias, or the key's source text: `group-by Folder, $type` → columns `Folder`, `$type`, `count`) — then `count`. Key values compare like `distinct` (lists by their elements: `group-by Tags` merges equal tag lists); `null`/`missing` values group as `null` per column. Keys are separated by commas or whitespace like `select` fields; computed keys need parentheses (`group-by Kind, big=(Mass > 10)`). Column names must be distinct (case-insensitive) and none may be `count` (`syntax-error` / `invalid-plan`). A single key without a name keeps the `{key, count}` form above. |
| `describe` | terminal: schema of the distinct runtime types of the items (members, kinds, writability). |
| `set p = e, ...` | mutation (§8). |
| `call m(args)` | mutation: invoke an allowlisted method on each item (§8). |
| `call q.m(args)` | mutation: invoke the method `m` of the one object the source expression `q` yields, once per item (source-qualified call, §8). |

**Struct keys** (`distinct`, `group-by`). A struct is keyed by its type and the values of its members (the members its
default encoding shows, §6: explicit-only members excluded, and members of the struct's own type — derived values such
as `normalized` or `inverse` — left out; nested structs likewise, two levels deep like encoding; a struct type without
members keys by the runtime's own equality), where every number
is rounded the way numeric tuples are displayed (§13): a single-precision number to 6 significant digits, any other
number to 12, a magnitude below `1e-6` (single) / `1e-9` (other) to `0`, and `-0` to `0`; `NaN` keys like `NaN`. So
values that print alike group together: `select Scale | distinct` keeps one `(1, 1, 1)` although one Scale is
`0.99999994`. Two structs share a key exactly when their members print alike by that rule (a type adapter's display may
differ: members it does not show still count, so adapters hide derived members such as `magnitude` if they vary in the
last digits); this is a key, not a tolerance (rounding
is transitive, a tolerance is not), so near a rounding boundary two values that `==` calls equal (`1.0000049` and
`1.0000051`, printed `1` and `1.00001`) still form two groups. `==` is not rounded: structs compare through their type adapter (numeric tuples within a tolerance, §13), otherwise
member by member with exact numbers (single precision compared as floats, §4.1). Scalars keep exact numeric equality.

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
trailing string `"…+N"`); maps → JSON object (string keys) of encoded values with the same limit. A list or map nested
inside itself (or nested more than 32 levels deep) encodes the repeated (or too deep) occurrence as the string `"…"`.
Numbers: integral values without fraction; floats in shortest round-trip form of the *runtime's
own precision* (a C# `float` 0.1f encodes as `0.1`, not `0.10000000149`; `(float)Math.PI / 2` as `1.5707964`, the
shortest of the 9-digit forms, not `1.57079637`). Non-finite numbers encode as the strings `"NaN"`, `"Infinity"` and
`"-Infinity"`. This JSON form is exact: it is what plans, receipts and hosts may parse back.

**Display numbers.** Text meant for agents and people (the compact `table` encoding of the tool layer,
`docs/agent-tool-contract.md`) prints every number with at most 15 significant digits, so a computed
`0.1 + 0.2` shows as `0.3` (its JSON form stays `0.30000000000000004`); integral values print without fraction and
non-finite values as `NaN` / `Infinity` / `-Infinity`. Numeric tuple text written by type adapters follows §13.

`unchanged` (`set` receipts only): the number of no-op assignments left out of the rows (§8).

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
* A `ref()` text that is not a reference (no `Type#id` / `Type@n` form, typically an object name) is `stale-ref` too;
  its hint says that `ref()` takes a `$ref`, not a name, and how to find the object by name — the host's finder when
  the host names one (e.g. `unity.find("Floor")`), otherwise a `where Name == "…"` query.
* Object identity (for cycles, `distinct`, `==`) is reference identity in the owning runtime.
* The identity member that backs `$ref` is always read-only through RQL (`readonly-member`).

## 8. Mutations

Mutations are disabled unless the caller's policy enables them (`permission-denied` otherwise).
A request that asks for mutation (`options.allowMutation`) must end in a `set` or `call` stage; otherwise it is
refused with `invalid-plan` before anything runs (the mutation tool never doubles as a query tool, and a forgotten
`set` is reported instead of silently returning rows).

* **Empty through a typo**: when no item reaches the `set` / `call` stage, every `unknown-member` warning of a member
  read that was **tested on items and missed on all of them** becomes an `unknown-member` **error** (its message adds
  that nothing was changed), provided **no known type declares** the name — no type statically reachable from the
  sources (the types the sources have produced so far: a source that is null right now, such as an empty selection, keeps
  the types it had, so runtimes MAY cache the names by those types), no target type of a registered type adapter (§13;
  its adapter-defined members included), no `select` / `group-by` column and nothing the host's known-member hook
  accepts:
  `objects | where name == "X" | get transfrom | set position = …` fails instead of reporting "0 changes". These stay
  warnings with an empty receipt: a name some known type declares, so a polymorphic stream whose items happen not to
  have the member (`components | where intensity > 1 | set …` with no light among them) is a legitimate empty change;
  a read inside `has(…)` (the guard's answer); misses on objects whose type the runtime does not declare (dynamic
  objects whose members it learns per instance); and names never evaluated (§10), because a filter that matched
  nothing leaves the later stages unevaluated. Hosts whose type knowledge is wider than static reachability (every
  component type of an engine, items from function sources or `ref()`) SHOULD supply a **known-member hook**
  (name → bool); it also silences the static "not evaluated" warning (§10) for those names.
* Targets must be objects with identity; value-type or scalar items (e.g. from `get Transform.Position`)
  are rejected with `invalid-plan` because a change to a copy would be lost.
* Writing through value types requires every value-type parent member on the path to be writable and
  allowed by policy, since the modified copy is written back into it.
* A request's `maxMutationTargets` (in any request that passes it, read-only ones included) must be an integer from 1 to
  the host's hard ceiling (target limit, below). A value outside that range (`0`, `1001` with the default ceiling) is refused with `invalid-value` before anything runs, naming
  the limit argument (§10) and the range — never clamped, so a caller never believes it raised the limit when it did not.
* `set p = e`: for each item, evaluate `e` against the item, coerce to the member type of `p`
  (number→integral only if integral and in range; string→enum by enum name resolution, below; null only for nullable /
  reference types; list literal → list/array type; object literal → map / constructed object, §4.5), then write. Writing through value types
  (`Transform.Position.x = 1`) MUST write back the modified copy to every enclosing value-type
  member up to the first reference object. Target member must be writable and allowed by
  policy (`readonly-member`, `permission-denied`). An indexed target (`set Tags[1] = "x"`) replaces an existing
  element; an index out of range is `invalid-plan`, with a hint to assign the whole list instead
  (`set Tags = append(Tags, "x")`, `insert(…)` or a list literal).
* `call m(args)`: only methods allowlisted by the runtime policy; arguments coerced as above. A method MAY declare a
  **validator** (target, coerced arguments → rejection reason or none; .NET: the `validate` of an adapter method, or a
  public companion `string Validate<Name>(same parameters)` next to an `[RqlCallable]` method). Every target's call is
  resolved, coerced and validated before the first call — on dry runs too, so a dry run reports a call that would be
  refused — and a rejection fails the stage with `invalid-value` (naming the method, the target and the reason) with
  nothing called. Methods without a validator behave as before. A method that throws stops the stage with error
  `call-threw` (naming the method and the exception); calls completed before it are not rolled back (calls cannot be
  undone generically: hosts make them safe through validators and batch validation, below).
* **Scope of values and arguments**: a `set` value and a `call` argument are evaluated per target with `$` = the
  target, and `$index`, `$path` and `$depth` are the target's as it entered the stage: `$index` is its position in the
  stream entering `set` / `call` (null and destroyed items, which are skipped, keep their positions), so
  `nodes | where class == "RigidBody3D" | set Position.X = $index * 2` spreads the matches 0, 2, 4, ….
* **Source-qualified call** `call q.m(args)`: `q` is a source expression (§2: a registered source with optional
  navigation such as `editor` or `world.Scenes[0]`, `ref(…)`, a host function), evaluated **once**, after the mutation
  policy check (a read request is `permission-denied` without evaluating it) and before any item is read; it must yield exactly one object with identity, the **receiver**. Its method `m` runs **once per stream item**,
  and the item is only the scope of the arguments: `$`, `$index`, `$path` and `$depth` in them are the item's, and an
  item may be any value — a number from `range`, a list entry, a string, a projected row, a map entry or an object
  (null and destroyed items are skipped as above). So `range(0, 300) | call editor.CreateObject("Cube", {position:
  [($ % 15) * 2, 0, floor($ / 15) * 2]})` creates 300 objects in one call. Everything else is the unqualified `call`'s:
  the allowlist, coercion and overloads, per-invocation validators and batch validation (records name the receiver as
  target; every invocation is resolved, coerced and validated before the first, on dry runs too), the host's
  before-write hook receiving all invocations at once (one undo step), `call-threw` stopping the stage with the earlier
  invocations completed, and the target limit, which counts **invocations** (items reaching the stage; on
  `mutation-limit` the message counts items, not objects). The receiver's method is resolved before the stream is read,
  so a misspelt method fails (`unknown-function`) even when no item arrives. A receiver that is not one object fails
  before anything runs: a list or map → `invalid-plan` (its hint shows `q | call m(…)` for calling every element), null
  or destroyed → `invalid-plan`, a struct or scalar → `invalid-plan`, an error value (e.g. `stale-ref`) → that code, and
  a name that is not a source → `unknown-source` whose hint shows `… | get Member | call m(…)` for a method of each
  item's member. Messages about one invocation name the receiver and the item's position (`for item 3`).
* **Calls on items that are not objects**: an unqualified `call` whose items are numbers, strings or other value copies
  is `invalid-plan` (targets must be objects); its hint shows the source-qualified form, naming the sources whose
  object has the method (`… | call editor.CreateObject(… $ …)`).
* **Map entries as targets**: a `set` target that is a map entry streamed from a map (`… | get Rows | where
  Value.IsElite | set Value.Health = Value.Health * 1.1`) writes through that map: `Value.p` (and a bare `p`, which reads
  through to the value like member access, §4.4) is the path `[key].p` on the map, and `Value` alone replaces the
  entry's value. When `get` read the map by a plain path (members, `[i]`, `["k"]`), the write is the ordinary `set` of
  `path[key].p` on the item `get` read it from (`Rows["Orc"].Health`); entries of a map read from an entry continue
  that entry's path (`… | get Index | get Value.Props | set Value = 1` writes `Index["a"].Props["hp"]`). Everything
  else follows from that path — coercion to the map's value type, struct values and copy-on-read members written back,
  validation of virtual-map entries, read-only maps refused (`readonly-member`), owners for host hooks, rollback, dry
  runs, budgets. Otherwise (a map source, a computed `get` expression, `recurse`) the write starts at the map itself and
  the item `get` read it from, when there is one, is the owner unless an object inside the value is; a map that is a
  copy (built by a getter) then does not keep the change. `Key` cannot be set (`readonly-member`). Receipts carry one
  row per entry: `$ref` is the owner of the change (the nearest enclosing object, §13) and `member` the written path.
  `call` on entries is `invalid-plan` (call the values: `… | get Value | call …`).
* **Enum name resolution**: a string meets an enum type (a write coercion, a map key, a comparison) and is resolved
  to one entry. Leading and trailing spaces are ignored; integer text is the underlying value (writes accept only
  defined values, or any combination for a flags enum); a runtime whose enums support flags MAY accept a comma-separated
  list of names, each resolved by these rules. Otherwise the first step that finds a candidate decides:
  1. an entry name equal to the text (ordinal, case-sensitive), or a host alias equal to it;
  2. when the text is qualified — `Type::Name`, or `Type.Name` when it has no `::` — the steps 1, 3 and 4 run on the
     part after the last separator (`"EWidgetSpace::World"` → `World`, `"NodeKind.gamma"` → `Gamma`). The qualifier is
     not checked against the type's name: hosts name types differently from the source code agents copy from
     (`EWidgetSpace` / `WidgetSpace`), and the entry must exist in the target type anyway;
  3. entry names and host aliases equal to the text ignoring case;
  4. entry names without their **entry prefix** (the text up to and including the first `_`, when both that part and
     the rest are non-empty) equal to the text ignoring case: `"Fill"` → `HAlign_Fill`.

  Candidates are counted as distinct entries (an alias and its entry are one). One candidate is the result; several
  stop the resolution as **ambiguous**: a write fails with `coercion-failed` listing the candidates
  (`"Fill" is ambiguous for EAlign: HAlign_Fill, VAlign_Fill`), a comparison is not equal. No candidate is
  `coercion-failed` listing the entry names (comparison: not equal). Exact matches thus always win over the lenient
  steps, and a lenient step never picks between two entries. **Host aliases**: a host MAY declare alias names for
  enum entries (an engine's display names, `"Fill"` for `HAlign_Fill`); they take part in steps 1 to 3 like entry
  names (not in step 4), encoding still uses the entry name.
* **Host text coercion**: a host MAY register a text coercion that converts a string written to a struct or object
  type that no type adapter coerces (an engine's own text form, `"(Minimum=(X=0.5,Y=0),Maximum=(X=0.5,Y=0))"`). It
  runs after the adapters and before the built-in rules, on every write coercion (`set`, `call` arguments, object
  literal members); declining leaves the built-in rules, a refusal is `coercion-failed` with its reason.
* **Batch validation**: a host MAY register a batch validator that receives every planned change of the stage at
  once (`set`: target, owner, member, old and coerced new value; `call`: target, method and coerced arguments) after
  every target passed its own checks and before the first write or call — **on dry runs too**. A reason it returns
  (or an exception it throws) refuses the whole stage with `invalid-value` (naming the reason), nothing written or
  called. It serves rules that span targets: two calls saving to the same file, a component whose dependency is
  removed by the same `call`. Per-target validators (§13) still run first.
* **All-or-nothing**: every target and assignment is resolved and coerced before any write. If
  more than `budgets.maxMutationTargets` items reach the stage → `mutation-limit`,
  nothing written. If a write fails midway the runtime MUST restore already-written values.
* **Target limit**: the limit guards the *breadth* of a change — a filter that is wider than intended (a forgotten
  `where`, a name that matches more than expected) — not its correctness: resolution, coercion, validation, batch
  validation, the host's before-write hook (e.g. one undo step for the whole stage), rollback on failure and dry runs
  apply to a change of any size. Its default is the host's (`MaxTargets`, 20 unless the host sets it): hosts that
  record the whole change as one undoable step (editors) SHOULD use a larger default (e.g. 200); hosts without undo
  keep a small one. A request MAY raise it through `budgets.maxMutationTargets` up to the host's ceiling (`HardMaxTargets`,
  default 1000), which bounds the cost of one stage (planning, receipts, undo memory); raising it is always an explicit
  per-request decision, and whether a host's callers can raise it is independent of whether they can pass the other
  budgets (§10). On `mutation-limit` the runtime keeps counting the items that reach the stage, up to the ceiling (the
  earlier stages run for them as usual; null and destroyed items are skipped as targets are; the stage itself checks
  and evaluates nothing for the extra items), so the message states how many items matched (`160 objects matched
  'set', more than the limit of 20`) or that more than the ceiling did. A budget error (`budget-exceeded`, `timeout`)
  while counting is reported instead of `mutation-limit`: it is what the caller must fix first. A limit argument or
  hint override the host sets to an empty text counts as unset.
* **Dry-run** (`options.dryRun`) computes and returns the receipt without writing.
* Receipt: `kind: "rows"`, columns `["$ref", "member", "old", "new"]` (`call`: `["$ref", "method", "result"]`, one row per
  invocation; `$ref` is the target's, and for a source-qualified call the `$ref` of the returned object when the method
  returned an object with identity — the created object — otherwise the receiver's; on a dry run the result is
  `"(dry-run)"` and `$ref` the target's / receiver's). A `set`
  receipt lists only the assignments that changed the value: one whose planned (coerced) value **and** read-back `new`
  (below; the planned value on a dry run) both equal its `old` by `==` (§4.1) is a **no-op** — a write the host refused or
  clamped back to the old value is not, it stays listed so the caller sees that it did not take; a no-op is still
  written (setters and host hooks see it) and omitted from `rows`, and the result's `unchanged` field counts the omitted assignments (absent when none), so
  `set y = (y < 0 ? 0.5 : y)` over 40 items reports the 4 that changed plus `unchanged: 36`. A receipt with no rows but
  `unchanged > 0` is not an empty mutation (the typo rule above does not apply).
  After the writes, `new` is the value **read back** from each written member, i.e. what the host actually stored (a
  setter or the host may clamp, normalise or ignore a value: `set mass = -1` can report `1E-07`, an unknown tag can
  keep the old one). On a dry run nothing is written and `new` is the planned (coerced) value. If the read-back fails,
  `new` is the planned value and the query gets a `getter-threw` warning. Host committed hooks (§13) receive the
  read-back values too.

## 9. Extensions

Hosts register **sources**, **functions** (`ns.fn(args)`), **methods**, **stages** (`ns.stage args`)
and **type descriptors**. Names of non-core extensions MUST be namespaced (`unity.`, `unreal.`,
`myapp.`). The parser accepts unknown names structurally; binding fails with `unknown-stage`/
`unknown-function` if no extension is registered. In expressions `ns.fn(x)` parses as method `fn`
on path `ns`; when the path is a bare dotted identifier chain that matches a registered function
namespace, the binder treats it as the function `ns.fn`. An extension function that throws evaluates to
`error(function-threw)` for that call, and the query gets one `function-threw` warning naming the function and the
exception (a getter that throws is `getter-threw`, a `call`ed method `call-threw`).

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
| `maxMutationTargets` | the host's (20 unless set, §8) | `mutation-limit` |

Budget hints: the hints of `budget-exceeded` and `timeout` advise raising a budget (`budgets.*`) only when the host
declares that its callers can pass request budgets (default: they can); hosts whose tools take no budgets argument turn
that off, so no hint names a setting the caller cannot reach. The target limit (§8) has its own **limit argument**: the
name under which the host's callers raise it (e.g. a tool argument `max_targets`). When the host names none, it is
`budgets.maxMutationTargets` if callers can pass budgets, and otherwise callers cannot raise the limit. The
`mutation-limit` hint:

* when callers can raise the limit and the ceiling is above it, and the matched count is within the ceiling: first
  advise raising the limit argument to the count, which changes every item in one all-or-nothing call
  (`pass max_targets = 160 (host ceiling 1000) to change all 160 in one all-or-nothing call (a dry run previews it);
  or narrow the query (where / ref(...))`);
* when more items matched than the ceiling: advise raising the limit argument to the ceiling and splitting the change
  into batches of the ceiling (as below, with the ceiling as batch size), or narrowing the query;
* when callers cannot raise the limit: narrow the query or split the change into batches of the limit
  (`… | first 20 | set …`, then `… | skip 20 | first 20 | set …`, or `first 20` again when the change removes the items
  from the filter).

A host MAY replace the whole hint.

Diagnostics: `{severity: "error"|"warning", code, message, at?: {start, end}, hint?}`. Error codes
are a closed set per release (package 0.2.0 added `call-threw`, `function-threw`, `hook-threw`, `invalid-value` and
`invalid-regex`, see `versioning.md`; clients should treat an unknown code as a generic error): `syntax-error`, `invalid-plan`, `version-unsupported`, `unknown-source`,
`unknown-stage`, `unknown-function`, `unknown-member` (warning: no evaluated item had it; an error when it empties a
`set` / `call`, §8),
`unknown-meta`, `type-mismatch`, `arity`, `div-by-zero`, `getter-threw` (a member read threw), `call-threw` (a `call`ed
method threw, §8), `function-threw` (an extension function threw, §9), `hook-threw` (warning: a host committed hook
threw, §13), `invalid-regex` (§4.1), `stale-ref`,
`permission-denied`, `readonly-member`, `coercion-failed` (a value cannot be converted to the member type),
`invalid-value` (the value converts, but the host's validation of the member rejects it, §13), `mutation-limit`, `budget-exceeded`,
`timeout`, `output-truncated`, `unsupported`, `internal-error` (a runtime bug surfaced as a diagnostic instead of
a crash), `no-match` (warning: an `x is T` test was false
for every tested item; the hint names where `T` objects are reachable).

`unknown-member` SHOULD carry a `hint` with the closest member names of the observed types —
this is the main self-correction channel for agents.

Hints never present a source or tool as available unless the host has it (syntax examples in parser hints use
placeholder names), and never name request options (`options.allowMutation`) the caller does not see. A host MAY name its
**schema tool** (e.g. `rql-schema`); hints that point to the list of types say "list the types with rql-schema",
otherwise "with the schema tool". A host MAY name its **mutation tool** (e.g. `rql-mutate`): a `set` / `call` sent without
a mutation request is `permission-denied` "… requires a mutation request: run it with rql-mutate", otherwise "with the
mutation tool". A `call` of a method the targets do not have (`unknown-function`) lists the targets' callable methods and also
names every source whose value is a single object that has a callable method of that name, with both forms
(`objects | call CreateGameObject(…)` → "the source 'editor' has CreateGameObject: editor | call CreateGameObject(…) calls
it once, … | call editor.CreateGameObject(… $ …) once per item").

**Suggestions** ("did you mean …?" for members, sources, stages, functions and type names) rank candidates the same
way in every runtime. Comparison is ASCII case-insensitive. A candidate qualifies when its *optimal string alignment*
distance to the name (Damerau–Levenshtein without repeated edits of a substring: insertion, deletion, substitution and
the transposition of two adjacent characters each cost 1, so `Lable` → `Label` is 1) is at most `max(1, ⌊len/3⌋)` of
the name's length, or when it contains the name (at least 3 characters), or when the name contains it and it is at
least half as long as the name (`LabelText` → `Label`, but not `NoSuchType` → `Type`). Order: containment first, then
distance, then ordinal name; at most 3 suggestions. When nothing qualifies, a hint MAY list the type's members, at most
12 of them followed by `… (N more)`, **ordered by distance to the name** (then ordinal), never in declaration order.
**Nested members**: the candidates of an `unknown-member` hint (a read or a `set` target) also include the members of the
type's **struct-typed** members, one and two levels down, as dotted paths (`BodyInstance.CollisionProfileName`), from
static type information only (no getter runs; at most 200 nested paths per type). A nested path qualifies and ranks by
its last segment, with the rules above (containment, then distance), and among equal ranks a direct member comes before
a nested one, a shallower path before a deeper one, then ordinal; so `set CollisionProfileName = …` on a component whose
real member is `BodyInstance.CollisionProfileName` gets "did you mean BodyInstance.CollisionProfileName?". The member
list shown when nothing qualifies stays the type's own members.
**Mixed streams**: an `unknown-member` hint names the known types that do have the member (statically reachable types,
least derived first, at most 3) and suggests filtering to them: "'light_energy' is a member of OmniLight3D: filter to
those first, e.g. where $ is OmniLight3D (or where has(light_energy))". A `set` whose first target segment some targets
lack while other targets have it fails with `unknown-member` naming how many targets lack it and the targets' types that
have it ("only OmniLight3D has 'light_energy': filter first, e.g. … | where $ is OmniLight3D | set …"), and an unknown
`call` method names the targets' types that have it likewise.
Runtimes SHOULD also report `unknown-member` for a bare name that was never evaluated (short-circuit,
empty stream, the untaken branch of `if`) when no type statically reachable from the sources or adapted by a type
adapter declares it, no `select`
alias produces it, no other read of the name found it and the host's known-member hook (§8) does not accept it.

## 11. Lexical conveniences (normative)

Parsers MUST bound expression nesting (reference: 100 levels) and operator count (reference: 1000) and
report `syntax-error` beyond them; numeric literals must be finite; select aliases may not start with `$`.

Numbers in RQL text use ASCII digits only. In QueryPlan JSON an integer field accepts any JSON number with an integral value (`2` and `2.0`), never `2.5`.


To reduce agent errors the parser accepts, and canonicalizes away:

* Keywords `and or not in is null true false` and core stage names in any letter case.
* `=` as equality in expressions (`where Name = "x"`), `&&`, `||`, `!` for `and`, `or`, `not`.
* `limit N` as an alias of `first N`; `expr as name` as an alias form in `select`.
* `cond ? then : else` for `if(cond, then, else)` (§4.3). Like every complex expression it needs parentheses where the
  grammar takes a `postfix` (`select kind = ($ is Light ? "light" : "other")`); a `?` whose then-branch is not followed by `:` is a
  `syntax-error` whose hint shows the form.
* `x is null` / `x is not null` canonicalize to `==`/`!=` with a null literal; `x not in L` and
  `x is not T` to `not(...)`; a negated numeric literal folds into the literal.

Comparisons do not chain: a comparison (`==`, `<`, …, `contains`, `starts-with`, `ends-with`, `matches`, `in`, `is`)
followed by another comparison operator (`path contains "addons" == false`) is a `syntax-error` that says so and shows
the fix (`not (path contains "addons")`). One query per call: when a syntax error falls on or after a line that starts a
new query (a name, optionally a call, then `|`) after a non-blank line that does not end in `|`, the hint says "one query
per call" and names the line.

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
| `pow` edge cases | The host math library's IEEE `pow`: `pow(1, NaN)` and `pow(-1, ±Infinity)` are `1` in .NET and C++, NaN in JS. Portable plans must not depend on them. |
| Regex dialect | Only the §4.1 subset is portable; a pattern outside it may be valid in one runtime and `invalid-regex` in another (e.g. .NET inline options `(?i)`). Outside the subset, `matches` answers and `replace-regex` results can also differ: .NET `$` matches before a final newline and `\d` / `\w` match Unicode digits and letters; C++ `std::regex` works on UTF-8 bytes (ASCII-safe; a match that would split a character is skipped). |

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
| Methods | Add callable methods (name, typed parameters with optional defaults, result type, description, optional validator, §8) invoked by the `call` stage exactly like host-allowlisted methods: arguments are coerced to the parameter types (adapter coercion applies), overloads are chosen by arity then coercibility, results go to the receipt, `dryRun` invokes nothing, and calls are not rolled back. Runtimes with method reflection MAY also expose existing host methods by name (for engine types that cannot be annotated) and hide methods. `describe` lists every callable method as `name(type param, …)` plus the description. |
| Virtual maps | A member whose value is a string-keyed map defined by the adapter: the owner's keys, per-key get, optional per-key set and validation. An optional per-key existence test avoids scanning the keys on lookups. Reads and writes use the ordinary map paths (`M["k"]`, `set M["k"] = v`): values are coerced to the declared value type, validation runs before any write (`invalid-value` with the reason), writes take part in rollback, a map without setter is `readonly-member`, writes go to existing entries only (an absent key is `invalid-value` "no entry", because a new entry could not be
removed again on rollback), and entries are never removed. Schemas show the type as `Dictionary<string, T>`. |
| Encode | Replace the output encoding of values of the type (first adapter that encodes wins). |
| Coerce | Convert a value supplied by a query (literal, list, other type) to the type on writes (`set`, `call` arguments). Values already of the target type bypass it. A failure carries a reason → `coercion-failed`, reason in the message. |
| Compare | Equality and/or ordering of a value of the type with any other value (first adapter of the left operand that answers wins, then of the right operand with the result mirrored). Ordering applies everywhere values are ordered: comparison operators, `sort`, `min` / `max` (which also accept values whose type has an ordering adapter). An adapter typically coerces the other operand, so `Pos == "(1, -1)"` works. |
| Identity / liveness | Stable id for `$ref`, resolution of `Type#id`, and an `IsAlive` verdict. A destroyed object (the liveness verdict — first answering adapter, then the host's liveness hook; only objects, never collections, maps, structs or scalars, are asked — is "not alive", e.g. Unity's fake-null) **reads as null everywhere**: the conversion happens where values enter expressions (member reads, list / map elements, sources), so `== null` / `is null` / `coalesce` / sort / `distinct` / encoding all see `null`, members of it read as members of null (no getter runs), stream expansion skips it like a null element (collection methods still see the `null`, e.g. `targets.any($ == null)`), it is therefore never a mutation target, and `ref()` to it is `stale-ref`. Adapters may choose the **identity** and **display** members by name (e.g. Unity's lowercase `name`). |
| Write-back | A member can be marked **copy-on-read** (its getter returns a copy, e.g. Unity's `Renderer.sharedMaterials`, `Gradient.colorKeys`): writes into its value are assigned back through the member's setter exactly like value types (§8), with the same writability rules and rollback. A value type can be marked **writes-through** (a handle to shared state, e.g. Unity's `ParticleSystem.MainModule`): writes to its copies are live, so no write-back to the owner happens, get-only owners are fine, virtual setters on it are allowed, and its values may be mutation targets. |
| Exclusion | Hide members by predicate (e.g. every `[Obsolete]` member), and engine-wide exclude member **types** (a member is dropped when its type, element type or a generic argument matches), e.g. delegates, native pointers or assemblies that must never be touched. |
| Literals | Coercion receives object literals (§4.5) and may build values itself; an adapter may publish a JSON Schema of the literals it accepts, shown by `describe` as `literal`. |
| Describe | A one-line description of the type for schemas and `describe`, e.g. how to write literals. |
| Arithmetic | Opt-in component-wise arithmetic (§4.2) for a struct type: the adapter reads a value's numeric **components** (always the same count for the type, in a fixed order) and **builds** a value of the type from such a list. The first applicable adapter that returns components decides; a build that fails is `type-mismatch`. Hosts opt in only types whose arithmetic is component-wise (vectors, rotators, colors), never quaternions or matrices. |

**Numeric tuples** (vectors, rotations, quaternions, colors: adapters that encode a struct as text `"(x, y, z)"`, such
as the .NET `NumericTupleAdapter`; hosts with their own tuple adapters SHOULD follow the same rules so agents see one
format across engines):

* *Display*: each component is printed with at most 6 significant digits when the component is single precision and
  12 when it is double precision, a component whose magnitude is below `1e-6` (single) / `1e-9` (double) prints as
  `0`, `-0` prints as `0`, and non-finite components print as `NaN`, `Infinity`, `-Infinity`. So float noise such as
  `(9.999998, 45, 29.9999981)` or `(-8.742278E-08, 0, -2)` reads `(10, 45, 30)` and `(0, 0, -2)`. The text is a
  display form: it parses back to within the equality tolerance, not bit-exactly.
* *Input*: text components accept `nan`, `inf`, `infinity` with an optional sign, case-insensitively, besides
  numbers, so every printed form parses back.
* *Arithmetic*: a tuple adapter MAY opt in to component-wise arithmetic (§4.2) with its components as the arithmetic
  components (.NET `NumericTupleAdapter.Arithmetic`); vector and color adapters SHOULD, quaternion adapters MUST NOT.
* *Equality*: component-wise within a tolerance of `max(1e-5, 1e-5 × |larger component|)` (an absolute tolerance near
  zero, relative for large values, so a printed value compares equal to its source); a NaN component equals nothing.
  The other operand may be the tuple text, a list of numbers, or an **object literal**: its keys name members
  (case-insensitively), members it leaves out keep the compared value's own components, so
  `Position == {x: 0, y: 3}` tests x and y only; a key that is not a member makes the comparison `unknown`. `!=` is
  the negation of that result.

Writes through adapter members keep the all-or-nothing guarantee: old values are read through the member getter before
any write and restored through its setter on failure — including the write whose setter threw, since a setter may change
state before throwing. Adapters SHOULD make virtual setters round-trip their getter;
a lossy member (`set(get())` changes state, e.g. the fixture's case-folding `title`) restores only what its getter
exposes. Validation runs while the mutation is planned, before any write, and rejects with a reason
(→ `invalid-value`, naming the value as the query wrote it, e.g. `invalid value 99 for 'layer'`; a member validated while an
object literal is constructed fails that construction with `coercion-failed`); a setter that throws at write time triggers the rollback. Virtual setters on value types are
rejected at configuration (they would write to a copy).

**Host write hooks.** Hosts may observe mutations: a *before-write* hook receives every planned change (item, owner,
member, old, new; for `call` the method) after planning and before any write, and may veto by failing
(`permission-denied`, nothing changed); a *committed* hook receives the changes after every write succeeded (not on
dry-run or after a rollback; completed calls are reported even when a later call fails). The **owner** is the object
whose state changes: the nearest enclosing object on the written path — never a list, map (including virtual-map
views) or struct (including writes-through handles); the item when there is none. For `set Renderer.sharedMaterial.color`
it is the Material, for `set Uniforms["_Color"]` the material owning the virtual map, for `set main.duration` the particle
system. That is what hosts mark dirty or record for undo. A failing committed hook is a
`hook-threw` warning; the writes stay applied.

**Kernel (adapter composition).** Adapters never call each other. An adapter that needs a nested operation — coerce a
component of a composite literal, compare elements, encode a member — asks the engine's *kernel* for that operation on a
type (`kernel.TryCoerce(value, Color)`, `kernel.Equal(a, b)`, `kernel.Compare(a, b)`, encode via the encode context),
without knowing which adapter, if any, handles the type. The kernel runs exactly the pipeline a query would (adapters by
priority, built-in behaviour, object-literal construction) and owns the cross-cutting rules, so adapters do not repeat
them: (1) **recursion protection** — an adapter already performing an operation for a type is skipped when the kernel is
asked for the same operation and type again, so asking for one's own type yields the next adapter or the built-in
behaviour; a nesting limit (32) bounds the rest; (2) the host's **write policy** applies to every member assigned while
building nested values; (3) **error paths** — a nested failure's reason is prefixed with its location (`[1].color: …`).

**Modules (non-normative, host convenience).** Adapters, type exclusions, sources and functions are contributed by
*modules* (order + configure). Runtimes with module/assembly reflection discover modules by scan (or take them from a
host's type index), configure them in
order (then by name), isolate failures (a failing module is reported, the others still configure) and report the result.
Modules may also contribute agent-facing *guides* (topic + markdown) that hosts render into their help or skill files.
A module that lives in an optional package exists exactly when that package is installed, so enabling or disabling an
engine feature (e.g. Unity's Particle System) enables or disables its adapters.

**Type display names.** A host MAY give runtime types display names — the names its own API and documentation use
(Godot's ClassDB `GPUParticles3D` for the C# class `GpuParticles3D`). The display name is the type's name everywhere a
type is shown: `$type`, `$ref` (`GPUParticles3D#…`), `describe` and schemas (names, bases, member types), hints and
suggestions. `x is T`, `ref("T#id")` and an object literal's `"$type"` accept both the display name and the runtime's own
short name. .NET: `RqlEngine.TypeDisplayName` (type → name or null, cached per type); TypeScript and C++ name types at
registration (`TypeSpec.name` plus the extra `is` names in `interfaces` / the `TypeRegistry` name plus
`ClassBuilder::also_is`), which already gives both.

**Listing.** Engines list their registered adapters in registration order (.NET `RqlEngine.Adapters`, TypeScript
`engine.adapters`, C++ `Engine::adapters()`). The members of adapters' target types, adapter-defined members included,
are known names (§8, §10) without a host hook.

**Scope.** Adapters belong to an engine (a host may run engines with different adapter sets over one type registry);
engines without adapters behave exactly as without this section. An explicit-only member is also not searched by
`ref("Type#id")` fallback resolution, so objects reachable only through one need a host stable-ref resolver.
`distinct`, `group-by` keys and `in`/`contains` over maps hash by built-in value identity (structs by their rounded
members, §5) and do not consult adapter equality in 0.1.

**Conformance.** `fixtures/adapters/cases.json` specifies a test adapter set over the conformance graph that every runtime
implements in its test harness (see the file's `adapters` section); its cases must pass in every runtime.
