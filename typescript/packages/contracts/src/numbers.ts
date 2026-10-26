/**
 * Number formatting compatible with .NET `double.ToString("R", InvariantCulture)` (shortest
 * round-trip digits; scientific notation when the decimal exponent is >= 15 or < -4). Used wherever
 * the reference runtime turns a number into text: string concatenation, map keys in output,
 * `$path` keys and set-receipt labels. JSON output itself uses plain JSON numbers.
 */
export function formatNumber(d: number): string {
  if (Number.isNaN(d)) return 'NaN';
  if (d === Infinity) return 'Infinity';
  if (d === -Infinity) return '-Infinity';
  if (d === 0) return Object.is(d, -0) ? '-0' : '0';
  const sign = d < 0 ? '-' : '';
  const [mant = '0', expText = '0'] = Math.abs(d).toExponential().split('e');
  const digits = mant.replace('.', '');
  const exp = Number(expText);
  const scale = exp + 1;
  if (scale > 15 || scale < -3) {
    const rest = digits.slice(1);
    const e = Math.abs(exp);
    return sign + digits[0] + (rest.length > 0 ? '.' + rest : '') + 'E' + (exp < 0 ? '-' : '+') + (e < 10 ? '0' + e : String(e));
  }
  if (scale <= 0) return sign + '0.' + '0'.repeat(-scale) + digits;
  if (scale >= digits.length) return sign + digits + '0'.repeat(scale - digits.length);
  return sign + digits.slice(0, scale) + '.' + digits.slice(scale);
}

/** Integral finite value that a JSON writer emits without a fraction (|d| < 2^53). */
export function isJsonInteger(d: number): boolean {
  return Number.isInteger(d) && Math.abs(d) < 9007199254740992;
}
