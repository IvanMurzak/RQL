# ADR-0008: Bulk create from a range

**Status:** accepted (owner approval 2026-10-27; proposed and deferred as wave-4 finding #18). Implemented in the spec,
the three runtimes and the shared fixtures; see "Decision" for where the implementation refines the proposal.

## Problem

Agents cannot create N objects in one RQL call. Unity T19 ("300 cubes") and Godot W15 (particle emitters) fell back to
`script-execute`. `call` invokes a method once per stream item on that item, and the targets must be objects with
identity, so a creation method on a host source (`editor.CreateObject(…)`) runs once per query at most.

## Proposal (as deferred)

1. **A `range(start, end[, step])` source function** (core, spec §2): a list of numbers that streams like any list
   source. `range(0, 300)` gives 0 … 299, `$index` included. It is bounded by `maxVisited` and by the target limit when
   it drives a mutation.
2. **`call src.method(args)` with a source-qualified method**: the call targets the single object the named source
   yields (the `editor` source). It runs **once per stream item**, and `$`, `$index`, `$path` and `$depth` in the
   arguments are that item's:
   `range(0, 300) | call editor.CreateObject("Cube", {position: [$ * 2, 0, 0]})`. It also works with list literals and
   other streams (`[["a", 1], ["b", 2]] | call editor.Create($[0], $[1])`).
3. **Guarantees, the same as `call` today**:
   - Every invocation counts against the target limit (`max_targets`; the default of 200 allows 200 objects per call).
   - Every argument is resolved, coerced and validated, together with batch validation, before the first invocation, on
     dry runs too.
   - The host's before-write hook receives all N records at once, so an editor host records them as one undo step.
   - The receipt has one row per invocation (`$ref` of the result when it is an object).
   - "All-or-nothing" means what it means for `call`: validation is all-or-nothing, but a method that throws midway
     stops the stage (`call-threw`) with the earlier invocations completed. Hosts make creation safe through validators
     and the undo group, which can revert the whole step.

## Decision

The proposal is implemented as written, with these refinements (spec §2, §4.3, §6, §8, §10; `versioning.md`):

- **The qualifier is any source expression**, not only a source name: a registered source with navigation
  (`editor`, `world.Scenes[0]`, `g.Index.special`), `ref("Scene#3")` or a host function (.NET, C++). It is evaluated once,
  before the stream is read, and must be one object with identity; a list or map, null, a struct / scalar or an error
  value fails before anything runs, with hints (`src | call m(…)` for each element of a list; `… | get Member | call m(…)`
  when the name is a member of the items, not a source).
- **Plan shape**: the `call` stage gains the optional field `on` (an expression). Protocol stays `0.1`: every codec
  rejects unknown properties, so a runtime without this change refuses such a plan with `invalid-plan` ("unexpected
  property 'on' in stage 'call'") instead of calling the method on the items.
- **Text syntax**: `call` followed by a postfix expression that ends in a method call (`call editor.Create(…)`,
  `call ref("T#1").m(…)`); a qualified call without parentheses is a `syntax-error`. `call M(args)` and `call M` are
  unchanged.
- **Items may be any value** — numbers, strings, lists, projected rows (`… | select n = Name | call editor.Create(n)`),
  map entries, objects; null and destroyed items are skipped as for every `call`. Targets-have-identity applies to the
  receiver.
- **List literals are sources** too (they were `unknown-source`), and `range(…)` is also a core function in expressions
  (the list of its numbers, each counted against `maxVisited`). As a source, `range` is lazy, so `range(0, 1e9) | first 3`
  is cheap, and an unbounded range feeding a mutation stops at `mutation-limit` (counting up to the host ceiling).
  `range` arguments must be finite numbers and the step non-zero (`type-mismatch`); `end` is excluded and
  `start + k × step` is computed per item (no accumulated rounding).
- **The receiver's method is resolved before the stream is read**, so a misspelt method fails (`unknown-function`) even
  when no item arrives.
- **Receipt**: `$ref` is the returned object's when the method returned an object with identity, otherwise the
  receiver's (void and scalar results, dry runs). Messages about one invocation name the receiver and the item position
  (`for item 3`); `mutation-limit` counts items ("300 items reached 'call'").
- **Hints**: an unqualified `call` on items that are not objects (`range(0, 3) | call CreateObject(…)`) is still
  `invalid-plan`, now with a hint showing the qualified form and the sources whose object has the method; the
  unknown-callable hint shows both forms (`editor | call M(…)` once, `… | call editor.M(… $ …)` once per item).
- **Agent guidance**: the host-independent language rules (boolean conditions, `?:`, list equality, group items,
  receipts, `call` and bulk create) ship as text with every runtime (`RqlLanguageGuide.Rules`, `LANGUAGE_RULES`,
  `rql::language_rules()`; source `fixtures/guide/language-rules.txt`, checked by each runtime's tests), and the
  evaluation prompt `v5` and the Unity experiment's guide append it. The evaluation catalog has a `bulk` split
  (row, grid and list creation through a scene's `CreateObject`).

## Host adoption

Each engine exposes its creation methods on a source whose value is one object (Unity `editor.CreateGameObject` /
prefab instantiation, Unreal `SpawnActor`, Godot node creation / scene instantiation), allowlisted for `call`, ideally
with validators and a batch validator, and records the before-write hook's records as one undo step.

## Alternatives considered

- **`range(…) | call CreateObject(…)` where the items are numbers**: this would need calls on scalar items, which
  breaks the targets-have-identity rule.
- **A host extension stage (`unity.create N …`)**: possible without core changes, but every engine would write its own
  and the target limit, validation and undo rules would not be shared.
- **Batch arguments on the creation method (`CreateObjects(count, template)`)**: a host-side workaround, but it cannot
  express per-item variation such as positions or names from `$index`.
