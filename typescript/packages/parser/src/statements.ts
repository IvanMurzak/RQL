/** Splitting a request text into statements at ';' (spec §14). Port of dotnet/Rql.Parser/RqlStatements.cs. */
import type { TextSpan } from '@rql/contracts';
import { trimDotnet } from './lexer.js';

/** One statement of a request text: its trimmed text and the untrimmed segment between separators. */
export interface RqlStatement {
  /** The statement, trimmed. Diagnostic positions of a statement run on its own are relative to this text. */
  readonly text: string;
  readonly span: TextSpan;
}

/** Most statements one request may hold (spec §14). */
export const MAX_STATEMENTS = 10;

/**
 * Splits a request text at each ';' outside string literals, quoted identifiers and brackets. Blank statements are dropped;
 * a text without a separator is one statement spanning the whole text; a text with nothing but separators and blanks is
 * its first (blank) segment. Never throws: an unterminated string or quoted identifier runs to the end of the text.
 */
export function splitStatements(text: string): RqlStatement[] {
  const segments: RqlStatement[] = [];
  const segment = (start: number, end: number): RqlStatement => ({ text: trimDotnet(text.slice(start, end)), span: { start, end } });
  let depth = 0;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'") i = closingQuote(text, i, true);
    else if (c === '`') i = closingQuote(text, i, false);
    else if (c === '(' || c === '[' || c === '{') depth++;
    else if (c === ')' || c === ']' || c === '}') depth = Math.max(0, depth - 1);
    else if (c === ';' && depth === 0) {
      segments.push(segment(start, i));
      start = i + 1;
    }
  }
  segments.push(segment(start, text.length));
  if (segments.length === 1) return segments;
  const statements = segments.filter((s) => s.text.length > 0);
  return statements.length > 0 ? statements : segments.slice(0, 1);
}

/** The index of the quote closing the one at `open`, or the last index when it is unterminated. */
function closingQuote(text: string, open: number, escapes: boolean): number {
  const quote = text[open];
  for (let i = open + 1; i < text.length; i++) {
    if (escapes && text[i] === '\\') i++;
    else if (text[i] === quote) return i;
  }
  return text.length - 1;
}
