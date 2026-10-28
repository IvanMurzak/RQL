import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t } from '@rql/execution';

// ADR-0010: overload resolution without side effects, engine objects never built from literals, the best-matching
// overload's failure and arguments by name from one object literal. Mirrors dotnet/Rql.Tests/OverloadTests.cs.

let engineObjectsBuilt = 0;
let placementsBuilt = 0;

/** An engine object: its constructor registers it in the scene (Unity's `new GameObject()`). */
class EngineObject {
  Id = 0;
  Name = '';
  constructor() {
    engineObjectsBuilt++;
  }
}

/** Plain data whose constructions are counted too. */
class Placement {
  position: number[] = [];
  constructor() {
    placementsBuilt++;
  }
}

class Editor {
  scene: EngineObject[] = [];
  add(name: string): EngineObject {
    const o = Object.assign(new EngineObject(), { Id: 100 + this.scene.length, Name: name });
    this.scene.push(o);
    return o;
  }
}

function host(engineObjects = true): { e: RqlEngine; editor: Editor } {
  const model = new TypeModel()
    .register(EngineObject, { members: [{ name: 'Id', type: t.int }, 'Name'] })
    .register(Placement, { members: [{ name: 'position', type: t.list(t.number) }] })
    .register(Editor, {
      members: [],
      methods: [
        {
          name: 'CreateGameObject',
          params: [
            { name: 'name', type: t.string },
            { name: 'parent', type: t.of(EngineObject) },
            { name: 'primitive', type: t.string },
          ],
          returns: t.of(EngineObject),
          invoke: (ed, args) => (ed as Editor).add(args[0] as string),
        },
        {
          name: 'CreateGameObject',
          params: [
            { name: 'name', type: t.string },
            { name: 'placement', type: t.of(Placement) },
            { name: 'primitive', type: t.string },
            { name: 'tag', type: t.string, default: '' },
          ],
          returns: t.of(EngineObject),
          invoke: (ed, args) => (ed as Editor).add(args[0] as string),
        },
        {
          name: 'Spawn',
          params: [
            { name: 'name', type: t.string },
            { name: 'primitive', type: t.string, default: 'Cube' },
            { name: 'size', type: t.number, default: 1 },
          ],
          returns: t.of(EngineObject),
          invoke: (ed, args) => (ed as Editor).add(`${args[0] as string}:${args[1] as string}:${args[2] as number}`),
          validate: (_ed, args) => ((args[2] as number) <= 0 ? 'size must be positive' : undefined),
        },
        { name: 'Link', params: [{ name: 'other', type: t.of(EngineObject) }], returns: t.string, invoke: (_ed, args) => (args[0] as EngineObject).Name },
      ],
    });
  const editor = new Editor();
  const e = new RqlEngine({ types: model }).addSource('editor', () => editor);
  if (engineObjects) e.addAdapter({ target: EngineObject, isAlive: () => true });
  e.mutations.enabled = true;
  e.mutations.maxTargets = 500;
  return { e, editor };
}

const M = { allowMutation: true };
const D = { allowMutation: true, dryRun: true };
const error = (r: { ok: boolean; diagnostics: readonly { code: string; message: string }[] }): string => {
  assert.equal(r.ok, false, JSON.stringify(r));
  return `${r.diagnostics[0]!.code}: ${r.diagnostics[0]!.message}`;
};

test('overload resolution constructs nothing for the overloads not chosen', () => {
  const { e, editor } = host();
  engineObjectsBuilt = 0;
  placementsBuilt = 0;
  const r = e.execute('[1] | call editor.CreateGameObject("X", {position: [1, 2, 3]}, "Cube")', {}, D);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(r.rows![0]![1], 'CreateGameObject(string name, Placement placement, string primitive, string tag)');
  assert.equal(engineObjectsBuilt, 0);
  assert.equal(placementsBuilt, 1);
  assert.equal(editor.scene.length, 0);
});

test('an empty literal never builds an engine object, even for the only overload', () => {
  const { e, editor } = host();
  engineObjectsBuilt = 0;
  for (const options of [D, M]) {
    const err = error(e.execute('[1] | call editor.Link({})', {}, options));
    assert.ok(err.startsWith('coercion-failed: ') && err.includes('EngineObject is an engine object and cannot be built from an object literal'), err);
  }
  assert.equal(engineObjectsBuilt, 0);
  assert.equal(editor.scene.length, 0);
});

test('the host decides which classes literals may build', () => {
  const plain = host(false).e;
  assert.equal(plain.execute('[1] | call editor.Link({Name: "made"})', {}, M).ok, true);
  plain.mutations.canConstructFromLiteral = (c) => (c === EngineObject ? false : undefined);
  assert.ok(error(plain.execute('[1] | call editor.Link({Name: "made"})', {}, M)).includes('engine object'));
  const allowed = host().e;
  allowed.mutations.canConstructFromLiteral = (c) => (c === EngineObject ? true : undefined);
  const r = allowed.execute('[1] | call editor.Link({Name: "made"})', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(r.rows![0]![2], 'made');
});

test('a typo inside a placement is reported against the placement overload', () => {
  const { e } = host();
  const err = error(e.execute('[1] | call editor.CreateGameObject("X", {positon: [1, 2, 3]}, "Cube")', {}, D));
  assert.ok(err.endsWith(": argument 'placement': no member 'positon' on Placement"), err);
});

test('overloads that came equally close are all listed; an arity mismatch names the counts', () => {
  const { e } = host();
  const err = error(e.execute('[1] | call editor.CreateGameObject("X", 5, "Cube")', {}, D));
  assert.ok(err.includes("no overload fits: CreateGameObject(string name, EngineObject parent, string primitive): argument 'parent': "), err);
  assert.ok(err.includes("; CreateGameObject(string name, Placement placement, string primitive, string tag): argument 'placement': "), err);
  assert.ok(error(e.execute('[1] | call editor.CreateGameObject("X")', {}, D)).endsWith('expects 3 or 4 argument(s), got 1'));
});

test('one object literal binds by parameter name', () => {
  const { e, editor } = host();
  const r = e.execute('range(0, 2) | call editor.Spawn({name: "Crate" + $, size: 2})', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.deepEqual(
    editor.scene.map((o) => o.Name),
    ['Crate0:Cube:2', 'Crate1:Cube:2'],
  );
  assert.ok(error(e.execute('[1] | call editor.Spawn({name: "A", size: 0})', {}, D)).includes('size must be positive; nothing was called'));
  assert.ok(error(e.execute('[1] | call editor.Spawn({name: "A", sise: 2})', {}, D)).endsWith("no parameter 'sise' (did you mean 'size'?); parameters: name, primitive, size"));
  assert.ok(error(e.execute('[1] | call editor.Spawn({size: 2})', {}, D)).endsWith("missing parameter 'name'; parameters: name, primitive, size"));
  assert.ok(error(e.execute('[1] | call editor.Spawn({name: "A", size: "big"})', {}, D)).endsWith("parameter 'size': expected a number"));
});
