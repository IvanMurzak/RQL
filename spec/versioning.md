# Versioning

* The protocol version is the `rql` field of QueryPlan / QueryResult / CapabilityManifest (`"0.1"`).
  Runtimes reject unknown versions with `version-unsupported`.
* **Patch-level changes** (no version bump while `0.x`): adding diagnostic codes, functions, methods,
  stages, meta members or lexical conveniences; clarifying semantics that no golden vector depended on.
* **Breaking changes** (bump to `0.2`, …): changing the meaning or JSON shape of an existing plan node,
  operator, stage or result field; changing the canonical form of existing syntax.
* Every semantic change follows: spec text → .NET reference → `regen` → reviewed fixture diff →
  TypeScript and C++ pass → changelog entry below.
* Text syntax is not versioned separately: the parser always emits the current plan version.

## Changelog (0.1, pre-release)

* Initial grammar, semantics, schemas, golden vectors.
* `group-by` is a streaming stage producing `{key, count}` rows (was terminal) — evidence: tasks needing
  "largest group".
* Identity members are read-only.
* `$depth` meta; `$name` columns shadow metas on rows; map entries delegate members to their value;
  single-column rows stand for their value; scope chain in method arguments; verbatim unknown string
  escapes; `no-match` warning — all from Tier B failure analysis (see `RESULTS.md`).
* `recurse` includes its input items by default (`include-self=false` to drop them; canonical plans only
  carry `includeSelf: false`) — evidence: E6/E9 agents repeatedly lost the start items (`X.Dependencies | recurse Dependencies`).
* `$type`/`$ref` on lists map over elements; `==` on lists/maps is unknown; integral doubles accepted for
  integer plan/budget fields; ASCII-only digits in text; portability decisions (§12).
* Type adapters (§13): host extensions for members (virtual / hidden / only / non-public fields / read-only /
  reference / explicit-only), encoding, coercion, comparison, identity and liveness, with deterministic resolution
  order; golden vectors in `fixtures/adapters/cases.json`. No grammar or plan change. Capability manifest type
  descriptors gain `description` and member `explicitOnly` / `description`.
* Object literals `{ key: expr }` (§4.5) with construction on writes (`"$type"` polymorphism); list functions
  `append` / `insert` / `remove-at` / `without`; `ref()` resolves exact ids before trimming display forms.
  Adapters (§13): methods, virtual (and mixed-type) maps, copy-on-read and writes-through write-back, predicate hiding
  and type exclusion, destroyed objects read as null, identity/display choice, literal schemas, host write hooks with
  owners — closing the ReflectorNet / Unity-MCP converter parity gaps (docs/type-adapters.md).
* Live-battery fixes (Unreal / Unity / Godot; package 0.2.0, protocol stays `0.1`; additive or clarifying): functions
  `sqrt`, `pow`, `min(a, b, …)`, `max(a, b, …)` (§4.3); suggestions use optimal-string-alignment distance and a tighter
  containment rule, member listings are capped (§10); a dotted path naming a projected column resolves to it (§3); a
  mutation request without `set` / `call` is `invalid-plan` (§8); receipts report the read-back `new` value (§8);
  codes `call-threw`, `function-threw`, `hook-threw` (all formerly `getter-threw`), `invalid-value` (validator
  rejections, formerly `coercion-failed`) and `invalid-regex` (§4.1, §10, §13) — **behaviour change for clients that
  match on codes**: those cases no longer report `getter-threw` / `coercion-failed`; `call` validators checked for every target before the first call (§8);
  aggregates warn about ignored values (§4.3); `distinct` compares projected rows by value (§5); display numbers and
  non-finite encoding (§6); numeric tuple display, input and object-literal equality (§13).
* Wave-2 fixes (Unreal / Unity / Godot; unreleased, protocol stays `0.1`; additive or clarifying, no new error code):
  `$index` / `$path` / `$depth` in `set` values and `call` arguments are the target's, `$index` its position in the
  stream entering the stage (§3, §8) — **behaviour change**: these read a stale value (`$index`) or `missing`
  (`$path`, and `$depth` in .NET / C++) before; string functions and methods `replace`, `replace-regex`, `substring`,
  `split`, `trim`, `pad` (§4.3); `group-by` with several or named keys and its plan form `{fields}` (§5); batch
  validation of a whole `set` / `call` stage, dry runs included (§8); budget hints only name `budgets.*` when the
  host's callers can pass budgets, the `mutation-limit` hint suggests batches and is replaceable by the host (§10);
  `set` through map entries streamed from a writable map (§8; formerly always `invalid-plan`); host text coercion for
  struct / object types (§8);
  hints for an out-of-range list index in a `set` target, a computed `sort` key, a parenthesised tuple and a `ref()`
  given a name (§7); unquoted `$`-name object keys (`{$type: "T"}`, §4.5);
  `$path` is the query path, `distinct e` keeps items (§3.1, §5).
* Wave-3 fixes (Unreal / Unity / Godot; unreleased, protocol stays `0.1`; additive or clarifying, no new error code):
  **enum name resolution** (§8) wherever a string meets an enum (writes, map keys, comparisons): exact name or host
  alias first, then the part after a `Type::` / `Type.` qualifier, then ignoring case, then without the entry prefix
  (`"Fill"` → `HAlign_Fill`); several candidates are ambiguous (`coercion-failed` listing them, not equal in
  comparisons) — **behaviour change**: qualified and prefix-less names were `coercion-failed` (and never equal), a
  case-only collision picked one entry, integer text now equals an enum in TypeScript, and only flags enums take
  comma-separated lists in .NET / TypeScript; host alias names for enum entries (§8). **Target limit** (§8, §10): its
  safety intent and host default are specified (editor hosts with undo: e.g. 200), `mutation-limit` states how many
  items matched (counting up to the host ceiling), its hint advises raising the host's **limit argument** to the count
  first and is independent of whether callers can pass the other budgets (a budget error while counting is reported
  instead); capability manifest `mutations.hardMaxTargets`.
* Wave-3b fixes (Unity / Unreal / Godot; unreleased, protocol stays `0.1`; additive or clarifying, no new error code,
  no new plan node shape — but a plan using `if` needs a runtime with this change): the core function **`if(cond, then, else)`** with the text form `cond ? then : else` (lowest
  precedence, right-associative, lazy; §4.3, §11, grammar `conditional`); a `set` / `call` that no item reaches because
  of a member read that missed on every tested item and that **no known type declares** fails with an `unknown-member`
  error instead of "0 changes" (§8) — **behaviour change** for such queries, which returned ok — with a host
  known-member hook that polymorphic hosts and hosts with function / `ref()` sources SHOULD supply; hints name only
  what the host has: the `unknown-source` example is a registered source, the projected-rows hint names no sample
  path, the schema tool is the host's (`SchemaHint`, default "the schema tool"; was `rql_schema / describe`), and an
  unknown `call` method names a source that has it (§2, §10).
* Wave-4 fixes (Unity / Unreal / Godot; unreleased, protocol stays `0.1`; no new error code, no new plan node shape):
  **no implicit truthiness** (§1.2): a number, string, enum, object, struct, list or map in a predicate position
  (`where`, `and` / `or` / `not`, `if` / `?:`, method predicates) fails the query with `type-mismatch` and a hint showing a
  boolean test — **behaviour change**: such conditions were silently `unknown` (`where sharedMaterial` kept nothing);
  `null` stays `false`. **Lists compare by value** (§4.1, §5): `==` / `!=` / `in` / `contains`, `distinct` and `group-by`
  keys — **behaviour change**: list equality was `unknown` and `group-by` / `distinct` never merged equal lists. **Float
  widening** (§1.1): a float is the double of its shortest decimal as a number (`4.2f * 1` is `4.2`) — results of
  arithmetic and aggregates over float members change in their last digits. **Target limit range** (§8): a request
  limit outside 1 … the host ceiling is refused with `invalid-value` — **behaviour change**: it was clamped (or `0`
  accepted). Hints: the host's mutation tool in `permission-denied` (no `options.allowMutation`), mixed-stream
  `unknown-member` hints and member lists ranked by similarity (§10), type display names (§13), and the parser hints
  "comparisons do not chain" and "one query per call" (§11).
* Wave-4 features (unreleased; protocol stays `0.1`, no new error code): **group items** — group-by rows have the member
  `items` (the group's items), so `group-by Kind | select key, avg = items.avg(Weight)` aggregates per group (§5);
  **no-op assignments** — a `set` receipt omits assignments whose value stayed the same and counts them in the new
  optional result field `unchanged` (§6, §8; **behaviour change**: such rows were listed); engines list their adapters
  (§13).
* Bulk create, ADR-0008 (unreleased; protocol stays `0.1`, no new error code): the core source function
  **`range(start, end[, step])`** (lazy, bounded by `maxVisited`; also a list function in expressions) and **list literals
  as sources** (§2; **behaviour change**: both were `unknown-source`); the **source-qualified call** `call q.m(args)`,
  which runs the one object's method once per stream item with the item's `$` / `$index` / `$path` / `$depth` in the
  arguments, under every `call` guarantee, the target limit counting invocations (§8). Plan shape: the `call` stage gains
  the optional field `on` (the source expression); every codec is strict about unknown properties, so a runtime without
  this change refuses such a plan with `invalid-plan` ("unexpected property 'on'") instead of calling the method on the
  items. Receipts of a source-qualified call carry the returned object's `$ref` (§8). An unqualified `call` on items that
  are not objects hints the qualified form, and the unknown-callable hint shows both forms (§10). Encoding: a list or map
  nested in itself encodes the repetition as `"…"` (§6; .NET recursed without end). Known names include the members of
  type adapters' target types (§8, §13). The host-independent language rules ship as text with every runtime
  (`fixtures/guide/language-rules.txt`). The group-row hint names the rows' own key columns (§5; it showed `select key` and `items.avg(key)` after a named key).
* Wave-5, ADR-0009 (unreleased; protocol stays `0.1`, no new error code, no plan change): **component-wise struct
  arithmetic** for types whose adapter declares components (the new adapter capability *Arithmetic*, §4.2, §13:
  `Scale * 1.5`, `Position + [0, 1, 0]`, `-v`); other struct operands stay `type-mismatch`, now with a reason. **Behaviour
  change**: an absent map key reads as `null`, not `missing` (§3), so `== null` / `is null` are `true` and `!= v` keeps
  the item; `has(m["k"])` still tests the key, and unknown members stay `missing`. `distinct` / `group-by` key structs by
  their members with numbers rounded as numeric tuples print (§5), so `(1, 1, 1)` and `(0.99999994, 1, 1)` merge.
  `unknown-member` hints also offer struct members one and two levels down (`BodyInstance.CollisionProfileName`, §10).
  Two more shared language rules (vectors, missing map entries). **Behaviour change**: `==` compares two structs of one type member by member, recursively
  (§4.1; it was `unknown` without an equality adapter), so a `set` that writes a struct's current value, nested structs
  included, is counted as unchanged.
