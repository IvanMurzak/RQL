/**
 * Shared golden-vector suite (fixtures/), run exactly like dotnet/Rql.Conformance:
 * syntax → canonical plan (+ codec round trip), negative syntax / plans, execution vectors against
 * a FRESH native graph per case (the `then` step runs on the same graph instance).
 */
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RqlError, deserializePlan, parseBudgets, planToJson, resultToJson, type Budgets, type JsonValue, type QueryOptions, type QueryResult } from '@rql/contracts';
import { RqlEngine } from '@rql/execution';
import { parse } from '@rql/parser';
import { conformanceModel, loadGraph } from './graph.js';
import { registerTestAdapters } from './test-adapters.js';

export interface Vector {
  readonly suite: 'syntax' | 'codec' | 'negative-syntax' | 'negative-plan' | 'execution' | 'adapters';
  readonly name: string;
  readonly ok: boolean;
  readonly detail?: string;
}

export const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const fixture = (rel: string): unknown => JSON.parse(readFileSync(resolve(repoRoot, 'fixtures', rel), 'utf8')) as unknown;

/** Structural JSON equality; numbers compare by value; object key order ignored. */
export function jsonEq(a: unknown, b: unknown): boolean {
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return a === b;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) {
    const bb = b as unknown[];
    return a.length === bb.length && a.every((v, i) => jsonEq(v, bb[i]));
  }
  const ka = Object.keys(a);
  const ob = b as Record<string, unknown>;
  return ka.length === Object.keys(ob).length && ka.every((k) => Object.prototype.hasOwnProperty.call(ob, k) && jsonEq((a as Record<string, unknown>)[k], ob[k]));
}

/** Conformance view of a result: no stats, no "rql", diagnostics reduced to severity+code. */
export function normalize(r: QueryResult): JsonValue {
  const o = resultToJson(r, false);
  delete o.rql;
  if (Array.isArray(o.diagnostics)) {
    o.diagnostics = o.diagnostics.map((d) => {
      const dj = d as { severity: string; code: string };
      return { severity: dj.severity, code: dj.code };
    });
  }
  return o;
}

const errMsg = (e: unknown): string => (e instanceof Error ? e.message : String(e));

interface Case {
  [k: string]: unknown;
}
const cases = (doc: unknown): Case[] => (doc as { cases: Case[] }).cases;

export function syntaxVectors(): Vector[] {
  const out: Vector[] = [];
  for (const c of cases(fixture('syntax/cases.json'))) {
    const rql = c.rql as string;
    try {
      const got = planToJson(parse(rql));
      out.push({ suite: 'syntax', name: rql, ok: jsonEq(got, c.plan), detail: `got ${JSON.stringify(got)}` });
    } catch (e) {
      out.push({ suite: 'syntax', name: rql, ok: false, detail: 'failed to parse: ' + errMsg(e) });
      continue;
    }
    try {
      const round = planToJson(deserializePlan(JSON.stringify(c.plan)));
      out.push({ suite: 'codec', name: rql, ok: jsonEq(round, c.plan), detail: `round trip ${JSON.stringify(round)}` });
    } catch (e) {
      out.push({ suite: 'codec', name: rql, ok: false, detail: 'stored plan rejected: ' + errMsg(e) });
    }
  }
  return out;
}

export function negativeVectors(): Vector[] {
  const out: Vector[] = [];
  for (const c of cases(fixture('negative/syntax.json'))) {
    let code: string | null = null;
    try {
      parse(c.rql as string);
    } catch (e) {
      code = e instanceof RqlError ? e.code : 'exception: ' + errMsg(e);
    }
    out.push({ suite: 'negative-syntax', name: JSON.stringify(c.rql), ok: code !== null && code === c.code, detail: `got ${code ?? 'parsed OK'}, want ${String(c.code)}` });
  }
  for (const c of cases(fixture('negative/plans.json'))) {
    let code: string | null = null;
    try {
      deserializePlan(JSON.stringify(c.plan));
    } catch (e) {
      code = e instanceof RqlError ? e.code : 'exception: ' + errMsg(e);
    }
    const want = (c.code as string | null | undefined) ?? null;
    out.push({ suite: 'negative-plan', name: JSON.stringify(c.plan), ok: code === want, detail: `got ${code ?? 'accepted'}, want ${want ?? 'accepted'}` });
  }
  return out;
}

export function executionVectors(): Vector[] {
  return caseVectors('execution', 'execution/cases.json', (e) => e);
}

/** fixtures/adapters: same harness, plus the test adapter set on every fresh engine. */
export function adapterVectors(): Vector[] {
  return caseVectors('adapters', 'adapters/cases.json', registerTestAdapters);
}

function caseVectors(suite: 'execution' | 'adapters', file: string, setup: (e: RqlEngine) => RqlEngine): Vector[] {
  const out: Vector[] = [];
  const model = conformanceModel();
  const graphJson = fixture('cross-runtime/graph.json');
  for (const c of cases(fixture(file))) {
    const name = c.name as string;
    const graph = loadGraph(graphJson);
    const engine = setup(new RqlEngine({ types: model }).addSource('g', () => graph));
    engine.mutations.enabled = true;
    const budgets: Budgets = c.budgets !== undefined ? parseBudgets(c.budgets) : {};
    const o = (c.options ?? {}) as { allowMutation?: boolean; dryRun?: boolean };
    const options: QueryOptions = { allowMutation: o.allowMutation ?? false, dryRun: o.dryRun ?? false };
    out.push(step(suite, engine, name, c.rql as string, c.plan, c.expect, budgets, options));
    if (c.then !== undefined) out.push(step(suite, engine, name + ' (then)', c.then as string, c.thenPlan, c.thenExpect, {}, {}));
  }
  return out;
}

function step(suite: Vector['suite'], engine: RqlEngine, name: string, rql: string, planJson: unknown, expect: unknown, budgets: Budgets, options: QueryOptions): Vector {
  let result: QueryResult;
  try {
    if (planJson !== undefined) {
      let plan;
      try {
        plan = deserializePlan(planJson);
      } catch (e) {
        return { suite, name, ok: false, detail: 'stored plan rejected: ' + errMsg(e) };
      }
      result = engine.execute(plan, budgets, options);
    } else {
      result = engine.execute(rql, budgets, options);
    }
  } catch (e) {
    return { suite, name, ok: false, detail: `'${rql}' threw ${e instanceof Error ? (e.stack ?? e.message) : String(e)}` };
  }
  const got = normalize(result);
  return { suite, name, ok: jsonEq(got, expect), detail: `'${rql}'\n  got:  ${JSON.stringify(got)}\n  want: ${JSON.stringify(expect)}` };
}

export function allVectors(): Vector[] {
  return [...syntaxVectors(), ...negativeVectors(), ...executionVectors(), ...adapterVectors()];
}

export function summary(vs: readonly Vector[]): string {
  const failed = vs.filter((v) => !v.ok).length;
  return `${vs.length - failed} passed, ${failed} failed`;
}
