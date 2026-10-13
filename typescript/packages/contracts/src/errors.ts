import type { TextSpan } from './plan.js';

/** The closed set of v0.1 diagnostic codes (spec/semantics.md §10). */
export type DiagnosticCode =
  | 'syntax-error'
  | 'invalid-plan'
  | 'version-unsupported'
  | 'unknown-source'
  | 'unknown-stage'
  | 'unknown-function'
  | 'unknown-member'
  | 'unknown-meta'
  | 'type-mismatch'
  | 'arity'
  | 'div-by-zero'
  | 'getter-threw'
  | 'stale-ref'
  | 'permission-denied'
  | 'readonly-member'
  | 'coercion-failed'
  | 'mutation-limit'
  | 'budget-exceeded'
  | 'timeout'
  | 'output-truncated'
  | 'unsupported'
  | 'no-match';

export type DiagnosticSeverity = 'error' | 'warning';

export interface Diagnostic {
  readonly severity: DiagnosticSeverity;
  readonly code: DiagnosticCode;
  readonly message: string;
  readonly at?: TextSpan;
  readonly hint?: string;
}

export function diagnostic(
  severity: DiagnosticSeverity,
  code: DiagnosticCode,
  message: string,
  hint?: string,
  at?: TextSpan,
): Diagnostic {
  const d: { -readonly [K in keyof Diagnostic]: Diagnostic[K] } = { severity, code, message };
  if (at !== undefined) d.at = at;
  if (hint !== undefined) d.hint = hint;
  return d;
}

/** Thrown for malformed plans/requests and fatal execution errors. `code` is a spec diagnostic code. */
export class RqlError extends Error {
  readonly code: DiagnosticCode;
  readonly at: TextSpan | undefined;
  readonly hint: string | undefined;

  constructor(code: DiagnosticCode, message: string, at?: TextSpan, hint?: string) {
    super(message);
    this.name = 'RqlError';
    this.code = code;
    this.at = at;
    this.hint = hint;
  }

  toDiagnostic(): Diagnostic {
    return diagnostic('error', this.code, this.message, this.hint, this.at);
  }
}
