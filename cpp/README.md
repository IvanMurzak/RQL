# RQL — C++ runtime

C++17 library that decodes, validates and executes RQL **QueryPlans** (spec/query-plan.schema.json)
against live C++ objects. Zero third-party dependencies. There is no RQL text parser here by design:
the .NET / TypeScript sidecar parses text into a plan, C++ executes it.

```bash
cmake -S cpp -B cpp/build && cmake --build cpp/build && ctest --test-dir cpp/build
cpp/build/rql_conformance . -v     # golden vectors, verbose (run from the repo root)
```

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
