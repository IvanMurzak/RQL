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
  assert.equal(formatNumber(Infinity), 'Infinity');
  assert.equal(formatNumber(-Infinity), '-Infinity');
  assert.equal(formatNumber(NaN), 'NaN');
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

test('unexpected host exceptions become internal-error diagnostics', () => {
  const e = new RqlEngine()
    .addSource('xs', () => [1, 2])
    .addStage('host.boom', () => {
      throw new TypeError('host bug');
    });
  const r = e.execute('xs | host.boom | count');
  assert.equal(r.ok, false);
  assert.equal(r.diagnostics[0]?.code, 'internal-error');
});

test('request maxMutationTargets is capped by the host ceiling', () => {
  const xs = Array.from({ length: 5 }, (_, i) => ({ Id: i, v: 0 }));
  const e = new RqlEngine().addSource('xs', () => xs);
  e.mutations.enabled = true;
  e.mutations.hardMaxTargets = 3;
  const r = e.execute('xs | set v = 1', { maxMutationTargets: 100 }, { allowMutation: true });
  assert.equal(r.diagnostics[0]?.code, 'mutation-limit');
  assert.ok(xs.every((x) => x.v === 0));
});

// ---------------------------------------------------------------- live-battery findings (0.2.0)

class Light {
  Label = '';
  Intensity = 0;
  Parent: Light | null = null;
}

class Body {
  Name = 'Crate';
  private mass = 1;
  /** Clamps like Unity's Rigidbody.mass. */
  get Mass(): number {
    return this.mass;
  }
  set Mass(v: number) {
    this.mass = v < 1e-7 ? 1e-7 : v;
  }
}

function battery(): { e: RqlEngine; bodies: Body[] } {
  const lamp = Object.assign(new Light(), { Label: 'Lamp', Intensity: 2 });
  const sun = Object.assign(new Light(), { Label: 'Sun', Intensity: 10 });
  lamp.Parent = sun;
  sun.Parent = lamp;
  const lights = [lamp, sun];
  const bodies = [new Body()];
  const model = new TypeModel()
    .register(Light, { members: ['Label', { name: 'Intensity', type: t.number }, { name: 'Parent', type: t.of(Light) }] })
    .register(Body, { members: ['Name', { name: 'Mass', type: t.number }] });
  const e = new RqlEngine({ types: model }).addSource('lights', () => lights).addSource('bodies', () => bodies);
  e.mutations.enabled = true;
  return { e, bodies };
}

const hintOf = (r: { diagnostics: readonly { code: string; hint?: string }[] }): string => r.diagnostics.find((d) => d.code === 'unknown-member')?.hint ?? '';

test('suggestions use optimal string alignment and a tight containment rule', () => {
  const { e } = battery();
  assert.equal(hintOf(e.execute('lights | where Lable == "Lamp"')), 'did you mean Label?');

  class Thing {
    Type = 1;
    Transform = 2;
  }
  const model = new TypeModel().register(Thing, { members: [{ name: 'Type', type: t.int }, { name: 'Transform', type: t.int }] });
  const te = new RqlEngine({ types: model }).addSource('things', () => [new Thing()]);
  // "NoSuchType" contains "Type", but "Type" is under half its length: no suggestion.
  const hint = hintOf(te.execute('things | where NoSuchType == 1'));
  assert.ok(!hint.startsWith('did you mean'), hint);
});

test('member listings in hints are capped at 12 names', () => {
  const names = Array.from({ length: 40 }, (_, i) => 'Member' + String(i));
  class Wide {}
  const model = new TypeModel().register(Wide, { members: names.map((name) => ({ name, type: t.int })) });
  const wide = Object.assign(new Wide(), Object.fromEntries(names.map((n, i) => [n, i])));
  const e = new RqlEngine({ types: model }).addSource('ws', () => [wide]);
  e.mutations.enabled = true;
  const hint = hintOf(e.execute('ws | where zzzz == 1'));
  assert.ok(hint.startsWith('members: Member0 Member1 '), hint);
  assert.ok(hint.endsWith('Member11 … (28 more)'), hint);
  const setHint = e.execute('ws | set zzzz = 1', {}, { allowMutation: true }).diagnostics[0]?.hint ?? '';
  assert.ok(setHint.endsWith('Member11 … (28 more)'), setHint);
});

test('math functions sqrt / pow / min / max', () => {
  const { e } = battery();
  const r = e.execute('lights | select d=(sqrt(Intensity)) p=(pow(Intensity, 2)) m=(max(Intensity, 5)) n=(min(Intensity, 5, 3))');
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.deepEqual(rows(r), [
    [Math.SQRT2, 4, 5, 2],
    [Math.sqrt(10), 100, 10, 3],
  ]);
  const odd = e.execute('lights | first | select a=(sqrt(-1)) b=(min(1, Nothing)) c=(max(1, Label)) d=(pow(2, null))');
  assert.deepEqual(rows(odd), [['NaN', null, { $error: 'type-mismatch' }, null]]);
  assert.equal(e.execute('lights | select x=(max(1))').diagnostics[0]?.code, 'arity');
  assert.equal(e.execute('lights | select x=(pow(1, 2, 3))').diagnostics[0]?.code, 'arity');
  // The stage keeps aggregating the stream.
  assert.equal(e.execute('lights | max Intensity').value, 10);
});

test('a dotted path after select names the projected column (no warning)', () => {
  const { e } = battery();
  const r = e.execute('lights | select Parent.Label Label | sort Parent.Label desc');
  assert.equal(r.ok, true);
  assert.deepEqual(r.diagnostics, []);
  assert.deepEqual(rows(r), [
    ['Sun', 'Lamp'],
    ['Lamp', 'Sun'],
  ]);
});

test('set receipts report the value read back; dry run keeps the planned value', () => {
  const { e, bodies } = battery();
  const seen: unknown[] = [];
  e.mutations.committed = (records) => void seen.push(...records.map((rec) => rec.new));
  const r = e.execute('bodies | set Mass = -1', {}, { allowMutation: true });
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.equal(bodies[0]!.Mass, 1e-7);
  assert.deepEqual(rows(r)[0]!.slice(1), ['Mass', 1, 1e-7]);
  assert.deepEqual(seen, [1e-7]); // the committed hook sees the read-back value too
  const dry = e.execute('bodies | set Mass = -5', {}, { allowMutation: true, dryRun: true });
  assert.equal(rows(dry)[0]![3], -5);
});

test('an invalid literal regex fails before running; a computed one warns once', () => {
  const { e } = battery();
  const r = e.execute('lights | where Label matches "([" | count');
  assert.equal(r.ok, false);
  assert.equal(r.diagnostics[0]?.code, 'invalid-regex');
  assert.ok(r.diagnostics[0]?.message.includes('(['));
  const computed = e.execute('lights | where Label matches (Label + "[") | count');
  assert.equal(computed.value, 0);
  assert.equal(computed.diagnostics.filter((d) => d.code === 'invalid-regex').length, 1);
});

test('a throwing extension function is an error value plus one function-threw warning', () => {
  const { e } = battery();
  e.addFunction('app.find', () => {
    throw new RangeError('no such object');
  });
  const r = e.execute('lights | select f=(app.find(Label))');
  assert.equal(r.ok, true);
  assert.deepEqual(rows(r), [[{ $error: 'function-threw' }], [{ $error: 'function-threw' }]]);
  const warnings = r.diagnostics.filter((d) => d.code === 'function-threw');
  assert.equal(warnings.length, 1);
  assert.equal(warnings[0]?.message, 'app.find() threw RangeError: no such object');
});

test('aggregates warn once about ignored values', () => {
  const { e } = battery();
  const r = e.execute('lights | sum Label');
  assert.equal(r.value, 0);
  assert.deepEqual(
    r.diagnostics.map((d) => [d.code, d.message]),
    [['type-mismatch', 'sum ignored 2 non-numeric value(s) (first: "Lamp")']],
  );
  assert.deepEqual(e.execute('lights | sum Intensity').diagnostics, []);
});

test('call validators run for every target before the first call, on dry runs too', () => {
  class Door {
    Opened = 0;
    constructor(
      public Id: number,
      public Locked: boolean,
    ) {}
  }
  const doors = [new Door(1, false), new Door(2, true)];
  const types = new TypeModel().register(Door, { members: [{ name: 'Id', type: t.int }, { name: 'Locked', type: t.bool }] });
  const e = new RqlEngine({ types })
    .addSource('doors', () => doors)
    .addAdapter({
      target: Door,
      members: (m) =>
        m.method(
          'open',
          [{ name: 'force', type: t.int }],
          undefined,
          (d, force: number) => void (d.Opened += force),
          'open the door',
          (d, force: number) => (d.Locked ? `door ${d.Id} is locked` : force > 10 ? 'too much force' : null),
        ),
    });
  e.mutations.enabled = true;
  const refused = e.execute('doors | call open(1)', {}, { allowMutation: true });
  assert.equal(refused.ok, false);
  assert.equal(refused.diagnostics[0]?.code, 'invalid-value');
  assert.equal(refused.diagnostics[0]?.message, 'cannot call open(int force) on Door#2: door 2 is locked; nothing was called');
  assert.deepEqual(doors.map((d) => d.Opened), [0, 0]); // not even the first door was opened
  const dry = e.execute('doors | where Id == 1 | call open(20)', {}, { allowMutation: true, dryRun: true });
  assert.equal(dry.diagnostics[0]?.code, 'invalid-value'); // a dry run reports the refusal
  assert.ok(dry.diagnostics[0]!.message.includes('too much force'));
  assert.equal(e.execute('doors | where Id == 1 | call open(2)', {}, { allowMutation: true }).ok, true);
  assert.deepEqual(doors.map((d) => d.Opened), [2, 0]);
});

test('receipts of enum list elements show old and new by name', () => {
  enum Mood {
    Calm,
    Angry = 5,
  }
  class Npc {
    Id = 1;
    Moods: Mood[] = [Mood.Calm, Mood.Angry];
  }
  const npc = new Npc();
  const MoodType = new EnumType('Mood', Mood);
  const model = new TypeModel().register(Npc, { members: [{ name: 'Id', type: t.int }, { name: 'Moods', type: t.list(MoodType) }] });
  const e = new RqlEngine({ types: model }).addSource('npcs', () => [npc]);
  e.mutations.enabled = true;
  assert.deepEqual(rows(e.execute('npcs | set Moods[1] = "Calm"', {}, { allowMutation: true })), [['Npc#1', 'Moods[1]', 'Angry', 'Calm']]);
  assert.deepEqual(npc.Moods, [Mood.Calm, Mood.Calm]); // raw values stored
  assert.deepEqual(rows(e.execute('npcs | set Moods[0] = "Angry"', {}, { allowMutation: true, dryRun: true })), [['Npc#1', 'Moods[0]', 'Calm', 'Angry']]);
});

test('distinct keys of rows and structs do not collide', () => {
  class Pair {
    constructor(
      public a: unknown,
      public b: unknown,
    ) {}
  }
  class Holder {
    constructor(
      public Id: number,
      public p: Pair,
      public f: unknown,
    ) {}
  }
  const model = new TypeModel()
    .register(Pair, { kind: 'struct', members: [{ name: 'a', type: t.any }, { name: 'b', type: t.any }] })
    .register(Holder, { members: [{ name: 'Id', type: t.int }, { name: 'p', type: t.of(Pair) }, { name: 'f', type: t.any }] });
  const o1 = { Id: 1 };
  const o2 = { Id: 2 };
  const hs = [
    new Holder(1, new Pair('a,s:b', 'c'), () => 1),
    new Holder(2, new Pair('a', 'b,s:c'), () => 1),
    new Holder(3, new Pair(o1, 'x'), () => 1),
    new Holder(4, new Pair(o2, 'x'), () => 1),
  ];
  const e = new RqlEngine({ types: model }).addSource('hs', () => hs);
  // Structs: separators inside strings and members differing only by object references stay distinct.
  assert.equal(e.execute('hs | get p | distinct | count').value, 4);
  // Rows: string columns quoted; functions with the same source text compare by identity.
  assert.equal(e.execute('hs | select p.a p.b | distinct | count').value, 4);
  assert.equal(e.execute('hs | select f | distinct | count').value, 4);
  hs[1]!.f = hs[0]!.f;
  assert.equal(e.execute('hs | select f | distinct | count').value, 3);
});

test('aggregates count error values as ignored (min / max never use them)', () => {
  const e = new RqlEngine().addSource('xs', () => [{ v: 1 }, { v: 2 }]);
  const sum = e.execute('xs | sum (v / 0)');
  assert.equal(sum.value, 0);
  assert.deepEqual(sum.diagnostics.map((d) => d.message), ['sum ignored 2 non-numeric value(s) (first: error(div-by-zero))']);
  const max = e.execute('xs | max (v / 0)');
  assert.equal(max.value, null);
  assert.equal(max.diagnostics[0]?.code, 'type-mismatch');
  // missing stays silent (the unknown name gets its own unknown-member warning, no type-mismatch)
  assert.ok(!e.execute('xs | sum Nothing').diagnostics.some((d) => d.code === 'type-mismatch'));
});

test('a dotted select alias makes its first segment a known name (no "not evaluated" warning)', () => {
  const world = { enemies: [{ name: 'orc', settings: { volume: 3 } }, { name: 'elf', settings: { volume: 1 } }] };
  const e = new RqlEngine().addSource('world', () => world);
  const r = e.execute('world.enemies | select settings.volume | sort settings.volume');
  assert.deepEqual(r.diagnostics, []);
  assert.deepEqual(rows(r), [[1], [3]]);
});

test('a mutation request must end with set or call', () => {
  const { e, bodies } = battery();
  const r = e.execute('bodies | select Name', {}, { allowMutation: true });
  assert.equal(r.ok, false);
  assert.equal(r.diagnostics[0]?.code, 'invalid-plan');
  assert.equal(e.execute('bodies', {}, { allowMutation: true }).diagnostics[0]?.code, 'invalid-plan');
  assert.equal(bodies[0]!.Mass, 1);
});
