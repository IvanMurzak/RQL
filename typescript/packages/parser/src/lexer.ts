/** RQL lexer (spec/grammar.ebnf, lexical part). Port of dotnet/Rql.Parser/Lexer.cs. */
import { RqlError, type TextSpan } from '@rql/contracts';

export type TokenKind = 'ident' | 'quoted' | 'meta' | 'number' | 'string' | 'punct' | 'end';

export interface Token {
  readonly kind: TokenKind;
  /** Identifier name / punctuation / meta name (without `$`) / raw literal text. */
  readonly text: string;
  readonly start: number;
  readonly end: number;
  /** Literal value for number and string tokens. */
  readonly value?: number | string;
  /** Whitespace (or start of input) immediately before the token. */
  readonly spaceBefore: boolean;
}

export const isPunct = (t: Token, p: string): boolean => t.kind === 'punct' && t.text === p;

/** Case-insensitive keyword test (keywords are ASCII). */
export const isWord = (t: Token, word: string): boolean =>
  t.kind === 'ident' && t.text.length === word.length && t.text.toLowerCase() === word;

export const spanOf = (t: Token): TextSpan => ({ start: t.start, end: t.end });

export function describeToken(t: Token): string {
  switch (t.kind) {
    case 'end':
      return 'end of query';
    case 'string':
      return 'string literal';
    case 'number':
      return 'number ' + t.text;
    case 'meta':
      return "'$" + t.text + "'";
    default:
      return "'" + t.text + "'";
  }
}

// Longest punctuation first (order matters for the prefix match).
const PUNCTS = ['==', '!=', '<=', '>=', '&&', '||', '|', '.', ',', '(', ')', '[', ']', '{', '}', ':', '=', '<', '>', '+', '-', '*', '/', '%', '!', '?'];

/** .NET char.IsWhiteSpace. */
export function isWhiteSpace(c: number): boolean {
  return (
    c === 0x20 ||
    (c >= 0x09 && c <= 0x0d) ||
    c === 0x85 ||
    c === 0xa0 ||
    c === 0x1680 ||
    (c >= 0x2000 && c <= 0x200a) ||
    c === 0x2028 ||
    c === 0x2029 ||
    c === 0x202f ||
    c === 0x205f ||
    c === 0x3000
  );
}

export const isIdentStart = (c: number): boolean => (c >= 97 && c <= 122) || (c >= 65 && c <= 90) || c === 95;
const isDigit = (c: number): boolean => c >= 48 && c <= 57;
const isIdentPart = (c: number): boolean => isIdentStart(c) || isDigit(c);

const error = (message: string, start: number, end: number): RqlError =>
  new RqlError('syntax-error', `${message} at ${start}`, { start, end });

export function tokenize(text: string): Token[] {
  const tokens: Token[] = [];
  const n = text.length;
  let i = 0;
  for (;;) {
    const ws = i;
    while (i < n && isWhiteSpace(text.charCodeAt(i))) i++;
    const spaceBefore = i > ws || i === 0;
    if (i >= n) {
      tokens.push({ kind: 'end', text: '', start: n, end: n, spaceBefore });
      return tokens;
    }
    const c = text.charCodeAt(i);
    const start = i;
    if (isIdentStart(c)) {
      i = readIdent(text, i);
      tokens.push({ kind: 'ident', text: text.slice(start, i), start, end: i, spaceBefore });
    } else if (c === 0x24 /* $ */) {
      i++;
      if (i < n && isIdentStart(text.charCodeAt(i))) i = readIdent(text, i);
      tokens.push({ kind: 'meta', text: text.slice(start + 1, i), start, end: i, spaceBefore });
    } else if (c === 0x60 /* ` */) {
      const close = text.indexOf('`', i + 1);
      if (close < 0) throw error('unterminated quoted identifier', start, n);
      if (close === i + 1) throw error('empty quoted identifier', start, close + 1);
      tokens.push({ kind: 'quoted', text: text.slice(i + 1, close), start, end: close + 1, spaceBefore });
      i = close + 1;
    } else if (isDigit(c)) {
      const [end, value] = readNumber(text, i);
      i = end;
      tokens.push({ kind: 'number', text: text.slice(start, i), start, end: i, value, spaceBefore });
    } else if (c === 0x22 || c === 0x27) {
      const [end, value] = readString(text, i);
      i = end;
      tokens.push({ kind: 'string', text: text.slice(start, i), start, end: i, value, spaceBefore });
    } else {
      const p = PUNCTS.find((cand) => text.startsWith(cand, i));
      if (p === undefined) throw error(`unexpected character '${text[i]}'`, start, start + 1);
      i += p.length;
      tokens.push({ kind: 'punct', text: p, start, end: i, spaceBefore });
    }
  }
}

/** ident_seg { "-" ident_seg }: a hyphen joins only when directly followed by a letter or `_`. */
function readIdent(text: string, i: number): number {
  const n = text.length;
  while (i < n && isIdentPart(text.charCodeAt(i))) i++;
  while (i + 1 < n && text.charCodeAt(i) === 0x2d && isIdentStart(text.charCodeAt(i + 1))) {
    i++;
    while (i < n && isIdentPart(text.charCodeAt(i))) i++;
  }
  return i;
}

function readNumber(text: string, i: number): [number, number] {
  const start = i;
  const n = text.length;
  while (i < n && isDigit(text.charCodeAt(i))) i++;
  if (i + 1 < n && text[i] === '.' && isDigit(text.charCodeAt(i + 1))) {
    i++;
    while (i < n && isDigit(text.charCodeAt(i))) i++;
  }
  if (i < n && (text[i] === 'e' || text[i] === 'E')) {
    let j = i + 1;
    if (j < n && (text[j] === '+' || text[j] === '-')) j++;
    if (j < n && isDigit(text.charCodeAt(j))) {
      i = j;
      while (i < n && isDigit(text.charCodeAt(i))) i++;
    }
  }
  if (i < n && isIdentStart(text.charCodeAt(i))) {
    throw error(`invalid number '${text.slice(start, i + 1)}'`, start, i + 1);
  }
  const value = Number(text.slice(start, i));
  if (!Number.isFinite(value)) throw error(`number '${text.slice(start, i)}' is out of range`, start, i);
  return [i, value];
}

const HEX4 = /^[0-9a-fA-F]{1,4}$/;

function readString(text: string, i: number): [number, string] {
  const quote = text[i];
  const start = i++;
  const n = text.length;
  let out = '';
  for (;;) {
    if (i >= n) throw error('unterminated string literal', start, n);
    const c = text[i]!;
    if (c === quote) {
      i++;
      break;
    }
    if (c === '\\') {
      if (i + 1 >= n) throw error('unterminated escape', i, n);
      const e = text[i + 1];
      switch (e) {
        case '"':
        case "'":
        case '\\':
          out += e;
          break;
        case 'n':
          out += '\n';
          break;
        case 't':
          out += '\t';
          break;
        case 'r':
          out += '\r';
          break;
        case 'u': {
          // int.TryParse(..., NumberStyles.HexNumber) tolerates surrounding whitespace.
          const hex = i + 6 > n ? '' : trimDotnet(text.slice(i + 2, i + 6));
          if (!HEX4.test(hex)) throw error('invalid \\u escape', i, Math.min(n, i + 6));
          out += String.fromCharCode(parseInt(hex, 16));
          i += 4;
          break;
        }
        default:
          // Unknown escapes are kept verbatim so regex patterns ("\[", "\.") survive.
          out += '\\' + e;
          break;
      }
      i += 2;
      continue;
    }
    out += c;
    i++;
  }
  return [i, out];
}

/** .NET string.Trim() (char.IsWhiteSpace on both ends). */
export function trimDotnet(s: string): string {
  let a = 0;
  let b = s.length;
  while (a < b && isWhiteSpace(s.charCodeAt(a))) a++;
  while (b > a && isWhiteSpace(s.charCodeAt(b - 1))) b--;
  return s.slice(a, b);
}
