/** Runs a request of several statements (spec §14). Port of dotnet/Rql.Execution/RqlScript.cs. */
import { RqlError, diagnostic, failResult, type QueryOptions, type QueryPlan, type QueryResult, type StatementResult } from '@rql/contracts';
import { MAX_STATEMENTS, parse, type RqlStatement } from '@rql/parser';
import { now } from './exec.js';
import { mutationWithoutWrite } from './pipeline.js';

/**
 * Parses every statement, then runs them in order with `execute` and returns one result whose `statements` hold each
 * statement's result. A read request runs every statement that parses. A mutation request (`allowMutation`) runs nothing
 * unless every statement parses and ends with set or call, and stops at the first statement that fails: the changes of the
 * statements before it stay. More than MAX_STATEMENTS statements fail as a whole with `syntax-error`. Hosts that execute
 * plans elsewhere pass their own `execute`.
 */
export function runStatements(statements: readonly RqlStatement[], options: QueryOptions, execute: (plan: QueryPlan) => QueryResult): QueryResult {
  if (statements.length > MAX_STATEMENTS) {
    return failResult([
      diagnostic('error', 'syntax-error', `${statements.length} statements in one call; at most ${MAX_STATEMENTS} are allowed`, 'split them over several calls'),
    ]);
  }
  const started = now();
  const entries: StatementResult[] = statements.map((s) => ({ text: s.text }));
  const plans: (QueryPlan | undefined)[] = [];
  let invalid = -1;
  statements.forEach((s, i) => {
    try {
      const plan = parse(s.text);
      const noWrite = mutationWithoutWrite(plan, options);
      if (noWrite !== undefined) throw noWrite;
      plans[i] = plan;
    } catch (e) {
      if (!(e instanceof RqlError)) throw e;
      entries[i]!.result = failResult([e.toDiagnostic()]);
      if (invalid < 0) invalid = i;
    }
  });

  let failed = -1;
  entries.forEach((entry, i) => {
    if (entry.result !== undefined) return; // did not parse
    if (options.allowMutation && invalid >= 0) {
      entry.skipped = `statement ${invalid + 1} is invalid: a change runs only when every statement is valid, so nothing was changed`;
    } else if (failed >= 0) {
      entry.skipped = `statement ${failed + 1} failed: the statements after a failed change do not run`;
    } else {
      const r = execute(plans[i]!);
      entry.result = r;
      if (!r.ok && options.allowMutation) failed = i;
    }
  });

  const ran = entries.flatMap((e) => (e.result !== undefined ? [e.result] : []));
  const result: QueryResult = {
    ok: entries.every((e) => e.result?.ok === true),
    statements: entries,
    diagnostics: [],
    stats: {
      visited: ran.reduce((n, r) => n + r.stats.visited, 0),
      membersRead: ran.reduce((n, r) => n + r.stats.membersRead, 0),
      elapsedMs: now() - started,
    },
  };
  if (ran.some((r) => r.dryRun === true)) result.dryRun = true;
  return result;
}
