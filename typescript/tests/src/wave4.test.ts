import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlError } from '@rql/contracts';
import { RqlEngine, TypeModel, t } from '@rql/execution';
import { parse } from '@rql/parser';

// Wave-4 findings: conditions, target-limit range, host names in hints, mixed streams, parser hints.

class Item {
  Name = '';
  Weight = 0;
  Other: Item | null = null;
  Tags: string[] = [];
}
class Light extends Item {
  Energy = 1;
}

function model(): TypeModel {
  return new TypeModel()
    .register(Item, { members: ['Name', { name: 'Weight', type: t.number }, { name: 'Other', type: t.nullable(t.of(Item)) }, { name: 'Tags', type: t.list(t.string) }] })
    .register(Light, { members: [{ name: 'Energy', type: t.number }] });
}

function engine(items: object[]): RqlEngine {
  const e = new RqlEngine({ types: model() }).addSource('items', () => items);
  e.mutations.enabled = true;
  return e;
}

const one = (): Item => Object.assign(new Item(), { Name: 'a', Weight: 4.2, Other: new Item(), Tags: ['t'] });

test('a non-boolean condition fails with a boolean test to write instead', () => {
  const cases: [string, string, string][] = [
    ['items | where Other | count', "'Other' is a Item", 'Other != null, or has(Other)'],
    ['items | where Name | count', 'the string', 'Name != ""'],
    ['items | where Weight | count', 'the number 4.2', 'Weight != 0'],
    ['items | where Tags | count', 'a list', 'len(Tags) > 0'],
    ['items | select c = (Other ? 1 : 0)', "'Other'", 'Other != null'],
    ['items | where Weight > 1 and Other | count', "'Other'", 'has(Other)'],
    ['items | where Tags.any($) | count', 'the string', '!= ""'],
  ];
  for (const [q, message, hint] of cases) {
    const r = engine([one()]).execute(q);
    assert.equal(r.ok, false, q);
    const d = r.diagnostics[0]!;
    assert.equal(d.code, 'type-mismatch', q);
    assert.ok(d.message.includes(message), `${q}: ${d.message}`);
    assert.ok(d.hint!.startsWith('a condition must be a boolean, e.g. '), d.hint);
    assert.ok(d.hint!.includes(hint), `${q}: ${d.hint}`);
  }
  // null is false, not an error
  assert.equal(engine([new Item(), new Item()]).execute('items | where Other | count').value, 0);
});

test('a request target limit outside 1 … the ceiling is refused', () => {
  for (const n of [0, 1001, -5, 2.5]) {
    const items = [one()];
    const e = engine(items);
    e.mutations.limitArgument = 'max_targets';
    const r = e.execute('items | set Name = "b"', { maxMutationTargets: n }, { allowMutation: true });
    assert.equal(r.ok, false);
    assert.equal(r.diagnostics[0]?.code, 'invalid-value');
    assert.ok(r.diagnostics[0]!.message.includes(`max_targets must be from 1 to 1000 (the host ceiling), got ${n}`), r.diagnostics[0]!.message);
    assert.equal(items[0]!.Name, 'a');
  }
});

test('permission-denied names the host mutation tool', () => {
  const e = engine([one()]);
  let d = e.execute('items | set Name = "x"').diagnostics[0]!;
  assert.equal(d.code, 'permission-denied');
  assert.ok(d.message.includes('run it with the mutation tool'));
  e.mutateHint = 'rql-mutate';
  d = e.execute('items | set Name = "x"').diagnostics[0]!;
  assert.ok(d.message.includes('run it with rql-mutate'));
  assert.ok(!(d.message + (d.hint ?? '')).includes('allowMutation'));
});

test('set on a mixed stream names the types that have the member', () => {
  const e = engine([one(), Object.assign(new Light(), { Name: 'l' })]);
  const d = e.execute('items | set Energy = 2', {}, { allowMutation: true }).diagnostics[0]!;
  assert.equal(d.code, 'unknown-member');
  assert.ok(d.message.includes('1 of 2 targets'), d.message);
  assert.ok(d.hint!.includes("only Light has 'Energy'"), d.hint);
  assert.ok(d.hint!.includes('where $ is Light'), d.hint);
});

test('unknown members are listed by similarity, and known types that have one are named', () => {
  const e = engine([one()]);
  const d = e.execute('items | where Wieght_kg > 1').diagnostics.find((x) => x.code === 'unknown-member')!;
  assert.ok(d.hint!.startsWith('members: Weight'), d.hint);
  const e2 = new RqlEngine({ types: model() }).addSource('items', () => [one()]).addSource('lights', () => [new Light()]);
  const d2 = e2.execute('items | where $ is Item and not ($ is Light) | where Energy > 1').diagnostics.find((x) => x.code === 'unknown-member')!;
  assert.ok(d2.hint!.includes("'Energy' is a member of Light"), d2.hint);
});

test('known types stay known after their source turns null', () => {
  class Lamp {
    Glow = 1;
  }
  let sel: object | null = null;
  const e = new RqlEngine({ types: model().register(Lamp, { members: [{ name: 'Glow', type: t.number }] }) })
    .addSource('items', () => [one()])
    .addSource('sel', () => sel);
  const hint = (): string => e.execute('items | where Glow > 0').diagnostics.find((x) => x.code === 'unknown-member')!.hint ?? '';
  assert.ok(!hint().includes('Lamp'), hint());
  sel = new Lamp();
  assert.equal(e.execute('sel | count').value, 1);
  sel = null;
  assert.ok(hint().includes("'Glow' is a member of Lamp"), hint());
});

test('a nested set path does not suggest filtering the stream', () => {
  const e = new RqlEngine({ types: model() }).addSource('items', () => [one()]).addSource('lights', () => [new Light()]);
  e.mutations.enabled = true;
  const d = e.execute('items | set Other.Energy = 2', {}, { allowMutation: true }).diagnostics[0]!;
  assert.equal(d.code, 'unknown-member');
  assert.ok(!(d.hint ?? '').includes('where $ is'), d.hint);
});

test('self-containing lists do not overflow', () => {
  const a: unknown[] = [1];
  a.push(a);
  const b: unknown[] = [1];
  b.push(b);
  const e = new RqlEngine().addSource('ls', () => [a, b]);
  assert.equal(e.execute('ls | where $ == $ | count').ok, true);
  assert.equal(e.execute('ls | group-by $ | count').ok, true);
});

test('group rows aggregate their items; the engine lists its adapters', () => {
  const items = [Object.assign(new Item(), { Name: 'a', Weight: 1 }), Object.assign(new Item(), { Name: 'a', Weight: 4.2 }), Object.assign(new Item(), { Name: 'b', Weight: 2 })];
  const e = engine(items);
  const r = e.execute('items | group-by Name | select key, count, avg = items.avg(Weight)');
  assert.equal(r.ok, true);
  assert.equal(r.rows![0]![2], 2.6);
  const miss = e.execute('items | group-by Name | select key, avg = Weight').diagnostics.find((d) => d.code === 'unknown-member')!;
  assert.ok(miss.hint!.includes('items.avg(Weight)'), miss.hint);
  assert.equal(e.adapters.length, 0);
  const a = { name: 'ItemNames', target: Item };
  e.addAdapter(a);
  assert.equal(e.adapters[0], a);
});

test('a second query on a new line hints one query per call', () => {
  assert.throws(
    () => parse('items | where Name == "a" | set Name = "b"\nitems | where Name == "c" | count'),
    (e: unknown) => e instanceof RqlError && e.code === 'syntax-error' && (e.hint ?? '').includes('one query per call: line 2'),
  );
  for (const q of [
    'items\n| where Name == "a" foo',
    'items |\n  where x | y\nfoo',
    'items | where x\n  and y z',
    'items | where x > 1\ndistinct | count',
    'items | select Name,\n  Tags | sort Name +',
    'items | where Name == "a" or\n  Name | first 2 )',
  ]) {
    assert.throws(() => parse(q), (e: unknown) => e instanceof RqlError && !(e.hint ?? '').includes('one query per call'));
  }
});

test('chained comparisons explain the fix', () => {
  const cases: [string, string][] = [
    ['items | where path contains "addons" == false', 'not (path contains "addons")'],
    ['items | where a == b == c', 'not (a == b)'],
    ['items | where Name starts-with "a" contains "b"', 'comparisons do not chain'],
  ];
  for (const [q, hint] of cases) {
    assert.throws(
      () => parse(q),
      (e: unknown) => e instanceof RqlError && e.code === 'syntax-error' && e.message.includes('comparisons do not chain') && (e.message + ' ' + (e.hint ?? '')).includes(hint),
    );
  }
});
