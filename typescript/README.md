# RQL — TypeScript runtime

TypeScript/JavaScript implementation of RQL v0.1 (`spec/`). Three npm workspace packages, no
runtime dependencies (TypeScript is the only dev dependency):

| Package | Contents |
|---|---|
| `@rql/contracts` | `QueryPlan` / `Expr` / `Stage` types, canonical plan codec (`serializePlan`, `deserializePlan`, same key order, default omission, validation and error codes as the .NET `PlanCodec`), `QueryResult` / `Diagnostic` / `Budgets`, `RqlError`. |
| `@rql/parser` | `parse(text)` / `tryParse(text)`: port of the .NET lexer/parser, produces identical canonical plans. |
| `@rql/execution` | `RqlEngine` over **live** JS objects, `TypeModel` reflection model, extension sources/functions/stages, mutations. |

```bash
npm install
npm test            # tsc -b, golden-vector conformance (prints "N passed, M failed"), node:test suites
npm run conformance # conformance only
```

## Usage

```ts
import { RqlEngine, TypeModel, EnumType, t } from '@rql/execution';

enum Kind { Alpha, Beta }
class Vec2 { constructor(public x = 0, public y = 0) {} }
class Unit { Id = 1; Name = 'u'; Kind = Kind.Alpha; Hp = 10; Pos = new Vec2(); Parent: Unit | null = null; }

const types = new TypeModel()
  .register(Vec2, { kind: 'struct', members: [{ name: 'x', type: t.number }, { name: 'y', type: t.number }] })
  .register(Unit, {
    members: [
      { name: 'Id', type: t.int },                 // identity (backs $ref, always read-only)
      { name: 'Name', type: t.string },            // display member (summaries)
      { name: 'Kind', type: new EnumType('Kind', Kind) },
      { name: 'Hp', type: t.int },
      { name: 'Pos', type: t.of(Vec2) },
      { name: 'Parent', type: t.nullable(t.of(Unit)), reference: true }, // not followed by generic recurse
    ],
    methods: [{ name: 'heal', params: [{ name: 'amount', type: t.int }] }], // allowlisted for `call`
  });

const engine = new RqlEngine({ types }).addSource('world', () => world);
engine.mutations.enabled = true;                       // host switch; requests still need allowMutation
engine.execute('world.units | where Kind == "alpha" and Hp < 20 | select $ref Name Hp');
engine.execute('ref("Unit#1") | set Pos.x = 5', {}, { allowMutation: true, dryRun: true });
```

Engine hooks: `addSource`, `addFunction('ns.fn', args => ...)`, `addStage('ns.stage', (items, args, named) => iterable)`,
`mutations.canWrite`, `isAlive(o)` (liveness), `resolveStableRef(type, id)` (falls back to a bounded
search of owning members of all sources), `defaults` (budgets/hard caps), `alwaysTrackPaths`.

## Reflection model and its limits

TypeScript types are erased at runtime, so declared types come from **descriptors**
(`TypeModel.register(Class, spec)`): member order, `int` vs `number`, enums, structs, lists, maps,
nullability, identity / display members, non-owning references, read-only members, callable methods.
Base classes are detected through the prototype chain (base members first). Unregistered subclasses
of a registered class inherit its descriptor. `model.tag(obj, Class)` treats a plain object as a
registered type.

Undeclared values work through a **dynamic fallback**: own enumerable properties (never invoking
getters while listing) plus prototype getters; identity is an `Id`/`id` member, display a string
`Name`/`name` member; `$type` is the constructor name (minified code changes it). The fallback cannot
know `int` vs `number`, enums or structs (objects without identity are encoded inline), and writes are
accepted without coercion.

Runtime kinds: `Array`/`Set`/typed arrays/other iterables = lists, `Map` and plain objects in a member
declared `t.map(...)` = maps, `EnumValue` (from enum-typed members, or stored by the host inside
collections) = enums, `Date` = scalar. Enum members store the raw TS enum value back on `set`.
Structs are mutated in place (JS objects are references); `set Pos.x = 5` additionally writes the struct
back through a writable parent member, and assigning a whole struct assigns a copy.

## Type adapters (spec §13)

```ts
engine.addAdapter({
  target: THREE.Vector3,                     // class and subclasses; or { interfaceName, matches(ctor) }
  priority: 0, name: 'Vector3Adapter', description: 'Vector3: write as "(x, y, z)" or [x, y, z]',
  members: (m) => m.add('len', { type: t.number, get: (v) => v.length() }).hide('isVector3'),
  encode: (v) => `(${v.x}, ${v.y}, ${v.z})`,
  coerce: (value) => Array.isArray(value) ? { ok: true, value: new THREE.Vector3(...value) } : { ok: false, reason: 'expected [x, y, z]' },
  equals: (v, other) => other instanceof THREE.Vector3 ? v.equals(other) : undefined,
  compare: (v, other) => other instanceof THREE.Vector3 ? v.length() - other.length() : undefined,
  stableId: undefined, isAlive: undefined,   // identity / liveness for object types
});
engine.explain(THREE.Vector3);               // ordered adapters with rank and priority
```

Resolution: exact class (rank 0), base classes by prototype-chain distance (up to `Object`), then
`matches` adapters; ties by priority (higher first), then registration order. Member configuration
(`add`, `hide`, `only`, `readOnly`, `reference`, `explicitOnly`, `describe`) runs least → most specific
and needs a registered descriptor (an adapter with `members` on an undeclared class fails the query with
`internal-error`); the other capabilities also apply to undeclared class instances,
`Array`/`Map`/`Set`/`Date`. Primitives (`string`, `number`, `boolean`) are not adaptable. Virtual members
are non-owning by default; a virtual setter must round-trip its getter (rollback). `addAdapter` forks a
`TypeModel` shared with other engines, so their behaviour does not change.
