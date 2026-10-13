using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Rql
{
    /// <summary>Thrown for malformed plans/requests. <see cref="Code"/> is a spec diagnostic code.</summary>
    public sealed class RqlException : Exception
    {
        public RqlException(string code, string message, TextSpan? at = null, string? hint = null) : base(message)
        {
            Code = code; At = at; Hint = hint;
        }
        public string Code { get; }
        public TextSpan? At { get; }
        public string? Hint { get; }

        public Diagnostic ToDiagnostic() => new Diagnostic(DiagnosticSeverity.Error, Code, Message, At, Hint);
    }

    /// <summary>Canonical JSON codec for <see cref="QueryPlan"/> (fixed key order, defaults omitted).</summary>
    public static class PlanCodec
    {
        private static readonly JsonWriterOptions WriterOptions = new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // ------------------------------------------------------------ serialize

        public static string Serialize(QueryPlan plan, bool indented = false)
        {
            return Write(w =>
            {
                w.WriteStartObject();
                w.WriteString("rql", plan.Version);
                w.WritePropertyName("source");
                WriteExpr(w, plan.Source);
                w.WriteStartArray("stages");
                foreach (var s in plan.Stages) WriteStage(w, s);
                w.WriteEndArray();
                w.WriteEndObject();
            }, indented);
        }

        public static string SerializeExpr(Expr e) => Write(w => WriteExpr(w, e), false);

        private static string Write(Action<Utf8JsonWriter> body, bool indented)
        {
            using var ms = new MemoryStream();
            var opts = WriterOptions;
            opts.Indented = indented;
            using (var w = new Utf8JsonWriter(ms, opts)) body(w);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static void WriteNumber(Utf8JsonWriter w, double d)
        {
            if (Math.Abs(d) < 9007199254740992d && Math.Floor(d) == d && !(d == 0 && double.IsNegative(d)))
                w.WriteNumberValue((long)d);
            else
                w.WriteNumberValue(d);
        }

        private static void WriteLiteral(Utf8JsonWriter w, object? v)
        {
            switch (v)
            {
                case null: w.WriteNullValue(); break;
                case bool b: w.WriteBooleanValue(b); break;
                case string s: w.WriteStringValue(s); break;
                case double d: WriteNumber(w, d); break;
                default: throw new ArgumentException("Unsupported literal type " + v.GetType());
            }
        }

        public static void WriteExpr(Utf8JsonWriter w, Expr e)
        {
            w.WriteStartObject();
            switch (e)
            {
                case LitExpr l:
                    w.WritePropertyName("lit");
                    WriteLiteral(w, l.Value);
                    break;
                case ListExpr l:
                    w.WriteStartArray("list");
                    foreach (var i in l.Items) WriteExpr(w, i);
                    w.WriteEndArray();
                    break;
                case PathExpr p:
                    w.WriteStartArray("path");
                    foreach (var s in p.Segments) WriteSegment(w, s);
                    w.WriteEndArray();
                    if (p.On != null) { w.WritePropertyName("on"); WriteExpr(w, p.On); }
                    break;
                case OpExpr o:
                    w.WriteString("op", o.Op);
                    WriteExprArray(w, "args", o.Args);
                    break;
                case CallExpr c:
                    w.WriteString("call", c.Name);
                    if (c.On != null) { w.WritePropertyName("on"); WriteExpr(w, c.On); }
                    WriteExprArray(w, "args", c.Args);
                    break;
                default:
                    throw new ArgumentException("Unknown expression " + e.GetType().Name);
            }
            w.WriteEndObject();
        }

        private static void WriteExprArray(Utf8JsonWriter w, string name, IReadOnlyList<Expr> items)
        {
            w.WriteStartArray(name);
            foreach (var i in items) WriteExpr(w, i);
            w.WriteEndArray();
        }

        private static void WriteSegment(Utf8JsonWriter w, Segment s)
        {
            switch (s.Kind)
            {
                case SegmentKind.Member: w.WriteStringValue(s.Name); break;
                case SegmentKind.Index: w.WriteStartObject(); w.WriteNumber("index", s.Index); w.WriteEndObject(); break;
                case SegmentKind.Meta: w.WriteStartObject(); w.WriteString("meta", s.Name); w.WriteEndObject(); break;
                case SegmentKind.Key:
                    w.WriteStartObject();
                    w.WritePropertyName("key");
                    WriteLiteral(w, s.Key);
                    w.WriteEndObject();
                    break;
            }
        }

        private static void WriteStage(Utf8JsonWriter w, Stage s)
        {
            w.WriteStartObject();
            w.WriteString("op", s.Op);
            switch (s)
            {
                case WhereStage x:
                    w.WritePropertyName("pred"); WriteExpr(w, x.Pred);
                    break;
                case SelectStage x:
                    w.WriteStartArray("fields");
                    foreach (var f in x.Fields)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", f.Name);
                        w.WritePropertyName("expr"); WriteExpr(w, f.Expr);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    break;
                case SortStage x:
                    w.WriteStartArray("keys");
                    foreach (var k in x.Keys)
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("expr"); WriteExpr(w, k.Expr);
                        if (k.Desc) w.WriteBoolean("desc", true);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    break;
                case LimitStage x:
                    w.WriteNumber("n", x.N);
                    break;
                case SimpleTerminalStage _:
                    break;
                case ExprStage x:
                    if (x.Expr != null) { w.WritePropertyName("expr"); WriteExpr(w, x.Expr); }
                    break;
                case RecurseStage x:
                    if (x.Via != null && x.Via.Count > 0) WriteExprArray(w, "via", x.Via);
                    if (x.MaxDepth.HasValue) w.WriteNumber("maxDepth", x.MaxDepth.Value);
                    if (!x.IncludeSelf) w.WriteBoolean("includeSelf", false);
                    break;
                case SetStage x:
                    w.WriteStartArray("assign");
                    foreach (var a in x.Assignments)
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("target"); WriteExpr(w, a.Target);
                        w.WritePropertyName("value"); WriteExpr(w, a.Value);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    break;
                case CallStage x:
                    w.WriteString("method", x.Method);
                    WriteExprArray(w, "args", x.Args);
                    break;
                case GenericStage x:
                    if (x.Args.Count > 0) WriteExprArray(w, "args", x.Args);
                    if (x.Named.Count > 0)
                    {
                        w.WriteStartObject("named");
                        var keys = new List<string>(x.Named.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        foreach (var k in keys) { w.WritePropertyName(k); WriteExpr(w, x.Named[k]); }
                        w.WriteEndObject();
                    }
                    break;
            }
            w.WriteEndObject();
        }

        public static string FormatKey(object? key) => key switch
        {
            string s => JsonSerializer.Serialize(s),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => "null",
        };

        // ------------------------------------------------------------ deserialize

        public static QueryPlan Deserialize(string json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException ex) { throw Invalid("plan is not valid JSON: " + ex.Message); }
            using (doc) return ReadPlan(doc.RootElement);
        }

        public static QueryPlan ReadPlan(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("plan must be an object");
            AllowOnly(root, "plan", "rql", "source", "stages");
            if (!root.TryGetProperty("rql", out var v) || v.ValueKind != JsonValueKind.String)
                throw Invalid("plan.rql (version string) is required");
            var version = v.GetString()!;
            if (version != QueryPlan.CurrentVersion)
                throw new RqlException("version-unsupported", $"plan version '{version}' is not supported (supported: {QueryPlan.CurrentVersion})");
            var source = ReadExpr(Required(root, "source", "plan"));
            var stagesEl = Required(root, "stages", "plan");
            if (stagesEl.ValueKind != JsonValueKind.Array) throw Invalid("plan.stages must be an array");
            var stages = new List<Stage>();
            foreach (var s in stagesEl.EnumerateArray()) stages.Add(ReadStage(s));
            var plan = new QueryPlan(source, stages, version);
            ValidateStructure(plan);
            return plan;
        }

        /// <summary>Structural validation shared by codec and parser (terminal placement etc.).</summary>
        public static void ValidateStructure(QueryPlan plan)
        {
            for (int i = 0; i < plan.Stages.Count - 1; i++)
            {
                if (plan.Stages[i].IsTerminal)
                    throw new RqlException("invalid-plan", $"stage '{plan.Stages[i].Op}' is terminal and must be the last stage", plan.Stages[i].Span);
            }
        }

        private static RqlException Invalid(string message) => new RqlException("invalid-plan", message);

        private static JsonElement Required(JsonElement obj, string name, string ctx)
        {
            if (!obj.TryGetProperty(name, out var v)) throw Invalid($"{ctx}.{name} is required");
            return v;
        }

        private static void AllowOnly(JsonElement obj, string ctx, params string[] names)
        {
            if (obj.ValueKind != JsonValueKind.Object) throw Invalid($"{ctx} must be an object");
            foreach (var p in obj.EnumerateObject())
            {
                if (Array.IndexOf(names, p.Name) < 0) throw Invalid($"unexpected property '{p.Name}' in {ctx}");
            }
        }

        private static object? ReadLiteral(JsonElement v, string ctx)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Null: return null;
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number:
                {
                    var d = v.GetDouble();
                    if (double.IsInfinity(d) || double.IsNaN(d)) throw Invalid($"{ctx} number is out of range");
                    return d;
                }
                case JsonValueKind.String: return v.GetString();
                default: throw Invalid($"{ctx} must be a scalar literal");
            }
        }

        public static Expr ReadExpr(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) throw Invalid("expression must be an object");
            if (e.TryGetProperty("lit", out var lit))
            {
                AllowOnly(e, "lit expression", "lit");
                return new LitExpr(ReadLiteral(lit, "lit"));
            }
            if (e.TryGetProperty("list", out var list))
            {
                AllowOnly(e, "list expression", "list");
                return new ListExpr(ReadExprArray(list, "list"));
            }
            if (e.TryGetProperty("path", out var path))
            {
                AllowOnly(e, "path expression", "path", "on");
                return ReadPath(e, path);
            }
            if (e.TryGetProperty("op", out var op))
            {
                AllowOnly(e, "op expression", "op", "args");
                if (op.ValueKind != JsonValueKind.String) throw Invalid("op must be a string");
                var name = op.GetString()!;
                if (!Ops.Expression.Contains(name)) throw Invalid($"unknown operator '{name}'");
                var args = ReadExprArray(Required(e, "args", "op"), "args");
                int expected = name == "not" || name == "neg" ? 1 : 2;
                if (args.Count != expected) throw new RqlException("arity", $"operator '{name}' takes {expected} argument(s), got {args.Count}");
                return new OpExpr(name, args);
            }
            if (e.TryGetProperty("call", out var call))
            {
                AllowOnly(e, "call expression", "call", "on", "args");
                if (call.ValueKind != JsonValueKind.String || call.GetString()!.Length == 0) throw Invalid("call must be a non-empty string");
                var args = ReadExprArray(Required(e, "args", "call"), "args");
                Expr? on = e.TryGetProperty("on", out var onEl) ? ReadExpr(onEl) : null;
                return new CallExpr(call.GetString()!, args, on);
            }
            throw Invalid("expression must have one of: lit, list, path, op, call");
        }

        private static PathExpr ReadPath(JsonElement e, JsonElement path)
        {
            if (path.ValueKind != JsonValueKind.Array) throw Invalid("path must be an array");
            var segs = new List<Segment>();
            foreach (var s in path.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String)
                {
                    var n = s.GetString()!;
                    if (n.Length == 0) throw Invalid("member segment must not be empty");
                    segs.Add(Segment.Member(n));
                }
                else if (s.ValueKind == JsonValueKind.Object)
                {
                    if (s.TryGetProperty("index", out var ix))
                    {
                        AllowOnly(s, "index segment", "index");
                        if (ix.ValueKind != JsonValueKind.Number || !TryIntegral(ix, out var l)) throw Invalid("index must be an integer");
                        segs.Add(Segment.At(l));
                    }
                    else if (s.TryGetProperty("key", out var key))
                    {
                        AllowOnly(s, "key segment", "key");
                        var k = ReadLiteral(key, "key");
                        if (!(k is string) && !(k is double)) throw Invalid("key must be a string or number");
                        segs.Add(Segment.ForKey(k));
                    }
                    else if (s.TryGetProperty("meta", out var meta))
                    {
                        AllowOnly(s, "meta segment", "meta");
                        if (meta.ValueKind != JsonValueKind.String || meta.GetString()!.Length == 0) throw Invalid("meta must be a non-empty string");
                        segs.Add(Segment.Meta(meta.GetString()!));
                    }
                    else throw Invalid("segment object must have index, key or meta");
                }
                else throw Invalid("segment must be a string or object");
            }
            Expr? on = e.TryGetProperty("on", out var onEl) ? ReadExpr(onEl) : null;
            return new PathExpr(segs, on);
        }

        private static List<Expr> ReadExprArray(JsonElement arr, string ctx)
        {
            if (arr.ValueKind != JsonValueKind.Array) throw Invalid($"{ctx} must be an array");
            var list = new List<Expr>();
            foreach (var i in arr.EnumerateArray()) list.Add(ReadExpr(i));
            return list;
        }

        private static int ReadInt(JsonElement obj, string name, string ctx, int min)
        {
            var v = Required(obj, name, ctx);
            if (v.ValueKind != JsonValueKind.Number || !TryIntegral(v, out var n) || n < min || n > int.MaxValue)
                throw Invalid($"{ctx}.{name} must be an integer >= {min}");
            return (int)n;
        }

        /// <summary>Integers are JSON numbers with an integral value (1 and 1.0 are both accepted; 1.5 is not).</summary>
        private static bool TryIntegral(JsonElement v, out long value)
        {
            value = 0;
            if (v.TryGetInt64(out value)) return true;
            var d = v.GetDouble();
            if (Math.Floor(d) != d || Math.Abs(d) > 9007199254740992d) return false;
            value = (long)d;
            return true;
        }

        private static bool ReadTrueFlag(JsonElement obj, string name, string ctx)
        {
            if (!obj.TryGetProperty(name, out var v)) return false;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            throw Invalid($"{ctx}.{name} must be a boolean");
        }

        public static Stage ReadStage(JsonElement s)
        {
            if (s.ValueKind != JsonValueKind.Object) throw Invalid("stage must be an object");
            var opEl = Required(s, "op", "stage");
            if (opEl.ValueKind != JsonValueKind.String) throw Invalid("stage.op must be a string");
            var op = opEl.GetString()!;
            string ctx = "stage '" + op + "'";
            switch (op)
            {
                case "where":
                    AllowOnly(s, ctx, "op", "pred");
                    return new WhereStage(ReadExpr(Required(s, "pred", ctx)));
                case "select":
                {
                    AllowOnly(s, ctx, "op", "fields");
                    var fields = new List<SelectField>();
                    var arr = Required(s, "fields", ctx);
                    if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) throw Invalid("select.fields must be a non-empty array");
                    foreach (var f in arr.EnumerateArray())
                    {
                        AllowOnly(f, "select field", "name", "expr");
                        var name = Required(f, "name", "field");
                        if (name.ValueKind != JsonValueKind.String) throw Invalid("field.name must be a string");
                        fields.Add(new SelectField(name.GetString()!, ReadExpr(Required(f, "expr", "field"))));
                    }
                    return new SelectStage(fields);
                }
                case "sort":
                {
                    AllowOnly(s, ctx, "op", "keys");
                    var keys = new List<SortKey>();
                    var arr = Required(s, "keys", ctx);
                    if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) throw Invalid("sort.keys must be a non-empty array");
                    foreach (var k in arr.EnumerateArray())
                    {
                        AllowOnly(k, "sort key", "expr", "desc");
                        keys.Add(new SortKey(ReadExpr(Required(k, "expr", "sort key")), ReadTrueFlag(k, "desc", "sort key")));
                    }
                    return new SortStage(keys);
                }
                case "first":
                case "skip":
                    AllowOnly(s, ctx, "op", "n");
                    return new LimitStage(op, ReadInt(s, "n", op, 0));
                case "count":
                case "describe":
                    AllowOnly(s, ctx, "op");
                    return new SimpleTerminalStage(op);
                case "get":
                case "sum":
                case "min":
                case "max":
                case "avg":
                case "group-by":
                    AllowOnly(s, ctx, "op", "expr");
                    return new ExprStage(op, ReadExpr(Required(s, "expr", ctx)));
                case "distinct":
                    AllowOnly(s, ctx, "op", "expr");
                    return new ExprStage(op, s.TryGetProperty("expr", out var de) ? ReadExpr(de) : null);
                case "recurse":
                {
                    AllowOnly(s, ctx, "op", "via", "maxDepth", "includeSelf");
                    List<Expr>? via = s.TryGetProperty("via", out var viaEl) ? ReadExprArray(viaEl, "via") : null;
                    int? depth = s.TryGetProperty("maxDepth", out _) ? ReadInt(s, "maxDepth", ctx, 1) : (int?)null;
                    return new RecurseStage(via, depth, !s.TryGetProperty("includeSelf", out _) || ReadTrueFlag(s, "includeSelf", ctx));
                }
                case "set":
                {
                    AllowOnly(s, ctx, "op", "assign");
                    var arr = Required(s, "assign", ctx);
                    if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) throw Invalid("set.assign must be a non-empty array");
                    var list = new List<Assignment>();
                    foreach (var a in arr.EnumerateArray())
                    {
                        AllowOnly(a, "assignment", "target", "value");
                        if (!(ReadExpr(Required(a, "target", "assignment")) is PathExpr target) || target.Segments.Count == 0
                            || target.On != null || target.Segments.Any(sg => sg.Kind == SegmentKind.Meta))
                            throw Invalid("assignment.target must be a non-empty member path relative to the item (no 'on', no meta segments)");
                        list.Add(new Assignment(target, ReadExpr(Required(a, "value", "assignment"))));
                    }
                    return new SetStage(list);
                }
                case "call":
                {
                    AllowOnly(s, ctx, "op", "method", "args");
                    var m = Required(s, "method", ctx);
                    if (m.ValueKind != JsonValueKind.String || m.GetString()!.Length == 0) throw Invalid("call.method must be a non-empty string");
                    return new CallStage(m.GetString()!, ReadExprArray(Required(s, "args", ctx), "args"));
                }
                default:
                {
                    if (op.IndexOf('.') < 0)
                        throw new RqlException("unknown-stage", $"unknown stage '{op}' (extension stages must be namespaced, e.g. 'myapp.{op}')");
                    AllowOnly(s, ctx, "op", "args", "named");
                    var args = s.TryGetProperty("args", out var a) ? ReadExprArray(a, "args") : new List<Expr>();
                    var named = new Dictionary<string, Expr>();
                    if (s.TryGetProperty("named", out var n))
                    {
                        if (n.ValueKind != JsonValueKind.Object) throw Invalid("named must be an object");
                        foreach (var p in n.EnumerateObject()) named[p.Name] = ReadExpr(p.Value);
                    }
                    return new GenericStage(op, args, named);
                }
            }
        }
    }
}
