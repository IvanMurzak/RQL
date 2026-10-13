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
* `$type`/`$ref` on lists map over elements; `==` on lists/maps is unknown; integral doubles accepted for
  integer plan/budget fields; ASCII-only digits in text; portability decisions (§12).
