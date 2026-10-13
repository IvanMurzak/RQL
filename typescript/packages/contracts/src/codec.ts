/**
 * Canonical JSON codec for QueryPlan: fixed key order, defaults omitted, validation with the same
 * rules and error codes as dotnet/Rql.Contracts/PlanCodec.cs.
 */
import { RqlError } from './errors.js';
import { formatNumber } from './numbers.js';
import {
  RQL_VERSION,
  isExprOp,
  isTerminal,
  type Assignment,
  type Expr,
  type LiteralValue,
  type PathExpr,
  type QueryPlan,
  type Segment,
  type SelectField,
  type SortKey,
  type Stage,
} from './plan.js';

export type JsonValue = null | boolean | number | string | JsonValue[] | JsonObject;
export interface JsonObject {
  [key: string]: JsonValue;
}

// ------------------------------------------------------------------ serialize

/** Canonical JSON text of a plan (compact unless `indent` is given). */
export function serializePlan(p: QueryPlan, indent?: number): string {
  return JSON.stringify(planToJson(p), null, indent);
}

export function serializeExpr(e: Expr): string {
  return JSON.stringify(exprToJson(e));
}

/** Canonical JSON tree of a plan (object keys in canonical order). */
export function planToJson(p: QueryPlan): JsonObject {
  return { rql: p.version, source: exprToJson(p.source), stages: p.stages.map(stageToJson) };
}

function literalToJson(v: LiteralValue): JsonValue {
  if (typeof v === 'number' && !Number.isFinite(v)) {
    throw new RqlError('invalid-plan', `literal ${formatNumber(v)} cannot be represented in JSON`);
  }
  return v;
}

export function exprToJson(e: Expr): JsonObject {
  switch (e.kind) {
    case 'lit':
      return { lit: literalToJson(e.value) };
    case 'list':
      return { list: e.items.map(exprToJson) };
    case 'path': {
      const o: JsonObject = { path: e.segments.map(segmentToJson) };
      if (e.on !== undefined) o.on = exprToJson(e.on);
      return o;
    }
    case 'op':
      return { op: e.op, args: e.args.map(exprToJson) };
    case 'call': {
      const o: JsonObject = { call: e.name };
      if (e.on !== undefined) o.on = exprToJson(e.on);
      o.args = e.args.map(exprToJson);
      return o;
    }
  }
}

function segmentToJson(s: Segment): JsonValue {
  switch (s.kind) {
    case 'member':
      return s.name;
    case 'index':
      return { index: s.index };
    case 'meta':
      return { meta: s.name };
    case 'key':
      return { key: literalToJson(s.key) };
  }
}

function stageToJson(s: Stage): JsonObject {
  const o: JsonObject = { op: s.op };
  switch (s.kind) {
    case 'where':
      o.pred = exprToJson(s.pred);
      break;
    case 'select':
      o.fields = s.fields.map((f) => ({ name: f.name, expr: exprToJson(f.expr) }));
      break;
    case 'sort':
      o.keys = s.keys.map((k) => {
        const ko: JsonObject = { expr: exprToJson(k.expr) };
        if (k.desc) ko.desc = true;
        return ko;
      });
      break;
    case 'limit':
      o.n = s.n;
      break;
    case 'simple':
      break;
    case 'expr':
      if (s.expr !== undefined) o.expr = exprToJson(s.expr);
      break;
    case 'recurse':
      if (s.via !== undefined && s.via.length > 0) o.via = s.via.map(exprToJson);
      if (s.maxDepth !== undefined) o.maxDepth = s.maxDepth;
      if (!s.includeSelf) o.includeSelf = false;
      break;
    case 'set':
      o.assign = s.assign.map((a) => ({ target: exprToJson(a.target), value: exprToJson(a.value) }));
      break;
    case 'call':
      o.method = s.method;
      o.args = s.args.map(exprToJson);
      break;
    case 'generic':
      if (s.args.length > 0) o.args = s.args.map(exprToJson);
      if (s.named.size > 0) {
        const named: JsonObject = {};
        for (const k of [...s.named.keys()].sort(compareOrdinal)) named[k] = exprToJson(s.named.get(k)!);
        o.named = named;
      }
      break;
  }
  return o;
}

export function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** JSON string literal with .NET's default (non-relaxed) escaping, as used for key labels. */
export function quoteJsonString(s: string): string {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    switch (c) {
      case 0x22: out += '\\"'; continue;
      case 0x5c: out += '\\\\'; continue;
      case 0x0a: out += '\\n'; continue;
      case 0x0d: out += '\\r'; continue;
      case 0x09: out += '\\t'; continue;
      case 0x08: out += '\\b'; continue;
      case 0x0c: out += '\\f'; continue;
    }
    if (c < 0x20 || c > 0x7e || c === 0x26 || c === 0x27 || c === 0x2b || c === 0x3c || c === 0x3e || c === 0x60) {
      out += '\\u' + c.toString(16).toUpperCase().padStart(4, '0');
    } else {
      out += s[i];
    }
  }
  return out + '"';
}

/** Display form of a map key segment (`"name"` or a number). */
export function formatKey(key: string | number | null): string {
  if (typeof key === 'string') return quoteJsonString(key);
  if (typeof key === 'number') return formatNumber(key);
  return 'null';
}

export function segmentToString(s: Segment): string {
  switch (s.kind) {
    case 'member':
      return s.name;
    case 'meta':
      return '$' + s.name;
    case 'index':
      return '[' + String(s.index) + ']';
    case 'key':
      return '[' + formatKey(s.key) + ']';
  }
}

// ------------------------------------------------------------------ deserialize

type Json = unknown;
type Obj = { readonly [key: string]: Json };

const invalid = (message: string): RqlError => new RqlError('invalid-plan', message);

function isObject(v: Json): v is Obj {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function has(o: Obj, name: string): boolean {
  return Object.prototype.hasOwnProperty.call(o, name);
}

function required(o: Obj, name: string, ctx: string): Json {
  if (!has(o, name)) throw invalid(`${ctx}.${name} is required`);
  return o[name];
}

function allowOnly(o: Json, ctx: string, ...names: string[]): asserts o is Obj {
  if (!isObject(o)) throw invalid(`${ctx} must be an object`);
  for (const k of Object.keys(o)) {
    if (!names.includes(k)) throw invalid(`unexpected property '${k}' in ${ctx}`);
  }
}

const isInt32 = (v: Json): v is number => typeof v === 'number' && Number.isInteger(v) && v >= -2147483648 && v <= 2147483647;
const isSafeInt = (v: Json): v is number => typeof v === 'number' && Number.isSafeInteger(v);

/**
 * Parses and validates a plan. Accepts JSON text or an already parsed JSON value.
 * Throws {@link RqlError} with code invalid-plan / version-unsupported / unknown-stage / arity.
 */
export function deserializePlan(json: string | Json): QueryPlan {
  let root: Json = json;
  if (typeof json === 'string') {
    try {
      root = JSON.parse(json) as Json;
    } catch (e) {
      throw invalid('plan is not valid JSON: ' + (e instanceof Error ? e.message : String(e)));
    }
  }
  return readPlan(root);
}

export function readPlan(root: Json): QueryPlan {
  if (!isObject(root)) throw invalid('plan must be an object');
  allowOnly(root, 'plan', 'rql', 'source', 'stages');
  const v = root['rql'];
  if (!has(root, 'rql') || typeof v !== 'string') throw invalid('plan.rql (version string) is required');
  if (v !== RQL_VERSION) {
    throw new RqlError('version-unsupported', `plan version '${v}' is not supported (supported: ${RQL_VERSION})`);
  }
  const source = readExpr(required(root, 'source', 'plan'));
  const stagesEl = required(root, 'stages', 'plan');
  if (!Array.isArray(stagesEl)) throw invalid('plan.stages must be an array');
  const stages = stagesEl.map(readStage);
  const p: QueryPlan = { version: v, source, stages };
  validateStructure(p);
  return p;
}

/** Structural validation shared by codec and parser (terminal placement). */
export function validateStructure(p: QueryPlan): void {
  for (let i = 0; i < p.stages.length - 1; i++) {
    const s = p.stages[i]!;
    if (isTerminal(s)) throw new RqlError('invalid-plan', `stage '${s.op}' is terminal and must be the last stage`, s.span);
  }
}

function readLiteral(v: Json, ctx: string): LiteralValue {
  if (typeof v === 'number' && !Number.isFinite(v)) throw invalid(`${ctx} number is out of range`);
  if (v === null || typeof v === 'boolean' || typeof v === 'number' || typeof v === 'string') return v;
  throw invalid(`${ctx} must be a scalar literal`);
}

export function readExpr(e: Json): Expr {
  if (!isObject(e)) throw invalid('expression must be an object');
  if (has(e, 'lit')) {
    allowOnly(e, 'lit expression', 'lit');
    return { kind: 'lit', value: readLiteral(e['lit'], 'lit') };
  }
  if (has(e, 'list')) {
    allowOnly(e, 'list expression', 'list');
    return { kind: 'list', items: readExprArray(e['list'], 'list') };
  }
  if (has(e, 'path')) {
    allowOnly(e, 'path expression', 'path', 'on');
    return readPath(e, e['path']);
  }
  if (has(e, 'op')) {
    allowOnly(e, 'op expression', 'op', 'args');
    const name = e['op'];
    if (typeof name !== 'string') throw invalid('op must be a string');
    if (!isExprOp(name)) throw invalid(`unknown operator '${name}'`);
    const args = readExprArray(required(e, 'args', 'op'), 'args');
    const expected = name === 'not' || name === 'neg' ? 1 : 2;
    if (args.length !== expected) throw new RqlError('arity', `operator '${name}' takes ${expected} argument(s), got ${args.length}`);
    return { kind: 'op', op: name, args };
  }
  if (has(e, 'call')) {
    allowOnly(e, 'call expression', 'call', 'on', 'args');
    const name = e['call'];
    if (typeof name !== 'string' || name.length === 0) throw invalid('call must be a non-empty string');
    const args = readExprArray(required(e, 'args', 'call'), 'args');
    if (has(e, 'on')) return { kind: 'call', name, args, on: readExpr(e['on']) };
    return { kind: 'call', name, args };
  }
  throw invalid('expression must have one of: lit, list, path, op, call');
}

function readPath(e: Obj, p: Json): PathExpr {
  if (!Array.isArray(p)) throw invalid('path must be an array');
  const segments: Segment[] = [];
  for (const s of p as Json[]) {
    if (typeof s === 'string') {
      if (s.length === 0) throw invalid('member segment must not be empty');
      segments.push({ kind: 'member', name: s });
    } else if (isObject(s)) {
      if (has(s, 'index')) {
        allowOnly(s, 'index segment', 'index');
        const ix = s['index'];
        if (!isSafeInt(ix)) throw invalid('index must be an integer');
        segments.push({ kind: 'index', index: ix });
      } else if (has(s, 'key')) {
        allowOnly(s, 'key segment', 'key');
        const k = readLiteral(s['key'], 'key');
        if (typeof k !== 'string' && typeof k !== 'number') throw invalid('key must be a string or number');
        segments.push({ kind: 'key', key: k });
      } else if (has(s, 'meta')) {
        allowOnly(s, 'meta segment', 'meta');
        const m = s['meta'];
        if (typeof m !== 'string' || m.length === 0) throw invalid('meta must be a non-empty string');
        segments.push({ kind: 'meta', name: m });
      } else {
        throw invalid('segment object must have index, key or meta');
      }
    } else {
      throw invalid('segment must be a string or object');
    }
  }
  if (has(e, 'on')) return { kind: 'path', segments, on: readExpr(e['on']) };
  return { kind: 'path', segments };
}

function readExprArray(arr: Json, ctx: string): Expr[] {
  if (!Array.isArray(arr)) throw invalid(`${ctx} must be an array`);
  return (arr as Json[]).map(readExpr);
}

function readInt(o: Obj, name: string, ctx: string, min: number): number {
  const v = required(o, name, ctx);
  if (!isInt32(v) || v < min) throw invalid(`${ctx}.${name} must be an integer >= ${min}`);
  return v;
}

function readTrueFlag(o: Obj, name: string, ctx: string): boolean {
  if (!has(o, name)) return false;
  const v = o[name];
  if (typeof v === 'boolean') return v;
  throw invalid(`${ctx}.${name} must be a boolean`);
}

function nonEmptyArray(v: Json, message: string): Json[] {
  if (!Array.isArray(v) || v.length === 0) throw invalid(message);
  return v as Json[];
}

export function readStage(s: Json): Stage {
  if (!isObject(s)) throw invalid('stage must be an object');
  const opEl = required(s, 'op', 'stage');
  if (typeof opEl !== 'string') throw invalid('stage.op must be a string');
  const op = opEl;
  const ctx = `stage '${op}'`;
  switch (op) {
    case 'where':
      allowOnly(s, ctx, 'op', 'pred');
      return { kind: 'where', op, pred: readExpr(required(s, 'pred', ctx)) };
    case 'select': {
      allowOnly(s, ctx, 'op', 'fields');
      const arr = nonEmptyArray(required(s, 'fields', ctx), 'select.fields must be a non-empty array');
      const fields: SelectField[] = arr.map((f) => {
        allowOnly(f, 'select field', 'name', 'expr');
        const name = required(f, 'name', 'field');
        if (typeof name !== 'string') throw invalid('field.name must be a string');
        return { name, expr: readExpr(required(f, 'expr', 'field')) };
      });
      return { kind: 'select', op, fields };
    }
    case 'sort': {
      allowOnly(s, ctx, 'op', 'keys');
      const arr = nonEmptyArray(required(s, 'keys', ctx), 'sort.keys must be a non-empty array');
      const keys: SortKey[] = arr.map((k) => {
        allowOnly(k, 'sort key', 'expr', 'desc');
        return { expr: readExpr(required(k, 'expr', 'sort key')), desc: readTrueFlag(k, 'desc', 'sort key') };
      });
      return { kind: 'sort', op, keys };
    }
    case 'first':
    case 'skip':
      allowOnly(s, ctx, 'op', 'n');
      return { kind: 'limit', op, n: readInt(s, 'n', op, 0) };
    case 'count':
    case 'describe':
      allowOnly(s, ctx, 'op');
      return { kind: 'simple', op };
    case 'get':
    case 'sum':
    case 'min':
    case 'max':
    case 'avg':
    case 'group-by':
      allowOnly(s, ctx, 'op', 'expr');
      return { kind: 'expr', op, expr: readExpr(required(s, 'expr', ctx)) };
    case 'distinct':
      allowOnly(s, ctx, 'op', 'expr');
      return has(s, 'expr') ? { kind: 'expr', op, expr: readExpr(s['expr']) } : { kind: 'expr', op };
    case 'recurse': {
      allowOnly(s, ctx, 'op', 'via', 'maxDepth', 'includeSelf');
      const via = has(s, 'via') ? readExprArray(s['via'], 'via') : undefined;
      const maxDepth = has(s, 'maxDepth') ? readInt(s, 'maxDepth', ctx, 1) : undefined;
      const includeSelf = !has(s, 'includeSelf') || readTrueFlag(s, 'includeSelf', ctx);
      return {
        kind: 'recurse',
        op,
        includeSelf,
        ...(via !== undefined ? { via } : {}),
        ...(maxDepth !== undefined ? { maxDepth } : {}),
      };
    }
    case 'set': {
      allowOnly(s, ctx, 'op', 'assign');
      const arr = nonEmptyArray(required(s, 'assign', ctx), 'set.assign must be a non-empty array');
      const assign: Assignment[] = arr.map((a) => {
        allowOnly(a, 'assignment', 'target', 'value');
        const target = readExpr(required(a, 'target', 'assignment'));
        if (target.kind !== 'path' || target.segments.length === 0 || target.on !== undefined || target.segments.some((sg) => sg.kind === 'meta')) {
          throw invalid("assignment.target must be a non-empty member path relative to the item (no 'on', no meta segments)");
        }
        return { target, value: readExpr(required(a, 'value', 'assignment')) };
      });
      return { kind: 'set', op, assign };
    }
    case 'call': {
      allowOnly(s, ctx, 'op', 'method', 'args');
      const m = required(s, 'method', ctx);
      if (typeof m !== 'string' || m.length === 0) throw invalid('call.method must be a non-empty string');
      return { kind: 'call', op, method: m, args: readExprArray(required(s, 'args', ctx), 'args') };
    }
    default: {
      if (!op.includes('.')) {
        throw new RqlError('unknown-stage', `unknown stage '${op}' (extension stages must be namespaced, e.g. 'myapp.${op}')`);
      }
      allowOnly(s, ctx, 'op', 'args', 'named');
      const args = has(s, 'args') ? readExprArray(s['args'], 'args') : [];
      const named = new Map<string, Expr>();
      if (has(s, 'named')) {
        const n = s['named'];
        if (!isObject(n)) throw invalid('named must be an object');
        for (const k of Object.keys(n)) named.set(k, readExpr(n[k]));
      }
      return { kind: 'generic', op, args, named };
    }
  }
}
