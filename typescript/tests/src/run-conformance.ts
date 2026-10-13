/** CLI: runs the golden-vector suite and prints "N passed, M failed" (exit code 1 on failures). */
import { allVectors, summary } from './conformance.js';

const vectors = allVectors();
for (const v of vectors) if (!v.ok) process.stdout.write(`FAIL ${v.suite}: ${v.name}\n  ${v.detail ?? ''}\n`);
process.stdout.write(`conformance: ${summary(vectors)}\n`);
if (vectors.some((v) => !v.ok)) process.exitCode = 1;
