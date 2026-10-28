import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EnumType, EnumValue, RqlEngine, TypeModel, t, type TypeAdapter } from '@rql/execution';

// Wave-5 semantics: component-wise struct arithmetic (spec §4.2), absent map keys as null (§3), rounded struct keys for
// distinct / group-by (§5) and nested struct members in unknown-member hints (§10).

class Vec3 {
  constructor(
    public x = 0,
    public y = 0,
    public z = 0,
  ) {}
}
class Quat {
  constructor(
    public x = 0,
    public y = 0,
    public z = 0,
    public w = 1,
  ) {}
}
class Inner {
  Damping = 0;
}
class Body {
  CollisionProfileName = '';
  Settings = new Inner();
}
class Thing {
  Name = '';
  Scale = new Vec3(1, 1, 1);
  Rotation = new Quat();
  BodyInstance = new Body();
  Props = new Map<string, number>();
}

const vec3Adapter: TypeAdapter<Vec3> = {
  target: Vec3,
  encode: (v) => `(${v.x}, ${v.y}, ${v.z})`,
  components: (v) => [v.x, v.y, v.z],
  fromComponents: (c) => (c.length === 3 ? new Vec3(c[0], c[1], c[2]) : undefined),
};

function engine(things: Thing[], arithmetic = true): RqlEngine {
  const types = new TypeModel()
    .register(Vec3, { kind: 'struct', members: [{ name: 'x', type: t.number }, { name: 'y', type: t.number }, { name: 'z', type: t.number }] })
    .register(Quat, { kind: 'struct', members: ['x', 'y', 'z', 'w'].map((name) => ({ name, type: t.number })) })
    .register(Inner, { kind: 'struct', members: [{ name: 'Damping', type: t.number }] })
    .register(Body, { kind: 'struct', members: ['CollisionProfileName', { name: 'Settings', type: t.of(Inner) }] })
    .register(Thing, {
      members: ['Name', { name: 'Scale', type: t.of(Vec3) }, { name: 'Rotation', type: t.of(Quat) }, { name: 'BodyInstance', type: t.of(Body) }, { name: 'Props', type: t.map(t.string, t.number) }],
    });
  const e = new RqlEngine({ types }).addSource('things', () => things);
  if (arithmetic) e.addAdapter(vec3Adapter);
  e.mutations.enabled = true;
  return e;
}

function things(): Thing[] {
  const a = Object.assign(new Thing(), { Name: 'Coin_1' });
  a.Props.set('mp', 5);
  const b = Object.assign(new Thing(), { Name: 'Coin_2', Scale: new Vec3(0.1 * 3, 1, 1) });
  return [a, b];
}

const mutate = { allowMutation: true };

test('a vector member scales when its adapter declares components', () => {
  const list = things();
  const r = engine(list).execute('things | where Name starts-with "Coin_" | set Scale = (Scale * 1.5)', {}, mutate);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.deepEqual([list[0]!.Scale.x, list[0]!.Scale.z], [1.5, 1.5]);
  const s = engine(things()).execute('things | first 1 | select a = (Scale + [0, 1, 0]) b = (-Scale) c = (2 * Scale - Scale / 2) d = (Scale * Scale)');
  assert.deepEqual(s.rows![0], ['(1, 2, 1)', '(-1, -1, -1)', '(1.5, 1.5, 1.5)', '(1, 1, 1)']);
});

test('struct arithmetic mismatches are clear type-mismatch errors', () => {
  const e = engine(things());
  const q = e.execute('things | set Rotation = Rotation * 2', {}, mutate);
  assert.equal(q.ok, false);
  assert.ok(q.diagnostics[0]!.message.includes('Quat has no component-wise arithmetic'), q.diagnostics[0]!.message);
  const n = e.execute('things | set Scale = Scale + 1', {}, mutate);
  assert.equal(n.ok, false);
  assert.ok(n.diagnostics[0]!.message.includes('multiply or divide by a number'), n.diagnostics[0]!.message);
  const off = engine(things(), false).execute('things | set Scale = Scale * 2', {}, mutate);
  assert.equal(off.ok, false);
  assert.ok(off.diagnostics[0]!.message.includes('Vec3 has no component-wise arithmetic'), off.diagnostics[0]!.message);
});

test('distinct and group-by merge structs whose members print alike', () => {
  const e = engine(things());
  const r = e.execute('things | select s = (Scale * 10) | distinct');
  assert.equal(r.ok, true);
  assert.equal(r.rows!.length, 2); // (10, 10, 10) and (3, 10, 10)
  const list = things();
  list[1]!.Scale = new Vec3(0.1 * 3, 1, 1);
  list[0]!.Scale = new Vec3(0.3, 1, 1);
  const g = engine(list).execute('things | group-by Scale');
  assert.deepEqual(g.rows!.map((row) => row[1]), [2]);
});

test('an absent map key reads as null; has() tests the key', () => {
  const r = engine(things()).execute('things | select Name n = (Props["mp"] is null) h = has(Props["mp"])');
  assert.deepEqual(r.rows, [
    ['Coin_1', false, true],
    ['Coin_2', true, false],
  ]);
});

test('unknown-member hints search nested struct members', () => {
  const e = engine(things());
  const w = e.execute('things | set CollisionProfileName = "NoCollision"', {}, mutate);
  assert.equal(w.ok, false);
  assert.ok(w.diagnostics[0]!.hint!.includes('did you mean BodyInstance.CollisionProfileName'), w.diagnostics[0]!.hint);
  const deep = e.execute('things | where Damping > 0 | count');
  assert.ok(deep.diagnostics.some((d) => (d.hint ?? '').includes('BodyInstance.Settings.Damping')), JSON.stringify(deep.diagnostics));
});

test('the scope chain falls back past an absent key; an unconvertible key stays missing', () => {
  class Holder {
    Threshold = 3;
    Entries = [new Map<string, number>([['v', 5]])];
    ByKind = new Map<EnumValue, number>();
  }
  const Kind = new EnumType('Kind', ['A', 'B']);
  const h = new Holder();
  h.ByKind.set(Kind.values[0]!, 1);
  const types = new TypeModel().register(Holder, {
    members: [{ name: 'Threshold', type: t.number }, { name: 'Entries', type: t.list(t.map(t.string, t.number)) }, { name: 'ByKind', type: t.map(Kind, t.number) }],
  });
  const e = new RqlEngine({ types }).addSource('holders', () => [h]);
  assert.equal(e.execute('holders | where Entries.any(v > Threshold) | count').value, 1);
  const r = e.execute('holders | select a = (ByKind["B"] is null) c = (ByKind["Nope"] is null)');
  assert.deepEqual(r.rows, [[true, null]]);
});
