/**
 * Compiles plan expressions into closures over {@link Exec} (spec §3–4): paths and member access,
 * Kleene logic, comparisons, arithmetic, core functions and collection methods.
 * Port of dotnet/Rql.Execution/Compiler.cs.
 */
import { RqlError, diagnostic, formatNumber, type CallExpr, type Expr, type OpExpr, type PathExpr } from '@rql/contracts';
import { elements, findMapKey, indexAt, isMapLike, mapEntries, mapGet, sizeOf, type MapLike } from './collections.js';
import { firstAnswer } from './adapters.js';
import { Exec, IsSite, MemberSite, errorMessage } from './exec.js';
import { EnumValue, type TypeInfo, type TypeModel } from './types.js';
import { ErrorValue, MISSING, MapEntry, ObjectValue, RowValue, UNKNOWN, isAbsent, toNumber } from './values.js';

export type Eval = (x: Exec) => unknown;
type Truth = boolean | undefined;

export const CORE_FUNCTIONS = [
  'has', 'is-null', 'len', 'lower', 'upper', 'abs', 'round', 'floor', 'ceil', 'sqrt', 'pow', 'min', 'max', 'coalesce', 'ref',
  'append', 'insert', 'remove-at', 'without',
  'replace', 'replace-regex', 'substring', 'split', 'trim', 'pad',
  'contains', 'starts-with', 'ends-with', 'matches',
];

export const CORE_METHODS = [
  'any', 'all', 'count', 'sum', 'min', 'max', 'avg', 'first', 'keys', 'values',
  'contains', 'starts-with', 'ends-with', 'matches', 'lower', 'upper', 'len',
  'replace', 'replace-regex', 'substring', 'split', 'trim', 'pad',
];

const isList = (x: Exec, v: unknown): v is object => typeof v === 'object' && v !== null && x.types.infoOf(v).isList;

export class Compiler {
  constructor(private readonly x: Exec) {}

  compile(e: Expr): Eval {
    switch (e.kind) {
      case 'lit': {
        const v = e.value;
        return () => v;
      }
      case 'list': {
        const items = e.items.map((i) => this.compile(i));
        return (x) => items.map((i) => i(x));
      }
      case 'object': {
        const entries = e.entries.map((en) => ({ key: en.key, value: this.compile(en.value) }));
        return (x) => {
          const o = new ObjectValue();
          for (const en of entries) o.set(en.key, en.value(x));
          return o;
        };
      }
      case 'path':
        return this.compilePath(e);
      case 'op':
        return this.compileOp(e);
      case 'call':
        return this.compileCall(e);
    }
  }

  // ---------------------------------------------------------------- paths

  private compilePath(p: PathExpr): Eval {
    const relativeToItem = p.on === undefined;
    let cur: Eval = p.on !== undefined ? this.compile(p.on) : (x) => x.live(x.current);
    p.segments.forEach((s, i) => {
      const prev = cur;
      const first = i === 0 && relativeToItem;
      switch (s.kind) {
        case 'member': {
          const site = new MemberSite(s.name, p.span, first);
          this.x.sites.push(site);
          if (first) {
            // Lexical scope chain: inside method arguments a bare name missing on the element
            // resolves against the enclosing item(s), innermost first (spec §3).
            cur = (x) => {
              const r = member(x, x.live(x.current), site);
              if (r !== MISSING || x.outer.length === 0) return r;
              for (let k = x.outer.length - 1; k >= 0; k--) {
                const o = member(x, x.outer[k], site);
                if (o !== MISSING) return o;
              }
              return r;
            };
          } else cur = (x) => member(x, prev(x), site);
          break;
        }
        case 'index': {
          const idx = s.index;
          cur = (x) => {
            const v = prev(x);
            if (isMapLike(v)) return mapLookup(x, v, idx);
            return indexOf(x, v, idx);
          };
          break;
        }
        case 'key': {
          const key = s.key;
          cur = (x) => keyAt(x, prev(x), key, p);
          break;
        }
        case 'meta':
          cur = this.compileMeta(s.name, prev, first);
          break;
      }
    });
    if (relativeToItem && p.segments.length > 1 && p.segments.every((s) => s.kind === 'member')) {
      // After `select Owner.Label`, the path Owner.Label names that column (spec §3), not Owner on the row.
      const column = p.segments.map((s) => (s.kind === 'member' ? s.name : '')).join('.');
      const navigate = cur;
      return (x) => {
        const row = x.current;
        if (row instanceof RowValue) {
          const i = row.indexOf(column);
          if (i >= 0) return row.values[i];
        }
        return navigate(x);
      };
    }
    return cur;
  }

  private compileMeta(name: string, prev: Eval, ofCurrentItem: boolean): Eval {
    const inner = this.compileMetaCore(name, prev, ofCurrentItem);
    if (!ofCurrentItem) return inner;
    // After `select`, a column literally named "$name" (e.g. select $path) shadows the meta member.
    const column = '$' + name;
    return (x) => {
      const cur = x.current;
      if (cur instanceof RowValue) {
        const i = cur.indexOf(column);
        if (i >= 0) return cur.values[i];
      }
      return inner(x);
    };
  }

  private compileMetaCore(name: string, prev: Eval, ofCurrentItem: boolean): Eval {
    switch (name) {
      case 'type':
        return (x) => {
          const v = prev(x);
          if (v === null || isAbsent(v)) return v;
          if (!ofCurrentItem && isList(x, v)) return mapList(x, v, (el) => (el === null ? MISSING : x.types.infoOf(el).shortName));
          return x.types.infoOf(v).shortName;
        };
      case 'ref':
        return (x) => {
          const v = prev(x);
          if (v === null || isAbsent(v)) return v;
          const refOf = (o: unknown): unknown => {
            // A one-column row stands for its value; multi-column rows have no identity.
            if (o instanceof RowValue) o = o.columns.length === 1 ? o.values[0] : null;
            return typeof o === 'object' && o !== null && !isAbsent(o) && x.types.infoOf(o).isObject ? x.engine.refOf(o) : MISSING;
          };
          if (!ofCurrentItem && isList(x, v)) return mapList(x, v, refOf);
          return refOf(v);
        };
      case 'path':
        this.x.trackPaths = true;
        if (!ofCurrentItem) return () => MISSING;
        return (x) => (x.currentPath !== null ? x.currentPath.toString() : MISSING);
      case 'index':
        if (!ofCurrentItem) return () => MISSING;
        return (x) => x.index;
      case 'depth':
        if (!ofCurrentItem) return () => MISSING;
        return (x) => (x.currentDepth >= 0 ? x.currentDepth : MISSING);
      default:
        this.x.warn('unknown-meta', `unknown meta member '$${name}'`, 'meta members: $type $ref $path $index $depth');
        return () => MISSING;
    }
  }

  // ---------------------------------------------------------------- operators

  private compileOp(o: OpExpr): Eval {
    const a = this.compile(o.args[0]!);
    const b: Eval = o.args.length > 1 ? this.compile(o.args[1]!) : () => null;
    switch (o.op) {
      case 'and':
        return (x) => {
          const l = truth(a(x));
          if (l === false) return false;
          const r = truth(b(x));
          if (r === false) return false;
          return l === true && r === true ? true : UNKNOWN;
        };
      case 'or':
        return (x) => {
          const l = truth(a(x));
          if (l === true) return true;
          const r = truth(b(x));
          if (r === true) return true;
          return l === false && r === false ? false : UNKNOWN;
        };
      case 'not':
        return (x) => {
          const v = truth(a(x));
          return v === undefined ? UNKNOWN : !v;
        };
      case '==':
        return (x) => tri(equal(x, a(x), b(x)));
      case '!=':
        return (x) => {
          const v = equal(x, a(x), b(x));
          return v === undefined ? UNKNOWN : !v;
        };
      case '<':
        return (x) => ord(x, a(x), b(x), (c) => c < 0);
      case '<=':
        return (x) => ord(x, a(x), b(x), (c) => c <= 0);
      case '>':
        return (x) => ord(x, a(x), b(x), (c) => c > 0);
      case '>=':
        return (x) => ord(x, a(x), b(x), (c) => c >= 0);
      case 'contains':
        return (x) => tri(contains(x, a(x), b(x)));
      case 'starts-with':
        return (x) => tri(stringTest(a(x), b(x), (s, t) => s.toUpperCase().startsWith(t.toUpperCase())));
      case 'ends-with':
        return (x) => tri(stringTest(a(x), b(x), (s, t) => s.toUpperCase().endsWith(t.toUpperCase())));
      case 'matches':
        checkLiteralRegex(o.args[1]);
        return (x) => {
          const r = tri(matches(x, a(x), b(x)));
          x.checkTime(); // regexes have no per-match timeout in JS; bound the whole query
          return r;
        };
      case 'in':
        return (x) => tri(isIn(x, a(x), b(x)));
      case 'is': {
        const tn = o.args[1];
        const site = tn !== undefined && tn.kind === 'lit' && typeof tn.value === 'string' ? new IsSite(tn.value, o.span) : undefined;
        if (site !== undefined) this.x.isSites.push(site);
        return (x) => {
          const v = a(x);
          const r = isType(x, v, b(x));
          if (site !== undefined && r !== undefined) {
            site.evaluated++;
            if (r) site.matched++;
            else if (v !== null && site.seen.size < 6) site.seen.add(x.types.infoOf(v));
          }
          return tri(r);
        };
      }
      case 'neg':
        return (x) => arith('neg', a(x), 0);
      case '+':
      case '-':
      case '*':
      case '/':
      case '%': {
        const op = o.op;
        return (x) => arith(op, a(x), b(x));
      }
      case 'index':
        return (x) => {
          const v = a(x);
          const k = b(x);
          if (isAbsent(k)) return k;
          if (v === null) return MISSING;
          if (isAbsent(v)) return v;
          if (isMapLike(v)) return mapLookup(x, v, typeof k === 'number' || typeof k === 'string' ? k : toText(x, k));
          const kn = toNumber(k);
          if (kn !== undefined && Math.floor(kn) === kn) return indexOf(x, v, kn);
          if (typeof k === 'string') return member(x, v, new MemberSite(k, undefined));
          return MISSING;
        };
    }
  }

  // ---------------------------------------------------------------- calls

  private compileCall(c: CallExpr): Eval {
    // ns.fn(x) parses as method fn on path ns: rewrite when a namespaced function is registered (spec §9).
    const on = c.on;
    if (on !== undefined && on.kind === 'path' && on.on === undefined && on.segments.length > 0 && on.segments.every((s) => s.kind === 'member')) {
      const ns = on.segments.map((s) => (s.kind === 'member' ? s.name : '')).join('.') + '.' + c.name;
      const ext = this.x.engine.functions.get(ns);
      if (ext !== undefined) return this.compileExtension(ns, ext, c.args);
    }
    return on === undefined ? this.compileFunction(c) : this.compileMethod(c, on);
  }

  private compileExtension(fnName: string, fn: (args: unknown[]) => unknown, argExprs: readonly Expr[]): Eval {
    const args = argExprs.map((e) => this.compile(e));
    return (x) => {
      const vals = args.map((a) => a(x));
      try {
        const r = fn(vals);
        return r === undefined ? null : r;
      } catch (e) {
        const detail = `${fnName}() threw ${e instanceof Error ? e.name : typeof e}: ${errorMessage(e)}`;
        x.warnOnce('function-threw', detail, 'function-threw:' + fnName);
        return new ErrorValue('function-threw', detail);
      }
    };
  }

  private arity(c: CallExpr, min: number, max: number): void {
    if (c.args.length < min || c.args.length > max) {
      const expect = min === max ? String(min) : `${min}-${max}`;
      throw new RqlError('arity', `'${c.name}' takes ${expect} argument(s), got ${c.args.length}`, c.span);
    }
  }

  private compileFunction(c: CallExpr): Eval {
    const name = c.name.toLowerCase();
    if (c.name.includes('.')) {
      const ext = this.x.engine.functions.get(c.name);
      if (ext !== undefined) return this.compileExtension(c.name, ext, c.args);
    }
    switch (name) {
      case 'has': {
        this.arity(c, 1, 1);
        const a = this.compile(c.args[0]!);
        return (x) => a(x) !== MISSING;
      }
      case 'is-null': {
        this.arity(c, 1, 1);
        const a = this.compile(c.args[0]!);
        return (x) => tri(equal(x, a(x), null));
      }
      case 'len': {
        this.arity(c, 1, 1);
        const a = this.compile(c.args[0]!);
        return (x) => len(x, a(x));
      }
      case 'lower':
      case 'upper': {
        this.arity(c, 1, 1);
        const a = this.compile(c.args[0]!);
        const up = name === 'upper';
        return (x) => caseOf(a(x), up);
      }
      case 'abs':
      case 'floor':
      case 'ceil':
      case 'sqrt': {
        this.arity(c, 1, 1);
        const a = this.compile(c.args[0]!);
        const f = name === 'abs' ? Math.abs : name === 'floor' ? Math.floor : name === 'ceil' ? Math.ceil : Math.sqrt;
        return (x) => num(a(x), f);
      }
      case 'pow':
      case 'min':
      case 'max': {
        if (name === 'pow') this.arity(c, 2, 2);
        else this.arity(c, 2, 64);
        const args = c.args.map((e) => this.compile(e));
        // Math.min / Math.max match .NET (NaN wins; -0 < +0).
        const f: (l: number, r: number) => number = name === 'pow' ? Math.pow : name === 'min' ? Math.min : Math.max;
        return (x) => {
          let acc = 0;
          for (let i = 0; i < args.length; i++) {
            const v = args[i]!(x);
            if (v === null || isAbsent(v)) return v;
            const d = toNumber(v);
            if (d === undefined) return new ErrorValue('type-mismatch', `${name}() expects numbers, got ${describeValue(v)}`);
            acc = i === 0 ? d : f(acc, d);
          }
          return acc;
        };
      }
      case 'round': {
        this.arity(c, 1, 2);
        const a = this.compile(c.args[0]!);
        const digits = c.args.length > 1 ? this.compile(c.args[1]!) : undefined;
        return (x) => {
          let dg = 0;
          const dd = digits !== undefined ? toNumber(digits(x)) : undefined;
          if (dd !== undefined) dg = Math.max(0, Math.min(15, Math.trunc(dd)));
          return num(a(x), (v) => roundAwayFromZero(v, dg));
        };
      }
      // List editing (spec §4.3): pure functions returning a new list; assign the result with `set`.
      case 'append': {
        this.arity(c, 2, 64);
        const args = c.args.map((e) => this.compile(e));
        return (x) => {
          const list = listArg(x, args[0]!(x));
          if (list === undefined) return new ErrorValue('type-mismatch', 'append() expects a list first');
          for (let i = 1; i < args.length; i++) list.push(args[i]!(x));
          return list;
        };
      }
      case 'insert':
      case 'remove-at': {
        const inserting = name === 'insert';
        this.arity(c, inserting ? 3 : 2, inserting ? 3 : 2);
        const [la, ia, va] = c.args.map((e) => this.compile(e)) as [Eval, Eval, Eval | undefined];
        return (x) => {
          const at = listAndIndex(x, name, la(x), ia(x), inserting);
          if (at instanceof ErrorValue) return at;
          if (inserting) at.list.splice(at.index, 0, va!(x));
          else at.list.splice(at.index, 1);
          return at.list;
        };
      }
      case 'without': {
        this.arity(c, 2, 2);
        const [la, da] = c.args.map((e) => this.compile(e)) as [Eval, Eval];
        return (x) => {
          const list = listArg(x, la(x));
          if (list === undefined) return new ErrorValue('type-mismatch', 'without() expects a list first');
          const drop = da(x);
          return list.filter((el) => equal(x, el, drop) !== true);
        };
      }
      case 'coalesce': {
        this.arity(c, 1, 16);
        const args = c.args.map((e) => this.compile(e));
        return (x) => {
          for (const a of args) {
            const v = a(x);
            if (v !== null && !isAbsent(v)) return v;
          }
          return null;
        };
      }
      case 'ref': {
        this.arity(c, 1, 64);
        const args = c.args.map((e) => this.compile(e));
        return (x) => {
          const found: unknown[] = [];
          for (const a of args) {
            const v = a(x);
            const refs = isList(x, v) ? [...elements(v, x.types.infoOf(v))] : [v];
            for (const r of refs) {
              if (typeof r !== 'string') return new ErrorValue('type-mismatch', 'ref() expects reference strings');
              const res = x.engine.resolveRef(r, x);
              if (!res.ok) {
                x.diagnostics.push(diagnostic('error', 'stale-ref', res.error, x.engine.refHint(r)));
                return new ErrorValue('stale-ref', res.error);
              }
              found.push(res.value);
            }
          }
          return found.length === 1 ? found[0] : found;
        };
      }
      case 'contains':
      case 'starts-with':
      case 'ends-with':
      case 'matches':
        this.arity(c, 2, 2);
        return this.compileOp({ kind: 'op', op: name, args: c.args, ...(c.span !== undefined ? { span: c.span } : {}) });
      case 'replace':
      case 'replace-regex':
      case 'substring':
      case 'split':
      case 'trim':
      case 'pad':
        return this.compileStringFunction(c, name, c.args, 0);
      default: {
        const known = [...CORE_FUNCTIONS, ...this.x.engine.functions.keys()];
        throw new RqlError('unknown-function', `unknown function '${c.name}'`, c.span, `functions: ${known.join(' ')}; collection methods: list.any(pred) list.count() ...`);
      }
    }
  }

  private compileMethod(c: CallExpr, onExpr: Expr): Eval {
    const on = this.compile(onExpr);
    const name = c.name.toLowerCase();
    switch (name) {
      case 'any':
      case 'all':
      case 'count':
      case 'first': {
        this.arity(c, name === 'all' ? 1 : 0, 1);
        const pred = c.args.length > 0 ? this.compile(c.args[0]!) : undefined;
        return (x) => quantify(x, on(x), pred, name);
      }
      case 'sum':
      case 'min':
      case 'max':
      case 'avg': {
        this.arity(c, 0, 1);
        const sel = c.args.length > 0 ? this.compile(c.args[0]!) : undefined;
        return (x) => aggregate(x, on(x), sel, name);
      }
      case 'keys':
      case 'values': {
        this.arity(c, 0, 0);
        const keys = name === 'keys';
        return (x) => {
          const v = on(x);
          if (!isMapLike(v)) return MISSING;
          return [...mapEntries(v)].map((e) => x.live(keys ? e.Key : e.Value));
        };
      }
      case 'contains':
      case 'starts-with':
      case 'ends-with':
      case 'matches':
        this.arity(c, 1, 1);
        return this.compileOp({ kind: 'op', op: name, args: [onExpr, c.args[0]!], ...(c.span !== undefined ? { span: c.span } : {}) });
      case 'lower':
      case 'upper': {
        this.arity(c, 0, 0);
        const up = name === 'upper';
        return (x) => caseOf(on(x), up);
      }
      case 'len':
        this.arity(c, 0, 0);
        return (x) => len(x, on(x));
      case 'replace':
      case 'replace-regex':
      case 'substring':
      case 'split':
      case 'trim':
      case 'pad':
        return this.compileStringFunction(c, name, [onExpr, ...c.args], 1);
      default:
        throw new RqlError('unknown-function', `unknown method '${c.name}'`, c.span, 'methods: ' + CORE_METHODS.join(' '));
    }
  }

  /**
   * String functions (spec §4.3), as functions (`replace(s, a, b)`) or methods on the string (`s.replace(a, b)`,
   * `receiver` = 1: the first argument is the receiver and the arity excludes it).
   */
  private compileStringFunction(c: CallExpr, name: string, argExprs: readonly Expr[], receiver: number): Eval {
    const [min, max] = STRING_ARITY[name]!;
    this.arity(c, min - receiver, max - receiver);
    if (name === 'replace-regex') checkLiteralRegex(argExprs[1]);
    const args = argExprs.map((e) => this.compile(e));
    return (x) => {
      const vals: unknown[] = [];
      for (const a of args) {
        const v = a(x);
        if (v === null || v === undefined || isAbsent(v)) return v ?? null; // null / missing / error propagate (first one)
        vals.push(v);
      }
      const r = stringFunction(x, name, vals);
      if (name === 'replace-regex') x.checkTime();
      return r;
    };
  }
}

// ------------------------------------------------------------------ member access

function mapList(x: Exec, list: object, f: (el: unknown) => unknown): unknown[] {
  const out: unknown[] = [];
  for (const el of liveElements(x, list)) {
    const r = f(el === undefined ? null : el);
    if (r !== MISSING) out.push(r);
  }
  return out;
}

export function member(x: Exec, v: unknown, site: MemberSite): unknown {
  if (v === null || v === undefined) return MISSING;
  if (isAbsent(v)) return v;
  const info = x.types.infoOf(v);
  let m = site.cachedInfo === info ? site.cachedMember : undefined;
  if (m === undefined) {
    if (info.isList) {
      const n = site.name.toUpperCase();
      if (n === 'COUNT' || n === 'LENGTH') {
        site.hits++;
        return sizeOf(v, x.types) ?? 0;
      }
      // Implicit map over elements, flattening one level (spec §3).
      const out: unknown[] = [];
      for (const el of liveElements(x, v as object, info)) {
        x.visit();
        const r = member(x, el === undefined ? null : el, site);
        if (r === MISSING) continue;
        if (typeof r === 'object' && r !== null && x.types.infoOf(r).isList) {
          for (const inner of liveElements(x, r)) out.push(inner);
        } else out.push(r);
      }
      return out;
    }
    if (info.isMap) {
      if (site.name.toUpperCase() === 'COUNT') {
        site.hits++;
        return sizeOf(v, x.types) ?? 0;
      }
      return mapLookup(x, v as MapLike, site.name, site);
    }
    if (v instanceof RowValue) {
      const ci = v.indexOf(site.name);
      if (ci >= 0) {
        site.hits++;
        return v.values[ci];
      }
      // A single-column row stands for its value (`select A.B | get Item` style navigation).
      if (v.columns.length === 1) return member(x, v.values[0], site);
      site.misses++;
      site.rowColumns ??= v.columns;
      return MISSING;
    }
    m = info.find(site.name, v);
    if (m === undefined && v instanceof MapEntry) {
      // Entries expose key/value; any other member is looked up on the value (spec §4.4).
      return member(x, v.Value, site);
    }
    if (m === undefined || typeof v !== 'object') {
      site.misses++;
      if (site.missTypes.size < 8) site.missTypes.add(info);
      return MISSING;
    }
    if (info.dynamic === undefined) {
      site.cachedInfo = info;
      site.cachedMember = m;
    }
  }
  site.hits++;
  x.membersRead++;
  try {
    return x.live(m.get(v as object)); // a destroyed object read from a member is null
  } catch (e) {
    x.getterThrew(m.name, e);
    return new ErrorValue('getter-threw', errorMessage(e));
  }
}

export function mapLookup(x: Exec, map: MapLike, key: unknown, site?: MemberSite): unknown {
  x.membersRead++;
  const k = findMapKey(map, key);
  if (!k.found) return MISSING;
  if (site !== undefined) site.hits++;
  const v = mapGet(map, k.key);
  return v === undefined ? null : x.live(v);
}

function indexOf(x: Exec, v: unknown, idx: number): unknown {
  if (v === null || v === undefined) return MISSING;
  if (isAbsent(v)) return v;
  if (typeof v !== 'object') return MISSING;
  const info = x.types.infoOf(v);
  if (!info.isList) return MISSING;
  return x.live(indexAt(v, idx));
}

function keyAt(x: Exec, v: unknown, key: string | number, p: PathExpr): unknown {
  if (v === null || v === undefined) return MISSING;
  if (isAbsent(v)) return v;
  if (isMapLike(v)) return mapLookup(x, v, key);
  const info = x.types.infoOf(v);
  if (typeof key === 'string' && !info.isList) return member(x, v, new MemberSite(key, p.span));
  if (typeof key === 'number' && Math.floor(key) === key) return indexOf(x, v, key);
  return MISSING;
}

// ------------------------------------------------------------------ logic and comparison

export function truth(v: unknown): Truth {
  if (typeof v === 'boolean') return v;
  if (v === null) return false;
  return undefined;
}

const tri = (b: Truth): unknown => (b === undefined ? UNKNOWN : b);

function isStructOrCollection(x: Exec, v: object): boolean {
  const k = x.types.infoOf(v).kind;
  return k === 'struct' || k === 'list' || k === 'map';
}

/** Equality per spec §4.1; undefined = unknown. */
export function equal(x: Exec, a: unknown, b: unknown): Truth {
  if (isAbsent(a) || isAbsent(b)) return undefined;
  if (a === null || b === null) return a === null && b === null;
  if (x.types.hasCapability('equals')) {
    const r = adapterEqual(x.types, a, b);
    if (r !== undefined) return r;
  }
  const na = toNumber(a);
  if (na !== undefined) {
    const nb = toNumber(b);
    if (nb !== undefined) return na === nb;
    if (b instanceof EnumValue) return b.ordinal === na;
    return undefined;
  }
  if (a instanceof EnumValue) return enumEqual(a, b);
  if (b instanceof EnumValue) return enumEqual(b, a);
  if (typeof a === 'string') return typeof b === 'string' ? a === b : undefined;
  if (typeof a === 'boolean') return typeof b === 'boolean' ? a === b : undefined;
  if (typeof a !== 'object' || typeof b !== 'object') return undefined;
  if (a instanceof Date || b instanceof Date) return a instanceof Date && b instanceof Date ? a.getTime() === b.getTime() : undefined;
  // Structs, lists and maps are not comparable in v0 (spec §4.1); objects by identity.
  if (isStructOrCollection(x, a) || isStructOrCollection(x, b)) return undefined;
  return a === b;
}

function enumEqual(e: EnumValue, other: unknown): Truth {
  if (typeof other === 'string') return e.name.toUpperCase() === other.toUpperCase();
  const n = toNumber(other);
  if (n !== undefined) return e.ordinal === n;
  if (other instanceof EnumValue) return e.type === other.type && e.value === other.value;
  return undefined;
}

/** First adapter of the left operand that answers, then of the right one (spec §13). */
function adapterEqual(model: TypeModel, a: unknown, b: unknown): Truth {
  const l = typeof a === 'object' && a !== null ? firstAnswer(model.infoOf(a).adapterSet.equals, (ad) => ad.equals!(a, b)) : undefined;
  if (l !== undefined) return l;
  return typeof b === 'object' && b !== null ? firstAnswer(model.infoOf(b).adapterSet.equals, (ad) => ad.equals!(b, a)) : undefined;
}

/** Like {@link adapterEqual}; a right-operand answer is mirrored. */
function adapterCompare(model: TypeModel, a: unknown, b: unknown): number | undefined {
  const valid = (r: number | undefined): number | undefined => (r === undefined || Number.isNaN(r) ? undefined : Math.sign(r));
  const l = typeof a === 'object' && a !== null ? firstAnswer(model.infoOf(a).adapterSet.compare, (ad) => valid(ad.compare!(a, b))) : undefined;
  if (l !== undefined) return l;
  const r = typeof b === 'object' && b !== null ? firstAnswer(model.infoOf(b).adapterSet.compare, (ad) => valid(ad.compare!(b, a))) : undefined;
  return r === undefined ? undefined : -r || 0;
}

export function compareNumbers(a: number, b: number): number {
  if (a < b) return -1;
  if (a > b) return 1;
  if (a === b) return 0;
  // .NET double.CompareTo: NaN sorts first and equals NaN.
  return Number.isNaN(a) ? (Number.isNaN(b) ? 0 : -1) : 1;
}

/** Ordering comparison; undefined = incomparable. */
export function compare(model: TypeModel, a: unknown, b: unknown): number | undefined {
  if (a === null || b === null || isAbsent(a) || isAbsent(b)) return undefined;
  if (model.hasCapability('compare')) {
    const r = adapterCompare(model, a, b);
    if (r !== undefined) return r;
  }
  const na = toNumber(a);
  if (na !== undefined) {
    const nb = toNumber(b);
    if (nb !== undefined) return compareNumbers(na, nb);
    if (b instanceof EnumValue) return compareNumbers(na, b.ordinal);
    return undefined;
  }
  if (a instanceof EnumValue) {
    if (b instanceof EnumValue && b.type === a.type) return compareNumbers(a.ordinal, b.ordinal);
    const nb = toNumber(b);
    if (nb !== undefined) return compareNumbers(a.ordinal, nb);
    if (typeof b === 'string') {
      const ev = a.type.parse(b);
      if (ev !== undefined) return compareNumbers(a.ordinal, ev.ordinal);
    }
    return undefined;
  }
  if (typeof a === 'string' && typeof b === 'string') return a < b ? -1 : a > b ? 1 : 0;
  if (typeof a === 'boolean' && typeof b === 'boolean') return Number(a) - Number(b);
  if (a instanceof Date && b instanceof Date) return compareNumbers(a.getTime(), b.getTime());
  return undefined;
}

function ord(x: Exec, a: unknown, b: unknown, test: (c: number) => boolean): unknown {
  const c = compare(x.types, a, b);
  return c === undefined ? UNKNOWN : test(c);
}

function contains(x: Exec, a: unknown, b: unknown): Truth {
  if (isAbsent(a) || isAbsent(b) || a === null) return undefined;
  if (typeof a === 'string') return typeof b === 'string' ? a.toUpperCase().includes(b.toUpperCase()) : undefined;
  if (typeof a !== 'object') return undefined;
  const info = x.types.infoOf(a);
  if (info.isList) {
    for (const el of liveElements(x, a, info)) if (equal(x, el, b) === true) return true;
    return false;
  }
  if (info.isMap) {
    for (const e of mapEntries(a as MapLike)) if (equal(x, x.live(e.Key), b) === true) return true;
    return false;
  }
  return undefined;
}

function stringTest(a: unknown, b: unknown, test: (s: string, t: string) => boolean): Truth {
  if (typeof a !== 'string' || typeof b !== 'string') return undefined;
  return test(a, b);
}

/** A compiled pattern, or the compile error message of an invalid one. */
type CompiledRegex = { re: RegExp; error?: undefined } | { re?: undefined; error: string };

const regexCache = new Map<string, CompiledRegex>();

function getRegex(pattern: string): CompiledRegex {
  let hit = regexCache.get(pattern);
  if (hit === undefined) {
    try {
      hit = { re: new RegExp(pattern, 'i') };
    } catch (e) {
      hit = { error: errorMessage(e) };
    }
    if (regexCache.size < 256) regexCache.set(pattern, hit);
  }
  return hit;
}

/** A literal pattern that does not compile fails the query before it runs (spec §4.1). */
function checkLiteralRegex(pattern: Expr | undefined): void {
  if (pattern?.kind !== 'lit' || typeof pattern.value !== 'string') return;
  const { error } = getRegex(pattern.value);
  if (error !== undefined) throw new RqlError('invalid-regex', `invalid regex "${pattern.value}": ${error}`, pattern.span);
}

function matches(x: Exec, a: unknown, b: unknown): Truth {
  if (typeof a !== 'string' || typeof b !== 'string') return undefined;
  const { re, error } = getRegex(b);
  if (re === undefined) {
    x.warnOnce('invalid-regex', `invalid regex "${b}": ${error}; such values count as unknown`, 'invalid-regex');
    return undefined;
  }
  return re.test(a);
}

function isIn(x: Exec, a: unknown, b: unknown): Truth {
  if (isAbsent(a) || isAbsent(b) || b === null) return undefined;
  if (!isList(x, b)) return equal(x, a, b);
  for (const el of liveElements(x, b)) if (equal(x, a, el) === true) return true;
  return false;
}

function isType(x: Exec, a: unknown, b: unknown): Truth {
  if (isAbsent(a)) return undefined;
  if (a === null) return false;
  if (typeof b !== 'string') return undefined;
  return x.types.infoOf(a).isA(b);
}

// ------------------------------------------------------------------ arithmetic and text

export function arith(op: string, a: unknown, b: unknown): unknown {
  if (a === MISSING || b === MISSING) return MISSING;
  if (a instanceof ErrorValue) return a;
  if (b instanceof ErrorValue) return b;
  if (op === '+' && (typeof a === 'string' || typeof b === 'string')) return toText(undefined, a) + toText(undefined, b);
  const l = toNumber(a);
  const r = toNumber(b);
  if (l === undefined || r === undefined) return new ErrorValue('type-mismatch', `cannot apply '${op}' to ${describeValue(a)} and ${describeValue(b)}`);
  switch (op) {
    case 'neg':
      return -l;
    case '+':
      return l + r;
    case '-':
      return l - r;
    case '*':
      return l * r;
    case '/':
      return r === 0 ? new ErrorValue('div-by-zero') : l / r;
    case '%':
      return r === 0 ? new ErrorValue('div-by-zero') : l % r;
    default:
      return new ErrorValue('type-mismatch');
  }
}

/** Text form used for concatenation, map keys in output and sort fallbacks. */
export function toText(x: Exec | undefined, v: unknown): string {
  if (v === null || v === undefined) return '';
  if (typeof v === 'string') return v;
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  if (typeof v === 'number') return formatNumber(v);
  if (typeof v === 'bigint') return v.toString();
  if (v instanceof EnumValue) return v.name;
  if (v instanceof Date) return v.toISOString();
  if (typeof v === 'object') {
    const ts = (v as { toString?: unknown }).toString;
    if (typeof ts === 'function' && ts !== Object.prototype.toString && !Array.isArray(v)) return String(v);
    return x !== undefined ? x.types.infoOf(v).shortName : (v.constructor?.name ?? 'Object');
  }
  return String(v);
}

export function describeValue(v: unknown): string {
  if (v === null || v === undefined) return 'null';
  if (v === MISSING) return 'missing';
  if (v === UNKNOWN) return 'unknown';
  if (v instanceof ErrorValue) return v.toString();
  if (typeof v === 'string') return '"' + v + '"';
  if (typeof v === 'number') return 'Number';
  if (typeof v === 'boolean') return 'Boolean';
  if (v instanceof EnumValue) return v.type.name;
  return (v as object).constructor?.name ?? 'Object';
}

function len(x: Exec, v: unknown): unknown {
  if (v === null) return MISSING;
  if (isAbsent(v)) return v;
  if (typeof v === 'string') return v.length;
  if (typeof v !== 'object') return MISSING;
  const info = x.types.infoOf(v);
  return info.isList || info.isMap ? (sizeOf(v, x.types) ?? MISSING) : MISSING;
}

function caseOf(v: unknown, upper: boolean): unknown {
  if (v === null || isAbsent(v)) return v;
  if (typeof v !== 'string') return new ErrorValue('type-mismatch');
  return upper ? v.toUpperCase() : v.toLowerCase();
}

// ------------------------------------------------------------------ string functions (spec §4.3)

const STRING_ARITY: Record<string, [number, number]> = {
  replace: [3, 3],
  'replace-regex': [3, 3],
  substring: [2, 3],
  pad: [2, 3],
  split: [2, 2],
  trim: [1, 1],
};

/** The widest result `pad` builds. */
export const MAX_PAD_WIDTH = 1024;

const TRIM_CHARS = ' \t\n\r\v\f';

/** An integral number that fits a 32-bit int, or undefined. */
function integral(v: unknown): number | undefined {
  const d = toNumber(v);
  return d !== undefined && Number.isInteger(d) && Math.abs(d) <= 2147483647 ? d : undefined;
}

const escapeRegex = (s: string): string => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

const literalRegexes = new Map<string, RegExp>();
const globalRegexes = new Map<string, RegExp>();

/** A compiled regex from a bounded cache (like `getRegex`). Global regexes are safe to share: replace / split / matchAll reset or copy `lastIndex`. */
function cachedRegex(cache: Map<string, RegExp>, key: string, make: () => RegExp): RegExp {
  let re = cache.get(key);
  if (re === undefined) {
    re = make();
    if (cache.size < 256) cache.set(key, re);
  }
  return re;
}

/** Every occurrence of `needle` (ordinal case-insensitive, like `contains`), left to right, non-overlapping. */
function literalPattern(needle: string): RegExp {
  return cachedRegex(literalRegexes, needle, () => new RegExp(escapeRegex(needle), 'gi'));
}

function stringFunction(x: Exec, name: string, vals: unknown[]): unknown {
  if (name === 'pad') {
    // A number pads its text form: pad(3, 2) → "03".
    const v = vals[0];
    const text = typeof v === 'string' ? v : toNumber(v) !== undefined ? formatNumber(toNumber(v)!) : undefined;
    if (text === undefined) return new ErrorValue('type-mismatch', `pad() needs a string or a number, got ${describeValue(v)}`);
    const width = integral(vals[1]);
    if (width === undefined || width < 0 || width > MAX_PAD_WIDTH) return new ErrorValue('type-mismatch', `pad() width must be an integer from 0 to ${MAX_PAD_WIDTH}`);
    const fill = vals.length > 2 ? vals[2] : '0';
    if (typeof fill !== 'string' || fill.length !== 1) return new ErrorValue('type-mismatch', 'pad() fill must be a one-character string');
    return text.padStart(width, fill);
  }
  const s = vals[0];
  if (typeof s !== 'string') return new ErrorValue('type-mismatch', `${name}() needs a string, got ${describeValue(s)}`);
  switch (name) {
    case 'trim': {
      let a = 0;
      let b = s.length;
      while (a < b && TRIM_CHARS.includes(s[a]!)) a++;
      while (b > a && TRIM_CHARS.includes(s[b - 1]!)) b--;
      return s.slice(a, b);
    }
    case 'replace':
    case 'split': {
      const from = vals[1];
      const to = vals[2];
      if (typeof from !== 'string' || (name === 'replace' && typeof to !== 'string')) return new ErrorValue('type-mismatch', `${name}() needs string arguments`);
      if (name === 'split') return from.length === 0 ? new ErrorValue('type-mismatch', 'split() needs a non-empty separator') : s.split(literalPattern(from));
      return from.length === 0 ? s : s.replace(literalPattern(from), () => to as string);
    }
    case 'substring': {
      let start = integral(vals[1]);
      const want = vals.length > 2 ? integral(vals[2]) : undefined;
      if (start === undefined || (vals.length > 2 && want === undefined)) return new ErrorValue('type-mismatch', 'substring() needs integral start and length');
      if (want !== undefined && want < 0) return new ErrorValue('type-mismatch', 'substring() length must not be negative');
      const n = s.length;
      if (start < 0) start += n;
      start = Math.max(0, Math.min(n, start));
      const count = want === undefined ? n - start : Math.min(n - start, want);
      return s.substring(start, start + count);
    }
    default: {
      // replace-regex
      const pattern = vals[1];
      const repl = vals[2];
      if (typeof pattern !== 'string' || typeof repl !== 'string') return new ErrorValue('type-mismatch', 'replace-regex() needs string arguments');
      const { re, error } = getRegex(pattern);
      if (re === undefined) {
        const detail = `invalid regex "${pattern}": ${error}`;
        x.warnOnce('invalid-regex', detail, 'invalid-regex');
        return new ErrorValue('invalid-regex', detail);
      }
      const global = cachedRegex(globalRegexes, pattern, () => new RegExp(re.source, re.flags + 'g'));
      let out = '';
      let last = 0;
      for (const m of s.matchAll(global)) {
        out += s.slice(last, m.index) + expandReplacement(m, repl);
        last = m.index + m[0].length;
      }
      return out + s.slice(last);
    }
  }
}

/** Portable replacement text (spec §4.3): `$0`–`$9` insert a group (empty when absent), `$$` a dollar. */
function expandReplacement(m: RegExpMatchArray, repl: string): string {
  if (!repl.includes('$')) return repl;
  let out = '';
  for (let i = 0; i < repl.length; i++) {
    const ch = repl[i]!;
    const next = repl[i + 1];
    if (ch === '$' && next !== undefined) {
      if (next === '$') {
        out += '$';
        i++;
        continue;
      }
      if (next >= '0' && next <= '9') {
        out += m[next.charCodeAt(0) - 48] ?? '';
        i++;
        continue;
      }
    }
    out += ch;
  }
  return out;
}

function num(v: unknown, f: (d: number) => number): unknown {
  if (v === null || isAbsent(v)) return v;
  const d = toNumber(v);
  return d !== undefined ? f(d) : new ErrorValue('type-mismatch');
}

/** .NET Math.Round(v, digits, MidpointRounding.AwayFromZero). */
export function roundAwayFromZero(v: number, digits: number): number {
  if (!Number.isFinite(v) || Math.abs(v) >= 1e16) return v;
  const p = 10 ** digits;
  const s = v * p;
  const r = Math.trunc(s + Math.sign(s) * 0.49999999999999994);
  return r / p;
}

// ------------------------------------------------------------------ collection methods

/** List elements / map entries as they enter expressions: holes and destroyed objects read as null. */
function* liveElements(x: Exec, v: object, info: TypeInfo = x.types.infoOf(v)): Generator<unknown> {
  for (const el of elements(v, info)) yield el === undefined ? null : x.live(el);
}

/**
 * The list argument (a fresh copy) and a resolved index for insert / remove-at: negative indexes count from
 * the end (for insert, -1 appends); a non-list, non-integral or out-of-range index is a type-mismatch.
 */
function listAndIndex(x: Exec, fn: string, listValue: unknown, indexValue: unknown, inserting: boolean): { list: unknown[]; index: number } | ErrorValue {
  const list = listArg(x, listValue);
  if (list === undefined) return new ErrorValue('type-mismatch', `${fn}() expects a list first`);
  if (typeof indexValue !== 'number' || Math.floor(indexValue) !== indexValue) return new ErrorValue('type-mismatch', `${fn}() index must be an integer`);
  const size = inserting ? list.length + 1 : list.length;
  const index = indexValue < 0 ? size + indexValue : indexValue;
  if (index < 0 || index >= size) return new ErrorValue('type-mismatch', `${fn}() index ${formatNumber(indexValue)} out of range (count ${list.length})`);
  return { list, index };
}

/** A fresh copy of a list value (never the live collection), or undefined when the value is not a list. */
function listArg(x: Exec, v: unknown): unknown[] | undefined {
  if (!isList(x, v)) return undefined;
  return [...liveElements(x, v)];
}

function elementsOf(x: Exec, v: unknown): Iterable<unknown> | undefined {
  if (v === null || isAbsent(v) || typeof v !== 'object') return undefined;
  const info: TypeInfo = x.types.infoOf(v);
  return info.isList || info.isMap ? liveElements(x, v, info) : undefined;
}

function quantify(x: Exec, v: unknown, pred: Eval | undefined, kind: string): unknown {
  if (v === MISSING || v instanceof ErrorValue) return kind === 'count' || kind === 'first' ? MISSING : UNKNOWN;
  let els = elementsOf(x, v);
  if (els === undefined) {
    if (v === null) return kind === 'count' ? 0 : kind === 'first' ? MISSING : kind === 'all';
    // A single value behaves as a one-element collection.
    els = [v];
  }
  const saved = x.current;
  const savedPath = x.currentPath;
  const savedIndex = x.index;
  x.outer.push(saved);
  try {
    let count = 0;
    let sawUnknown = false;
    let i = 0;
    for (const raw of els) {
      x.visit(); // collection-method elements count toward maxVisited/timeout (spec §10)
      const el = raw === undefined ? null : raw;
      let tv: Truth = true;
      if (pred !== undefined) {
        x.current = el;
        x.currentPath = null;
        x.index = i;
        tv = truth(pred(x));
      }
      i++;
      switch (kind) {
        case 'any':
          if (tv === true) return true;
          if (tv === undefined) sawUnknown = true;
          break;
        case 'all':
          if (tv === false) return false;
          if (tv === undefined) sawUnknown = true;
          break;
        case 'first':
          if (tv === true) return el;
          break;
        default:
          if (tv === true) count++;
          break;
      }
    }
    switch (kind) {
      case 'any':
        return sawUnknown ? UNKNOWN : false;
      case 'all':
        return sawUnknown ? UNKNOWN : true;
      case 'first':
        return MISSING;
      default:
        return count;
    }
  } finally {
    x.outer.pop();
    x.current = saved;
    x.currentPath = savedPath;
    x.index = savedIndex;
  }
}

function aggregate(x: Exec, v: unknown, sel: Eval | undefined, kind: string): unknown {
  if (v === MISSING || v instanceof ErrorValue) return v;
  const els = elementsOf(x, v);
  if (els === undefined) return v === null ? null : MISSING;
  const saved = x.current;
  const savedPath = x.currentPath;
  x.outer.push(saved);
  try {
    const values: unknown[] = [];
    for (const raw of els) {
      x.visit();
      const el = raw === undefined ? null : raw;
      if (sel !== undefined) {
        x.current = el;
        x.currentPath = null;
        values.push(sel(x));
      } else values.push(el);
    }
    return reduce(x, values, kind);
  } finally {
    x.outer.pop();
    x.current = saved;
    x.currentPath = savedPath;
  }
}

/** Objects whose type has an ordering adapter (spec §13: ordering applies to min / max too). */
function hasOrdering(model: TypeModel, v: unknown): boolean {
  return typeof v === 'object' && v !== null && model.hasCapability('compare') && model.infoOf(v).adapterSet.compare.length > 0;
}

/**
 * sum / avg over numbers; min / max over numbers, strings, enums and adapter-ordered values (spec §4.3, §13).
 * null / missing are skipped silently; any other value that cannot take part is skipped too and reported once per
 * aggregate kind as a type-mismatch warning.
 */
export function reduce(x: Exec, values: Iterable<unknown>, kind: string): unknown {
  const model = x.types;
  let sum = 0;
  let n = 0;
  let ignored = 0;
  let firstIgnored = '';
  let best: unknown = null;
  const minmax = kind === 'min' || kind === 'max';
  for (const val of values) {
    if (val === null || val === undefined || val === MISSING) continue; // error / unknown values are counted as ignored below
    let used: boolean;
    if (minmax) {
      used = !isAbsent(val) && (toNumber(val) !== undefined || typeof val === 'string' || val instanceof EnumValue || hasOrdering(model, val));
      if (used && best === null) best = val;
      else if (used) {
        const c = compare(model, val, best);
        if (c === undefined) used = false;
        else if ((kind === 'min' && c < 0) || (kind === 'max' && c > 0)) best = val;
      }
    } else {
      const d = toNumber(val);
      used = d !== undefined;
      if (d !== undefined) {
        sum += d;
        n++;
      }
    }
    if (!used && ignored++ === 0) firstIgnored = describeValue(val);
  }
  if (ignored > 0) {
    const what = minmax ? 'value(s) not comparable with the others' : 'non-numeric value(s)';
    x.warnOnce('type-mismatch', `${kind} ignored ${ignored} ${what} (first: ${firstIgnored})`, 'aggregate:' + kind);
  }
  if (kind === 'sum') return sum;
  if (kind === 'avg') return n === 0 ? null : sum / n;
  return best;
}
