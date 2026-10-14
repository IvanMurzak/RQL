import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t } from '@rql/execution';

class Base {
  Id = 1;
  Name = 'b';
}
class Mid extends Base {}
class Leaf extends Mid {
  Extra = 2;
}

const model = (): TypeModel =>
  new TypeModel()
    .register(Base, { members: [{ name: 'Id', type: t.int }, { name: 'Name', type: t.string }] })
    .register(Leaf, { members: [{ name: 'Extra', type: t.int }] });

test('adapter ranking: specificity, then priority, then registration order', () => {
  const e = new RqlEngine({ types: model() });
  e.addAdapter({ name: 'base-low', target: Base })
    .addAdapter({ name: 'iface', interfaceName: 'HasExtra', matches: (c) => c === Leaf })
    .addAdapter({ name: 'leaf', target: Leaf })
    .addAdapter({ name: 'base-high', target: Base, priority: 5 })
    .addAdapter({ name: 'mid', target: Mid })
    .addAdapter({ name: 'base-low-2', target: Base })
    .addAdapter({ name: 'object', target: Object });
  const order = e.explain(Leaf).map((s) => s.split(' ')[0]);
  assert.deepEqual(order, ['leaf', 'mid', 'base-high', 'base-low', 'base-low-2', 'object', 'iface']);
  assert.ok(/rank 0, priority 0/.test(e.explain(Leaf)[0]!));
  assert.deepEqual(e.explain(Base).map((s) => s.split(' ')[0]), ['base-high', 'base-low', 'base-low-2', 'object']);
});

test('member configuration composes from base to subclass; unknown member → internal-error', () => {
  const e = new RqlEngine({ types: model() })
    .addSource('xs', () => [new Leaf()])
    .addAdapter({ target: Base, members: (m) => m.add('label', { type: t.string, get: (b) => `#${b.Id}` }) })
    .addAdapter({ target: Leaf, members: (m) => m.add('label', { type: t.string, get: (l) => `leaf ${l.Extra}` }).hide('Name') });
  assert.deepEqual(e.execute('xs | select label').rows, [['leaf 2']]);
  assert.equal(e.execute('xs | select Name').diagnostics[0]?.code, 'unknown-member');
  const bad = new RqlEngine({ types: model() }).addSource('xs', () => [new Leaf()]).addAdapter({ target: Leaf, members: (m) => m.readOnly('Nope') });
  const r = bad.execute('xs | select Extra');
  assert.equal(r.ok, false);
  assert.equal(r.diagnostics[0]?.code, 'internal-error');
});

test('adapter stableId drives $ref and ref(); isAlive turns destroyed objects into stale-ref', () => {
  class Actor {
    guid = 'a-1';
    name = 'hero';
    destroyed = false;
  }
  const actor = new Actor();
  const e = new RqlEngine()
    .addSource('actors', () => [actor])
    .addAdapter({ target: Actor, stableId: (a) => a.guid, isAlive: (a) => !a.destroyed });
  assert.deepEqual(e.execute('actors | select $ref').rows, [['Actor#a-1']]);
  assert.deepEqual(e.execute('ref("Actor#a-1") | select name').rows, [['hero']]);
  actor.destroyed = true;
  assert.equal(e.execute('ref("Actor#a-1") | count').diagnostics[0]?.code, 'stale-ref');
});

test('explicit-only members are never read implicitly (generic recurse, inline encoding)', () => {
  let reads = 0;
  class Holder {
    Id = 1;
    get expensive(): object[] {
      reads++;
      return [];
    }
    plain = [{ Id: 2, Name: 'child' }];
  }
  const h = new Holder();
  const types = new TypeModel().register(Holder, { members: [{ name: 'Id', type: t.int }, { name: 'expensive', type: t.list('any') }, { name: 'plain', type: t.list('any') }] });
  const e = new RqlEngine({ types }).addSource('h', () => h).addAdapter({ target: Holder, members: (m) => m.explicitOnly('expensive') });
  assert.equal(e.execute('h | recurse | count').value, 2);
  assert.equal(reads, 0);
  assert.equal(e.execute('h | select expensive.count()').rows?.[0]?.[0], 0);
  assert.equal(reads, 1);
  const d = e.execute('h | describe').types?.[0] as { members: { name: string; explicitOnly?: boolean }[] };
  assert.equal(d.members.find((m) => m.name === 'expensive')?.explicitOnly, true);
});

test('addAdapter does not leak into other engines sharing the model', () => {
  const shared = model();
  const plain = new RqlEngine({ types: shared }).addSource('xs', () => [new Leaf()]);
  const adapted = new RqlEngine({ types: shared }).addSource('xs', () => [new Leaf()]).addAdapter({ target: Leaf, encode: () => 'LEAF' });
  assert.deepEqual(adapted.execute('xs').items, ['LEAF']);
  assert.deepEqual(plain.execute('xs').items, [{ $ref: 'Leaf#1', name: 'b' }]);
});

test('virtual setters on struct types are rejected at configuration', () => {
  class V {
    x = 0;
  }
  const types = new TypeModel().register(V, { kind: 'struct', members: [{ name: 'x', type: t.number }] });
  const e = new RqlEngine({ types })
    .addSource('vs', () => [new V()])
    .addAdapter({ target: V, members: (m) => m.add('double', { get: (v) => v.x * 2, set: () => {} }) });
  assert.equal(e.execute('vs | select x').diagnostics[0]?.code, 'internal-error');
});
