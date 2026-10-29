import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { QueryResult } from '@rql/contracts';
import { RqlEngine, TypeModel, t } from '@rql/execution';

// ADR-0011: literal kinds (engine / value / instanced with an outer), the host's refusal advice, known names that never
// evaluate a source, warnings for failing sort keys and sparse group-by / distinct keys. Mirrors dotnet/Rql.Tests/Wave6Tests.cs.

let nextId = 0;
let sceneNodesBuilt = 0;
let subObjectsBuilt = 0;

class EngineBase {
  Id = ++nextId;
  Name = '';
}

/** A scene object (Godot Node): never built from a literal. */
class SceneNode extends EngineBase {
  Mesh: AssetBase | null = null;
  Triggers: SubObject[] = [];
  Where = new Placement();
  constructor() {
    super();
    sceneNodesBuilt++;
  }
}

/** A value-like asset (Godot Resource). */
class AssetBase extends EngineBase {}
class BoxMesh extends AssetBase {
  Size = 1;
}

/** An instanced subobject (Unreal EditInlineNew). */
class SubObject extends EngineBase {
  Outer: SceneNode | null = null;
  Threshold = 0;
  Inner: SubObject | null = null;
  Parent: SubObject | null = null;
}
class HoldTrigger extends SubObject {}

class Placement {
  Trigger: SubObject | null = null;
  X = 0;
}

function model(): TypeModel {
  const sub = [
    { name: 'Threshold', type: t.number },
    { name: 'Inner', type: t.nullable(t.of(SubObject)) },
    { name: 'Parent', type: t.nullable(t.of(SubObject)), reference: true },
  ];
  return new TypeModel()
    .register(EngineBase, { members: [{ name: 'Id', type: t.int, readonly: true }, 'Name'] })
    .register(AssetBase, { members: [] })
    .register(BoxMesh, { members: [{ name: 'Size', type: t.number }] })
    .register(SubObject, { members: sub })
    .register(HoldTrigger, { members: [] })
    .register(Placement, { kind: 'struct', members: [{ name: 'Trigger', type: t.nullable(t.of(SubObject)) }, { name: 'X', type: t.number }] })
    .register(SceneNode, {
      members: [
        { name: 'Mesh', type: t.nullable(t.of(AssetBase)) },
        { name: 'Triggers', type: t.list(t.of(SubObject)) },
        { name: 'Where', type: t.of(Placement) },
      ],
      methods: [
        {
          name: 'AddTrigger',
          params: [{ name: 'trigger', type: t.of(SubObject) }],
          returns: t.string,
          invoke: (n, args) => {
            const trigger = args[0] as SubObject;
            (n as SceneNode).Triggers.push(trigger);
            return trigger.Outer?.Name ?? '-';
          },
        },
        { name: 'Attach', params: [{ name: 'node', type: t.of(SceneNode) }], returns: t.string, invoke: (_n, args) => (args[0] as SceneNode).Name },
      ],
    });
}

function scene(adapters = true): { e: RqlEngine; node: SceneNode } {
  const node = Object.assign(new SceneNode(), { Name: 'Player' });
  const e = new RqlEngine({ types: model() }).addSource('scene', () => [node], undefined, SceneNode);
  e.addAdapter({ target: EngineBase, stableId: (v) => String(v.Id), isAlive: () => true });
  if (adapters) {
    e.addAdapter({ target: AssetBase, literalKind: () => 'value' });
    e.addAdapter({
      target: SubObject,
      literalKind: () => 'instanced',
      createForLiteral: (target, outer) => {
        subObjectsBuilt++;
        const o = new (target as unknown as new () => SubObject)();
        o.Outer = outer instanceof SceneNode ? outer : null;
        o.Parent = outer instanceof SubObject ? outer : null;
        return o;
      },
    });
  }
  e.mutations.enabled = true;
  return { e, node };
}

const M = { allowMutation: true };
const D = { allowMutation: true, dryRun: true };
const like = (text: string, re: RegExp): void => assert.ok(re.test(text), text);
const error = (r: QueryResult): string => {
  assert.equal(r.ok, false, JSON.stringify(r));
  return r.diagnostics[0]!.message;
};

test('a value kind builds an identified class from a literal', () => {
  const { e, node } = scene();
  const r = e.execute('scene | set Mesh = {$type: "BoxMesh", Size: 2}', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal((node.Mesh as BoxMesh).Size, 2);
});

test('without a kind an identified class is an engine object', () => {
  const { e, node } = scene(false);
  const msg = error(e.execute('scene | set Mesh = {$type: "BoxMesh", Size: 2}', {}, M));
  like(msg, /BoxMesh is an engine object/);
  assert.equal(node.Mesh, null);
});

test('host hooks decide the kind', () => {
  const { e } = scene(false);
  e.mutations.literalKindOf = (c) => (c === BoxMesh || c === AssetBase ? 'value' : undefined);
  assert.equal(e.execute('scene | set Mesh = {$type: "BoxMesh"}', {}, M).ok, true);
  const { e: e2 } = scene();
  e2.mutations.canConstructFromLiteral = (c) => (c === BoxMesh ? false : undefined);
  like(error(e2.execute('scene | set Mesh = {$type: "BoxMesh"}', {}, M)), /engine object/);
  const { e: e3, node: n3 } = scene(false);
  e3.mutations.canConstructFromLiteral = (c) => (c === BoxMesh ? true : undefined);
  assert.equal(e3.execute('scene | set Mesh = {$type: "BoxMesh", Size: 3}', {}, M).ok, true);
  assert.equal((n3.Mesh as BoxMesh).Size, 3);
});

test('a host factory builds values and receives the outer', () => {
  const { e, node } = scene();
  let seen: object | undefined;
  e.mutations.createForLiteral = (c, outer) => {
    seen = outer;
    return c === BoxMesh ? Object.assign(new BoxMesh(), { Size: 7 }) : undefined;
  };
  assert.equal(e.execute('scene | set Mesh = {$type: "BoxMesh"}', {}, M).ok, true);
  assert.equal((node.Mesh as BoxMesh).Size, 7);
  assert.equal(seen, node);
});

test('an instanced literal is built for the owner of the written member', () => {
  const { e, node } = scene();
  const r = e.execute('scene | set Triggers = [{$type: "HoldTrigger", Threshold: 0.5}, {Threshold: 1}]', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(node.Triggers.length, 2);
  assert.ok(node.Triggers[0] instanceof HoldTrigger);
  for (const tr of node.Triggers) assert.equal(tr.Outer, node);
  assert.equal(node.Triggers[0]!.Threshold, 0.5);
});

test('a nested instanced literal is owned by the object the enclosing literal builds', () => {
  const { e, node } = scene();
  assert.equal(e.execute('scene | set Triggers = [{Threshold: 1, Inner: {$type: "HoldTrigger", Threshold: 2}}]', {}, M).ok, true);
  const outer = node.Triggers[0]!;
  assert.equal(outer.Inner!.Parent, outer);
  assert.equal(outer.Inner!.Outer, null);
  // A struct literal passes its own outer on.
  const r = e.execute('scene | set Where = {X: 1, Trigger: {Threshold: 3}}', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(node.Where.Trigger!.Outer, node);
});

test('an instanced call argument is built for the target or the receiver', () => {
  const { e, node } = scene();
  const r = e.execute('scene | call AddTrigger({$type: "HoldTrigger", Threshold: 0.5})', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal(r.rows![0]![2], 'Player');
  e.addSource('player', () => node);
  const q = e.execute('range(0, 2) | call player.AddTrigger({Threshold: $})', {}, M);
  assert.equal(q.ok, true, JSON.stringify(q));
  assert.equal(node.Triggers.length, 3);
  for (const tr of node.Triggers) assert.equal(tr.Outer, node);
});

test('the overload probe never runs a factory; a dry run builds but writes nothing', () => {
  const { e, node } = scene();
  sceneNodesBuilt = 0;
  subObjectsBuilt = 0;
  like(error(e.execute('scene | call Attach({Name: "x"})', {}, M)), /SceneNode is an engine object/);
  assert.equal(sceneNodesBuilt, 0);
  assert.equal(e.execute('scene | call AddTrigger({Threshold: 2})', {}, D).ok, true);
  assert.equal(subObjectsBuilt, 1);
  assert.equal(node.Triggers.length, 0);
});

test('the host replaces the engine-object refusal advice', () => {
  const { e } = scene(false);
  like(error(e.execute('scene | call Attach({Name: "x"})', {}, M)), /cannot be built from an object literal; pass an existing one/);
  e.mutations.literalRefusalHint = () => 'use scene.find(…) or an asset path';
  like(error(e.execute('scene | call Attach({Name: "x"})', {}, M)), /cannot be built from an object literal; use scene\.find\(…\) or an asset path/);
});

class Group {
  Label = '';
}

test('warnings never evaluate a source the query does not name', () => {
  let walks = 0;
  const types = new TypeModel().register(Group, { members: ['Label'] }).register(SceneNode, { members: [] });
  const e = new RqlEngine({ types })
    .addSource('items', () => [Object.assign(new Group(), { Label: 'a' })])
    .addSource('groups', () => {
      walks++;
      return [];
    });
  e.mutations.enabled = true;
  e.execute('items | where Labl == "x"');
  e.execute('items | where $ is SceneNode | count');
  e.execute('items | where false and Nmae == 1 | count');
  e.execute('items | where Labl == "x" | set Label = "y"', {}, M);
  assert.equal(walks, 0);
  // The query's own source counts even when the stream never pulled it.
  let own = 0;
  const e2 = new RqlEngine({ types }).addSource('items', () => {
    own++;
    return [new Group()];
  });
  const r = e2.execute('items | sort Label | first 0 | count');
  assert.equal(r.ok, true);
  assert.equal(r.diagnostics.some((d) => d.code === 'unknown-member'), false);
  assert.equal(own, 1);
});

class Thing {
  Name = '';
  Mass = 0;
  Volume = 0;
}

class Lamp {
  Name = '';
  type = 'Point';
}

test('a failing sort key warns', () => {
  const types = new TypeModel().register(Thing, { members: ['Name', { name: 'Mass', type: t.number }, { name: 'Volume', type: t.number }] });
  const e = new RqlEngine({ types }).addSource('things', () => [Object.assign(new Thing(), { Name: 'a', Mass: 1 }), Object.assign(new Thing(), { Name: 'b', Mass: 2, Volume: 1 })]);
  const r = e.execute('things | sort (Mass / Volume) desc | select Name');
  assert.equal(r.ok, true);
  assert.equal(r.diagnostics.length, 1);
  assert.equal(r.diagnostics[0]!.code, 'div-by-zero');
  like(r.diagnostics[0]!.message, /sort key 1 failed for 1 of 2 item\(s\)/);
  like(r.diagnostics[0]!.message, /they sort last/);
  assert.equal(r.rows![0]![0], 'b');
  assert.equal(e.execute('things | sort Mass').diagnostics.length, 0);
});

test('a sparse group-by member warns and offers the meta member', () => {
  const types = new TypeModel()
    .register(Thing, { members: ['Name', { name: 'Mass', type: t.number }, { name: 'Volume', type: t.number }] })
    .register(Lamp, { members: ['Name', 'type'] });
  const items: object[] = [];
  for (let i = 0; i < 9; i++) items.push(Object.assign(new Thing(), { Name: 't' + i }));
  items.push(Object.assign(new Lamp(), { Name: 'l' }));
  const e = new RqlEngine({ types }).addSource('items', () => items);
  const r = e.execute('items | group-by type');
  assert.equal(r.diagnostics.length, 1);
  assert.equal(r.diagnostics[0]!.code, 'unknown-member');
  assert.equal(r.diagnostics[0]!.message, "member 'type' exists on 1 of 10 item(s); the other 9 group as null");
  assert.equal(r.diagnostics[0]!.hint, 'did you mean $type (the meta member)?');
  const named = e.execute('items | group-by k=type, Name').diagnostics;
  assert.equal(named.length, 1);
  like(named[0]!.message, /exists on 1 of 10/);
  const distinct = e.execute('items | distinct type | count').diagnostics;
  assert.equal(distinct[0]!.message, "member 'type' exists on 1 of 10 item(s); the other 9 are kept as one");
  assert.equal(e.execute('items | group-by Volume').diagnostics.length, 0);
});

// ------------------------------------------------------------ review fixes: source learning, factories

class Gadget {
  Power = 1;
}

test('a source that starts empty learns its element types on a later read', () => {
  const items: object[] = [];
  const types = new TypeModel().register(Gadget, { members: [{ name: 'Power', type: t.number }] });
  const e = new RqlEngine({ types }).addSource('sel', () => items);
  assert.equal(e.execute('sel | count').ok, true);
  items.push(new Gadget());
  const r = e.execute('sel | where false and Power > 1 | count');
  assert.equal(r.ok, true);
  assert.equal(r.diagnostics.filter((d) => d.code === 'unknown-member').length, 0, JSON.stringify(r.diagnostics));
});

test('known names never consume a generator source', () => {
  const types = new TypeModel().register(Gadget, { members: [{ name: 'Power', type: t.number }] });
  let reads = 0;
  const e = new RqlEngine({ types })
    .addSource('items', () => [new Gadget()])
    .addSource('gen', function* () {
      reads++;
      yield new Gadget();
    });
  e.execute('items | where Pwr > 1');
  assert.equal(reads, 0);
  const r = e.execute('gen | where Power > 0 | count');
  assert.equal(r.ok, true);
  assert.equal(r.value, 1);
});

test('a declared source type makes its members known before any read', () => {
  const types = new TypeModel().register(Gadget, { members: [{ name: 'Power', type: t.number }] });
  let reads = 0;
  const e = new RqlEngine({ types }).addSource('items', () => [new Gadget()]).addSource('later', () => {
    reads++;
    return [];
  }, undefined, Gadget);
  const r = e.execute('items | where false and Power > 1 | count');
  assert.equal(r.diagnostics.filter((d) => d.code === 'unknown-member').length, 0);
  assert.equal(reads, 0);
});

test('the query\'s own source is read at most once per query', () => {
  const types = new TypeModel().register(Gadget, { members: [{ name: 'Power', type: t.number }] });
  let reads = 0;
  const e = new RqlEngine({ types }).addSource('nothing', () => {
    reads++;
    return null;
  });
  e.execute('nothing | where Nmae == 1 | count');
  assert.equal(reads, 1);
});

test('a throwing factory fails the build, an absent answer falls back to the constructor', () => {
  const { e, node } = scene();
  e.mutations.createForLiteral = (ctor) => {
    if (ctor === BoxMesh) throw new Error('no meshes today');
    return undefined;
  };
  like(error(e.execute('scene | set Mesh = {$type: "BoxMesh"}', {}, M)), /building BoxMesh failed: no meshes today/);
  e.mutations.createForLiteral = () => undefined;
  const r = e.execute('scene | set Mesh = {$type: "BoxMesh", Size: 4}', {}, M);
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.equal((node.Mesh as BoxMesh).Size, 4);
});
