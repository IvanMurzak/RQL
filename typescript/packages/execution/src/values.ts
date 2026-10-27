/** Runtime value kinds of the evaluator (spec §1.1) that have no native JS counterpart. */
import type { DiagnosticCode } from '@rql/contracts';
import type { Segment } from '@rql/contracts';

/** The path does not exist (no such member/key/index). Distinct from null. */
export class Missing {
  static readonly value = new Missing();
  /** Nominal brand (keeps type guards from narrowing unrelated values). */
  readonly absent = 'missing' as const;
  private constructor() {}
  toString(): string {
    return 'missing';
  }
}

/** Third truth value of Kleene logic (spec §1.2). */
export class Unknown {
  static readonly value = new Unknown();
  readonly absent = 'unknown' as const;
  private constructor() {}
  toString(): string {
    return 'unknown';
  }
}

export const MISSING = Missing.value;
export const UNKNOWN = Unknown.value;

/** An evaluation error value (getter threw, division by zero, type mismatch...). */
export class ErrorValue {
  constructor(
    readonly code: DiagnosticCode,
    readonly detail?: string,
  ) {}
  toString(): string {
    return `error(${this.code})`;
  }
}

/** A key/value pair yielded when iterating a map (spec §4.4). Members: Key, Value. */
export class MapEntry {
  constructor(
    readonly Key: unknown,
    readonly Value: unknown,
  ) {}
}

/**
 * The map of streamed entries as a write path (spec §8): `root` plus `prefix` reach the map, so `set Value.p` on an entry
 * is the ordinary `set` of `prefix[key].p` on the root (owners, value types and write-back of copies come with the path).
 * When `get` used a computed expression the root is the map itself.
 */
export interface EntryOrigin {
  readonly root: object;
  readonly prefix: readonly Segment[];
  /** The object the map was read from (owner fallback); undefined for a map source. */
  readonly holder: object | undefined;
}

const entryOrigins = new WeakMap<MapEntry, EntryOrigin>();

/** A streamed entry remembering its map (internal: not part of the entry's members). */
export function streamedEntry(e: MapEntry, origin: EntryOrigin): MapEntry {
  const s = new MapEntry(e.Key, e.Value);
  entryOrigins.set(s, origin);
  return s;
}

export const entryOrigin = (e: MapEntry): EntryOrigin | undefined => entryOrigins.get(e);

/** Whether `s` is the member `upperName` (case-insensitive; `upperName` upper-cased). */
export const isMember = (s: Segment, upperName: string): boolean => s.kind === 'member' && s.name.toUpperCase() === upperName;

/**
 * Where map `map`, read by `get` from `from` along `segments` (a plain path, else undefined), lives (spec §8): that path
 * from the item — or, for an entry item, the entry's path continued through `[key]` — else the map itself.
 */
export function originOf(from: unknown, map: object, segments: readonly Segment[] | undefined): EntryOrigin {
  if (from instanceof MapEntry) {
    const o = entryOrigin(from);
    if (o === undefined || segments === undefined || segments.length === 0 || isMember(segments[0]!, 'KEY')) return { root: map, prefix: [], holder: o?.holder };
    const keySeg = { kind: 'key', key: from.Key } as Segment;
    return { root: o.root, prefix: [...o.prefix, keySeg, ...(isMember(segments[0]!, 'VALUE') ? segments.slice(1) : segments)], holder: o.holder };
  }
  const holder = typeof from === 'object' && from !== null && !(from instanceof RowValue) ? from : undefined;
  return holder !== undefined && segments !== undefined ? { root: holder, prefix: segments, holder } : { root: map, prefix: [], holder };
}

/** Projected row flowing between stages after `select`; its members are the column names. */
export class RowValue {
  constructor(
    readonly columns: readonly string[],
    readonly values: unknown[],
  ) {}

  indexOf(name: string): number {
    const i = this.columns.indexOf(name);
    if (i >= 0) return i;
    const up = name.toUpperCase();
    return this.columns.findIndex((c) => c.toUpperCase() === up);
  }
}

/** missing / error / unknown: values that are not a real value. */
export function isAbsent(v: unknown): v is Missing | Unknown | ErrorValue {
  return v === MISSING || v === UNKNOWN || v instanceof ErrorValue;
}

/** Numeric value of number / bigint (enums and bools are not numbers). */
export function toNumber(v: unknown): number | undefined {
  if (typeof v === 'number') return v;
  if (typeof v === 'bigint') return Number(v);
  return undefined;
}

/**
 * Value of an object literal `{ key: expr, … }` (spec §4.5): an ordered string-keyed map that behaves like
 * any map. Writes coerce it into the target type (adapters first, then map building or construction).
 */
export class ObjectValue extends Map<string, unknown> {}
