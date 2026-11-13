# ADR-0014: Several statements in one request (`;`)

**Status:** accepted (2026-11-12, owner approved making `;` valid). Implemented in the spec (§14, §11), .NET and
TypeScript (splitter, runner, result shape, rules text) and the .NET agent table format; C++ has no text parser
(ADR-0003) and only gains the rules line. Source: the multi-project experiment (task DEV-30): most of its 19 RQL tool
errors were agents joining two reads with `;` (`scenes | select name path isDirty; objects | where name == "Beacon" | …`).

## Decisions

1. **Split at `;` outside strings, quoted identifiers and brackets.** A character scan (no lexer), so a malformed
   statement still splits and reports its own error; an unterminated string runs to the end. Blank statements are
   dropped, so a trailing `;` is harmless. RQL has no comments. Shared vectors: `fixtures/statements/cases.json`.
2. **QueryPlan stays one statement.** The protocol and the C++ executor are unchanged; a request of several statements
   is a text-level layer that parses each statement and runs its plan (`RqlScript.Run` / `runStatements` take the
   executor, so a host that runs plans elsewhere — Unreal's IPC bridge — uses the same rules).
3. **One statement is the old request, byte for byte.** No `statements` field, same diagnostics and positions; only
   text that used to fail on `;` changes.
4. **Each statement is its own request.** Own budgets and diagnostics; positions relative to the statement. Agents read
   one block per statement (`[i/n] statement`).
5. **Reads run every statement**: independent reads are the observed use, and a failed read has no effect to protect.
6. **Mutations: per-statement transactions, validate all first, stop at the first failure.** All-or-nothing across
   statements was rejected: a `call` cannot be undone by the library (§8 already leaves earlier calls in place), and
   rolling back earlier `set` statements after a later one fails would need cross-statement undo that only some hosts
   have. So every statement must parse and end with `set` / `call` before any runs (a typo in statement 3 changes
   nothing), each statement is validated and written all-or-nothing as today, and the first one that fails stops the
   rest; the earlier changes stay and are listed in the result. Host hooks run per statement (a host with undo gets one
   step per statement; a host that saves after the request saves once).
7. **At most 10 statements per request** (a `syntax-error` beyond): per-statement budgets would otherwise multiply the
   time a host's main thread is held and the output size.
