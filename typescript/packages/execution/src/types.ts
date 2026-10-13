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
import { MapEntry, RowValue } from './values.js';

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

export function friendlyName(r: TypeRef, model?: TypeModel): string {
  if (typeof r === 'string') return r;
  if (r instanceof EnumType) return r.name;
  switch (r.kind) {
    case 'nullable':
      return friendlyName(r.of, model) + '?';
    case 'list':
      return `list<${friendlyName(r.of, model)}>`;
    case 'map':
      return `map<${friendlyName(r.key, model)}, ${friendlyName(r.of, model)}>`;
    case 'class':
      return model?.infoForCtor(r.ctor)?.shortName ?? r.ctor.name;
  }
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
  private readonly byName = new Map<string, EnumValue>();

  constructor(
    name: string,
    values: Readonly<Record<string, number | string>> | readonly string[],
    options: { flags?: boolean; description?: string } = {},
  ) {
    this.name = name;
    this.flags = options.flags ?? false;
    this.description = options.description;
    const entries: [string, number | string][] = Array.isArray(values)
      ? (values as readonly string[]).map((n, i) => [n, i])
      : Object.entries(values as Readonly<Record<string, number | string>>).filter(([k]) => !/^-?\d+(\.\d+)?$/.test(k));
    this.values = entries.map(([n, v], i) => new EnumValue(this, v, n, typeof v === 'number' ? v : i));
    for (const ev of this.values) {
      if (!this.byRaw.has(ev.value)) this.byRaw.set(ev.value, ev);
      const key = ev.name.toUpperCase();
      if (!this.byName.has(key)) this.byName.set(key, ev);
    }
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

  /** Like .NET `Enum.Parse(type, s, ignoreCase: true)`: names, numbers, comma-separated flags. */
  parse(text: string): EnumValue | undefined {
    const s = text.trim();
    if (s.length === 0) return undefined;
    if (/^[+-]?\d+$/.test(s)) return this.wrap(Number(s));
    const parts = s.split(',').map((p) => p.trim());
    if (parts.length === 1) return this.byName.get(parts[0]!.toUpperCase());
    let acc = 0;
    for (const p of parts) {
      const ev = this.byName.get(p.toUpperCase());
      if (ev === undefined || typeof ev.value !== 'number') return undefined;
      acc |= ev.value;
    }
    return this.wrap(acc);
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
export class RecordMap {
  constructor(readonly target: Record<string, unknown>) {}
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

export class MemberAccessor {
  private declaredCache: 'scalar' | 'struct' | 'other' | undefined;

  constructor(
    readonly name: string,
    readonly type: TypeRef,
    /** Reads the member (enum values wrapped, plain-object maps exposed as {@link RecordMap}). */
    readonly get: (target: object) => unknown,
    /** Writes the member (enum values unwrapped); undefined when not writable. */
    readonly set: ((target: object, value: unknown) => void) | undefined,
    readonly canWrite: boolean,
    readonly owning: boolean,
    readonly order: number,
    readonly description: string | undefined,
    private readonly model: TypeModel | undefined,
  ) {}

  /** A copy with another write permission / order (identity members, inheritance). */
  with(changes: { canWrite?: boolean; order?: number }): MemberAccessor {
    return new MemberAccessor(
      this.name, this.type, this.get, this.set, changes.canWrite ?? this.canWrite, this.owning,
      changes.order ?? this.order, this.description, this.model,
    );
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
      dynamic?: DynamicResolver;
      ctor?: Ctor;
    } = {},
  ) {
    this.typeNames = new Set([...(options.typeNames ?? [shortName])].map((n) => n.toUpperCase()));
    this.idMember = options.idMember;
    this.displayMember = options.displayMember;
    this.callable = options.callable ?? [];
    this.base = options.base;
    this.description = options.description;
    this.enumType = options.enumType;
    this.dynamic = options.dynamic;
    this.ctor = options.ctor;
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
  /** Set for undeclared types: members are discovered per instance. */
  readonly dynamic: DynamicResolver | undefined;
  readonly ctor: Ctor | undefined;

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
  date: scalar('Date'),
  null: scalar('Null'),
} as const;

/**
 * Registry of type descriptors plus the per-class metadata cache. One model can be shared by many
 * engines; register every class before the first query that touches it.
 */
export class TypeModel {
  private readonly specs = new Map<Ctor, TypeSpec>();
  private readonly infos = new Map<object, TypeInfo>();
  private readonly tags = new WeakMap<object, Ctor>();
  private readonly enumInfos = new Map<EnumType, TypeInfo>();
  private readonly dynamicAccessors = new Map<string, MemberAccessor>();
  private readonly listInfos = new Map<string, TypeInfo>();
  private readonly mapInfo = new TypeInfo('map', 'Map');
  private readonly recordInfo = new TypeInfo('map', 'Record');
  private readonly plainInfo: TypeInfo;
  private readonly entryInfo: TypeInfo;
  private readonly rowInfo = new TypeInfo('object', 'RowValue');

  constructor() {
    this.plainInfo = new TypeInfo('object', 'Object', [], { dynamic: (o) => this.ownMembers(o, []) });
    const ro = (name: 'Key' | 'Value', i: number): MemberAccessor =>
      new MemberAccessor(name, 'any', (e) => (e as MapEntry)[name], undefined, false, true, i, undefined, this);
    this.entryInfo = new TypeInfo('object', 'MapEntry', [ro('Key', 0), ro('Value', 1)]);
  }

  /** Registers a class descriptor. */
  register<T extends object>(ctor: Ctor<T>, spec: TypeSpec<T>): this {
    this.specs.set(ctor, spec as unknown as TypeSpec);
    this.infos.clear();
    return this;
  }

  /** Treats `obj` (e.g. a plain object literal) as an instance of the registered class `ctor`. */
  tag<T extends object>(obj: object, ctor: Ctor<T>): this {
    this.tags.set(obj, ctor);
    return this;
  }

  isRegistered(ctor: Ctor): boolean {
    return this.specs.has(ctor);
  }

  get registeredTypes(): readonly Ctor[] {
    return [...this.specs.keys()];
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
    if (Array.isArray(o)) return this.listInfo('Array');
    if (o instanceof EnumValue) return this.enumInfo(o.type);
    if (o instanceof Map) return this.mapInfo;
    if (o instanceof RecordMap) return this.recordInfo;
    if (o instanceof Set) return this.listInfo('Set');
    if (o instanceof MapEntry) return this.entryInfo;
    if (o instanceof RowValue) return this.rowInfo;
    if (o instanceof Date) return SCALARS.date;
    if (ArrayBuffer.isView(o) && !(o instanceof DataView)) return this.listInfo(o.constructor.name);
    const tagged = this.tags.get(o);
    if (tagged !== undefined) return this.infoForCtor(tagged) ?? this.plainInfo;
    const proto = Object.getPrototypeOf(o) as object | null;
    if (proto === null || proto === Object.prototype) return this.plainInfo;
    const cached = this.infos.get(proto);
    if (cached !== undefined) return cached;
    const info = this.buildFor(proto, o);
    this.infos.set(proto, info);
    return info;
  }

  /** Metadata of a registered class (or of a subclass of one); undefined when unknown. */
  infoForCtor(ctor: Ctor): TypeInfo | undefined {
    const proto = ctor.prototype as object;
    const cached = this.infos.get(proto);
    if (cached !== undefined) return cached;
    if (!this.specs.has(ctor) && this.registeredAncestor(ctor) === undefined) return undefined;
    const info = this.buildFor(proto, undefined);
    this.infos.set(proto, info);
    return info;
  }

  private listInfo(name: string): TypeInfo {
    let info = this.listInfos.get(name);
    if (info === undefined) {
      info = new TypeInfo('list', name);
      this.listInfos.set(name, info);
    }
    return info;
  }

  private registeredAncestor(ctor: Ctor): Ctor | undefined {
    for (let c = Object.getPrototypeOf(ctor) as Ctor | null; c !== null && c !== Function.prototype; c = Object.getPrototypeOf(c) as Ctor | null) {
      if (this.specs.has(c)) return c;
    }
    return undefined;
  }

  private buildFor(proto: object, sample: object | undefined): TypeInfo {
    const ctor = (Object.prototype.hasOwnProperty.call(proto, 'constructor') ? (proto as { constructor: unknown }).constructor : undefined) as Ctor | undefined;
    if (ctor !== undefined && typeof ctor === 'function') {
      const spec = this.specs.get(ctor);
      if (spec !== undefined) return this.buildDeclared(ctor, spec);
      const anc = this.registeredAncestor(ctor);
      if (anc !== undefined) {
        // Unregistered subclass of a registered class: inherits the declared members.
        const b = this.infoForCtor(anc)!;
        const name = ctor.name || b.shortName;
        return new TypeInfo(b.kind, name, b.members, {
          typeNames: [name, ...b.typeNames], idMember: b.idMember, displayMember: b.displayMember,
          callable: b.callable, base: b, description: b.description, ctor,
        });
      }
    }
    if (sample !== undefined && typeof (sample as { [Symbol.iterator]?: unknown })[Symbol.iterator] === 'function') {
      return this.listInfo(ctor?.name || 'Iterable');
    }
    return this.buildDynamic(proto, ctor);
  }

  private buildDeclared(ctor: Ctor, spec: TypeSpec): TypeInfo {
    const baseCtor = spec.base !== undefined ? spec.base ?? undefined : this.registeredAncestor(ctor);
    const base = baseCtor !== undefined ? this.infoForCtor(baseCtor) : undefined;
    const name = spec.name ?? ctor.name;
    const members: MemberAccessor[] = base !== undefined ? [...base.members] : [];
    for (const ms of spec.members) {
      const m = this.makeMember(ctor, typeof ms === 'string' ? { name: ms } : ms, members.length);
      const i = members.findIndex((x) => x.name === m.name);
      // Overrides keep the base declaration order but use the most derived accessor.
      if (i >= 0) members[i] = m.with({ order: members[i]!.order });
      else members.push(m);
    }

    const pick = (explicit: string | null | undefined, inherited: MemberAccessor | undefined, dflt: (m: MemberAccessor) => boolean): MemberAccessor | undefined => {
      if (explicit === null) return undefined;
      if (explicit !== undefined) {
        const m = members.find((x) => x.name === explicit);
        if (m === undefined) throw new Error(`type '${name}': member '${explicit}' is not declared`);
        return m;
      }
      if (inherited !== undefined) return members.find((x) => x.name === inherited.name);
      return members.find(dflt);
    };
    let idMember = pick(spec.id, base?.idMember, (m) => m.name === 'Id' && ID_TYPES.has(unwrapNullable(m.type)));
    if (idMember !== undefined && idMember.canWrite) {
      // Stable identities never change through RQL (refs would silently break).
      const ro = idMember.with({ canWrite: false });
      members[members.indexOf(idMember)] = ro;
      idMember = ro;
    }
    const displayMember = pick(spec.display, base?.displayMember, (m) => m.name === 'Name' && unwrapNullable(m.type) === 'string');

    const callable = new Map<string, MethodAccessor>();
    for (const m of base?.callable ?? []) callable.set(m.name, m);
    for (const ms of spec.methods ?? []) callable.set(ms.name, this.makeMethod(ms));
    const methods = [...callable.values()].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));

    return new TypeInfo(spec.kind ?? base?.kind ?? 'object', name, members, {
      typeNames: [name, ...(base?.typeNames ?? []), ...(spec.interfaces ?? [])],
      idMember, displayMember, callable: methods, base, description: spec.description, ctor,
    });
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
    return new MemberAccessor(key, type, get, set, canWrite, !spec.reference, order, spec.description, this);
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
    return new MethodAccessor(name, spec.params ?? [], spec.returns, spec.description, invoke);
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
    return new TypeInfo('object', shortName, getters, {
      typeNames: names.length > 0 ? names : ['Object'],
      dynamic: (o) => this.ownMembers(o, getters),
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
          key, 'any', (o) => normalize((o as Record<string, unknown>)[key]),
          canWrite ? (o, v) => { (o as Record<string, unknown>)[key] = v; } : undefined,
          canWrite, true, existing >= 0 ? existing : out.length, undefined, this,
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
        key, 'any', (o) => normalize((o as Record<string, unknown>)[key]),
        (o, v) => {
          (o as Record<string, unknown>)[key] = v;
        },
        true, true, order, undefined, this,
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
