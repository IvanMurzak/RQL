import assert from 'node:assert/strict';
import { test } from 'node:test';
import { RqlEngine, TypeModel, t } from '@rql/execution';

// Read stages after a call of read-only methods (spec §8 "Reading call results"). Mirrors dotnet/Rql.Tests/CallReadTests.cs.

class Ground {
  Inside = true;
  Height = 0;
}

class Terrain {
  Name = 'Island';
  Ready = true;
  reads = 0;
  sculpts = 0;
  /** A hill whose top is at (150, 100). */
  surface(point: number[]): Ground {
    this.reads++;
    const dx = point[0]! - 150;
    const dy = point[1]! - 100;
    return Object.assign(new Ground(), { Height: 500 - (dx * dx + dy * dy) / 100 });
  }
  sculpt(height: number): number {
    this.sculpts++;
    return height;
  }
}

function host(): { e: RqlEngine; terrain: Terrain; commits: number[] } {
  const model = new TypeModel()
    .register(Ground, { kind: 'struct', members: [{ name: 'Inside', type: t.bool }, { name: 'Height', type: t.number }] })
    .register(Terrain, {
      members: ['Name', { name: 'Ready', type: t.bool }],
      methods: [
        { name: 'surface', params: [{ name: 'point', type: t.list(t.number) }], returns: t.of(Ground), readOnly: true },
        { name: 'sculpt', params: [{ name: 'height', type: t.number }], returns: t.number },
      ],
    });
  const terrain = new Terrain();
  const commits: number[] = [];
  const e = new RqlEngine({ types: model }).addSource('landscape', () => terrain).addSource('terrains', () => [terrain]);
  e.mutations.enabled = true;
  e.mutations.committed = (records) => commits.push(records.length);
  return { e, terrain, commits };
}

const M = { allowMutation: true };

test('read stages run over the rows of read-only calls', () => {
  const { e, terrain } = host();
  const r = e.execute(
    'range(0, 961) | call landscape.surface([($ % 31) * 10, floor($ / 31) * 10]) | sort result.Height desc | first 1 | select h = result.Height',
    { maxMutationTargets: 1000 },
    M,
  );
  assert.equal(r.ok, true, JSON.stringify(r));
  assert.deepEqual(r.columns, ['h']);
  assert.deepEqual(r.rows, [[500]]);
  assert.equal(terrain.reads, 961);
});

test('a mutating call stays terminal and nothing is called', () => {
  const { e, terrain } = host();
  for (const options of [M, { allowMutation: true, dryRun: true }]) {
    const r = e.execute('terrains | call sculpt(5) | first 1', undefined, options);
    assert.equal(r.diagnostics[0]!.code, 'invalid-plan');
    assert.equal(r.diagnostics[0]!.message, "stage 'call' is terminal and must be the last stage");
    assert.ok(r.diagnostics[0]!.hint!.includes('sculpt('), r.diagnostics[0]!.hint);
  }
  assert.equal(terrain.sculpts, 0);
});

test('a binding error in a read stage fails before any call runs', () => {
  const { e, terrain, commits } = host();
  const r = e.execute('range(0, 3) | call landscape.surface([$, 0]) | where nosuchfn(result)', undefined, M);
  assert.equal(r.diagnostics[0]!.code, 'unknown-function');
  assert.equal(terrain.reads, 0);
  assert.equal(commits.length, 0);
});

test('a plan that ends with the call keeps the typo rule for result / method', () => {
  const { e, terrain } = host();
  const r = e.execute('terrains | where result > 1 | call sculpt(1)', undefined, M);
  assert.equal(r.ok, false);
  assert.equal(r.diagnostics[0]!.code, 'unknown-member');
  assert.equal(terrain.sculpts, 0);
});
