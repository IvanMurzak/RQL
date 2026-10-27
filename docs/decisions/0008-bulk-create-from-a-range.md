# ADR-0008: Bulk create from a range (design note, deferred)

**Status:** proposed, deferred (wave-4 finding #18, 2026-10-27)

## Problem

Agents cannot create N objects in one RQL call. Unity T19 ("300 cubes") and Godot W15 (particle emitters) fell back to
`script-execute`. `call` invokes a method once per stream item on that item, and the targets must be objects with
identity, so a creation method on a host source (`editor.CreateObject(…)`) runs once per query at most.

## Proposal

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

## Why deferred

The change touches the grammar and plan shape: `call` gains an optional source qualifier, a new plan field and a new
codec version check for older runtimes. It also needs `range` in all three runtimes, new fixtures, and host adoption
(each engine's creation methods must be `call`-able on a source object). The wave-4 scope was fixes and hints plus two
small features, so this is kept for a separate, owner-approved change.

## Alternatives considered

- **`range(…) | call CreateObject(…)` where the items are numbers**: this would need calls on scalar items, which
  breaks the targets-have-identity rule.
- **A host extension stage (`unity.create N …`)**: possible today without core changes, but every engine would write its
  own and the target limit, validation and undo rules would not be shared.
- **Batch arguments on the creation method (`CreateObjects(count, template)`)**: a host-side workaround, but it cannot
  express per-item variation such as positions or names from `$index`.
