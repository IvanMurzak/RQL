/** Runtime value kinds of the evaluator (spec §1.1) that have no native JS counterpart. */
import type { DiagnosticCode } from '@rql/contracts';
import type { TypeRef } from './types.js';

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

/** Where a streamed entry came from (spec §8): `set Value… = …` writes through `map`. */
export interface EntryOrigin {
  readonly map: object;
  /** The stream item the map was read from (by `get`), for receipts and host hooks; undefined for a source. */
  readonly holder: object | undefined;
  /** The member path the map was read by (`Props` for `get Props`), prefixing receipt labels. */
  readonly label: string | undefined;
  /** The map's declared type (from the member `get` read), so writes coerce to its value type. */
  readonly type: TypeRef;
}

const entryOrigins = new WeakMap<MapEntry, EntryOrigin>();

/** A streamed entry remembering its map (internal: not part of the entry's members). */
export function streamedEntry(e: MapEntry, origin: EntryOrigin): MapEntry {
  const s = new MapEntry(e.Key, e.Value);
  entryOrigins.set(s, origin);
  return s;
}

export const entryOrigin = (e: MapEntry): EntryOrigin | undefined => entryOrigins.get(e);

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
