/** Permissioned, all-or-nothing `set` / `call` (spec §8). Port of dotnet/Rql.Execution/Mutator.cs. */
import { RqlError, formatNumber, segmentToString, type CallStage, type PathExpr, type QueryResult, type Segment, type SetStage } from '@rql/contracts';
import { findMapKey, isMapLike, mapGet, type MapLike } from './collections.js';
import { describeValue, type Compiler, type Eval } from './compiler.js';
import type { Encoder } from './encoder.js';
import { errorMessage, type Exec } from './exec.js';
import type { MutationRecord } from './engine.js';
import type { Item } from './pipeline.js';
import { adapterName } from './adapters.js';
import { closest, memberList } from './suggest.js';
import {
  EnumType,
  EnumValue,
  StringMap,
  VirtualMap,
  friendlyName,
  isNullable,
  unwrapNullable,
  type Ctor,
  type MemberAccessor,
  type MethodAccessor,
  type TypeInfo,
  type TypeModel,
  type TypeRef,
} from './types.js';
import { ErrorValue, MISSING, MapEntry, ObjectValue, RowValue, UNKNOWN, isAbsent, toNumber } from './values.js';

interface PlannedWrite {
  item: object;
  /** The last reference object on the written path (host hooks mark it dirty). */
  owner: object;
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
  /** Adapter validation of a coerced value before any write: rejection reason or undefined. */
  validate(_container: object, _value: unknown): string | undefined {
    return undefined;
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
  override validate(container: object, value: unknown): string | undefined {
    return this.m.validate?.(container, value);
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
    if (m instanceof StringMap) {
      const k = String(this.key);
      if (value === MISSING) m.delete(k);
      else m.set(k, raw);
    } else if (value === MISSING) m.delete(this.key);
    else m.set(this.key, raw);
  }
  override validate(container: object, value: unknown): string | undefined {
    if (!(container instanceof StringMap)) return undefined;
    const k = String(this.key);
    // Virtual maps only take writes to existing entries: a new key could not be removed again on rollback.
    if (container instanceof VirtualMap && !container.has(k)) return `no entry '${k}'`;
    return container.validate(k, value);
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
      // Null and destroyed items are skipped (a destroyed object reads as null, spec §13).
      if (it.value === null || it.value === undefined || this.x.dead(it.value)) continue;
      if (it.value instanceof RowValue) throw new RqlError('invalid-plan', `'${op}' needs objects, not projected rows; remove 'select' before '${op}'`);
      if (it.value instanceof MapEntry) {
        throw new RqlError('invalid-plan', `'${op}' cannot target map entries; set the map member with a key path instead (Stats["key"] = ...)`);
      }
      const v = it.value;
      const vi = typeof v === 'object' ? this.x.types.infoOf(v) : undefined;
      if (vi === undefined || v instanceof EnumValue || v instanceof Date || (vi.kind === 'struct' && !vi.writesThrough)) {
        throw new RqlError(
          'invalid-plan',
          `'${op}' needs objects with identity; '${this.x.types.infoOf(v).shortName}' items are value copies, so the change would be lost`,
          undefined,
          'set the member from its owner instead, e.g. objects | where … | set Transform.Position.x = 0',
        );
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

  /**
   * Runs a host hook (not on dry-run). `records` is built once per set/call and shared by both hooks.
   * beforeWrite: a throw vetoes the mutation (permission-denied, nothing written). committed: a throw is a
   * hook-threw warning, the writes stay.
   */
  private runHook(kind: 'beforeWrite' | 'committed', records: () => readonly MutationRecord[]): void {
    const hook = this.x.engine.mutations[kind];
    if (hook === undefined || this.x.options.dryRun) return;
    const recs = records();
    if (recs.length === 0) return;
    try {
      hook(recs);
    } catch (e) {
      if (kind === 'beforeWrite') throw new RqlError('permission-denied', `the host rejected the change before anything was written: ${errorMessage(e)}`);
      const name = e instanceof Error ? e.name : typeof e;
      this.x.warn('hook-threw', `host commit hook threw ${name}: ${errorMessage(e)}; the writes were applied`);
    }
  }

  /**
   * Whether a container read through `slot` is a copy that must be assigned back after writing into it:
   * structs (unless handles that write through) and copy-on-read members (spec §13).
   */
  private needsWriteBack(slot: Slot, container: object): boolean {
    if (slot instanceof MemberSlot && slot.m.copyOnRead) return true;
    const info = this.x.types.infoOf(container);
    return info.kind === 'struct' && !info.writesThrough;
  }

  /**
   * The object whose state changes, for host hooks: the nearest enclosing class instance on the written
   * path (never a list, map / virtual-map view or struct), searching up from the final container; else the item.
   */
  private ownerOf(item: object, chain: readonly { value: object }[]): object {
    for (let j = chain.length - 1; j >= 0; j--) {
      const c = chain[j]!.value;
      if (this.x.types.infoOf(c).kind === 'object') return c;
    }
    return item;
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
        const { slot, container, owner } = this.resolve(item, target.segments);
        if (!slot.writable(x)) throw new RqlError(slot.denyCode, `'${lbl}' on ${this.refOf(item)} is not writable${slot.denyReason}`);
        const co = coerce(x.types, nv, slot.type, x.engine.mutations.canWrite);
        if (!co.ok) throw new RqlError(co.code ?? 'coercion-failed', `cannot assign ${describeValue(nv)} to '${lbl}' (${friendlyName(slot.type, x.types)}): ${co.why}`);
        // Validation runs while the mutation is planned, before any write (spec §13).
        const rejected = slot.validate(container, co.value);
        if (rejected !== undefined) throw new RqlError('invalid-value', `invalid value ${valueText(nv)} for '${lbl}' on ${this.refOf(item)}: ${rejected}`);
        writes.push({ item, owner, segments: target.segments, label: lbl, old: safeGet(slot, container, lbl), new: co.value });
      }
    }

    const toRecords = (): MutationRecord[] => writes.map((w): MutationRecord => ({ item: w.item, owner: w.owner, member: w.label, old: w.old, new: w.new }));
    let records = once(toRecords);
    this.runHook('beforeWrite', records);
    if (!x.options.dryRun) {
      let applied = 0;
      try {
        for (; applied < writes.length; applied++) this.assign(writes[applied]!.item, writes[applied]!.segments, writes[applied]!.new);
      } catch (e) {
        const failed = writes[applied]!;
        // Restore the failing write too: an adapter setter may change state before it throws.
        let selfRestored = true;
        try {
          this.assign(failed.item, failed.segments, failed.old);
        } catch {
          selfRestored = false;
        }
        let rollbackFailures = 0;
        for (let k = applied - 1; k >= 0; k--) {
          try {
            this.assign(writes[k]!.item, writes[k]!.segments, writes[k]!.old);
          } catch {
            rollbackFailures++;
          }
        }
        let rolled =
          rollbackFailures === 0
            ? `${applied} earlier write(s) were rolled back`
            : `rolling back ${applied} earlier write(s) FAILED for ${rollbackFailures} of them — state may be partially modified`;
        if (!selfRestored) rolled += `; restoring '${failed.label}' itself also failed, so it may be partially modified`;
        throw new RqlError('coercion-failed', `write '${writes[applied]!.label}' failed (${errorMessage(e)}); ${rolled}`);
      }
      // The receipt (and the committed hook) report what the host stored, which a setter may have clamped (spec §8).
      for (const w of writes) w.new = this.readBack(w);
      records = once(toRecords);
    }

    this.runHook('committed', records);
    const rows = writes.map((w) => [x.engine.refOf(w.item), w.label, w.old, w.new]);
    const r = this.enc.rows(['$ref', 'member', 'old', 'new'], rows);
    if (x.options.dryRun) r.dryRun = true;
    return r;
  }

  /** The written member's value after the writes; the planned value (plus a warning) when it cannot be read. */
  private readBack(w: PlannedWrite): unknown {
    try {
      let cur: object = w.item;
      let declared: TypeRef = 'any';
      for (let i = 0; ; i++) {
        const slot = this.slotOf(cur, declared, w.segments[i]!, w.label);
        const v = slot.get(cur);
        if (i === w.segments.length - 1) {
          // List / map slots hold raw enum values; the receipt shows the enum like the planned value.
          const et = unwrapNullable(slot.type);
          return et instanceof EnumType && (typeof v === 'number' || typeof v === 'string') ? et.wrap(v) : v;
        }
        if (v === null || v === undefined || v === MISSING || typeof v !== 'object') return w.new;
        cur = v;
        declared = slot.type;
      }
    } catch (e) {
      const name = e instanceof Error ? e.name : typeof e;
      this.x.warnOnce(
        'getter-threw',
        `reading back '${w.label}' after the write failed (${name}: ${errorMessage(e)}); the receipt shows the planned value`,
        'read-back',
      );
      return w.new;
    }
  }

  private slotOf(container: object, declared: TypeRef, seg: Segment, lbl: string): Slot {
    const x = this.x;
    const info = x.types.infoOf(container);
    const args = typeArgs(declared);
    if (info.isMap) {
      if (container instanceof StringMap && container.readOnly) throw new RqlError('readonly-member', `'${lbl}': map is read-only`);
      if (seg.kind === 'member' && seg.name.toUpperCase() === 'COUNT') {
        throw new RqlError('readonly-member', `'${lbl}': Count of a map is read-only (use ["Count"] to address a key literally named Count)`);
      }
      const key = seg.kind === 'member' ? seg.name : seg.kind === 'key' ? seg.key : seg.kind === 'index' ? seg.index : undefined;
      // Member syntax addresses an existing key case-insensitively, like reads (spec §3); new keys keep the given spelling.
      const k = key === undefined ? undefined : coerceKey(container as MapLike, key, args?.key ?? 'any', seg.kind === 'member');
      if (k === undefined) throw new RqlError('coercion-failed', `invalid map key in '${lbl}'`);
      const entryType = container instanceof VirtualMap ? container.entryType(String(k)) : undefined;
      return new MapSlot(k, entryType ?? args?.of ?? 'any');
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
      throw new RqlError('unknown-member', `no member '${name}' on ${info.shortName}`, undefined, close.length > 0 ? `did you mean ${close.join(' / ')}?` : memberList(names));
    }
    return new MemberSlot(info, m);
  }

  private resolve(root: object, segs: readonly Segment[]): { slot: Slot; container: object; owner: object } {
    const lbl = segs.map(segmentToString).join('');
    let cur: object = root;
    let declared: TypeRef = 'any';
    const chain: { slot: Slot; value: object }[] = [];
    for (let i = 0; i < segs.length; i++) {
      const slot = this.slotOf(cur, declared, segs[i]!, lbl);
      if (i === segs.length - 1) {
        // Every copy on the way (struct or copy-on-read member) is written back into its parent: those
        // parent slots must be writable and allowed too.
        for (let j = chain.length - 1; j >= 0 && this.needsWriteBack(chain[j]!.slot, chain[j]!.value); j--) {
          const c = chain[j]!;
          if (!c.slot.writable(this.x)) {
            throw new RqlError(
              c.slot.denyCode,
              `'${lbl}' on ${this.refOf(root)} is not writable: '${segmentToString(segs[j]!)}' is a copy (value type or copy-on-read member) that must be written back through a non-writable member${c.slot.denyReason}`,
            );
          }
        }
        return { slot, container: cur, owner: this.ownerOf(root, chain) };
      }
      const next = this.x.live(safeGet(slot, cur, lbl)); // a destroyed container reads as null
      if (next === null || next === MISSING || typeof next !== 'object') {
        throw new RqlError('coercion-failed', `cannot set '${lbl}': '${segmentToString(segs[i]!)}' is null on ${this.refOf(root)}`);
      }
      chain.push({ slot, value: next as object });
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
      if (!this.needsWriteBack(slots[j - 1]!, containers[j]!)) break;
      // Writability was checked in resolve().
      slots[j - 1]!.set(containers[j - 1]!, containers[j]);
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
          const co = coerce(x.types, vals[i], p.type ?? 'any', x.engine.mutations.canWrite);
          if (!co.ok) {
            if (co.code !== undefined) throw new RqlError(co.code, `cannot call ${s.method} on ${x.engine.refOf(item)}: argument '${p.name}': ${co.why}`);
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

    const callRecords = once(() =>
      planned.map((p): MutationRecord => ({ item: p.item, owner: p.item, member: p.method.signature(x.types), old: undefined, new: undefined, method: p.method })),
    );
    this.runHook('beforeWrite', callRecords);
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
          // Completed calls are not rolled back, so the host still learns about them.
          const completed = done;
          this.runHook('committed', () => callRecords().slice(0, completed));
          throw new RqlError('call-threw', `${p.method.name} on ${x.engine.refOf(p.item)} threw ${name}: ${errorMessage(e)}; ${done} earlier call(s) completed and are NOT rolled back`);
        }
      }
      done++;
      rows.push([x.engine.refOf(p.item), p.method.signature(x.types), result]);
    }
    this.runHook('committed', callRecords);
    const r = this.enc.rows(['$ref', 'method', 'result'], rows);
    if (x.options.dryRun) r.dryRun = true;
    return r;
  }
}

/** A lazily computed value, built at most once. */
function once<T>(build: () => T): () => T {
  let value: { v: T } | undefined;
  return () => (value ??= { v: build() }).v;
}

function safeGet(slot: Slot, container: object, lbl: string): unknown {
  try {
    return slot.get(container);
  } catch (e) {
    const name = e instanceof Error ? e.name : typeof e;
    throw new RqlError('getter-threw', `reading '${lbl}' failed: ${name}: ${errorMessage(e)}`);
  }
}

/** A value as the query wrote it (99, "Tag", true), for invalid-value messages; other kinds by description. */
function valueText(v: unknown): string {
  if (v === null || v === undefined) return 'null';
  if (typeof v === 'string') return '"' + v + '"';
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  const n = toNumber(v);
  return n !== undefined ? formatNumber(n) : describeValue(v);
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
function coerceKey(m: MapLike, key: string | number, keyType: TypeRef, caseInsensitive: boolean): unknown {
  const existing = findMapKey(m, key);
  if (existing.found && (caseInsensitive || typeof existing.key !== 'string' || typeof key !== 'string' || existing.key === key)) return existing.key;
  return parseKey(key, m instanceof StringMap ? 'string' : keyType);
}

/** A map key converted to the declared key type (raw host form), or undefined when it does not fit. */
function parseKey(key: unknown, keyType: TypeRef): unknown {
  const kt = unwrapNullable(keyType);
  if (kt === 'string') return typeof key === 'number' ? formatNumber(key) : key instanceof EnumValue ? key.name : key;
  if (kt === 'int' || kt === 'number') {
    const n = typeof key === 'number' ? key : typeof key === 'string' && key.trim() !== '' ? Number(key) : NaN;
    if (Number.isNaN(n) || (kt === 'int' && !Number.isInteger(n))) return undefined;
    return n;
  }
  if (kt instanceof EnumType) {
    const ev = key instanceof EnumValue ? key : typeof key === 'number' ? kt.wrap(key) : typeof key === 'string' ? kt.parse(key) : undefined;
    return ev !== undefined && ev.type === kt ? ev.value : undefined;
  }
  return key;
}

/** Host-facing raw form of a coerced value (enum values unwrapped for enum-typed parameters). */
function rawValue(v: unknown, type: TypeRef): unknown {
  return v instanceof EnumValue && unwrapNullable(type) instanceof EnumType ? v.value : v;
}

/** Coercion outcome; a failure may carry `permission-denied` (host policy) instead of coercion-failed. */
type Coerced = { ok: true; value: unknown } | { ok: false; why: string; code?: 'permission-denied' };

/** Host allow-check for members assigned while constructing from object literals (`mutations.canWrite`). */
type WritePolicy = ((type: TypeInfo, member: MemberAccessor) => boolean) | undefined;

/** A nested failure with more context; keeps a policy denial's code. */
const nested = (co: { why: string; code?: 'permission-denied' }, prefix: string): Coerced =>
  co.code !== undefined ? { ok: false, why: prefix + co.why, code: co.code } : { ok: false, why: prefix + co.why };

/** Whether `v` already is a `ctor` value: an instance, or tagged/declared as that type or a subtype. */
function isInstance(model: TypeModel, v: unknown, ctor: Ctor, ti: TypeInfo | undefined): boolean {
  return typeof v === 'object' && v !== null && (v instanceof ctor || (ti !== undefined && model.infoOf(v).isA(ti.shortName)));
}

/**
 * Coerces a value to a declared type (spec §8): number → int only if integral and in range,
 * string → enum by name, null only for nullable/reference types, list literal → list type.
 */
export function coerce(model: TypeModel, v: unknown, target: TypeRef, policy?: WritePolicy): Coerced {
  if (isAbsent(v)) return { ok: false, why: 'value is ' + describeValue(v) };
  const t = unwrapNullable(target);
  if (v === null || v === undefined) {
    const reference = typeof t === 'object' && !(t instanceof EnumType) && (t.kind === 'list' || t.kind === 'map' || (t.kind === 'class' && model.infoForCtor(t.ctor)?.kind !== 'struct'));
    if (isNullable(target) || reference) return { ok: true, value: null };
    return { ok: false, why: 'null is not allowed for ' + friendlyName(target, model) };
  }
  if (t === 'any') return { ok: true, value: v };
  if (typeof t === 'object' && !(t instanceof EnumType) && t.kind === 'class') {
    const ti = model.infoForCtor(t.ctor);
    if (isInstance(model, v, t.ctor, ti)) {
      // Structs have value semantics: assign a copy.
      if (ti?.kind === 'struct') return { ok: true, value: Object.assign(Object.create(Object.getPrototypeOf(v) as object | null) as object, v) };
      return { ok: true, value: v };
    }
    // Values already of the target type bypass adapters; others may be converted (spec §13).
    for (const ad of model.hasCapability('coerce') ? model.adapterSetFor(t.ctor).coerce : []) {
      const r = ad.coerce!(v, t.ctor);
      if (r === undefined) continue;
      if (!r.ok) return { ok: false, why: r.reason };
      if (r.value !== null && !isInstance(model, r.value, t.ctor, ti)) {
        throw new Error(`adapter ${adapterName(ad)} coerced to ${describeValue(r.value)}, not ${t.ctor.name}`);
      }
      return { ok: true, value: r.value };
    }
  }
  if (v instanceof ObjectValue) return fromLiteral(model, v, t, target, policy);
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
      let same = Array.isArray(v);
      for (const el of v as Iterable<unknown>) {
        const co = coerce(model, el === undefined ? null : el, t.of, policy);
        if (!co.ok) return nested(co, 'element: ');
        const raw = rawValue(co.value, t.of);
        if (raw !== el) same = false;
        items.push(raw);
      }
      // Natural JS semantics (spec §12): an array that needs no coercion is assigned as is (alias).
      return { ok: true, value: same ? v : items };
    }
    case 'map':
      return isMapLike(v) ? coerceMap(model, v, t.key, t.of, target, policy) : { ok: false, why: `expected ${friendlyName(target, model)}` };
    default:
      return { ok: false, why: `expected ${friendlyName(target, model)}` };
  }
}

/** A map value (or object literal) built entry by entry: keys parsed to the key type, values coerced. */
function coerceMap(model: TypeModel, v: MapLike, keyType: TypeRef, valueType: TypeRef, target: TypeRef, policy: WritePolicy): Coerced {
  const out = new Map<unknown, unknown>();
  for (const [k, el] of v.entries()) {
    const key = parseKey(k, keyType);
    if (key === undefined) return { ok: false, why: `invalid key '${String(k)}' for ${friendlyName(target, model)}` };
    const co = coerce(model, el === undefined ? null : el, valueType, policy);
    if (!co.ok) return nested(co, `'${String(k)}': `);
    out.set(key, rawValue(co.value, valueType));
  }
  return { ok: true, value: out };
}

/** Object literal → map or constructed object (spec §4.5); anything else cannot be built from one. */
function fromLiteral(model: TypeModel, literal: ObjectValue, t: TypeRef, target: TypeRef, policy: WritePolicy): Coerced {
  if (typeof t === 'object' && !(t instanceof EnumType)) {
    if (t.kind === 'map') return coerceMap(model, literal, t.key, t.of, target, policy);
    if (t.kind === 'class') return construct(model, literal, t.ctor, model.infoForCtor(t.ctor), policy);
  }
  return { ok: false, why: `an object literal cannot become ${friendlyName(target, model)}` };
}

/**
 * Constructs the target class from an object literal (spec §4.5): the concrete type is the target or the
 * registered subclass named by "$type"; every other key must name a writable member, whose value is coerced
 * recursively and validated. Members not named keep the constructor's defaults.
 */
function construct(model: TypeModel, literal: ObjectValue, ctor: Ctor, ti: TypeInfo | undefined, policy: WritePolicy): Coerced {
  let concrete = ctor;
  if (literal.has('$type')) {
    const tn = literal.get('$type');
    if (typeof tn !== 'string') return { ok: false, why: '"$type" must be a type name string' };
    const hits = model.subtypesNamed(ctor, tn);
    if (hits.length === 0) return { ok: false, why: `no registered type named '${tn}' derives from ${ti?.shortName ?? ctor.name}` };
    if (hits.length > 1) return { ok: false, why: `'${tn}' is ambiguous: ${hits.map((c) => c.name).join(', ')}` };
    concrete = hits[0]!;
  }
  let instance: object;
  try {
    instance = new (concrete as unknown as new () => object)();
  } catch (e) {
    return { ok: false, why: `cannot construct ${concrete.name} (needs a constructor without required arguments, or an adapter that coerces object literals): ${errorMessage(e)}` };
  }
  const ci = model.infoOf(instance);
  for (const [key, value] of literal) {
    if (key === '$type') continue;
    const m = ci.find(key, instance);
    if (m === undefined) return { ok: false, why: `no member '${key}' on ${ci.shortName}` };
    if (!m.canWrite || m.set === undefined) return { ok: false, why: `member '${m.name}' of ${ci.shortName} is read-only` };
    // Construction is a write: the host policy applies to every member assigned (spec §13).
    if (policy !== undefined && !policy(ci, m)) return { ok: false, why: `member '${m.name}' of ${ci.shortName} is denied by host policy`, code: 'permission-denied' };
    const co = coerce(model, value === undefined ? null : value, m.type, policy);
    if (!co.ok) return nested(co, `'${m.name}': `);
    const rejected = m.validate?.(instance, co.value);
    if (rejected !== undefined) return { ok: false, why: `'${m.name}': ${rejected}` };
    try {
      m.set(instance, co.value);
    } catch (e) {
      return { ok: false, why: `'${m.name}': ${errorMessage(e)}` };
    }
  }
  return { ok: true, value: instance };
}
