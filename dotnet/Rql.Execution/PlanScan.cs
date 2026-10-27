using System;
using System.Collections.Generic;
using System.Linq;

namespace Rql.Execution
{
    /// <summary>Static plan analysis used for execution-time optimizations.</summary>
    internal static class PlanScan
    {
        public static bool UsesMeta(QueryPlan plan, string meta) =>
            AllExprs(plan).Any(e => UsesSegment(e, s => s.Kind == SegmentKind.Meta && s.Name == meta));

        /// <summary>
        /// Whether a stage after <paramref name="stage"/> names a member <paramref name="name"/> (case-insensitive), e.g. the
        /// <c>items</c> of group rows (spec §5).
        /// </summary>
        public static bool UsesMemberAfter(QueryPlan plan, Stage stage, string name)
        {
            int at = -1;
            for (int i = 0; i < plan.Stages.Count; i++) if (ReferenceEquals(plan.Stages[i], stage)) { at = i; break; }
            return plan.Stages.Skip(at + 1).SelectMany(StageExprs)
                .Any(e => UsesSegment(e, s => s.Kind == SegmentKind.Member && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool UsesSegment(Expr e, Func<Segment, bool> test) => e switch
        {
            PathExpr p => p.Segments.Any(test) || (p.On != null && UsesSegment(p.On, test)),
            OpExpr o => o.Args.Any(a => UsesSegment(a, test)),
            CallExpr c => c.Args.Any(a => UsesSegment(a, test)) || (c.On != null && UsesSegment(c.On, test)),
            ListExpr l => l.Items.Any(a => UsesSegment(a, test)),
            ObjectExpr o => o.Entries.Any(kv => UsesSegment(kv.Value, test)),
            _ => false,
        };

        public static IEnumerable<Expr> AllExprs(QueryPlan plan) => new[] { plan.Source }.Concat(plan.Stages.SelectMany(StageExprs));

        private static IEnumerable<Expr> StageExprs(Stage st)
        {
            switch (st)
            {
                case WhereStage w: yield return w.Pred; break;
                case SelectStage s: foreach (var f in s.Fields) yield return f.Expr; break;
                case GroupByStage g: foreach (var k in g.Keys) yield return k.Expr; break;
                case SortStage s: foreach (var k in s.Keys) yield return k.Expr; break;
                case ExprStage e when e.Expr != null: yield return e.Expr; break;
                case RecurseStage r when r.Via != null: foreach (var v in r.Via) yield return v; break;
                case SetStage s: foreach (var a in s.Assignments) { yield return a.Target; yield return a.Value; } break;
                case CallStage c: foreach (var a in c.Args) yield return a; break;
                case GenericStage g: foreach (var a in g.Args.Concat(g.Named.Values)) yield return a; break;
            }
        }
    }
}
