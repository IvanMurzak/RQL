/** Permissioned, all-or-nothing `set` / `call` (spec §8). Port of dotnet/Rql.Execution/Mutator.cs. */
import { RqlError, segmentToString, type CallStage, type PathExpr, type QueryResult, type Segment, type SetStage } from '@rql/contracts';
import { findMapKey, isMapLike, mapGet, type MapLike } from './collections.js';
import { describeValue, type Compiler, type Eval } from './compiler.js';
import type { Encoder } from './encoder.js';
import { errorMessage, type Exec } from './exec.js';
import type { Item } from './pipeline.js';
import { closest } from './suggest.js';
import {
  EnumType,
  EnumValue,
  RecordMap,
  friendlyName,
  isNullable,
  unwrapNullable,
  type MemberAccessor,
  type MethodAccessor,
  type TypeInfo,
  type TypeModel,
  type TypeRef,
} from './types.js';
import { ErrorValue, MISSING, MapEntry, RowValue, UNKNOWN, isAbsent, toNumber } from './values.js';

interface PlannedWrite {
  item: object;
  segments: readonly Segment[];
  label: string;
  old: unknown;
  new: unknown;
}

abstract class Slot {
  abstract readonly type: TypeRef;
  abstract get(container: object): unknown;
  abstract set(container: object, value: unknown): void;
  writable(_x: Exec): boolean {
    return true;
  }
  get denyCode(): 'readonly-member' | 'permission-denied' {
    return 'readonly-member';
  }
  get denyReason(): string {
    return '';
  }
}

class MemberSlot extends Slot {
  private policyDenied = false;
  constructor(
    private readonly declaring: TypeInfo,
    readonly m: MemberAccessor,
  ) {
    super();
  }
  get type(): TypeRef {
    return this.m.type;
  }
  get(container: object): unknown {
    return this.m.get(container);
  }
  set(container: object, value: unknown): void {
    if (this.m.set === undefined) throw new TypeError(`member '${this.m.name}' has no setter`);
    this.m.set(container, value);
  }
  override writable(x: Exec): boolean {
    if (!this.m.canWrite) return false;
    const policy = x.engine.mutations.canWrite;
    if (policy !== undefined && !policy(this.declaring, this.m)) {
      this.policyDenied = true;
      return false;
    }
    return true;
  }
  override get denyCode(): 'readonly-member' | 'permission-denied' {
    return this.policyDenied ? 'permission-denied' : 'readonly-member';
  }
  override get denyReason(): string {
    return this.policyDenied ? ' (denied by host policy)' : ' (read-only member)';
  }
}

class ListSlot extends Slot {
  constructor(
    private readonly index: number,
    readonly type: TypeRef,
  ) {
    super();
  }
  private idx(l: unknown[]): number {
    return this.index < 0 ? l.length + this.index : this.index;
  }
  get(container: object): unknown {
    const l = container as unknown[];
    const v = l[this.idx(l)];
    return v === undefined ? null : v;
  }
  set(container: object, value: unknown): void {
    const l = container as unknown[];
    l[this.idx(l)] = value instanceof EnumValue && unwrapNullable(this.type) instanceof EnumType ? value.value : value;
  }
}

class MapSlot extends Slot {
  constructor(
    private readonly key: unknown,
    readonly type: TypeRef,
  ) {
    super();
  }
  get(container: object): unknown {
    const m = container as MapLike;
    const k = findMapKey(m, this.key);
    if (!k.found) return MISSING;
    const v = mapGet(m, k.key);
    return v === undefined ? null : v;
  }
  set(container: object, value: unknown): void {
    const m = container as MapLike;
    const raw = value instanceof EnumValue && unwrapNullable(this.type) instanceof EnumType ? value.value : value;
    if (m instanceof RecordMap) {
      const k = String(this.key);
      if (value === MISSING) m.delete(k);
      else m.set(k, raw);
    } else if (value === MISSING) m.delete(this.key);
    else m.set(this.key, raw);
  }
}

const typeArgs = (r: TypeRef): { key: TypeRef; of: TypeRef } | undefined => {
  const u = unwrapNullable(r);
  if (typeof u !== 'object' || u instanceof EnumType) return undefined;
  if (u.kind === 'list') return { key: 'int', of: u.of };
  if (u.kind === 'map') return { key: u.key, of: u.of };
  return undefined;
};

export class Mutator {
  constructor(
    private readonly x: Exec,
    private readonly c: Compiler,
    private readonly enc: Encoder,
  ) {}

  private checkPolicy(op: string): void {
    if (!this.x.engine.mutations.enabled) throw new RqlError('permission-denied', `'${op}' is not allowed: this host does not permit mutations`);
    if (!this.x.options.allowMutation) throw new RqlError('permission-denied', `'${op}' requires a mutation request (use the mutate tool / options.allowMutation)`);
  }

  private targets(stream: Iterable<Item>, op: string): Item[] {
    const list: Item[] = [];
    for (const it of stream) {
      if (it.value === null || it.value === undefined) continue;
      if (it.value instanceof RowValue) throw new RqlError('invalid-plan', `'${op}' needs objects, not projected rows; remove 'select' before '${op}'`);
      if (it.value instanceof MapEntry) {
        throw new RqlError('invalid-plan', `'${op}' cannot target map entries; set the map member with a key path instead (Stats["key"] = ...)`);
      }
      list.push(it);
      if (list.length > this.x.maxMutationTargets) {
        throw new RqlError(
          'mutation-limit',
          `more than ${this.x.maxMutationTargets} objects matched '${op}'; nothing was changed`,
          undefined,
          'narrow the query (where / first / ref(...)) or raise budgets.maxMutationTargets if the bulk change is intended',
        );
      }
    }
    return list;
  }

  private refOf(v: unknown): string {
    return typeof v === 'object' && v !== null ? this.x.engine.refOf(v) : describeValue(v);
  }

  // ---------------------------------------------------------------- set

  set(stream: Iterable<Item>, s: SetStage): QueryResult {
    const x = this.x;
    this.checkPolicy('set');
    const assigns = s.assign.map((a) => ({ target: a.target, value: this.c.compile(a.value), label: label(a.target) }));
    const targets = this.targets(stream, 'set');
    const writes: PlannedWrite[] = [];
    for (const it of targets) {
      for (const { target, value, label: lbl } of assigns) {
        x.current = it.value;
        x.currentPath = null;
        x.currentDepth = it.depth;
        const nv = value(x);
        if (nv === MISSING || nv === UNKNOWN) throw new RqlError('coercion-failed', `value for '${lbl}' on ${this.refOf(it.value)} is missing`);
        if (nv instanceof ErrorValue) {
          throw new RqlError(nv.code === 'stale-ref' ? 'stale-ref' : 'coercion-failed', `value for '${lbl}' on ${this.refOf(it.value)} failed: ${nv.detail ?? nv.code}`);
        }
        const item = it.value as object;
        const { slot, container } = this.resolve(item, target.segments);
        if (!slot.writable(x)) throw new RqlError(slot.denyCode, `'${lbl}' on ${this.refOf(item)} is not writable${slot.denyReason}`);
        const co = coerce(x.types, nv, slot.type);
        if (!co.ok) throw new RqlError('coercion-failed', `cannot assign ${describeValue(nv)} to '${lbl}' (${friendlyName(slot.type, x.types)}): ${co.why}`);
        writes.push({ item, segments: target.segments, label: lbl, old: slot.get(container), new: co.value });
      }
    }

    if (!x.options.dryRun) {
      let applied = 0;
      try {
        for (; applied < writes.length; applied++) this.assign(writes[applied]!.item, writes[applied]!.segments, writes[applied]!.new);
      } catch (e) {
        for (let k = applied - 1; k >= 0; k--) {
          try {
            this.assign(writes[k]!.item, writes[k]!.segments, writes[k]!.old);
          } catch {
            // best effort
          }
        }
        throw new RqlError('coercion-failed', `write '${writes[applied]!.label}' failed (${errorMessage(e)}); ${applied} earlier write(s) were rolled back`);
      }
    }

    const rows = writes.map((w) => [x.engine.refOf(w.item), w.label, w.old, w.new]);
    const r = this.enc.rows(['$ref', 'member', 'old', 'new'], rows);
    if (x.options.dryRun) r.dryRun = true;
    return r;
  }

  private slotOf(container: object, declared: TypeRef, seg: Segment, lbl: string): Slot {
    const x = this.x;
    const info = x.types.infoOf(container);
    const args = typeArgs(declared);
    if (info.isMap) {
      const key = seg.kind === 'member' ? seg.name : seg.kind === 'key' ? seg.key : seg.kind === 'index' ? seg.index : undefined;
      const k = key === undefined ? undefined : coerceKey(container as MapLike, key, args?.key ?? 'any');
      if (k === undefined) throw new RqlError('coercion-failed', `invalid map key in '${lbl}'`);
      return new MapSlot(k, args?.of ?? 'any');
    }
    if (info.isList) {
      if (seg.kind !== 'index') throw new RqlError('invalid-plan', `'${lbl}': lists can only be indexed ([i]) in set targets`);
      if (!Array.isArray(container) || Object.isFrozen(container)) throw new RqlError('readonly-member', `'${lbl}': collection is not writable by index`);
      const i = seg.index < 0 ? container.length + seg.index : seg.index;
      if (i < 0 || i >= container.length) throw new RqlError('invalid-plan', `'${lbl}': index ${seg.index} out of range (count ${container.length})`);
      return new ListSlot(seg.index, args?.of ?? 'any');
    }
    const name = seg.kind === 'member' ? seg.name : seg.kind === 'key' && typeof seg.key === 'string' ? seg.key : undefined;
    if (name === undefined) throw new RqlError('invalid-plan', `'${lbl}': invalid segment ${segmentToString(seg)} on ${info.shortName}`);
    const m = info.find(name, container);
    if (m === undefined) {
      const names = info.membersOf(container).map((mm) => mm.name);
      const close = closest(name, names, 3);
      throw new RqlError('unknown-member', `no member '${name}' on ${info.shortName}`, undefined, close.length > 0 ? `did you mean ${close.join(' / ')}?` : 'members: ' + names.join(' '));
    }
    return new MemberSlot(info, m);
  }

  private resolve(root: object, segs: readonly Segment[]): { slot: Slot; container: object } {
    const lbl = segs.map(segmentToString).join('');
    let cur: object = root;
    let declared: TypeRef = 'any';
    for (let i = 0; i < segs.length; i++) {
      const slot = this.slotOf(cur, declared, segs[i]!, lbl);
      if (i === segs.length - 1) return { slot, container: cur };
      const next = slot.get(cur);
      if (next === null || next === MISSING || typeof next !== 'object') {
        throw new RqlError('coercion-failed', `cannot set '${lbl}': '${segmentToString(segs[i]!)}' is null on ${this.refOf(root)}`);
      }
      cur = next as object;
      declared = slot.type;
    }
    throw new Error('empty set target');
  }

  /** Navigate fresh, write, then write back value-like (struct) containers up the chain. */
  private assign(root: object, segs: readonly Segment[], value: unknown): void {
    const lbl = segs.map(segmentToString).join('');
    const containers: object[] = [root];
    const slots: Slot[] = [];
    let cur: object = root;
    let declared: TypeRef = 'any';
    for (let i = 0; i < segs.length; i++) {
      const slot = this.slotOf(cur, declared, segs[i]!, lbl);
      slots.push(slot);
      if (i === segs.length - 1) break;
      cur = slot.get(cur) as object;
      declared = slot.type;
      containers.push(cur);
    }
    slots[slots.length - 1]!.set(containers[containers.length - 1]!, value);
    for (let j = containers.length - 1; j >= 1; j--) {
      if (this.x.types.infoOf(containers[j]).kind !== 'struct') break;
      // JS structs are references (already updated in place); write back only through writable members
      // so hosts whose getters return copies see the change.
      const parent = slots[j - 1]!;
      if (parent instanceof MemberSlot && !parent.m.canWrite) break;
      parent.set(containers[j - 1]!, containers[j]);
    }
  }

  // ---------------------------------------------------------------- call

  call(stream: Iterable<Item>, s: CallStage): QueryResult {
    const x = this.x;
    this.checkPolicy('call');
    const args: Eval[] = s.args.map((a) => this.c.compile(a));
    const targets = this.targets(stream, 'call');
    const planned: { item: object; method: MethodAccessor; args: unknown[] }[] = [];
    const wanted = s.method.toUpperCase();
    for (const it of targets) {
      const item = it.value as object;
      const info = x.types.infoOf(item);
      const candidates = info.callable.filter((m) => m.name.toUpperCase() === wanted);
      if (candidates.length === 0) {
        const all = info.callable.map((m) => m.signature(x.types));
        throw new RqlError(
          hasMethod(item, wanted) ? 'permission-denied' : 'unknown-function',
          `no callable method '${s.method}' on ${info.shortName}`,
          undefined,
          all.length > 0 ? 'callable: ' + all.join(', ') : `${info.shortName} has no callable methods`,
        );
      }
      x.current = item;
      x.currentDepth = it.depth;
      const vals = args.map((a) => a(x));
      let chosen: { method: MethodAccessor; args: unknown[] } | undefined;
      let why = '';
      for (const m of candidates) {
        const ps = m.params;
        if (ps.length < vals.length || ps.slice(vals.length).some((p) => !p.optional && !('default' in p))) {
          why = `expects ${ps.length} argument(s)`;
          continue;
        }
        const cv: unknown[] = [];
        let ok = true;
        for (let i = 0; i < ps.length; i++) {
          const p = ps[i]!;
          if (i >= vals.length) {
            cv.push(p.default);
            continue;
          }
          const co = coerce(x.types, vals[i], p.type ?? 'any');
          if (!co.ok) {
            ok = false;
            why = `argument '${p.name}': ${co.why}`;
            break;
          }
          cv.push(rawValue(co.value, p.type ?? 'any'));
        }
        if (ok) {
          chosen = { method: m, args: cv };
          break;
        }
      }
      if (chosen === undefined) {
        throw new RqlError('coercion-failed', `cannot call ${s.method} on ${x.engine.refOf(item)}: ${why}`, undefined, 'signature: ' + candidates.map((m) => m.signature(x.types)).join(', '));
      }
      planned.push({ item, ...chosen });
    }

    const rows: unknown[][] = [];
    let done = 0;
    for (const p of planned) {
      let result: unknown = '(dry-run)';
      if (!x.options.dryRun) {
        try {
          result = p.method.invoke(p.item, p.args);
          if (result === undefined) result = null;
        } catch (e) {
          const name = e instanceof Error ? e.name : typeof e;
          throw new RqlError('getter-threw', `${p.method.name} on ${x.engine.refOf(p.item)} threw ${name}: ${errorMessage(e)}; ${done} earlier call(s) completed and are NOT rolled back`);
        }
      }
      done++;
      rows.push([x.engine.refOf(p.item), p.method.signature(x.types), result]);
    }
    const r = this.enc.rows(['$ref', 'method', 'result'], rows);
    if (x.options.dryRun) r.dryRun = true;
    return r;
  }
}

function label(p: PathExpr): string {
  return p.segments.map((s, i) => (s.kind === 'member' ? (i === 0 ? '' : '.') + s.name : segmentToString(s))).join('');
}

function hasMethod(o: object, upperName: string): boolean {
  for (let p: object | null = o; p !== null && p !== Object.prototype; p = Object.getPrototypeOf(p) as object | null) {
    for (const k of Object.getOwnPropertyNames(p)) {
      if (k.toUpperCase() !== upperName || k === 'constructor') continue;
      const d = Object.getOwnPropertyDescriptor(p, k);
      if (d !== undefined && typeof d.value === 'function') return true;
    }
  }
  return false;
}

/** Map key for a set target, coerced to the declared key type; existing equivalent keys win. */
function coerceKey(m: MapLike, key: string | number, keyType: TypeRef): unknown {
  const existing = findMapKey(m, key);
  if (existing.found) return existing.key;
  const kt = unwrapNullable(keyType);
  if (m instanceof RecordMap || kt === 'string') return typeof key === 'number' ? String(key) : key;
  if (kt === 'int' || kt === 'number') {
    const n = typeof key === 'number' ? key : Number(key);
    if (Number.isNaN(n) || (typeof key === 'string' && key.trim() === '') || (kt === 'int' && !Number.isInteger(n))) return undefined;
    return n;
  }
  if (kt instanceof EnumType) {
    const ev = typeof key === 'number' ? kt.wrap(key) : kt.parse(key);
    return ev?.value;
  }
  return key;
}

/** Host-facing raw form of a coerced value (enum values unwrapped for enum-typed parameters). */
function rawValue(v: unknown, type: TypeRef): unknown {
  return v instanceof EnumValue && unwrapNullable(type) instanceof EnumType ? v.value : v;
}

type Coerced = { ok: true; value: unknown } | { ok: false; why: string };

/**
 * Coerces a value to a declared type (spec §8): number → int only if integral and in range,
 * string → enum by name, null only for nullable/reference types, list literal → list type.
 */
export function coerce(model: TypeModel, v: unknown, target: TypeRef): Coerced {
  if (isAbsent(v)) return { ok: false, why: 'value is ' + describeValue(v) };
  const t = unwrapNullable(target);
  if (v === null || v === undefined) {
    const reference = typeof t === 'object' && !(t instanceof EnumType) && (t.kind === 'list' || t.kind === 'map' || (t.kind === 'class' && model.infoForCtor(t.ctor)?.kind !== 'struct'));
    if (isNullable(target) || reference) return { ok: true, value: null };
    return { ok: false, why: 'null is not allowed for ' + friendlyName(target, model) };
  }
  if (t === 'any') return { ok: true, value: v };
  if (t instanceof EnumType) {
    const fail: Coerced = { ok: false, why: 'expected one of ' + t.values.map((e) => e.name).join(', ') };
    if (v instanceof EnumValue) return v.type === t ? { ok: true, value: v } : fail;
    if (typeof v === 'string') {
      const ev = t.parse(v);
      return ev !== undefined && (t.isDefined(ev) || t.flags) ? { ok: true, value: ev } : fail;
    }
    const n = toNumber(v);
    if (n !== undefined && Math.floor(n) === n) {
      const ev = t.wrap(n);
      if (t.isDefined(ev)) return { ok: true, value: ev };
    }
    return fail;
  }
  switch (t) {
    case 'int':
    case 'number': {
      const n = toNumber(v);
      if (n === undefined) return { ok: false, why: 'expected a number' };
      if (t === 'int') {
        if (Math.floor(n) !== n) return { ok: false, why: 'expected an integer' };
        if (n < -2147483648 || n > 2147483647) return { ok: false, why: 'out of range for int' };
      }
      return { ok: true, value: n };
    }
    case 'string':
      return typeof v === 'string' ? { ok: true, value: v } : { ok: false, why: 'expected a string' };
    case 'bool':
      return typeof v === 'boolean' ? { ok: true, value: v } : { ok: false, why: 'expected true or false' };
    default:
      break;
  }
  if (typeof t !== 'object' || t instanceof EnumType) return { ok: false, why: `expected ${friendlyName(target, model)}` };
  switch (t.kind) {
    case 'list': {
      if (typeof v !== 'object' || !model.infoOf(v).isList) return { ok: false, why: `expected ${friendlyName(target, model)}` };
      const items: unknown[] = [];
      for (const el of v as Iterable<unknown>) {
        const co = coerce(model, el === undefined ? null : el, t.of);
        if (!co.ok) return { ok: false, why: 'element: ' + co.why };
        items.push(rawValue(co.value, t.of));
      }
      return { ok: true, value: items };
    }
    case 'map': {
      if (!isMapLike(v)) return { ok: false, why: `expected ${friendlyName(target, model)}` };
      const out = new Map<unknown, unknown>();
      for (const [k, el] of v.entries()) {
        const co = coerce(model, el === undefined ? null : el, t.of);
        if (!co.ok) return { ok: false, why: 'value: ' + co.why };
        out.set(k, rawValue(co.value, t.of));
      }
      return { ok: true, value: out };
    }
    case 'class': {
      const ti = model.infoForCtor(t.ctor);
      const ok = typeof v === 'object' && (v instanceof t.ctor || (ti !== undefined && model.infoOf(v).isA(ti.shortName)));
      if (!ok) return { ok: false, why: `expected ${friendlyName(target, model)}` };
      // Structs have value semantics: assign a copy.
      if (ti?.kind === 'struct') return { ok: true, value: Object.assign(Object.create(Object.getPrototypeOf(v) as object | null) as object, v) };
      return { ok: true, value: v };
    }
    default:
      return { ok: false, why: `expected ${friendlyName(target, model)}` };
  }
}
