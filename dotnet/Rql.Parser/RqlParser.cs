using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Parsing;

namespace Rql
{
    /// <summary>
    /// Syntax-only RQL parser: text → canonical <see cref="QueryPlan"/>. Knows no domain types,
    /// sources or extension names (spec/grammar.ebnf).
    /// </summary>
    public sealed class RqlParser
    {
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "and", "or", "not", "in", "is", "null", "true", "false",
        };

        private static readonly Dictionary<string, string> StageAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["limit"] = "first",
        };

        private readonly string _text;
        private readonly List<Token> _tokens;
        private int _pos;

        private RqlParser(string text)
        {
            _text = text;
            _tokens = Lexer.Tokenize(text);
        }

        /// <summary>Parses or throws <see cref="RqlException"/> (code syntax-error / invalid-plan).</summary>
        public static QueryPlan Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new RqlParser(text);
            var plan = p.ParseQuery();
            PlanCodec.ValidateStructure(plan);
            return plan;
        }

        /// <summary>
        /// Parses one expression with nothing after it, e.g. a value literal <c>{name: "Lamp", position: [1, 2, 3]}</c>
        /// (hosts that accept RQL literals outside a query, such as tool arguments). Throws <see cref="RqlException"/>.
        /// </summary>
        public static Expr ParseValue(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new RqlParser(text);
            if (p.Peek().Kind == TokenKind.End) throw p.Error("empty value", p.Peek());
            var e = p.ParseExpr();
            if (p.Peek().Kind != TokenKind.End) throw p.Error($"unexpected {p.Peek().Describe()} after the value", p.Peek());
            return e;
        }

        public static bool TryParse(string text, out QueryPlan? plan, out Diagnostic? error)
        {
            try
            {
                plan = Parse(text);
                error = null;
                return true;
            }
            catch (RqlException ex)
            {
                plan = null;
                error = ex.ToDiagnostic();
                return false;
            }
        }

        // ------------------------------------------------------------ helpers

        private Token Peek(int ahead = 0) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];
        private Token Next() => _tokens[_pos < _tokens.Count - 1 ? _pos++ : _pos];
        private bool AtStageEnd => Peek().Kind == TokenKind.End || Peek().Is("|");

        private bool Accept(string punct)
        {
            if (!Peek().Is(punct)) return false;
            _pos++;
            return true;
        }

        private bool AcceptWord(string word)
        {
            if (!Peek().IsWord(word)) return false;
            _pos++;
            return true;
        }

        private Token Expect(string punct, string context)
        {
            var t = Peek();
            if (!t.Is(punct)) throw Error($"expected '{punct}' {context}, found {t.Describe()}", t);
            return Next();
        }

        private RqlException Error(string message, Token at, string? hint = null) =>
            new RqlException("syntax-error", message + " at " + at.Start, at.Span, hint);

        private static T At<T>(T node, int start, int end) where T : Expr
        {
            node.Span = new TextSpan(start, end);
            return node;
        }

        private int PrevEnd => _pos > 0 ? _tokens[_pos - 1].End : 0;

        private string Slice(int start, int end) => _text.Substring(start, end - start).Trim();

        private bool IsName(Token t) =>
            t.Kind == TokenKind.QuotedIdent || (t.Kind == TokenKind.Ident && !Reserved.Contains(t.Text));

        // ------------------------------------------------------------ query / stages

        private QueryPlan ParseQuery()
        {
            if (Peek().Kind == TokenKind.End) throw Error("empty query; expected a source such as 'world' or 'ref(\"Type#1\")'", Peek());
            var source = ParsePostfix();
            var stages = new List<Stage>();
            while (true)
            {
                var t = Peek();
                if (t.Kind == TokenKind.End) break;
                if (!t.Is("|"))
                    throw Error($"unexpected {t.Describe()}; stages are separated by '|'", t);
                Next();
                stages.Add(ParseStage());
            }
            return new QueryPlan(source, stages);
        }

        private Stage ParseStage()
        {
            var t = Peek();
            if (t.Kind != TokenKind.Ident)
                throw Error($"expected a stage name after '|', found {t.Describe()}", t, "stages: where select sort first skip count get recurse distinct sum min max avg group-by describe set call");
            Next();
            string name = t.Text;
            // Namespaced extension stage: ns.stage-name (no whitespace around dots).
            while (Peek().Is(".") && !Peek().SpaceBefore && Peek(1).Kind == TokenKind.Ident && !Peek(1).SpaceBefore)
            {
                Next();
                name += "." + Next().Text;
            }
            string op = name.IndexOf('.') >= 0 ? name : name.ToLowerInvariant();
            if (StageAliases.TryGetValue(op, out var alias)) op = alias;

            Stage stage;
            switch (op)
            {
                case "where":
                    if (AtStageEnd) throw Error("expected a predicate after 'where'", Peek(), "example: where Health < 20 and Enabled");
                    stage = new WhereStage(ParseExpr());
                    break;
                case "select": stage = ParseSelect(); break;
                case "sort": stage = ParseSort(); break;
                case "first":
                    stage = new LimitStage("first", AtStageEnd ? 1 : ParseCount("first"));
                    break;
                case "skip": stage = new LimitStage("skip", ParseCount("skip")); break;
                case "count":
                case "describe":
                    stage = new SimpleTerminalStage(op);
                    break;
                case "get":
                case "sum":
                case "min":
                case "max":
                case "avg":
                    if (AtStageEnd) throw Error($"expected an expression after '{op}'", Peek());
                    stage = new ExprStage(op, ParsePostfix());
                    break;
                case "group-by":
                    if (AtStageEnd) throw Error($"expected an expression after '{op}'", Peek(), "example: group-by Kind, or group-by Folder, $type");
                    stage = ParseGroupBy(t);
                    break;
                case "distinct":
                    stage = new ExprStage(op, AtStageEnd ? null : ParsePostfix());
                    break;
                case "recurse": stage = ParseRecurse(); break;
                case "set": stage = ParseSet(); break;
                case "call": stage = ParseCallStage(); break;
                default:
                    if (op.IndexOf('.') < 0)
                        throw new RqlException("unknown-stage", $"unknown stage '{name}' at {t.Start}", t.Span,
                            "stages: where select sort first skip count get recurse distinct sum min max avg group-by describe set call; extension stages are namespaced (ns.name)");
                    stage = ParseGeneric(op);
                    break;
            }
            if (!AtStageEnd)
            {
                var bad = Peek();
                throw Error($"unexpected {bad.Describe()} in '{op}' stage", bad, StageHint(op));
            }
            stage.Span = new TextSpan(t.Start, PrevEnd);
            return stage;
        }

        private static string? StageHint(string op) => op switch
        {
            "select" => "select takes paths/aliases: select $ref Name hp=(Health / MaxHealth); wrap complex expressions in parentheses",
            "sort" => "sort Key [asc|desc], Key2 ...; wrap computed keys in parentheses: sort (Mass * Speed) desc",
            "group-by" => "group-by Key[, Key2 ...]; name keys with alias=expr, wrap computed keys in parentheses",
            "first" => "first N",
            "count" => "count takes no arguments; filter first: | where ... | count",
            _ => null,
        };

        private int ParseCount(string op)
        {
            var t = Peek();
            if (t.Kind != TokenKind.Number || !(t.Value is double d) || d < 0 || Math.Floor(d) != d || d > int.MaxValue)
                throw Error($"'{op}' expects a non-negative integer, found {t.Describe()}", t);
            Next();
            return (int)d;
        }

        /// <summary>
        /// group-by keys, written like select fields (spec §5). One unnamed key keeps the {key, count} form (ExprStage);
        /// several keys or a named key give one column per key, then count.
        /// </summary>
        private Stage ParseGroupBy(Token stageToken)
        {
            var keys = ParseFields("group-by", out bool named);
            if (keys.Count == 1 && !named) return new ExprStage("group-by", keys[0].Expr);
            if (GroupByStage.Problem(keys) is string problem) throw Error(problem, stageToken);
            return new GroupByStage(keys);
        }

        private Stage ParseSelect()
        {
            var fields = ParseFields("select", out _);
            if (fields.Count == 0) throw Error("expected at least one field after 'select'", Peek(), "example: select $ref Name Health");
            return new SelectStage(fields);
        }

        private List<SelectField> ParseFields(string op, out bool named)
        {
            var fields = new List<SelectField>();
            named = false;
            while (!AtStageEnd)
            {
                if (fields.Count > 0) Accept(",");
                if (AtStageEnd) break;
                var ft = Peek();
                if (ft.Kind == TokenKind.Punct && !ft.Is("(") && !ft.Is("[") && !ft.Is("-"))
                    throw Error($"unexpected {ft.Describe()} in {op}", ft, op == "select"
                        ? "wrap computed columns in parentheses: select Name ratio=(Health / MaxHealth)"
                        : "wrap computed keys in parentheses: group-by Folder, big=(Mass > 10)");
                string? alias = null;
                if (IsName(Peek()) && Peek(1).Is("="))
                {
                    alias = Next().Text;
                    if (alias.StartsWith("$", StringComparison.Ordinal))
                        throw Error($"column alias '{alias}' may not start with '$' (reserved for meta members)", ft);
                    Next();
                }
                var expr = ParsePostfix();
                if (alias == null && Peek().IsWord("as") && IsName(Peek(1)))
                {
                    Next();
                    alias = Next().Text;
                }
                if (alias != null) named = true;
                var name = alias ?? Slice(expr.Span!.Value.Start, expr.Span!.Value.End);
                fields.Add(new SelectField(name, expr));
            }
            return fields;
        }

        private Stage ParseSort()
        {
            var keys = new List<SortKey>();
            while (!AtStageEnd)
            {
                if (keys.Count > 0) Accept(",");
                if (AtStageEnd) break;
                var kt = Peek();
                if (kt.Kind == TokenKind.Punct && !kt.Is("(") && !kt.Is("[") && !kt.Is("-"))
                    throw Error($"unexpected {kt.Describe()} in sort", kt, "wrap computed sort keys in parentheses: sort (Mass * Speed) desc");
                var e = ParsePostfix();
                bool desc = false;
                if (AcceptWord("desc")) desc = true;
                else AcceptWord("asc");
                keys.Add(new SortKey(e, desc));
            }
            if (keys.Count == 0) throw Error("expected a sort key after 'sort'", Peek(), "example: sort Health desc");
            return new SortStage(keys);
        }

        private Stage ParseRecurse()
        {
            var via = new List<Expr>();
            int? depth = null;
            bool includeSelf = true;
            while (!AtStageEnd)
            {
                Accept(",");
                if (Peek().Kind == TokenKind.Ident && Peek(1).Is("="))
                {
                    var opt = Next();
                    Next();
                    var vt = Peek();
                    var value = ParsePostfix();
                    switch (opt.Text.ToLowerInvariant())
                    {
                        case "max-depth":
                        case "depth":
                            if (!(value is LitExpr l && l.Value is double d && d >= 1 && Math.Floor(d) == d && d <= int.MaxValue))
                                throw Error("max-depth expects a positive integer", vt);
                            depth = (int)(double)((LitExpr)value).Value!;
                            break;
                        case "include-self":
                            if (!(value is LitExpr b && b.Value is bool flag)) throw Error("include-self expects true or false", vt);
                            includeSelf = flag;
                            break;
                        default:
                            throw Error($"unknown recurse option '{opt.Text}'", opt, "options: max-depth=N include-self=false");
                    }
                }
                else
                {
                    via.Add(ParsePostfix());
                }
            }
            return new RecurseStage(via.Count > 0 ? via : null, depth, includeSelf);
        }

        private Stage ParseSet()
        {
            var list = new List<Assignment>();
            while (!AtStageEnd)
            {
                if (list.Count > 0) Accept(",");
                var tt = Peek();
                var target = ParsePostfix();
                if (!(target is PathExpr p) || p.On != null || p.Segments.Count == 0 || p.Segments.Any(s => s.Kind == SegmentKind.Meta))
                    throw Error("set target must be a member path such as Health or Transform.Position.x", tt);
                if (!Accept("=") && !Accept("=="))
                    throw Error($"expected '=' after set target, found {Peek().Describe()}", Peek());
                list.Add(new Assignment(p, ParseExpr()));
            }
            if (list.Count == 0) throw Error("expected an assignment after 'set'", Peek(), "example: set Health = 100, Enabled = true");
            return new SetStage(list);
        }

        private Stage ParseCallStage()
        {
            var t = Peek();
            if (!IsName(t)) throw Error("expected a method name after 'call'", t, "example: call Heal(10)");
            Next();
            var args = Peek().Is("(") ? ParseArgs() : new List<Expr>();
            return new CallStage(t.Text, args);
        }

        private Stage ParseGeneric(string op)
        {
            var args = new List<Expr>();
            var named = new Dictionary<string, Expr>();
            while (!AtStageEnd)
            {
                Accept(",");
                if (Peek().Kind == TokenKind.Ident && Peek(1).Is("="))
                {
                    var key = Next().Text;
                    Next();
                    named[key] = ParsePostfix();
                }
                else args.Add(ParsePostfix());
            }
            return new GenericStage(op, args, named);
        }

        // ------------------------------------------------------------ expressions

        public Expr ParseExpr() => ParseOr();

        /// <summary>Maximum expression nesting; deeper input is rejected instead of overflowing the stack.</summary>
        public const int MaxNesting = 100;
        private int _nesting;
        /// <summary>Operator count cap: left-deep chains (a+a+a…) would otherwise build arbitrarily deep trees.</summary>
        public const int MaxOperators = 1000;
        private int _operators;

        private void CountOperator()
        {
            if (++_operators > MaxOperators)
                throw Error($"more than {MaxOperators} operators in one query", Peek());
        }

        private void Enter()
        {
            if (++_nesting > MaxNesting)
                throw Error($"expression nesting deeper than {MaxNesting} levels", Peek());
        }

        private Expr ParseOr()
        {
            Enter();
            try { return ParseOrCore(); }
            finally { _nesting--; }
        }

        private Expr ParseOrCore()
        {
            int start = Peek().Start;
            var left = ParseAnd();
            while (Peek().IsWord("or") || Peek().Is("||"))
            {
                Next();
                CountOperator();
                var right = ParseAnd();
                left = At(new OpExpr("or", new[] { left, right }), start, PrevEnd);
            }
            return left;
        }

        private Expr ParseAnd()
        {
            int start = Peek().Start;
            var left = ParseNot();
            while (Peek().IsWord("and") || Peek().Is("&&"))
            {
                Next();
                CountOperator();
                var right = ParseNot();
                left = At(new OpExpr("and", new[] { left, right }), start, PrevEnd);
            }
            return left;
        }

        private Expr ParseNot()
        {
            int start = Peek().Start;
            if (Peek().IsWord("not") || Peek().Is("!"))
            {
                Next();
                Enter();
                Expr inner;
                try { inner = ParseNot(); }
                finally { _nesting--; }
                return At(new OpExpr("not", new[] { inner }), start, PrevEnd);
            }
            return ParseComparison();
        }

        private static readonly Dictionary<string, string> CompareOps = new Dictionary<string, string>
        {
            ["=="] = "==", ["="] = "==", ["!="] = "!=", ["<"] = "<", ["<="] = "<=", [">"] = ">", [">="] = ">=",
        };

        private static readonly HashSet<string> WordOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "contains", "starts-with", "ends-with", "matches",
        };

        private Expr ParseComparison()
        {
            int start = Peek().Start;
            var left = ParseAdditive();
            var t = Peek();
            if (t.Kind == TokenKind.Punct && CompareOps.TryGetValue(t.Text, out var op))
            {
                Next();
                var right = ParseAdditive();
                return At(new OpExpr(op, new[] { left, right }), start, PrevEnd);
            }
            if (t.Kind == TokenKind.Ident && WordOps.Contains(t.Text))
            {
                Next();
                var right = ParseAdditive();
                return At(new OpExpr(t.Text.ToLowerInvariant(), new[] { left, right }), start, PrevEnd);
            }
            if (t.IsWord("is"))
            {
                Next();
                bool negate = AcceptWord("not");
                Expr result;
                var tt = Peek();
                if (AcceptWord("null"))
                {
                    result = new OpExpr(negate ? "!=" : "==", new[] { left, At(new LitExpr(null), tt.Start, tt.End) });
                    return At(result, start, PrevEnd);
                }
                string typeName;
                if (tt.Kind == TokenKind.String) typeName = (string)tt.Value!;
                else if (IsName(tt)) typeName = tt.Text;
                else throw Error($"expected 'null' or a type name after 'is', found {tt.Describe()}", tt);
                Next();
                result = At(new OpExpr("is", new[] { left, At(new LitExpr(typeName), tt.Start, tt.End) }), start, PrevEnd);
                return negate ? At(new OpExpr("not", new[] { result }), start, PrevEnd) : result;
            }
            if (t.IsWord("in") || (t.IsWord("not") && Peek(1).IsWord("in")))
            {
                bool negate = t.IsWord("not");
                Next();
                if (negate) Next();
                var right = ParseAdditive();
                Expr result = At(new OpExpr("in", new[] { left, right }), start, PrevEnd);
                return negate ? At(new OpExpr("not", new[] { result }), start, PrevEnd) : result;
            }
            return left;
        }

        private Expr ParseAdditive()
        {
            int start = Peek().Start;
            var left = ParseMultiplicative();
            while (Peek().Is("+") || Peek().Is("-"))
            {
                var op = Next().Text;
                CountOperator();
                var right = ParseMultiplicative();
                left = At(new OpExpr(op, new[] { left, right }), start, PrevEnd);
            }
            return left;
        }

        private Expr ParseMultiplicative()
        {
            int start = Peek().Start;
            var left = ParseUnary();
            while (Peek().Is("*") || Peek().Is("/") || Peek().Is("%"))
            {
                var op = Next().Text;
                CountOperator();
                var right = ParseUnary();
                left = At(new OpExpr(op, new[] { left, right }), start, PrevEnd);
            }
            return left;
        }

        private Expr ParseUnary()
        {
            int start = Peek().Start;
            if (Peek().Is("-"))
            {
                Next();
                Enter();
                Expr inner;
                try { inner = ParseUnary(); }
                finally { _nesting--; }
                if (inner is LitExpr l && l.Value is double d) return At(new LitExpr(-d), start, PrevEnd);
                return At(new OpExpr("neg", new[] { inner }), start, PrevEnd);
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            int start = Peek().Start;
            var expr = ParsePrimary();
            while (true)
            {
                if (Peek().Is("."))
                {
                    Next();
                    var m = Peek();
                    if (m.Kind == TokenKind.Meta)
                    {
                        if (m.Text.Length == 0) throw Error("expected a meta name after '.$'", m);
                        Next();
                        expr = Extend(expr, Segment.Meta(m.Text), start);
                    }
                    else if (m.Kind == TokenKind.QuotedIdent || m.Kind == TokenKind.Ident)
                    {
                        Next();
                        if (m.Kind == TokenKind.Ident && Peek().Is("(") && !Peek().SpaceBefore)
                        {
                            var args = ParseArgs();
                            expr = At(new CallExpr(m.Text, args, expr), start, PrevEnd);
                        }
                        else expr = Extend(expr, Segment.Member(m.Text), start);
                    }
                    else throw Error($"expected a member name after '.', found {m.Describe()}", m);
                }
                else if (Peek().Is("[") && !Peek().SpaceBefore)
                {
                    Next();
                    var inner = ParseExpr();
                    Expect("]", "to close index");
                    if (inner is LitExpr l && l.Value is double d && Math.Floor(d) == d && Math.Abs(d) <= int.MaxValue)
                        expr = Extend(expr, Segment.At((long)d), start);
                    else if (inner is LitExpr ls && ls.Value is string s)
                        expr = Extend(expr, Segment.ForKey(s), start);
                    else
                        expr = At(new OpExpr("index", new[] { expr, inner }), start, PrevEnd);
                }
                else break;
            }
            return expr;
        }

        private Expr Extend(Expr baseExpr, Segment seg, int start)
        {
            if (baseExpr is PathExpr p)
            {
                var segs = new List<Segment>(p.Segments) { seg };
                return At(new PathExpr(segs, p.On), start, PrevEnd);
            }
            return At(new PathExpr(new[] { seg }, baseExpr), start, PrevEnd);
        }

        private List<Expr> ParseArgs()
        {
            Expect("(", "to start arguments");
            var args = new List<Expr>();
            if (Accept(")")) return args;
            do args.Add(ParseExpr());
            while (Accept(","));
            Expect(")", "to close arguments");
            return args;
        }

        private Expr ParsePrimary()
        {
            var t = Peek();
            int start = t.Start;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    Next();
                    return At(new LitExpr((double)t.Value!), start, t.End);
                case TokenKind.String:
                    Next();
                    return At(new LitExpr((string)t.Value!), start, t.End);
                case TokenKind.Meta:
                    Next();
                    return At(new PathExpr(t.Text.Length == 0 ? Array.Empty<Segment>() : new[] { Segment.Meta(t.Text) }), start, t.End);
                case TokenKind.QuotedIdent:
                    Next();
                    return At(new PathExpr(new[] { Segment.Member(t.Text) }), start, t.End);
                case TokenKind.Ident:
                    if (t.IsWord("true")) { Next(); return At(new LitExpr(true), start, t.End); }
                    if (t.IsWord("false")) { Next(); return At(new LitExpr(false), start, t.End); }
                    if (t.IsWord("null")) { Next(); return At(new LitExpr(null), start, t.End); }
                    if (Reserved.Contains(t.Text))
                        throw Error($"unexpected keyword '{t.Text}'", t, "use backticks to address a member named like a keyword: `in`");
                    Next();
                    if (Peek().Is("(") && !Peek().SpaceBefore)
                    {
                        var args = ParseArgs();
                        return At(new CallExpr(t.Text, args), start, PrevEnd);
                    }
                    return At(new PathExpr(new[] { Segment.Member(t.Text) }), start, t.End);
                case TokenKind.Punct:
                    if (t.Is("("))
                    {
                        Next();
                        var inner = ParseExpr();
                        if (Peek().Is(","))
                            throw Error($"expected ')' to close '(', found {Peek().Describe()}", Peek(),
                                "parentheses group one expression, they are not a tuple: write a struct as an object literal {X: 0, Y: 0} or a list [0, 0]");
                        Expect(")", "to close '('");
                        inner.Span = new TextSpan(start, PrevEnd);
                        return inner;
                    }
                    if (t.Is("["))
                    {
                        Next();
                        var items = new List<Expr>();
                        if (!Accept("]"))
                        {
                            do items.Add(ParseExpr());
                            while (Accept(","));
                            Expect("]", "to close list");
                        }
                        return At(new ListExpr(items), start, PrevEnd);
                    }
                    if (t.Is("{"))
                    {
                        Next();
                        var entries = new List<KeyValuePair<string, Expr>>();
                        var keys = new HashSet<string>(StringComparer.Ordinal);
                        if (!Accept("}"))
                        {
                            do
                            {
                                var k = Peek();
                                // `$name` keys (`{$type: "PlaneMesh"}`) read like their quoted form (spec §4.5).
                                bool metaKey = k.Kind == TokenKind.Meta && k.Text.Length > 0;
                                if (k.Kind != TokenKind.Ident && k.Kind != TokenKind.QuotedIdent && k.Kind != TokenKind.String && !metaKey)
                                    throw Error($"expected an object key (name, $name or string), found {k.Describe()}", k, "keys are names or strings: {Name: \"x\", \"$type\": \"T\"}");
                                Next();
                                var key = k.Kind == TokenKind.String ? (string)k.Value! : metaKey ? "$" + k.Text : k.Text;
                                if (key.Length == 0) throw Error("object keys must not be empty", k);
                                if (!keys.Add(key)) throw Error($"duplicate object key '{key}'", k);
                                Expect(":", "after an object key");
                                entries.Add(new KeyValuePair<string, Expr>(key, ParseExpr()));
                            }
                            while (Accept(","));
                            Expect("}", "to close the object");
                        }
                        return At(new ObjectExpr(entries), start, PrevEnd);
                    }
                    break;
            }
            throw Error($"expected an expression, found {t.Describe()}", t);
        }
    }
}
