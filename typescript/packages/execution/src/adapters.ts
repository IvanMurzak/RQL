/**
 * Type adapters (spec §13): host extensions that customise how values of a class family are read
 * (virtual / hidden / read-only / explicit-only members), shown (encoding), written (coercion) and
 * compared, plus identity and liveness. Port of dotnet/Rql.Execution/TypeAdapters.cs.
 *
 * Applicability tiers available in JS: the exact class, then base classes by prototype-chain distance
 * (up to `Object`), then structural "interface" adapters selected by a `matches(ctor)` predicate.
 * Ties: higher `priority` first, then registration order.
 */
import type { JsonValue } from '@rql/contracts';
import type { Ctor, MemberAccessor, TypeRef } from './types.js';

/** Outcome of an adapter coercion: `undefined` = not handled (defer to the next adapter / default). */
export type CoerceOutcome = { ok: true; value: unknown } | { ok: false; reason: string } | undefined;

/** What an adapter's `encode` can use. */
export interface EncodeContext {
  /** Nesting depth of the value being encoded (0 = a top-level cell). */
  readonly depth: number;
  /** Encode another value with full adapter support (e.g. a member of this value). */
  encodeValue(value: unknown): JsonValue;
  /** The standard object summary `{"$ref": …, "name": …}`. */
  summary(value: object): JsonValue;
}

export interface VirtualMemberSpec<T extends object, V = unknown> {
  /** Declared type (describe, coercion of written values). Default `any`. */
  type?: TypeRef;
  get(target: T): V;
  /** Optional setter; MUST round-trip the getter (`set(get())` restores the state) for rollbacks. */
  set?(target: T, value: V): void;
  /** Runs while a mutation is planned (before any write); returns a rejection reason, or undefined to accept. */
  validate?(target: T, value: V): string | undefined;
  description?: string;
  /** Virtual members are computed values: not followed by generic `recurse` unless `owning: true`. */
  owning?: boolean;
}

/** The member table of one type while adapters shape it (least specific adapter first). */
export interface MemberTable<T extends object = object> {
  /** Short name of the type being configured. */
  readonly typeName: string;
  readonly members: readonly MemberAccessor[];
  has(name: string): boolean;
  /** Add (or replace) a virtual member. */
  add<V>(name: string, spec: VirtualMemberSpec<T, V>): this;
  /** Remove members entirely (unreadable and unwritable). Absent names are ignored. */
  hide(...names: string[]): this;
  /** Keep only the named members. */
  only(...names: string[]): this;
  readOnly(...names: string[]): this;
  /** Non-owning references: generic `recurse` does not follow them. */
  reference(...names: string[]): this;
  /** Read only when a path names the member: never by generic recurse, inline encoding or ref search. */
  explicitOnly(...names: string[]): this;
  describe(name: string, description: string): this;
}

interface AdapterCapabilities<T extends object> {
  /** Tie-break among adapters of equal specificity (higher first). Default 0. */
  priority?: number;
  /** Name shown by `explain` (default: the target class name). */
  name?: string;
  /** One line for schemas and `describe`, e.g. how to write literals of the type. */
  description?: string;
  members?(table: MemberTable<T>): void;
  /** Replace the output encoding; `undefined` defers. */
  encode?(value: T, ctx: EncodeContext): JsonValue | undefined;
  /** Convert a query value (literal, list, other value) to `target` (the adapter's class or a subclass). */
  coerce?(value: unknown, target: Ctor): CoerceOutcome;
  /** Equality with any other value; `undefined` defers. */
  equals?(value: T, other: unknown): boolean | undefined;
  /** Ordering with any other value (sign matters); `undefined` defers. */
  compare?(value: T, other: unknown): number | undefined;
  /** Stable id for `$ref` ("Type#id"); `undefined` defers to the identity member / session handles. */
  stableId?(value: T): string | undefined;
  /** Liveness verdict (destroyed objects → stale-ref); `undefined` defers. */
  isAlive?(value: T): boolean | undefined;
}

/** Adapter for a class and its subclasses. */
export interface ClassAdapter<T extends object = object> extends AdapterCapabilities<T> {
  target: Ctor<T>;
}

/** Structural ("interface") adapter: applies to every class for which `matches` returns true. */
export interface InterfaceAdapter extends AdapterCapabilities<object> {
  target?: undefined;
  /** Label for `explain`. */
  interfaceName: string;
  matches(ctor: Ctor): boolean;
}

export type TypeAdapter<T extends object = object> = ClassAdapter<T> | InterfaceAdapter;

/** Internal view: capabilities over `object` (values are checked against the target before calls). */
export type AnyAdapter = AdapterCapabilities<object> & { target?: Ctor; interfaceName?: string; matches?(ctor: Ctor): boolean };

const INTERFACE_RANK = 10000;

/** Value capabilities an adapter may implement (member shaping is applied at type build time). */
export const CAPABILITIES = ['equals', 'compare', 'encode', 'coerce', 'stableId', 'isAlive'] as const;
export type Capability = (typeof CAPABILITIES)[number];

/** Applicable adapters of one type split per capability, most specific first. */
export type AdapterSet = { readonly [C in Capability]: readonly AnyAdapter[] };

const EMPTY: readonly AnyAdapter[] = [];
export const NO_ADAPTERS: AdapterSet = { equals: EMPTY, compare: EMPTY, encode: EMPTY, coerce: EMPTY, stableId: EMPTY, isAlive: EMPTY };

export function adapterSet(adapters: readonly AnyAdapter[]): AdapterSet {
  if (adapters.length === 0) return NO_ADAPTERS;
  const pick = (c: Capability): readonly AnyAdapter[] => {
    const l = adapters.filter((a) => typeof a[c] === 'function');
    return l.length > 0 ? l : EMPTY;
  };
  return { equals: pick('equals'), compare: pick('compare'), encode: pick('encode'), coerce: pick('coerce'), stableId: pick('stableId'), isAlive: pick('isAlive') };
}

/** First defined answer of `ask` over `adapters` (most specific first); undefined when all defer. */
export function firstAnswer<R>(adapters: readonly AnyAdapter[], ask: (a: AnyAdapter) => R | undefined): R | undefined {
  for (const a of adapters) {
    const r = ask(a);
    if (r !== undefined) return r;
  }
  return undefined;
}

/** Adapter registry and deterministic resolution (spec §13). */
export class AdapterRegistry {
  private readonly list: AnyAdapter[] = [];
  private readonly byCtor = new Map<Ctor, readonly AnyAdapter[]>();
  private readonly any: Record<Capability, boolean> = { equals: false, compare: false, encode: false, coerce: false, stableId: false, isAlive: false };

  /** @param internal engine-internal classes (rows, map entries, absent markers, errors): never adapted. */
  constructor(private readonly internal: ReadonlySet<object>) {}

  get size(): number {
    return this.list.length;
  }

  /** Whether any registered adapter implements the capability (gates the hot paths). */
  has(c: Capability): boolean {
    return this.any[c];
  }

  add(adapter: TypeAdapter): void {
    const a = adapter as unknown as AnyAdapter;
    if (a.target === undefined && typeof a.matches !== 'function') throw new Error('type adapter needs a target class or a matches() predicate');
    this.list.push(a);
    for (const c of CAPABILITIES) if (typeof a[c] === 'function') this.any[c] = true;
    this.byCtor.clear();
  }

  copyTo(other: AdapterRegistry): void {
    for (const a of this.list) other.add(a as TypeAdapter);
  }

  /** Applicable adapters, most specific first (none for engine-internal classes). */
  for(ctor: Ctor | undefined): readonly AnyAdapter[] {
    if (ctor === undefined || this.list.length === 0 || this.internal.has(ctor)) return EMPTY;
    let hit = this.byCtor.get(ctor);
    if (hit === undefined) {
      hit = this.list
        .map((a, index) => ({ a, index, rank: rank(a, ctor) }))
        .filter((h) => h.rank >= 0)
        .sort((x, y) => x.rank - y.rank || (y.a.priority ?? 0) - (x.a.priority ?? 0) || x.index - y.index)
        .map((h) => h.a);
      this.byCtor.set(ctor, hit);
    }
    return hit;
  }

  explain(ctor: Ctor): string[] {
    return this.for(ctor).map((a) => `${adapterName(a)} (target ${a.target?.name ?? 'interface ' + (a.interfaceName ?? '?')}, rank ${rank(a, ctor)}, priority ${a.priority ?? 0})`);
  }
}

export function adapterName(a: AnyAdapter): string {
  return a.name ?? (a.target !== undefined ? a.target.name + 'Adapter' : (a.interfaceName ?? 'adapter'));
}

/** Specificity rank (lower = more specific), -1 = not applicable: base-chain distance, then the interface tier. */
export function rank(a: AnyAdapter, ctor: Ctor): number {
  if (a.target === undefined) return a.matches?.(ctor) ? INTERFACE_RANK : -1;
  let d = 0;
  for (let c: unknown = ctor; typeof c === 'function'; c = Object.getPrototypeOf(c), d++) {
    if (c === a.target) return d;
    if (c === Object) break;
  }
  // Every class ultimately derives from Object (including classes extending null-prototype chains).
  return a.target === Object ? d : -1;
}
