# Host integration guide

How to embed RQL in a host (an editor plugin, a game server, an app) and expose it to agents. This page is the map;
the per-runtime details are in [`typescript/README.md`](../typescript/README.md), [`cpp/README.md`](../cpp/README.md)
and, for .NET, the XML docs of `RqlEngine` / `MutationPolicy`. Type adapters have their own page,
[`type-adapters.md`](type-adapters.md); the normative rules are [spec §8](../spec/semantics.md#8-mutations),
[§10](../spec/semantics.md#10-budgets-and-errors) and
[§13](../spec/semantics.md#13-type-adapters-host-extension-normative-behaviour).

Several hooks below are not in a published package yet; [`feature-matrix.md`](feature-matrix.md) marks them
*unreleased* and lists each one's status per runtime.

## Contents

- [Engine and sources](#engine-and-sources)
- [Extensions and modules](#extensions-and-modules)
- [Type adapters](#type-adapters)
- [Mutations](#mutations)
- [Literal construction](#literal-construction)
- [Hints and host texts](#hints-and-host-texts)
- [Agent tools and output](#agent-tools-and-output)
- [API names per runtime](#api-names-per-runtime)

## Engine and sources

An engine holds the reflection model, the registered sources and the policies. Sources are getters: they run when a
query names them, never ahead of time.

```csharp
var engine = new RqlEngine()
    .AddSource("objects", () => scene.AllObjects, "every object in the scene")   // AddSource<T>: declares T
    .AddSource("selection", () => editor.Selection, "the selected objects", typeof(List<GameObject>));
engine.IsAlive = o => !(o is UnityEngine.Object u) || u != null;               // destroyed objects read as null
engine.ResolveStableRef = (type, id) => FindById(type, id);                      // fast ref("Type#id")
```

**Declared source types.** Known member names (used by the typo rule, the "never evaluated" warning and hints) never
evaluate a source the query does not name, so a source contributes only its declared type until a query reads it.
.NET `AddSource<T>(name, Func<T>)` declares `T` (nothing is declared when the item type is `object`, an interface or an
untyped list; pass a `Func<object?>` to keep a source undeclared); `AddSource(name, getter, description, declaredType)`
declares one explicitly, also from a module (`RqlModuleContext.AddSource`). TypeScript `addSource(name, getter,
description, type)`; C++ `add_source(name, getter, description, declared_type)` or `add_object_source(name, &obj)`.

**Known members beyond the sources.** A host whose items come from function sources or `ref()`, or whose streams are
polymorphic (every component or node class), supplies a known-member hook (name → bool): .NET `IsKnownMember`, TS
`isKnownMember`, C++ `is_known_member`. Without it, a `set` whose items all lack a real member can fail as a typo.

## Extensions and modules

- **Functions and stages** are namespaced (`myapp.find(…)`, `myapp.expand …`): `AddFunction` / `AddStage` (.NET),
  `addFunction` / `addStage` (TS), `add_function` / `add_stage` (C++). A registered function may also be a source
  (`myapp.find("Player") | …`) and a call receiver (`call myapp.asset("/x").Graph.AddNode(…)`); both are pending in
  TypeScript.
- **Modules** (.NET) bundle adapters, sources, functions, exclusions and agent guides:
  `IRqlModule { int Order { get; } void Configure(RqlModuleContext context); }`, discovered with `engine.AddModulesFrom(assemblies)`
  or `engine.AddModules(types)` (hosts with a type index, such as Unity's `TypeCache`). Failures are isolated and
  reported; every `RqlModuleReport` lists what the module registered (`Sources`, `Functions`, `Stages`, `Guides`,
  `Adapters`), so a host can tell which package owns a source.

```csharp
public sealed class MyGameRqlModule : IRqlModule
{
    public int Order => 200;
    public void Configure(RqlModuleContext ctx) =>
        ctx.AddAdapter(new FloatRangeAdapter())
           .AddSource("spawners", () => Spawner.All)                       // declares the getter's type
           .AddFunction("mygame.wave", args => Waves.Get((int)(double)args[0]!), "a wave by number")
           .AddGuide("My game", "spawners | set rate = 2 changes every spawner's rate.");
}
```

## Type adapters

Adapters shape how a host type is read, shown, written, compared and called: virtual members, hidden and explicit-only
members, encoding, coercion, equality and ordering, identity and liveness, methods with validators, virtual maps,
copy-on-read write-back, literal schemas, component-wise arithmetic (`GetComponents` / `MakeFromComponents`,
`NumericTupleAdapter.Arithmetic => true`) and literal kinds. See [`type-adapters.md`](type-adapters.md).

```csharp
sealed class Vector3Adapter : NumericTupleAdapter<Vector3>
{
    protected override int Width => 3;
    protected override string Expected => "(x, y, z) or [x, y, z]";
    protected override float Part(Vector3 v, int i) => v[i];
    protected override Vector3? Make(float[] f) => f.Length == 3 ? new Vector3(f[0], f[1], f[2]) : null;
    protected override bool Arithmetic => true;   // Scale * 1.5, Position + [0, 1, 0], length / normalize / lerp
}
```

Engines list their adapters (`RqlEngine.Adapters`, TS `engine.adapters`, C++ `Engine::adapters()`), and the members of
adapters' target types count as known names without a hook.

## Mutations

Writes are off until the host enables them, and each request must ask for them
(`QueryOptions { AllowMutation = true, DryRun = … }`).

```csharp
var m = engine.Mutations;
m.Enabled = true;
m.MaxTargets = 200;                 // default target limit; editors with undo: e.g. 200 (library default 20)
m.HardMaxTargets = 1000;            // the ceiling a request can raise it to
m.LimitArgument = "max_targets";    // the tool argument callers raise it with (mapped to Budgets.MaxMutationTargets)
m.CanWrite = (type, member) => !IsLocked(type, member);
m.ValidateBatch = records => SavesTwice(records) ? "two calls save the same file" : null;   // dry runs too
m.BeforeWrite = records => Undo.RecordObjects(Owners(records), "RQL mutation");            // throw = veto
m.Committed = records => MarkDirty(Owners(records));
m.CoerceText = (type, text) => ParseEngineText(type, text);    // e.g. "(Minimum=(X=0.5,Y=0),Maximum=(X=0.5,Y=0))"
engine.EnumAliases = type => DisplayNamesOf(type);             // "Fill" → HAlign_Fill, the editor's names
engine.Defaults.BudgetsAdjustable = false;                     // tools without a budgets argument
```

- **Records** carry the target, the **owner** (the nearest enclosing object on the written path, which is what hosts
  record for undo or mark dirty), the member or method, the old and new values and, for `call`, the coerced arguments.
- **Order of checks**: resolution and coercion, per-member and per-method validators, the batch validator, then the
  before-write hook (all invocations of a stage at once, so one undo step), then the writes, then the committed hook.
  Dry runs stop after the batch validator.
- **Lists**: a list or array member (or parameter) takes any list value, not only list literals: a host array or
  collection and another member's list convert element by element with the usual element coercion (`set Weights = Ids`
  writes an `int[]` into a `List<double>`). A value of the member's own type is assigned as is; strings and maps are
  never lists, and neither is a type the host treats as an object (Unity's `Transform`).
- **Target limit**: a request's limit outside 1 … `HardMaxTargets` is `invalid-value`. `mutation-limit` counts the
  matches (up to the ceiling) and advises the limit argument; `LimitHint` replaces that hint entirely.

## Literal construction

Object literals build values on writes ([spec §4.5](../spec/semantics.md#45-object-literals),
[ADR-0011](decisions/0011-literal-kinds-known-names-key-warnings.md)). Each type has a literal kind: **engine** (never
built; the default for a type with an identity or liveness adapter), **value** (built fresh) or **instanced** (built as
a subobject of its outer: the owner of the written member, the call's target, or the object an enclosing literal
builds). The kind is the first answer of the host hook, then the adapters, then the default; finally
`CanConstructFromLiteral`, when it answers, overrides it.

| Hook | Use |
|---|---|
| `Mutations.LiteralKindOf(type)` | mark value-like engine types (Godot resources) as value, Unreal `EditInlineNew` classes as instanced |
| `Mutations.CreateForLiteral(type, outer)` | build the empty value with its outer (Unreal `NewObject(Outer, Class)`, Unity `ScriptableObject.CreateInstance`) |
| `Mutations.CanConstructFromLiteral(type)` | older override, applied last: false → engine, true → an engine kind becomes value (instanced stays instanced) |
| `Mutations.LiteralRefusalHint(type)` | the advice after an engine-object refusal (e.g. "use unity.find(…) or an asset path") |
| C++ `MutationPolicy::resolve_subtype(target, name)` | lazy lookup of a literal's `$type` for runtime-built types (Unreal `UClass`es); answers are not cached and must not construct anything. .NET sees every loaded type and TypeScript registration is public, so they need no hook. A misspelt `$type` is answered with "did you mean …?" from the known subtypes (.NET: loaded concrete types, display names included, excluded types left out; TS: registered classes; C++: registered descriptors). The hook cannot list names, so C++ suggests a runtime-built type only after it was registered with `TypeRegistry::register_descriptor` |

Overload resolution checks every candidate without constructing anything (ADR-0010), so factories and constructors run
only for an overload that passes the check (normally the chosen one; a value that only a member validator rejects is
found during the real coercion). Construction happens while planning, so it runs on dry runs too.

## Hints and host texts

Hints must only name what the host has ([spec §10](../spec/semantics.md#10-budgets-and-errors),
[ADR-0013](decisions/0013-hints-and-host-texts.md)).

| Setting | Effect |
|---|---|
| `SchemaHint` (e.g. `"rql-schema"`) | "list the types with rql-schema" instead of "with the schema tool" |
| `MutateHint` (e.g. `"rql-mutate"`) | the `permission-denied` of a `set` / `call` sent as a query names the mutation tool |
| `FindHint` (e.g. `"unity.find(\"Floor\")"`) | a `ref()` given a name points to the host's finder |
| `IsKnownMember` | see [Engine and sources](#engine-and-sources) |
| `TypeDisplayName` (.NET; TS `TypeSpec.name` + `interfaces`, C++ registry name + `also_is`) | the host's type names in `$type`, `$ref`, schemas and hints; `is T`, `ref()` and `$type` accept both names |
| `EnumAliases` (TS `EnumType` `aliases`, C++ `EnumBuilder::alias`) | display names for enum entries |
| `Defaults.BudgetsAdjustable = false` | no hint advises `budgets.*` |
| `Mutations.LimitArgument` / `LimitHint` | the target-limit hint |

**Shared language rules.** Every runtime ships the host-independent rules agents need (conditions are booleans, `?:`,
list equality, group items, receipts, `call` and bulk create, vectors, missing map entries, arguments by name, typed
literals; [`fixtures/guide/language-rules.txt`](../fixtures/guide/language-rules.txt)). Append them to the agent guide:

```csharp
string rules = RqlLanguageGuide.Rules;                                       // placeholder editor.CreateObject
string rulesFor = RqlLanguageGuide.RulesFor("editor.CreateGameObject");      // the host's creation call
string rulesWith = RqlLanguageGuide.RulesWith(new RqlRuleExamples           // the host's own examples
{
    CreateCall = "editor.SpawnActor",
    Grid = "range(0, 150) | call editor.SpawnActor(\"StaticMeshActor\", \"Tile\" + $, [($ % 15) * 200, floor($ / 15) * 200, 0])",
    PerEntry = "[[\"A\", [0, 0, 0]], [\"B\", [200, 0, 0]]] | call editor.SpawnActor(\"PointLight\", $[0], $[1])",
    Named = "editor.SpawnActor({ClassName: \"StaticMeshActor\", Label: \"Tile\" + $, Location: [$ * 200, 0, 0]})",
});
```

`Grid` and `PerEntry` are whole examples (range or list included), so a host can keep the grid within its target limit;
`Named` is the call after `range(0, 10) | call`. An example left out keeps the default with the host's call. TypeScript:
`LANGUAGE_RULES`, `languageRules(createCall | {createCall, grid, perEntry, named})`; C++: `rql::language_rules()`,
`language_rules_for(call)`, `language_rules_with(rql::RuleExamples{…})`.

## Agent tools and output

The recommended tool surface is `rql_query` (read-only), `rql_mutate` (`dry_run`, plus the host's limit argument) and
`rql_schema` ([`agent-tool-contract.md`](agent-tool-contract.md)). The .NET `AgentFormat` renders the compact table
agents read:

```csharp
AgentFormat.Render(result, OutputFormat.Table);                       // receipts: first 50 rows + count
AgentFormat.Render(result, OutputFormat.Table, maxReceiptRows: 0);    // every receipt row
AgentFormat.Render(result, OutputFormat.Table, 50, moreRowsNote: "query the rest by name");   // "" drops the note
```

A compact receipt prints a method shared by every row once and leaves out a `$ref` that repeats the result's. The
default footer after the count says the json format lists every row; hosts whose tools offer no json format replace it
with `moreRowsNote`. TypeScript and C++ return JSON only (Unreal-MCP renders through a .NET sidecar).

## API names per runtime

| Concept | .NET | TypeScript | C++ |
|---|---|---|---|
| Source | `AddSource`, `AddSource<T>` | `addSource(name, getter, desc?, type?)` | `add_source`, `add_object_source` |
| Liveness / ref lookup | `IsAlive`, `ResolveStableRef` | `isAlive`, `resolveStableRef` | `is_alive`, `resolve_stable_ref` |
| Mutation switch | `Mutations.Enabled` | `mutations.enabled` | `mutations().enabled` |
| Target limit | `MaxTargets`, `HardMaxTargets`, `LimitArgument`, `LimitHint` | `maxTargets`, `hardMaxTargets`, `limitArgument`, `limitHint` | `max_targets`, `hard_max_targets`, `limit_argument`, `limit_hint` |
| Write policy and hooks | `CanWrite`, `ValidateBatch`, `BeforeWrite`, `Committed` | `canWrite`, `validateBatch`, `beforeWrite`, `committed` | `can_write`, `validate_batch`, `before_write`, `committed` |
| Text coercion | `CoerceText` | `coerceText` | `coerce_text` |
| Literal kinds | `LiteralKindOf`, `CreateForLiteral`, `CanConstructFromLiteral`, `LiteralRefusalHint` | `literalKindOf`, `createForLiteral`, `canConstructFromLiteral`, `literalRefusalHint` | `literal_kind_of`, `create_for_literal`, `can_construct_from_literal`, `literal_refusal_hint`, `resolve_subtype` |
| Hint texts | `SchemaHint`, `MutateHint`, `FindHint`, `IsKnownMember` | `schemaHint`, `mutateHint`, `findHint`, `isKnownMember` | `schema_hint`, `mutate_hint`, `find_hint`, `is_known_member` |
| Type and enum names | `TypeDisplayName`, `EnumAliases` | `TypeSpec.name` / `interfaces`, `EnumType` `aliases` | registry name / `also_is`, `EnumBuilder::alias` |
| Budget advice | `Defaults.BudgetsAdjustable` | `defaults.budgetsAdjustable` | `defaults().budgets_adjustable` |
| Language rules | `RqlLanguageGuide.Rules` / `RulesFor` / `RulesWith` | `LANGUAGE_RULES` / `languageRules` | `language_rules` / `_for` / `_with` |
| Modules | `IRqlModule`, `AddModulesFrom`, `RqlModuleReport` | — | — |
