/**
 * Syntax-only RQL parser: text → canonical QueryPlan (spec/grammar.ebnf). Knows no domain types,
 * sources or extension names. Port of dotnet/Rql.Parser/RqlParser.cs; produces identical plans.
 */
import {
  RqlError,
  groupByProblem,
  validateStructure,
  type Assignment,
  type Diagnostic,
  type Expr,
  type ExprOp,
  type ObjectEntry,
  type PathExpr,
  type QueryPlan,
  type Segment,
  type SelectField,
  type SortKey,
  type Stage,
  RQL_VERSION,
} from '@rql/contracts';
import { describeToken, isPunct, isWord, spanOf, tokenize, trimDotnet, type Token } from './lexer.js';

const RESERVED: ReadonlySet<string> = new Set(['and', 'or', 'not', 'in', 'is', 'null', 'true', 'false']);
const STAGE_ALIASES: ReadonlyMap<string, string> = new Map([['limit', 'first']]);
const COMPARE_OPS: ReadonlyMap<string, ExprOp> = new Map<string, ExprOp>([
  ['==', '=='], ['=', '=='], ['!=', '!='], ['<', '<'], ['<=', '<='], ['>', '>'], ['>=', '>='],
]);
const WORD_OPS: ReadonlySet<string> = new Set(['contains', 'starts-with', 'ends-with', 'matches']);

/** Maximum expression nesting (spec §11 reference value). */
export const MAX_NESTING = 100;
/** Maximum binary operators per query (spec §11 reference value). */
export const MAX_OPERATORS = 1000;

const STAGE_LIST = 'stages: where select sort first skip count get recurse distinct sum min max avg group-by describe set call';

/** Parses or throws {@link RqlError} (syntax-error / unknown-stage / invalid-plan). */
export function parse(text: string): QueryPlan {
  let plan: QueryPlan;
  try {
    plan = new Parser(text).parseQuery();
  } catch (e) {
    const line = e instanceof RqlError && e.code === 'syntax-error' ? secondQueryLine(text, e.at?.start ?? 0) : undefined;
    if (line === undefined) throw e;
    // Two queries pasted into one call: say so instead of only reporting the token where the second one began.
    const err = e as RqlError;
    const hint = `one query per call: line ${line} starts another query; run each query in its own call`;
    throw new RqlError(err.code, err.message, err.at, err.hint === undefined ? hint : `${hint}; ${err.hint}`);
  }
  validateStructure(plan);
  return plan;
}

const QUERY_START = /^\s*[A-Za-z_][\w.-]*\s*(\(.*\))?\s*\|/;

/**
 * The 1-based number of the first line, starting at or before `errorAt`, that starts a new query (a name, optionally a
 * call, then '|') after a non-blank line that does not end in '|'; undefined when there is none.
 */
export function secondQueryLine(text: string, errorAt: number): number | undefined {
  const lines = text.split('\n');
  let offset = 0;
  let prev: string | undefined;
  for (let i = 0; i < lines.length && offset <= errorAt; i++) {
    const line = lines[i]!;
    if (prev !== undefined && !prev.trimEnd().endsWith('|') && QUERY_START.test(line)) return i + 1;
    if (line.trim().length > 0) prev = line;
    offset += line.length + 1;
  }
  return undefined;
}

export type ParseOutcome = { ok: true; plan: QueryPlan } | { ok: false; error: Diagnostic };

export function tryParse(text: string): ParseOutcome {
  try {
    return { ok: true, plan: parse(text) };
  } catch (e) {
    if (e instanceof RqlError) return { ok: false, error: e.toDiagnostic() };
    throw e;
  }
}

function at<T extends Expr>(node: T, start: number, end: number): T {
  node.span = { start, end };
  return node;
}

class Parser {
  private readonly tokens: Token[];
  private pos = 0;
  private nesting = 0;
  private operators = 0;

  constructor(private readonly text: string) {
    this.tokens = tokenize(text);
  }

  // ---------------------------------------------------------------- helpers

  private peek(ahead = 0): Token {
    return this.tokens[Math.min(this.pos + ahead, this.tokens.length - 1)]!;
  }

  private next(): Token {
    const t = this.tokens[this.pos]!;
    if (this.pos < this.tokens.length - 1) this.pos++;
    return t;
  }

  private get atStageEnd(): boolean {
    const t = this.peek();
    return t.kind === 'end' || isPunct(t, '|');
  }

  private accept(punct: string): boolean {
    if (!isPunct(this.peek(), punct)) return false;
    this.pos++;
    return true;
  }

  private acceptWord(word: string): boolean {
    if (!isWord(this.peek(), word)) return false;
    this.pos++;
    return true;
  }

  private expect(punct: string, context: string): Token {
    const t = this.peek();
    if (!isPunct(t, punct)) throw this.error(`expected '${punct}' ${context}, found ${describeToken(t)}`, t);
    return this.next();
  }

  private error(message: string, t: Token, hint?: string): RqlError {
    return new RqlError('syntax-error', `${message} at ${t.start}`, spanOf(t), hint);
  }

  private get prevEnd(): number {
    return this.pos > 0 ? this.tokens[this.pos - 1]!.end : 0;
  }

  private isName(t: Token): boolean {
    return t.kind === 'quoted' || (t.kind === 'ident' && !RESERVED.has(t.text.toLowerCase()));
  }

  // ---------------------------------------------------------------- query / stages

  parseQuery(): QueryPlan {
    if (this.peek().kind === 'end') {
      throw this.error("empty query; expected a source name, ref(\"Type#id\") or a host function", this.peek());
    }
    const source = this.parsePostfix();
    const stages: Stage[] = [];
    for (;;) {
      const t = this.peek();
      if (t.kind === 'end') break;
      if (!isPunct(t, '|')) throw this.error(`unexpected ${describeToken(t)}; stages are separated by '|'`, t);
      this.next();
      stages.push(this.parseStage());
    }
    return { version: RQL_VERSION, source, stages };
  }

  private parseStage(): Stage {
    const t = this.peek();
    if (t.kind !== 'ident') throw this.error(`expected a stage name after '|', found ${describeToken(t)}`, t, STAGE_LIST);
    this.next();
    let name = t.text;
    // Namespaced extension stage: ns.stage-name (no whitespace around dots).
    while (isPunct(this.peek(), '.') && !this.peek().spaceBefore && this.peek(1).kind === 'ident' && !this.peek(1).spaceBefore) {
      this.next();
      name += '.' + this.next().text;
    }
    let op = name.includes('.') ? name : name.toLowerCase();
    op = STAGE_ALIASES.get(op) ?? op;

    let stage: Stage;
    switch (op) {
      case 'where':
        if (this.atStageEnd) throw this.error("expected a predicate after 'where'", this.peek(), 'example: where Health < 20 and Enabled');
        stage = { kind: 'where', op, pred: this.parseExpr() };
        break;
      case 'select':
        stage = this.parseSelect();
        break;
      case 'sort':
        stage = this.parseSort();
        break;
      case 'first':
        stage = { kind: 'limit', op, n: this.atStageEnd ? 1 : this.parseCount(op) };
        break;
      case 'skip':
        stage = { kind: 'limit', op, n: this.parseCount(op) };
        break;
      case 'count':
      case 'describe':
        stage = { kind: 'simple', op };
        break;
      case 'get':
      case 'sum':
      case 'min':
      case 'max':
      case 'avg':
        if (this.atStageEnd) throw this.error(`expected an expression after '${op}'`, this.peek());
        stage = { kind: 'expr', op, expr: this.parsePostfix() };
        break;
      case 'group-by':
        if (this.atStageEnd) throw this.error(`expected an expression after '${op}'`, this.peek(), 'example: group-by Kind, or group-by Folder, $type');
        stage = this.parseGroupBy(t);
        break;
      case 'distinct':
        stage = this.atStageEnd ? { kind: 'expr', op } : { kind: 'expr', op, expr: this.parsePostfix() };
        break;
      case 'recurse':
        stage = this.parseRecurse();
        break;
      case 'set':
        stage = this.parseSet();
        break;
      case 'call':
        stage = this.parseCallStage();
        break;
      default:
        if (!op.includes('.')) {
          throw new RqlError(
            'unknown-stage',
            `unknown stage '${name}' at ${t.start}`,
            spanOf(t),
            STAGE_LIST + '; extension stages are namespaced (ns.name)',
          );
        }
        stage = this.parseGeneric(op);
        break;
    }
    if (!this.atStageEnd) {
      const bad = this.peek();
      throw this.error(`unexpected ${describeToken(bad)} in '${op}' stage`, bad, stageHint(op));
    }
    stage.span = { start: t.start, end: this.prevEnd };
    return stage;
  }

  private parseCount(op: string): number {
    const t = this.peek();
    const d = t.value;
    if (t.kind !== 'number' || typeof d !== 'number' || d < 0 || Math.floor(d) !== d || d > 2147483647) {
      throw this.error(`'${op}' expects a non-negative integer, found ${describeToken(t)}`, t);
    }
    this.next();
    return d;
  }

  /**
   * group-by keys, written like select fields (spec §5). One unnamed key keeps the {key, count} form (ExprStage);
   * several keys or a named key give one column per key, then count.
   */
  private parseGroupBy(stageToken: Token): Stage {
    const { fields: keys, named } = this.parseFields('group-by');
    if (keys.length === 1 && !named) return { kind: 'expr', op: 'group-by', expr: keys[0]!.expr };
    const problem = groupByProblem(keys);
    if (problem !== undefined) throw this.error(problem, stageToken);
    return { kind: 'group-by', op: 'group-by', keys };
  }

  private parseSelect(): Stage {
    const { fields } = this.parseFields('select');
    if (fields.length === 0) throw this.error("expected at least one field after 'select'", this.peek(), 'example: select $ref Name Health');
    return { kind: 'select', op: 'select', fields };
  }

  private parseFields(op: string): { fields: SelectField[]; named: boolean } {
    const fields: SelectField[] = [];
    let named = false;
    while (!this.atStageEnd) {
      if (fields.length > 0) this.accept(',');
      if (this.atStageEnd) break;
      const ft = this.peek();
      if (ft.kind === 'punct' && !isPunct(ft, '(') && !isPunct(ft, '[') && !isPunct(ft, '-')) {
        throw this.error(
          `unexpected ${describeToken(ft)} in ${op}`,
          ft,
          op === 'select' ? 'wrap computed columns in parentheses: select Name ratio=(Health / MaxHealth)' : 'wrap computed keys in parentheses: group-by Folder, big=(Mass > 10)',
        );
      }
      let alias: string | undefined;
      if (this.isName(this.peek()) && isPunct(this.peek(1), '=')) {
        alias = this.next().text;
        if (alias.startsWith('$')) throw this.error(`column alias '${alias}' may not start with '$' (reserved for meta members)`, ft);
        this.next();
      }
      const expr = this.parsePostfix();
      if (alias === undefined && isWord(this.peek(), 'as') && this.isName(this.peek(1))) {
        this.next();
        alias = this.next().text;
      }
      if (alias !== undefined) named = true;
      const span = expr.span!;
      fields.push({ name: alias ?? trimDotnet(this.text.slice(span.start, span.end)), expr });
    }
    return { fields, named };
  }

  private parseSort(): Stage {
    const keys: SortKey[] = [];
    while (!this.atStageEnd) {
      if (keys.length > 0) this.accept(',');
      if (this.atStageEnd) break;
      const kt = this.peek();
      if (kt.kind === 'punct' && !isPunct(kt, '(') && !isPunct(kt, '[') && !isPunct(kt, '-')) {
        throw this.error(`unexpected ${describeToken(kt)} in sort`, kt, 'wrap computed sort keys in parentheses: sort (Mass * Speed) desc');
      }
      const expr = this.parsePostfix();
      let desc = false;
      if (this.acceptWord('desc')) desc = true;
      else this.acceptWord('asc');
      keys.push({ expr, desc });
    }
    if (keys.length === 0) throw this.error("expected a sort key after 'sort'", this.peek(), 'example: sort Health desc');
    return { kind: 'sort', op: 'sort', keys };
  }

  private parseRecurse(): Stage {
    const via: Expr[] = [];
    let maxDepth: number | undefined;
    let includeSelf = true;
    while (!this.atStageEnd) {
      this.accept(',');
      if (this.peek().kind === 'ident' && isPunct(this.peek(1), '=')) {
        const opt = this.next();
        this.next();
        const vt = this.peek();
        const value = this.parsePostfix();
        switch (opt.text.toLowerCase()) {
          case 'max-depth':
          case 'depth': {
            const d = value.kind === 'lit' ? value.value : undefined;
            if (typeof d !== 'number' || d < 1 || Math.floor(d) !== d || d > 2147483647) {
              throw this.error('max-depth expects a positive integer', vt);
            }
            maxDepth = d;
            break;
          }
          case 'include-self': {
            const b = value.kind === 'lit' ? value.value : undefined;
            if (typeof b !== 'boolean') throw this.error('include-self expects true or false', vt);
            includeSelf = b;
            break;
          }
          default:
            throw this.error(`unknown recurse option '${opt.text}'`, opt, 'options: max-depth=N include-self=false');
        }
      } else {
        via.push(this.parsePostfix());
      }
    }
    return {
      kind: 'recurse',
      op: 'recurse',
      includeSelf,
      ...(via.length > 0 ? { via } : {}),
      ...(maxDepth !== undefined ? { maxDepth } : {}),
    };
  }

  private parseSet(): Stage {
    const assign: Assignment[] = [];
    while (!this.atStageEnd) {
      if (assign.length > 0) this.accept(',');
      const tt = this.peek();
      const target = this.parsePostfix();
      if (target.kind !== 'path' || target.on !== undefined || target.segments.length === 0 || target.segments.some((s) => s.kind === 'meta')) {
        throw this.error('set target must be a member path such as Health or Transform.Position.x', tt);
      }
      if (!this.accept('=') && !this.accept('==')) {
        throw this.error(`expected '=' after set target, found ${describeToken(this.peek())}`, this.peek());
      }
      assign.push({ target, value: this.parseExpr() });
    }
    if (assign.length === 0) throw this.error("expected an assignment after 'set'", this.peek(), 'example: set Health = 100, Enabled = true');
    return { kind: 'set', op: 'set', assign };
  }

  private parseCallStage(): Stage {
    const t = this.peek();
    if (!this.isName(t)) throw this.error("expected a method name after 'call'", t, 'example: call Heal(10)');
    this.next();
    const args = isPunct(this.peek(), '(') ? this.parseArgs() : [];
    return { kind: 'call', op: 'call', method: t.text, args };
  }

  private parseGeneric(op: string): Stage {
    const args: Expr[] = [];
    const named = new Map<string, Expr>();
    while (!this.atStageEnd) {
      this.accept(',');
      if (this.peek().kind === 'ident' && isPunct(this.peek(1), '=')) {
        const key = this.next().text;
        this.next();
        named.set(key, this.parsePostfix());
      } else {
        args.push(this.parsePostfix());
      }
    }
    return { kind: 'generic', op, args, named };
  }

  // ---------------------------------------------------------------- expressions

  parseExpr(): Expr {
    return this.parseConditional();
  }

  /** `cond ? a : b` (lowest precedence, right-associative), canonicalized to the core function `if(cond, a, b)`. */
  private parseConditional(): Expr {
    const start = this.peek().start;
    const cond = this.parseOr();
    if (!isPunct(this.peek(), '?')) return cond;
    this.next();
    this.countOperator();
    return this.nested(() => {
      const then = this.parseExpr();
      if (!isPunct(this.peek(), ':')) {
        throw this.error(
          `expected ':' after the then-branch of '?', found ${describeToken(this.peek())}`,
          this.peek(),
          'a conditional is cond ? then : else (or if(cond, then, else))',
        );
      }
      this.next();
      const otherwise = this.parseExpr();
      return at({ kind: 'call', name: 'if', args: [cond, then, otherwise] }, start, this.prevEnd);
    });
  }

  /** Counts a binary operator: left-deep chains (a+a+a…) would otherwise build arbitrarily deep trees. */
  private countOperator(): void {
    if (++this.operators > MAX_OPERATORS) throw this.error(`more than ${MAX_OPERATORS} operators in one query`, this.peek());
  }

  /** Runs `f` one nesting level deeper; deeper input is rejected instead of overflowing the stack. */
  private nested<T>(f: () => T): T {
    if (++this.nesting > MAX_NESTING) throw this.error(`expression nesting deeper than ${MAX_NESTING} levels`, this.peek());
    try {
      return f();
    } finally {
      this.nesting--;
    }
  }

  private parseOr(): Expr {
    return this.nested(() => this.parseOrCore());
  }

  private parseOrCore(): Expr {
    const start = this.peek().start;
    let left = this.parseAnd();
    while (isWord(this.peek(), 'or') || isPunct(this.peek(), '||')) {
      this.next();
      this.countOperator();
      const right = this.parseAnd();
      left = at({ kind: 'op', op: 'or', args: [left, right] }, start, this.prevEnd);
    }
    return left;
  }

  private parseAnd(): Expr {
    const start = this.peek().start;
    let left = this.parseNot();
    while (isWord(this.peek(), 'and') || isPunct(this.peek(), '&&')) {
      this.next();
      this.countOperator();
      const right = this.parseNot();
      left = at({ kind: 'op', op: 'and', args: [left, right] }, start, this.prevEnd);
    }
    return left;
  }

  private parseNot(): Expr {
    const start = this.peek().start;
    if (isWord(this.peek(), 'not') || isPunct(this.peek(), '!')) {
      this.next();
      const inner = this.nested(() => this.parseNot());
      return at({ kind: 'op', op: 'not', args: [inner] }, start, this.prevEnd);
    }
    return this.parseComparison();
  }

  /** A comparison followed by another comparison operator: they do not chain (spec §11), so say how to write it. */
  private parseComparison(): Expr {
    const start = this.peek().start;
    const e = this.parseComparisonCore();
    const t = this.peek();
    if (this.isComparisonOperator(t)) {
      const left = trimDotnet(this.text.slice(start, this.prevEnd));
      throw this.error(
        `unexpected ${describeToken(t)}: comparisons do not chain`,
        t,
        `contains / starts-with / ends-with / matches / in / is and == < > … take one operand on each side; to negate write not (${left}), to combine use and / or`,
      );
    }
    return e;
  }

  private isComparisonOperator(t: Token): boolean {
    if (t.kind === 'punct') return COMPARE_OPS.has(t.text);
    return t.kind === 'ident' && (WORD_OPS.has(t.text.toLowerCase()) || isWord(t, 'is') || isWord(t, 'in') || (isWord(t, 'not') && isWord(this.peek(1), 'in')));
  }

  private parseComparisonCore(): Expr {
    const start = this.peek().start;
    const left = this.parseAdditive();
    const t = this.peek();
    const cmp = t.kind === 'punct' ? COMPARE_OPS.get(t.text) : undefined;
    if (cmp !== undefined) {
      this.next();
      const right = this.parseAdditive();
      return at({ kind: 'op', op: cmp, args: [left, right] }, start, this.prevEnd);
    }
    if (t.kind === 'ident' && WORD_OPS.has(t.text.toLowerCase())) {
      this.next();
      const right = this.parseAdditive();
      return at({ kind: 'op', op: t.text.toLowerCase() as ExprOp, args: [left, right] }, start, this.prevEnd);
    }
    if (isWord(t, 'is')) {
      this.next();
      const negate = this.acceptWord('not');
      const tt = this.peek();
      if (this.acceptWord('null')) {
        const nul = at({ kind: 'lit', value: null }, tt.start, tt.end);
        return at({ kind: 'op', op: negate ? '!=' : '==', args: [left, nul] }, start, this.prevEnd);
      }
      let typeName: string;
      if (tt.kind === 'string') typeName = tt.value as string;
      else if (this.isName(tt)) typeName = tt.text;
      else throw this.error(`expected 'null' or a type name after 'is', found ${describeToken(tt)}`, tt);
      this.next();
      const result = at<Expr>({ kind: 'op', op: 'is', args: [left, at({ kind: 'lit', value: typeName }, tt.start, tt.end)] }, start, this.prevEnd);
      return negate ? at({ kind: 'op', op: 'not', args: [result] }, start, this.prevEnd) : result;
    }
    if (isWord(t, 'in') || (isWord(t, 'not') && isWord(this.peek(1), 'in'))) {
      const negate = isWord(t, 'not');
      this.next();
      if (negate) this.next();
      const right = this.parseAdditive();
      const result = at<Expr>({ kind: 'op', op: 'in', args: [left, right] }, start, this.prevEnd);
      return negate ? at({ kind: 'op', op: 'not', args: [result] }, start, this.prevEnd) : result;
    }
    return left;
  }

  private parseAdditive(): Expr {
    const start = this.peek().start;
    let left = this.parseMultiplicative();
    while (isPunct(this.peek(), '+') || isPunct(this.peek(), '-')) {
      const o = this.next().text as ExprOp;
      this.countOperator();
      const right = this.parseMultiplicative();
      left = at({ kind: 'op', op: o, args: [left, right] }, start, this.prevEnd);
    }
    return left;
  }

  private parseMultiplicative(): Expr {
    const start = this.peek().start;
    let left = this.parseUnary();
    while (isPunct(this.peek(), '*') || isPunct(this.peek(), '/') || isPunct(this.peek(), '%')) {
      const o = this.next().text as ExprOp;
      this.countOperator();
      const right = this.parseUnary();
      left = at({ kind: 'op', op: o, args: [left, right] }, start, this.prevEnd);
    }
    return left;
  }

  private parseUnary(): Expr {
    const start = this.peek().start;
    if (isPunct(this.peek(), '-')) {
      this.next();
      const inner = this.nested(() => this.parseUnary());
      if (inner.kind === 'lit' && typeof inner.value === 'number') return at({ kind: 'lit', value: -inner.value }, start, this.prevEnd);
      return at({ kind: 'op', op: 'neg', args: [inner] }, start, this.prevEnd);
    }
    return this.parsePostfix();
  }

  private parsePostfix(): Expr {
    const start = this.peek().start;
    let expr = this.parsePrimary();
    for (;;) {
      if (isPunct(this.peek(), '.')) {
        this.next();
        const m = this.peek();
        if (m.kind === 'meta') {
          if (m.text.length === 0) throw this.error("expected a meta name after '.$'", m);
          this.next();
          expr = this.extend(expr, { kind: 'meta', name: m.text }, start);
        } else if (m.kind === 'quoted' || m.kind === 'ident') {
          this.next();
          if (m.kind === 'ident' && isPunct(this.peek(), '(') && !this.peek().spaceBefore) {
            const args = this.parseArgs();
            expr = at({ kind: 'call', name: m.text, args, on: expr }, start, this.prevEnd);
          } else {
            expr = this.extend(expr, { kind: 'member', name: m.text }, start);
          }
        } else {
          throw this.error(`expected a member name after '.', found ${describeToken(m)}`, m);
        }
      } else if (isPunct(this.peek(), '[') && !this.peek().spaceBefore) {
        this.next();
        const inner = this.parseExpr();
        this.expect(']', 'to close index');
        if (inner.kind === 'lit' && typeof inner.value === 'number' && Math.floor(inner.value) === inner.value && Math.abs(inner.value) <= 2147483647) {
          expr = this.extend(expr, { kind: 'index', index: inner.value }, start);
        } else if (inner.kind === 'lit' && typeof inner.value === 'string') {
          expr = this.extend(expr, { kind: 'key', key: inner.value }, start);
        } else {
          expr = at({ kind: 'op', op: 'index', args: [expr, inner] }, start, this.prevEnd);
        }
      } else {
        break;
      }
    }
    return expr;
  }

  private extend(base: Expr, s: Segment, start: number): PathExpr {
    if (base.kind === 'path') {
      const segments = [...base.segments, s];
      return at(base.on !== undefined ? { kind: 'path', segments, on: base.on } : { kind: 'path', segments }, start, this.prevEnd);
    }
    return at({ kind: 'path', segments: [s], on: base }, start, this.prevEnd);
  }

  private parseArgs(): Expr[] {
    this.expect('(', 'to start arguments');
    const args: Expr[] = [];
    if (this.accept(')')) return args;
    do args.push(this.parseExpr());
    while (this.accept(','));
    this.expect(')', 'to close arguments');
    return args;
  }

  private parsePrimary(): Expr {
    const t = this.peek();
    const start = t.start;
    switch (t.kind) {
      case 'number':
        this.next();
        return at({ kind: 'lit', value: t.value as number }, start, t.end);
      case 'string':
        this.next();
        return at({ kind: 'lit', value: t.value as string }, start, t.end);
      case 'meta':
        this.next();
        return at({ kind: 'path', segments: t.text.length === 0 ? [] : [{ kind: 'meta', name: t.text }] }, start, t.end);
      case 'quoted':
        this.next();
        return at({ kind: 'path', segments: [{ kind: 'member', name: t.text }] }, start, t.end);
      case 'ident': {
        if (isWord(t, 'true')) {
          this.next();
          return at({ kind: 'lit', value: true }, start, t.end);
        }
        if (isWord(t, 'false')) {
          this.next();
          return at({ kind: 'lit', value: false }, start, t.end);
        }
        if (isWord(t, 'null')) {
          this.next();
          return at({ kind: 'lit', value: null }, start, t.end);
        }
        if (RESERVED.has(t.text.toLowerCase())) {
          throw this.error(`unexpected keyword '${t.text}'`, t, 'use backticks to address a member named like a keyword: `in`');
        }
        this.next();
        if (isPunct(this.peek(), '(') && !this.peek().spaceBefore) {
          const args = this.parseArgs();
          return at({ kind: 'call', name: t.text, args }, start, this.prevEnd);
        }
        return at({ kind: 'path', segments: [{ kind: 'member', name: t.text }] }, start, t.end);
      }
      case 'punct':
        if (isPunct(t, '(')) {
          this.next();
          const inner = this.parseExpr();
          if (isPunct(this.peek(), ',')) {
            throw this.error(
              `expected ')' to close '(', found ${describeToken(this.peek())}`,
              this.peek(),
              'parentheses group one expression, they are not a tuple: write a struct as an object literal {X: 0, Y: 0} or a list [0, 0]',
            );
          }
          this.expect(')', "to close '('");
          inner.span = { start, end: this.prevEnd };
          return inner;
        }
        if (isPunct(t, '[')) {
          this.next();
          const items: Expr[] = [];
          if (!this.accept(']')) {
            do items.push(this.parseExpr());
            while (this.accept(','));
            this.expect(']', 'to close list');
          }
          return at({ kind: 'list', items }, start, this.prevEnd);
        }
        if (isPunct(t, '{')) {
          this.next();
          const entries: ObjectEntry[] = [];
          const keys = new Set<string>();
          if (!this.accept('}')) {
            do {
              const k = this.peek();
              // `$name` keys (`{$type: "PlaneMesh"}`) read like their quoted form (spec §4.5).
              const metaKey = k.kind === 'meta' && k.text.length > 0;
              if (k.kind !== 'ident' && k.kind !== 'quoted' && k.kind !== 'string' && !metaKey) {
                throw this.error(`expected an object key (name, $name or string), found ${describeToken(k)}`, k, 'keys are names or strings: {Name: "x", "$type": "T"}');
              }
              this.next();
              const key = k.kind === 'string' ? (k.value as string) : metaKey ? '$' + k.text : k.text;
              if (key.length === 0) throw this.error('object keys must not be empty', k);
              if (keys.has(key)) throw this.error(`duplicate object key '${key}'`, k);
              keys.add(key);
              this.expect(':', 'after an object key');
              entries.push({ key, value: this.parseExpr() });
            } while (this.accept(','));
            this.expect('}', 'to close the object');
          }
          return at({ kind: 'object', entries }, start, this.prevEnd);
        }
        break;
      default:
        break;
    }
    throw this.error(`expected an expression, found ${describeToken(t)}`, t);
  }
}

function stageHint(op: string): string | undefined {
  switch (op) {
    case 'select':
      return 'select takes paths/aliases: select $ref Name hp=(Health / MaxHealth); wrap complex expressions in parentheses';
    case 'sort':
      return 'sort Key [asc|desc], Key2 ...; wrap computed keys in parentheses: sort (Mass * Speed) desc';
    case 'group-by':
      return 'group-by Key[, Key2 ...]; name keys with alias=expr, wrap computed keys in parentheses';
    case 'first':
      return 'first N';
    case 'count':
      return 'count takes no arguments; filter first: | where ... | count';
    default:
      return undefined;
  }
}
