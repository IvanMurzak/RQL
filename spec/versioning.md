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
  first and is independent of whether callers can pass the other budgets; capability manifest `mutations.hardMaxTargets`.
