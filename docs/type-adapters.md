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
| Identity | `StableId(value)`, `IsAlive(value)` | `$ref` / `ref("Type#id")`, `stale-ref` |
| Describe | `Description` | schema / `describe` |

**Applicability and order.** `Target` can be an exact type, a base class, an interface or an open generic
(`typeof(List<>)`). Every applicable adapter participates, most specific first: the base chain from the type itself
(closed type, then its open generic definition, at each level), then interfaces, then open generic interfaces. Ties go
by `Priority` (higher first), then registration order. Member configuration runs in the reverse order (least specific
first), so a `Renderer` adapter refines what the `UnityEngine.Object` adapter configured. `engine.Types.Explain(type)`
prints the resolved order.

**Safety rules.** Validation runs before any write, and a setter that throws triggers the normal rollback. Virtual
setters must round-trip their getter (the rollback writes the old value back through the setter). Virtual setters on
value types are rejected (they would write to a copy). Configuring a member that does not exist fails the query with
`internal-error` and names the member. Adapters are per engine: engines without adapters keep the shared,
adapter-free type model. When no adapter is registered anywhere, the cost is one static read (measured within ±2% of
the pre-adapter build on every benchmark query, with dynamic PGO off for deterministic codegen).

## Unity-MCP converters → RQL adapters

What Unity-MCP does today through ReflectorNet converters, and the RQL equivalent:

| Unity-MCP converter behaviour | RQL adapter |
|---|---|
| Generic Unity converter: public members plus `[SerializeField]` private fields | `IncludeFields(f => f.IsDefined(typeof(SerializeField)))` on a `UnityEngine.Object` adapter (inherited by all subclasses) |
| `UnityEngine.Object` fake-null, reference by instance id | `StableId` → `GetInstanceID()`, `IsAlive` → `o != null`; destroyed objects give `stale-ref` |
| `GameObject`: `activeSelf` written through `SetActive`, noisy members ignored | virtual `active` member with getter `activeSelf` and setter `SetActive`; `Hide(...)` |
| `Component` back-pointers (`gameObject`, `transform`) ignored to avoid cycles | `Reference("gameObject", "transform")`: still readable by path, not followed by generic `recurse` |
| `Renderer` / `MeshFilter`: `material(s)` / `mesh` ignored (getters instantiate copies) | `ExplicitOnly("material", "materials", "mesh")`: never read implicitly; read only when a query names them |
| `Material`: shader properties exposed as members | reads: a virtual `properties` map member computed per instance (`Materials.properties["_Glossiness"]`). Member tables are per type and shader properties are per instance, so writes need adapter-defined methods, which don't exist yet (see roadmap) |
| Asset references by id / path / GUID | `StableId` → asset GUID; host `ResolveStableRef` resolves `Texture2D#<guid>` |
| `Texture` / `Sprite`: no cascade into pixel data | `Only("name", "width", "height", "format")` |
| `Scene`: whitelist (other getters crash) | `Only(...)`: unlisted members do not exist for RQL, so they cannot be read by accident |
| Struct converters (`Vector3`, `Color`, `Quaternion`): fields only, readable literals | `Encode` to `"(x, y, z)"`, `TryCoerce` from string or `[x, y, z]`, `AreEqual` with epsilon, virtual `magnitude` |
| `EntityId` serialized as a string | `Encode` → string, `TryCoerce` from string |

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
systems can express. They pass the same `fixtures/adapters/cases.json`.
