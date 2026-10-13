/**
 * Host-facing RQL runtime over live JS objects (port of dotnet/Rql.Execution/RqlEngine.cs):
 * sources, extension functions/stages, mutation policy, budgets, stable references.
 */
import { failResult, formatNumber, type Budgets, type JsonObject, type QueryOptions, type QueryPlan, type QueryResult } from '@rql/contracts';
import { isIdentStart, tryParse } from '@rql/parser';
import { reachableTypes, typeDescriptor } from './describe.js';
import { Exec, now } from './exec.js';
import { Pipeline } from './pipeline.js';
import { EnumValue, TypeModel, type MemberAccessor, type TypeInfo } from './types.js';
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
  maxTargets: number;
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
  readonly types: TypeModel;
  readonly defaults: EngineDefaults = {
    maxVisited: 200_000,
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
  readonly mutations: MutationPolicy = { enabled: false, maxTargets: 20 };
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

  constructor(options: EngineOptions = {}) {
    this.types = options.types ?? new TypeModel();
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
    const result = new Pipeline(x).run();
    result.stats = { visited: x.visited, membersRead: x.membersRead, elapsedMs: now() - started };
    return result;
  }

  // ---------------------------------------------------------------- references

  /** Stable reference string for an object: "Type#id" or session handle "Type@n". */
  refOf(o: object): string {
    const info = this.types.infoOf(o);
    const idm = info.idMemberOf(o);
    if (idm !== undefined) {
      let id: unknown;
      try {
        id = idm.get(o);
      } catch {
        id = null;
      }
      if (id !== null && id !== undefined) return info.shortName + '#' + idText(id);
    }
    return info.shortName + '@' + String(this.handleOf(o));
  }

  private handleOf(o: object): number {
    const h = this.handles.get(o);
    if (h !== undefined) return h;
    const id = ++this.nextHandle;
    this.handles.set(o, id);
    this.handleTargets.set(id, new WeakRef(o));
    return id;
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
  resolveRef(reference: string): RefResolution {
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
      found = this.resolveStableRef !== undefined ? this.resolveStableRef(type, id) : this.searchSources(type, id);
    }
    if (typeof found !== 'object' || found === null || !this.types.infoOf(found).isA(type)) {
      return { ok: false, error: `reference '${ref}' does not resolve to a live object` };
    }
    if (this.isAlive !== undefined && !this.isAlive(found)) return { ok: false, error: `reference '${ref}' points to a destroyed object` };
    return { ok: true, value: found };
  }

  /** Fallback ref resolution: bounded walk over owning members of all sources. */
  private searchSources(type: string, id: string): object | undefined {
    const visited = new Set<object>();
    const stack: { o: object; depth: number }[] = [];
    for (const s of this.sourceMap.values()) {
      const v = s.getter();
      if (typeof v === 'object' && v !== null) stack.push({ o: v, depth: 0 });
    }
    let budget = this.defaults.maxVisited;
    while (stack.length > 0 && budget-- > 0) {
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
      const idm = info.idMemberOf(o);
      if (idm !== undefined && info.isA(type)) {
        let v: unknown;
        try {
          v = idm.get(o);
        } catch {
          v = null;
        }
        if (v !== null && v !== undefined && idText(v) === id) return o;
      }
      if (depth >= 32) continue;
      for (const m of info.membersOf(o)) {
        if (!m.owning || m.declaredScalar) continue;
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
