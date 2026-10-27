/**
 * Host-facing RQL runtime over live JS objects (port of dotnet/Rql.Execution/RqlEngine.cs):
 * sources, extension functions/stages, mutation policy, budgets, stable references.
 */
import { diagnostic, failResult, formatNumber, type Budgets, type JsonObject, type QueryOptions, type QueryPlan, type QueryResult } from '@rql/contracts';
import { isIdentStart, tryParse } from '@rql/parser';
import { reachableTypes, typeDescriptor } from './describe.js';
import { firstAnswer, type ClassAdapter, type InterfaceAdapter, type TypeAdapter } from './adapters.js';
import { Exec, now } from './exec.js';
import { Pipeline } from './pipeline.js';
import { EnumValue, TypeModel, type Ctor, type MemberAccessor, type MethodAccessor, type TypeInfo, type TypeRef } from './types.js';
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

/** Host text coercion for a class target (`ctor`): the value, or undefined/null when not handled; a throw refuses. */
export type TextCoercion = (ctor: Ctor, text: string) => unknown;

export interface MutationPolicy {
  /** Host-level switch. Requests must additionally set `options.allowMutation`. */
  enabled: boolean;
  /** Optional extra allow-check per (declaring type, member). */
  canWrite?: (type: TypeInfo, member: MemberAccessor) => boolean;
  /**
   * Default per-request target limit (spec §8 target limit): requests may raise it up to `hardMaxTargets`. It guards
   * the breadth of a change, not its correctness (all-or-nothing, rollback and dry runs hold at any size): hosts that
   * record a whole stage as one undo step (editors) should use a larger default, e.g. 200.
   */
  maxTargets: number;
  /** Host ceiling no request can exceed (bounds one stage's planning, receipts and undo memory). */
  hardMaxTargets: number;
  /**
   * The name under which this host's callers raise the target limit, e.g. a tool argument `max_targets` that the host
   * maps to `budgets.maxMutationTargets` (spec §10). The mutation-limit hint advises it whether or not
   * `defaults.budgetsAdjustable` is set. Unset: `budgets.maxMutationTargets` when budgets are adjustable, otherwise
   * callers cannot raise the limit and the hint only suggests narrowing or batches.
   */
  limitArgument?: string;
  /**
   * Batch validation (spec §8): sees every planned change of a set / call stage at once (targets, members or methods,
   * coerced values or arguments) after each target passed its own checks and before anything is written or called,
   * on dry runs too. Return a reason to refuse the whole stage (→ invalid-value, nothing changed), or undefined/null.
   * A throw counts as a refusal.
   */
  validateBatch?: (records: readonly MutationRecord[]) => string | null | undefined;
  /**
   * Replaces the mutation-limit hint (spec §10): receives the stage (`set` / `call`) and the target limit; returning
   * undefined/null keeps the built-in hint. Hosts whose tools take their own limit argument name it here.
   */
  limitHint?: (op: string, limit: number) => string | null | undefined;
  /**
   * Host text coercion (spec §8): converts a string written to a struct / object type that no type adapter coerces
   * (an engine's own text form). Return the value, or undefined/null when the type is not handled (the built-in rules
   * then report the failure); throw to refuse with the message as reason.
   */
  coerceText?: TextCoercion;
  /**
   * Runs after planning and before anything is written (not on dry-run), e.g. to record undo state of the
   * owners. Throwing vetoes the whole mutation (→ permission-denied, nothing changed).
   */
  beforeWrite?: (records: readonly MutationRecord[]) => void;
  /**
   * Runs after every write succeeded (not on dry-run or rollback), e.g. to mark the owners dirty. A throw
   * becomes a hook-threw warning; the writes stay applied. Completed calls are reported even when a later call fails.
   */
  committed?: (records: readonly MutationRecord[]) => void;
}

/** One change made (or about to be made) by `set` or `call`, as seen by host hooks. */
export interface MutationRecord {
  /** The mutation target (a stream item). */
  readonly item: object;
  /** The object whose state changes: the last reference object on the written path (e.g. the material, not the mesh). */
  readonly owner: object;
  /** The written path (`set`) or the method signature (`call`). */
  readonly member: string;
  readonly old: unknown;
  readonly new: unknown;
  /** The invoked method for `call`. */
  readonly method?: MethodAccessor;
  /** The coerced arguments of a `call` (defaults filled in); empty for `set`. */
  readonly args: readonly unknown[];
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
  /**
   * Whether the host's callers can pass request budgets (`budgets.*`). Hosts whose tools expose no budgets argument
   * set it to false, so error hints never advise raising a budget the caller cannot reach (spec §10). The target limit
   * has its own switch, `mutations.limitArgument`.
   */
  budgetsAdjustable: boolean;
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
    budgetsAdjustable: true,
  };
  readonly mutations: MutationPolicy = { enabled: false, maxTargets: 20, hardMaxTargets: 1000 };
  runtimeName = 'typescript';
  /** Liveness check for explicitly destroyed objects. Undefined = always alive. */
  isAlive: ((o: object) => boolean) | undefined;
  /** Resolves "Type#id" (type short name, id text); undefined = fall back to searching the sources. */
  resolveStableRef: ((type: string, id: string) => unknown) | undefined;
  /** When false, `$path` tracking is computed only for plans that reference `$path`. */
  alwaysTrackPaths = false;
  /**
   * How callers find an object by name on this host, e.g. `unity.find("Floor")`; ref() hints name it (spec §7).
   * Undefined: the hints suggest a generic query.
   */
  findHint: string | undefined;
  /**
   * How callers list types and their members on this host, e.g. `rql-schema` (a tool name); hints name it (spec §10).
   * Undefined: the hints say "the schema tool".
   */
  schemaHint: string | undefined;
  /**
   * Whether some type the host knows has a member of this name (case-insensitive), beyond the types reachable from the
   * sources. A name known here is never reported as a certain typo: neither by the static unknown-member check nor as the
   * cause of an empty mutation (spec §8). Undefined: only the reachable types count.
   */
  isKnownMember: ((name: string) => boolean) | undefined;

  /**
   * How callers run a mutation on this host, e.g. `rql-mutate` (a tool name); the permission-denied hint of a `set` /
   * `call` sent as a plain query names it (spec §10). Undefined: "the mutation tool".
   */
  mutateHint: string | undefined;

  /** The mutation tool as hint text. */
  get mutateTool(): string {
    return this.mutateHint !== undefined && this.mutateHint !== '' ? this.mutateHint : 'the mutation tool';
  }

  /** The schema tool as hint text. */
  get schemaTool(): string {
    return this.schemaHint !== undefined && this.schemaHint !== '' ? this.schemaHint : 'the schema tool';
  }

  /** Whether the host hook knows the member name (a throwing hook counts as no). */
  hostKnowsMember(name: string): boolean {
    try {
      return this.isKnownMember?.(name) === true;
    } catch {
      return false;
    }
  }

  /** The first registered source, shown as an example in hints (never an invented name). */
  get exampleSource(): string | undefined {
    return this.sourceMap.keys().next().value;
  }

  /** Sources whose value is a single object with a callable method of this name, as hint text, or '' (spec §10). */
  sourcesWithCallable(method: string): string {
    const wanted = method.toUpperCase();
    const names: string[] = [];
    for (const s of this.sourceMap.values()) {
      let v: unknown;
      try {
        v = s.getter();
      } catch {
        continue;
      }
      if (typeof v !== 'object' || v === null || !this.alive(v)) continue;
      const info = this.types.infoOf(v);
      if (info.isList || info.isMap) continue;
      if (info.callable.some((m) => m.name.toUpperCase() === wanted)) names.push(s.name);
    }
    if (names.length === 0) return '';
    return names.length === 1
      ? `; the source '${names[0]}' has ${method}: ${names[0]} | call ${method}(…)`
      : `; sources with ${method}: ${names.join(', ')} (e.g. ${names[0]} | call ${method}(…))`;
  }

  /** The hint of a ref() that did not resolve: malformed text is usually a name, not a $ref. */
  refHint(reference: string): string {
    const t = reference.trim();
    const i = t.search(/[#@]/);
    const find = this.findHint !== undefined ? `look it up with ${this.findHint}` : 'look it up by name: … | where Name == "…"';
    return i <= 0 || i === t.length - 1
      ? `ref() takes a $ref from a query result (Type#id), not a name; ${find}`
      : `the object may be gone or its $ref stale; re-query it (${find}) to get a fresh $ref`;
  }

  private readonly sourceMap = new Map<string, SourceRegistration>();
  private readonly functionMap = new Map<string, ExtensionFunction>();
  private readonly stageMap = new Map<string, StageHandler>();
  private readonly handles = new WeakMap<object, number>();
  private readonly handleTargets = new Map<number, WeakRef<object>>();
  private nextHandle = 0;
  private knownMembers: { version: number; names: ReadonlyMap<string, string>; types: readonly TypeInfo[] } | undefined;

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
    this.ownModel().addAdapter(adapter);
    this.knownMembers = undefined;
    return this;
  }

  /**
   * Never expose members whose declared type (or element / key / value type) matches, e.g. delegates or
   * native handles (spec §13). Configure before queries run; a shared model is forked first.
   */
  excludeTypes(excluded: (type: TypeRef) => boolean): this {
    this.ownModel().excludeTypes(excluded);
    this.knownMembers = undefined;
    return this;
  }

  private ownModel(): TypeModel {
    if (!this.ownsModel) {
      this.model = this.model.fork();
      this.ownsModel = true;
    }
    return this.model;
  }

  /**
   * Every type adapter registered on this engine, in registration order, e.g. for a host known-member hook that also
   * accepts adapter-defined members.
   */
  get adapters(): readonly TypeAdapter[] {
    return this.model.adapters;
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
    this.sourceTypes.delete(name);
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
      result = new Pipeline(x).run();
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

  /**
   * Liveness (the one definition, spec §13): the first adapter verdict, else the host `isAlive` hook
   * (asked about host objects only, not about engine values such as rows or map entries), else alive.
   */
  alive(o: object): boolean {
    const verdict = this.types.adapterAlive(o);
    if (verdict !== undefined) return verdict;
    if (this.isAlive === undefined) return true;
    const info = this.types.infoOf(o);
    return info.ctor === undefined || (info.kind !== 'object' && info.kind !== 'struct') || this.isAlive(o);
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
  /**
   * Records the type of a source's value (queries call it as they read a source). Known names come from the types the
   * sources have produced, not only their current values: a source that is null right now keeps the types it had (spec §8).
   */
  noteSourceType(source: string, value: unknown): void {
    if (typeof value !== 'object' || value === null) return;
    const info = this.types.infoOf(value);
    let seen = this.sourceTypes.get(source);
    if (seen === undefined) this.sourceTypes.set(source, (seen = new Set()));
    if (seen.has(info)) return;
    seen.add(info);
    this.knownMembers = undefined;
  }

  private readonly sourceTypes = new Map<string, Set<TypeInfo>>();

  knownMemberNames(): ReadonlyMap<string, string> {
    // Sources that never produced a value are read again; the others keep the types they had.
    for (const s of this.sourceMap.values()) {
      if (this.sourceTypes.has(s.name)) continue;
      try {
        this.noteSourceType(s.name, s.getter());
      } catch {
        // no type yet
      }
    }
    const cached = this.knownMembers;
    if (cached !== undefined && cached.version === this.types.version) return cached.names;
    const names = new Map<string, string>(['key', 'value', 'count', 'Count', 'Length', 'items'].map((n) => [n.toUpperCase(), n]));
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
    for (const [name, seen] of this.sourceTypes) if (this.sourceMap.has(name)) for (const info of seen) if (!roots.includes(info)) roots.push(info);
    const types = reachableTypes(this.types, roots, 1000);
    for (const t of types) for (const m of t.members) add(m.name);
    this.knownMembers = { version: this.types.version, names, types };
    return names;
  }

  /**
   * Names of the known object types (statically reachable from the sources) that have a member of this name, at most
   * `max`, base types before their subclasses — for hints that suggest filtering a mixed stream (spec §10). Types in
   * `except` are skipped.
   */
  typesDeclaring(member: string, except: Iterable<TypeInfo> = [], max = 3): string[] {
    this.knownMemberNames();
    const skip = new Set(except);
    const hits = (this.knownMembers?.types ?? []).filter((t) => (t.kind === 'object' || t.kind === 'struct') && !skip.has(t) && t.find(member) !== undefined);
    // A base type that has the member covers its subclasses: name the least derived types.
    const derives = (t: TypeInfo, base: TypeInfo): boolean => {
      for (let b = t.base; b !== undefined; b = b.base) if (b === base) return true;
      return false;
    };
    return [...new Set(hits.filter((t) => !hits.some((o) => o !== t && derives(t, o))).map((t) => t.shortName))].slice(0, max);
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
    // Ids may contain spaces / parentheses (asset paths): the exact text wins; copied display forms such as
    // "Enemy#12(Goblin_0012)" or "Enemy#12 Goblin" resolve through their trimmed prefix.
    const exact = reference.trim();
    const cut = exact.search(/[ (\t]/);
    const forms = cut > 0 ? [exact, exact.slice(0, cut)] : [exact];
    const parsed = forms.map(parseRef);
    const first = parsed[0];
    if (first === undefined) {
      const short = parsed[1];
      return short !== undefined ? this.finishRef(short, this.lookup(short, x)) : { ok: false, error: `malformed reference '${exact}' (expected Type#id or Type@handle)` };
    }
    const short = parsed[1];
    // One source walk for both forms when they are stable refs of one type; handles / host resolvers are cheap lookups.
    if (short !== undefined && first.sep === '#' && short.sep === '#' && first.type === short.type && this.resolveStableRef === undefined) {
      const hits = this.searchSources(first.type, [first.id, short.id], x);
      return this.matches(first, hits[0]) ? this.finishRef(first, hits[0]) : this.finishRef(short, hits[1]);
    }
    // An exact-id match is final (a destroyed exact match is stale-ref, not a fallback to the display form).
    const found = this.lookup(first, x);
    return this.matches(first, found) || short === undefined ? this.finishRef(first, found) : this.finishRef(short, this.lookup(short, x));
  }

  private matches(ref: ParsedRef, found: unknown): found is object {
    return typeof found === 'object' && found !== null && this.types.infoOf(found).isA(ref.type);
  }

  private lookup(ref: ParsedRef, x: Exec | undefined): unknown {
    if (ref.sep === '@') return /^\s*[+-]?\d+\s*$/.test(ref.id) ? this.handleTargets.get(Number(ref.id))?.deref() : undefined;
    return this.resolveStableRef !== undefined ? this.resolveStableRef(ref.type, ref.id) : this.searchSources(ref.type, [ref.id], x)[0];
  }

  private finishRef(ref: ParsedRef, found: unknown): RefResolution {
    if (!this.matches(ref, found)) {
      return { ok: false, error: `reference '${ref.text}' does not resolve to a live object` };
    }
    if (!this.alive(found)) return { ok: false, error: `reference '${ref.text}' points to a destroyed object` };
    return { ok: true, value: found };
  }

  /**
   * Fallback resolution: one walk finding the objects with each of `ids` (in order of preference); nodes
   * count toward the calling query's budgets and results are cached per query.
   */
  private searchSources(type: string, ids: readonly string[], x: Exec | undefined): readonly (object | undefined)[] {
    const key = type + '#' + ids.join('\u0000');
    const cached = x?.refCache.get(key);
    if (cached !== undefined) return cached;
    const found = this.searchSourcesCore(type, ids, x);
    x?.refCache.set(key, found);
    return found;
  }

  /** Bounded walk over owning members of all sources. */
  private searchSourcesCore(type: string, ids: readonly string[], x: Exec | undefined): (object | undefined)[] {
    const hits: (object | undefined)[] = ids.map(() => undefined);
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
      if (info.isA(type)) {
        const k = ids.indexOf(stableIdOf(o, info) ?? '\u0000');
        if (k === 0) return [o, ...hits.slice(1)]; // the most preferred form: done
        if (k > 0 && hits[k] === undefined) hits[k] = o;
      }
      if (depth >= 32) continue;
      for (const m of info.implicitMembersOf(o)) {
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
    return hits;
  }
}

interface ParsedRef {
  readonly text: string;
  readonly type: string;
  readonly sep: '#' | '@';
  readonly id: string;
}

function parseRef(text: string): ParsedRef | undefined {
  const i = text.search(/[#@]/);
  if (i <= 0 || i === text.length - 1) return undefined;
  return { text, type: text.slice(0, i), sep: text[i] as '#' | '@', id: text.slice(i + 1) };
}

/** The "#id" part of a stable reference: adapter identity first, then the id member; undefined = none. */
function stableIdOf(o: object, info: TypeInfo): string | undefined {
  const sid = firstAnswer(info.adapterSet.stableId, (a) => a.stableId!(o));
  if (sid !== undefined) return sid;
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
