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
