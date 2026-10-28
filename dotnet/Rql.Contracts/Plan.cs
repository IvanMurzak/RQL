using System;
using System.Collections.Generic;

namespace Rql
{
    /// <summary>A versioned, runtime-neutral RQL query: source + stages. See spec/query-plan.schema.json.</summary>
    public sealed class QueryPlan
    {
        public const string CurrentVersion = "0.1";

        public QueryPlan(Expr source, IReadOnlyList<Stage> stages, string version = CurrentVersion)
        {
            Source = source;
            Stages = stages;
            Version = version;
        }

        public string Version { get; }
        public Expr Source { get; }
        public IReadOnlyList<Stage> Stages { get; }

        public override string ToString() => PlanCodec.Serialize(this);
    }

    // ---------------------------------------------------------------- expressions

    public abstract class Expr
    {
        /// <summary>Source span in the query text, when produced by a parser. Not serialized.</summary>
        public TextSpan? Span { get; set; }

        public override string ToString() => PlanCodec.SerializeExpr(this);
    }

    /// <summary>Literal: null, bool, double or string.</summary>
    public sealed class LitExpr : Expr
    {
        public LitExpr(object? value) { Value = value; }
        public object? Value { get; }
    }

    public sealed class ListExpr : Expr
    {
        public ListExpr(IReadOnlyList<Expr> items) { Items = items; }
        public IReadOnlyList<Expr> Items { get; }
    }

    /// <summary>Object literal <c>{ key: expr, … }</c>: an ordered string-keyed map; keys are unique (spec §4.5).</summary>
    public sealed class ObjectExpr : Expr
    {
        public ObjectExpr(IReadOnlyList<KeyValuePair<string, Expr>> entries) { Entries = entries; }
        public IReadOnlyList<KeyValuePair<string, Expr>> Entries { get; }
    }

    /// <summary>Navigation from the current item, or from <see cref="On"/>. Empty path = the item itself.</summary>
    public sealed class PathExpr : Expr
    {
        public PathExpr(IReadOnlyList<Segment> segments, Expr? on = null) { Segments = segments; On = on; }
        public IReadOnlyList<Segment> Segments { get; }
        public Expr? On { get; }
    }

    public sealed class OpExpr : Expr
    {
        public OpExpr(string op, IReadOnlyList<Expr> args) { Op = op; Args = args; }
        public string Op { get; }
        public IReadOnlyList<Expr> Args { get; }
    }

    /// <summary>Function call (On == null) or method call on the value of <see cref="On"/>.</summary>
    public sealed class CallExpr : Expr
    {
        public CallExpr(string name, IReadOnlyList<Expr> args, Expr? on = null) { Name = name; Args = args; On = on; }
        public string Name { get; }
        public IReadOnlyList<Expr> Args { get; }
        public Expr? On { get; }
    }

    public enum SegmentKind { Member, Index, Key, Meta }

    public readonly struct Segment
    {
        private Segment(SegmentKind kind, string? name, long index, object? key)
        {
            Kind = kind; Name = name; Index = index; Key = key;
        }

        public SegmentKind Kind { get; }
        /// <summary>Member or meta name (meta without '$').</summary>
        public string? Name { get; }
        public long Index { get; }
        /// <summary>Map key: string or double.</summary>
        public object? Key { get; }

        public static Segment Member(string name) => new Segment(SegmentKind.Member, name, 0, null);
        public static Segment Meta(string name) => new Segment(SegmentKind.Meta, name, 0, null);
        public static Segment At(long index) => new Segment(SegmentKind.Index, null, index, null);
        public static Segment ForKey(object key) => new Segment(SegmentKind.Key, null, 0, key);

        public override string ToString() => Kind switch
        {
            SegmentKind.Member => Name!,
            SegmentKind.Meta => "$" + Name,
            SegmentKind.Index => "[" + Index + "]",
            _ => "[" + PlanCodec.FormatKey(Key) + "]",
        };
    }

    public readonly struct TextSpan
    {
        public TextSpan(int start, int end) { Start = start; End = end; }
        public int Start { get; }
        public int End { get; }
        public override string ToString() => Start + ".." + End;
    }

    // ---------------------------------------------------------------- stages

    public abstract class Stage
    {
        protected Stage(string op) { Op = op; }
        public string Op { get; }
        public TextSpan? Span { get; set; }

        /// <summary>Terminal stages must be the last stage of a plan.</summary>
        public virtual bool IsTerminal => false;
        public virtual bool IsMutation => false;
    }

    public sealed class WhereStage : Stage
    {
        public WhereStage(Expr pred) : base("where") { Pred = pred; }
        public Expr Pred { get; }
    }

    public sealed class SelectField
    {
        public SelectField(string name, Expr expr) { Name = name; Expr = expr; }
        public string Name { get; }
        public Expr Expr { get; }
    }

    public sealed class SelectStage : Stage
    {
        public SelectStage(IReadOnlyList<SelectField> fields) : base("select") { Fields = fields; }
        public IReadOnlyList<SelectField> Fields { get; }
    }

    /// <summary>
    /// group-by with several keys or named keys (spec §5): rows with one column per key, then <c>count</c>. A single unnamed key
    /// stays an <see cref="ExprStage"/> (columns <c>key</c>, <c>count</c>).
    /// </summary>
    public sealed class GroupByStage : Stage
    {
        public GroupByStage(IReadOnlyList<SelectField> keys) : base("group-by") { Keys = keys; }
        public IReadOnlyList<SelectField> Keys { get; }

        /// <summary>Why these keys cannot form group-by columns (duplicate names, or a key named <c>count</c>), or null.</summary>
        public static string? Problem(IReadOnlyList<SelectField> keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "count" };
            foreach (var k in keys)
                if (!seen.Add(k.Name))
                    return string.Equals(k.Name, "count", StringComparison.OrdinalIgnoreCase)
                        ? "group-by key column 'count' clashes with the count column; name the key: group-by n=count"
                        : $"group-by key column '{k.Name}' appears twice; name the keys: group-by a=..., b=...";
            return null;
        }
    }

    public sealed class SortKey
    {
        public SortKey(Expr expr, bool desc) { Expr = expr; Desc = desc; }
        public Expr Expr { get; }
        public bool Desc { get; }
    }

    public sealed class SortStage : Stage
    {
        public SortStage(IReadOnlyList<SortKey> keys) : base("sort") { Keys = keys; }
        public IReadOnlyList<SortKey> Keys { get; }
    }

    /// <summary>first / skip.</summary>
    public sealed class LimitStage : Stage
    {
        public LimitStage(string op, int n) : base(op) { N = n; }
        public int N { get; }
    }

    /// <summary>count / describe (no arguments, terminal).</summary>
    public sealed class SimpleTerminalStage : Stage
    {
        public SimpleTerminalStage(string op) : base(op) { }
        public override bool IsTerminal => true;
    }

    /// <summary>get / sum / min / max / avg / group-by / distinct.</summary>
    public sealed class ExprStage : Stage
    {
        public ExprStage(string op, Expr? expr) : base(op) { Expr = expr; }
        public Expr? Expr { get; }
        public override bool IsTerminal => Op != "get" && Op != "distinct" && Op != "group-by";
    }

    public sealed class RecurseStage : Stage
    {
        public RecurseStage(IReadOnlyList<Expr>? via, int? maxDepth, bool includeSelf) : base("recurse")
        {
            Via = via; MaxDepth = maxDepth; IncludeSelf = includeSelf;
        }
        public IReadOnlyList<Expr>? Via { get; }
        public int? MaxDepth { get; }
        public bool IncludeSelf { get; }
    }

    public sealed class Assignment
    {
        public Assignment(PathExpr target, Expr value) { Target = target; Value = value; }
        public PathExpr Target { get; }
        public Expr Value { get; }
    }

    public sealed class SetStage : Stage
    {
        public SetStage(IReadOnlyList<Assignment> assignments) : base("set") { Assignments = assignments; }
        public IReadOnlyList<Assignment> Assignments { get; }
        public override bool IsTerminal => true;
        public override bool IsMutation => true;
    }

    public sealed class CallStage : Stage
    {
        public CallStage(string method, IReadOnlyList<Expr> args, Expr? on = null) : base("call") { Method = method; Args = args; On = on; }
        public string Method { get; }
        public IReadOnlyList<Expr> Args { get; }

        /// <summary>
        /// The receiver of a source-qualified call (<c>call editor.Create(…)</c>, spec §8): a source expression evaluated once,
        /// whose single object the method runs on once per stream item. Null: the method runs on each item.
        /// </summary>
        public Expr? On { get; }
        public override bool IsTerminal => true;
        public override bool IsMutation => true;
    }

    /// <summary>Extension stage (namespaced name), accepted structurally and bound by the host.</summary>
    public sealed class GenericStage : Stage
    {
        public GenericStage(string op, IReadOnlyList<Expr> args, IReadOnlyDictionary<string, Expr> named) : base(op)
        {
            Args = args; Named = named;
        }
        public IReadOnlyList<Expr> Args { get; }
        public IReadOnlyDictionary<string, Expr> Named { get; }
    }

    public static class Ops
    {
        public static readonly HashSet<string> Expression = new HashSet<string>
        {
            "or", "and", "not", "==", "!=", "<", "<=", ">", ">=", "contains", "starts-with", "ends-with",
            "matches", "in", "is", "neg", "+", "-", "*", "/", "%", "index",
        };

        public static readonly HashSet<string> CoreStages = new HashSet<string>
        {
            "where", "select", "sort", "first", "skip", "count", "describe", "get", "sum", "min", "max", "avg",
            "group-by", "distinct", "recurse", "set", "call",
        };
    }
}
