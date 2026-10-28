/**
 * QueryPlan v0.1 — the language- and transport-neutral IR of an RQL query
 * (spec/query-plan.schema.json). Mirrors dotnet/Rql.Contracts/Plan.cs.
 */

export const RQL_VERSION = '0.1';

/** Source span in the query text (UTF-16 offsets), set by the parser; never serialized. */
export interface TextSpan {
  readonly start: number;
  readonly end: number;
}

// ------------------------------------------------------------------ expressions

export type LiteralValue = null | boolean | number | string;

export interface LitExpr {
  readonly kind: 'lit';
  readonly value: LiteralValue;
  span?: TextSpan;
}

export interface ListExpr {
  readonly kind: 'list';
  readonly items: readonly Expr[];
  span?: TextSpan;
}

/** One `key: value` entry of an object literal. */
export interface ObjectEntry {
  readonly key: string;
  readonly value: Expr;
}

/** Object literal `{ key: expr, … }`: an ordered string-keyed map; keys are unique and non-empty (spec §4.5). */
export interface ObjectExpr {
  readonly kind: 'object';
  readonly entries: readonly ObjectEntry[];
  span?: TextSpan;
}

/** Navigation from the current item, or from `on`. Empty path = the item itself. */
export interface PathExpr {
  readonly kind: 'path';
  readonly segments: readonly Segment[];
  readonly on?: Expr;
  span?: TextSpan;
}

export const EXPR_OPS = [
  'or', 'and', 'not', '==', '!=', '<', '<=', '>', '>=', 'contains', 'starts-with', 'ends-with',
  'matches', 'in', 'is', 'neg', '+', '-', '*', '/', '%', 'index',
] as const;

export type ExprOp = (typeof EXPR_OPS)[number];

const EXPR_OP_SET: ReadonlySet<string> = new Set(EXPR_OPS);

export function isExprOp(op: string): op is ExprOp {
  return EXPR_OP_SET.has(op);
}

export interface OpExpr {
  readonly kind: 'op';
  readonly op: ExprOp;
  readonly args: readonly Expr[];
  span?: TextSpan;
}

/** Function call (no `on`) or method call on the value of `on`. */
export interface CallExpr {
  readonly kind: 'call';
  readonly name: string;
  readonly args: readonly Expr[];
  readonly on?: Expr;
  span?: TextSpan;
}

export type Expr = LitExpr | ListExpr | ObjectExpr | PathExpr | OpExpr | CallExpr;

export type Segment =
  | { readonly kind: 'member'; readonly name: string }
  | { readonly kind: 'index'; readonly index: number }
  | { readonly kind: 'key'; readonly key: string | number }
  /** Meta member name without the leading `$`. */
  | { readonly kind: 'meta'; readonly name: string };

// ------------------------------------------------------------------ stages

interface StageBase {
  span?: TextSpan;
}

export interface WhereStage extends StageBase {
  readonly kind: 'where';
  readonly op: 'where';
  readonly pred: Expr;
}

export interface SelectField {
  readonly name: string;
  readonly expr: Expr;
}

export interface SelectStage extends StageBase {
  readonly kind: 'select';
  readonly op: 'select';
  readonly fields: readonly SelectField[];
}

/**
 * group-by with several keys or named keys (spec §5): rows with one column per key, then `count`. A single unnamed key
 * stays an `ExprStage` (columns `key`, `count`).
 */
export interface GroupByStage extends StageBase {
  readonly kind: 'group-by';
  readonly op: 'group-by';
  readonly keys: readonly SelectField[];
}

/** Why these keys cannot form group-by columns (duplicate names, or a key named `count`), or undefined. */
export function groupByProblem(keys: readonly SelectField[]): string | undefined {
  const seen = new Set<string>(['COUNT']);
  for (const k of keys) {
    const upper = k.name.toUpperCase();
    if (seen.has(upper)) {
      return upper === 'COUNT'
        ? "group-by key column 'count' clashes with the count column; name the key: group-by n=count"
        : `group-by key column '${k.name}' appears twice; name the keys: group-by a=..., b=...`;
    }
    seen.add(upper);
  }
  return undefined;
}

export interface SortKey {
  readonly expr: Expr;
  readonly desc: boolean;
}

export interface SortStage extends StageBase {
  readonly kind: 'sort';
  readonly op: 'sort';
  readonly keys: readonly SortKey[];
}

/** `first` / `skip`. */
export interface LimitStage extends StageBase {
  readonly kind: 'limit';
  readonly op: 'first' | 'skip';
  readonly n: number;
}

/** `count` / `describe` (no arguments, terminal). */
export interface SimpleTerminalStage extends StageBase {
  readonly kind: 'simple';
  readonly op: 'count' | 'describe';
}

export type ExprStageOp = 'get' | 'sum' | 'min' | 'max' | 'avg' | 'group-by' | 'distinct';

/** get / distinct / group-by (streaming) and sum / min / max / avg (terminal). `expr` is optional only for distinct. */
export interface ExprStage extends StageBase {
  readonly kind: 'expr';
  readonly op: ExprStageOp;
  readonly expr?: Expr;
}

export interface RecurseStage extends StageBase {
  readonly kind: 'recurse';
  readonly op: 'recurse';
  readonly via?: readonly Expr[];
  readonly maxDepth?: number;
  /** Emit the input items themselves (depth 0). Default true; serialized only when false. */
  readonly includeSelf: boolean;
}

export interface Assignment {
  readonly target: PathExpr;
  readonly value: Expr;
}

export interface SetStage extends StageBase {
  readonly kind: 'set';
  readonly op: 'set';
  readonly assign: readonly Assignment[];
}

export interface CallStage extends StageBase {
  readonly kind: 'call';
  readonly op: 'call';
  readonly method: string;
  readonly args: readonly Expr[];
  /**
   * The receiver of a source-qualified call (`call editor.Create(…)`, spec §8): a source expression evaluated once, whose
   * single object the method runs on once per stream item. Absent: the method runs on each item.
   */
  readonly on?: Expr;
}

/** Extension stage (namespaced name), accepted structurally and bound by the host. */
export interface GenericStage extends StageBase {
  readonly kind: 'generic';
  readonly op: string;
  readonly args: readonly Expr[];
  /** Named options in source/insertion order (serialized sorted by key). */
  readonly named: ReadonlyMap<string, Expr>;
}

export type Stage =
  | WhereStage
  | SelectStage
  | GroupByStage
  | SortStage
  | LimitStage
  | SimpleTerminalStage
  | ExprStage
  | RecurseStage
  | SetStage
  | CallStage
  | GenericStage;

export interface QueryPlan {
  readonly version: string;
  readonly source: Expr;
  readonly stages: readonly Stage[];
}

export const CORE_STAGES: ReadonlySet<string> = new Set([
  'where', 'select', 'sort', 'first', 'skip', 'count', 'describe', 'get', 'sum', 'min', 'max', 'avg',
  'group-by', 'distinct', 'recurse', 'set', 'call',
]);

/** Terminal stages must be the last stage of a plan. */
export function isTerminal(s: Stage): boolean {
  switch (s.kind) {
    case 'simple':
    case 'set':
    case 'call':
      return true;
    case 'expr':
      return s.op !== 'get' && s.op !== 'distinct' && s.op !== 'group-by';
    default:
      return false;
  }
}

export function isMutation(s: Stage): boolean {
  return s.kind === 'set' || s.kind === 'call';
}

// ------------------------------------------------------------------ constructors

export const plan = (source: Expr, stages: readonly Stage[], version: string = RQL_VERSION): QueryPlan => ({
  version,
  source,
  stages,
});

export const lit = (value: LiteralValue): LitExpr => ({ kind: 'lit', value });
export const list = (items: readonly Expr[]): ListExpr => ({ kind: 'list', items });
export const object = (entries: readonly ObjectEntry[]): ObjectExpr => ({ kind: 'object', entries });
export const path = (segments: readonly Segment[], on?: Expr): PathExpr =>
  on === undefined ? { kind: 'path', segments } : { kind: 'path', segments, on };
export const op = (o: ExprOp, args: readonly Expr[]): OpExpr => ({ kind: 'op', op: o, args });
export const call = (name: string, args: readonly Expr[], on?: Expr): CallExpr =>
  on === undefined ? { kind: 'call', name, args } : { kind: 'call', name, args, on };

export const seg = {
  member: (name: string): Segment => ({ kind: 'member', name }),
  meta: (name: string): Segment => ({ kind: 'meta', name }),
  at: (index: number): Segment => ({ kind: 'index', index }),
  key: (key: string | number): Segment => ({ kind: 'key', key }),
} as const;
