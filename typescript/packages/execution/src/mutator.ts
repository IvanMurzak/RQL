/** Permissioned, all-or-nothing `set` / `call` (spec §8). Port of dotnet/Rql.Execution/Mutator.cs. */
import { RqlError, formatNumber, segmentToString, type CallStage, type PathExpr, type QueryResult, type Segment, type SetStage } from '@rql/contracts';
import { findMapKey, isMapLike, mapGet, type MapLike } from './collections.js';
import { describeValue, equal, type Compiler, type Eval } from './compiler.js';
import type { Encoder } from './encoder.js';
import { errorMessage, type Exec } from './exec.js';
import type { MutationRecord, TextCoercion } from './engine.js';
import type { Item } from './pipeline.js';
import { adapterName, firstAnswer, type LiteralKind } from './adapters.js';
import { closestParameter, memberHint, nestedMembers, typeNameHint } from './suggest.js';

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
  type ParamSpec,
  type TypeInfo,
  type TypeModel,
  type TypeRef,
} from './types.js';
import { ErrorValue, MISSING, MapEntry, ObjectValue, RowValue, UNKNOWN, entryOrigin, isAbsent, isMember, toNumber, type EntryOrigin } from './values.js';

interface PlannedWrite {
  /** Where the write navigates from: the target, or the map of a streamed entry (`writeRoot`). */
  item: object;
  /** The stream item (host records). */
  target: object;
  /** The receipt's $ref: the target's, or for an entry the owner of the change (spec §8). */
  ref: string;
  /** The last reference object on the written path (host hooks mark it dirty). */
  owner: object;
  segments: readonly Segment[];
  label: string;
  old: unknown;
  new: unknown;
  /** The coerced value the query asked for (`new` becomes the read-back after the write). */
  planned: unknown;
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
    return v === undefined ? null : wrapEnum(v, this.type);
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
    return v === undefined ? null : wrapEnum(v, this.type);
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

/**
 * List / map elements hold raw enum values; slots read them as enum values, so receipts show `old` and the read-back
 * `new` the same way (by name) as the planned value.
 */
function wrapEnum(v: unknown, type: TypeRef): unknown {
  const et = unwrapNullable(type);
  return et instanceof EnumType && (typeof v === 'number' || typeof v === 'string') ? et.wrap(v) : v;
}

const typeArgs = (r: TypeRef): { key: TypeRef; of: TypeRef } | undefined => {
  const u = unwrapNullable(r);
  if (typeof u !== 'object' || u instanceof EnumType) return undefined;
  if (u.kind === 'list') return { key: 'int', of: u.of };
  if (u.kind === 'map') return { key: u.key, of: u.of };
  return undefined;
};

/** A target with the scope its expressions see: its path, depth and position in the stage's input (spec §8). */
interface Target {
  readonly item: Item;
  readonly index: number;
}

/**
 * The mutation-limit hint (spec §10). When callers can raise the limit (`argument` defined) and the ceiling is above
 * it: raise it to the `matched` count (one all-or-nothing call), or (more matched than the ceiling) to the ceiling in
 * batches of the ceiling. Otherwise: batches of `limit`.
 */
export function mutationLimitHint(op: string, limit: number, matched: number | undefined, argument: string | undefined, ceiling: number): string {
  const narrow = 'narrow the query (where / ref(...))';
  const batches = (n: number): string =>
    `split the change into batches of ${n}: … | first ${n} | ${op} …, then … | skip ${n} | first ${n} | ${op} … ` +
    `(repeat plain first ${n} instead when the change removes the items from your where)`;
  if (argument === undefined || ceiling <= limit) return `${narrow} or ${batches(limit)}`;
  return matched !== undefined
    ? `pass ${argument} = ${matched} (host ceiling ${ceiling}) to change all ${matched} in one all-or-nothing call (a dry run previews it); or ${narrow}`
    : `pass ${argument} = ${ceiling} (the host ceiling) and ${batches(ceiling)}; or ${narrow}`;
}

export class Mutator {
  constructor(
    private readonly x: Exec,
    private readonly c: Compiler,
    private readonly enc: Encoder,
  ) {}

  private checkPolicy(op: string): void {
    if (!this.x.engine.mutations.enabled) throw new RqlError('permission-denied', `'${op}' is not allowed: this host does not permit mutations`);
    if (!this.x.options.allowMutation) throw new RqlError('permission-denied', `'${op}' requires a mutation request: run it with ${this.x.engine.mutateTool}`);
  }

  /** `$`, `$index`, `$path` and `$depth` in a set value / call argument are the target's (spec §8). */
  private enterScope(t: Target): void {
    const x = this.x;
    x.current = t.item.value;
    x.currentPath = t.item.path;
    x.currentDepth = t.item.depth;
    x.index = t.index;
  }

  /** The host's batch validation (spec §8): every planned change at once, before any write or call, dry runs too. */
  private validateBatch(op: string, records: () => readonly MutationRecord[]): void {
    const validate = this.x.engine.mutations.validateBatch;
    if (validate === undefined) return;
    const batch = records();
    if (batch.length === 0) return;
    let rejected: string | null | undefined;
    try {
      rejected = validate(batch);
    } catch (e) {
      rejected = `the batch check threw ${e instanceof Error ? e.name : typeof e}: ${errorMessage(e)}`;
    }
    if (rejected !== undefined && rejected !== null) {
      throw new RqlError('invalid-value', `the host refused this '${op}' of ${batch.length} change(s): ${rejected}; nothing was changed`);
    }
  }

  /** Compiles set values / call arguments, where `$count` is defined (spec §3, §8). */
  private mutationValues<T>(compile: () => T): T {
    this.c.mutationValues = true;
    try {
      return compile();
    } finally {
      this.c.mutationValues = false;
    }
  }

  /**
   * `method`: for `call`, the method (hints). `perItem`: a source-qualified call, whose items are only the scope of its
   * arguments, so any value may be one (spec §8).
   */
  private targets(stream: Iterable<Item>, op: string, method?: string, perItem = false): Target[] {
    const rest = stream[Symbol.iterator]();
    try {
      return this.collect(rest, op, method, perItem);
    } finally {
      rest.return?.(); // like for…of: release the source (its finally blocks) on a throw or an early stop
    }
  }

  private collect(rest: Iterator<Item>, op: string, method: string | undefined, perItem: boolean): Target[] {
    const list: Target[] = [];
    let index = -1;
    for (let step = rest.next(); !step.done; step = rest.next()) {
      const it = step.value;
      index++; // skipped null/destroyed items keep their positions: $index is the position in the stage's input
      // Null and destroyed items are skipped (a destroyed object reads as null, spec §13).
      if (it.value === null || it.value === undefined || this.x.dead(it.value)) continue;
      if (perItem) {
        list.push({ item: it, index });
        if (list.length > this.x.maxMutationTargets) throw this.limitExceeded(rest, list.length, op, 'items reached');
        continue;
      }
      if (it.value instanceof RowValue) throw new RqlError('invalid-plan', `'${op}' needs objects, not projected rows; remove 'select' before '${op}'`);
      if (it.value instanceof MapEntry) {
        if (op !== 'set' || entryOrigin(it.value) === undefined) {
          throw new RqlError(
            'invalid-plan',
            op === 'set'
              ? `'set' cannot target these map entries (they do not come from a map it can write to); set the map member with a key path instead (Stats["key"] = ...)`
              : `'${op}' cannot target map entries; call the method on the values instead: … | get Value | ${op} …`,
          );
        }
        list.push({ item: it, index }); // set Value… writes through the entry's map (spec §8)
        if (list.length > this.x.maxMutationTargets) throw this.limitExceeded(rest, list.length, op);
        continue;
      }
      const v = it.value;
      const vi = typeof v === 'object' ? this.x.types.infoOf(v) : undefined;
      if (vi === undefined || v instanceof EnumValue || v instanceof Date || (vi.kind === 'struct' && !vi.writesThrough)) {
        throw new RqlError(
          'invalid-plan',
          `'${op}' needs objects with identity; '${this.x.types.infoOf(v).shortName}' items are value copies, so the change would be lost`,
          undefined,
          method !== undefined ? this.x.engine.perItemCallHint(method) : 'set the member from its owner instead, e.g. objects | where … | set Transform.Position.x = 0',
        );
      }
      list.push({ item: it, index });
      if (list.length > this.x.maxMutationTargets) throw this.limitExceeded(rest, list.length, op);
    }
    this.x.count = index + 1; // $count: the stage's input, skipped items included, so $index runs 0 … $count - 1
    return list;
  }

  /**
   * mutation-limit (spec §8, §10): counts the rest of the stage's input up to the host ceiling (counting only) so the
   * message states how many items matched, and builds the hint for the host's limit argument.
   */
  private limitExceeded(rest: Iterator<Item>, matched: number, op: string, matchedText = 'objects matched'): RqlError {
    const m = this.x.engine.mutations;
    const limit = this.x.maxMutationTargets;
    const ceiling = Math.max(m.hardMaxTargets, limit);
    // A budget error while counting propagates: it, not the limit, is what the caller must fix first.
    while (matched <= ceiling) {
      const step = rest.next();
      if (step.done) break;
      const v = step.value.value;
      if (v !== null && v !== undefined && !this.x.dead(v)) matched++;
    }
    const exact = matched <= ceiling ? matched : undefined;
    const message =
      exact !== undefined
        ? `${matched} ${matchedText} '${op}', more than the limit of ${limit}; nothing was changed`
        : `more than ${ceiling} ${matchedText} '${op}' (the limit is ${limit}); nothing was changed`;
    const argument = this.x.limitArgument;
    return new RqlError('mutation-limit', message, undefined, m.limitHint?.(op, limit) || mutationLimitHint(op, limit, exact, argument, ceiling));
  }

  /**
   * Where a set target writes (spec §8): the item itself, or — for a map entry streamed from a map — the entry's origin,
   * with `Value.p` (or `p`, which reads through to the value like member access) rewritten to `prefix[key].p`: the
   * ordinary write path from the object the map was read from.
   */
  private writeRoot(item: object, origin: EntryOrigin | undefined, segments: readonly Segment[], lbl: string): { root: object; segments: readonly Segment[] } {
    if (origin === undefined) return { root: item, segments };
    if (isMember(segments[0]!, 'KEY')) {
      throw new RqlError('readonly-member', `'${lbl}': a map entry's key cannot be set`, undefined, 'keys are fixed: set the value (set Value… = …) or assign the whole map');
    }
    const keySeg = { kind: 'key', key: (item as MapEntry).Key } as Segment;
    return { root: origin.root, segments: [...origin.prefix, keySeg, ...(isMember(segments[0]!, 'VALUE') ? segments.slice(1) : segments)] };
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

  /**
   * A set target member that some targets lack while others have it (a mixed stream): unknown-member naming the types that
   * have it, so the agent filters to them (spec §10). All other cases are reported per target by slotOf.
   */
  private checkMixedTargets(targets: readonly Target[], paths: readonly PathExpr[]): void {
    const x = this.x;
    const objects = targets.map((t) => t.item.value as object).filter((v) => !(v instanceof MapEntry));
    const types = [...new Set(objects.map((v) => x.types.infoOf(v)))].filter((i) => (i.kind === 'object' || i.kind === 'struct') && i.dynamic === undefined);
    if (types.length < 2) return;
    for (const p of paths) {
      const seg = p.segments[0];
      if (seg?.kind !== 'member') continue;
      const name = seg.name;
      const lacking = types.filter((i) => i.find(name) === undefined);
      if (lacking.length === 0 || lacking.length === types.length) continue;
      const having = [...new Set(types.filter((i) => i.find(name) !== undefined).map((i) => i.shortName))].slice(0, 3);
      const n = objects.filter((v) => lacking.includes(x.types.infoOf(v))).length;
      throw new RqlError(
        'unknown-member',
        `no member '${name}' on ${lacking.map((i) => i.shortName).slice(0, 4).join(', ')} (${n} of ${targets.length} targets); nothing was changed`,
        undefined,
        filterHint(having, `'${name}'`, 'set …') + `, or … | where has(${name}) | set …`,
      );
    }
  }

  set(stream: Iterable<Item>, s: SetStage): QueryResult {
    const x = this.x;
    this.checkPolicy('set');
    const assigns = this.mutationValues(() => s.assign.map((a) => ({ target: a.target, value: this.c.compile(a.value), label: label(a.target) })));
    const targets = this.targets(stream, 'set');
    this.checkMixedTargets(targets, s.assign.map((a) => a.target));
    const writes: PlannedWrite[] = [];
    const rules = hostRules(x);
    for (const t of targets) {
      const it = t.item;
      const item = it.value as object;
      const origin = item instanceof MapEntry ? entryOrigin(item) : undefined;
      const itemRef = origin !== undefined ? this.refOf(origin.holder ?? origin.root) : this.refOf(item);
      // Without a path from the object the map was read from, the write starts at the map: that object (when known) still
      // owns the change unless an object inside the value does.
      const holder = origin !== undefined && origin.prefix.length === 0 ? origin.holder : undefined;
      for (const { target, value, label: assignLabel } of assigns) {
        const { root, segments } = this.writeRoot(item, origin, target.segments, assignLabel);
        const lbl = origin === undefined ? assignLabel : segmentsLabel(segments);
        this.enterScope(t);
        const nv = value(x);
        if (nv === MISSING || nv === UNKNOWN) throw new RqlError('coercion-failed', `value for '${lbl}' on ${itemRef} is missing`);
        if (nv instanceof ErrorValue) {
          throw new RqlError(nv.code === 'stale-ref' ? 'stale-ref' : 'coercion-failed', `value for '${lbl}' on ${itemRef} failed: ${nv.detail ?? nv.code}`);
        }
        const resolved = this.resolve(root, segments);
        const { slot, container } = resolved;
        const owner = holder !== undefined && resolved.owner === root ? holder : resolved.owner;
        if (!slot.writable(x)) throw new RqlError(slot.denyCode, `'${lbl}' on ${itemRef} is not writable${slot.denyReason}`);
        // The outer of an instanced literal is the owning object, never a map, list or struct (spec §4.5).
        const outer = typeof owner === 'object' && owner !== null && x.types.infoOf(owner).kind === 'object' ? owner : undefined;
        const co = coerce(x.types, nv, slot.type, { ...rules, outer });
        if (!co.ok) throw new RqlError(co.code ?? 'coercion-failed', `cannot assign ${describeValue(nv)} to '${lbl}' (${friendlyName(slot.type, x.types)}): ${co.why}`);
        // Validation runs while the mutation is planned, before any write (spec §13).
        const rejected = slot.validate(container, co.value);
        if (rejected !== undefined) throw new RqlError('invalid-value', `invalid value ${valueText(nv)} for '${lbl}' on ${itemRef}: ${rejected}`);
        writes.push({ item: root, target: item, ref: origin !== undefined ? this.refOf(owner) : itemRef, owner, segments, label: lbl, old: safeGet(slot, container, lbl), new: co.value, planned: co.value });
      }
    }

    const toRecords = (): MutationRecord[] =>
      writes.map((w): MutationRecord => ({ item: w.target, owner: w.owner, member: w.label, old: w.old, new: w.new, args: [] }));
    let records = once(toRecords);
    this.validateBatch('set', records);
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
    // No-op assignments (the asked-for value and the stored one both equal the old one) are counted, not listed
    // (spec §8 receipt); a write the host refused or clamped back to the old value stays listed.
    const changed = writes.filter((w) => !(equal(x, w.old, w.planned) === true && equal(x, w.old, w.new) === true));
    const rows = changed.map((w) => [w.ref, w.label, w.old, w.new]);
    const r = this.enc.rows(['$ref', 'member', 'old', 'new'], rows, 'changes');
    if (x.options.dryRun) r.dryRun = true;
    if (changed.length < writes.length) r.unchanged = writes.length - changed.length;
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
        if (i === w.segments.length - 1) return v;
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

  /** `top`: the segment is read on the target itself, so its unknown-member hint may suggest filtering the stream. */
  private slotOf(container: object, declared: TypeRef, seg: Segment, lbl: string, top = false): Slot {
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
      if (i < 0 || i >= container.length) {
        throw new RqlError('invalid-plan', `'${lbl}': index ${seg.index} out of range (count ${container.length})`, undefined, listGrowHint(lbl, seg));
      }
      return new ListSlot(seg.index, args?.of ?? 'any');
    }
    const name = seg.kind === 'member' ? seg.name : seg.kind === 'key' && typeof seg.key === 'string' ? seg.key : undefined;
    if (name === undefined) throw new RqlError('invalid-plan', `'${lbl}': invalid segment ${segmentToString(seg)} on ${info.shortName}`);
    const m = info.find(name, container);
    if (m === undefined) {
      const names = info.membersOf(container).map((mm) => mm.name);
      throw new RqlError(
        'unknown-member',
        `no member '${name}' on ${info.shortName}`,
        undefined,
        memberHint(name, names, top ? this.x.engine.typesDeclaring(name, [info]) : [], nestedMembers(this.x.types, [info])),
      );
    }
    return new MemberSlot(info, m);
  }

  private resolve(root: object, segs: readonly Segment[]): { slot: Slot; container: object; owner: object } {
    const lbl = segmentsLabel(segs);
    let cur: object = root;
    let declared: TypeRef = 'any';
    const chain: { slot: Slot; value: object }[] = [];
    for (let i = 0; i < segs.length; i++) {
      const slot = this.slotOf(cur, declared, segs[i]!, lbl, i === 0);
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
    const lbl = segmentsLabel(segs);
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

  /**
   * `receiver`: a source-qualified call's object (spec §8): its method runs once per stream item, with `$` and the other
   * metas the item's; undefined: the method runs on each item.
   */
  call(stream: Iterable<Item>, s: CallStage, resolveReceiver?: () => object): QueryResult {
    const x = this.x;
    this.checkPolicy('call');
    const receiver = resolveReceiver?.(); // after the policy check: a read request never evaluates the target
    const args: Eval[] = this.mutationValues(() => s.args.map((a) => this.c.compile(a)));
    const wanted = s.method.toUpperCase();
    // The callable overloads of the method on a target; none: unknown-function (or permission-denied for a method the host
    // does not expose), naming the targets' types and the sources that have it (spec §10).
    const candidatesOf = (item: object, stream: readonly Target[] | undefined): MethodAccessor[] => {
      const info = x.types.infoOf(item);
      const found = info.callable.filter((m) => m.name.toUpperCase() === wanted);
      if (found.length > 0) return found;
      const all = info.callable.map((m) => m.signature(x.types));
      const exists = hasMethod(item, wanted);
      // In a mixed stream, name the targets' types that do have the method (filter to them first).
      const having =
        stream === undefined
          ? []
          : [...new Set(stream.map((o) => x.types.infoOf(o.item.value as object)))]
              .filter((o) => o.callable.some((m) => m.name.toUpperCase() === wanted))
              .map((o) => o.shortName)
              .slice(0, 3);
      const filter = having.length > 0 ? filterHint(having, s.method, `call ${s.method}(…)`) + '; ' : '';
      throw new RqlError(
        exists ? 'permission-denied' : 'unknown-function',
        `no callable method '${s.method}' on ${stream === undefined ? x.engine.refOf(item) : info.shortName}`,
        undefined,
        filter +
          (all.length > 0 ? 'callable: ' + all.join(', ') : `${info.shortName} has no callable methods`) +
          (exists ? '' : x.engine.sourcesWithCallable(s.method)),
      );
    };
    // A source-qualified call checks its receiver's method before reading the stream: a typo fails even with no items.
    const fixed = receiver !== undefined ? candidatesOf(receiver, undefined) : undefined;
    const targets = this.targets(stream, 'call', s.method, receiver !== undefined);
    const planned: { item: object; method: MethodAccessor; args: unknown[]; index: number }[] = [];
    // Where a failure is: the target, or for a source-qualified call the receiver and the item's position.
    const at = (item: object, index: number): string => x.engine.refOf(item) + (receiver !== undefined ? ` for item ${index}` : '');
    const rules = hostRules(x);
    for (const t of targets) {
      const item = receiver ?? (t.item.value as object);
      const candidates = fixed ?? candidatesOf(item, targets);
      this.enterScope(t);
      const vals = args.map((a) => a(x));
      const resolved = resolveOverload(x.types, { ...rules, outer: item }, candidates, vals);
      if ('denied' in resolved) throw new RqlError('permission-denied', `cannot call ${s.method} on ${at(item, t.index)}: ${resolved.denied}`);
      if ('why' in resolved) {
        throw new RqlError('coercion-failed', `cannot call ${s.method} on ${at(item, t.index)}: ${resolved.why}`, undefined, 'signature: ' + candidates.map((m) => m.signature(x.types)).join(', '));
      }
      const chosen = resolved;
      // The method's own check, for every target before the first call (and on dry runs): all-or-nothing (spec §8).
      const check = chosen.method.validate;
      if (check !== undefined) {
        let rejected: string | null | undefined;
        try {
          rejected = check(item, chosen.args);
        } catch (e) {
          rejected = `its check threw ${e instanceof Error ? e.name : typeof e}: ${errorMessage(e)}`;
        }
        if (rejected !== null && rejected !== undefined) {
          throw new RqlError('invalid-value', `cannot call ${chosen.method.signature(x.types)} on ${at(item, t.index)}: ${rejected}; nothing was called`);
        }
      }
      planned.push({ item, ...chosen, index: t.index });
    }

    const callRecords = once(() =>
      planned.map(
        (p): MutationRecord => ({ item: p.item, owner: p.item, member: p.method.signature(x.types), old: undefined, new: undefined, method: p.method, args: p.args }),
      ),
    );
    this.validateBatch('call', callRecords);
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
          throw new RqlError('call-threw', `${p.method.name} on ${at(p.item, p.index)} threw ${name}: ${errorMessage(e)}; ${done} earlier call(s) completed and are NOT rolled back`);
        }
      }
      done++;
      // A source-qualified call reports what each invocation returned (the created object, spec §8).
      const refOf = receiver !== undefined && !x.options.dryRun && this.identified(result) ? (result as object) : p.item;
      rows.push([x.engine.refOf(refOf), p.method.signature(x.types), result]);
    }
    this.runHook('committed', callRecords);
    const r = this.enc.rows(['$ref', 'method', 'result'], rows, 'calls');
    if (x.options.dryRun) r.dryRun = true;
    return r;
  }

  /** An object with identity (not a list, map, struct or scalar): a receipt row can carry its $ref. */
  private identified(v: unknown): boolean {
    if (v === null || typeof v !== 'object' || v instanceof EnumValue || v instanceof Date || v instanceof ErrorValue) return false;
    return this.x.types.infoOf(v).kind === 'object';
  }
}

/** Why one candidate does not fit, ranked by how close it came (spec §8 "Overloads"). */
interface Miss {
  method: MethodAccessor;
  arityFits: boolean;
  matched: number;
  depth: number;
  why: string;
  /** Bound by name except one key that is a parameter's name misspelt (spec §8): closer than any failure depth. */
  misspelt?: boolean;
}

const compareMiss = (a: Miss, b: Miss): number =>
  a.arityFits !== b.arityFits
    ? a.arityFits
      ? 1
      : -1
    : a.matched !== b.matched
      ? a.matched - b.matched
      : (a.misspelt === true) !== (b.misspelt === true)
        ? a.misspelt === true
          ? 1
          : -1
        : a.depth - b.depth;

const hasDefault = (p: ParamSpec): boolean => p.optional === true || 'default' in p;

/**
 * Overload resolution and argument binding for `call` (spec §8 "Overloads"): in order, each candidate is checked with a
 * side-effect-free probe and the first that passes is coerced for real (the next is tried only when that fails); a single
 * object literal that no overload took binds by parameter name; a failure reports a host policy denial met on the way, else
 * the best-matching candidate.
 */
export function resolveOverload(
  model: TypeModel,
  rules: CoerceRules,
  candidates: readonly MethodAccessor[],
  given: readonly unknown[],
): { method: MethodAccessor; args: unknown[] } | { why: string } | { denied: string } {
  const misses: Miss[] = [];
  const probe: CoerceRules = { ...rules, probe: true };
  let denied: string | undefined;
  // Checks every bound value without side effects, then coerces them for real; a policy denial is remembered, not raised,
  // so an overload that would not be chosen cannot veto the call.
  const bind = (m: MethodAccessor, vals: readonly unknown[], slots: readonly number[], kind: string): unknown[] | undefined => {
    let matched = 0;
    let depth = 0;
    let first: string | undefined;
    for (let k = 0; k < slots.length; k++) {
      const p = m.params[slots[k]!]!;
      const co = coerce(model, vals[k], p.type ?? 'any', probe);
      if (co.ok) {
        matched++;
        continue;
      }
      if (co.code !== undefined) denied ??= `${kind} '${p.name}': ${co.why}`;
      if (first === undefined) {
        first = `${kind} '${p.name}': ${co.why}`;
        depth = co.depth ?? 0;
      }
    }
    if (first !== undefined) {
      // By name, every bound key names a parameter (spec §8): an exact name whose value fails ranks above a misspelt one.
      misses.push({ method: m, arityFits: true, matched: kind === 'parameter' ? slots.length : matched, depth, why: first });
      return undefined;
    }
    const cv: unknown[] = m.params.map((p) => p.default);
    for (let k = 0; k < slots.length; k++) {
      const p = m.params[slots[k]!]!;
      const co = coerce(model, vals[k], p.type ?? 'any', rules);
      if (co.ok) {
        cv[slots[k]!] = rawValue(co.value, p.type ?? 'any');
        continue;
      }
      // Only the real coercion sees what the probe cannot (member validators, constructors that throw, members of classes
      // the probe cannot see); a candidate that passed the probe is the closest match, so its reason is the one reported.
      misses.push({ method: m, arityFits: true, matched: Number.MAX_SAFE_INTEGER, depth: 0, why: `${kind} '${p.name}': ${co.why}` });
      return undefined;
    }
    return cv;
  };
  // 1. Positional.
  for (const m of candidates) {
    const ps = m.params;
    if (ps.length < given.length || ps.slice(given.length).some((p) => !hasDefault(p))) {
      misses.push({ method: m, arityFits: false, matched: 0, depth: 0, why: '' });
      continue;
    }
    const args = bind(
      m,
      given,
      given.map((_, i) => i),
      'argument',
    );
    if (args !== undefined) return { method: m, args };
  }
  // 2. By name: a single object literal that no overload took positionally names the parameters.
  if (given.length === 1 && given[0] instanceof ObjectValue) {
    const literal = given[0];
    const vals = [...literal.values()];
    for (const m of candidates) {
      const slots = bindNames(m, literal, misses);
      const args = slots !== undefined ? bind(m, vals, slots, 'parameter') : undefined;
      if (args !== undefined) return { method: m, args };
    }
  }
  return denied !== undefined ? { denied } : { why: explainMisses(model, misses, given.length) };
}

/** ASCII case-insensitive equality (parameter names, as in every runtime). */
const asciiLower = (s: string): string => s.replace(/[A-Z]/g, (c) => String.fromCharCode(c.charCodeAt(0) + 32));

/** The fewest arguments a call can pass: up to the last parameter without a default. */
function minArity(ps: readonly ParamSpec[]): number {
  let min = 0;
  ps.forEach((p, i) => {
    if (!hasDefault(p)) min = i + 1;
  });
  return min;
}

/**
 * The parameter index of each literal key (exact name, else the only case-insensitive match), or undefined with a miss when
 * a key names no parameter or a parameter without a default is not named.
 */
function bindNames(m: MethodAccessor, literal: ObjectValue, misses: Miss[]): number[] | undefined {
  const ps = m.params;
  const slots: number[] = [];
  const named = new Array<boolean>(ps.length).fill(false);
  let known = 0;
  let unknown: string | undefined;
  for (const key of literal.keys()) {
    let i = ps.findIndex((p) => p.name === key);
    if (i < 0) {
      const ci = ps.flatMap((p, j) => (asciiLower(p.name) === asciiLower(key) ? [j] : []));
      if (ci.length === 1) i = ci[0]!;
    }
    if (i < 0) {
      unknown ??= key;
      slots.push(-1);
      continue;
    }
    if (named[i]) {
      misses.push({ method: m, arityFits: true, matched: known, depth: 0, why: `parameter '${ps[i]!.name}' is named twice` });
      return undefined;
    }
    named[i] = true;
    slots.push(i);
    known++;
  }
  const all = ps.map((p) => p.name).join(', ');
  if (unknown !== undefined) {
    // A key close to a parameter not named yet is that parameter misspelt, so "no parameter 'klass' (did you mean
    // 'className'?)" beats an overload that cannot take the literal at all (spec §8).
    const close = closestParameter(
      unknown,
      ps.filter((_, j) => !named[j]).map((p) => p.name),
    );
    misses.push({
      method: m,
      arityFits: true,
      matched: known,
      misspelt: close !== undefined,
      depth: 0,
      why: `no parameter '${unknown}'` + (close !== undefined ? ` (did you mean '${close}'?)` : '') + (ps.length > 0 ? `; parameters: ${all}` : '; it takes no parameters'),
    });
    return undefined;
  }
  const missing = ps.findIndex((p, j) => !named[j] && !hasDefault(p));
  if (missing >= 0) {
    misses.push({ method: m, arityFits: true, matched: known, depth: 0, why: `missing parameter '${ps[missing]!.name}'; parameters: ${all}` });
    return undefined;
  }
  return slots;
}

/** The best candidate's reason; candidates that came equally close are listed with their signatures. */
function explainMisses(model: TypeModel, misses: readonly Miss[], given: number): string {
  const fit = misses.filter((x) => x.arityFits);
  if (fit.length === 0) {
    // Each overload accepts from its last parameter without a default up to all of them.
    const ranges = misses.map((x) => [minArity(x.method.params), x.method.params.length] as const).sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    const counts = [...new Set(ranges.map(([min, max]) => (min === max ? `${min}` : `${min} to ${max}`)))];
    return `expects ${counts.join(' or ')} argument(s), got ${given}`;
  }
  let best = fit[0]!;
  for (const x of fit) if (compareMiss(x, best) > 0) best = x;
  // One entry per method (a single literal argument can miss both positionally and by name).
  const tied = fit.filter((x, i) => compareMiss(x, best) === 0 && fit.findIndex((o) => o.method === x.method && compareMiss(o, best) === 0) === i);
  if (tied.length === 1) return best.why;
  return 'no overload fits: ' + tied.map((x) => `${x.method.signature(model)}: ${x.why}`).join('; ');
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
  return segmentsLabel(p.segments);
}

/** A set target only replaces an existing element; adding one means assigning the whole list. */
export function listGrowHint(lbl: string, seg: Segment & { kind: 'index' }): string {
  const at = lbl.lastIndexOf(segmentToString(seg));
  const list = at > 0 ? lbl.slice(0, at) : 'List';
  return (
    `an indexed set replaces an existing element only; to add elements assign the whole list: set ${list} = append(${list}, value), ` +
    `set ${list} = insert(${list}, ${seg.index}, value) or set ${list} = [ ... ]`
  );
}

function segmentsLabel(segments: readonly Segment[]): string {
  return segments.map((s, i) => (s.kind === 'member' ? (i === 0 ? '' : '.') + s.name : segmentToString(s))).join('');
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

/**
 * Coercion outcome; a failure may carry `permission-denied` (host policy) instead of coercion-failed, and `depth`: how deep
 * inside the value it failed (0 or absent: the value itself, 1: a member or element of it, …), which ranks overloads.
 */
type Coerced = { ok: true; value: unknown } | { ok: false; why: string; code?: 'permission-denied'; depth?: number };

/** Host allow-check for members assigned while constructing from object literals (`mutations.canWrite`). */
type WritePolicy = ((type: TypeInfo, member: MemberAccessor) => boolean) | undefined;

/**
 * The host rules a coercion applies (spec §8, §4.5): the per-member write policy, the text coercion and which classes an
 * object literal may build. A `probe` checks convertibility without side effects (overload resolution): nothing is
 * constructed, assigned or validated (member validators need the instance); adapters and the text coercion answer as usual.
 */
export interface CoerceRules {
  policy?: WritePolicy;
  text?: TextCoercion | undefined;
  canConstruct?: ((ctor: Ctor) => boolean | null | undefined) | undefined;
  /** The host's literal-kind hook (spec §4.5, §13): asked before the adapters. */
  literalKind?: ((ctor: Ctor) => LiteralKind | null | undefined) | undefined;
  /** The host's literal factory (ctor, outer): asked before the adapters' factories and `new ctor()`. */
  create?: ((ctor: Ctor, outer: object | undefined) => object | null | undefined) | undefined;
  /** The host's advice ending the engine-object refusal. */
  refusalHint?: ((ctor: Ctor) => string | null | undefined) | undefined;
  /** The object that will own the value (spec §4.5): instanced literals are built for it. */
  outer?: object | undefined;
  probe?: boolean;
  /** Literal kinds already resolved for this stage: the hooks answer per class and cannot change within it. */
  kinds?: Map<Ctor, LiteralKind>;
}

/** The rules of an execution's host. */
export function hostRules(x: Exec): CoerceRules {
  const m = x.engine.mutations;
  return {
    policy: m.canWrite,
    text: m.coerceText,
    canConstruct: m.canConstructFromLiteral,
    literalKind: m.literalKindOf,
    create: m.createForLiteral,
    refusalHint: m.literalRefusalHint,
    kinds: new Map(),
  };
}

/** Stands for the object a probed literal would build (nothing is constructed), so its nested values have an outer. */
const PROBE_OUTER: object = Object.freeze({});

/** A nested failure with more context; keeps a policy denial's code and counts one level deeper. */
const nested = (co: { why: string; code?: 'permission-denied'; depth?: number }, prefix: string): Coerced => {
  const depth = (co.depth ?? 0) + 1;
  return co.code !== undefined ? { ok: false, why: prefix + co.why, code: co.code, depth } : { ok: false, why: prefix + co.why, depth };
};

/** Whether `v` already is a `ctor` value: an instance, or tagged/declared as that type or a subtype. */
function isInstance(model: TypeModel, v: unknown, ctor: Ctor, ti: TypeInfo | undefined): boolean {
  return typeof v === 'object' && v !== null && (v instanceof ctor || (ti !== undefined && model.infoOf(v).isA(ti.shortName)));
}

/**
 * Coerces a value to a declared type (spec §8): number → int only if integral and in range,
 * string → enum by name, null only for nullable/reference types, list literal → list type.
 */
export function coerce(model: TypeModel, v: unknown, target: TypeRef, rules: CoerceRules = {}): Coerced {
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
      // Structs have value semantics: assign a copy (a probe only answers).
      if (ti?.kind === 'struct' && rules.probe !== true) return { ok: true, value: Object.assign(Object.create(Object.getPrototypeOf(v) as object | null) as object, v) };
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
    // The host's text form for struct / object types no adapter handled (spec §8).
    if (rules.text !== undefined && typeof v === 'string') {
      let parsed: unknown;
      try {
        parsed = rules.text(t.ctor, v);
      } catch (e) {
        return { ok: false, why: errorMessage(e) };
      }
      if (parsed !== undefined && parsed !== null) {
        if (!isInstance(model, parsed, t.ctor, ti)) throw new Error(`the host's text coercion returned ${describeValue(parsed)}, not ${t.ctor.name}`);
        return { ok: true, value: parsed };
      }
    }
  }
  if (v instanceof ObjectValue) return fromLiteral(model, v, t, target, rules);
  if (t instanceof EnumType) {
    const fail: Coerced = { ok: false, why: t.expected() };
    if (v instanceof EnumValue) return v.type === t ? { ok: true, value: v } : fail;
    if (typeof v === 'string') {
      // Enum name resolution (spec §8): exact, qualified, case-insensitive, entry prefix; ambiguity refused.
      const r = t.resolve(v);
      if (!('value' in r)) return { ok: false, why: r.why };
      return t.isDefined(r.value) || t.flags ? { ok: true, value: r.value } : fail;
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
        const co = coerce(model, el === undefined ? null : el, t.of, rules);
        if (!co.ok) return nested(co, 'element: ');
        const raw = rawValue(co.value, t.of);
        if (raw !== el) same = false;
        items.push(raw);
      }
      // Natural JS semantics (spec §12): an array that needs no coercion is assigned as is (alias).
      return { ok: true, value: same ? v : items };
    }
    case 'map':
      return isMapLike(v) ? coerceMap(model, v, t.key, t.of, target, rules) : { ok: false, why: `expected ${friendlyName(target, model)}` };
    default:
      return { ok: false, why: `expected ${friendlyName(target, model)}` };
  }
}

/** A map value (or object literal) built entry by entry: keys parsed to the key type, values coerced. */
function coerceMap(model: TypeModel, v: MapLike, keyType: TypeRef, valueType: TypeRef, target: TypeRef, rules: CoerceRules): Coerced {
  const out = new Map<unknown, unknown>();
  for (const [k, el] of v.entries()) {
    const key = parseKey(k, keyType);
    if (key === undefined) return { ok: false, why: `invalid key '${String(k)}' for ${friendlyName(target, model)}`, depth: 1 };
    const co = coerce(model, el === undefined ? null : el, valueType, rules);
    if (!co.ok) return nested(co, `'${String(k)}': `);
    out.set(key, rawValue(co.value, valueType));
  }
  return { ok: true, value: out };
}

/** Object literal → map or constructed object (spec §4.5); anything else cannot be built from one. */
function fromLiteral(model: TypeModel, literal: ObjectValue, t: TypeRef, target: TypeRef, rules: CoerceRules): Coerced {
  if (typeof t === 'object' && !(t instanceof EnumType)) {
    if (t.kind === 'map') return coerceMap(model, literal, t.key, t.of, target, rules);
    if (t.kind === 'class') return construct(model, literal, t.ctor, model.infoForCtor(t.ctor), rules);
  }
  return { ok: false, why: `an object literal cannot become ${friendlyName(target, model)}` };
}

/**
 * The literal kind of `ctor` (spec §4.5, §13): the host's literal-kind hook, else the adapters (most specific first), else
 * engine for a class with a stableId or isAlive adapter and value for any other; then the host's canConstructFromLiteral
 * (false → engine, true → an engine class becomes value).
 */
function literalKindOf(model: TypeModel, ctor: Ctor, rules: CoerceRules): LiteralKind {
  const cached = rules.kinds?.get(ctor);
  if (cached !== undefined) return cached;
  const set = model.adapterSetFor(ctor);
  let kind = rules.literalKind?.(ctor) ?? undefined;
  if (kind === undefined) kind = firstAnswer(set.literalKind, (a) => a.literalKind!(ctor));
  let k: LiteralKind = kind ?? (set.stableId.length === 0 && set.isAlive.length === 0 ? 'value' : 'engine');
  const decided = rules.canConstruct?.(ctor);
  if (decided === false) k = 'engine';
  else if (decided === true && k === 'engine') k = 'value';
  rules.kinds?.set(ctor, k);
  return k;
}

/** The value a factory builds for a literal of `ctor` (host first, then adapters); undefined: none answered. */
function createForLiteral(model: TypeModel, ctor: Ctor, outer: object | undefined, rules: CoerceRules): object | undefined {
  const made = rules.create?.(ctor, outer) ?? undefined;
  if (made !== undefined) return made;
  return firstAnswer(model.adapterSetFor(ctor).createForLiteral, (a) => a.createForLiteral!(ctor, outer) ?? undefined);
}

/**
 * Constructs the target class from an object literal (spec §4.5): the concrete type is the target or the
 * registered subclass named by "$type"; every other key must name a writable member, whose value is coerced
 * recursively and validated. Members not named keep the constructor's defaults. A probe checks the same rules without
 * constructing (members of classes whose members depend on the instance are then left to the real coercion).
 */
function construct(model: TypeModel, literal: ObjectValue, ctor: Ctor, ti: TypeInfo | undefined, rules: CoerceRules): Coerced {
  let concrete = ctor;
  if (literal.has('$type')) {
    const tn = literal.get('$type');
    if (typeof tn !== 'string') return { ok: false, why: '"$type" must be a type name string' };
    const hits = model.subtypesNamed(ctor, tn);
    if (hits.length === 0) return { ok: false, why: `no registered type named '${tn}' derives from ${ti?.shortName ?? ctor.name}${typeNameHint(tn, model.subtypeNames(ctor))}` };
    if (hits.length > 1) return { ok: false, why: `'${tn}' is ambiguous: ${hits.map((c) => c.name).join(', ')}` };
    concrete = hits[0]!;
  }
  // Engine objects are never built from literals (spec §4.5): only existing ones can be passed.
  const kind = literalKindOf(model, concrete, rules);
  const name = model.infoForCtor(concrete)?.shortName ?? concrete.name;
  if (kind === 'engine') {
    const hint = rules.refusalHint?.(concrete);
    return { ok: false, why: `${name} is an engine object and cannot be built from an object literal; ${hint !== undefined && hint !== null && hint !== '' ? hint : 'pass an existing one (e.g. ref("…"))'}` };
  }
  // An instanced subobject is built for the object that owns it (spec §4.5).
  if (kind === 'instanced' && rules.outer === undefined) {
    return { ok: false, why: `${name} is an instanced subobject: it is built only for the object that owns it (write it to a member of that object or pass it to a call)` };
  }
  let instance: object | undefined;
  if (rules.probe !== true) {
    try {
      instance = createForLiteral(model, concrete, rules.outer, rules);
    } catch (e) {
      return { ok: false, why: `building ${name} failed: ${errorMessage(e)}` };
    }
    if (instance !== undefined && !isInstance(model, instance, concrete, model.infoForCtor(concrete))) throw new Error(`the literal factory built ${describeValue(instance)}, not ${concrete.name}`);
    if (instance === undefined) {
      try {
        instance = new (concrete as unknown as new () => object)();
      } catch (e) {
        return { ok: false, why: `cannot construct ${concrete.name} (needs a constructor without required arguments, or an adapter that coerces object literals): ${errorMessage(e)}` };
      }
    }
  }
  const ci = instance !== undefined ? model.infoOf(instance) : model.infoForCtor(concrete);
  // A probe cannot see the members of a class whose member table depends on the instance: the real coercion checks them.
  if (ci === undefined || (instance === undefined && ci.dynamic !== undefined)) return { ok: true, value: undefined };
  // Values nested in an object literal belong to the object it builds; a struct passes its own outer on.
  const inner: CoerceRules = ci.kind === 'object' ? { ...rules, outer: instance ?? PROBE_OUTER } : rules;
  for (const [key, value] of literal) {
    if (key === '$type') continue;
    const m = ci.find(key, instance);
    if (m === undefined) return { ok: false, why: `no member '${key}' on ${ci.shortName}`, depth: 1 };
    if (!m.canWrite || m.set === undefined) return { ok: false, why: `member '${m.name}' of ${ci.shortName} is read-only`, depth: 1 };
    // Construction is a write: the host policy applies to every member assigned (spec §13).
    if (rules.policy !== undefined && !rules.policy(ci, m)) return { ok: false, why: `member '${m.name}' of ${ci.shortName} is denied by host policy`, code: 'permission-denied', depth: 1 };
    const co = coerce(model, value === undefined ? null : value, m.type, inner);
    if (!co.ok) return nested(co, `'${m.name}': `);
    if (instance === undefined) continue;
    const rejected = m.validate?.(instance, co.value);
    if (rejected !== undefined) return { ok: false, why: `'${m.name}': ${rejected}`, depth: 1 };
    try {
      m.set(instance, co.value);
    } catch (e) {
      return { ok: false, why: `'${m.name}': ${errorMessage(e)}`, depth: 1 };
    }
  }
  return { ok: true, value: instance };
}

/** "only A, B have X: filter first, e.g. … | where $ is A | stage" (spec §10 mixed streams). */
function filterHint(having: readonly string[], what: string, stage: string): string {
  return `only ${having.join(', ')} ${having.length === 1 ? 'has' : 'have'} ${what}: filter first, e.g. … | where $ is ${having[0]} | ${stage}`;
}
