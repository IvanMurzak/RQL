import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlError, deserializePlan, formatNumber, serializePlan } from '@rql/contracts';
import { EnumType, RqlEngine, TypeModel, t } from '@rql/execution';
import { parse } from '@rql/parser';

const rows = (r: { rows?: unknown[][] }): unknown[][] => r.rows ?? [];

test('formatNumber matches .NET "R" formatting', () => {
  assert.equal(formatNumber(1), '1');
  assert.equal(formatNumber(2.5), '2.5');
  assert.equal(formatNumber(1e15), '1E+15');
  assert.equal(formatNumber(123456789012345), '123456789012345');
  assert.equal(formatNumber(0.0001), '0.0001');
  assert.equal(formatNumber(0.00001), '1E-05');
  assert.equal(formatNumber(-1.5e-7), '-1.5E-07');
  assert.equal(formatNumber(Infinity), '∞');
});

test('plan codec round-trips generic stages with sorted named options', () => {
  const p = parse('world | myapp.expand Items, Children mode="deep" a=1 | first 3');
  const json = serializePlan(p);
  assert.ok(json.indexOf('"a"') < json.indexOf('"mode"'));
  assert.equal(serializePlan(deserializePlan(json)), json);
});

test('parse errors carry code and span', () => {
  assert.throws(() => parse('objects | where (a'), (e: unknown) => e instanceof RqlError && e.code === 'syntax-error' && e.at !== undefined);
});

test('undeclared plain objects and class instances use the dynamic fallback', () => {
  class Enemy {
    id: number;
    name: string;
    hp: number;
    constructor(id: number, name: string, hp: number) {
      this.id = id;
      this.name = name;
      this.hp = hp;
    }
    get dead(): boolean {
      return this.hp <= 0;
    }
  }
  const world = { enemies: [new Enemy(1, 'goblin', 0), new Enemy(2, 'orc', 30)], settings: { volume: 3, music: true } };
  const e = new RqlEngine().addSource('world', () => world);
  const r = e.execute('world.enemies | where not dead | select $ref $type name hp');
  assert.deepEqual(rows(r), [['Enemy#2', 'Enemy', 'orc', 30]]);
  // Identity-less, nameless objects are encoded inline.
  assert.deepEqual(e.execute('world | select settings').rows, [[{ volume: 3, music: true }]]);
  assert.deepEqual(e.execute('ref("Enemy#1") | select name').rows, [['goblin']]);
});

test('declared enums: name encoding, case-insensitive string compare, numeric compare, set by name', () => {
  enum Mood {
    Calm,
    Angry = 5,
  }
  class Npc {
    Id = 1;
    Name = 'bob';
    Mood: Mood = Mood.Calm;
  }
  const npc = new Npc();
  const model = new TypeModel().register(Npc, { members: [{ name: 'Id', type: t.int }, 'Name', { name: 'Mood', type: new EnumType('Mood', Mood) }] });
  const e = new RqlEngine({ types: model }).addSource('npcs', () => [npc]);
  e.mutations.enabled = true;
  assert.deepEqual(rows(e.execute('npcs | where Mood == "calm" and Mood < 5 | select Mood')), [['Calm']]);
  const r = e.execute('npcs | set Mood = "ANGRY"', {}, { allowMutation: true });
  assert.deepEqual(rows(r), [['Npc#1', 'Mood', 'Calm', 'Angry']]);
  assert.equal(npc.Mood, Mood.Angry); // raw TS enum value stored back
  assert.equal(e.execute('npcs | set Id = 2', {}, { allowMutation: true }).diagnostics[0]?.code, 'readonly-member');
});

test('plain object declared as a map is a map', () => {
  class Unit {
    Name = 'u';
    Stats: Record<string, number> = { str: 3, dex: 4 };
  }
  const u = new Unit();
  const model = new TypeModel().register(Unit, { members: ['Name', { name: 'Stats', type: t.map(t.string, t.int) }] });
  const e = new RqlEngine({ types: model }).addSource('units', () => [u]);
  e.mutations.enabled = true;
  assert.deepEqual(rows(e.execute('units | select Stats.str Stats.Count len(Stats)')), [[3, 2, 2]]);
  e.execute('units | set Stats["luck"] = 7', {}, { allowMutation: true });
  assert.equal(u.Stats.luck, 7);
});

test('extension functions and stages', () => {
  const e = new RqlEngine()
    .addSource('nums', () => [1, 2, 3, 4])
    .addFunction('math.sq', (args) => (typeof args[0] === 'number' ? args[0] * args[0] : null))
    .addStage('my.double', function* (input) {
      for (const v of input) {
        yield v;
        yield v;
      }
    });
  assert.deepEqual(e.execute('nums | where math.sq($) > 4 | my.double | count').value, 4);
  assert.equal(e.execute('nums | other.stage').diagnostics[0]?.code, 'unknown-stage');
});

test('call: allowlisted methods only, coerced arguments, dry run', () => {
  class Hero {
    Id = 7;
    Name = 'h';
    Hp = 10;
    heal(amount: number): number {
      this.Hp += amount;
      return this.Hp;
    }
    destroy(): void {}
  }
  const h = new Hero();
  const model = new TypeModel().register(Hero, {
    members: [{ name: 'Id', type: t.int }, 'Name', { name: 'Hp', type: t.int }],
    methods: [{ name: 'heal', params: [{ name: 'amount', type: t.int }], returns: t.int }],
  });
  const e = new RqlEngine({ types: model }).addSource('heroes', () => [h]);
  e.mutations.enabled = true;
  assert.deepEqual(rows(e.execute('heroes | call Heal(5)', {}, { allowMutation: true })), [['Hero#7', 'heal(int amount)', 15]]);
  assert.equal(e.execute('heroes | call heal(1.5)', {}, { allowMutation: true }).diagnostics[0]?.code, 'coercion-failed');
  assert.equal(e.execute('heroes | call destroy', {}, { allowMutation: true }).diagnostics[0]?.code, 'permission-denied');
  assert.deepEqual(rows(e.execute('heroes | call heal(1)', {}, { allowMutation: true, dryRun: true })), [['Hero#7', 'heal(int amount)', '(dry-run)']]);
  assert.equal(h.Hp, 15);
});

test('liveness hook turns destroyed objects into stale refs', () => {
  const a = { Id: 1, Name: 'a', destroyed: false };
  const e = new RqlEngine().addSource('xs', () => [a]);
  e.isAlive = (o) => !(o as typeof a).destroyed;
  assert.equal(e.execute('ref("Object#1") | count').value, 1);
  a.destroyed = true;
  assert.equal(e.execute('ref("Object#1") | count').diagnostics[0]?.code, 'stale-ref');
});

test('first stops pulling upstream items (lazy)', () => {
  let reads = 0;
  const items = Array.from({ length: 1000 }, (_, i) => ({
    get v(): number {
      reads++;
      return i;
    },
  }));
  const e = new RqlEngine().addSource('xs', () => items);
  assert.equal(e.execute('xs | where v >= 0 | first 3 | count').value, 3);
  assert.equal(reads, 3);
});
