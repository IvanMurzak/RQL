/** Execution request/result contracts (spec/query-result.schema.json, §10 budgets). */
import type { JsonObject, JsonValue } from './codec.js';
import { RqlError, type Diagnostic } from './errors.js';
import { RQL_VERSION } from './plan.js';

/** Request-level resource limits (spec §10). Absent = runtime default. */
export interface Budgets {
  maxVisited?: number;
  maxRows?: number;
  maxListItems?: number;
  maxDepth?: number;
  timeoutMs?: number;
  maxOutputBytes?: number;
  maxMutationTargets?: number;
}

const BUDGET_KEYS: ReadonlySet<string> = new Set([
  'maxVisited', 'maxRows', 'maxListItems', 'maxDepth', 'timeoutMs', 'maxOutputBytes', 'maxMutationTargets',
]);

/** Validates a JSON budgets object (non-negative int32 values, known keys only). */
export function parseBudgets(json: unknown): Budgets {
  if (typeof json !== 'object' || json === null || Array.isArray(json)) throw new RqlError('invalid-plan', 'budgets must be an object');
  const b: Record<string, number> = {};
  for (const [k, v] of Object.entries(json)) {
    if (typeof v !== 'number' || !Number.isInteger(v) || v < 0 || v > 2147483647) {
      throw new RqlError('invalid-plan', `budget '${k}' must be a non-negative integer`);
    }
    if (!BUDGET_KEYS.has(k)) throw new RqlError('invalid-plan', `unknown budget '${k}'`);
    b[k] = v;
  }
  return b as Budgets;
}

export interface QueryOptions {
  dryRun?: boolean;
  allowMutation?: boolean;
}

export type ResultKind = 'rows' | 'items' | 'scalar' | 'schema';

export interface Stats {
  visited: number;
  membersRead: number;
  elapsedMs: number;
}

/** Execution result; values are already encoded (spec §6). Optional keys are absent when unset. */
export interface QueryResult {
  ok: boolean;
  kind?: ResultKind;
  columns?: string[];
  rows?: JsonValue[][];
  items?: JsonValue[];
  /** Present (possibly null) for scalar results. */
  value?: JsonValue;
  types?: JsonObject[];
  total?: number;
  truncated?: boolean;
  dryRun?: boolean;
  /** A `set` receipt: how many assignments left the value as it was (old == new), omitted from the rows. */
  unchanged?: number;
  /**
   * A request of several statements (spec §14): one entry per statement, in order. The result's own rows, items and value
   * stay absent; `ok` is true only when every statement ran and succeeded. Absent for a single statement.
   */
  statements?: StatementResult[];
  diagnostics: Diagnostic[];
  stats: Stats;
}

/** One statement of a request of several (spec §14): its text and either its result or why it did not run. */
export interface StatementResult {
  /** The statement as written (trimmed). */
  text: string;
  result?: QueryResult;
  /** Why the statement did not run (absent when it ran). */
  skipped?: string;
}

export function failResult(diagnostics: Iterable<Diagnostic>): QueryResult {
  return { ok: false, diagnostics: [...diagnostics], stats: { visited: 0, membersRead: 0, elapsedMs: 0 } };
}

/** Wire form of a result (key order as in the .NET reference). */
export function resultToJson(r: QueryResult, includeStats = true): JsonObject {
  const o: JsonObject = { rql: RQL_VERSION, ok: r.ok };
  if (r.kind !== undefined) o.kind = r.kind;
  if (r.columns !== undefined) o.columns = r.columns;
  if (r.rows !== undefined) o.rows = r.rows;
  if (r.items !== undefined) o.items = r.items;
  if (r.value !== undefined) o.value = r.value;
  if (r.types !== undefined) o.types = r.types;
  if (r.total !== undefined) o.total = r.total;
  if (r.truncated) o.truncated = true;
  if (r.dryRun) o.dryRun = true;
  if (r.unchanged !== undefined && r.unchanged > 0) o.unchanged = r.unchanged;
  if (r.statements !== undefined) {
    o.statements = r.statements.map((s): JsonObject =>
      s.result !== undefined ? { text: s.text, result: resultToJson(s.result, includeStats) } : { text: s.text, skipped: s.skipped ?? '' });
  }
  if (r.diagnostics.length > 0) {
    o.diagnostics = r.diagnostics.map((d) => {
      const dj: JsonObject = { severity: d.severity, code: d.code, message: d.message };
      if (d.hint !== undefined) dj.hint = d.hint;
      if (d.at !== undefined) dj.at = { start: d.at.start, end: d.at.end };
      return dj;
    });
  }
  if (includeStats) {
    o.stats = { visited: r.stats.visited, membersRead: r.stats.membersRead, elapsedMs: Math.round(r.stats.elapsedMs * 1000) / 1000 };
  }
  return o;
}
