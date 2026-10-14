# Type adapters (custom converters)

Hosts teach RQL how engine types should be **read, shown, written and compared** with type adapters
(normative rules: [spec §13](../spec/semantics.md#13-type-adapters-host-extension-normative-behaviour); golden
vectors: [`fixtures/adapters/cases.json`](../fixtures/adapters/cases.json)). Adapters change the reflection model and
value semantics only. Grammar and plans stay the same, so a plan written against a Unity host runs unchanged against a
remote executor with the same adapters.

## .NET API

```csharp
engine.AddAdapter(new Vector3Adapter())
      .AddAdapter(new UnityObjectAdapter())
      .AddAdapter(new RendererAdapter());

sealed class Vector3Adapter : RqlTypeAdapter<Vector3>
{
    public override string Description => "Vector3: write as \"(x, y, z)\" or [x, y, z]";
    protected override void Members(MemberTable<Vector3> m) => m.Add("magnitude", v => v.magnitude);
    protected override JsonNode? Encode(Vector3 v, EncodeContext c) => JsonValue.Create($"({v.x}, {v.y}, {v.z})");
    public override AdapterOutcome TryCoerce(object? value, Type target, out object? result, out string? reason) { … }
    protected override bool? Equal(Vector3 v, object? other) => TryVec(other, out var o) ? (v - o).sqrMagnitude < 1e-10f : null;
}

sealed class UnityObjectAdapter : RqlTypeAdapter<UnityEngine.Object>
{
    protected override string? Id(UnityEngine.Object o) => o.GetInstanceID().ToString();   // $ref "GameObject#12345"
    protected override bool? Alive(UnityEngine.Object o) => o != null;                       // fake-null → stale-ref
    protected override void Members(MemberTable<UnityEngine.Object> m) =>
        m.IncludeFields(f => f.IsDefined(typeof(SerializeField), false)).Hide("hideFlags");
}

sealed class RendererAdapter : RqlTypeAdapter<Renderer>
{
    // Renderer.material instantiates a material copy: never read it implicitly.
    protected override void Members(MemberTable<Renderer> m) => m.ExplicitOnly("material", "materials");
}
```

| Capability | Members of `RqlTypeAdapter` | Effect |
|---|---|---|
| Members | `ConfigureMembers(MemberTable)` / `Members(MemberTable<T>)`: `Add` (virtual member: getter, optional setter, `validate`, description), `Hide`, `Only`, `IncludeFields`, `ReadOnly`, `Reference`, `ExplicitOnly`, `Describe` | Paths, `where`, `select`, `set`, `describe`, generic `recurse`, ref search |
| Encode | `Encode(value, EncodeContext)` (context: `EncodeValue`, `Summary`, `Depth`) | Output cells |
| Coerce | `TryCoerce(value, target, out result, out reason)` → `NotHandled` / `Ok` / `Failed` | `set` values, `call` arguments |
| Compare | `AreEqual(value, other)`, `Compare(value, other)` | `==`, `!=`, `<`…, `sort`, `in`, `contains` |
| Methods | `MemberTable.AddMethod`, typed `Method` / `Function`, `Expose(name)` for unannotated CLR methods, `HideMethods` | `call` stage, `describe` |
| Virtual maps | `MemberTable.AddMap` / typed `Map<TValue>(name, keys, get, set?, validate?)` | `M["k"]` reads, `set M["k"] = v` writes |
| Mixed-type maps | `MemberTable.AddMixedMap(name, keys, entryType, get, set?, validate?)` | per-key value types (e.g. Material properties) |
| Write-back | `CopyOnRead(names)`, `WritesThrough()` | copy-returning getters, handle structs |
| Exclusion | `HideWhere(pred)`, `engine.ExcludeTypes(pred)` | `[Obsolete]` members, blacklisted types |
| Identity / display | `Identity(name)`, `Display(name)` | `$ref` ids, summary names |
| Literals | `LiteralSchema` (JSON Schema), object literals reach `TryCoerce` | `describe` → `literal` |
| Host hooks | `engine.Mutations.BeforeWrite` / `Committed` (records with `Owner`) | undo, dirty marking, vetoes |
| Discovery | `engine.AddAdaptersFrom(assemblies)`, `[RqlAdapterOrder]`, `RqlTypeAdapter(string typeName)` | module scanning, optional engine modules |
| Identity | `StableId(value)`, `IsAlive(value)` | `$ref` / `ref("Type#id")`, `stale-ref` |
| Describe | `Description` | schema / `describe` |

**Applicability and order.** `Target` can be an exact type, a base class, an interface or an open generic
(`typeof(List<>)`). Every applicable adapter participates, most specific first: the base chain from the type itself
(closed type, then its open generic definition, at each level), then interfaces, then open generic interfaces. Ties go
by `Priority` (higher first), then registration order. Member configuration runs in the reverse order (least specific
first), so a `Renderer` adapter refines what the `UnityEngine.Object` adapter configured. `engine.Types.Explain(type)`
prints the resolved order.

**Safety rules.** Validation runs before any write, and a setter that throws triggers the normal rollback. Virtual
setters should round-trip their getter (the rollback writes the old value back through the setter). Virtual setters on
value types are rejected (they would write to a copy). Configuring a member that does not exist fails the query with
`internal-error` and names the member. Adapters are per engine: engines without adapters keep the shared,
adapter-free type model. Objects reachable only through explicit-only members are not found by `ref("Type#id")`
search, so give such hosts a `ResolveStableRef`. `distinct` / `group-by` don't use adapter equality in 0.1. When no adapter is registered anywhere, the cost is one static read (measured within ±2% of
the pre-adapter build on every benchmark query, with dynamic PGO off for deterministic codegen).

## Unity-MCP converters → RQL adapters

Every customisation Unity-MCP makes through ReflectorNet 5.4.2 converters (`Runtime/Converter/`, registered in
`Runtime/UnityMcpPlugin.Converters.cs`) and its RQL equivalent. None of the Unity-AI-* extensions define converters;
they rely on the generic ones plus `SetDirty` after modifications (covered by the committed hook).

| Unity-MCP behaviour | RQL |
|---|---|
| Generic converter for **all** types: private `[SerializeField]` fields added, `[Obsolete]` / `[NonSerialized]` dropped | one adapter on `object`: `IncludeFields(f => f.IsPrivate && f.IsDefined(typeof(SerializeField)))` and `HideWhere(m => m.Member?.IsDefined(typeof(ObsoleteAttribute), true) == true \|\| …NonSerialized…)` |
| Type blacklist (~20 types: delegates, `IntPtr`, Span, TMPro/TextCore/RTHandle/Fusion/Addressables by assembly) | `engine.ExcludeTypes(t => …)`: members of those types, element types or generic arguments are dropped everywhere (delegates and by-ref-like types are never members anyway) |
| `UnityEngine.Object`: reference as `{instanceID}`, fake-null serialised as null | `StableId` → `GetEntityId()` (Unity 6.5+) / `GetInstanceID()`, `IsAlive` → `o != null`: destroyed objects **read as null** (`where target == null` finds missing references), `ref()` to them is `stale-ref`; `Display("name")` for the lowercase name |
| Object references assigned from `{instanceID}` JSON | `set target = ref("Texture2D#123")` |
| `GameObject`: ignores `gameObject`, `transform`, `scene`, `transformHandle`; `activeSelf` written through `SetActive` | `Reference(…)` / `Hide(…)`; virtual `active` member with setter `SetActive`, or `Expose("SetActive")` → `call SetActive(false)`; a virtual `Components` member (what `GameObjectData` builds) |
| Components: back-pointers `gameObject` / `transform` ignored | `Reference("gameObject", "transform")` |
| `Transform`: non-public `parentInternal` ignored | non-public properties are never members |
| `Renderer` / `MeshFilter` / `Collider`: `material(s)` / `mesh` ignored (getters instantiate copies) | `ExplicitOnly(…)`; `CopyOnRead("sharedMaterials")` so `set sharedMaterials[1] = ref(…)` is assigned back instead of lost; the Collider adapter targets `"UnityEngine.Collider"` by name and is inert without the physics module |
| `Material`: shader properties (int / float / range / color / vector / texture) via `Has*` / `Get*` / `Set*`; `shader` set from a name via `Shader.Find`; `SetDirty` after writes | `AddMixedMap("properties", keys, entryType: per property type, get, set, validate)` → `set Materials.properties["_Color"] = "(1, 0, 0, 1)"`; `TryCoerce` string → `Shader`; the committed hook receives the Material as owner |
| Assets: resolve by instanceID, asset path or GUID; Texture / Sprite reference-only with Sprite ↔ Texture conversion; `TextAsset.bytes` ignored | `ResolveStableRef` (`ref()` resolves exact ids first, so `ref("Material#Assets/My Mats/Red (1).mat")` works); summaries for reference-only types; `TryCoerce` Sprite ↔ Texture; `Hide("bytes")` |
| Structs (Vector*, Color*, Quaternion, Matrix4x4, Bounds*, Rect*, gradient keys): fields only, whole-value writes | `Encode` / `TryCoerce` / `AreEqual` / `Compare`, `Only(…)`, `LiteralSchema`; object literals `{x: 1, y: 2, z: 3}` construct them |
| `Scene`: whitelist of 8 safe properties | `Only(…)` |
| `EntityId` as string or ulong | `Encode` / `TryCoerce` |
| `Gradient` / `AnimationCurve`: whole-value set from JSON; arrays returned as copies | `TryCoerce` from object literals; `CopyOnRead("colorKeys", "keys")` |
| Particle system modules (`ps.main` …): handle structs, writes go to the native object | `WritesThrough()` on each module type: `set main.duration = 4` is live without write-back |
| `[Serializable]` class fields, `[SerializeReference]` subtype replacement (`$type`) | object literals construct values: `set effect = {"$type": "BurnEffect", damage: 3}` |
| Collections: element writes, whole-list replacement | `set items[2] = …`, list literals, `append` / `insert` / `remove-at` / `without` |
| Post-modification `EditorUtility.SetDirty` / repaint | `Mutations.Committed` with the owners; `Mutations.BeforeWrite` for `Undo.RecordObjects` or a veto |
| Converter modules discovered by assembly scan | `engine.AddAdaptersFrom(assemblies)` with `[RqlAdapterOrder]`, failures isolated and reported |

Deliberately different: RQL queries and assigns members instead of round-tripping whole serialized objects (no
SerializedMember diff format, no JSON repair of malformed nesting); writes are all-or-nothing instead of partial
success; method calls are limited to allowlisted / exposed instance methods.

## Compared with ReflectorNet converters

| ReflectorNet (5.4.2) | RQL adapters |
|---|---|
| Converter choice by priority score (exact 10001, else 10000 − inheritance distance); equal scores are resolved by `ConcurrentBag` enumeration order, which is not deterministic | Deterministic total order: specificity tier, then priority, then registration order; `Explain` shows it |
| One winning converter per type: a subclass converter replaces the base converter's behaviour | All applicable adapters compose: members are configured base → derived, other capabilities fall through to the next adapter |
| An ignored member is gone for reads, writes and queries | `Hide` (gone), `ExplicitOnly` (never read implicitly, still addressable by path) and `Reference` (not traversed) are separate tools |
| Side-effecting getters must be ignored completely | `ExplicitOnly` keeps them reachable without implicit invocation |
| Serialization-oriented: no say in comparisons | Equality/ordering hooks, so `where Position == "(0, 1, 0)"` and `sort Position` mean what the host intends |
| Write errors come back as converter-specific text | Coercion and validation failures become `coercion-failed` with the adapter's reason; all-or-nothing rollback is kept |
| Per-reflector converter registry | Per-engine registry; engines without adapters are unaffected |

## Other runtimes

TypeScript (ThreeJS hosts: `Vector3`, `Color`, `Object3D`) and C++ (Unreal: `FVector`, `FRotator`, `UObject`
validity/pending-kill) implement the same capabilities with idiomatic APIs, limited to the resolution tiers their type
systems can express (TS: exact / prototype chain / `matches(ctor)` predicate; C++: exact / base distance / interface
name, no `IncludeFields` since C++ cannot reflect private fields). In TS, member shaping needs a registered class:
an adapter with `members` that resolves to an undeclared (per-instance) class fails the query with `internal-error`
rather than silently not hiding anything. All three pass the same
`fixtures/adapters/cases.json`.
