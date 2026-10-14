/**
 * Host-facing RQL runtime over live JS objects (port of dotnet/Rql.Execution/RqlEngine.cs):
 * sources, extension functions/stages, mutation policy, budgets, stable references.
 */
import { diagnostic, failResult, formatNumber, type Budgets, type JsonObject, type QueryOptions, type QueryPlan, type QueryResult } from '@rql/contracts';
import { isIdentStart, tryParse } from '@rql/parser';
import { reachableTypes, typeDescriptor } from './describe.js';
import type { ClassAdapter, InterfaceAdapter, TypeAdapter } from './adapters.js';
import { Exec, now, withActiveModel } from './exec.js';
import { Pipeline } from './pipeline.js';
import { EnumValue, TypeModel, type Ctor, type MemberAccessor, type TypeInfo } from './types.js';
import { MapEntry } from './values.js';
import { elements } from './collections.js';

export interface SourceRegistration {
  readonly name: string;
  readonly getter: () => unknown;
  readonly description: string | undefined;
}

/** Extension function `ns.fn(args)`: receives evaluated arguments (may include MISSING/ErrorValue). */
export type ExtensionFunction = (args: unknown[]) => unknown;

/** Per-item evaluator handed to extension stages: evaluates an argument expression with `$` = item. */
export type ArgEvaluator = (item: unknown) => unknown;

/** Extension stage `ns.stage args named=...`: maps the item stream to a new stream. */
export type StageHandler = (
  input: Iterable<unknown>,
  args: readonly ArgEvaluator[],
  named: ReadonlyMap<string, ArgEvaluator>,
) => Iterable<unknown>;

export interface MutationPolicy {
  /** Host-level switch. Requests must additionally set `options.allowMutation`. */
  enabled: boolean;
  /** Optional extra allow-check per (declaring type, member). */
  canWrite?: (type: TypeInfo, member: MemberAccessor) => boolean;
  /** Default per-request target limit (requests may raise it up to `hardMaxTargets`). */
  maxTargets: number;
  /** Host ceiling no request can exceed. */
  hardMaxTargets: number;
}

export interface EngineDefaults {
  maxVisited: number;
  maxRows: number;
  maxListItems: number;
  maxDepth: number;
  timeoutMs: number;
  maxOutputBytes: number;
  defaultRecurseDepth: number;
  /** Hard caps applied after request budgets. */
  hardMaxVisited: number;
  hardMaxRows: number;
  hardTimeoutMs: number;
}

export type RefResolution = { ok: true; value: object } | { ok: false; error: string };

export interface EngineOptions {
  /** Type descriptors (shareable between engines). Default: a new empty model. */
  types?: TypeModel;
}

export class RqlEngine {
  /** Reflection model (descriptors + adapters). Replaced by a private fork on the first `addAdapter` if shared. */
  get types(): TypeModel {
    return this.model;
  }
  readonly defaults: EngineDefaults = {
    maxVisited: 1_000_000,
    maxRows: 50,
    maxListItems: 20,
    maxDepth: 16,
    timeoutMs: 2_000,
    maxOutputBytes: 65_536,
    defaultRecurseDepth: 8,
    hardMaxVisited: 5_000_000,
    hardMaxRows: 10_000,
    hardTimeoutMs: 30_000,
  };
  readonly mutations: MutationPolicy = { enabled: false, maxTargets: 20, hardMaxTargets: 1000 };
  runtimeName = 'typescript';
  /** Liveness check for explicitly destroyed objects. Undefined = always alive. */
  isAlive: ((o: object) => boolean) | undefined;
  /** Resolves "Type#id" (type short name, id text); undefined = fall back to searching the sources. */
  resolveStableRef: ((type: string, id: string) => unknown) | undefined;
  /** When false, `$path` tracking is computed only for plans that reference `$path`. */
  alwaysTrackPaths = false;

  private readonly sourceMap = new Map<string, SourceRegistration>();
  private readonly functionMap = new Map<string, ExtensionFunction>();
  private readonly stageMap = new Map<string, StageHandler>();
  private readonly handles = new WeakMap<object, number>();
  private readonly handleTargets = new Map<number, WeakRef<object>>();
  private nextHandle = 0;
  private knownMembers: { version: number; names: ReadonlyMap<string, string> } | undefined;

  private model: TypeModel;
  /** False while `model` may be shared with other engines (passed in via options). */
  private ownsModel: boolean;

  constructor(options: EngineOptions = {}) {
    this.model = options.types ?? new TypeModel();
    this.ownsModel = options.types === undefined;
  }

  /**
   * Registers a type adapter (spec §13): virtual/hidden/explicit-only members, encoding, coercion,
   * comparison, identity and liveness. Register adapters before running queries. A model shared with
   * other engines is forked first, so their behaviour does not change.
   */
  addAdapter<T extends object>(adapter: ClassAdapter<T>): this;
  addAdapter(adapter: InterfaceAdapter): this;
  addAdapter<T extends object>(adapter: TypeAdapter<T>): this;
  addAdapter(adapter: TypeAdapter): this {
    if (!this.ownsModel) {
      this.model = this.model.fork();
      this.ownsModel = true;
    }
    this.model.addAdapter(adapter);
    this.knownMembers = undefined;
    return this;
  }

  /** The ordered adapters applying to a class, most specific first (rank, priority). */
  explain(ctor: Ctor): string[] {
    return this.model.explain(ctor);
  }

  get sources(): ReadonlyMap<string, SourceRegistration> {
    return this.sourceMap;
  }
  get functions(): ReadonlyMap<string, ExtensionFunction> {
    return this.functionMap;
  }
  get stages(): ReadonlyMap<string, StageHandler> {
    return this.stageMap;
  }

  addSource(name: string, getter: () => unknown, description?: string): this {
    validateName(name, false);
    this.sourceMap.set(name, { name, getter, description });
    this.knownMembers = undefined;
    return this;
  }

  addFunction(name: string, fn: ExtensionFunction): this {
    validateName(name, true);
    this.functionMap.set(name, fn);
    return this;
  }

  addStage(name: string, handler: StageHandler): this {
    validateName(name, true);
    this.stageMap.set(name, handler);
    return this;
  }

  // ---------------------------------------------------------------- execution

  /** Executes RQL text or a QueryPlan. Never throws for query errors (they become diagnostics). */
  execute(query: string | QueryPlan, budgets: Budgets = {}, options: QueryOptions = {}): QueryResult {
    const started = now();
    let plan: QueryPlan;
    if (typeof query === 'string') {
      const parsed = tryParse(query);
      if (!parsed.ok) {
        const fail = failResult([parsed.error]);
        fail.stats.elapsedMs = now() - started;
        return fail;
      }
      plan = parsed.plan;
    } else {
      plan = query;
    }
    const x = new Exec(this, budgets, options, plan);
    let result: QueryResult;
    try {
      result = withActiveModel(this.model, () => new Pipeline(x).run());
    } catch (e) {
      // Last line of defence: untrusted queries must never crash the host with a raw exception.
      const name = e instanceof Error ? e.name : typeof e;
      const message = e instanceof Error ? e.message : String(e);
      result = failResult([diagnostic('error', 'internal-error', `${name}: ${message}`, 'this is an RQL runtime bug (or a host type adapter threw); please report the query')]);
    }
    result.stats = { visited: x.visited, membersRead: x.membersRead, elapsedMs: now() - started };
    return result;
  }

  // ---------------------------------------------------------------- references

  /** Stable reference string for an object: "Type#id" or session handle "Type@n". */
  refOf(o: object): string {
    const info = this.types.infoOf(o);
    const id = stableIdOf(o, info);
    return id !== undefined ? info.shortName + '#' + id : info.shortName + '@' + String(this.handleOf(o));
  }

  /** Liveness: the first adapter verdict, else the host `isAlive` hook, else alive. */
  alive(o: object): boolean {
    for (const a of this.types.infoOf(o).adapters) {
      const v = a.isAlive?.(o);
      if (v !== undefined) return v;
    }
    return this.isAlive === undefined || this.isAlive(o);
  }

  private handleOf(o: object): number {
    const h = this.handles.get(o);
    if (h !== undefined) return h;
    const id = ++this.nextHandle;
    this.handles.set(o, id);
    this.handleTargets.set(id, new WeakRef(o));
    if ((id & 4095) === 0) {
      // Drop entries whose objects were collected so the table does not grow forever.
      for (const [k, w] of this.handleTargets) if (w.deref() === undefined) this.handleTargets.delete(k);
    }
    return id;
  }

  /**
   * Member names declared by any type statically reachable from the sources (plus the universal
   * key/value/count/Length), keyed by upper-cased name; used to flag never-evaluated bare names as typos.
   */
  knownMemberNames(): ReadonlyMap<string, string> {
    const cached = this.knownMembers;
    if (cached !== undefined && cached.version === this.types.version) return cached.names;
    const names = new Map<string, string>(['key', 'value', 'count', 'Count', 'Length'].map((n) => [n.toUpperCase(), n]));
    const add = (n: string): void => {
      if (!names.has(n.toUpperCase())) names.set(n.toUpperCase(), n);
    };
    const roots: TypeInfo[] = [];
    for (const s of this.sourceMap.values()) {
      let v: unknown;
      try {
        v = s.getter();
      } catch {
        continue;
      }
      if (v === null || v === undefined) continue;
      const info = this.types.infoOf(v);
      roots.push(info);
      // Undeclared roots: learn members from the instance itself (and a few elements of collections).
      if (info.dynamic !== undefined) for (const m of info.membersOf(v)) add(m.name);
      if ((info.isList || info.isMap) && typeof v === 'object') {
        let n = 0;
        for (const e of elements(v, info)) {
          const ev = e instanceof MapEntry ? e.Value : e;
          if (typeof ev === 'object' && ev !== null) {
            const ei = this.types.infoOf(ev);
            roots.push(ei);
            for (const m of ei.membersOf(ev)) add(m.name);
          }
          if (++n >= 8) break;
        }
      }
    }
    for (const t of reachableTypes(this.types, roots, 1000)) for (const m of t.members) add(m.name);
    this.knownMembers = { version: this.types.version, names };
    return names;
  }

  /** Type descriptors of everything reachable from the registered sources (schema tool). */
  describeTypes(): JsonObject[] {
    const roots: TypeInfo[] = [];
    for (const s of this.sourceMap.values()) {
      let v: unknown;
      try {
        v = s.getter();
      } catch {
        continue;
      }
      if (v !== null && v !== undefined) roots.push(this.types.infoOf(v));
    }
    return reachableTypes(this.types, roots).map((t) => typeDescriptor(this.types, t, this.mutations.enabled));
  }

  /** Resolves a reference; tolerates copied display forms such as "Enemy#12(Goblin_0012)". */
  resolveRef(reference: string, x?: Exec): RefResolution {
    let ref = reference.trim();
    const cut = ref.search(/[ (\t]/);
    if (cut > 0) ref = ref.slice(0, cut);
    const i = ref.search(/[#@]/);
    if (i <= 0 || i === ref.length - 1) return { ok: false, error: `malformed reference '${ref}' (expected Type#id or Type@handle)` };
    const type = ref.slice(0, i);
    const id = ref.slice(i + 1);
    let found: unknown;
    if (ref[i] === '@') {
      if (/^\s*[+-]?\d+\s*$/.test(id)) found = this.handleTargets.get(Number(id))?.deref();
    } else {
      found = this.resolveStableRef !== undefined ? this.resolveStableRef(type, id) : this.searchSources(type, id, x);
    }
    if (typeof found !== 'object' || found === null || !this.types.infoOf(found).isA(type)) {
      return { ok: false, error: `reference '${ref}' does not resolve to a live object` };
    }
    if (!this.alive(found)) return { ok: false, error: `reference '${ref}' points to a destroyed object` };
    return { ok: true, value: found };
  }

  /** Fallback resolution; nodes count toward the calling query's budgets and results are cached per query. */
  private searchSources(type: string, id: string, x: Exec | undefined): object | undefined {
    const key = type + '#' + id;
    if (x !== undefined && x.refCache.has(key)) return x.refCache.get(key);
    const found = this.searchSourcesCore(type, id, x);
    x?.refCache.set(key, found);
    return found;
  }

  /** Bounded walk over owning members of all sources. */
  private searchSourcesCore(type: string, id: string, x: Exec | undefined): object | undefined {
    const visited = new Set<object>();
    const stack: { o: object; depth: number }[] = [];
    for (const s of this.sourceMap.values()) {
      const v = s.getter();
      if (typeof v === 'object' && v !== null) stack.push({ o: v, depth: 0 });
    }
    let budget = this.defaults.maxVisited;
    while (stack.length > 0 && budget-- > 0) {
      x?.visit();
      const { o, depth } = stack.pop()!;
      if (visited.has(o)) continue;
      visited.add(o);
      const info = this.types.infoOf(o);
      if (info.isList || info.isMap) {
        for (const e of elements(o, info)) {
          const ev = e instanceof MapEntry ? e.Value : e;
          if (typeof ev === 'object' && ev !== null && !(ev instanceof EnumValue)) stack.push({ o: ev, depth });
        }
        continue;
      }
      if (!info.isObject) continue;
      if (info.isA(type) && stableIdOf(o, info) === id) return o;
      if (depth >= 32) continue;
      for (const m of info.membersOf(o)) {
        if (!m.owning || m.explicitOnly || m.declaredScalar) continue;
        let v: unknown;
        try {
          v = m.get(o);
        } catch {
          continue;
        }
        if (typeof v === 'object' && v !== null && this.types.infoOf(v).kind !== 'struct') stack.push({ o: v, depth: depth + 1 });
      }
    }
    return undefined;
  }
}

/** The "#id" part of a stable reference: adapter identity first, then the id member; undefined = none. */
function stableIdOf(o: object, info: TypeInfo): string | undefined {
  for (const a of info.adapters) {
    const sid = a.stableId?.(o);
    if (sid !== undefined) return sid;
  }
  const idm = info.idMemberOf(o);
  if (idm === undefined) return undefined;
  let id: unknown;
  try {
    id = idm.get(o);
  } catch {
    id = null;
  }
  return id === null || id === undefined ? undefined : idText(id);
}

function idText(id: unknown): string {
  if (typeof id === 'number') return formatNumber(id);
  if (id instanceof EnumValue) return id.name;
  return String(id);
}

function validateName(name: string, namespaced: boolean): void {
  if (name.length === 0) throw new Error('name required');
  for (const part of name.split('.')) {
    if (part.length === 0 || !isIdentStart(part.charCodeAt(0))) throw new Error(`invalid name '${name}'`);
  }
  if (namespaced && !name.includes('.')) throw new Error(`extension names must be namespaced (ns.name): '${name}'`);
}
