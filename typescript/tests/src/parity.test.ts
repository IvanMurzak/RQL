// Unity-MCP / ReflectorNet parity features (spec §4.3, §4.5, §7, §13): host write hooks, writes-through
// handles, copy-on-read members, predicate hiding and type exclusion, destroyed objects, object literals.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t, type MutationRecord } from '@rql/execution';

class Color {
  constructor(
    public r = 0,
    public g = 0,
    public b = 0,
  ) {}
}
class Material {
  constructor(
    public Id: string,
    public color = new Color(),
  ) {}
}
class Renderer {
  constructor(
    public Id: number,
    public Name: string,
    public material: Material,
  ) {}
}
/** A handle to shared state (like Unity's ParticleSystem.MainModule). */
class MainModule {
  constructor(private readonly ps: ParticleSystem) {}
  get duration(): number {
    return this.ps.state.duration;
  }
  set duration(v: number) {
    this.ps.state.duration = v;
  }
}
class ParticleSystem {
  state = { duration: 1, loop: false };
  constructor(public Id: number) {}
  get main(): MainModule {
    return new MainModule(this); // a fresh copy on every read, writing through to `state`
  }
}

function graphics(): { e: RqlEngine; renderers: Renderer[]; systems: ParticleSystem[] } {
  const renderers = [new Renderer(1, 'r1', new Material('m1')), new Renderer(2, 'r2', new Material('m2'))];
  const systems = [new ParticleSystem(10)];
  const types = new TypeModel()
    .register(Color, { kind: 'struct', members: [{ name: 'r', type: t.number }, { name: 'g', type: t.number }, { name: 'b', type: t.number }] })
    .register(Material, { members: [{ name: 'Id', type: t.string }, { name: 'color', type: t.of(Color) }] })
    .register(Renderer, { members: [{ name: 'Id', type: t.int }, { name: 'Name', type: t.string }, { name: 'material', type: t.of(Material) }] })
    .register(MainModule, { kind: 'struct', members: [{ name: 'duration', type: t.number }] })
    .register(ParticleSystem, { members: [{ name: 'Id', type: t.int }, { name: 'main', type: t.of(MainModule) }] });
  const e = new RqlEngine({ types })
    .addSource('renderers', () => renderers)
    .addSource('systems', () => systems)
    .addAdapter({
      target: MainModule,
      members: (m) => m.writesThrough().add<boolean>('loop', { type: t.bool, get: (mm) => (mm as unknown as { ps: ParticleSystem }).ps.state.loop, set: (mm, v) => void ((mm as unknown as { ps: ParticleSystem }).ps.state.loop = v) }),
    });
  e.mutations.enabled = true;
  return { e, renderers, systems };
}

const mut = { allowMutation: true };

test('host hooks: owner is the last reference object on the path; veto; dry-run; committed failure is a warning', () => {
  const { e, renderers } = graphics();
  const seen: { phase: string; records: readonly MutationRecord[] }[] = [];
  e.mutations.beforeWrite = (records) => void seen.push({ phase: 'before', records });
  e.mutations.committed = (records) => void seen.push({ phase: 'committed', records });

  const r = e.execute('renderers | set material.color.r = 1', {}, mut);
  assert.equal(r.ok, true);
  assert.deepEqual(seen.map((s) => s.phase), ['before', 'committed']);
  const rec = seen[1]!.records;
  assert.equal(rec.length, 2);
  assert.equal(rec[0]!.item, renderers[0]); // the target ...
  assert.equal(rec[0]!.owner, renderers[0]!.material); // ... but the Material owns the changed Color
  assert.deepEqual([rec[0]!.member, rec[0]!.old, rec[0]!.new], ['material.color.r', 0, 1]);

  seen.length = 0;
  e.execute('renderers | set material.color.g = 1', {}, { allowMutation: true, dryRun: true });
  assert.equal(seen.length, 0);

  e.mutations.beforeWrite = () => {
    throw new Error('locked by version control');
  };
  const vetoed = e.execute('renderers | set material.color.b = 1', {}, mut);
  assert.equal(vetoed.diagnostics[0]?.code, 'permission-denied');
  assert.deepEqual(renderers.map((x) => x.material.color.b), [0, 0]);

  e.mutations.beforeWrite = undefined;
  e.mutations.committed = () => {
    throw new Error('undo stack full');
  };
  const warned = e.execute('renderers | set Name = "x"', {}, mut);
  assert.equal(warned.ok, true);
  assert.equal(warned.diagnostics[0]?.code, 'hook-threw');
  assert.deepEqual(renderers.map((x) => x.Name), ['x', 'x']);
});

test('host hooks on call: completed calls are reported even when a later call fails', () => {
  class Counter {
    n = 0;
    constructor(public Id: number) {}
  }
  const cs = [new Counter(1), new Counter(2)];
  const types = new TypeModel().register(Counter, { members: [{ name: 'Id', type: t.int }, { name: 'n', type: t.int }] });
  const e = new RqlEngine({ types })
    .addSource('cs', () => cs)
    .addAdapter({
      target: Counter,
      members: (m) =>
        m.method('inc', [], t.int, (c) => {
          if (c.Id === 2) throw new Error('boom');
          return ++c.n;
        }),
    });
  e.mutations.enabled = true;
  const committed: string[] = [];
  e.mutations.committed = (records) => void committed.push(...records.map((r) => `${(r.item as Counter).Id}:${r.member}:${r.method?.name}`));
  const r = e.execute('cs | call inc()', {}, mut);
  assert.equal(r.diagnostics[0]?.code, 'call-threw');
  assert.deepEqual(committed, ['1:inc():inc']);
});

test('writes-through handles: no write-back, get-only owner, virtual setters, and they can be targets', () => {
  const { e, systems } = graphics();
  assert.equal(e.execute('systems | set main.duration = 5', {}, mut).ok, true);
  assert.equal(systems[0]!.state.duration, 5);
  assert.equal(e.execute('systems | get main | set duration = 7, loop = true', {}, mut).ok, true);
  assert.deepEqual(systems[0]!.state, { duration: 7, loop: true });
});

test('copy-on-read members write back through the setter; a get-only copy is read-only', () => {
  class Grad {
    private keys = [1, 2, 3];
    constructor(public Id: number) {}
    get colorKeys(): number[] {
      return [...this.keys];
    }
    set colorKeys(v: number[]) {
      this.keys = [...v];
    }
    get frozenKeys(): number[] {
      return [...this.keys];
    }
  }
  const g = new Grad(1);
  const types = new TypeModel().register(Grad, {
    members: [{ name: 'Id', type: t.int }, { name: 'colorKeys', type: t.list(t.number) }, { name: 'frozenKeys', type: t.list(t.number) }],
  });
  const e = new RqlEngine({ types }).addSource('gs', () => [g]).addAdapter({ target: Grad, members: (m) => m.copyOnRead('colorKeys', 'frozenKeys') });
  e.mutations.enabled = true;
  assert.equal(e.execute('gs | set colorKeys[1] = 9', {}, mut).ok, true);
  assert.deepEqual(g.colorKeys, [1, 9, 3]);
  assert.equal(e.execute('gs | set frozenKeys[0] = 0', {}, mut).diagnostics[0]?.code, 'readonly-member');
  assert.deepEqual(g.colorKeys, [1, 9, 3]);
});

test('hideWhere and engine-level type exclusion drop members (also through element and value types)', () => {
  class Native {
    ptr = 1;
  }
  class Obj {
    Id = 1;
    Name = 'o';
    _internal = 2;
    handle = new Native();
    handles = [new Native()];
    byName = new Map([['a', new Native()]]);
  }
  const types = new TypeModel()
    .register(Native, { members: ['ptr'] })
    .register(Obj, {
      members: [
        { name: 'Id', type: t.int },
        { name: 'Name', type: t.string },
        { name: '_internal', type: t.int },
        { name: 'handle', type: t.of(Native) },
        { name: 'handles', type: t.list(t.of(Native)) },
        { name: 'byName', type: t.map(t.string, t.of(Native)) },
      ],
    });
  const e = new RqlEngine({ types })
    .addSource('os', () => [new Obj()])
    .excludeTypes((r) => typeof r === 'object' && 'kind' in r && r.kind === 'class' && r.ctor === Native)
    .addAdapter({ target: Obj, members: (m) => m.hideWhere((mm) => mm.name.startsWith('_')) });
  const d = e.execute('os | describe').types?.[0] as { members: { name: string }[] };
  assert.deepEqual(d.members.map((m) => m.name), ['Id', 'Name']);
  assert.equal(e.execute('os | select handle').diagnostics[0]?.code, 'unknown-member');
});

test('destroyed objects read as null: skipped in streams and mutations, null inside collections, stale refs', () => {
  let getterRuns = 0;
  class Go {
    destroyed = false;
    private hp = 10;
    constructor(public Id: number) {}
    get Hp(): number {
      getterRuns++;
      return this.hp;
    }
    set Hp(v: number) {
      this.hp = v;
    }
  }
  const gos = [new Go(1), new Go(2)];
  const types = new TypeModel().register(Go, { members: [{ name: 'Id', type: t.int }, { name: 'Hp', type: t.int }] });
  const e = new RqlEngine({ types })
    .addSource('gos', () => gos)
    .addSource('world', () => ({ gos }))
    .addAdapter({ target: Go, isAlive: (g) => !g.destroyed });
  e.mutations.enabled = true;
  gos[1]!.destroyed = true;
  getterRuns = 0;
  assert.deepEqual(e.execute('gos | select Id Hp').rows, [[1, 10]]); // the destroyed item is skipped like a null one
  assert.deepEqual(e.execute('world | select dead=gos.any($ == null) n=gos.count() hp=gos.Hp second=gos[1] c=coalesce(gos[1], "gone")').rows, [
    [true, 2, [10], null, 'gone'],
  ]);
  assert.equal(getterRuns, 2); // never on the destroyed object
  assert.equal(e.execute('ref("Go#2") | count').diagnostics[0]?.code, 'stale-ref');
  const r = e.execute('gos | set Hp = 0', {}, mut);
  assert.deepEqual(r.rows?.map((row) => row[0]), ['Go#1']);
  gos[1]!.destroyed = false;
  assert.deepEqual(gos.map((g) => g.Hp), [0, 10]); // only the live one changed
});

test('the host isAlive hook is the fallback liveness source', () => {
  const xs = [{ Id: 1, gone: false }, { Id: 2, gone: true }];
  const e = new RqlEngine().addSource('xs', () => xs);
  e.isAlive = (o) => !(o as { gone?: boolean }).gone;
  assert.deepEqual(e.execute('xs | select Id').rows, [[1]]);
});

test('object literals: maps, construction with defaults, "$type" ambiguity, adapter coercion first', () => {
  class Shape {
    Id = 0;
    Name = '';
    sides = 0;
  }
  class Square extends Shape {
    size = 1;
  }
  class Holder {
    Id = 1;
    shape: Shape | null = null;
    weights = new Map<string, number>();
  }
  const h = new Holder();
  const types = new TypeModel()
    .register(Shape, { members: [{ name: 'Id', type: t.int }, { name: 'Name', type: t.string }, { name: 'sides', type: t.int }] })
    .register(Square, { members: [{ name: 'size', type: t.number }] })
    .register(Holder, { members: [{ name: 'Id', type: t.int }, { name: 'shape', type: t.nullable(t.of(Shape)) }, { name: 'weights', type: t.map(t.string, t.number) }] });
  const e = new RqlEngine({ types }).addSource('hs', () => [h]);
  e.mutations.enabled = true;
  assert.equal(e.execute('hs | set shape = {"$type": "Square", Name: "sq", size: 2.5}, weights = {a: 1, b: 2}', {}, mut).ok, true);
  assert.ok(h.shape instanceof Square);
  assert.deepEqual([h.shape.Name, h.shape.sides, (h.shape as Square).size], ['sq', 0, 2.5]);
  assert.deepEqual([...h.weights], [['a', 1], ['b', 2]]);
  for (const bad of ['{sides: 1.5}', '{"$type": "Circle"}', '{"$type": 3}', '{nope: 1}']) {
    assert.equal(e.execute(`hs | set shape = ${bad}`, {}, mut).diagnostics[0]?.code, 'coercion-failed', bad);
  }
  assert.equal(e.execute('hs | set weights = {a: "x"}', {}, mut).diagnostics[0]?.code, 'coercion-failed');
  // A misspelt "$type" is answered with the closest registered subtype names (spec §4.5, §10).
  const typo = e.execute('hs | set shape = {"$type": "Sqare"}', {}, mut).diagnostics[0];
  assert.equal(typo?.code, 'coercion-failed');
  assert.ok((typo?.message ?? '').includes("no registered type named 'Sqare' derives from Shape (did you mean 'Square'?)"), typo?.message);
  assert.ok(!(e.execute('hs | set shape = {"$type": "Zebra"}', {}, mut).diagnostics[0]?.message ?? '').includes('did you mean'));
  // An excluded class is never built from "$type" nor offered (spec §4.5, §13).
  types.excludeTypes((r) => typeof r === 'object' && 'kind' in r && r.kind === 'class' && r.ctor === Square);
  const hidden = e.execute('hs | set shape = {"$type": "Square"}', {}, mut).diagnostics[0];
  assert.equal(hidden?.code, 'coercion-failed');
  assert.ok(!(e.execute('hs | set shape = {"$type": "Sqare"}', {}, mut).diagnostics[0]?.message ?? '').includes('did you mean'));
});

test('host lists, sets and typed arrays convert element by element to a typed list', () => {
  class Lists {
    Id = 1;
    ints = [1, 2, 3];
    set = new Set([4, 5]);
    floats = new Float64Array([0.5, 1.5]);
    names: string[] = ['a'];
    map = new Map([['k', 1]]);
  }
  const l = new Lists();
  const types = new TypeModel().register(Lists, {
    members: [
      { name: 'Id', type: t.int },
      { name: 'ints', type: t.list(t.int) },
      { name: 'set', type: t.list(t.int) },
      { name: 'floats', type: t.list(t.number) },
      { name: 'names', type: t.list(t.string) },
      { name: 'map', type: t.map(t.string, t.int) },
    ],
  });
  const e = new RqlEngine({ types }).addSource('ls', () => [l]);
  e.mutations.enabled = true;
  const r = e.execute('ls | set ints = set', {}, mut);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.deepEqual(l.ints, [4, 5]);
  assert.equal(e.execute('ls | set ints = floats', {}, mut).diagnostics[0]?.code, 'coercion-failed'); // 0.5 is not an int
  assert.equal(e.execute('ls | set names = ints', {}, mut).diagnostics[0]?.code, 'coercion-failed');
  assert.equal(e.execute('ls | set ints = map', {}, mut).diagnostics[0]?.code, 'coercion-failed');
  assert.deepEqual(l.ints, [4, 5]);
  assert.deepEqual(l.names, ['a']);
});

test('list functions are pure and validate their arguments', () => {
  const tags = ['a', 'b'];
  const e = new RqlEngine().addSource('xs', () => [{ Id: 1, tags }]);
  assert.deepEqual(e.execute('xs | select append(tags, "c") insert(tags, -1, "z") remove-at(tags, 0) without(tags, "a")').rows, [
    [['a', 'b', 'c'], ['a', 'b', 'z'], ['b'], ['b']],
  ]);
  assert.deepEqual(tags, ['a', 'b']);
  assert.deepEqual(e.execute('xs | select insert(tags, 1.5, "q") insert(tags, 3, "q") remove-at(5, 0)').rows, [
    [{ $error: 'type-mismatch' }, { $error: 'type-mismatch' }, { $error: 'type-mismatch' }],
  ]);
});

test('virtual maps with has(): lookups do not scan keys; case-insensitive misses read keys only', () => {
  let keyScans = 0;
  let gets = 0;
  class Mat {
    Id = 1;
    props: Record<string, number> = { _Gloss: 0.5, _Metal: 1 };
  }
  const types = new TypeModel().register(Mat, { members: [{ name: 'Id', type: t.int }] });
  const e = new RqlEngine({ types })
    .addSource('mats', () => [new Mat()])
    .addAdapter({
      target: Mat,
      members: (m) =>
        m.map<number>('p', {
          keys: (o) => {
            keyScans++;
            return Object.keys(o.props);
          },
          has: (o, k) => k in o.props,
          get: (o, k) => {
            gets++;
            return o.props[k]!;
          },
        }, t.number),
    });
  assert.deepEqual(e.execute('mats | select p["_Gloss"]').rows, [[0.5]]);
  assert.equal(keyScans, 0);
  assert.deepEqual(e.execute('mats | select p["_gloss"]').rows, [[0.5]]); // case-insensitive fallback
  assert.equal(gets, 2); // one read per lookup: the fallback scans keys, not values
});

test('ref(): an exact id containing spaces wins over its trimmed display form, in one search', () => {
  const assets = [{ Id: 'Red', Name: 'short' }, { Id: 'Red (1)', Name: 'exact' }];
  const e = new RqlEngine().addSource('assets', () => assets);
  assert.deepEqual(e.execute('ref("Object#Red (1)") | select Name').rows, [['exact']]);
  assert.deepEqual(e.execute('ref("Object#Red (copy)") | select Name').rows, [['short']]);
});

test('hook owner: nearest enclosing object (virtual-map entry → material, handle member → system, map entry → owner)', () => {
  class Mat {
    Id = 'm';
    uniforms: Record<string, number> = { _Color: 0 };
  }
  class Mesh {
    Id = 1;
    material = new Mat();
    weights = new Map<string, number>([['a', 1]]);
  }
  const mesh = new Mesh();
  const { e: base, systems } = graphics();
  const types = new TypeModel()
    .register(Mat, { members: [{ name: 'Id', type: t.string }] })
    .register(Mesh, { members: [{ name: 'Id', type: t.int }, { name: 'material', type: t.of(Mat) }, { name: 'weights', type: t.map(t.string, t.number) }] });
  const e = new RqlEngine({ types })
    .addSource('meshes', () => [mesh])
    .addAdapter({
      target: Mat,
      members: (m) =>
        m.map<number>('Uniforms', { keys: (o) => Object.keys(o.uniforms), get: (o, k) => o.uniforms[k]!, set: (o, k, v) => void (o.uniforms[k] = v) }, t.number),
    });
  e.mutations.enabled = true;
  const owners: object[] = [];
  e.mutations.committed = (records) => void owners.push(...records.map((r) => r.owner));
  base.mutations.committed = (records) => void owners.push(...records.map((r) => r.owner));
  assert.equal(e.execute('meshes | set material.Uniforms["_Color"] = 1, weights["a"] = 2', {}, mut).ok, true);
  assert.equal(base.execute('systems | set main.duration = 3', {}, mut).ok, true);
  assert.deepEqual(owners, [mesh.material, mesh, systems[0]]);
});

test('virtual maps accept writes to existing entries only (no entry → invalid-value before the entry validator)', () => {
  let validated = 0;
  let written = 0;
  class Mat {
    Id = 'm';
    values: Record<string, number> = { a: 1 };
  }
  const mat = new Mat();
  const types = new TypeModel().register(Mat, { members: [{ name: 'Id', type: t.string }] });
  const e = new RqlEngine({ types })
    .addSource('mats', () => [mat])
    .addAdapter({
      target: Mat,
      members: (m) =>
        m.map<number>('v', {
          keys: (o) => Object.keys(o.values),
          get: (o, k) => o.values[k]!,
          set: (o, k, v) => {
            written++;
            o.values[k] = v;
          },
          validate: () => {
            validated++;
            return undefined;
          },
        }, t.number),
    });
  e.mutations.enabled = true;
  const r = e.execute('mats | set v["a"] = 2, v["b"] = 3', {}, mut);
  assert.equal(r.diagnostics[0]?.code, 'invalid-value');
  assert.ok(r.diagnostics[0]!.message.includes("invalid value 3 for 'v[\"b\"]'"), r.diagnostics[0]!.message);
  assert.ok(r.diagnostics[0]!.message.includes("no entry 'b'"));
  assert.deepEqual([validated, written, mat.values], [1, 0, { a: 1 }]);
});

test('construction from object literals respects the host member policy (permission-denied)', () => {
  class Stats {
    Armor = 1;
    Speed = 1;
  }
  class Hero {
    Id = 1;
    stats = new Stats();
  }
  const hero = new Hero();
  const types = new TypeModel()
    .register(Stats, { members: [{ name: 'Armor', type: t.int }, { name: 'Speed', type: t.int }] })
    .register(Hero, { members: [{ name: 'Id', type: t.int }, { name: 'stats', type: t.of(Stats) }] });
  const e = new RqlEngine({ types }).addSource('heroes', () => [hero]);
  e.mutations.enabled = true;
  e.mutations.canWrite = (type, member) => !(type.shortName === 'Stats' && member.name === 'Armor');
  assert.equal(e.execute('heroes | set stats.Armor = 9999', {}, mut).diagnostics[0]?.code, 'permission-denied');
  assert.equal(e.execute('heroes | set stats = {Armor: 9999}', {}, mut).diagnostics[0]?.code, 'permission-denied');
  assert.equal(e.execute('heroes | set stats = {Speed: 2}', {}, mut).ok, true);
  assert.deepEqual([hero.stats.Armor, hero.stats.Speed], [1, 2]);
});

test('set through a destroyed intermediate object fails like null and writes nothing', () => {
  class Target {
    hp = 5;
    destroyed = false;
  }
  class Turret {
    Id = 1;
    Target = new Target();
    shots = 0;
  }
  const turret = new Turret();
  const types = new TypeModel()
    .register(Target, { members: [{ name: 'hp', type: t.int }] })
    .register(Turret, { members: [{ name: 'Id', type: t.int }, { name: 'Target', type: t.nullable(t.of(Target)) }, { name: 'shots', type: t.int }] });
  const e = new RqlEngine({ types }).addSource('turrets', () => [turret]).addAdapter({ target: Target, isAlive: (x) => !x.destroyed });
  e.mutations.enabled = true;
  turret.Target.destroyed = true;
  const r = e.execute('turrets | set shots = 1, Target.hp = 0', {}, mut);
  assert.equal(r.diagnostics[0]?.code, 'coercion-failed');
  assert.ok(r.diagnostics[0]!.message.includes("'Target' is null"), r.diagnostics[0]!.message);
  assert.deepEqual([turret.shots, turret.Target.hp], [0, 5]);
});

test('ref(): an exact-id match is final, even when destroyed (no display-form fallback)', () => {
  const assets = [{ Id: 'Red', Name: 'short', gone: false }, { Id: 'Red (1)', Name: 'exact', gone: true }];
  const e = new RqlEngine().addSource('assets', () => assets);
  e.isAlive = (o) => !(o as { gone?: boolean }).gone;
  assert.equal(e.execute('ref("Object#Red (1)") | count').diagnostics[0]?.code, 'stale-ref');
  assert.deepEqual(e.execute('ref("Object#Red (2)") | select Name').rows, [['short']]);
});

test('hooks are skipped when a set or call has no records', () => {
  const { e } = graphics();
  let calls = 0;
  e.mutations.beforeWrite = () => void calls++;
  e.mutations.committed = () => void calls++;
  assert.equal(e.execute('renderers | where Id == 99 | set Name = "x"', {}, mut).ok, true);
  assert.equal(calls, 0);
});
