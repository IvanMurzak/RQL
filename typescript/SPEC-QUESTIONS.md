# Spec questions found while porting to TypeScript

Divergences between `spec/semantics.md` and the .NET reference that no fixture covers. The TS runtime
follows the **spec** where they disagree (AGENTS.md: the semantic contract wins).

1. **`$type` / `$ref` on a list value** (§3: "`Components.$type` is the list of component type names").
   .NET applies the meta to the list itself (`$type` → `"List"`, `$ref` → missing). TS maps the meta over
   the elements when it is applied to a navigated value (`Children.$type`), as the spec says.
2. **`==` on lists / maps** (§4.1: "Lists/structs/maps: `unknown`"). .NET compares collections by reference
   (`Tags == Tags` is `true`). TS returns `unknown`.
3. **`set` / `call` and `$depth`**: .NET's mutator does not set the current depth, so `$depth` in a set
   value sees the depth of the last item the recurse stage touched. TS uses the target item's depth.
4. **Getter failures inside inline-encoded objects**: .NET encodes the error value as an object summary
   (`{"$ref": "RqlError@n"}`); TS encodes `{"$error": "getter-threw"}` as §6 prescribes for errors.
5. **Non-ASCII digits in query text**: .NET's lexer accepts any Unicode digit and then `double.Parse`
   throws a non-RQL exception; TS only treats ASCII digits as numbers (→ `syntax-error`).
6. **Plan codec integers**: .NET rejects `"n": 1.0` / `"maxDepth": 2.0` / `{"index": 1.0}` (integer
   lexeme required); JSON.parse loses the lexeme, so TS accepts integral values written with a fraction.
   Suggest stating in the schema/spec whether `1.0` is a valid integer.
7. **Nullability of strings in `set`**: §8 says "null only for nullable / reference types". .NET treats
   `string` as a reference type (always nullable); in TS `string` is a primitive, so `set Name = null`
   needs the member declared `t.nullable(t.string)` (the conformance `Note` member is).
