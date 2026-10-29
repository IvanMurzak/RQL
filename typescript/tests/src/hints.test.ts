import assert from 'node:assert/strict';
import { test } from 'node:test';
import { LANGUAGE_RULES, RqlEngine, TypeModel, languageRules, t } from '@rql/execution';

// ADR-0013: misspelt parameter names rank first, the host's creation call in the rules, receipts never suggest paging.
// Mirrors dotnet/Rql.Tests/HintTests.cs (AgentFormat and module reports are .NET only).

class NodeOptions {
  name = '';
  position: number[] = [];
}

class Godot {
  made: string[] = [];
}

class Thing {
  Id = 0;
  Name = '';
}

const M = { allowMutation: true };

function godot(): { e: RqlEngine; g: Godot } {
  const model = new TypeModel()
    .register(NodeOptions, { members: ['name', { name: 'position', type: t.list(t.number) }] })
    .register(Godot, {
      members: [],
      methods: [
        {
          name: 'CreateNode',
          params: [{ name: 'options', type: t.of(NodeOptions) }],
          returns: t.string,
          invoke: (g, args) => {
            (g as Godot).made.push((args[0] as NodeOptions).name);
            return (args[0] as NodeOptions).name;
          },
        },
        {
          name: 'CreateNode',
          params: [
            { name: 'className', type: t.string },
            { name: 'name', type: t.string, default: '' },
            { name: 'position', type: t.list(t.number), default: null },
          ],
          returns: t.string,
          invoke: (g, args) => {
            (g as Godot).made.push(args[0] as string);
            return args[0] as string;
          },
        },
      ],
    });
  const g = new Godot();
  const e = new RqlEngine({ types: model }).addSource('godot', () => g);
  e.mutations.enabled = true;
  return { e, g };
}

test('a misspelt parameter name beats the options overload', () => {
  const { e, g } = godot();
  const r = e.execute('[1] | call godot.CreateNode({klass: "MeshInstance3D"})', {}, M);
  assert.equal(r.diagnostics[0]!.code, 'coercion-failed');
  assert.ok(r.diagnostics[0]!.message.includes("no parameter 'klass' (did you mean 'className'?)"), r.diagnostics[0]!.message);
  const other = e.execute('[1] | call godot.CreateNode({zzzzzz: 1})', {}, M);
  assert.ok(!other.diagnostics[0]!.message.includes('did you mean'), other.diagnostics[0]!.message);
  assert.equal(g.made.length, 0);
});

test('the rules take the host creation call', () => {
  const text = languageRules('editor.CreateGameObject');
  assert.ok(!text.includes('editor.CreateObject'));
  assert.ok(!text.includes('stands for'));
  assert.ok(text.includes('call editor.CreateGameObject("Cube"'));
  assert.ok(text.includes('call editor.CreateGameObject($[0], $[1])'));
  assert.equal(languageRules(), LANGUAGE_RULES);
  assert.equal(languageRules(''), LANGUAGE_RULES);
  assert.equal(languageRules('  '), LANGUAGE_RULES);
  const spawn = {
    createCall: 'editor.SpawnActor',
    grid: 'editor.SpawnActor("StaticMeshActor", "Tile" + $, [($ % 15) * 200, floor($ / 15) * 200, 0])',
    perEntry: '[["A", [0, 0, 0]], ["B", [200, 0, 0]]] | call editor.SpawnActor("PointLight", $[0], $[1])',
  };
  const own = languageRules(spawn);
  assert.ok(own.includes(`range(0, 300) | call ${spawn.grid} builds a 15-wide grid`));
  assert.ok(own.includes(`${spawn.perEntry} one object per entry`));
  assert.ok(own.includes('call editor.SpawnActor({name: "Tile" + $'));
  assert.ok(!own.includes('editor.CreateObject') && !own.includes('stands for'));
  assert.equal(languageRules({}), LANGUAGE_RULES);
  assert.equal(languageRules({ grid: ' ' }), LANGUAGE_RULES);
  assert.ok(languageRules('  editor.CreateGameObject ').includes('call editor.CreateGameObject("Cube"'));
});

class Painter {}

test('exact names outrank misspelt ones and bound parameters are not suggested', () => {
  const ret = (v: unknown): unknown => v;
  const model = new TypeModel().register(Painter, {
    members: [],
    methods: [
      { name: 'Set', params: [{ name: 'color', type: t.number }], returns: t.string, invoke: () => 'int' },
      // Methods are keyed by name and arity: the second Set takes an optional parameter.
      {
        name: 'Set',
        params: [
          { name: 'colour', type: t.string },
          { name: 'mix', type: t.number, default: 1 },
        ],
        returns: t.string,
        invoke: () => 'string',
      },
      { name: 'Make', params: [{ name: 'className', type: t.string }], returns: t.string, invoke: (_p, a) => ret(a[0]) },
      {
        name: 'Make',
        params: [
          { name: 'className', type: t.string },
          { name: 'klass', type: t.number },
        ],
        returns: t.string,
        invoke: (_p, a) => ret(a[0]),
      },
      { name: 'Open', params: [{ name: 'URLPath', type: t.string }], returns: t.string, invoke: (_p, a) => ret(a[0]) },
    ],
  });
  const p = new Painter();
  const e = new RqlEngine({ types: model }).addSource('p', () => p);
  e.mutations.enabled = true;
  const r = e.execute('[1] | call p.Set({color: "red"})', {}, M);
  assert.ok(r.diagnostics[0]!.message.includes("parameter 'color'"), r.diagnostics[0]!.message);
  assert.ok(!r.diagnostics[0]!.message.includes("did you mean 'colour'"), r.diagnostics[0]!.message);
  const m = e.execute('[1] | call p.Make({className: "X", klass: "Y"})', {}, M);
  assert.ok(!m.diagnostics[0]!.message.includes("did you mean 'className'"), m.diagnostics[0]!.message);
  assert.ok(m.diagnostics[0]!.message.includes("parameter 'klass'"), m.diagnostics[0]!.message);
  const o = e.execute('[1] | call p.Open({pth: "/x"})', {}, M);
  assert.ok(o.diagnostics[0]!.message.includes("did you mean 'URLPath'"), o.diagnostics[0]!.message);
});

test('shortened receipts never suggest paging', () => {
  const things: Thing[] = [];
  for (let i = 1; i <= 200; i++) things.push(Object.assign(new Thing(), { Id: i, Name: `t${i}` }));
  const model = new TypeModel().register(Thing, { members: [{ name: 'Id', type: t.int }, 'Name'] });
  const e = new RqlEngine({ types: model }).addSource('things', () => things);
  e.mutations.enabled = true;
  e.mutations.maxTargets = 500;
  const r = e.execute('things | set Name = Name + "-a-fairly-long-suffix-to-fill-the-receipt"', { maxOutputBytes: 2000 }, M);
  assert.equal(r.ok, true, JSON.stringify(r.diagnostics));
  assert.equal(r.truncated, true);
  const w = r.diagnostics.find((d) => d.code === 'output-truncated')!;
  assert.ok(w.hint!.includes('the receipt is shortened, not the change: all 200 changes were made'), w.hint);
  assert.ok(!w.hint!.includes('page'));
  assert.equal(things.filter((x) => x.Name.endsWith('suffix-to-fill-the-receipt')).length, 200);
  const dry = e.execute('things | set Name = Name + "-b-fairly-long-suffix-to-fill-the-receipt"', { maxOutputBytes: 2000 }, { allowMutation: true, dryRun: true });
  assert.ok(dry.diagnostics.find((d) => d.code === 'output-truncated')!.hint!.includes('planned changes were checked (dry run)'));
});
