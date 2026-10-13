# ADR-0002: The QueryPlan, not the text syntax, is the cross-runtime contract

**Status:** accepted · 2026-10-13

**Decision.** Parsers emit a canonical, versioned JSON QueryPlan (fixed key order, defaults omitted).
Executors consume plans. Conformance compares plans (syntax fixtures) and results (execution fixtures).
Text conveniences (aliases, `is null`, `&&`, case-insensitive keywords) are canonicalized away.

**Consequences.** Remote executors (Unreal C++) never need a parser; text syntax can evolve for agent
ergonomics without touching executors, as long as the canonical plan is unchanged.
