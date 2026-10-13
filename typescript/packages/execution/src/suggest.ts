/** "Did you mean" suggestions (Levenshtein + substring), as in the .NET reference. */
export function closest(name: string, candidates: Iterable<string>, max: number): string[] {
  const lower = name.toLowerCase();
  const upName = name.toUpperCase();
  const scored: { c: string; d: number; sub: boolean }[] = [];
  for (const c of candidates) {
    const upC = c.toUpperCase();
    const d = distance(lower, c.toLowerCase());
    const sub = upC.includes(upName) || upName.includes(upC);
    if (sub || d <= Math.max(1, Math.floor(name.length / 3))) scored.push({ c, d, sub });
  }
  scored.sort((a, b) => (a.sub === b.sub ? 0 : a.sub ? -1 : 1) || a.d - b.d || (a.c < b.c ? -1 : a.c > b.c ? 1 : 0));
  return [...new Set(scored.map((s) => s.c))].slice(0, max);
}

function distance(a: string, b: string): number {
  let prev = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const cur = [i];
    for (let j = 1; j <= b.length; j++) {
      cur[j] = Math.min(prev[j]! + 1, cur[j - 1]! + 1, prev[j - 1]! + (a[i - 1] === b[j - 1] ? 0 : 1));
    }
    prev = cur;
  }
  return prev[b.length]!;
}
