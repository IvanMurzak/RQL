/** "Did you mean" suggestions and capped member listings (spec §10), as in the .NET reference. */

/** Members listed by a hint when no suggestion qualifies. */
export const MAX_LISTED = 12;

export function closest(name: string, candidates: Iterable<string>, max: number): string[] {
  const lower = name.toLowerCase();
  const limit = Math.max(1, Math.floor(name.length / 3));
  const scored: { c: string; d: number; sub: boolean }[] = [];
  for (const c of candidates) {
    const d = distance(lower, c.toLowerCase());
    const sub = contains(name, c);
    if (sub || d <= limit) scored.push({ c, d, sub });
  }
  scored.sort((a, b) => (a.sub === b.sub ? 0 : a.sub ? -1 : 1) || a.d - b.d || (a.c < b.c ? -1 : a.c > b.c ? 1 : 0));
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
