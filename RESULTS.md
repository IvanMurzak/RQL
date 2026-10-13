# RESULTS — what was implemented and measured

All numbers below were produced in this session on a local machine (Apple M-series, 12 cores, 24 GB,
macOS; .NET 10.0.13, Node 26, Apple clang 21) and against the live DeepSeek API. Nothing is
extrapolated. Raw traces are in the gitignored `evaluation/runs/`; per-run summaries recomputed from those
traces are committed in `evaluation/baselines/*.json`; spend is in `evaluation/baselines/spend-ledger.json`.

## 1. Implemented

| Area | State |
|---|---|
| Spec | EBNF grammar, normative semantics (incl. §12 portability decisions), JSON schemas for QueryPlan / QueryResult / ObjectRef / CapabilityManifest, versioning + changelog |
| Golden vectors | __VECTORS__ |
| .NET reference | parser, codec, live-CLR executor (compiled or reflection accessors), mutations, diagnostics, agent formats; libraries target netstandard2.1 + net8.0 |
| TypeScript | contracts, identical parser, live-object executor with descriptor registry |
| C++17 | own JSON, codec, provider-based reflection (`TypeRegistry`), executor; no parser by design |
| Sandbox | deterministic game world + app data (small 0.3k → huge 145k objects), tool host, snapshot differ, CLI |
| Tier A | conformance in 3 runtimes, benchmarks, task self-check (reference agent 89/89) |
| Tier B | 89-task catalog v1 (dev 62 / holdout 27 instances), hidden grader, DeepSeek runner, paired reports, spend ledger, regrade |
| Baseline for comparison | conventional reflection tools (find / get_object JSON dump / set / call / describe) on the same tasks + grader |

## 2. Tier A (deterministic)

Conformance (latest run): __CONFORMANCE__

Executor benchmark, Large world (14,437 GameObjects; `evaluation/reports/tierA-large.json`, median of 51
interleaved runs after 60 warmups, compiled accessors):

__TIERA_TABLE__

* **Optimizations measured** (same benchmark, before → after): `sort | first 10` fused into a bounded
  top-N: 1.79 → 0.50 ms (−72%), 830 → 287 KB allocated. Generic `recurse`: 7.9 → 6.8 ms, 20.3 → 10.1 MB.
  Per-element enumerator boxing removed from source/`get` expansion; a per-call delegate allocation
  in the type cache removed. One attempted optimization was **rejected by measurement**: an
  allocation-free child collector for `recurse Children` cut allocations −82% but was slower in median
  (1.75 vs 0.95 ms), so the iterator version stays for `via` recursion (root cause not established).
* **Compiled vs reflection accessors**: within 3–10% (ADR-0005), so the AOT/IL2CPP-safe mode is viable.
* **Huge world** (145,001 objects): typical queries 7–15 ms; `first-lazy` 0.15 ms (stops after 4,897
  items). Two unbounded queries (`world | recurse max-depth=8`, `get Components` over everything) are
  stopped by the default `maxVisited` budget with an actionable error, which is the designed behaviour.
* **Why not "serialize, then filter"**: the same Large world as one JSON document is 7.7 MB with
  `ReferenceHandler.Preserve` (85 MB for Huge; ≈ 2.3 M / 25 M tokens at ~3.5 chars/token, an
  **estimate**), and with `IgnoreCycles` the serializer ran **out of memory** because shared references
  re-expand whole subgraphs. RQL answers were 51–3,450 bytes.

## 3. Tier B (agent in the loop)

Setup: DeepSeek `deepseek-flash` (V4.1-Flash) non-thinking, temperature 0, max 12 turns, 4,096 tokens per call, 50 rows
per result; 3 repeats per task; cost = provider-reported tokens × `configs/pricing.json` snapshot
(off-peak rates applied by request time). Holdout templates/seeds were never used for tuning.

### 3.1 Experiment log (one factor at a time unless noted)

__EXPERIMENT_TABLE__

### 3.2 What each change was and why (failure evidence → change → effect)

__FINDINGS__

### 3.3 RQL vs conventional reflection tools

__LEGACY__

## 4. Spend

__SPEND__

## 5. Limitations (honest)

* One provider and model family. Behaviour with other models is untested here.
* Language, sandbox and tasks were designed together; holdout templates reduce, but don't remove, the
  risk of overfitting. Dev reached 100% early, so later dev comparisons mostly measure efficiency.
* n per condition is 186 (dev) or 81 (holdout) task-runs, and CIs are wide near 100%. Single-task flips
  of ±1–2 are noise (temperature 0 is not deterministic on this provider).
* Cost differences below ~15% are confounded by prompt-cache hit variance (reported per run).
* The legacy baseline is a faithful-in-spirit re-implementation of conventional reflection tools, not the
  production ReflectorNet/Unity-MCP tool set.
* C++ executes plans only; TS/C++ have no regex timeout; C++ case folding is ASCII.
* No sibling repository was modified; no CI exists for this repo yet (CI needs the owner's approval).

## 6. Next experiments (prioritized)

__NEXT__
