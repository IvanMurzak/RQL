/**
 * Lazy stage pipeline over live values (spec §2, §5): source binding, streaming stages as
 * generators, terminal stages and result assembly. Port of dotnet/Rql.Execution/Pipeline.cs.
 * Expressions are compiled eagerly (binding errors surface before any traversal); iteration is lazy
 * (`first` stops pulling upstream items).
 */
import {
  RqlError,
  diagnostic,
  failResult,
  isTerminal as isTerminalStage,
  segmentToString,
  type Expr,
  type GenericStage,
  type PathExpr,
  type QueryResult,
  type RecurseStage,
  type SelectStage,
  type SortStage,
  type Stage,
} from '@rql/contracts';
import { mapEntries, type MapLike } from './collections.js';
import { Compiler, compare, reduce, toText, type Eval } from './compiler.js';
import { describeRun, reachableTypes } from './describe.js';
import { Encoder } from './encoder.js';
import type { ArgEvaluator, SourceRegistration } from './engine.js';
import { PathNode, type Exec } from './exec.js';
import { Mutator } from './mutator.js';
import { closest, memberList } from './suggest.js';
import { EnumType, EnumValue, unwrapNullable, type TypeInfo } from './types.js';
import { ErrorValue, MISSING, MapEntry, RowValue, UNKNOWN, isAbsent, toNumber } from './values.js';

export interface Item {
  readonly value: unknown;
  readonly path: PathNode | null;
  /** Depth within the most recent recurse stage (0 = start item); -1 outside recurse. */
  readonly depth: number;
}

const item = (value: unknown, path: PathNode | null, depth = -1): Item => ({ value, path, depth });

export class Pipeline {
  private readonly c: Compiler;

  constructor(private readonly x: Exec) {
    this.c = new Compiler(x);
    x.trackPaths = x.engine.alwaysTrackPaths || usesMeta(x.plan, 'path');
  }

  run(): QueryResult {
    const x = this.x;
    let result: QueryResult;
    try {
      result = this.runCore();
    } catch (e) {
      if (!(e instanceof RqlError)) throw e;
      const fail = failResult(x.diagnostics.filter((d) => d.severity === 'error' && d.code !== e.code));
      fail.diagnostics.unshift(e.toDiagnostic());
      this.addWarnings(fail, true);
      // A warning of the failure's own code (e.g. a throwing function) only repeats the error.
      fail.diagnostics = fail.diagnostics.filter((d) => d.severity !== 'warning' || d.code !== e.code);
      return fail;
    }
    const errors = x.diagnostics.filter((d) => d.severity === 'error');
    if (errors.length > 0) {
      const seen = new Set<string>();
      return failResult(errors.filter((d) => !seen.has(d.message) && seen.add(d.message) !== undefined));
    }
    this.addWarnings(result, false);
    return result;
  }

  private addWarnings(result: QueryResult, onlyGeneral: boolean): void {
    const x = this.x;
    for (const d of x.diagnostics) if (d.severity === 'warning') result.diagnostics.push(d);
    if (x.getterErrors > 0) {
      result.diagnostics.push(diagnostic('warning', 'getter-threw', `${x.getterErrors} member read(s) failed; first: ${x.firstGetterError ?? ''}`));
    }
    if (onlyGeneral) return;
    for (const site of x.isSites) {
      if (site.matched > 0 || site.evaluated === 0) continue;
      const holders = this.holders(site.typeName);
      const hint =
        holders.length > 0
          ? `${site.typeName} objects are reached via ${holders.join(', ')}`
          : `no reachable type is named '${site.typeName}'; use rql_schema / describe to list types`;
      const seen = [...site.seen].map((t) => t.shortName).join(', ');
      result.diagnostics.push(
        diagnostic('warning', 'no-match', `none of the ${site.evaluated} tested item(s) is a ${site.typeName} (saw ${seen})`, hint, site.span),
      );
    }
    const reported = new Set<string>();
    // Static check for never-evaluated bare names (short-circuit `and`/`or`, empty streams):
    // a name that no reachable type declares and no select produces is certainly a typo.
    let known: ReadonlyMap<string, string> | undefined;
    for (const site of x.sites) {
      if (!site.bare || site.hits > 0 || site.misses > 0 || reported.has(site.name)) continue;
      known ??= this.knownNames();
      if (known.has(site.name.toUpperCase())) continue;
      reported.add(site.name);
      const close = closest(site.name, known.values(), 3);
      result.diagnostics.push(
        diagnostic(
          'warning',
          'unknown-member',
          `no type reachable from the sources has a member '${site.name}' (not evaluated)`,
          close.length > 0 ? `did you mean ${close.join(' / ')}?` : `if '${site.name}' is a value, quote it: "${site.name}"`,
          site.span,
        ),
      );
    }
    for (const site of x.sites) {
      if (site.hits > 0 || site.misses === 0 || reported.has(site.name)) continue;
      reported.add(site.name);
      if (site.rowColumns !== undefined && site.missTypes.size === 0) {
        result.diagnostics.push(
          diagnostic(
            'warning',
            'unknown-member',
            `no column '${site.name}' in the projected rows (columns: ${site.rowColumns.join(', ')})`,
            'select produces rows with only the listed columns; to navigate into objects use the path in the source or `get Path` (e.g. world.LocalPlayer.Inventory.Slots | select Item.Name)',
            site.span,
          ),
        );
        continue;
      }
      const types = [...site.missTypes];
      const typeNames = [...new Set(types.map((t) => t.shortName))].slice(0, 4).join(', ');
      const candidates = [...new Set(types.flatMap((t) => t.members.map((m) => m.name)))];
      const close = closest(site.name, candidates, 3);
      let hint = close.length > 0 ? `did you mean ${close.join(' / ')}?` : candidates.length > 0 ? memberList(candidates) : '';
      if (close.length === 0 && /^\p{Lu}/u.test(site.name)) {
        hint += (hint.length > 0 ? '; ' : '') + `if '${site.name}' is a value, quote it: "${site.name}"`;
      }
      result.diagnostics.push(diagnostic('warning', 'unknown-member', `no member '${site.name}' on ${typeNames}`, hint, site.span));
    }
  }

  /** Known member names (keyed upper-cased) plus the plan's select field names. */
  private knownNames(): ReadonlyMap<string, string> {
    const names = new Map(this.x.engine.knownMemberNames());
    for (const st of this.x.plan.stages) {
      if (st.kind === 'select') for (const f of st.fields) if (!names.has(f.name.toUpperCase())) names.set(f.name.toUpperCase(), f.name);
    }
    return names;
  }

  /** Where objects of a type live: sources and "Type.Member" paths, from declared reachability. */
  private holders(typeName: string): string[] {
    const x = this.x;
    const roots: { source: string; info: TypeInfo }[] = [];
    for (const s of x.engine.sources.values()) {
      let v: unknown;
      try {
        v = s.getter();
      } catch {
        continue;
      }
      if (v !== null && v !== undefined) roots.push({ source: s.name, info: x.types.infoOf(v) });
    }
    const types = reachableTypes(x.types, roots.map((r) => r.info));
    const targets = types.filter((t) => t.isA(typeName));
    if (targets.length === 0) return [];
    const holds = (t: TypeInfo | undefined): boolean => t !== undefined && targets.some((tg) => tg === t || tg.isA(t.shortName));
    const out = new Set<string>();
    for (const r of roots) {
      if (r.info.isList || r.info.isMap ? false : holds(r.info)) out.add('source ' + r.source);
    }
    for (const t of types) {
      for (const m of t.members) {
        if (t.base?.members.includes(m)) continue;
        const declared = unwrapNullable(m.type);
        let el = declared;
        if (typeof el === 'object' && !(el instanceof EnumType) && (el.kind === 'list' || el.kind === 'map')) el = unwrapNullable(el.of);
        if (typeof el === 'object' && !(el instanceof EnumType) && el.kind === 'class' && holds(x.types.infoForCtor(el.ctor))) out.add(t.shortName + '.' + m.name);
      }
    }
    return [...out].slice(0, 4);
  }

  private runCore(): QueryResult {
    const x = this.x;
    const plan = x.plan;
    const last = plan.stages[plan.stages.length - 1];
    if (x.options.allowMutation && last?.kind !== 'set' && last?.kind !== 'call') {
      throw new RqlError(
        'invalid-plan',
        'a mutation request must end with a set or call stage; nothing was changed',
        undefined,
        "add '| set Member = value' or '| call Method(...)', or use the query tool to read",
      );
    }
    let stream: Iterable<Item> = this.compileSource(plan.source);
    let terminal: Stage | undefined;
    for (const st of plan.stages) {
      if (isTerminalStage(st)) {
        terminal = st;
        break;
      }
      stream = this.apply(stream, st);
    }
    const encoder = new Encoder(x);
    if (terminal === undefined) return this.collect(stream, encoder);
    switch (terminal.kind) {
      case 'simple': {
        if (terminal.op === 'count') {
          let n = 0;
          for (const _ of stream) n++;
          return { ok: true, kind: 'scalar', value: n, diagnostics: [], stats: { visited: 0, membersRead: 0, elapsedMs: 0 } };
        }
        return describeRun(x, values(stream));
      }
      case 'expr': {
        const sel = this.c.compile(terminal.expr!);
        const r = reduce(x, this.values(stream, sel), terminal.op);
        return { ok: true, kind: 'scalar', value: encoder.encode(r), diagnostics: [], stats: { visited: 0, membersRead: 0, elapsedMs: 0 } };
      }
      case 'set':
        return new Mutator(x, this.c, encoder).set(stream, terminal);
      case 'call':
        return new Mutator(x, this.c, encoder).call(stream, terminal);
      default:
        throw new RqlError('unsupported', `stage '${terminal.op}' is not supported`);
    }
  }

  private *values(stream: Iterable<Item>, sel: Eval): Generator<unknown> {
    const x = this.x;
    let i = 0;
    for (const it of stream) {
      x.current = it.value;
      x.currentPath = it.path;
      x.currentDepth = it.depth;
      x.index = i++;
      yield sel(x);
    }
  }

  // ---------------------------------------------------------------- source

  private compileSource(src: Expr): Iterable<Item> {
    const x = this.x;
    if (src.kind === 'path' && src.on === undefined && src.segments.length > 0 && src.segments[0]!.kind === 'member') {
      // Longest dotted prefix that matches a registered source (exact, then case-insensitive).
      let memberPrefix = 0;
      while (memberPrefix < src.segments.length && src.segments[memberPrefix]!.kind === 'member') memberPrefix++;
      for (let len = memberPrefix; len >= 1; len--) {
        const name = src.segments.slice(0, len).map(memberName).join('.');
        const reg = this.findSource(name);
        if (reg === undefined) continue;
        const rest: PathExpr = { kind: 'path', segments: src.segments.slice(len), ...(src.span !== undefined ? { span: src.span } : {}) };
        const nav = rest.segments.length > 0 ? this.c.compile(rest) : undefined;
        return this.sourceItems(reg, nav, reg.name + pathSuffixText(rest));
      }
      const head = memberName(src.segments[0]!);
      const names = [...x.engine.sources.keys()];
      const close = closest(head, names, 3);
      throw new RqlError('unknown-source', `unknown source '${head}'`, src.span, (close.length > 0 ? `did you mean ${close.join(' / ')}? ` : '') + 'sources: ' + names.join(' '));
    }
    if (src.kind === 'call' && src.on === undefined && src.name.toLowerCase() === 'ref') {
      return this.refItems(this.c.compile(src));
    }
    throw new RqlError('unknown-source', 'query must start with a source name (e.g. world) or ref("Type#id")', src.span, 'sources: ' + [...x.engine.sources.keys()].join(' '));
  }

  private findSource(name: string): SourceRegistration | undefined {
    const sources = this.x.engine.sources;
    const exact = sources.get(name);
    if (exact !== undefined) return exact;
    const up = name.toUpperCase();
    const ci = [...sources.values()].filter((s) => s.name.toUpperCase() === up);
    return ci.length === 1 ? ci[0] : undefined;
  }

  private *sourceItems(reg: SourceRegistration, nav: Eval | undefined, pathText: string): Generator<Item> {
    const x = this.x;
    const root = normalize(reg.getter());
    let value: unknown = root;
    if (nav !== undefined) {
      x.current = root;
      x.currentPath = null;
      value = nav(x);
    }
    if (value instanceof ErrorValue) throw new RqlError(value.code, `source '${pathText}' failed: ${value.detail ?? value.code}`);
    if (value === MISSING) return;
    yield* this.expand(value, x.trackPaths ? new PathNode(null, pathText) : null);
  }

  private *refItems(ev: Eval): Generator<Item> {
    const x = this.x;
    x.current = null;
    const v = ev(x);
    if (v instanceof ErrorValue) return; // diagnostic already recorded
    yield* this.expand(v, null);
  }

  /** Lists → elements, maps → entries, else the value itself; null/missing and destroyed objects dropped. */
  private *expand(value: unknown, path: PathNode | null): Generator<Item> {
    const x = this.x;
    if (value === null || value === undefined || value === MISSING || value === UNKNOWN || value instanceof ErrorValue || x.dead(value)) return;
    if (value instanceof RowValue && value.columns.length === 1) {
      yield* this.expand(value.values[0], path);
      return;
    }
    const info = typeof value === 'object' ? x.types.infoOf(value) : undefined;
    if (info?.isList) {
      let i = 0;
      for (const el of value as Iterable<unknown>) {
        x.visit();
        const p = path !== null ? path.at(i) : null;
        i++;
        if (el === null || el === undefined || x.dead(el)) continue;
        yield item(el, p);
      }
      return;
    }
    if (info?.isMap) {
      for (const e of mapEntries(value as MapLike)) {
        x.visit();
        yield item(e, path !== null ? path.key(e.Key) : null);
      }
      return;
    }
    x.visit();
    yield item(value, path);
  }

  // ---------------------------------------------------------------- stages

  private apply(input: Iterable<Item>, st: Stage): Iterable<Item> {
    switch (st.kind) {
      case 'where':
        return this.where(input, this.c.compile(st.pred));
      case 'select':
        return this.select(input, st);
      case 'sort':
        return this.sort(input, st);
      case 'limit':
        return st.op === 'first' ? first(input, st.n) : skip(input, st.n);
      case 'expr':
        if (st.op === 'get') return this.get(input, st.expr!);
        if (st.op === 'distinct') return this.distinct(input, st.expr !== undefined ? this.c.compile(st.expr) : undefined);
        if (st.op === 'group-by') return this.groupBy(input, this.c.compile(st.expr!));
        break;
      case 'recurse':
        return this.recurse(input, st);
      case 'generic':
        return this.generic(input, st);
      default:
        break;
    }
    throw new RqlError('unsupported', `stage '${st.op}' is not supported here`, st.span);
  }

  private enter(it: Item, i: number): void {
    const x = this.x;
    x.current = it.value;
    x.currentPath = it.path;
    x.currentDepth = it.depth;
    x.index = i;
  }

  private *where(input: Iterable<Item>, pred: Eval): Generator<Item> {
    let i = 0;
    for (const it of input) {
      this.enter(it, i++);
      if (pred(this.x) === true) yield it;
    }
  }

  private select(input: Iterable<Item>, s: SelectStage): Iterable<Item> {
    const cols = s.fields.map((f) => f.name);
    const evals = s.fields.map((f) => this.c.compile(f.expr));
    return this.selectIter(input, cols, evals);
  }

  private *selectIter(input: Iterable<Item>, cols: readonly string[], evals: readonly Eval[]): Generator<Item> {
    let i = 0;
    for (const it of input) {
      this.enter(it, i++);
      const vals = new Array<unknown>(evals.length);
      for (let k = 0; k < evals.length; k++) vals[k] = evals[k]!(this.x);
      yield item(new RowValue(cols, vals), null);
    }
  }

  private sort(input: Iterable<Item>, s: SortStage): Iterable<Item> {
    const keys = s.keys.map((k) => ({ ev: this.c.compile(k.expr), desc: k.desc }));
    return this.sortIter(input, keys);
  }

  private *sortIter(input: Iterable<Item>, keys: readonly { ev: Eval; desc: boolean }[]): Generator<Item> {
    const rows: { it: Item; kv: unknown[] }[] = [];
    let i = 0;
    for (const it of input) {
      this.enter(it, i++);
      rows.push({ it, kv: keys.map((k) => k.ev(this.x)) });
    }
    this.x.checkTime();
    // Array.prototype.sort is stable.
    rows.sort((a, b) => {
      for (let k = 0; k < keys.length; k++) {
        const c = compareSortKeys(this.x, a.kv[k], b.kv[k], keys[k]!.desc);
        if (c !== 0) return c;
      }
      return 0;
    });
    for (const r of rows) yield r.it;
  }

  private get(input: Iterable<Item>, e: Expr): Iterable<Item> {
    return this.getIter(input, this.c.compile(e), pathSuffix(e));
  }

  private *getIter(input: Iterable<Item>, ev: Eval, suffix: string | undefined): Generator<Item> {
    const x = this.x;
    let i = 0;
    for (const it of input) {
      this.enter(it, i++);
      const v = ev(x);
      if (v instanceof ErrorValue) {
        x.getterErrors++;
        x.firstGetterError ??= v.detail ?? v.code;
        continue;
      }
      const p = x.trackPaths && it.path !== null && suffix !== undefined ? new PathNode(it.path, suffix) : null;
      yield* this.expand(v, p);
    }
  }

  private *distinct(input: Iterable<Item>, key: Eval | undefined): Generator<Item> {
    const seen = new Set<unknown>();
    let i = 0;
    for (const it of input) {
      let k = this.x.live(it.value);
      if (key !== undefined) {
        this.enter(it, i);
        k = key(this.x);
      }
      i++;
      const dk = distinctKey(this.x, k);
      if (!seen.has(dk)) {
        seen.add(dk);
        yield it;
      }
    }
  }

  /** group-by: rows {key, count} in order of first appearance (null/missing keys grouped as null). */
  private *groupBy(input: Iterable<Item>, key: Eval): Generator<Item> {
    const groups = new Map<unknown, { key: unknown; count: number }>();
    const NULL_GROUP = Symbol('null');
    for (const v of this.values(input, key)) {
      const k = v === MISSING || v === UNKNOWN ? null : v;
      const dk = k === null ? NULL_GROUP : distinctKey(this.x, k);
      const g = groups.get(dk);
      if (g !== undefined) g.count++;
      else groups.set(dk, { key: k, count: 1 });
    }
    const cols = ['key', 'count'];
    for (const g of groups.values()) yield item(new RowValue(cols, [g.key, g.count]), null);
  }

  private recurse(input: Iterable<Item>, r: RecurseStage): Iterable<Item> {
    const via = r.via?.map((v) => ({ ev: this.c.compile(v), suffix: pathSuffix(v) }));
    return this.recurseIter(input, r, via);
  }

  private *recurseIter(input: Iterable<Item>, r: RecurseStage, via: readonly { ev: Eval; suffix: string | undefined }[] | undefined): Generator<Item> {
    const x = this.x;
    let depthLimit = r.maxDepth ?? x.engine.defaults.defaultRecurseDepth;
    if (depthLimit > x.maxDepth) {
      x.warn('budget-exceeded', `recurse max-depth ${depthLimit} clamped to ${x.maxDepth}`);
      depthLimit = x.maxDepth;
    }
    const visited = new Set<object>();
    type Frame = { v: unknown; path: PathNode | null; depth: number; emit: boolean };
    for (const it of input) {
      if (it.value === null) continue;
      const stack: Frame[] = [{ v: it.value, path: it.path, depth: 0, emit: r.includeSelf }];
      while (stack.length > 0) {
        const { v, path, depth, emit } = stack.pop()!;
        // A destroyed object reads as null: not emitted, no getters run on it.
        if (v === null || v === undefined || x.dead(v)) continue;
        // Identity-tracked: reference objects (not primitives or value-like structs).
        if (typeof v === 'object' && x.types.infoOf(v).kind !== 'struct') {
          if (visited.has(v)) continue;
          visited.add(v);
        }
        if (emit) yield item(v, path, depth);
        if (depth >= depthLimit) continue;
        const children: { v: unknown; path: PathNode | null }[] = [];
        if (via !== undefined) {
          for (const { ev, suffix } of via) {
            x.current = v;
            x.currentPath = path;
            x.currentDepth = depth;
            const cv = ev(x);
            const cp = x.trackPaths && path !== null && suffix !== undefined ? new PathNode(path, suffix) : null;
            for (const child of this.expand(cv, cp)) children.push({ v: child.value, path: child.path });
          }
        } else {
          this.genericChildren(v, path, children);
        }
        // Push in reverse so the first child is visited first (pre-order).
        for (let k = children.length - 1; k >= 0; k--) stack.push({ v: children[k]!.v, path: children[k]!.path, depth: depth + 1, emit: true });
      }
    }
  }

  private genericChildren(v: unknown, path: PathNode | null, into: { v: unknown; path: PathNode | null }[]): void {
    const x = this.x;
    if (typeof v !== 'object' || v === null) return;
    const isObj = (o: unknown): boolean => typeof o === 'object' && o !== null && x.types.infoOf(o).isObject && !(o instanceof MapEntry);
    const addElements = (coll: unknown, p: PathNode | null): void => {
      for (const c of this.expand(coll, p)) {
        const cv = c.value instanceof MapEntry ? c.value.Value : c.value;
        if (isObj(cv)) into.push({ v: cv, path: c.path });
      }
    };
    const info = x.types.infoOf(v);
    if (info.isList || info.isMap) {
      addElements(v, path);
      return;
    }
    if (!info.isObject) return;
    for (const m of info.implicitMembersOf(v)) {
      if (!m.owning || m.declaredValueLike) continue;
      let cv: unknown;
      x.membersRead++;
      try {
        cv = x.live(m.get(v));
      } catch (e) {
        x.getterThrew(m.name, e);
        continue;
      }
      if (typeof cv !== 'object' || cv === null) continue;
      const p = x.trackPaths && path !== null ? path.member(m.name) : null;
      const ci = x.types.infoOf(cv);
      if (ci.isObject) {
        x.visit();
        into.push({ v: cv, path: p });
      } else if (ci.isList || ci.isMap) addElements(cv, p);
    }
  }

  private generic(input: Iterable<Item>, g: GenericStage): Iterable<Item> {
    const x = this.x;
    const handler = x.engine.stages.get(g.op);
    if (handler === undefined) {
      const names = [...x.engine.stages.keys()];
      const close = closest(g.op, names, 3);
      throw new RqlError('unknown-stage', `unknown stage '${g.op}'`, g.span, close.length > 0 ? `did you mean ${close.join(' / ')}?` : 'registered extension stages: ' + names.join(' '));
    }
    const make = (e: Expr): ArgEvaluator => {
      const ev = this.c.compile(e);
      return (it) => {
        const saved = x.current;
        x.current = it;
        try {
          return ev(x);
        } finally {
          x.current = saved;
        }
      };
    };
    const args = g.args.map(make);
    const named = new Map<string, ArgEvaluator>();
    for (const [k, e] of g.named) named.set(k, make(e));
    return this.genericIter(input, () => handler(values(input), args, named));
  }

  private *genericIter(_input: Iterable<Item>, start: () => Iterable<unknown>): Generator<Item> {
    for (const v of start()) {
      this.x.visit();
      yield item(v === undefined ? null : v, null);
    }
  }

  // ---------------------------------------------------------------- output

  private collect(stream: Iterable<Item>, enc: Encoder): QueryResult {
    const x = this.x;
    const collected: unknown[] = [];
    let total = 0;
    for (const it of stream) {
      if (total < x.maxRows) collected.push(it.value);
      total++;
    }
    // Rows only when EVERY item is a projected row (a later stage can mix rows and plain values).
    const rows = collected.length > 0 ? collected.every((c) => c instanceof RowValue) : this.lastIsRows();
    let r: QueryResult;
    if (rows) {
      const cols = collected.length > 0 ? (collected[0] as RowValue).columns : this.lastColumns();
      r = enc.rows(cols, collected.map((c) => (c as RowValue).values));
    } else {
      r = enc.items(collected);
    }
    r.total = total;
    if (total > collected.length) r.truncated = true;
    return r;
  }

  /** Whether the (empty) stream would have carried projected rows. */
  private lastIsRows(): boolean {
    let rows = false;
    for (const st of this.x.plan.stages) {
      if (st.kind === 'select' || (st.kind === 'expr' && st.op === 'group-by')) rows = true;
      else if ((st.kind === 'expr' && st.op !== 'distinct') || st.kind === 'recurse' || st.kind === 'generic') rows = false;
    }
    return rows;
  }

  private lastColumns(): string[] {
    const st = [...this.x.plan.stages].reverse().find((s) => s.kind === 'select' || (s.kind === 'expr' && s.op === 'group-by'));
    return st?.kind === 'select' ? st.fields.map((f) => f.name) : ['key', 'count'];
  }
}

// ------------------------------------------------------------------ helpers

function normalize(v: unknown): unknown {
  return v === undefined ? null : v;
}

function memberName(s: PathExpr['segments'][number]): string {
  return s.kind === 'member' ? s.name : segmentToString(s);
}

function pathSuffixText(p: PathExpr): string {
  return p.segments.map((s) => (s.kind === 'member' ? '.' + s.name : segmentToString(s))).join('');
}

/** Path text appended by `get` / `recurse via` when the expression is a plain member/index path. */
function pathSuffix(e: Expr): string | undefined {
  if (e.kind !== 'path' || e.on !== undefined || e.segments.some((s) => s.kind === 'meta')) return undefined;
  return pathSuffixText(e);
}

function* values(stream: Iterable<Item>): Generator<unknown> {
  for (const it of stream) yield it.value;
}

function* first(input: Iterable<Item>, n: number): Generator<Item> {
  if (n <= 0) return;
  let i = 0;
  for (const it of input) {
    yield it;
    if (++i >= n) return;
  }
}

function* skip(input: Iterable<Item>, n: number): Generator<Item> {
  let i = 0;
  for (const it of input) if (i++ >= n) yield it;
}

/** Sort rank: number/enum < string < bool < other; null/missing last in both directions (spec §5). */
function rank(v: unknown): number {
  if (v === null || v === undefined || isAbsent(v)) return 9;
  if (toNumber(v) !== undefined || v instanceof EnumValue) return 0;
  if (typeof v === 'string') return 1;
  if (typeof v === 'boolean') return 2;
  return 3;
}

function compareSortKeys(x: Exec, a: unknown, b: unknown, desc: boolean): number {
  const ra = rank(a);
  const rb = rank(b);
  if (ra === 9 || rb === 9) return ra - rb;
  if (ra !== rb) return desc ? rb - ra : ra - rb;
  let c: number;
  if (ra === 2) c = Number(a) - Number(b);
  else {
    const cmp = compare(x.types, a, b);
    if (cmp !== undefined) c = cmp;
    else {
      const ta = toText(x, a);
      const tb = toText(x, b);
      c = ta < tb ? -1 : ta > tb ? 1 : 0;
    }
  }
  return desc ? -c : c;
}

const enumIds = new WeakMap<object, number>();
let nextEnumId = 0;

/** A stable identity number for objects compared by identity inside composite (row) keys. */
const objectIds = new WeakMap<object, number>();
let nextObjectId = 0;
function objectId(o: object): number {
  let id = objectIds.get(o);
  if (id === undefined) objectIds.set(o, (id = nextObjectId++));
  return id;
}

/**
 * Key for distinct/group-by equality: numbers numerically, strings ordinally, enums by type+value,
 * structs member-wise, objects by identity (the object itself is the key).
 */
export function distinctKey(x: Exec, v: unknown): unknown {
  if (v === null || v === undefined) return 'n:';
  const n = toNumber(v);
  if (n !== undefined) return 'd:' + String(n === 0 ? 0 : n);
  if (typeof v === 'string') return 's:' + v;
  if (typeof v === 'boolean') return v ? 'b:1' : 'b:0';
  if (v instanceof EnumValue) {
    let id = enumIds.get(v.type);
    if (id === undefined) enumIds.set(v.type, (id = nextEnumId++));
    return `e:${id}:${typeof v.value}:${String(v.value)}`;
  }
  if (v instanceof Date) return 't:' + String(v.getTime());
  if (v instanceof RowValue) {
    // Projected rows compare by their column values (spec §5), each with the same equality.
    const parts = v.values.map((cv) => {
      const k = distinctKey(x, cv);
      if (typeof k === 'string') return JSON.stringify(k);
      return typeof k === 'object' && k !== null ? 'o:' + String(objectId(k)) : 'p:' + String(k);
    });
    return `r:[${parts.join(',')}]`;
  }
  if (typeof v === 'object') {
    const info = x.types.infoOf(v);
    if (info.kind === 'struct') {
      const parts = info.implicitMembersOf(v).map((m) => {
        const mv = m.get(v);
        const k = distinctKey(x, mv);
        return typeof k === 'string' ? k : '#';
      });
      return `v:${info.shortName}(${parts.join(',')})`;
    }
    return v;
  }
  return v;
}

/** Whether any expression of the plan uses the meta member `$name`. */
function usesMeta(plan: Exec['plan'], meta: string): boolean {
  const inExpr = (e: Expr): boolean => {
    switch (e.kind) {
      case 'path':
        return e.segments.some((s) => s.kind === 'meta' && s.name === meta) || (e.on !== undefined && inExpr(e.on));
      case 'op':
        return e.args.some(inExpr);
      case 'call':
        return e.args.some(inExpr) || (e.on !== undefined && inExpr(e.on));
      case 'list':
        return e.items.some(inExpr);
      case 'object':
        return e.entries.some((en) => inExpr(en.value));
      default:
        return false;
    }
  };
  if (inExpr(plan.source)) return true;
  for (const st of plan.stages) {
    switch (st.kind) {
      case 'where':
        if (inExpr(st.pred)) return true;
        break;
      case 'select':
        if (st.fields.some((f) => inExpr(f.expr))) return true;
        break;
      case 'sort':
        if (st.keys.some((k) => inExpr(k.expr))) return true;
        break;
      case 'expr':
        if (st.expr !== undefined && inExpr(st.expr)) return true;
        break;
      case 'recurse':
        if (st.via?.some(inExpr)) return true;
        break;
      case 'set':
        if (st.assign.some((a) => inExpr(a.target) || inExpr(a.value))) return true;
        break;
      case 'call':
        if (st.args.some(inExpr)) return true;
        break;
      case 'generic':
        if (st.args.some(inExpr) || [...st.named.values()].some(inExpr)) return true;
        break;
      default:
        break;
    }
  }
  return false;
}
