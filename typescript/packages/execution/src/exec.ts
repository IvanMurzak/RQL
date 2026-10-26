/** Per-query execution state (evaluation scope, budgets, counters, diagnostics). */
import { RqlError, diagnostic, formatNumber, type Budgets, type Diagnostic, type QueryOptions, type QueryPlan, type TextSpan } from '@rql/contracts';
import type { RqlEngine } from './engine.js';
import { EnumValue, type MemberAccessor, type TypeInfo, type TypeModel } from './types.js';

const perf = (globalThis as { performance?: { now(): number } }).performance;
export const now: () => number = perf !== undefined ? () => perf.now() : () => Date.now();

/** Linked navigation path; rendered lazily for `$path`. */
export class PathNode {
  constructor(
    readonly parent: PathNode | null,
    readonly text: string,
  ) {}

  member(name: string): PathNode {
    return new PathNode(this, '.' + name);
  }
  at(i: number): PathNode {
    return new PathNode(this, '[' + String(i) + ']');
  }
  key(key: unknown): PathNode {
    const text =
      typeof key === 'string'
        ? '"' + key.replace(/"/g, '\\"') + '"'
        : typeof key === 'number'
          ? formatNumber(key)
          : key instanceof EnumValue
            ? key.name
            : String(key);
    return new PathNode(this, '[' + text + ']');
  }

  toString(): string {
    const parts: string[] = [];
    for (let n: PathNode | null = this; n !== null; n = n.parent) parts.push(n.text);
    return parts.reverse().join('');
  }
}

/** Per-path-segment statistics (unknown-member diagnostics) plus a monomorphic inline cache. */
export class MemberSite {
  hits = 0;
  misses = 0;
  readonly missTypes = new Set<TypeInfo>();
  /** Columns of the first projected row that lacked the member. */
  rowColumns: readonly string[] | undefined;
  cachedInfo: TypeInfo | undefined;
  cachedMember: MemberAccessor | undefined;

  constructor(
    readonly name: string,
    readonly span: TextSpan | undefined,
    /** First segment of a relative bare path (no `on`, not `$`): eligible for the static name check. */
    readonly bare = false,
  ) {}
}

/** Statistics of one `x is T` test (T a literal), used for the no-match diagnostic. */
export class IsSite {
  evaluated = 0;
  matched = 0;
  readonly seen = new Set<TypeInfo>();

  constructor(
    readonly typeName: string,
    readonly span: TextSpan | undefined,
  ) {}
}

export class Exec {
  readonly types: TypeModel;
  readonly maxVisited: number;
  readonly maxRows: number;
  readonly maxListItems: number;
  readonly maxDepth: number;
  readonly timeoutMs: number;
  readonly maxOutputBytes: number;
  readonly maxMutationTargets: number;
  trackPaths = false;

  // evaluation scope (mutable; nested scopes save and restore it)
  current: unknown = null;
  currentPath: PathNode | null = null;
  /** Depth within the most recent recurse stage (0 = start item); -1 outside recurse. */
  currentDepth = -1;
  index = 0;
  /** Enclosing items while evaluating collection-method arguments (scope chain, innermost last). */
  readonly outer: unknown[] = [];

  visited = 0;
  membersRead = 0;
  getterErrors = 0;
  firstGetterError: string | undefined;
  readonly diagnostics: Diagnostic[] = [];
  readonly sites: MemberSite[] = [];
  readonly isSites: IsSite[] = [];
  /** Per-query cache of fallback ref searches ("Type#ids" → objects, one per id form). */
  readonly refCache = new Map<string, readonly (object | undefined)[]>();
  private readonly started = now();

  constructor(
    readonly engine: RqlEngine,
    budgets: Budgets,
    readonly options: QueryOptions,
    readonly plan: QueryPlan,
  ) {
    this.types = engine.types;
    this.liveness = this.types.hasCapability('isAlive') || engine.isAlive !== undefined;
    const d = engine.defaults;
    this.maxVisited = Math.min(budgets.maxVisited ?? d.maxVisited, d.hardMaxVisited);
    this.maxRows = Math.min(budgets.maxRows ?? d.maxRows, d.hardMaxRows);
    this.maxListItems = budgets.maxListItems ?? d.maxListItems;
    this.maxDepth = budgets.maxDepth ?? d.maxDepth;
    this.timeoutMs = Math.min(budgets.timeoutMs ?? d.timeoutMs, d.hardTimeoutMs);
    this.maxOutputBytes = budgets.maxOutputBytes ?? d.maxOutputBytes;
    // Requests may change the per-request limit, but never beyond the host's hard ceiling.
    this.maxMutationTargets = Math.min(budgets.maxMutationTargets ?? engine.mutations.maxTargets, engine.mutations.hardMaxTargets);
  }

  /** Whether any liveness source exists (adapter capability or host hook): one flag gates every check. */
  readonly liveness: boolean;

  /** A destroyed host object (spec §13): it reads as null everywhere. */
  dead(v: unknown): boolean {
    return this.liveness && typeof v === 'object' && v !== null && !this.engine.alive(v);
  }

  /** `v`, or null when it is a destroyed object: applied where values enter expressions. */
  live<T>(v: T): T | null {
    return this.liveness && this.dead(v) ? null : v;
  }

  get elapsedMs(): number {
    return now() - this.started;
  }

  warn(code: Diagnostic['code'], message: string, hint?: string): void {
    this.diagnostics.push(diagnostic('warning', code, message, hint));
  }

  private warned: Set<string> | undefined;

  /** Adds a warning once per `key` (default: code + message), however often it recurs. */
  warnOnce(code: Diagnostic['code'], message: string, key?: string): void {
    this.warned ??= new Set<string>();
    const k = key ?? code + '\n' + message;
    if (this.warned.has(k)) return;
    this.warned.add(k);
    this.warn(code, message);
  }

  visit(): void {
    if (++this.visited > this.maxVisited) {
      throw new RqlError(
        'budget-exceeded',
        `visited more than ${this.maxVisited} objects`,
        undefined,
        'narrow the source (e.g. a specific scene), filter earlier, lower recurse max-depth, or raise budgets.maxVisited',
      );
    }
    if ((this.visited & 1023) === 0) this.checkTime();
  }

  checkTime(): void {
    if (this.elapsedMs > this.timeoutMs) {
      throw new RqlError('timeout', `query exceeded ${this.timeoutMs} ms`, undefined, 'narrow the query or raise budgets.timeoutMs');
    }
  }

  getterThrew(member: string, e: unknown): void {
    if (this.getterErrors++ === 0) {
      const name = e instanceof Error ? e.name : typeof e;
      const msg = e instanceof Error ? e.message : String(e);
      this.firstGetterError = `getter '${member}' threw ${name}: ${msg}`;
    }
  }
}

export function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
