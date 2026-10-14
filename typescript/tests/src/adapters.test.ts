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

test('member adapters on an undeclared class are a host bug (internal-error); other capabilities still apply', () => {
  class Loose {
    a = 1;
  }
  const withMembers = new RqlEngine().addSource('xs', () => [new Loose()]).addAdapter({ target: Loose, members: (m) => m.hide('a') });
  assert.equal(withMembers.execute('xs | select a').diagnostics[0]?.code, 'internal-error');
  const encodeOnly = new RqlEngine().addSource('xs', () => [new Loose()]).addAdapter({ target: Loose, encode: () => 'loose' });
  assert.deepEqual(encodeOnly.execute('xs').items, ['loose']);
});

test('capabilities are gated: an encode-only adapter adds no equality/ordering lookups', () => {
  const e = new RqlEngine({ types: model() }).addAdapter({ target: Leaf, encode: () => 'x' });
  assert.equal(e.types.hasCapability('encode'), true);
  assert.equal(e.types.hasCapability('equals'), false);
  assert.equal(e.types.hasCapability('compare'), false);
  assert.equal(e.types.hasCapability('coerce'), false);
});

test('distinct on structs never reads explicit-only members', () => {
  let reads = 0;
  class P {
    x = 1;
    get heavy(): number {
      reads++;
      return 0;
    }
  }
  class Box {
    Id = 1;
    P = new P();
  }
  const types = new TypeModel()
    .register(P, { kind: 'struct', members: [{ name: 'x', type: t.number }, { name: 'heavy', type: t.number }] })
    .register(Box, { members: [{ name: 'Id', type: t.int }, { name: 'P', type: t.of(P) }] });
  const e = new RqlEngine({ types }).addSource('boxes', () => [new Box(), new Box()]).addAdapter({ target: P, members: (m) => m.explicitOnly('heavy') });
  assert.equal(e.execute('boxes | distinct P | count').value, 1);
  assert.equal(reads, 0);
});

test('a never-registered subclass inherits the read-only id (and the adapted table)', () => {
  class Sub extends Base {}
  const sub = new Sub();
  const e = new RqlEngine({ types: model() })
    .addSource('xs', () => [sub])
    .addAdapter({ target: Base, members: (m) => m.add('tag', { type: t.string, get: () => 'base' }) });
  e.mutations.enabled = true;
  const r = e.execute('xs | set Id = 99', {}, { allowMutation: true });
  assert.equal(r.diagnostics[0]?.code, 'readonly-member');
  assert.equal(sub.Id, 1);
  assert.deepEqual(e.execute('xs | select $ref $type tag').rows, [['Sub#1', 'Sub', 'base']]);
});

test('rollback restores the failing write when its setter changes state and then throws', () => {
  class Acc {
    Id = 1;
    Name = 'acc';
    balance = 10;
  }
  const accs = [new Acc(), Object.assign(new Acc(), { Id: 2 })];
  const types = new TypeModel().register(Acc, { members: [{ name: 'Id', type: t.int }, { name: 'Name', type: t.string }, { name: 'balance', type: t.int }] });
  const e = new RqlEngine({ types })
    .addSource('accs', () => accs)
    .addAdapter({
      target: Acc,
      members: (m) =>
        m.add<number>('cents', {
          type: t.int,
          get: (a) => a.balance * 100,
          set: (a, v) => {
            a.balance = v / 100; // state changes first ...
            if (a.Id === 2 && v !== 1000) throw new Error('ledger closed'); // ... then the setter throws (except when restoring)
          },
        }),
    });
  e.mutations.enabled = true;
  const r = e.execute('accs | set cents = 5000', {}, { allowMutation: true });
  assert.equal(r.diagnostics[0]?.code, 'coercion-failed');
  assert.deepEqual(accs.map((a) => a.balance), [10, 10]);
});

test('explicit-only members are never used as id or display', () => {
  let reads = 0;
  class Secret {
    get Id(): number {
      reads++;
      return 7;
    }
    get Name(): string {
      reads++;
      return 'hidden';
    }
    Label = 'x';
  }
  const types = new TypeModel().register(Secret, { members: [{ name: 'Id', type: t.int }, { name: 'Name', type: t.string }, 'Label'] });
  const e = new RqlEngine({ types }).addSource('s', () => [new Secret()]).addAdapter({ target: Secret, members: (m) => m.explicitOnly('Id', 'Name') });
  const r = e.execute('s | select $ref');
  assert.ok(/^Secret@\d+$/.test(String(r.rows?.[0]?.[0])), String(r.rows?.[0]?.[0]));
  assert.deepEqual(e.execute('s').items?.length, 1);
  assert.equal(e.execute('ref("Secret#7") | count').diagnostics[0]?.code, 'stale-ref');
  assert.equal(reads, 0);
  assert.deepEqual(e.execute('s | select Id').rows, [[7]]); // explicit access still works
});

test('hide / only with an unknown member name fail the query (internal-error)', () => {
  for (const cfg of [(m: { hide(...n: string[]): unknown }) => m.hide('Pasword'), (m: { only(...n: string[]): unknown }) => m.only('Name', 'Nmae')]) {
    const e = new RqlEngine({ types: model() }).addSource('xs', () => [new Leaf()]).addAdapter({ target: Leaf, members: (m) => void cfg(m as never) });
    const r = e.execute('xs | select Name');
    assert.equal(r.diagnostics[0]?.code, 'internal-error');
  }
});

test('a forked model sees types registered on the parent after addAdapter', () => {
  class Late {
    Id = 3;
    Name = 'late';
  }
  const shared = model();
  const e = new RqlEngine({ types: shared }).addSource('xs', () => [new Late()]).addAdapter({ target: Leaf, encode: () => 'leaf' });
  assert.deepEqual(e.execute('xs | select $type').rows, [['Late']]); // undeclared: dynamic fallback
  shared.register(Late, { name: 'LateType', members: [{ name: 'Id', type: t.int }, { name: 'Name', type: t.string }] });
  assert.equal(e.types.isRegistered(Late), true);
  assert.deepEqual(e.execute('xs | select $type $ref').rows, [['LateType', 'LateType#3']]);
  assert.deepEqual(e.execute('xs | first 1').items, [{ $ref: 'LateType#3', name: 'late' }]);
});

test('ordering adapters apply to min / max (enum-like class with a custom order)', () => {
  class Rarity {
    constructor(readonly name: string) {}
  }
  const ORDER = ['common', 'rare', 'epic'];
  const items = ['rare', 'common', 'epic', 'rare'].map((n) => ({ Id: n, r: new Rarity(n) }));
  const e = new RqlEngine()
    .addSource('items', () => items)
    .addAdapter({
      target: Rarity,
      encode: (v) => v.name,
      compare: (v, o) => (o instanceof Rarity ? ORDER.indexOf(v.name) - ORDER.indexOf(o.name) : undefined),
    });
  assert.equal(e.execute('items | max r').value, 'epic');
  assert.equal(e.execute('items | min r').value, 'common');
  assert.deepEqual(e.execute('items | sort r desc | first 1 | select r').rows, [['epic']]);
});
