# ADR-0013: Hints and host-supplied texts (named-argument typos, rule examples, receipt notes, module registrations)

**Status:** accepted (2026-10-29). Implemented in the spec (§8, §13), the three runtimes (rules text, overload ranking,
receipt hints) and the .NET tool layer (`AgentFormat`, module reports). Source: wave-6 cross-engine findings 2, 3, 4, 11
and 27 (finding 5, the engine-object refusal advice, is ADR-0011).

## Problems

1. **#27** `call godot.CreateNode({klass: "MeshInstance3D"})` reported the options overload's "no member 'klass'"
   instead of the misspelt parameter name: the by-name miss had no bound key and depth 0, so it ranked below a missing
   member inside a literal (depth 1).
2. **#3** The shared language rules show `editor.CreateObject`, which is not a method in Unity (`CreateGameObject`) or
   Godot (`CreateNode`); agents copied it.
3. **#4** The compact receipt footer "(the json format lists every row)" points at a format some hosts' tools do not
   offer.
4. **#11** Hosts could not tell which module registered a source, function, guide or adapter (Unity mapped delegates to
   assemblies).
5. **#2** A receipt cut by the output byte budget suggested "page with skip/first" — on a mutation that would repeat
   the change.

## Decisions

1. **A key close to a parameter name is that parameter, misspelt.** In by-name binding an unknown key with a suggestion
   (the §10 rules, or close to one word of a camelCase / snake_case parameter name) counts as a bound key, so that
   overload outranks one that cannot take the literal. A key with no close parameter keeps ranking as before.
2. **Host example in the rules.** `RulesFor(createCall)` (TS `languageRules`, C++ `language_rules_for`) substitutes the
   host's creation call for the placeholder and drops the "stands for" note; the constant stays the shared default.
   Hosts whose creation call is not `(name, {properties})` (Unreal `editor.SpawnActor(ClassName, Label, Location, …)`)
   give the examples themselves: `RulesWith(RqlRuleExamples)` (TS `languageRules({createCall, grid, perEntry, named})`,
   C++ `language_rules_with(RuleExamples)`); an example left out keeps the default with the host's call. A grid
   example with a `|` replaces the whole `range(0, 300) | call …`, so a host can keep it within its target limit. The
   rules also gain the ADR-0011 / ADR-0012 lines: `$type` for grouping by type, list vectors and vector functions,
   typed literals for value-like assets and owned subobjects.
3. **Receipt note override.** `AgentFormat.Render(result, format, maxReceiptRows, moreRowsNote)`; `""` omits the note.
4. **Module reports record registrations.** `RqlModuleReport.Sources`, `Functions`, `Stages`, `Guides`, `Adapters`,
   recorded by the engine while each module's `Configure` runs (direct `Engine` calls included; a name registered again
   by a later module is listed by that module too).
5. **Receipts are never paged.** The byte-budget hint for a receipt says every change was made (dry run: checked); the
   table header says "N of M receipt rows" (no paging advice).

## Consequences

- Message changes only (plus the new optional APIs); protocol `0.1` is unchanged.
- Hosts adopt by calling `RulesFor` with their creation call and, where their tools have no json format, passing
  `moreRowsNote`.
