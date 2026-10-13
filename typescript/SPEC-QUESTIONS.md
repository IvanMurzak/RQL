# Spec questions found while porting to TypeScript

Most questions raised during the port are now settled by spec/semantics.md §11–12 and golden vectors
(`meta-type-on-list`, `list-equality-unknown`, ASCII-only digits, integral `2.0` in plans); the TS
runtime follows those decisions. Remaining points, not covered by fixtures:

1. **`set` / `call` and `$depth`**: .NET's mutator does not set the current depth, so `$depth` in a set
   value sees the depth of the last item the recurse stage touched. TS uses the target item's depth.
2. **Getter failures inside inline-encoded objects**: .NET encodes the error value as an object summary
   (`{"$ref": "RqlError@n"}`); TS encodes `{"$error": "getter-threw"}` as §6 prescribes for errors.
3. **Number → text exponent form**: TS deliberately uses the .NET form (`1E+15`, `1E-05`) rather than JS
   (`1000000000000000`) so concatenation and map-key text match the reference; §12 allows either.
