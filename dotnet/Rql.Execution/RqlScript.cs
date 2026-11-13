using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Rql.Execution
{
    /// <summary>
    /// Runs a request of several statements (spec §14). <see cref="RqlEngine.Execute(string, Budgets?, QueryOptions?)"/> uses
    /// it for text with more than one statement; hosts that execute plans elsewhere (e.g. over IPC) pass their own executor.
    /// </summary>
    public static class RqlScript
    {
        /// <summary>
        /// Parses every statement, then runs them in order with <paramref name="execute"/> and returns one result whose
        /// <see cref="QueryResult.Statements"/> hold each statement's result. A read request runs every statement that
        /// parses. A mutation request (<see cref="QueryOptions.AllowMutation"/>) runs nothing unless every statement parses and
        /// ends with set or call, and stops at the first statement that fails: the changes of the statements before it stay.
        /// More than <see cref="RqlParser.MaxStatements"/> statements fail as a whole with <c>syntax-error</c>.
        /// </summary>
        public static QueryResult Run(IReadOnlyList<RqlStatement> statements, QueryOptions? options, Func<QueryPlan, QueryResult> execute)
        {
            if (statements == null) throw new ArgumentNullException(nameof(statements));
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            options ??= new QueryOptions();
            if (statements.Count > RqlParser.MaxStatements)
                return QueryResult.Fail(new[] { Diagnostic.Error("syntax-error",
                    $"{statements.Count} statements in one call; at most {RqlParser.MaxStatements} are allowed",
                    "split them over several calls") });

            var sw = Stopwatch.StartNew();
            var entries = statements.Select(s => new StatementResult(s.Text)).ToList();
            var plans = new QueryPlan?[statements.Count];
            int invalid = -1;
            for (int i = 0; i < statements.Count; i++)
            {
                try
                {
                    plans[i] = RqlParser.Parse(statements[i].Text);
                    if (Pipeline.MutationWithoutWrite(plans[i]!, options) is RqlException noWrite) throw noWrite;
                }
                catch (RqlException ex)
                {
                    entries[i].Result = QueryResult.Fail(new[] { ex.ToDiagnostic() });
                    if (invalid < 0) invalid = i;
                }
            }

            int failed = -1;
            for (int i = 0; i < statements.Count; i++)
            {
                if (entries[i].Result != null) continue; // did not parse
                if (options.AllowMutation && invalid >= 0)
                    entries[i].Skipped = $"statement {invalid + 1} is invalid: a change runs only when every statement is valid, so nothing was changed";
                else if (failed >= 0)
                    entries[i].Skipped = $"statement {failed + 1} failed: the statements after a failed change do not run";
                else
                {
                    var r = execute(plans[i]!);
                    entries[i].Result = r;
                    if (!r.Ok && options.AllowMutation) failed = i;
                }
            }

            var result = new QueryResult
            {
                Ok = entries.All(e => e.Result?.Ok == true),
                DryRun = entries.Any(e => e.Result?.DryRun == true),
                Statements = entries,
            };
            foreach (var e in entries)
            {
                if (e.Result == null) continue;
                result.Stats.Visited += e.Result.Stats.Visited;
                result.Stats.MembersRead += e.Result.Stats.MembersRead;
            }
            result.Stats.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
