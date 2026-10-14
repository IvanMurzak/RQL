# RQL — C++ runtime

C++17 library that decodes, validates and executes RQL **QueryPlans** (spec/query-plan.schema.json)
against live C++ objects. Zero third-party dependencies. There is no RQL text parser here by design:
the .NET / TypeScript sidecar parses text into a plan, C++ executes it.

```bash
cmake -S cpp -B cpp/build && cmake --build cpp/build && ctest --test-dir cpp/build
cpp/build/rql_conformance . -v     # golden vectors, verbose (run from the repo root)
```

ctest runs every suite twice: against `rql` and against `rql_nortti` (same sources built with
`-fno-rtti` / `/GR-`). Options: `-DRQL_NO_RTTI=ON` builds the main library and tests without RTTI;
`-DRQL_TEST_NO_RTTI_VARIANT=OFF` skips the extra variant; `-DCMAKE_CXX_STANDARD=20` builds as C++20.

## Unreal Engine (5.5+) integration

- **No RTTI needed.** The library never uses `typeid`, `std::type_index` or `dynamic_cast`. Type
  identity is a compile-time key (`rql::type_key<T>()`, the address of a per-type tag). For
  polymorphic hierarchies the registry finds the most-derived descriptor through either a
  `rql::TypeKey rql_dynamic_type() const` member (usually virtual; auto-detected) or a provider hook
  `registry.object<Base>("Base").dynamic_type([](const Base& b) { return ...; })` — for UObjects the
  hook maps `b.GetClass()` to a registered key.
- **Exceptions.** In UE, the RQL module sets `bEnableExceptions = true` (and needs no RTTI);
  exceptions never cross the public API: every `rql::Engine` member function is `noexcept` and turns
  any failure (including a throwing host getter, setter, function or stage) into `ok=false` with a
  diagnostic (`internal-error` for unexpected ones). The free codec helpers that throw
  (`decode_plan`, `Json::parse`, `Budgets::from_json`, `QueryOptions::from_json`) have `noexcept`
  `try_` counterparts (`try_decode_plan`, `Json::try_parse`, `*::try_from_json`) for host code; the
  throwing builder/`Traits` helpers are only called from registration code and from inside the
  engine. Engine registration with an invalid name is ignored and reported via `last_error()`.
- **Provider callbacks may be `noexcept`**: getters/setters/properties/stage handlers are plain
  `std::function`s, and `method()` accepts `noexcept` and `const noexcept` member functions.
- Type keys are per program image: register and query types from the same module (or export the
  registration function), as with any address-based key across DLL boundaries.

## Layout

- `include/rql/` — public headers: `json.hpp` (JSON value/parser/writer), `plan.hpp` (plan model +
  builders), `codec.hpp` (canonical codec), `result.hpp` (QueryResult, Diagnostic, Budgets),
  `value.hpp` (runtime values, list/map adapters), `reflection.hpp` (descriptors, registry, builders),
  `engine.hpp` (host API), `rql.hpp` (umbrella).
- `src/` — implementation; `exec.hpp` is internal (execution state, streams, compiler).
- `tests/` — `rql_conformance` (fixtures/), `rql_unit` (JSON edge cases + engine features), and the
  conformance graph as real C++ types (`conformance_graph.*`).

## Host API

```cpp
rql::TypeRegistry reg;
reg.structure<Vec2>("Vec2").member("x", &Vec2::x).member("y", &Vec2::y);
reg.enumeration<NodeKind>("NodeKind").value("Alpha", NodeKind::Alpha).value("Beta", NodeKind::Beta);
reg.object<Node>("Node")
    .member("Id", &Node::Id).readonly()           // identity members are read-only anyway
    .member("Name", &Node::Name)
    .member("Children", &Node::Children)            // std::vector<std::unique_ptr<Node>>: owning
    .member("Parent", &Node::Parent).reference()    // Node*: non-owning, not followed by generic recurse
    .property("Depth", [](Node& n) { return n.depth(); })
    .method("Heal", &Node::Heal, {"amount"})        // allowlisted for `call`
    .id("Id").display("Name");                     // defaults: "Id" (int/string) and "Name" (string)
reg.object<Special>("Special").base<Node>().member("Power", &Special::Power);
// Polymorphism without RTTI: Node has `virtual rql::TypeKey rql_dynamic_type() const`
// (Special overrides it to return rql::type_key<Special>()), or register .dynamic_type(fn).

rql::Engine engine(reg);
engine.add_object_source("world", &world);          // or add_source(name, [] { return rql::Value...; })
engine.add_function("myapp.dist", fn);              // namespaced extensions
engine.add_stage("myapp.expand", handler);
engine.mutations().enabled = true;                  // + per request: QueryOptions::allow_mutation = true
engine.is_alive = [](const rql::Value& o) { ... };  // liveness for explicitly destroyed objects
engine.resolve_stable_ref = [](auto& type, auto& id) { ... };  // fast "Type#id" lookup

rql::QueryResult r = engine.execute_json(plan_json, budgets, options);
std::string json = r.dump(/*include_stats=*/true);
```

Supported member types out of the box: arithmetic types, `bool`, `std::string`, enums, registered
structs (value semantics) and objects (reference semantics), `std::optional<T>`, `T*`,
`std::unique_ptr<T>`, `std::shared_ptr<T>`, `std::vector<T>`, `std::map<K,V>` and
`std::unordered_map<K,V>` (iterated sorted by key for determinism). Add more by specializing
`rql::Traits<T>` or by implementing the adapter interfaces below.

## Type adapters (spec §13)

`include/rql/adapters.hpp`. An `rql::TypeAdapter` is a struct of optional `std::function` capabilities
(`configure_members`, `encode`, `coerce`, `equals`, `compare`, `stable_id`, `is_alive`, `description`)
targeting a descriptor (exact type or base class) or an interface name (`also_is`). Register with
`engine.add_adapter(...)` before running queries; `engine.explain(type)` lists the applicable adapters
most specific first (base distance, then priority, then registration order; interfaces last; no
open-generic tier in C++).

```cpp
rql::TypeAdapter vec;
vec.target = reg.descriptor<FVector>();
vec.description = "FVector: write as \"(x, y, z)\" or [x, y, z]";
vec.configure_members = [](rql::MemberTable& t) { t.computed<FVector>("len", [](FVector& v) { return v.Size(); }); };
vec.encode = [&](const rql::Value& v, const rql::EncodeContext&) -> std::optional<rql::Json> { ... };
vec.coerce = [&](const rql::Value& v, const rql::TypeDescriptor&, rql::Value& out, std::string& why) { ... };
rql::TypeAdapter uobj;
uobj.target = reg.descriptor<UObject>();
uobj.is_alive = [&](const rql::Value& v) -> std::optional<bool> { return IsValid(rql::value_as<UObject>(reg, v)); };
engine.add_adapter(std::move(vec)).add_adapter(std::move(uobj));
```

Adapters can also add **callable methods** (`MemberTable::method(name, params, returns, fn,
description)`, `hide_methods`) invoked by `call` exactly like registered host methods (coercion,
defaults, overloads by arity, dry-run), and **virtual maps** (`MemberTable::map(name, keys, get, set,
validate)`) read and written through the ordinary map paths, e.g. a `UMaterialInstanceDynamic`'s
scalar parameters as `ScalarParams["Roughness"]` with validation before any write.

Parity with ReflectorNet-style converters (spec §13): `copy_on_read` members (getter returns a
copy; writes are assigned back), `writes_through()` handle structs (e.g. `FParticle…Module`-style
handles), `hide_where`, `Engine::exclude_types`, `identity` / `display` by name, `mixed_map`, a
`literal_schema` shown by `describe`, liveness verdicts that make destroyed (pending-kill) objects
read as null, and `MutationPolicy::before_write` / `committed` hooks whose records carry the
**owner** (the last reference object on the written path, e.g. the material when setting through a
mesh component) for undo / dirty marking.

Adapters are per `Engine`: the engine derives an adaptation of each descriptor an adapter applies to
(members after adapters, id/display recomputed, per-capability adapter lists), so one `TypeRegistry`
can serve engines with different adapter sets. An engine without adapters reads descriptors
directly (one flag check per lookup). Member-table configuration bugs (unknown member names, virtual setters on value types) fail
the query with `internal-error`. Callbacks receive `self` as a pointer to the adapter's target type.

## Provider seam (how a future Unreal adapter plugs in)

C++ has no runtime reflection, so the executor never touches C++ types directly. Everything goes
through three type-erased seams:

1. **`TypeDescriptor` / `MemberDescriptor` / `MethodDescriptor`** (`reflection.hpp`): name, kind
   (object/struct/enum/scalar/list/map), base chain with pointer casts, members with
   `Getter = Value(void* self)` / optional `Setter`, owning / readonly / nullable flags, id and display
   member, callable methods with parameter descriptors. The template builders (`object<T>()`,
   `member(&T::field)`) are only one way to fill them; an Unreal provider would build descriptors at
   runtime from `UClass` / `UStruct` / `UEnum` and `FProperty` (`ContainerPtrToValuePtr`), mapping
   `CPF_Edit`/`BlueprintReadOnly` to readonly, `TObjectPtr`/`TWeakObjectPtr` to references, and
   `UFUNCTION` allowlists to methods.
2. **`IListAdapter` / `IMapAdapter`** (`value.hpp`): `size()/at(i)/set(i)` and
   `for_each/find/set/erase` with key coercion. `std::vector`/`std::map` adapters are provided;
   `TArray` / `TMap` / `TSet` adapters implement the same interfaces (map iteration must be
   deterministic: insertion order or sorted).
3. **`Value`** (`value.hpp`): missing / null / unknown / error(code) / bool / number (+ scalar
   descriptor, single-precision aware) / string / enum(descriptor, value) / object(descriptor, pointer
   to the most-derived type, optional keep-alive) / struct(descriptor, owned copy) / list / map /
   map entry / projected row. Struct writes (`set Pos.x = 5`) mutate a copy and are written back
   through every enclosing struct slot up to the first object, exactly like .NET.

Identity is `(pointer, dynamic descriptor)`; `Engine::is_alive` must be installed by hosts with
explicit destruction (Unreal `IsValid`) — without it, `Type@n` handles are re-validated by
reachability from the sources, never by dereferencing a stale pointer.

## Limitations

See `docs/feature-matrix-cpp.md` and `cpp/SPEC-QUESTIONS.md`.
