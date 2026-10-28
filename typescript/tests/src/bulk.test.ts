import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { test } from 'node:test';
import { deserializePlan, planToJson } from '@rql/contracts';
import { LANGUAGE_RULES, RqlEngine, TypeModel, t } from '@rql/execution';
import { parse } from '@rql/parser';
import { repoRoot } from './conformance.js';

// ADR-0008 bulk create: range(), list sources and source-qualified call; plus the encoder cycle guard, adapter members as
// known names and the shared language rules. Mirrors dotnet/Rql.Tests/BulkCreateTests.cs.

class Thing {
  Id = 0;
  Name = '';
  X = 0;
  Z = 0;
}

class Editor {
  created: Thing[] = [];
  calls = 0;
  create(name: string, x = 0, z = 0): Thing {
    this.calls++;
    if (name === 'boom') throw new Error('cannot create boom');
    const th = Object.assign(new Thing(), { Id: 100 + this.created.length, Name: name, X: x, Z: z });
    this.created.push(th);
    return th;
  }
  log(_text: string): void {
    this.calls++;
  }
}

function host(): { e: RqlEngine; editor: Editor; things: Thing[] } {
  const model = new TypeModel()
    .register(Thing, { members: [{ name: 'Id', type: t.int }, 'Name', { name: 'X', type: t.number }, { name: 'Z', type: t.number }] })
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
          returns: t.of(Thing),
          validate: (_ed, args) => ((args[0] as string).length === 0 ? 'name must not be empty' : undefined),
        },
        { name: 'log', params: [{ name: 'text', type: t.string }] },
      ],
    });
  const editor = new Editor();
  const things = [Object.assign(new Thing(), { Id: 1, Name: 'a' }), Object.assign(new Thing(), { Id: 2, Name: 'b' })];
  const e = new RqlEngine({ types: model }).addSource('editor', () => editor).addSource('things', () => things);
  e.mutations.enabled = true;
  e.mutations.maxTargets = 200;
  return { e, editor, things };
}

const M = { allowMutation: true };
const items = (r: { ok: boolean; items?: unknown[] }): string => {
  assert.equal(r.ok, true, JSON.stringify(r));
  return JSON.stringify(r.items);
};

test('range() streams numbers lazily, bounded by maxVisited', () => {
  const { e } = host();
  assert.equal(items(e.execute('range(0, 5)')), '[0,1,2,3,4]');
  assert.equal(items(e.execute('range(10, 4, -2)')), '[10,8,6]');
  assert.equal(items(e.execute('range(0, 1, 0.25)')), '[0,0.25,0.5,0.75]');
  assert.equal(items(e.execute('range(5, 0)')), '[]');
  const r = e.execute('range(0, 4) | select $index, $path, sq = ($ * $)');
  assert.deepEqual(r.rows![3], [3, 'range(…)[3]', 9]);
  assert.equal(e.execute('range(0, 1000000000) | first 3').items!.length, 3);
  assert.equal(e.execute('range(0, 1000000000) | count', { maxVisited: 1000 }).diagnostics[0]!.code, 'budget-exceeded');
  assert.ok(e.execute('range(0, 5, 0)').diagnostics[0]!.message.includes('step must not be 0'));
  assert.equal(e.execute('range(1)').diagnostics[0]!.code, 'arity');
  const f = e.execute('things | first 1 | select xs = range(1, 4), n = range(0, 10).count()');
  assert.deepEqual(f.rows![0], [[1, 2, 3], 10]);
});

test('a list literal is a source', () => {
  const { e } = host();
  assert.equal(items(e.execute('["a", 2, [3, 4]]')), '["a",2,[3,4]]');
  assert.deepEqual(e.execute('[["x", 1], ["y", 2]] | select n = $[0], v = $[1]').rows![1], ['y', 2]);
});

test('a source-qualified call runs once per item', () => {
  const { e, editor } = host();
  const r = e.execute('range(0, 30) | call editor.create("Cube" + $index, ($ % 15) * 2, floor($ / 15) * 2)', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(editor.created.length, 30);
  assert.equal(r.rows!.length, 30);
  assert.equal(r.rows![16]![0], 'Thing#116');
  const th = editor.created[16]!;
  assert.deepEqual([th.Name, th.X, th.Z], ['Cube16', 2, 2]);
  assert.equal(e.execute('[["A", 1], ["B", 2]] | call editor.create($[0], $[1])', {}, M).ok, true);
  assert.equal(e.execute('things | select n = Name | call editor.create(n + "!")', {}, M).ok, true);
  assert.equal(editor.created.at(-1)!.Name, 'b!');
  const log = e.execute('range(0, 2) | call editor.log("x" + $)', {}, M);
  assert.ok(String(log.rows![0]![0]).startsWith('Editor@'), JSON.stringify(log.rows));
});

test('a source-qualified call checks every call before the first, and hooks see all at once', () => {
  const { e, editor } = host();
  const bad = e.execute('["a", "", "c"] | call editor.create($)', {}, M);
  assert.equal(bad.diagnostics[0]!.code, 'invalid-value');
  assert.ok(bad.diagnostics[0]!.message.includes('for item 1'), bad.diagnostics[0]!.message);
  assert.equal(editor.calls, 0);
  const dry = e.execute('["a", "b"] | call editor.create($)', {}, { allowMutation: true, dryRun: true });
  assert.equal(dry.ok, true);
  assert.equal(dry.rows!.length, 2);
  assert.equal(editor.calls, 0);
  const batches: number[] = [];
  e.mutations.validateBatch = (b) => {
    batches.push(b.length);
    return undefined;
  };
  const before: number[] = [];
  e.mutations.beforeWrite = (b) => void before.push(b.length);
  assert.equal(e.execute('range(0, 12) | call editor.create("n" + $)', {}, M).ok, true);
  assert.deepEqual(batches, [12]);
  assert.deepEqual(before, [12]);
  const threw = e.execute('["a", "boom", "c"] | call editor.create($)', {}, M);
  assert.equal(threw.diagnostics[0]!.code, 'call-threw');
  assert.ok(threw.diagnostics[0]!.message.includes('for item 1'));
});

test('a source-qualified call counts invocations against the target limit', () => {
  const { e, editor } = host();
  const r = e.execute('range(0, 300) | call editor.create("n" + $)', {}, M);
  assert.equal(r.diagnostics[0]!.code, 'mutation-limit');
  assert.ok(r.diagnostics[0]!.message.includes("300 items reached 'call'"), r.diagnostics[0]!.message);
  assert.equal(editor.calls, 0);
  assert.equal(e.execute('range(0, 300) | call editor.create("n" + $)', { maxMutationTargets: 300 }, M).ok, true);
  const huge = e.execute('range(0, 1000000000) | call editor.create("n" + $)', {}, M);
  assert.ok(huge.diagnostics[0]!.message.includes('more than 1000 items reached'), huge.diagnostics[0]!.message);
});

test('source-qualified call targets and hints', () => {
  const { e } = host();
  const list = e.execute('range(0, 2) | call things.create($)', {}, M).diagnostics[0]!;
  assert.equal(list.code, 'invalid-plan');
  assert.ok(list.message.includes('is a list of 2'), list.message);
  const member = e.execute('things | call Transform.translate(1)', {}, M).diagnostics[0]!;
  assert.equal(member.code, 'unknown-source');
  assert.ok(member.hint!.includes('get Transform | call translate(…)'), member.hint);
  assert.equal(e.execute('range(0, 0) | call editor.craete($)', {}, M).diagnostics[0]!.code, 'unknown-function');
  assert.equal(e.execute('range(0, 2) | call editor.create($)').diagnostics[0]!.code, 'permission-denied');
  const numbers = e.execute('range(0, 3) | call create("x")', {}, M).diagnostics[0]!;
  assert.equal(numbers.code, 'invalid-plan');
  assert.ok(numbers.hint!.includes('… | call editor.create(… $ …)'), numbers.hint);
});

test('a source-qualified call round-trips through the plan codec', () => {
  const plan = parse('range(0, 3) | call editor.create("n" + $)');
  const json = JSON.stringify(planToJson(plan));
  assert.ok(json.includes('"on":{"path":["editor"]}'), json);
  assert.equal(JSON.stringify(planToJson(deserializePlan(json))), json);
  assert.ok(JSON.stringify(planToJson(parse('[1] | call ref("Editor#1").create($)'))).includes('"on":{"call":"ref"'));
  assert.throws(() => parse('[1] | call editor.create'), /needs parentheses/);
});

test('self-containing lists and maps encode', () => {
  const a: unknown[] = [1];
  a.push(a);
  const m = new Map<string, unknown>([['k', 1]]);
  m.set('self', m);
  const e = new RqlEngine().addSource('ls', () => [a, m]);
  assert.equal(items(e.execute('ls')), '[[1,"…"],{"k":1,"self":"…"}]');
});

test('adapter targets are known members', () => {
  class Widget {
    Name = '';
  }
  const model = new TypeModel().register(Widget, { members: ['Name'] });
  const objs: object[] = [];
  const e = new RqlEngine({ types: model }).addSource('objs', () => objs);
  e.mutations.enabled = true;
  assert.ok(e.execute('objs | where glow > 0 | set Name = "x"', {}, M).diagnostics.some((d) => d.code === 'unknown-member'));
  e.addAdapter({ target: Widget, members: (m) => m.add<number>('glow', { type: t.number, get: () => 1 }) });
  assert.ok(!e.execute('objs | where glow > 0 | set Name = "x"', {}, M).diagnostics.some((d) => d.code === 'unknown-member'));
});

test('the group-row hint names the key columns', () => {
  const { e } = host();
  const d = e.execute('things | group-by n=Name | select key').diagnostics.find((x) => x.code === 'unknown-member')!;
  assert.ok(d.hint!.includes('select n, count, avg = items.avg(…)'), d.hint);
  assert.ok(!d.hint!.includes('items.avg(key)'), d.hint);
  const z = e.execute('things | group-by Name | select key, Z').diagnostics.find((x) => x.code === 'unknown-member')!;
  assert.ok(z.hint!.includes('select key, count, avg = items.avg(Z)'), z.hint);
});

test('the language rules match the shared fixture', () => {
  const text = readFileSync(resolve(repoRoot, 'fixtures', 'guide', 'language-rules.txt'), 'utf8').replace(/\r\n/g, '\n');
  assert.equal(LANGUAGE_RULES, text);
});
