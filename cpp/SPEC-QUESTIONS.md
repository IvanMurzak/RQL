# C++ runtime — spec questions

Points where the spec is silent or a runtime has to pick a behaviour. Items 1–7 are now resolved by
spec/semantics.md §12 (Portability decisions); the C++ runtime conforms to those decisions. Items 8–10
remain open. Items 11–14 are resolved by §13 ("Scope" and the rollback paragraph).

1. **Null for non-nullable reference-like members.** §8 says "null only for nullable / reference
   types". .NET treats `string` and `List<T>` as reference types (null accepted). In C++ `std::string`
   and `std::vector` cannot hold null, so `set Name = null` → `coercion-failed`. Should the spec say
   "null only for members the runtime descriptor marks nullable"?
2. **Case-insensitive comparisons.** §4.1 says "ordinal case-insensitive" without naming a case
   mapping. C++ folds ASCII only; .NET folds full Unicode (invariant). Should the portable contract be
   ASCII (or simple Unicode) folding?
3. **Ordinal string order.** UTF-16 code-unit order (.NET, JS) vs UTF-8 byte / code-point order (C++)
   differ for supplementary characters vs U+E000–U+FFFF. Fix the encoding in §4.1 / §5 sort?
4. **Number → text** (string `+`, map keys in output, ids in `$ref`, `$path` keys) is not specified.
   C++ mirrors .NET `"R"` (`1E+15`, `1E-05`); JS would print `1000000000000000` / `0.00001`.
5. **`$type` of scalars and collections** is runtime-specific; C++ mirrors .NET names (`String`,
   `Double`, `Int32`, `Single`, `Boolean`, `List`, `Dictionary`, `MapEntry`, `RowValue`). Worth
   pinning portable names in §3?
6. **Session handles without a liveness hook.** C++ cannot hold weak references to arbitrary
   objects, so `Type@n` is re-validated by searching sources (owning members only); an object that is
   alive but only reachable through non-owning references resolves to `stale-ref`. .NET (weak refs)
   would still resolve it. Acceptable, or should §7 require a host liveness hook for handles?
7. **Budgets codec vs plan codec.** The plan codec now accepts integral doubles (`1.0`) for integers,
   but `Budgets.FromJson` (.NET) still uses `TryGetInt32` (rejects `1.0`). C++ mirrors both as-is.
   Align?
8. **List assignment semantics.** `set Tags = OtherNode.Tags` aliases the same list in .NET (reference
   assignment); C++ copies the elements. Spec §8 only covers list *literals*.
9. **Extension stage laziness.** §5 requires `first` to stop pulling upstream; the C++ extension stage
   contract receives a materialized input vector. Should laziness be required of extension stages?
10. **`maxOutputBytes` unit** (UTF-8 bytes vs UTF-16 chars of escaped JSON) is unspecified.

## Type adapters (§13)

11. **Explicit-only owning collections hide their elements from `ref()` search.** With the test set,
    `Children` is explicit-only, so `ref("Node#4")` is `stale-ref` (now pinned by
    `adapt-explicit-only-not-searched-by-ref`). Hosts that mark containers explicit-only must
    provide `resolve_stable_ref`; worth stating in §13 / §7.
12. **Virtual setter round-trip.** §13 requires `set(get())` to restore state; the fixture's `title`
    adapter (get = upper, set = lower) does not round-trip mixed-case names (`B-2` → `b-2`). No case
    rolls back such a name, so all runtimes pass, but the example contradicts the MUST.
13. **`distinct` / `group-by` ignore adapter equality** (as in .NET: they use value/identity
    equality). Should `distinct Pos` use the adapter's `equals`? The spec is silent.
14. **Adapter scope.** C++ registers adapters per `Engine` (like .NET's per-engine model); the spec
    does not say whether adapters belong to an engine or to the type registry.

