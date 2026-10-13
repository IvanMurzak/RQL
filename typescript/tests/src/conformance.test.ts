import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { allVectors, summary, type Vector } from './conformance.js';

const vectors = allVectors();
const suites = new Map<string, Vector[]>();
for (const v of vectors) suites.set(v.suite, [...(suites.get(v.suite) ?? []), v]);

for (const [suite, vs] of suites) {
  describe(`conformance: ${suite} (${summary(vs)})`, () => {
    for (const v of vs) test(v.name, () => assert.ok(v.ok, v.detail));
  });
}
