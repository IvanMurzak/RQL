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

test('pi() takes no arguments', () => {
  // Values, null and type errors are in the shared fixtures (trig-functions, trig-null-and-type-mismatch).
  const { e } = host();
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

test('$count is missing with a warning outside set and call', () => {
  const { e } = host();
  const r = e.execute('g | select c = $count, i = $index');
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.equal(r.rows![0]![0], null);
  assert.ok(r.diagnostics.some((d) => d.code === 'unknown-meta' && d.message.includes('only defined in a set value or call argument')));
  // Inside a collection method of a set value $index is the element's, so $count is not defined there either.
  const { e: m, nodes } = host(2);
  const w = m.execute('nodes | set Value = Position.count($count > 0)', {}, mutate);
  assert.equal(w.ok, true, JSON.stringify(w.diagnostics));
  assert.deepEqual(nodes.map((n) => n.Value), [0, 0]); // the stage's 2 would count all 3
  assert.ok(!w.diagnostics.some((d) => d.code === 'unknown-meta'));
});
