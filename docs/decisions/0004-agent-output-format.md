# ADR-0004: Compact table rendering for agents, JSON for conformance

**Status:** accepted · 2026-10-13 (evidence: experiment E5)

**Decision.** Tools return the compact `col|col` table format by default; the JSON QueryResult remains
the normative/conformance form and is available as `format=json`.

**Evidence.** E5 (dev, n=186 paired): JSON vs table — equal success (100%), +8% total tokens, +19% tool-result
characters, +14% median wall time. A lower cost for JSON in that run is explained by a higher prompt-cache hit ratio
(0.89 vs 0.84), not by the format.
