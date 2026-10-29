import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t, type TypeAdapter } from '@rql/execution';

// ADR-0012: numeric lists as vectors, vector functions, ref() sources and call targets that navigate.
// Mirrors dotnet/Rql.Tests/VectorTests.cs (host function sources are pending in TS).

class V3 {
  constructor(
    public X = 0,
    public Y = 0,
    public Z = 0,
  ) {}
}
class V2 {
  constructor(
    public X = 0,
    public Y = 0,
  ) {}
}
class Actor {
  Name = 'a';
  Position = new V3(3, 0, 4);
  Flat = new V2();
}

const v3: TypeAdapter<V3> = {
  target: V3,
  components: (v) => [v.X, v.Y, v.Z],
  fromComponents: (c) => (c.length === 3 ? new V3(c[0], c[1], c[2]) : undefined),
};
const v2: TypeAdapter<V2> = {
  target: V2,
  components: (v) => [v.X, v.Y],
  fromComponents: (c) => (c.length === 2 ? new V2(c[0], c[1]) : undefined),
};

function engine(): { e: RqlEngine; a: Actor } {
  const a = new Actor();
  const types = new TypeModel()
    .register(V3, { kind: 'struct', members: ['X', 'Y', 'Z'].map((name) => ({ name, type: t.number })) })
    .register(V2, { kind: 'struct', members: ['X', 'Y'].map((name) => ({ name, type: t.number })) })
    .register(Actor, { members: ['Name', { name: 'Position', type: t.of(V3) }, { name: 'Flat', type: t.of(V2) }] });
  const e = new RqlEngine({ types }).addSource('actors', () => [a]).addAdapter(v3).addAdapter(v2);
  e.mutations.enabled = true;
  return { e, a };
}

const mutate = { allowMutation: true };

function cell(e: RqlEngine, q: string): unknown {
  const r = e.execute(q);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  return r.rows![0]![0];
}

test('numeric lists scale and add', () => {
  const { e, a } = engine();
  assert.deepEqual(cell(e, 'actors | select v = ([1, 2, 0] * 3)'), [3, 6, 0]);
  assert.deepEqual(cell(e, 'actors | select v = ([1, 2] + [3, 4])'), [4, 6]);
  assert.equal(e.execute('actors | set Position = Position + [1, 2, 0] * 2', {}, mutate).ok, true);
  assert.deepEqual([a.Position.X, a.Position.Y], [5, 4]);
});

test('list arithmetic mismatches say why', () => {
  const { e } = engine();
  for (const [expr, why] of [
    ['[1, 2] + 1', 'add a list of 2 numbers'],
    ['[1, 2] * [3, 4]', 'use dot(a, b)'],
    ['[1, 2] + [1, 2, 3]', 'the lists have 2 and 3 numbers'],
    ['[1, "x"] * 2', 'every list element must be a number'],
    ['1 / [1, 2]', 'a number cannot be divided by a list'],
  ] as const) {
    const r = e.execute(`actors | set Name = (${expr})`, {}, mutate);
    assert.ok(r.diagnostics[0]!.message.includes(why), `${expr}: ${r.diagnostics[0]!.message}`);
  }
});

test('vector functions on component structs and lists', () => {
  const { e } = engine();
  assert.equal(cell(e, 'actors | select v = Position.length()'), 5);
  assert.equal(cell(e, 'actors | select v = magnitude(Position)'), 5);
  assert.deepEqual(cell(e, 'actors | select v = normalize(Position)'), { X: 0.6, Y: 0, Z: 0.8 });
  assert.equal(cell(e, 'actors | select v = distance(Position, [0, 0, 0])'), 5);
  assert.equal(cell(e, 'actors | select v = Position.dot([1, 0, 0])'), 3);
  assert.deepEqual(cell(e, 'actors | select v = lerp([0, 0, 0], Position, 0.5)'), [1.5, 0, 2]);
  const msg = (q: string): string => e.execute(q, {}, mutate).diagnostics[0]!.message;
  assert.ok(msg('actors | set Name = distance(Position, Flat)').includes('distance() needs vectors of one type'));
  assert.ok(msg('actors | set Name = dot(Position, [1, 2])').includes('dot() needs vectors of the same width, got 3 and 2 components'));
  assert.ok(msg('actors | set Name = length(Name)').includes('length() takes a vector: a list of numbers or a struct with component-wise arithmetic, got'));
  assert.equal(e.execute('actors | select v = Position.lerp([1, 1, 1])').diagnostics[0]!.code, 'arity');
});

class Holder {
  Id = 7;
  Zero: number[] = [0, 0];
  Copy: number[] | null = null;
}

function holderEngine(): { e: RqlEngine; h: Holder } {
  const h = new Holder();
  const types = new TypeModel().register(Holder, {
    members: ['Id', { name: 'Zero', type: t.list(t.number) }, { name: 'Copy', type: t.nullable(t.list(t.number)) }],
    id: 'Id',
  });
  const e = new RqlEngine({ types })
    .addSource('hs', () => [h])
    .addAdapter({
      target: Holder,
      members: (m) =>
        m.add('Broken', {
          type: t.string,
          get: () => {
            throw new Error('boom');
          },
        }),
    });
  e.mutations.enabled = true;
  return { e, h };
}

test('normalize of a zero vector is a new value', () => {
  const { e, h } = holderEngine();
  assert.equal(e.execute('hs | set Copy = normalize(Zero)', {}, mutate).ok, true);
  assert.deepEqual(h.Copy, [0, 0]);
  assert.ok(h.Copy !== h.Zero, 'normalize returned its argument');
});

test('a vector function on a sized value points to len', () => {
  const { e } = holderEngine();
  for (const q of ['length("abc")', 'length(["a"])', 'length({a: 1})']) {
    const r = e.execute(`hs | set Copy = ${q}`, {}, mutate);
    assert.ok(r.diagnostics[0]!.message.endsWith('; for the size of a string, list or map use len(…)'), r.diagnostics[0]!.message);
  }
  assert.ok(!e.execute('hs | set Copy = length(Id)', {}, mutate).diagnostics[0]!.message.includes('len('));
});

test('navigation after ref() fails on its own error and tracks paths', () => {
  const { e } = holderEngine();
  assert.equal(e.execute('ref("Holder#99").Zero | count').diagnostics[0]!.code, 'stale-ref');
  const broken = e.execute('ref("Holder#7").Broken | count');
  assert.equal(broken.ok, false);
  assert.ok(broken.diagnostics[0]!.message.includes("source 'ref(…).Broken' failed"), broken.diagnostics[0]!.message);
  const paths = e.execute('ref("Holder#7").Zero | select $path');
  assert.equal(paths.rows![1]![0], 'ref(…).Zero[1]');
});
