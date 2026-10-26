// ThreeJS-flavoured adapter scenarios (spec §13 "Methods" and "Virtual maps"), with minimal stand-ins
// for THREE.Vector3 / ShaderMaterial / Object3D.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t } from '@rql/execution';

class Vector3 {
  constructor(
    public x = 0,
    public y = 0,
    public z = 0,
  ) {}
}
class ShaderMaterial {
  uuid: string;
  name: string;
  uniforms: Record<string, { value: number }>;
  constructor(uuid: string, name: string, uniforms: Record<string, { value: number }>) {
    this.uuid = uuid;
    this.name = name;
    this.uniforms = uniforms;
  }
}
class Object3D {
  target = new Vector3();
  lookCalls = 0;
  constructor(
    public uuid: string,
    public name: string,
    public material: ShaderMaterial,
  ) {}
  lookAt(v: Vector3): void {
    this.target = new Vector3(v.x, v.y, v.z);
    this.lookCalls++;
  }
}

function scene(): { e: RqlEngine; meshes: Object3D[] } {
  const meshes = [
    new Object3D('m1', 'hero', new ShaderMaterial('mat1', 'glow', { uOpacity: { value: 1 }, uTime: { value: 0 } })),
    new Object3D('m2', 'enemy', new ShaderMaterial('mat2', 'flat', { uOpacity: { value: 0.5 } })),
  ];
  const types = new TypeModel()
    .register(Vector3, { kind: 'struct', members: [{ name: 'x', type: t.number }, { name: 'y', type: t.number }, { name: 'z', type: t.number }] })
    .register(ShaderMaterial, { members: [{ name: 'name', type: t.string }, { name: 'uniforms', type: 'any' }], display: 'name' })
    .register(Object3D, { members: [{ name: 'name', type: t.string }, { name: 'target', type: t.of(Vector3) }, { name: 'material', type: t.of(ShaderMaterial) }], display: 'name' });
  const e = new RqlEngine({ types })
    .addSource('meshes', () => meshes)
    .addAdapter({
      target: Vector3,
      encode: (v) => [v.x, v.y, v.z],
      coerce: (value) =>
        Array.isArray(value) && value.length === 3 && value.every((n) => typeof n === 'number')
          ? { ok: true, value: new Vector3(value[0] as number, value[1] as number, value[2] as number) }
          : { ok: false, reason: 'expected [x, y, z]' },
    })
    .addAdapter({
      target: ShaderMaterial,
      stableId: (m) => m.uuid,
      members: (m) =>
        m
          .hide('uniforms')
          .map<number>(
            'u',
            {
              keys: (mat) => Object.keys(mat.uniforms),
              get: (mat, k) => mat.uniforms[k]!.value,
              set: (mat, k, v) => {
                mat.uniforms[k]!.value = v;
              },
              validate: (mat, k, v) => (!(k in mat.uniforms) ? `no uniform '${k}'` : k === 'uOpacity' && (v < 0 || v > 1) ? 'uOpacity must be in [0, 1]' : undefined),
            },
            t.number,
            'shader uniforms (value)',
          )
          .map<string>('info', { keys: () => ['uuid'], get: (mat) => mat.uuid }, t.string),
    })
    .addAdapter({
      target: Object3D,
      stableId: (o) => o.uuid,
      members: (m) =>
        m
          .method('lookAt', [{ name: 'target', type: t.of(Vector3) }], undefined, (o, v: Vector3) => o.lookAt(v), 'turn towards a point')
          .method('lookAt', [{ name: 'x', type: t.number }, { name: 'y', type: t.number }, { name: 'z', type: t.number }], undefined, (o, x: number, y: number, z: number) =>
            o.lookAt(new Vector3(x, y, z)),
          )
          .method('distanceTo', [{ name: 'p', type: t.of(Vector3) }], t.number, (o, p: Vector3) => Math.hypot(o.target.x - p.x, o.target.y - p.y, o.target.z - p.z)),
    });
  e.mutations.enabled = true;
  return { e, meshes };
}

const mut = { allowMutation: true };

test('uniforms as a validated virtual map: read, filter, write, validate, read-only, atomic', () => {
  const { e, meshes } = scene();
  assert.deepEqual(e.execute('meshes | select name material.u').rows, [['hero', { uOpacity: 1, uTime: 0 }], ['enemy', { uOpacity: 0.5 }]]);
  assert.deepEqual(e.execute('meshes | where material.u["uOpacity"] < 1 | select name').rows, [['enemy']]);
  const w = e.execute('meshes | get material | set u["uOpacity"] = 0.25', {}, mut);
  assert.deepEqual(w.rows, [['ShaderMaterial#mat1', 'u["uOpacity"]', 1, 0.25], ['ShaderMaterial#mat2', 'u["uOpacity"]', 0.5, 0.25]]);
  assert.deepEqual(meshes.map((m) => m.material.uniforms.uOpacity!.value), [0.25, 0.25]);

  const bad = e.execute('ref("ShaderMaterial#mat1") | set u["uOpacity"] = 2', {}, mut);
  assert.equal(bad.diagnostics[0]?.code, 'invalid-value');
  assert.ok(bad.diagnostics[0]!.message.includes('uOpacity must be in [0, 1]'));
  // uTime exists only on mat1: the whole set is rejected before any write.
  assert.equal(e.execute('meshes | get material | set u["uTime"] = 5', {}, mut).diagnostics[0]?.code, 'invalid-value');
  assert.equal(meshes[0]!.material.uniforms.uTime!.value, 0);
  assert.equal(e.execute('meshes | get material | set u["uOpacity"] = "high"', {}, mut).diagnostics[0]?.code, 'coercion-failed');
  assert.equal(e.execute('ref("ShaderMaterial#mat1") | set info["uuid"] = "x"', {}, mut).diagnostics[0]?.code, 'readonly-member');
  assert.equal(e.execute('meshes | select material.uniforms').diagnostics[0]?.code, 'unknown-member');
});

test('lookAt overloads: by arity, Vector3 argument via adapter coercion, results, dry run', () => {
  const { e, meshes } = scene();
  const r = e.execute('ref("Object3D#m1") | call lookAt(1, 2, 3)', {}, mut);
  assert.deepEqual(r.rows, [['Object3D#m1', 'lookAt(double x, double y, double z)', null]]);
  assert.deepEqual(meshes[0]!.target, new Vector3(1, 2, 3));
  e.execute('ref("Object3D#m1") | call lookAt([4, 5, 6])', {}, mut);
  assert.deepEqual(meshes[0]!.target, new Vector3(4, 5, 6));
  assert.deepEqual(e.execute('meshes | call distanceTo([4, 5, 6])', {}, mut).rows?.map((row) => row[2]), [0, Math.hypot(4, 5, 6)]);
  const fail = e.execute('ref("Object3D#m1") | call lookAt("up")', {}, mut);
  assert.equal(fail.diagnostics[0]?.code, 'coercion-failed');
  const dry = e.execute('meshes | call lookAt(0, 0, 0)', {}, { allowMutation: true, dryRun: true });
  assert.equal(dry.dryRun, true);
  assert.equal(meshes[0]!.lookCalls, 2);
  const d = e.execute('meshes | first 1 | describe').types?.[0] as { methods: string[] };
  assert.deepEqual(d.methods, ['distanceTo(Vector3 p)', 'lookAt(Vector3 target) — turn towards a point', 'lookAt(double x, double y, double z)']);
});

test('hideMethods removes adapter or declared methods; unknown names are a host bug', () => {
  const { e } = scene();
  e.addAdapter({ target: Object3D, priority: -1, name: 'late', members: (m) => m.hideMethods('distanceTo') });
  // A lower-priority adapter of equal rank runs first (least specific first), before distanceTo exists.
  assert.equal(e.execute('meshes | call distanceTo([0, 0, 0])', {}, mut).diagnostics[0]?.code, 'internal-error');
  const { e: e2 } = scene();
  e2.addAdapter({ target: Object3D, priority: 5, members: (m) => m.hideMethods('distanceTo') });
  const r = e2.execute('meshes | call distanceTo([0, 0, 0])', {}, mut);
  assert.equal(r.diagnostics[0]?.code, 'unknown-function');
});
