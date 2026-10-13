/** Runtime value kinds of the evaluator (spec §1.1) that have no native JS counterpart. */
import type { DiagnosticCode } from '@rql/contracts';

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
