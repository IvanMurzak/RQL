using System.Collections.Generic;
using System.Linq;

namespace Rql.Execution
{
    /// <summary>Static plan analysis used for execution-time optimizations.</summary>
    internal static class PlanScan
    {
        public static bool UsesMeta(QueryPlan plan, string meta) => AllExprs(plan).Any(e => UsesMeta(e, meta));

        /// <summary>Whether any path of the plan names a member <paramref name="name"/> (case-insensitive).</summary>
        public static bool UsesMember(QueryPlan plan, string name) => AllExprs(plan).Any(e => UsesMember(e, name));

        private static bool UsesMember(Expr e, string name) => e switch
        {
            PathExpr p => p.Segments.Any(s => s.Kind == SegmentKind.Member && string.Equals(s.Name, name, System.StringComparison.OrdinalIgnoreCase))
                          || (p.On != null && UsesMember(p.On, name)),
            OpExpr o => o.Args.Any(a => UsesMember(a, name)),
            CallExpr c => c.Args.Any(a => UsesMember(a, name)) || (c.On != null && UsesMember(c.On, name)),
            ListExpr l => l.Items.Any(a => UsesMember(a, name)),
            ObjectExpr o => o.Entries.Any(kv => UsesMember(kv.Value, name)),
            _ => false,
        };

        private static bool UsesMeta(Expr e, string meta) => e switch
        {
            PathExpr p => p.Segments.Any(s => s.Kind == SegmentKind.Meta && s.Name == meta) || (p.On != null && UsesMeta(p.On, meta)),
            OpExpr o => o.Args.Any(a => UsesMeta(a, meta)),
            CallExpr c => c.Args.Any(a => UsesMeta(a, meta)) || (c.On != null && UsesMeta(c.On, meta)),
            ListExpr l => l.Items.Any(a => UsesMeta(a, meta)),
            ObjectExpr o => o.Entries.Any(kv => UsesMeta(kv.Value, meta)),
            _ => false,
        };

        public static IEnumerable<Expr> AllExprs(QueryPlan plan)
        {
            yield return plan.Source;
            foreach (var st in plan.Stages)
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
}
