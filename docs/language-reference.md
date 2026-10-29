# RQL language reference

A practical reference for writing RQL queries. The normative contract is [`spec/semantics.md`](../spec/semantics.md)
(grammar: [`spec/grammar.ebnf`](../spec/grammar.ebnf)); every section below links the spec section it summarises, and
the spec wins where the two differ. Host-specific sources, functions and methods (`objects`, `editor.CreateObject`,
`myapp.find`) are examples: each host lists its own with its schema tool.

This page describes the language on `main`. Features that are not in a published package yet are marked *unreleased*
in [`feature-matrix.md`](feature-matrix.md) and in the changelog of [`spec/versioning.md`](../spec/versioning.md). The
QueryPlan protocol is `0.1`.

## Contents

- [Query shape](#query-shape)
- [Sources](#sources)
- [Stages](#stages)
- [Paths and meta members](#paths-and-meta-members)
- [Values and truth](#values-and-truth)
- [Operators](#operators)
- [Functions](#functions)
- [Collection methods](#collection-methods)
- [Literals](#literals)
- [Mutations](#mutations)
- [Receipts](#receipts)
- [Budgets and the target limit](#budgets-and-the-target-limit)
- [Diagnostics](#diagnostics)
- [Lexical conveniences](#lexical-conveniences)

## Query shape

```text
source | stage | stage …
```

A query is a **source** followed by `|`-separated **stages**. Each stage consumes a stream of items and produces a
stream (or one terminal value). Items are live objects: only the rows that survive are encoded. Inside a stage, bare
names are members of the current item and `$` is the item itself. One query per call.

## Sources

Spec: [§2](../spec/semantics.md#2-sources).

| Source | Example | Items |
|---|---|---|
| A registered source, optionally navigated | `objects`, `world.Scenes[0].Objects` | a list streams its elements, a map its entries, anything else is one item |
| `ref("Type#id", …)` | `ref("Enemy#233")`, `ref("Node#1").Children[0]` | the referenced object(s); `stale-ref` when gone |
| A host function, optionally navigated | `unity.assets("t:Material")`, `unreal.asset("/Game/BP").EventGraph` | the function's value (.NET and C++; TypeScript pending) |
| `range(start, end[, step])` | `range(0, 300)`, `range(10, 0, -2)` | numbers, lazily (`end` excluded); each counts against `maxVisited` |
| A list literal | `[["Crate", 1], ["Barrel", 2]]` | its elements, evaluated with no current item |

An unknown name is `unknown-source`; its hint lists the registered sources and source functions.

## Stages

Spec: [§5](../spec/semantics.md#5-stages).

| Stage | Does |
|---|---|
| `where e` | keeps the items whose condition is `true` |
| `select [name=]e …` | projects each item to a row; a column is named by its alias or its source text. `$ref`, `$path`, `$type` work as columns. Computed values go in parentheses: `select hp=(Health / MaxHealth)` |
| `sort k [asc\|desc], …` | stable sort; `null` / `missing` last. A key that fails on items sorts them last and adds a warning naming the key and how many items failed |
| `first [n]` · `skip n` | slicing (`first` defaults to 1, stops pulling items upstream; `limit n` is an alias) |
| `count` | terminal: the number of items |
| `get e` | replaces each item by `e`, flattening lists; `null` / `missing` dropped |
| `recurse [via …] [max-depth=N] [include-self=false]` | cycle-safe depth-first walk over the `via` members, or over every owning member; emits the start item at depth 0 unless `include-self=false`; `max-depth` defaults to 8 |
| `distinct [e]` | keeps the first **item** per distinct value of `e` (default `$`); project first (`select Tag \| distinct`) to list values |
| `group-by e` | rows `key`, `count` in order of first appearance; every row also has `items`, the group's items, for per-group aggregates |
| `group-by [n=]e, [n=]e …` | several or named keys: one column per key, then `count` (`group-by Folder, kind=$type`) |
| `sum e` · `min e` · `max e` · `avg e` | terminal aggregates (null and missing skipped; other ignored values counted in a warning) |
| `describe` | terminal: the schema of the items' runtime types |
| `set p = e, …` | mutation (see [Mutations](#mutations)) |
| `call m(args)` | mutation: the method of each item |
| `call src.m(args)` | mutation: the method of the one object `src` yields, once per item |

Terminal stages must be last (`invalid-plan` otherwise). Hosts add namespaced stages (`myapp.expand …`).

```text
objects | where $ is Enemy | group-by Faction | sort count desc
objects | where $ is Light | group-by Shadows | select key, count, avg = items.avg(Intensity)
world.Scenes | get Objects | recurse Children max-depth=16 | where Name starts-with "Chest" | select $path
```

Keys of `group-by` / `distinct`: lists compare by value, structs by their members with numbers rounded as they print
(so `(1, 1, 1)` and `(0.99999994, 1, 1)` merge). A key member that most items lack gets an `unknown-member` warning with
the counts ("did you mean `$type`?" for `group-by type`).

## Paths and meta members

Spec: [§3](../spec/semantics.md#3-paths-and-member-access).

| Form | Meaning |
|---|---|
| `a.b.c` | member access: exact name first, else a unique case-insensitive match |
| `list[0]` · `list[-1]` | index from the start / end; out of range is `missing` |
| `map["key"]` · `map.key` | key lookup; an **absent key reads as `null`** (`has(map["key"])` tests presence) |
| `list.member` | maps over the elements, flattening one level (`Components.$type`) |
| `list.Count` · `.Length` · `.count()` | the size of a list or map |
| `list.method(args)` | a collection method; inside, `$` and bare names are the element, falling back to the enclosing item |

| Meta | Value |
|---|---|
| `$` | the current item (the element inside a collection method) |
| `$ref` | stable reference `Type#id` (or a session handle `Type@n`) |
| `$type` | short runtime type name (the host's display name when it sets one) |
| `$path` | how this query reached the item (`objects[5].Children[1]`), not a host location |
| `$index` | position in the stream entering the stage; in a `set` value or `call` argument, the target's position |
| `$depth` | depth below the `recurse` start item |
| `$count` | in a `set` value or `call` argument only: the number of items entering the stage (`$index` runs from 0 to `$count - 1`) |

## Values and truth

Spec: [§1](../spec/semantics.md#1-model).

Values are `missing`, `null`, `error`, bool, number, string, enum, object (has identity), struct (value type), list and
map; `missing`, `null` and `error` stay distinct. Single-precision members compute with the decimal they show
(`4.2f` is `4.2`).

**Conditions are booleans** ([§1.2](../spec/semantics.md#12-truth-three-valued-logic)): `where`, `and` / `or` / `not`,
`?:` / `if` and method predicates take a comparison or `has()`. A number, string, object or list there fails with
`type-mismatch` and a hint (`where sharedMaterial != null`, not `where sharedMaterial`); `null` counts as `false`. Logic
is three-valued (Kleene): a comparison with `missing` or an error is unknown, and `where` keeps only `true`, so
`where not (Health > 5)` does not return items without `Health`.

## Operators

Spec: [§4.1](../spec/semantics.md#41-comparison), [§4.2](../spec/semantics.md#42-arithmetic).

| Operator | Meaning |
|---|---|
| `==` `!=` | numbers numerically, strings case-sensitively, enums by name (see enum names below) or number, objects by identity, **lists by value** (in order), **structs of one type member by member** (recursively; type adapters may answer first, e.g. vectors within a tolerance) |
| `<` `<=` `>` `>=` | numbers, strings (ordinal), enums (underlying value) |
| `contains` · `starts-with` · `ends-with` | case-insensitive on strings; `contains` on a list is membership |
| `matches "regex"` | case-insensitive regex (portable subset: literals, `. * + ? ^ $ [] () \|`, `\d \w \s`) |
| `in [a, b]` · `not in` | `==` against each element (`x in [[1, 2], [3, 4]]` works) |
| `is null` · `is not null` | null test; an absent map key is null, a misspelt member is unknown |
| `is Type` · `is not Type` | the runtime type, a base type or an interface has that short (or display) name |
| `and` `or` `not` (`&&` `\|\|` `!`) | Kleene logic |
| `+ - * / %`, unary `-` | numbers; `+` concatenates when either side is a string; division by zero is `div-by-zero` |
| `cond ? a : b` | conditional, lowest precedence, right-associative, lazy (same as `if(cond, a, b)`) |

Comparisons do not chain (`a contains "x" == false` is a syntax error; write `not (a contains "x")`).

**Vectors.** Structs whose type adapter opts in to component-wise arithmetic (vectors, rotators, colors; never
quaternions or matrices) compute component by component: `v * n`, `n * v`, `v / n`, `v ± w`, `v * w`, `v / w`, `-v`,
and a list of as many numbers on either side (`Position + [0, 1, 0]`). A list whose elements are all numbers is a vector
too: `L * n`, `n * L`, `L / n`, `L ± M` (same length), `-L`. `v + n`, `L * M` (use `dot`), mixed struct types and
length mismatches are `type-mismatch` with a reason.

```text
… | set Scale = Scale * 1.5, Position = Position + [0, 2, 0]
… | set Position = [Position.x, 0, Position.z] * 2
```

## Functions

Spec: [§4.3](../spec/semantics.md#43-functions-portable-core). Core functions work in every runtime; hosts add
namespaced ones (`unity.find(…)`).

| Function | Result |
|---|---|
| `has(path)` | the path exists (every map lookup in it finds its key) |
| `is-null(x)` | `x is null` |
| `len(x)` | string length, list / map size |
| `lower(s)` · `upper(s)` | case conversion |
| `abs` · `round(n[, digits])` · `floor` · `ceil` · `sqrt` · `pow(b, e)` | numeric |
| `min(a, b, …)` · `max(a, b, …)` | smallest / largest of two or more numbers |
| `sin` · `cos` · `tan` · `asin` · `acos` · `atan` · `atan2(y, x)` | trigonometry in **radians**; results are rounded (12 decimals for `sin` / `cos` / `tan`, 14 for the inverses) so every runtime returns the same digits: `sin(pi())` is `0` |
| `pi()` · `deg(x)` · `rad(x)` | π; radians to degrees (rounded: `deg(atan2(1, 1))` is `45`) and degrees to radians |
| `coalesce(a, b, …)` | the first argument that has a value |
| `if(cond, then, else)` | lazy conditional (`cond ? then : else`); only the taken branch runs, so `if(Count > 0, Total / Count, 0)` is safe |
| `ref("Type#id", …)` | resolve references (root scope) |
| `append(list, v, …)` · `insert(list, i, v)` · `remove-at(list, i)` · `without(list, v)` | new lists (assign the result) |
| `range(start, end[, step])` | the list of numbers `range` streams as a source |
| `replace(s, from, to)` | case-insensitive replace of every occurrence |
| `replace-regex(s, pattern, repl)` | regex replace; `$0`–`$9` insert groups, `$$` a dollar |
| `substring(s, start[, length])` | positions clamp to the string; negative `start` counts from the end |
| `split(s, sep)` · `trim(s)` · `pad(v, width[, fill])` | split (empty parts kept), trim whitespace, left-pad (`pad(3, 2)` → `"03"`) |
| `length(v)` · `magnitude(v)` | Euclidean length of a vector (`len` stays the size of a string or list) |
| `normalize(v)` · `distance(a, b)` · `dot(a, b)` · `lerp(a, b, t)` | vector functions on numeric lists and component structs; `distance` and `dot` return numbers, `normalize` and `lerp` a value of the first vector's kind |
| `contains(a, b)` · `starts-with` · `ends-with` · `matches` | function forms of the operators |

String and vector functions are also methods: `Name.replace("Pillar_", "Column_")`, `Label.trim()`,
`Position.length()`, `a.distance(b)`. Numeric, string and vector functions return a `null` / `missing` / error
argument unchanged and a `type-mismatch` error value for other wrong kinds.

```text
objects | where name starts-with "Pillar_" | set name = name.replace("Pillar_", "Column_")
objects | where $ is Enemy and distance([Position.x, Position.z], [0, 0]) < 50 | select Name
```

## Collection methods

| Method | Result |
|---|---|
| `.any([pred])` · `.all(pred)` | some / every element matches (`all` of nothing is `true`) |
| `.count([pred])` | number of (matching) elements |
| `.sum(e)` · `.avg(e)` · `.min(e)` · `.max(e)` | aggregates over the elements (`min` / `max` also order strings) |
| `.first([pred])` | the first (matching) element, or `missing` |
| `.keys()` · `.values()` | of a map |

`group-by` rows expose the group's items, so `items.avg(Weight)`, `items.count(Enabled)` and `items.first().Name` are
per-group aggregates.

## Literals

| Kind | Syntax |
|---|---|
| number, string, bool, null | `42` `1.5` `2e-3`, `"text"` `'text'`, `true` `false`, `null` (ASCII digits only) |
| list | `[1, 2, 3]`, `[]` |
| object | `{x: 1, y: 2}`, `{$type: "BoxMesh", Size: [1, 2, 1]}`, `{}` |

An object literal behaves as a map when read; on a write it builds a value
([§4.5](../spec/semantics.md#45-object-literals)): a map entry by entry, or an object / struct whose named members are
assigned (members left out keep the type's defaults; `$type` picks a concrete subtype). Each type has a **literal
kind**:

- **value**: built from the literal (plain data, and value-like assets the host marks, such as Godot resources);
- **instanced**: built as a subobject of the object it is written into (Unreal `EditInlineNew` objects:
  `set Triggers = [{$type: "InputTriggerHold", HoldTimeThreshold: 0.5}]`);
- **engine**: never built (scene objects: nodes, game objects, actors, components). Pass an existing one
  (`ref("Type#id")`, the host's finder or an asset path) or create it with a host method.

Type adapters may accept text forms on writes and comparisons: numeric tuples such as `"(0, 1, 0)"`
([§13](../spec/semantics.md#13-type-adapters-host-extension-normative-behaviour)), and host-specific forms such as hex
colors.

## Mutations

Spec: [§8](../spec/semantics.md#8-mutations).

```text
ref("Enemy#233") | set Health = MaxHealth, Transform.Position.y = Transform.Position.y + 2
objects | where $ is Enemy and Health < 5 | first 2 | call Heal(25)
objects | where name starts-with "Crate" | sort name | set Position.x = $index * 2
selection | set Position = [5 * cos(2 * pi() * $index / $count), 0, 5 * sin(2 * pi() * $index / $count)]
range(0, 300) | call editor.CreateObject("Cube", {position: [($ % 15) * 2, 0, floor($ / 15) * 2]})
[["Crate", 1], ["Barrel", 2]] | call editor.CreateObject($[0], $[1])
range(0, 10) | call editor.CreateObject({name: "Tile" + $, position: [$ * 2, 0, 0]})
app.Tables | get Rows | where Value.IsElite | set Value.Health = Value.Health * 1.1
```

- **Two switches.** Writes need the host's mutation switch **and** a mutation request; a mutation request must end in
  `set` or `call` (`invalid-plan` otherwise), and a read request with `set` / `call` is `permission-denied`.
- **All-or-nothing `set`.** Every target and value is resolved, coerced and validated before the first write; if a
  write fails midway, the written values are restored. Writes through structs (`set Position.y = 1`) are written back to
  the owner.
- **Validated `call`.** Every invocation is resolved, coerced and validated (per-method validators and the host's
  batch validator) before the first call, on dry runs too. A method that throws stops the stage with `call-threw`;
  earlier calls are not undone (hosts with undo record the whole stage as one step).
- **Dry runs** return the receipt without changing anything.
- **Target scope.** A value or argument is evaluated per target with `$` the target; `$index` is its position in the
  stream entering the stage and `$count` the size of that stream, so `$index / $count` spreads targets evenly (the
  circle above).
- **Targets** are objects with identity; a scalar or a struct copy (`get Transform.Position | set x = 1`) is
  `invalid-plan`. Map entries streamed by `get` write through their map.
- **Source-qualified `call src.m(args)`** (bulk create, [ADR-0008](decisions/0008-bulk-create-from-a-range.md)) runs the
  method of the one object `src` yields once per stream item; `$`, `$index`, `$path`, `$depth` in the arguments are the
  item's, and every invocation counts against the target limit. `src` may navigate (`world.Scenes[0].Spawn(…)`,
  `ref("Node#1").Children[0].Add(…)`, [ADR-0012](decisions/0012-list-vectors-vector-functions-qualified-paths.md)).
- **Overloads and named arguments** ([ADR-0010](decisions/0010-overloads-named-arguments-receipts.md)): overloads are
  checked without side effects and only the chosen one's arguments are built. One object literal that no overload takes
  as a whole binds its keys to parameter names (`{name: …, position: …}`); the rest keep their defaults, and a misspelt
  key is reported as the closest parameter (`klass` → `className`).
- **Coercion.** Integers need an integral value in range; a string meets an enum by **enum name resolution**: exact
  name or host alias, then the part after `Type::` / `Type.`, then case-insensitive, then without the entry prefix
  (`"Fill"` → `HAlign_Fill`); several candidates are refused as ambiguous. Lists from list literals, maps and objects
  from object literals, references from `ref(…)`.
- **A typo that empties a mutation is an error.** When no item reaches `set` / `call` because a member read missed on
  every item and no known type declares that name, the query fails with `unknown-member` instead of reporting zero
  changes. A real member that the matched items lack stays a warning.

## Receipts

Spec: [§8](../spec/semantics.md#8-mutations) (receipt), [`agent-tool-contract.md`](agent-tool-contract.md) (compact
form).

- `set`: one row per changed assignment, `$ref|member|old|new`; `new` is read back after the write (what the host
  stored). An assignment that leaves the value unchanged is still written but left out and counted in `unchanged`.
- `call`: one row per invocation, `$ref|method|result`. For a source-qualified call that returns an object, `$ref` is
  the created object's.
- The .NET agent table format compacts long receipts: a method shared by every row is printed once, a `$ref` equal to the
  result's is dropped, and only the first 50 rows are listed with a count of the rest.
- A receipt cut by the output byte budget says that every change was made (on a dry run: checked); it never suggests
  paging, which would repeat the change.

```text
DRY RUN - nothing changed; 2 results
$ref|member|old|new
Enemy#233|Health|1|90
Enemy#233|Transform.Position.y|25.52|27.52
```

## Budgets and the target limit

Spec: [§10](../spec/semantics.md#10-budgets-and-errors), [§8 target limit](../spec/semantics.md#8-mutations).

| Budget | Default | On exceed |
|---|---|---|
| `maxVisited` | 1 000 000 | `budget-exceeded` |
| `maxRows` | 50 | truncation (`N of M`) |
| `maxListItems` | 20 per encoded list | `…+N` marker |
| `maxDepth` (cap on `recurse max-depth`) | 16 | clamp + warning |
| `timeoutMs` | 2 000 | `timeout` |
| `maxOutputBytes` | 65 536 | rows cut + `output-truncated` |
| `maxMutationTargets` | the host's default (20 unless the host sets it) | `mutation-limit`, nothing changed |

The **target limit** guards the breadth of a change (a filter wider than intended), not its correctness:
all-or-nothing, validation, rollback, undo and dry runs hold at any size. Editor hosts with undo typically default to
200. A request raises it explicitly, from 1 up to the host's ceiling (default 1000), through the host's limit argument
(for example a `max_targets` tool argument); a value outside that range is refused with `invalid-value`, never clamped.
`mutation-limit` says how many items matched and advises raising the limit to that count, or splitting the change into
batches when it is above the ceiling.

## Diagnostics

Spec: [§10](../spec/semantics.md#10-budgets-and-errors).

Diagnostics are `error|warning <code>: <message> - hint: <hint>`. The codes are a closed set per release (clients
treat an unknown code as a generic error): `syntax-error`, `invalid-plan`, `version-unsupported`, `unknown-source`,
`unknown-stage`, `unknown-function`, `unknown-member`, `unknown-meta`, `type-mismatch`, `arity`, `div-by-zero`,
`getter-threw`, `call-threw`, `function-threw`, `hook-threw`, `invalid-regex`, `stale-ref`, `permission-denied`,
`readonly-member`, `coercion-failed`, `invalid-value`, `mutation-limit`, `budget-exceeded`, `timeout`,
`output-truncated`, `unsupported`, `internal-error`, `no-match`.

Hints are the agent's self-correction channel: "did you mean …?" for members (struct members two levels down
included), sources, stages, functions, types and parameter names; the types that have a member in a mixed stream
(`where $ is OmniLight3D`); the sources whose object has a called method (`editor | call CreateObject(…)` or
`… | call editor.CreateObject(… $ …)`). Hints only name tools and sources the host has (see the host's
[schema and mutation tool names](host-integration.md#hints-and-host-texts)).

## Lexical conveniences

Spec: [§11](../spec/semantics.md#11-lexical-conveniences-normative). The parser accepts and canonicalizes away:
keywords and stage names in any case, `=` for `==`, `&&` `||` `!`, `limit N` for `first N`, `expr as name` in
`select`, `cond ? a : b` for `if(…)`, `x is not null`, `x not in L`, `x is not T`. Expression nesting is capped at 100
levels and 1000 operators; there are no comments.
