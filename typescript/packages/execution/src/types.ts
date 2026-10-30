/**
 * Reflection model for live JS objects.
 *
 * TypeScript types are erased at runtime, so the executor learns about a host's classes from
 * **type descriptors** registered on a {@link TypeModel} (`model.register(Node, {...})`): member
 * order, declared member types (int vs number, enums, structs, lists, maps), identity/display
 * members, non-owning references, read-only members and callable methods. Values whose class is not
 * registered still work through a dynamic fallback (own enumerable properties + prototype getters),
 * with the limits documented in the README (no enum/struct/int typing, per-instance member sets).
 */
import { AdapterRegistry, adapterName, adapterSet, firstAnswer, type AdapterSet, type AnyAdapter, type Capability, type ClassAdapter, type InterfaceAdapter, type MemberTable, type TypeAdapter, type VirtualMemberSpec } from './adapters.js';
import type { JsonValue } from '@rql/contracts';
import { ErrorValue, MapEntry, Missing, ObjectValue, RowValue, Unknown } from './values.js';

/** Any class constructor (including abstract classes and classes with constructor parameters). */
export type Ctor<T extends object = object> = abstract new (...args: never[]) => T;

// ------------------------------------------------------------------ type references

export type ScalarName = 'string' | 'number' | 'int' | 'bool' | 'any';

/** Declared type of a member / parameter. */
export type TypeRef =
  | ScalarName
  | EnumType
  | { readonly kind: 'nullable'; readonly of: TypeRef }
  | { readonly kind: 'list'; readonly of: TypeRef }
  | { readonly kind: 'map'; readonly key: TypeRef; readonly of: TypeRef }
  | { readonly kind: 'class'; readonly ctor: Ctor };

/** Builders for {@link TypeRef}s. */
export const t = {
  string: 'string',
  number: 'number',
  int: 'int',
  bool: 'bool',
  any: 'any',
  nullable: (of: TypeRef): TypeRef => ({ kind: 'nullable', of }),
  list: (of: TypeRef): TypeRef => ({ kind: 'list', of }),
  map: (key: TypeRef, of: TypeRef): TypeRef => ({ kind: 'map', key, of }),
  /** A registered class (object or struct). */
  of: (ctor: Ctor): TypeRef => ({ kind: 'class', ctor }),
} as const satisfies Record<string, TypeRef | ((...args: never[]) => TypeRef)>;

export function unwrapNullable(r: TypeRef): TypeRef {
  return typeof r === 'object' && !(r instanceof EnumType) && r.kind === 'nullable' ? r.of : r;
}

export function isNullable(r: TypeRef): boolean {
  return r === 'any' || (typeof r === 'object' && !(r instanceof EnumType) && r.kind === 'nullable');
}

/**
 * Readable declared type name for schemas, in the C#-like spelling the reference runtime uses so
 * `describe` output is identical across runtimes: `int`, `double`, `string`, `bool`, `object`,
 * `List<T>`, `Dictionary<K, V>`, class names; `?` only for nullable value-like types.
 */
export function friendlyName(r: TypeRef, model?: TypeModel): string {
  if (typeof r === 'string') {
    switch (r) {
      case 'number':
        return 'double';
      case 'any':
        return 'object';
      default:
        return r;
    }
  }
  if (r instanceof EnumType) return r.name;
  switch (r.kind) {
    case 'nullable': {
      const inner = friendlyName(r.of, model);
      return isReferenceType(r.of, model) ? inner : inner + '?';
    }
    case 'list':
      return `List<${friendlyName(r.of, model)}>`;
    case 'map':
      return `Dictionary<${friendlyName(r.key, model)}, ${friendlyName(r.of, model)}>`;
    case 'class':
      return model?.infoForCtor(r.ctor)?.shortName ?? r.ctor.name;
  }
}

/** Reference-like declared types (nullability is implicit, as for .NET reference types). */
function isReferenceType(r: TypeRef, model?: TypeModel): boolean {
  if (r === 'string' || r === 'any') return true;
  if (typeof r === 'string' || r instanceof EnumType) return false;
  if (r.kind === 'class') return model?.infoForCtor(r.ctor)?.kind !== 'struct';
  return r.kind === 'list' || r.kind === 'map';
}

// ------------------------------------------------------------------ enums

/** A runtime enum value: encodes as its name, compares to strings by name and to numbers by value. */
export class EnumValue {
  constructor(
    readonly type: EnumType,
    /** The raw value stored in host objects (TS enum member value). */
    readonly value: number | string,
    readonly name: string,
    /** Numeric value used for ordering / number comparison (declaration index for string enums). */
    readonly ordinal: number,
  ) {}
  toString(): string {
    return this.name;
  }
}

/** An entry name without its entry prefix (text up to and including the first `_`), or undefined (spec §8). */
function withoutEntryPrefix(name: string): string | undefined {
  const i = name.indexOf('_');
  return i > 0 && i < name.length - 1 ? name.slice(i + 1) : undefined;
}

/**
 * Enum descriptor. Accepts a TS enum object (`enum NodeKind { Alpha, Beta }` — the numeric reverse
 * mapping is ignored), a name → value record, or a list of names (values 0..n-1).
 */
export class EnumType {
  readonly name: string;
  readonly flags: boolean;
  readonly description: string | undefined;
  readonly values: readonly EnumValue[];
  private readonly byRaw = new Map<number | string, EnumValue>();
  /** Host alias names (spec §8 enum name resolution), e.g. an editor's display names, with their entries. */
  private readonly aliases: readonly [string, EnumValue][];

  /**
   * `options.aliases` maps alias names to entry names (`{ Fill: 'HAlign_Fill' }`): they resolve like entry names
   * (exactly, then ignoring case) in writes, map keys and comparisons; encoding keeps the entry name.
   */
  constructor(
    name: string,
    values: Readonly<Record<string, number | string>> | readonly string[],
    options: { flags?: boolean; description?: string; aliases?: Readonly<Record<string, string>> } = {},
  ) {
    this.name = name;
    this.flags = options.flags ?? false;
    this.description = options.description;
    const entries: [string, number | string][] = Array.isArray(values)
      ? (values as readonly string[]).map((n, i) => [n, i])
      : Object.entries(values as Readonly<Record<string, number | string>>).filter(([k]) => !/^-?\d+(\.\d+)?$/.test(k));
    this.values = entries.map(([n, v], i) => new EnumValue(this, v, n, typeof v === 'number' ? v : i));
    for (const ev of this.values) if (!this.byRaw.has(ev.value)) this.byRaw.set(ev.value, ev);
    this.aliases = Object.entries(options.aliases ?? {}).flatMap(([alias, entry]): [string, EnumValue][] => {
      const ev = this.values.find((v) => v.name === entry);
      return ev === undefined ? [] : [[alias, ev]];
    });
  }

  /** Wraps a raw host value (defined values are cached instances). */
  wrap(raw: number | string): EnumValue {
    const ev = this.byRaw.get(raw);
    if (ev !== undefined) return ev;
    if (typeof raw === 'string') return new EnumValue(this, raw, raw, -1);
    return new EnumValue(this, raw, this.formatUndefined(raw), raw);
  }

  isDefined(ev: EnumValue): boolean {
    return ev.type === this && this.byRaw.get(ev.value) === ev;
  }

  /** {@link resolve}; undefined when the text resolves to no entry or to several. */
  parse(text: string): EnumValue | undefined {
    const r = this.resolve(text);
    return 'value' in r ? r.value : undefined;
  }

  /**
   * Enum name resolution (spec §8): integer text is the value (not checked for being defined); a flags enum accepts a
   * comma-separated list; otherwise the first step with candidates decides: exact name or alias, the part after a
   * `Type::` / `Type.` qualifier, ignoring case, ignoring the entry prefix (`HAlign_`). Several entries are ambiguous.
   */
  resolve(text: string): { value: EnumValue } | { why: string } {
    // Comparisons resolve the same literal once per item: memoize, bounded.
    let r = this.memo.get(text);
    if (r === undefined) {
      r = this.compute(text);
      if (this.memo.size >= 1024) this.memo.clear();
      this.memo.set(text, r);
    }
    return r;
  }

  private readonly memo = new Map<string, { value: EnumValue } | { why: string }>();

  private compute(text: string): { value: EnumValue } | { why: string } {
    const s = text.trim();
    if (/^[+-]?\d+$/.test(s)) return { value: this.wrap(Number(s)) };
    if (this.flags && s.includes(',')) {
      let acc = 0;
      for (const p of s.split(',')) {
        const r = this.resolveName(p.trim());
        if (!('value' in r)) return r;
        if (typeof r.value.value !== 'number') return { why: this.expected() };
        acc |= r.value.value;
      }
      return { value: this.wrap(acc) };
    }
    return this.resolveName(s);
  }

  /** The "expected one of ..." reason listing the entry names. */
  expected(): string {
    return 'expected one of ' + this.values.map((e) => e.name).join(', ');
  }

  private resolveName(s: string): { value: EnumValue } | { why: string } {
    const exact = (t: string): EnumValue[] | undefined => this.step(t, false, false);
    const anyCase = (t: string): EnumValue[] | undefined => this.step(t, true, false);
    const unprefixed = (t: string): EnumValue[] | undefined => this.step(t, true, true);
    let found = exact(s);
    if (found === undefined) {
      const q = s.lastIndexOf('::');
      const cut = q >= 0 ? q + 2 : s.lastIndexOf('.') + 1;
      if (cut > 0 && cut < s.length) {
        const part = s.slice(cut);
        found = exact(part) ?? anyCase(part) ?? unprefixed(part);
      }
    }
    found ??= anyCase(s) ?? unprefixed(s);
    if (found === undefined) return { why: this.expected() };
    // Distinct entries: names sharing one value are one entry.
    if (new Set(found.map((e) => e.value)).size > 1) {
      return { why: `"${s}" is ambiguous for ${this.name}: ${found.map((e) => e.name).join(', ')}` };
    }
    return { value: this.wrap(found[0]!.value) }; // the defined instance, also for an entry sharing another's value
  }

  /** The entries one resolution step matches (aliases take part except in the entry-prefix step), or undefined. */
  private step(s: string, ignoreCase: boolean, prefix: boolean): EnumValue[] | undefined {
    const up = s.toUpperCase();
    const eq = (n: string | undefined): boolean => n !== undefined && (ignoreCase ? n.toUpperCase() === up : n === s);
    const out: EnumValue[] = [];
    const add = (ev: EnumValue): void => {
      if (!out.includes(ev)) out.push(ev);
    };
    for (const ev of this.values) if (eq(prefix ? withoutEntryPrefix(ev.name) : ev.name)) add(ev);
    if (!prefix) for (const [alias, ev] of this.aliases) if (eq(alias)) add(ev);
    return out.length > 0 ? out : undefined;
  }

  private formatUndefined(raw: number): string {
    if (!this.flags || !Number.isInteger(raw) || raw <= 0) return String(raw);
    let rest = raw;
    const names: string[] = [];
    const numeric = this.values.filter((v): v is EnumValue & { value: number } => typeof v.value === 'number' && v.value !== 0);
    for (const v of [...numeric].sort((a, b) => b.value - a.value)) {
      if ((rest & v.value) === v.value) {
        names.unshift(v.name);
        rest &= ~v.value;
      }
    }
    return rest === 0 ? names.join(', ') : String(raw);
  }
}

// ------------------------------------------------------------------ record maps

/** A plain object exposed as a map (member declared as `t.map(...)` whose value is a plain object). */
/** A string-keyed map view (plain-object records, adapter virtual maps): one shape for the map paths. */
export abstract class StringMap {
  abstract readonly size: number;
  /** True when entries cannot be assigned (writes → readonly-member). */
  get readOnly(): boolean {
    return false;
  }
  abstract has(key: string): boolean;
  abstract get(key: string): unknown;
  abstract set(key: string, value: unknown): void;
  abstract delete(key: string): void;
  abstract entries(): IterableIterator<[string, unknown]>;
  /** The keys only (no value reads). */
  abstract keys(): Iterable<string>;
  /** Rejection reason for assigning `value` to `key` (validation before any write), or undefined. */
  validate(_key: string, _value: unknown): string | undefined {
    return undefined;
  }
}

/** A plain object exposed as a map (member declared as `t.map(...)` whose value is a plain object). */
export class RecordMap extends StringMap {
  constructor(readonly target: Record<string, unknown>) {
    super();
  }
  get size(): number {
    return Object.keys(this.target).length;
  }
  has(key: string): boolean {
    return Object.prototype.hasOwnProperty.call(this.target, key);
  }
  get(key: string): unknown {
    return this.has(key) ? this.target[key] : undefined;
  }
  set(key: string, value: unknown): void {
    Object.defineProperty(this.target, key, { value, writable: true, enumerable: true, configurable: true });
  }
  delete(key: string): void {
    delete this.target[key];
  }
  *entries(): IterableIterator<[string, unknown]> {
    for (const k of Object.keys(this.target)) yield [k, this.target[k]];
  }
  keys(): Iterable<string> {
    return Object.keys(this.target);
  }
}

/** Adapter definition of a virtual map member (spec §13 "Virtual maps"). */
export interface VirtualMapSpec<T extends object, V = unknown> {
  /** The owner's keys, in order. */
  keys(owner: T): Iterable<string>;
  /** Optional direct key test (avoids scanning `keys` on every lookup). */
  has?(owner: T, key: string): boolean;
  get(owner: T, key: string): V;
  /** Per-key assignment; absent = read-only map. May accept keys beyond `keys` (entries are never removed). */
  set?(owner: T, key: string, value: V): void;
  /** Runs while a mutation is planned (before any write); returns a rejection reason. */
  validate?(owner: T, key: string, value: V): string | undefined;
  /** Mixed-type maps: the declared type of one entry; written values are coerced to it before validation. */
  entryType?(owner: T, key: string): TypeRef | undefined;
}

/**
 * A map whose entries are defined by an adapter over its owner (e.g. a material's uniforms). Reads and
 * writes use the ordinary map paths, so coercion, validation, dry-run and rollback apply.
 */
export class VirtualMap extends StringMap {
  constructor(
    private readonly owner: object,
    private readonly spec: VirtualMapSpec<object>,
  ) {
    super();
  }
  override get readOnly(): boolean {
    return this.spec.set === undefined;
  }
  get size(): number {
    let n = 0;
    for (const _ of this.spec.keys(this.owner)) n++;
    return n;
  }
  has(key: string): boolean {
    if (this.spec.has !== undefined) return this.spec.has(this.owner, key);
    for (const k of this.spec.keys(this.owner)) if (k === key) return true;
    return false;
  }
  keys(): Iterable<string> {
    return this.spec.keys(this.owner);
  }
  get(key: string): unknown {
    return this.has(key) ? this.spec.get(this.owner, key) : undefined;
  }
  set(key: string, value: unknown): void {
    if (this.spec.set === undefined) throw new TypeError('read-only map');
    this.spec.set(this.owner, key, value);
  }
  delete(_key: string): void {
    throw new TypeError('entries of a virtual map cannot be removed');
  }
  *entries(): IterableIterator<[string, unknown]> {
    for (const k of [...this.spec.keys(this.owner)]) yield [k, this.spec.get(this.owner, k)];
  }
  override validate(key: string, value: unknown): string | undefined {
    return this.spec.validate?.(this.owner, key, value);
  }
  /** Declared type of one entry (mixed-type maps), or undefined for the map's value type. */
  entryType(key: string): TypeRef | undefined {
    return this.spec.entryType?.(this.owner, key);
  }
}

const recordViews = new WeakMap<object, RecordMap>();

function recordView(o: Record<string, unknown>): RecordMap {
  let v = recordViews.get(o);
  if (v === undefined) {
    v = new RecordMap(o);
    recordViews.set(o, v);
  }
  return v;
}

export function isPlainObject(v: unknown): v is Record<string, unknown> {
  if (typeof v !== 'object' || v === null) return false;
  const p = Object.getPrototypeOf(v) as object | null;
  return p === null || p === Object.prototype;
}

// ------------------------------------------------------------------ descriptors (registration input)

export interface MemberSpec<T extends object = object> {
  name: string;
  /** Declared type; default `any`. */
  type?: TypeRef;
  /** Non-owning reference (parent link, target, shared asset): not followed by generic `recurse`. */
  reference?: boolean;
  /** Readable but never writable through `set`. */
  readonly?: boolean;
  description?: string;
  /** Custom getter (default: `target[name]`). */
  get?(target: T): unknown;
  /** Custom setter (default: `target[name] = value`; absent for getter-only accessors). */
  set?(target: T, value: unknown): void;
  /** Validation of a coerced value before any write; returns a rejection reason (→ coercion-failed). */
  validate?(target: T, value: unknown): string | undefined;
}

export interface ParamSpec {
  name: string;
  type?: TypeRef;
  optional?: boolean;
  default?: unknown;
}

export interface MethodSpec<T extends object = object> {
  name: string;
  params?: readonly ParamSpec[];
  returns?: TypeRef;
  description?: string;
  /** Custom invocation (default: `target[name](...args)`). */
  invoke?(target: T, args: unknown[]): unknown;
  /**
   * Optional check of a planned call (target, coerced arguments): the rejection reason, or null / undefined. The `call`
   * stage runs it for every target before the first call, on dry runs too (spec §8); a rejection is `invalid-value`.
   */
  validate?(target: T, args: unknown[]): string | null | undefined;
}

export interface TypeSpec<T extends object = object> {
  /** Short type name used by `$type`, `$ref`, `is` (default: the constructor name). */
  name?: string;
  /** `struct` = value-like type without identity (encoded as its members). Default: base kind or `object`. */
  kind?: 'object' | 'struct';
  /** Base class; default: nearest registered ancestor in the prototype chain. `null` = none. */
  base?: Ctor | null;
  /** Own members in declaration order (base members come first automatically). */
  members: readonly (MemberSpec<T> | string)[];
  /** Identity member backing `$ref` (default: `Id` with type int/number/string/any). `null` = none. */
  id?: string | null;
  /** Display member used in summaries (default: `Name` of type string). `null` = none. */
  display?: string | null;
  /** Methods invocable with the `call` stage (when mutations are allowed). */
  methods?: readonly MethodSpec<T>[];
  /** Extra type names matched by `x is Name` (interfaces). */
  interfaces?: readonly string[];
  description?: string;
}

// ------------------------------------------------------------------ runtime metadata

export type TypeKind = 'scalar' | 'enum' | 'object' | 'struct' | 'list' | 'map';

export interface MemberInit {
  readonly name: string;
  readonly type: TypeRef;
  /** Reads the member (enum values wrapped, plain-object maps exposed as {@link RecordMap}). */
  readonly get: (target: object) => unknown;
  /** Writes the member (enum values unwrapped); undefined when not writable. */
  readonly set?: ((target: object, value: unknown) => void) | undefined;
  readonly canWrite: boolean;
  /** False for non-owning references: not followed by generic recurse. */
  readonly owning: boolean;
  readonly order: number;
  readonly description?: string | undefined;
  /** Read only when a path names it (spec §13). */
  readonly explicitOnly?: boolean;
  /** The getter returns a copy: writes into the value are assigned back through the setter (spec §13). */
  readonly copyOnRead?: boolean;
  /** Validation of a coerced value while a mutation is planned; returns a rejection reason. */
  readonly validate?: ((target: object, value: unknown) => string | undefined) | undefined;
}

export class MemberAccessor {
  private declaredCache: 'scalar' | 'struct' | 'other' | undefined;

  constructor(
    private readonly init: MemberInit,
    private readonly model: TypeModel | undefined,
  ) {}

  get name(): string {
    return this.init.name;
  }
  get type(): TypeRef {
    return this.init.type;
  }
  get get(): (target: object) => unknown {
    return this.init.get;
  }
  get set(): ((target: object, value: unknown) => void) | undefined {
    return this.init.set;
  }
  get canWrite(): boolean {
    return this.init.canWrite;
  }
  get owning(): boolean {
    return this.init.owning;
  }
  get order(): number {
    return this.init.order;
  }
  get description(): string | undefined {
    return this.init.description;
  }
  get copyOnRead(): boolean {
    return this.init.copyOnRead ?? false;
  }
  get explicitOnly(): boolean {
    return this.init.explicitOnly ?? false;
  }
  get validate(): ((target: object, value: unknown) => string | undefined) | undefined {
    return this.init.validate;
  }

  /** A copy with some properties changed (identity members, inheritance, adapters). */
  with(changes: Partial<Pick<MemberInit, 'canWrite' | 'order' | 'owning' | 'explicitOnly' | 'description' | 'copyOnRead'>>): MemberAccessor {
    return new MemberAccessor({ ...this.init, ...changes }, this.model);
  }

  /** Declared type is a scalar/enum or a struct: generic recursion never follows it. */
  get declaredValueLike(): boolean {
    if (this.declaredCache === undefined) {
      const r = unwrapNullable(this.type);
      if (r instanceof EnumType || (typeof r === 'string' && r !== 'any')) this.declaredCache = 'scalar';
      else if (typeof r === 'object' && r.kind === 'class' && this.model?.infoForCtor(r.ctor)?.kind === 'struct') this.declaredCache = 'struct';
      else this.declaredCache = 'other';
    }
    return this.declaredCache !== 'other';
  }

  get declaredScalar(): boolean {
    return this.declaredValueLike && this.declaredCache === 'scalar';
  }
}

export class MethodAccessor {
  constructor(
    readonly name: string,
    readonly params: readonly ParamSpec[],
    readonly returns: TypeRef | undefined,
    readonly description: string | undefined,
    readonly invoke: (target: object, args: unknown[]) => unknown,
    /** Optional check of a planned call: the rejection reason, or null / undefined (spec §8). */
    readonly validate?: (target: object, args: unknown[]) => string | null | undefined,
  ) {}

  signature(model?: TypeModel): string {
    return `${this.name}(${this.params.map((p) => `${friendlyName(p.type ?? 'any', model)} ${p.name}`).join(', ')})`;
  }
}

type DynamicResolver = (o: unknown) => readonly MemberAccessor[];

export class TypeInfo {
  readonly typeNames: ReadonlySet<string>;
  private readonly byName = new Map<string, MemberAccessor>();
  private readonly byNameCI = new Map<string, MemberAccessor | null>();

  constructor(
    readonly kind: TypeKind,
    readonly shortName: string,
    readonly members: readonly MemberAccessor[] = [],
    options: {
      typeNames?: Iterable<string>;
      idMember?: MemberAccessor;
      displayMember?: MemberAccessor;
      callable?: readonly MethodAccessor[];
      base?: TypeInfo;
      description?: string;
      enumType?: EnumType;
      writesThrough?: boolean;
      literalSchema?: JsonValue;
      dynamic?: DynamicResolver;
      ctor?: Ctor;
      adapters?: readonly AnyAdapter[];
      rawMembers?: readonly MemberAccessor[];
      rawCallable?: readonly MethodAccessor[];
    } = {},
  ) {
    this.implicitMembers = members.some((m) => m.explicitOnly) ? members.filter((m) => !m.explicitOnly) : members;
    this.typeNames = new Set([...(options.typeNames ?? [shortName])].map((n) => n.toUpperCase()));
    this.idMember = options.idMember;
    this.displayMember = options.displayMember;
    this.callable = options.callable ?? [];
    this.base = options.base;
    this.description = options.description;
    this.enumType = options.enumType;
    this.writesThrough = options.writesThrough ?? false;
    this.literalSchema = options.literalSchema;
    this.dynamic = options.dynamic;
    this.ctor = options.ctor;
    this.adapters = options.adapters ?? [];
    this.adapterSet = adapterSet(this.adapters);
    this.rawMembers = options.rawMembers ?? members;
    this.rawCallable = options.rawCallable ?? this.callable;
    for (const m of members) {
      this.byName.set(m.name, m);
      const k = m.name.toUpperCase();
      this.byNameCI.set(k, this.byNameCI.has(k) ? null : m);
    }
  }

  readonly idMember: MemberAccessor | undefined;
  readonly displayMember: MemberAccessor | undefined;
  readonly callable: readonly MethodAccessor[];
  readonly base: TypeInfo | undefined;
  readonly description: string | undefined;
  readonly enumType: EnumType | undefined;
  /** A struct whose copies are handles to shared state: no write-back, may be a mutation target (spec §13). */
  readonly writesThrough: boolean;
  /** JSON Schema of the literals adapters accept for the type (`describe` → `literal`). */
  readonly literalSchema: JsonValue | undefined;
  /** Set for undeclared types: members are discovered per instance. */
  readonly dynamic: DynamicResolver | undefined;
  readonly ctor: Ctor | undefined;
  /** Applicable type adapters, most specific first (spec §13). */
  readonly adapters: readonly AnyAdapter[];
  /** The same adapters split per capability (hot paths loop only over implementers). */
  readonly adapterSet: AdapterSet;
  /** Declared members before adapters shaped them (what subclasses inherit). */
  readonly rawMembers: readonly MemberAccessor[];
  /** Declared callable methods before adapters added or hid some (what subclasses inherit). */
  readonly rawCallable: readonly MethodAccessor[];
  /** Members read implicitly (all but explicit-only ones), for declared types. */
  readonly implicitMembers: readonly MemberAccessor[];

  get isList(): boolean {
    return this.kind === 'list';
  }
  get isMap(): boolean {
    return this.kind === 'map';
  }
  get isObject(): boolean {
    return this.kind === 'object';
  }

  /** Members of a concrete value (static for declared types). */
  membersOf(o: unknown): readonly MemberAccessor[] {
    return this.dynamic !== undefined ? this.dynamic(o) : this.members;
  }

  /**
   * Members that implicit traversals may read (inline/struct encoding, generic recurse, ref search,
   * distinct keys): never explicit-only ones (spec §13).
   */
  implicitMembersOf(o: unknown): readonly MemberAccessor[] {
    return this.dynamic !== undefined ? this.dynamic(o) : this.implicitMembers;
  }

  /** Exact ordinal match, then a unique case-insensitive match (spec §3). */
  find(name: string, o?: unknown): MemberAccessor | undefined {
    if (this.dynamic !== undefined) return findIn(this.dynamic(o), name);
    const m = this.byName.get(name);
    if (m !== undefined) return m;
    return this.byNameCI.get(name.toUpperCase()) ?? undefined;
  }

  idMemberOf(o: unknown): MemberAccessor | undefined {
    if (this.dynamic === undefined) return this.idMember;
    const ms = this.dynamic(o);
    return ms.find((m) => m.name === 'Id') ?? ms.find((m) => m.name === 'id');
  }

  displayMemberOf(o: unknown): MemberAccessor | undefined {
    if (this.dynamic === undefined) return this.displayMember;
    const ms = this.dynamic(o);
    const cand = ms.find((m) => m.name === 'Name') ?? ms.find((m) => m.name === 'name');
    if (cand === undefined) return undefined;
    const v = safeGet(cand, o);
    return v === null || typeof v === 'string' ? cand : undefined;
  }

  isA(typeName: string): boolean {
    return this.typeNames.has(typeName.toUpperCase());
  }
}

function findIn(members: readonly MemberAccessor[], name: string): MemberAccessor | undefined {
  let ci: MemberAccessor | undefined;
  let ambiguous = false;
  const up = name.toUpperCase();
  for (const m of members) {
    if (m.name === name) return m;
    if (m.name.toUpperCase() === up) {
      if (ci !== undefined) ambiguous = true;
      ci = m;
    }
  }
  return ambiguous ? undefined : ci;
}

function safeGet(m: MemberAccessor, o: unknown): unknown {
  try {
    return typeof o === 'object' && o !== null ? m.get(o) : undefined;
  } catch {
    return undefined;
  }
}

// ------------------------------------------------------------------ the model

const ID_TYPES: ReadonlySet<TypeRef> = new Set<TypeRef>(['int', 'number', 'string', 'any']);

const scalar = (name: string): TypeInfo => new TypeInfo('scalar', name);
const SCALARS = {
  string: scalar('String'),
  number: scalar('Number'),
  boolean: scalar('Boolean'),
  bigint: scalar('BigInt'),
  function: scalar('Function'),
  symbol: scalar('Symbol'),
  null: scalar('Null'),
} as const;

/**
 * Registry of type descriptors plus the per-class metadata cache. One model can be shared by many
 * engines; register every class before the first query that touches it.
 */
export class TypeModel {
  private readonly specs = new Map<Ctor, TypeSpec>();
  /** Set on forks: descriptors registered on the parent later stay visible. */
  private parent: TypeModel | undefined;
  private parentVersion = 0;
  private ownVersion = 0;
  private readonly infos = new Map<object, TypeInfo>();
  private tags = new WeakMap<object, Ctor>();
  private readonly enumInfos = new Map<EnumType, TypeInfo>();
  private readonly dynamicAccessors = new Map<string, MemberAccessor>();
  private readonly listInfos = new Map<string, TypeInfo>();
  private adapterRegistry = new AdapterRegistry(INTERNAL_CLASSES);
  /** Builtin infos (Map, Record, plain Object, Date), built lazily because they carry adapters. */
  private readonly builtins = new Map<string, TypeInfo>();
  private readonly adapterSets = new Map<Ctor, AdapterSet>();
  private readonly subtypeCache = new Map<Ctor, Map<string, readonly Ctor[]>>();
  private excluded: ((type: TypeRef) => boolean) | undefined;
  private readonly entryInfo: TypeInfo;
  private readonly rowInfo = new TypeInfo('object', 'RowValue');

  constructor() {
    const ro = (name: 'Key' | 'Value', i: number): MemberAccessor =>
      new MemberAccessor({ name, type: 'any', get: (e) => (e as MapEntry)[name], canWrite: false, owning: true, order: i }, this);
    this.entryInfo = new TypeInfo('object', 'MapEntry', [ro('Key', 0), ro('Value', 1)]);
    this.resetCaches();
  }

  private resetCaches(): void {
    this.infos.clear();
    this.listInfos.clear();
    this.builtins.clear();
    this.adapterSets.clear();
    this.subtypeCache.clear();
  }

  private builtin(key: 'map' | 'object-literal' | 'record' | 'virtual-map' | 'plain' | 'date'): TypeInfo {
    let info = this.builtins.get(key);
    if (info === undefined) {
      switch (key) {
        case 'map':
          info = new TypeInfo('map', 'Map', [], { ctor: Map, adapters: this.adapterRegistry.for(Map) });
          break;
        case 'record':
          info = new TypeInfo('map', 'Record', [], { adapters: this.adapterRegistry.for(Object) });
          break;
        case 'virtual-map':
          info = new TypeInfo('map', 'VirtualMap');
          break;
        case 'object-literal':
          info = new TypeInfo('map', 'ObjectValue');
          break;
        case 'plain': {
          const adapters = this.adapterRegistry.for(Object);
          rejectMemberAdapters(adapters, 'Object');
          info = new TypeInfo('object', 'Object', [], { dynamic: (o) => this.ownMembers(o, []), ctor: Object, adapters });
          break;
        }
        case 'date':
          info = new TypeInfo('scalar', 'Date', [], { ctor: Date, adapters: this.adapterRegistry.for(Date) });
          break;
      }
      this.builtins.set(key, info);
    }
    return info;
  }

  /** Whether any type adapter is registered. */
  get hasAdapters(): boolean {
    return this.adapterRegistry.size > 0;
  }

  /** Whether any registered adapter implements `capability` (gates equality, ordering, encoding, coercion). */
  hasCapability(capability: Capability): boolean {
    return this.adapterRegistry.has(capability);
  }

  /**
   * Registers a type adapter (spec §13). Clears cached metadata, so register adapters before queries
   * run. Prefer `RqlEngine.addAdapter`, which keeps models shared with other engines unchanged.
   */
  addAdapter<T extends object>(adapter: ClassAdapter<T>): this;
  addAdapter(adapter: InterfaceAdapter): this;
  addAdapter<T extends object>(adapter: TypeAdapter<T>): this;
  addAdapter(adapter: TypeAdapter): this {
    this.adapterRegistry.add(adapter);
    this.resetCaches();
    this.ownVersion++;
    return this;
  }

  /** Applicable adapters of a class, most specific first. */
  adaptersFor(ctor: Ctor): readonly AnyAdapter[] {
    return this.adapterRegistry.for(ctor);
  }

  /** The same adapters split per capability (cached per class). */
  adapterSetFor(ctor: Ctor): AdapterSet {
    let set = this.adapterSets.get(ctor);
    if (set === undefined) {
      set = adapterSet(this.adapterRegistry.for(ctor));
      this.adapterSets.set(ctor, set);
    }
    return set;
  }

  /** The ordered adapters applying to a class, most specific first, with rank and priority. */
  explain(ctor: Ctor): string[] {
    return this.adapterRegistry.explain(ctor);
  }

  /** Every registered type adapter in registration order (spec §13). */
  get adapters(): readonly TypeAdapter[] {
    return this.adapterRegistry.all;
  }

  /**
   * A child model for adapters: it reads this model's descriptors and tags live (later registrations
   * are visible), copies its current adapters, and keeps adapters added to it local.
   */
  fork(): TypeModel {
    const m = new TypeModel();
    m.parent = this;
    m.parentVersion = this.version;
    m.tags = this.tags;
    m.excluded = this.excluded;
    this.adapterRegistry.copyTo(m.adapterRegistry);
    m.resetCaches();
    return m;
  }

  /** Incremented on every registration here or on a parent (lets derived caches expire). */
  get version(): number {
    return this.ownVersion + (this.parent?.version ?? 0);
  }

  /** Registers a class descriptor. */
  register<T extends object>(ctor: Ctor<T>, spec: TypeSpec<T>): this {
    this.specs.set(ctor, spec as unknown as TypeSpec);
    this.resetCaches();
    this.ownVersion++;
    return this;
  }

  private specOf(ctor: Ctor): TypeSpec | undefined {
    return this.specs.get(ctor) ?? this.parent?.specOf(ctor);
  }

  /** Drops cached metadata when the parent registered descriptors since the last look. */
  private sync(): void {
    if (this.parent !== undefined && this.parent.version !== this.parentVersion) {
      this.parentVersion = this.parent.version;
      this.resetCaches();
    }
  }

  /** Treats `obj` (e.g. a plain object literal) as an instance of the registered class `ctor`. */
  tag<T extends object>(obj: object, ctor: Ctor<T>): this {
    this.tags.set(obj, ctor);
    return this;
  }

  isRegistered(ctor: Ctor): boolean {
    return this.specOf(ctor) !== undefined;
  }

  get registeredTypes(): readonly Ctor[] {
    return [...new Set([...(this.parent?.registeredTypes ?? []), ...this.specs.keys()])];
  }

  enumInfo(e: EnumType): TypeInfo {
    let info = this.enumInfos.get(e);
    if (info === undefined) {
      info = new TypeInfo('enum', e.name, [], { enumType: e, description: e.description });
      this.enumInfos.set(e, info);
    }
    return info;
  }

  /** Metadata of any runtime value. */
  infoOf(v: unknown): TypeInfo {
    switch (typeof v) {
      case 'string':
        return SCALARS.string;
      case 'number':
        return SCALARS.number;
      case 'boolean':
        return SCALARS.boolean;
      case 'bigint':
        return SCALARS.bigint;
      case 'function':
        return SCALARS.function;
      case 'symbol':
        return SCALARS.symbol;
      case 'undefined':
        return SCALARS.null;
      default:
        break;
    }
    const o = v as object | null;
    if (o === null) return SCALARS.null;
    this.sync();
    if (Array.isArray(o)) return this.listInfo('Array', Array);
    if (o instanceof EnumValue) return this.enumInfo(o.type);
    if (o instanceof ObjectValue) return this.builtin('object-literal');
    if (o instanceof Map) return this.builtin('map');
    if (o instanceof RecordMap) return this.builtin('record');
    if (o instanceof VirtualMap) return this.builtin('virtual-map');
    if (o instanceof Set) return this.listInfo('Set', Set);
    if (o instanceof MapEntry) return this.entryInfo;
    if (o instanceof RowValue) return this.rowInfo;
    if (o instanceof Date) return this.builtin('date');
    if (ArrayBuffer.isView(o) && !(o instanceof DataView)) return this.listInfo(o.constructor.name, o.constructor as Ctor);
    const tagged = this.tags.get(o);
    if (tagged !== undefined) return this.infoForCtor(tagged) ?? this.builtin('plain');
    const proto = Object.getPrototypeOf(o) as object | null;
    if (proto === null || proto === Object.prototype) return this.builtin('plain');
    const cached = this.infos.get(proto);
    if (cached !== undefined) return cached;
    const info = this.buildFor(proto, o);
    this.infos.set(proto, info);
    return info;
  }

  /** Metadata of a registered class (or of a subclass of one); undefined when unknown. */
  infoForCtor(ctor: Ctor): TypeInfo | undefined {
    this.sync();
    const proto = ctor.prototype as object;
    const cached = this.infos.get(proto);
    if (cached !== undefined) return cached;
    if (!this.isRegistered(ctor) && this.registeredAncestor(ctor) === undefined) return undefined;
    const info = this.buildFor(proto, undefined);
    this.infos.set(proto, info);
    return info;
  }

  private listInfo(name: string, ctor?: Ctor): TypeInfo {
    let info = this.listInfos.get(name);
    if (info === undefined) {
      info = new TypeInfo('list', name, [], ctor !== undefined ? { ctor, adapters: this.adapterRegistry.for(ctor) } : {});
      this.listInfos.set(name, info);
    }
    return info;
  }

  private registeredAncestor(ctor: Ctor): Ctor | undefined {
    for (let c = Object.getPrototypeOf(ctor) as Ctor | null; c !== null && c !== Function.prototype; c = Object.getPrototypeOf(c) as Ctor | null) {
      if (this.isRegistered(c)) return c;
    }
    return undefined;
  }

  private buildFor(proto: object, sample: object | undefined): TypeInfo {
    const ctor = (Object.prototype.hasOwnProperty.call(proto, 'constructor') ? (proto as { constructor: unknown }).constructor : undefined) as Ctor | undefined;
    if (ctor !== undefined && typeof ctor === 'function') {
      const spec = this.specOf(ctor);
      if (spec !== undefined) return this.buildDeclared(ctor, spec);
      const anc = this.registeredAncestor(ctor);
      if (anc !== undefined) {
        // Unregistered subclass of a registered class: inherits the declared members.
        const b = this.infoForCtor(anc)!;
        const name = ctor.name || b.shortName;
        const adapters = this.adapterRegistry.for(ctor);
        // Same table as a registered subclass would get: the base's identity decision, read-only id, adapters.
        const t = this.finishMembers(name, b.kind, adapters, [...b.rawMembers], [...b.rawCallable], b.idMember?.name ?? null, undefined, b.displayMember?.name ?? null, undefined);
        return new TypeInfo(b.kind, name, t.members, {
          typeNames: [name, ...b.typeNames], idMember: t.idMember, displayMember: t.displayMember, callable: t.methods,
          base: b, description: describeWith(adapters, b.description), ctor, adapters, rawMembers: b.rawMembers, rawCallable: b.rawCallable,
          writesThrough: t.writesThrough, ...literalOf(adapters),
        });
      }
    }
    if (sample !== undefined && typeof (sample as { [Symbol.iterator]?: unknown })[Symbol.iterator] === 'function') {
      return this.listInfo(ctor?.name || 'Iterable', ctor);
    }
    return this.buildDynamic(proto, ctor);
  }

  private buildDeclared(ctor: Ctor, spec: TypeSpec): TypeInfo {
    const baseCtor = spec.base !== undefined ? spec.base ?? undefined : this.registeredAncestor(ctor);
    const base = baseCtor !== undefined ? this.infoForCtor(baseCtor) : undefined;
    const name = spec.name ?? ctor.name;
    const members: MemberAccessor[] = base !== undefined ? [...base.rawMembers] : [];
    for (const ms of spec.members) {
      const m = this.makeMember(ctor, typeof ms === 'string' ? { name: ms } : ms, members.length);
      const i = members.findIndex((x) => x.name === m.name);
      // Overrides keep the base declaration order but use the most derived accessor.
      if (i >= 0) members[i] = m.with({ order: members[i]!.order });
      else members.push(m);
    }

    const kind = spec.kind ?? base?.kind ?? 'object';
    const rawMembers = [...members];
    const rawCallable: MethodAccessor[] = [...(base?.rawCallable ?? [])];
    for (const ms of spec.methods ?? []) putMethod(rawCallable, this.makeMethod(ms));
    const adapters = this.adapterRegistry.for(ctor);
    const t = this.finishMembers(name, kind, adapters, members, [...rawCallable], spec.id, base?.idMember, spec.display, base?.displayMember);

    return new TypeInfo(kind, name, t.members, {
      typeNames: [name, ...(base?.typeNames ?? []), ...(spec.interfaces ?? [])],
      idMember: t.idMember, displayMember: t.displayMember, callable: t.methods, base,
      description: describeWith(adapters, spec.description), ctor, adapters, rawMembers, rawCallable,
      writesThrough: t.writesThrough, ...literalOf(adapters),
    });
  }

  /**
   * Shapes a declared member table: adapters (least to most specific), then the identity and display
   * members, chosen among implicit members only (an explicit-only member is never read by summaries,
   * `$ref` or ref search), with the identity made read-only.
   */
  private finishMembers(
    name: string,
    kind: TypeKind,
    adapters: readonly AnyAdapter[],
    members: MemberAccessor[],
    methods: MethodAccessor[],
    idSpec: string | null | undefined,
    inheritedId: MemberAccessor | undefined,
    displaySpec: string | null | undefined,
    inheritedDisplay: MemberAccessor | undefined,
  ): { members: MemberAccessor[]; methods: MethodAccessor[]; idMember: MemberAccessor | undefined; displayMember: MemberAccessor | undefined; writesThrough: boolean } {
    const declared = new Set(members.map((m) => m.name));
    if (this.excluded !== undefined) {
      retainInPlace(members, (m) => !this.isExcludedType(m.type));
    }
    const table = this.applyAdapters(adapters, name, kind, members, methods);
    // Adapters may choose the identity / display members by name (most specific adapter wins).
    if (table?.identityName !== undefined) idSpec = table.identityName;
    if (table?.displayName !== undefined) displaySpec = table.displayName;
    // Callable methods: by name (ordinal), then arity.
    methods.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : a.params.length - b.params.length));
    const pick = (explicit: string | null | undefined, inherited: MemberAccessor | undefined, dflt: (m: MemberAccessor) => boolean): MemberAccessor | undefined => {
      if (explicit === null) return undefined;
      const implicit = (m: MemberAccessor): boolean => !m.explicitOnly;
      if (explicit !== undefined) {
        if (!declared.has(explicit) && !members.some((x) => x.name === explicit)) throw new Error(`type '${name}': member '${explicit}' is not declared`);
        return members.find((x) => x.name === explicit && implicit(x)); // undefined when hidden or explicit-only
      }
      if (inherited !== undefined) return members.find((x) => x.name === inherited.name && implicit(x));
      return members.find((x) => implicit(x) && dflt(x));
    };
    let idMember = pick(idSpec, inheritedId, (m) => m.name === 'Id' && ID_TYPES.has(unwrapNullable(m.type)));
    if (idMember !== undefined && idMember.canWrite) {
      // Stable identities never change through RQL (refs would silently break).
      const ro = idMember.with({ canWrite: false });
      members[members.indexOf(idMember)] = ro;
      idMember = ro;
    }
    const displayMember = pick(displaySpec, inheritedDisplay, (m) => m.name === 'Name' && unwrapNullable(m.type) === 'string');
    return { members, methods, idMember, displayMember, writesThrough: table?.isWritesThrough ?? false };
  }

  /** Runs the adapters' member configuration (least specific first) over `members`, in place. */
  private applyAdapters(adapters: readonly AnyAdapter[], typeName: string, kind: TypeKind, members: MemberAccessor[], methods: MethodAccessor[]): MemberTableImpl | undefined {
    if (!adapters.some((a) => a.members !== undefined)) return undefined;
    const table = new MemberTableImpl(typeName, kind, members, methods, this);
    for (let i = adapters.length - 1; i >= 0; i--) adapters[i]!.members?.(table);
    return table;
  }

  /**
   * Engine-wide member type exclusion (spec §13): members whose declared type, element / key / value type
   * or class matches are dropped from every declared type.
   */
  excludeTypes(excluded: (type: TypeRef) => boolean): this {
    const prev = this.excluded;
    this.excluded = prev === undefined ? excluded : (t) => prev(t) || excluded(t);
    this.resetCaches();
    this.ownVersion++;
    return this;
  }

  private isExcludedType(r: TypeRef): boolean {
    if (this.excluded === undefined) return false;
    if (this.excluded(r)) return true;
    if (typeof r !== 'object' || r instanceof EnumType) return false;
    switch (r.kind) {
      case 'nullable':
      case 'list':
        return this.isExcludedType(r.of);
      case 'map':
        return this.isExcludedType(r.key) || this.isExcludedType(r.of);
      default:
        return false;
    }
  }

  /** The first adapter liveness verdict for `v` (spec §13), or undefined when no adapter answers. */
  adapterAlive(v: object): boolean | undefined {
    if (!this.adapterRegistry.has('isAlive')) return undefined;
    return firstAnswer(this.infoOf(v).adapterSet.isAlive, (a) => a.isAlive!(v));
  }

  /** Concrete registered classes assignable to `ctor` whose short or constructor name is `name` (cached). */
  subtypesNamed(ctor: Ctor, name: string): readonly Ctor[] {
    return this.subtypeIndex(ctor).get(name) ?? [];
  }

  /** The names {@link subtypesNamed} accepts for `ctor`: what a misspelt "$type" is compared with (spec §4.5, §10). */
  subtypeNames(ctor: Ctor): readonly string[] {
    return [...this.subtypeIndex(ctor).keys()];
  }

  private subtypeIndex(ctor: Ctor): Map<string, readonly Ctor[]> {
    this.sync();
    let byName = this.subtypeCache.get(ctor);
    if (byName === undefined) {
      byName = new Map();
      for (const c of this.registeredTypes) {
        if (c !== ctor && !(c.prototype instanceof ctor)) continue;
        for (const n of new Set([c.name, this.infoForCtor(c)?.shortName ?? c.name])) byName.set(n, [...(byName.get(n) ?? []), c]);
      }
      this.subtypeCache.set(ctor, byName);
    }
    return byName;
  }

  private makeMember(ctor: Ctor, spec: MemberSpec, order: number): MemberAccessor {
    const key = spec.name;
    const type = spec.type ?? 'any';
    const rawGet = spec.get !== undefined ? spec.get.bind(spec) : (o: object): unknown => (o as Record<string, unknown>)[key];
    let rawSet: ((o: object, v: unknown) => void) | undefined;
    if (spec.set !== undefined) rawSet = spec.set.bind(spec);
    else if (spec.get === undefined && !isGetterOnly(ctor.prototype as object, key)) {
      rawSet = (o, v) => {
        (o as Record<string, unknown>)[key] = v;
      };
    }
    const declared = unwrapNullable(type);
    let get: (o: object) => unknown;
    let set = rawSet;
    if (declared instanceof EnumType) {
      get = (o) => {
        const raw = rawGet(o);
        if (raw === undefined || raw === null) return null;
        if (raw instanceof EnumValue) return raw;
        return typeof raw === 'number' || typeof raw === 'string' ? declared.wrap(raw) : raw;
      };
      if (rawSet !== undefined) {
        const s = rawSet;
        set = (o, v) => s(o, v instanceof EnumValue ? v.value : v);
      }
    } else if (typeof declared === 'object' && declared.kind === 'map') {
      get = (o) => {
        const raw = rawGet(o);
        if (raw === undefined) return null;
        return isPlainObject(raw) ? recordView(raw) : raw;
      };
      if (rawSet !== undefined) {
        const s = rawSet;
        set = (o, v) => {
          // Keep plain-object storage when the host uses it.
          if (v instanceof Map && isPlainObject(rawGet(o))) {
            const rec: Record<string, unknown> = {};
            for (const [k, x] of v) recordView(rec).set(String(k), x);
            s(o, rec);
          } else s(o, v);
        };
      }
    } else {
      get = (o) => {
        const raw = rawGet(o);
        return raw === undefined ? null : raw;
      };
    }
    const canWrite = !spec.readonly && set !== undefined;
    const validate = spec.validate !== undefined ? spec.validate.bind(spec) : undefined;
    return new MemberAccessor({ name: key, type, get, set, canWrite, owning: !spec.reference, order, description: spec.description, validate }, this);
  }

  private makeMethod(spec: MethodSpec): MethodAccessor {
    const name = spec.name;
    const invoke =
      spec.invoke !== undefined
        ? spec.invoke.bind(spec)
        : (o: object, args: unknown[]): unknown => {
            const fn = (o as Record<string, unknown>)[name];
            if (typeof fn !== 'function') throw new TypeError(`${name} is not a function`);
            return (fn as (...a: unknown[]) => unknown).apply(o, args);
          };
    const validate = spec.validate !== undefined ? spec.validate.bind(spec) : undefined;
    return new MethodAccessor(name, spec.params ?? [], spec.returns, spec.description, invoke, validate);
  }

  // ---------------------------------------------------------------- dynamic fallback

  private buildDynamic(proto: object, ctor: Ctor | undefined): TypeInfo {
    const getters = this.prototypeGetters(proto);
    const names: string[] = [];
    for (let p: object | null = proto; p !== null && p !== Object.prototype; p = Object.getPrototypeOf(p) as object | null) {
      const c = (p as { constructor?: { name?: string } }).constructor;
      if (Object.prototype.hasOwnProperty.call(p, 'constructor') && c?.name) names.push(c.name);
    }
    const shortName = names[0] ?? 'Object';
    // Member-shaping adapters need a declared member table; the other capabilities apply to dynamic classes too.
    const adapters = ctor !== undefined ? this.adapterRegistry.for(ctor) : [];
    rejectMemberAdapters(adapters, shortName);
    const description = describeWith(adapters, undefined);
    return new TypeInfo('object', shortName, getters, {
      typeNames: names.length > 0 ? names : ['Object'],
      dynamic: (o) => this.ownMembers(o, getters),
      adapters,
      ...(description !== undefined ? { description } : {}),
      ...(ctor !== undefined ? { ctor } : {}),
    });
  }

  /** Accessor properties along the prototype chain, base-most class first. */
  private prototypeGetters(proto: object): MemberAccessor[] {
    const levels: object[] = [];
    for (let p: object | null = proto; p !== null && p !== Object.prototype; p = Object.getPrototypeOf(p) as object | null) levels.unshift(p);
    const out: MemberAccessor[] = [];
    for (const level of levels) {
      for (const key of Object.getOwnPropertyNames(level)) {
        if (key === 'constructor' || key === '__proto__') continue;
        const d = Object.getOwnPropertyDescriptor(level, key);
        if (d?.get === undefined) continue;
        const existing = out.findIndex((m) => m.name === key);
        const canWrite = d.set !== undefined || isSetterSomewhere(proto, key);
        const m = new MemberAccessor(
          {
            name: key,
            type: 'any',
            get: (o) => normalize((o as Record<string, unknown>)[key]),
            set: canWrite ? (o, v) => { (o as Record<string, unknown>)[key] = v; } : undefined,
            canWrite,
            owning: true,
            order: existing >= 0 ? existing : out.length,
          },
          this,
        );
        if (existing >= 0) out[existing] = m;
        else out.push(m);
      }
    }
    return out;
  }

  private ownMembers(o: unknown, getters: readonly MemberAccessor[]): readonly MemberAccessor[] {
    if (typeof o !== 'object' || o === null) return getters;
    const rec = o as Record<string, unknown>;
    const out: MemberAccessor[] = [];
    for (const key of Object.keys(rec)) {
      // Inspect descriptors so listing members never runs getters.
      const d = Object.getOwnPropertyDescriptor(rec, key);
      if (d !== undefined && typeof d.value === 'function') continue;
      out.push(this.dynamicAccessor(key, out.length));
    }
    return getters.length === 0 ? out : [...out, ...getters.filter((g) => !Object.prototype.hasOwnProperty.call(rec, g.name))];
  }

  private dynamicAccessor(key: string, order: number): MemberAccessor {
    const cacheKey = key + '\u0000' + order;
    let m = this.dynamicAccessors.get(cacheKey);
    if (m === undefined) {
      m = new MemberAccessor(
        {
          name: key,
          type: 'any',
          get: (o) => normalize((o as Record<string, unknown>)[key]),
          set: (o, v) => {
            (o as Record<string, unknown>)[key] = v;
          },
          canWrite: true,
          owning: true,
          order,
        },
        this,
      );
      this.dynamicAccessors.set(cacheKey, m);
    }
    return m;
  }
}

const normalize = (v: unknown): unknown => (v === undefined ? null : v);

function isGetterOnly(proto: object, key: string): boolean {
  for (let p: object | null = proto; p !== null && p !== Object.prototype; p = Object.getPrototypeOf(p) as object | null) {
    const d = Object.getOwnPropertyDescriptor(p, key);
    if (d !== undefined) return d.get !== undefined && d.set === undefined;
  }
  return false;
}

function isSetterSomewhere(proto: object, key: string): boolean {
  for (let p: object | null = proto; p !== null && p !== Object.prototype; p = Object.getPrototypeOf(p) as object | null) {
    const d = Object.getOwnPropertyDescriptor(p, key);
    if (d?.set !== undefined) return true;
  }
  return false;
}

/** Keeps only the elements matching `keep`, in place (order preserved). */
function retainInPlace<T>(arr: T[], keep: (item: T) => boolean): void {
  let w = 0;
  for (const item of arr) if (keep(item)) arr[w++] = item;
  arr.length = w;
}

/** Adds a method, replacing one with the same name and arity. */
function putMethod(list: MethodAccessor[], m: MethodAccessor): void {
  const i = list.findIndex((x) => x.name === m.name && x.params.length === m.params.length);
  if (i >= 0) list.splice(i, 1);
  list.push(m);
}

/** Engine-internal value classes: never adapted (spec §13 applies to host types). */
const INTERNAL_CLASSES: ReadonlySet<object> = new Set<object>([RowValue, MapEntry, Missing, Unknown, ErrorValue, EnumValue, RecordMap, VirtualMap, ObjectValue]);

/**
 * Member configuration needs a declared member table (members of undeclared objects are discovered per
 * instance). An adapter with `members` resolving to an undeclared class is a host bug → internal-error.
 */
function rejectMemberAdapters(adapters: readonly AnyAdapter[], typeName: string): void {
  const a = adapters.find((x) => x.members !== undefined);
  if (a !== undefined) {
    throw new Error(`type adapter ${adapterName(a)} configures members of ${typeName}, which has no registered descriptor (register it with TypeModel.register)`);
  }
}

/** The literal schema of the most specific adapter that publishes one. */
function literalOf(adapters: readonly AnyAdapter[]): { literalSchema?: JsonValue } {
  const a = adapters.find((x) => x.literal !== undefined);
  return a !== undefined ? { literalSchema: a.literal! } : {};
}

function describeWith(adapters: readonly AnyAdapter[], fallback: string | undefined): string | undefined {
  for (const a of adapters) if (a.description !== undefined) return a.description;
  return fallback;
}

/** {@link MemberTable} over a mutable member list (spec §13 member configuration). */
class MemberTableImpl implements MemberTable<object> {
  constructor(
    readonly typeName: string,
    private readonly kind: TypeKind,
    private readonly list: MemberAccessor[],
    private readonly methodList: MethodAccessor[],
    private readonly model: TypeModel,
  ) {}

  get methods(): readonly MethodAccessor[] {
    return this.methodList;
  }

  isWritesThrough = false;
  identityName: string | undefined;
  displayName: string | undefined;

  hideWhere(hide: (m: MemberAccessor) => boolean): this {
    this.retain((m) => !hide(m));
    return this;
  }

  copyOnRead(...names: string[]): this {
    return this.change(names, (m) => m.with({ copyOnRead: true }));
  }

  writesThrough(): this {
    if (this.kind !== 'struct') throw new Error(`${this.typeName} is not a struct; writesThrough applies to handle value types`);
    this.isWritesThrough = true;
    return this;
  }

  identity(name: string): this {
    this.requireAll([name]);
    this.identityName = name;
    return this;
  }

  display(name: string): this {
    this.requireAll([name]);
    this.displayName = name;
    return this;
  }

  mixedMap(name: string, spec: VirtualMapSpec<object> & { entryType(owner: object, key: string): TypeRef }, description?: string): this {
    return this.map(name, spec, 'any', description);
  }

  method<A extends unknown[]>(name: string, params: readonly ParamSpec[], returns: TypeRef | undefined, fn: (target: object, ...args: A) => unknown,
    description?: string,
    validate?: (target: object, ...args: A) => string | null | undefined,
  ): this {
    if (name.length === 0) throw new Error('method name required');
    // Arguments arrive coerced to the declared parameter types, in parameter order.
    const check = validate !== undefined ? (o: object, args: unknown[]) => validate(o, ...(args as A)) : undefined;
    putMethod(this.methodList, new MethodAccessor(name, params, returns, description, (o, args) => fn(o, ...(args as A)), check));
    return this;
  }

  hideMethods(...names: string[]): this {
    for (const n of names) if (!this.methodList.some((m) => m.name === n)) throw new Error(`type ${this.typeName} has no callable method '${n}' to hide`);
    retainInPlace(this.methodList, (m) => !names.includes(m.name));
    return this;
  }

  map<V>(name: string, spec: VirtualMapSpec<object, V>, valueType: TypeRef = 'any', description?: string): this {
    if (spec.set !== undefined && this.kind === 'struct' && !this.isWritesThrough) throw new Error(`virtual map setters on value type ${this.typeName} would write to a copy`);
    const views = new WeakMap<object, VirtualMap>();
    const s = spec as VirtualMapSpec<object>;
    return this.add(name, {
      type: { kind: 'map', key: 'string', of: valueType },
      get: (o: object) => {
        let v = views.get(o);
        if (v === undefined) views.set(o, (v = new VirtualMap(o, s)));
        return v;
      },
      ...(description !== undefined ? { description } : {}),
    });
  }

  get members(): readonly MemberAccessor[] {
    return this.list;
  }

  has(name: string): boolean {
    return this.list.some((m) => m.name === name);
  }

  add<V>(name: string, spec: VirtualMemberSpec<object, V>): this {
    if (name.length === 0) throw new Error('virtual member name required');
    if (spec.set !== undefined && this.kind === 'struct' && !this.isWritesThrough) {
      throw new Error(`virtual setters on value type ${this.typeName} would write to a copy; expose a writable member instead (or mark the type writesThrough() if it is a handle)`);
    }
    const i = this.list.findIndex((m) => m.name === name);
    const order = i >= 0 ? this.list[i]!.order : this.list.reduce((mx, m) => Math.max(mx, m.order + 1), 0);
    const s = spec as VirtualMemberSpec<object, unknown>;
    const accessor = new MemberAccessor(
      {
        name,
        type: spec.type ?? 'any',
        get: (o) => {
          const v = s.get(o);
          return v === undefined ? null : v;
        },
        set: s.set !== undefined ? (o, v) => s.set!(o, v) : undefined,
        canWrite: s.set !== undefined,
        owning: spec.owning ?? false,
        order,
        description: spec.description,
        validate: s.validate !== undefined ? (o, v) => s.validate!(o, v) : undefined,
      },
      this.model,
    );
    if (i >= 0) this.list[i] = accessor;
    else this.list.push(accessor);
    return this;
  }

  hide(...names: string[]): this {
    this.requireAll(names);
    this.retain((m) => !names.includes(m.name));
    return this;
  }

  only(...names: string[]): this {
    this.requireAll(names);
    this.retain((m) => names.includes(m.name));
    return this;
  }

  /** A typo in hide("Pasword") must not leave Password exposed: unknown names are a host bug. */
  private requireAll(names: readonly string[]): void {
    for (const n of names) if (!this.has(n)) throw new Error(`type ${this.typeName} has no member '${n}' to configure`);
  }

  readOnly(...names: string[]): this {
    return this.change(names, (m) => m.with({ canWrite: false }));
  }

  reference(...names: string[]): this {
    return this.change(names, (m) => m.with({ owning: false }));
  }

  explicitOnly(...names: string[]): this {
    return this.change(names, (m) => m.with({ explicitOnly: true }));
  }

  describe(name: string, description: string): this {
    return this.change([name], (m) => m.with({ description }));
  }

  private retain(keep: (m: MemberAccessor) => boolean): void {
    retainInPlace(this.list, keep);
  }

  private change(names: readonly string[], f: (m: MemberAccessor) => MemberAccessor): this {
    for (const n of names) {
      const i = this.list.findIndex((m) => m.name === n);
      // A host bug: the query fails with internal-error.
      if (i < 0) throw new Error(`type ${this.typeName} has no member '${n}' to configure`);
      this.list[i] = f(this.list[i]!);
    }
    return this;
  }
}

