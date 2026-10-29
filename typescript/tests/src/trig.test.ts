import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t } from '@rql/execution';

// Trigonometric functions, pi(), deg() / rad() and the $count meta member (spec §3, §4.3, §8).
// Mirrors dotnet/Rql.Tests/TrigAndCountTests.cs.

class Node {
  Name = '';
  Position: number[] = [0, 0, 0];
  Value = 0;
}

class Editor {
  created: [string, number, number][] = [];
  create(name: string, x = 0, z = 0): string {
    this.created.push([name, x, z]);
    return name;
  }
}

const mutate = { allowMutation: true };

function host(n = 4): { e: RqlEngine; nodes: Node[]; editor: Editor } {
  const types = new TypeModel()
    .register(Node, { members: ['Name', { name: 'Position', type: t.list(t.number) }, { name: 'Value', type: t.number }] })
    .register(Editor, {
      members: [],
      methods: [
        {
          name: 'create',
          params: [
            { name: 'name', type: t.string },
            { name: 'x', type: t.number, default: 0 },
            { name: 'z', type: t.number, default: 0 },
          ],
          returns: t.string,
        },
      ],
    });
  const nodes = Array.from({ length: n }, (_, i) => Object.assign(new Node(), { Name: `n${i}` }));
  const editor = new Editor();
  const e = new RqlEngine({ types }).addSource('nodes', () => nodes).addSource('editor', () => editor).addSource('g', () => [1]);
  e.mutations.enabled = true;
  return { e, nodes, editor };
}

function row(e: RqlEngine, q: string): unknown[] {
  const r = e.execute(q);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  return r.rows![0]!;
}

test('trig functions work in radians and round to 12 places', () => {
  const { e } = host();
  assert.deepEqual(
    row(e, 'g | select s = sin(pi() / 2), c = cos(pi()), z = sin(pi()), t = tan(pi() / 4), a = asin(1), b = acos(0), n = atan(1), q = atan2(1, -1), p = pi(), d = deg(pi()), r = rad(180)'),
    [1, -1, 0, 1, 1.570796326795, 1.570796326795, 0.785398163397, 2.356194490192, Math.PI, 180, Math.PI],
  );
});

test('trig functions pass null through and reject other values', () => {
  const { e } = host();
  assert.deepEqual(row(e, 'g | select a = sin(null), b = atan2(1, null), c = deg(null)'), [null, null, null]);
  assert.ok(JSON.stringify(e.execute('g | select a = cos("x"), b = atan2("y", 1)')).includes('type-mismatch'));
  assert.equal(e.execute('g | select pi(1)').ok, false);
});

test('$count is the stage input in set values', () => {
  const { e, nodes } = host();
  const r = e.execute('nodes | set Position = [2 * cos(2 * pi() * $index / $count), 0, 2 * sin(2 * pi() * $index / $count)], Value = $count', {}, mutate);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.deepEqual(nodes.map((n) => n.Value), [4, 4, 4, 4]);
  assert.deepEqual(nodes.map((n) => n.Position), [[2, 0, 0], [0, 0, 2], [-2, 0, 0], [0, 0, -2]]);
  e.execute('nodes | where Name != "n0" | set Value = $count', {}, mutate);
  assert.deepEqual(nodes.map((n) => n.Value), [4, 3, 3, 3]);
});

test('$count is the item count in source-qualified calls', () => {
  const { e, editor } = host();
  const r = e.execute('range(0, 8) | call editor.create("P" + $index + "of" + $count, round(cos(2 * pi() * $ / $count), 3), round(sin(2 * pi() * $ / $count), 3))', {}, mutate);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.equal(editor.created.length, 8);
  assert.deepEqual(editor.created[2], ['P2of8', 0, 1]);
  assert.deepEqual(editor.created[5], ['P5of8', -0.707, -0.707]);
});

test('$count is missing outside set and call', () => {
  const { e } = host();
  const r = e.execute('g | select c = $count, i = $index');
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.equal(r.rows![0]![0], null);
  assert.ok(!r.diagnostics.some((d) => d.code === 'unknown-meta'));
});
