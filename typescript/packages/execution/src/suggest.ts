/** "Did you mean" suggestions and capped member listings (spec §10), as in the .NET reference. */
import { unwrapNullable, type TypeInfo, type TypeModel } from './types.js';

/** Members listed by a hint when no suggestion qualifies. */
export const MAX_LISTED = 12;

export function closest(name: string, candidates: Iterable<string>, max: number): string[] {
  return closestMembers(name, candidates, undefined, max);
}

/** Nested paths offered per type (spec §10): bounds the static walk over struct-typed members. */
export const MAX_NESTED = 200;

/**
 * Members of the types' struct-typed members, one and two levels down, as dotted paths ("BodyInstance.CollisionProfileName"),
 * from static type information only (spec §10 "Nested members"); at most {@link MAX_NESTED} per type.
 */
export function nestedMembers(model: TypeModel, types: Iterable<TypeInfo>): string[] {
  const result = new Set<string>();
  for (const t of types) {
    const budget = { left: MAX_NESTED };
    walk(model, t, '', 1, budget, result);
  }
  return [...result];
}

function walk(model: TypeModel, info: TypeInfo, prefix: string, level: number, budget: { left: number }, result: Set<string>): void {
  for (const m of info.members) {
    const r = unwrapNullable(m.type);
    if (typeof r !== 'object' || !('kind' in r) || r.kind !== 'class') continue;
    const inner = model.infoForCtor(r.ctor);
    if (inner === undefined || inner.kind !== 'struct') continue;
    const path = prefix + m.name + '.';
    for (const im of inner.members) {
      if (budget.left-- <= 0) return;
      result.add(path + im.name);
    }
    if (level < 2) walk(model, inner, path, level + 1, budget, result);
  }
}

/**
 * {@link closest} over a type's members plus nested paths (spec §10): a nested path qualifies and ranks by its last
 * segment; among equal ranks direct members first, then shallower paths, then ordinal.
 */
export function closestMembers(name: string, members: Iterable<string>, nested: readonly string[] | undefined, max: number): string[] {
  const lower = name.toLowerCase();
  const limit = Math.max(1, Math.floor(name.length / 3));
  const scored: { c: string; d: number; sub: boolean; depth: number }[] = [];
  const consider = (c: string, leaf: string, depth: number): void => {
    const d = distance(lower, leaf.toLowerCase());
    const sub = contains(name, leaf);
    if (sub || d <= limit) scored.push({ c, d, sub, depth });
  };
  for (const c of members) consider(c, c, 0);
  for (const c of nested ?? []) consider(c, c.slice(c.lastIndexOf('.') + 1), c.split('.').length - 1);
  scored.sort((a, b) => (a.sub === b.sub ? 0 : a.sub ? -1 : 1) || a.d - b.d || a.depth - b.depth || (a.c < b.c ? -1 : a.c > b.c ? 1 : 0));
  return [...new Set(scored.map((s) => s.c))].slice(0, max);
}

/**
 * Containment that signals a typo or a partial name: the candidate contains the name (at least 3 characters), or the
 * name contains a candidate at least half its length (so "NoSuchType" does not suggest "Type").
 */
function contains(name: string, candidate: string): boolean {
  const n = name.toUpperCase();
  const c = candidate.toUpperCase();
  return (name.length >= 3 && c.includes(n)) || (candidate.length * 2 >= name.length && n.includes(c));
}

/** "members: a b c … (N more)" with at most {@link MAX_LISTED} names. */
export function memberList(names: readonly string[]): string {
  return 'members: ' + names.slice(0, MAX_LISTED).join(' ') + (names.length > MAX_LISTED ? ` … (${names.length - MAX_LISTED} more)` : '');
}

/** Names ordered by their distance to `name` (then ordinal): the nearest come first in a capped list. */
export function bySimilarity(name: string, names: Iterable<string>): string[] {
  const lower = name.toLowerCase();
  return [...new Set(names)]
    .map((c) => ({ c, d: distance(lower, c.toLowerCase()) }))
    .sort((a, b) => a.d - b.d || (a.c < b.c ? -1 : a.c > b.c ? 1 : 0))
    .map((t) => t.c);
}

/**
 * The hint of an unknown member (spec §10): the known types that have it (filter the stream to them first), then the
 * closest names (nested struct members included), else the members ranked by similarity to the name.
 */
export function memberHint(name: string, members: readonly string[], declaring: readonly string[], nested?: readonly string[]): string {
  const parts: string[] = [];
  if (declaring.length > 0) {
    parts.push(`'${name}' is a member of ${declaring.join(', ')}: filter to those first, e.g. where $ is ${declaring[0]} (or where has(${name}))`);
  }
  const close = closestMembers(name, members, nested, 3);
  if (close.length > 0) parts.push(`did you mean ${close.join(' / ')}?`);
  else if (declaring.length === 0 && members.length > 0) parts.push(memberList(bySimilarity(name, members)));
  return parts.join('; ');
}

/** Optimal string alignment distance: Levenshtein plus adjacent transpositions at cost 1 ("Lable" → "Label" = 1). */
export function distance(a: string, b: string): number {
  // Rows i-2, i-1 and i of the DP matrix.
  let prev2: number[] = [];
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const cur = [i];
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      let v = Math.min(prev[j]! + 1, cur[j - 1]! + 1, prev[j - 1]! + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) v = Math.min(v, prev2[j - 2]! + 1);
      cur[j] = v;
    }
    prev2 = prev;
    prev = cur;
  }
  return prev[b.length]!;
}

const META_NAMES = ['type', 'ref', 'path', 'index', 'depth'];

/** The meta member a bare name stands for (`type` → `$type`, any case), or undefined (spec §5, §10). */
export function metaNamed(name: string): string | undefined {
  const lower = name.replace(/[A-Z]/g, (c) => String.fromCharCode(c.charCodeAt(0) + 32));
  return META_NAMES.includes(lower) ? '$' + lower : undefined;
}
