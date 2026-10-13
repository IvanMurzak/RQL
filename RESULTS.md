# RESULTS — what was implemented and measured

All numbers below were produced in this session on a local machine (Apple M-series, 12 cores, 24 GB,
macOS; .NET 10.0.13, Node 26, Apple clang 21) and against the live DeepSeek API. Nothing is
extrapolated. Raw traces are in the gitignored `evaluation/runs/`; per-run summaries recomputed from those
traces are committed in `evaluation/baselines/*.json`; spend is in `evaluation/baselines/spend-ledger.json`.

## 1. Implemented

| Area | State |
|---|---|
| Spec | EBNF grammar, normative semantics (incl. §12 portability decisions), JSON schemas for QueryPlan / QueryResult / ObjectRef / CapabilityManifest, versioning + changelog |
| Golden vectors | 496 checks: 141 syntax→plan cases (each also a codec round-trip = 282), 30 negative syntax, 27 negative/edge plans, 138 execution cases = 157 steps incl. 16 mutation scenarios with follow-up state checks, on a portable graph (`fixtures/cross-runtime/graph.json`) materialized natively per runtime |
| .NET reference | parser, codec, live-CLR executor (compiled or reflection accessors), mutations, diagnostics, agent formats; libraries target netstandard2.1 + net8.0 |
| TypeScript | contracts, identical parser, live-object executor with descriptor registry |
| C++17 | own JSON, codec, provider-based reflection (`TypeRegistry`), executor; no parser by design |
| Sandbox | deterministic game world + app data (small 0.3k → huge 145k objects), tool host, snapshot differ, CLI |
| Tier A | conformance in 3 runtimes, benchmarks, task self-check (reference agent 89/89) |
| Tier B | 89-task catalog v1 (dev 62 / holdout 27 instances), hidden grader, DeepSeek runner, paired reports, spend ledger, regrade |
| Baseline for comparison | conventional reflection tools (find / get_object JSON dump / set / call / describe) on the same tasks + grader |

## 2. Tier A (deterministic)

Conformance (latest run): .NET **496/496** golden checks (compiled and reflection accessors), TypeScript **496/496** (+12 unit tests), C++ **322 passed, 0 failed, 33 skipped** (text-only cases; 80 unit tests, ASan/UBSan clean); reference agent **119/119** tasks; .NET unit tests 36/36.

Executor benchmark, Large world (14,437 GameObjects; `evaluation/reports/tierA-large.json`, median of 51
interleaved runs after 60 warmups, compiled accessors):

| Query | Median ms | p90 ms | Alloc KB | Visited | Members read | Output B |
|---|---:|---:|---:|---:|---:|---:|
| `objects \| count` | 0.872 | 0.915 | 180 | 14,437 | 0 | 53 |
| `objects \| where $ is Enemy and Health < 20 and Health > 0 \| count` | 0.503 | 0.507 | 298 | 14,437 | 4,818 | 51 |
| `objects \| where Components.any($ is LightComponent and Enabled == false) \| count` | 2.078 | 2.184 | 504 | 14,437 | 16,384 | 51 |
| `objects \| where $ is Enemy \| sort Level desc \| first 10 \| select $ref Name Level` | 0.513 | 0.521 | 287 | 14,437 | 3,902 | 462 |
| `objects \| where $ is Enemy and IsElite \| first 1 \| select $ref Name` | 0.037 | 0.039 | 30 | 1,267 | 3 | 111 |
| `objects \| where $ is Enemy \| group-by Faction \| sort count desc` | 0.694 | 0.703 | 277 | 14,437 | 3,882 | 132 |
| `objects \| where $ is Character \| select $ref Name Health Transform.Position` | 1.086 | 1.234 | 1327 | 14,437 | 16,204 | 3,450 |
| `world.Scenes \| get Objects \| recurse Children max-depth=16 \| count` | 0.964 | 2.120 | 2647 | 14,445 | 14,446 | 53 |
| `world \| recurse max-depth=8 \| count` | 7.064 | 10.329 | 9035 | 85,464 | 76,201 | 53 |
| `world.Scenes \| get Objects \| recurse Children max-depth=16 \| where Name starts-with "Chest" \| first 5 \| select $path` | 0.061 | 0.064 | 276 | 722 | 1,363 | 471 |
| `objects \| where Name contains "oak" or Name matches "^Torch_0[0-9]+$" \| count` | 1.223 | 1.306 | 183 | 14,437 | 28,477 | 51 |
| `objects \| get Components \| where $ is ScriptComponent and ScriptName == "Chest" and Properties.locked == true \| count` | 1.351 | 1.560 | 2104 | 41,404 | 21,795 | 51 |
| `app.Orders \| where Status == "Delivered" and Customer.Profile.Country == "DE" \| sum Total` | 0.084 | 0.085 | 60 | 2,030 | 3,085 | 65 |

Note: some queries are bimodal across process launches (e.g. `filter-nested-any` 0.84 ms / 275 KB in one launch and 2.3–2.5 ms / ~600 KB in others), from JIT/PGO decisions rather than code changes. Use the per-launch numbers as ±2.5× bands.

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
(off-peak rates applied by request time).

**Holdout hygiene, disclosed:** holdout templates/seeds were not used to design language features, with two exceptions.
(1) The exact-change safety rule in prompt v2 was motivated by the `neg-delete-scene` failure, first seen in the E3
holdout checkpoint. (2) The choice between the two final configurations (`final-tool-schema` vs `final-prompt-schema`)
was made on holdout. Treat the final holdout numbers as optimistic; a fresh task catalog v2 is the clean test
(see §6).

### 3.1 Experiment log (one factor at a time unless noted)

Development split (62 task instances × 3 repeats = 186 paired runs per row; tuned on):

| Run | Split | n | Success (95% CI) | Cost / success | Tokens / task | Tool calls | Tool errors | Tool-result chars | Median / p95 wall s | Cache hit |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline | dev | 186 | 92.5% (87.8–95.5) | $0.00051 | 11,419 | 4.41 | 0.12 | 4,453 | 4.69 / 12.17 | 87% |
| e1-exec-ergonomics | dev | 186 | 98.4% (95.4–99.4) | $0.00042 | 10,711 | 4.14 | 0.06 | 4,372 | 4.84 / 10.53 | 89% |
| e2-row-ergonomics | dev | 186 | 97.8% (94.6–99.2) | $0.00040 | 10,172 | 3.93 | 0.06 | 4,397 | 4.56 / 9.50 | 90% |
| e3-scope-escapes | dev | 186 | 100.0% (98.0–100.0) | $0.00039 | 10,132 | 3.87 | 0.08 | 4,527 | 5.01 / 9.50 | 89% |
| e4-prompt-v2 | dev | 186 | 100.0% (98.0–100.0) | $0.00038 | 8,157 | 3.40 | 0.04 | 3,708 | 3.79 / 7.53 | 84% |
| e5-json-format | dev | 186 | 100.0% (98.0–100.0) | $0.00034 | 8,836 | 3.44 | 0.05 | 4,412 | 4.31 / 8.05 | 89% |
| e6-schema-in-prompt | dev | 186 | 97.3% (93.9–98.8) | $0.00024 | 7,785 | 2.04 | 0.03 | 502 | 2.59 / 6.49 | 93% |
| e7-thinking-low | dev | 186 | 99.5% (97.0–99.9) | $0.00053 | 10,045 | 3.64 | 0.08 | 4,498 | 4.26 / 9.98 | 83% |
| e8-prompt-v3 | dev | 186 | 99.5% (97.0–99.9) | $0.00037 | 8,012 | 3.25 | 0.04 | 3,656 | 3.88 / 7.16 | 84% |
| e9-v3-schema-in-prompt | dev | 186 | 96.8% (93.1–98.5) | $0.00027 | 8,072 | 2.08 | 0.08 | 512 | 2.41 / 6.48 | 91% |
| e10-recurse-self-v4-schema | dev | 186 | 98.9% (96.2–99.7) | $0.00026 | 8,017 | 2.07 | 0.07 | 544 | 2.63 / 6.41 | 91% |
| e11-recurse-self-v4 | dev | 186 | 100.0% (98.0–100.0) | $0.00037 | 8,587 | 3.26 | 0.03 | 4,171 | 4.06 / 7.35 | 85% |
| legacy-tools | dev | 186 | 80.1% (73.8–85.2) | $0.00693 | 155,317 | 30.00 | 1.61 | 87,777 | 8.89 / 35.65 | 83% |

Holdout split (27 instances × 3 = 81 runs per row; checkpoints only):

| Run | Split | n | Success (95% CI) | Cost / success | Tokens / task | Tool calls | Tool errors | Tool-result chars | Median / p95 wall s | Cache hit |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| baseline | holdout | 81 | 81.5% (71.7–88.4) | $0.00064 | 12,428 | 5.22 | 0.59 | 5,079 | 5.20 / 13.60 | 87% |
| current-e3 | holdout | 81 | 92.6% (84.8–96.6) | $0.00048 | 10,309 | 4.14 | 0.40 | 5,026 | 4.07 / 13.05 | 86% |
| e4-prompt-v2 | holdout | 81 | 98.8% (93.3–99.8) | $0.00036 | 8,127 | 3.28 | 0.10 | 3,257 | 4.23 / 6.72 | 86% |
| final-tool-schema | holdout | 81 | 97.5% (91.4–99.3) | $0.00038 | 8,257 | 3.23 | 0.12 | 3,021 | 3.99 / 8.16 | 86% |
| final-prompt-schema | holdout | 81 | 100.0% (95.5–100.0) | $0.00023 | 7,444 | 1.91 | 0.02 | 262 | 3.17 / 4.96 | 92% |
| pro-tool-schema | holdout | 81 | 92.6% (84.8–96.6) | $0.00126 | 7,716 | 3.07 | 0.17 | 3,046 | 5.22 / 8.99 | 90% |
| legacy-tools | holdout | 81 | 91.4% (83.2–95.8) | $0.00519 | 125,566 | 33.98 | 1.01 | 74,739 | 9.25 / 35.54 | 83% |

Run names: `baseline` = first evaluated commit (`8b53dd4`, run from a separate git worktree for the holdout);
`current-e3` = code after E3; `final-*` = prompt v4 + all language changes; `pro-*` = same as
`final-tool-schema` with `deepseek-v4-pro`. `final-prompt-schema` is the recommended configuration
(`evaluation/configs/final.json`).

**Headline (holdout; see the hygiene note above):** first iteration → final configuration: success **81.5% → 100%**
(95% CI 95.5–100), cost per successful task **$0.00064 → $0.00023 (−64%)**, tokens per task **12.4k → 7.4k
(−40%)**, tool calls **5.2 → 1.9**, median wall time **5.2 s → 3.2 s**, p95 **13.6 s → 5.0 s**.

### 3.2 What each change was and why (failure evidence → change → effect)

| # | Failure evidence (traces) | Change | Effect (paired, dev unless noted) |
|---|---|---|---|
| E1 | Agents filtered `objects` (GameObjects) for `$ is LightComponent` / `$ is Quest` and silently got 0; `$path` lost after `select`; map entries needed `.Value`; depth questions ran out of steps | `no-match` warning naming where a type is reachable; `$name` row columns shadow metas; map-entry member delegation; `$depth` meta | 92.5% → 98.4% (+13 / −2 flips), cost/success −18%, tool errors −50% |
| E2 | Agents used `select` as navigation (`world \| select A.B \| get $ \| …`); warnings said `no member 'Item' on ` (empty) | single-column rows stand for their value; row-specific `unknown-member` text; `$ref` of a row is its value's ref | 97.8% (noise vs E1: +3 / −5 flips; kept for ergonomics), tokens −5% |
| E3 | Outer item unreachable inside `.count(...)`; regex escapes `\\[` were syntax errors | lexical scope chain in method arguments; verbatim unknown escapes | 100% (+4 / −0) |
| — | Holdout checkpoint | baseline code vs E3 code | **81.5% → 92.6%**, cost/success −25%, tokens −17%, tool calls −21% |
| E4 | Over-verification loops; destructive approximation on "delete the scene" (6/6 holdout runs destroyed 11k–33k fields) | prompt v2: navigation tips, "submit as soon as…", exact-change-only safety rule | dev: =100%, tokens −19%, wall −24%; **holdout 98.8%** (+5 / −0), destructive runs 6/6 → 1/6 |
| E5 | — (ablation) | JSON results instead of compact table | =100%; tokens +8%, tool-result chars +19%, wall +14% → table kept (ADR-0004) |
| E6 | — (ablation) | schema in system prompt instead of `rql_schema` tool | cost/success −37%, tool calls −40%, wall −32%; success 97.3% (5 failures on one template: `X.Dependencies \| recurse Dependencies` drops the start items) |
| E7 | — (ablation) | thinking mode (`reasoning_effort=low`) | 99.5%, cost/success +40%, tokens +23% → rejected |
| E8/E9 | E6 failure pattern | prompt v3 explains recurse excludes inputs | no fix (E9 still 6 failures on the template) |
| E10/E11 | agents assume `recurse` keeps its input items (jq/XPath descendant-or-self intuition) | **semantic change**: `recurse` includes inputs by default (`include-self=false`), prompt v4; bundled with schema method dedupe/descriptions + static typo warning | with schema in prompt: 96.8% → 98.9% (template 0/6 → 4/6; remaining failures explicitly write `include-self=false`); tool schema: 100% |


### 3.3 RQL vs conventional reflection tools

Same tasks, same grader, same model and budgets; only the tool surface differs (`Toolset=legacy`:
`find_objects`, `get_object(path, depth)` JSON dump, `describe_type`, `set_value`, `call_method`).

| Split | Tool surface | Success | Cost / success | Tokens / task | Tool calls / task | Tool-result chars / task | Median / p95 wall s |
|---|---|---:|---:|---:|---:|---:|---:|
| dev (n=186) | legacy reflection tools | 80.1% | $0.00693 | 155,317 | 30.0 | 87,777 | 8.89 / 35.65 |
| dev (n=186) | RQL (E4 config) | 100% | $0.00038 | 8,157 | 3.4 | 3,708 | 3.79 / 7.53 |
| holdout (n=81) | legacy reflection tools | 91.4% | $0.00519 | 125,566 | 34.0 | 74,739 | 9.25 / 35.54 |
| holdout (n=81) | RQL final (prompt schema) | 100% | $0.00023 | 7,444 | 1.9 | 262 | 3.17 / 4.96 |

On paired holdout runs RQL gained 7 tasks and lost 0. Cost per successful task is **−96%** (22× cheaper),
and the median task takes −66% less time. Legacy failures are mostly step-budget exhaustion on
"search everything" tasks (scale: 0/12 on dev; `agg-paid-revenue`: 95–175 tool calls). Typical
legacy behaviour: walking `Children[i].Components[0]` one call at a time.

### 3.4 Clean generalization check — task catalog v2 (`holdout2`)

The holdout caveats in §3 motivated a fresh test. 15 new templates (`Tasks/CatalogV2.cs`, seeds 31/32,
30 instances) were written **after** all language and prompt work was frozen, validated only by the
scripted reference agent (30/30), then each condition was run exactly once (×3 repeats). There was no iteration.

| Run | Split | n | Success (95% CI) | Cost / success | Tokens / task | Tool calls | Tool errors | Tool-result chars | Median / p95 wall s | Cache hit |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| final-prompt-schema | holdout2 | 90 | 100.0% (95.9–100.0) | $0.00024 | 7,817 | 1.91 | 0.06 | 291 | 3.01 / 6.34 | 92% |
| final-tool-schema | holdout2 | 90 | 100.0% (95.9–100.0) | $0.00033 | 7,720 | 3.00 | 0.06 | 4,391 | 3.64 / 7.17 | 85% |
| legacy-tools | holdout2 | 90 | 66.7% (56.4–75.5) | $0.00956 | 169,190 | 36.86 | 2.10 | 102,045 | 9.60 / 37.44 | 82% |

**Result:** the frozen final configurations solved **90/90 = 100%** (95% CI 95.9–100) in both schema
modes. The conventional reflection-tool baseline solved **66.7%** at **40× the cost per success**
($0.00956 vs $0.00024), with 37 vs 1.9 tool calls and 3.2× the median wall time. Its failures are step-budget
exhaustion on search/aggregate tasks (e.g. `v2-agg-avg-lines`: 155–233 tool calls).

### 3.5 Independent code review and hardening

A separate review agent (report-only, findings confirmed by running them) found 14 confirmed defects in
the .NET reference. Examples: a parser stack overflow from deeply nested text (`((((…))))`, which crashes
the host and can't be caught); writes through struct sub-members that bypassed read-only fields,
`[RqlReadOnly]` and host write policy (a security bug); collection methods, regex and the `ref()` fallback search
escaping `maxVisited`/`timeoutMs` (24–52 s instead of the 2 s budget); raw .NET exceptions on mixed
row/value streams and NaN group keys; `set` on value copies reporting success without effect; and a
request able to raise the host's mutation-target limit. **All were fixed** in .NET and mirrored in TypeScript
and C++. Twenty new golden checks lock them in (496 total), plus 11 .NET regression tests. The
spec gained nesting/operator limits, the `internal-error` code (a last-resort catch so hosts never see raw
exceptions), value-copy target rejection, and a host hard ceiling for mutation targets.

Regression check: the final configuration rerun on dev with the hardened build scored 98.4% (183/186) vs
98.9% before, within noise. The only failures are the known `include-self` misreading on one template.
Tokens, tool calls and cost were unchanged.

## 4. Spend

Total recorded DeepSeek spend for the whole session: **$3.48** of the $25 global cap (balance before
start: $29.46), over 34 ledger entries (`evaluation/baselines/spend-ledger.json`). Costs are computed from
provider-reported token usage × the price snapshot. One entry is a known conservative double count (~$0.035; see the ledger).
Cross-check against the provider's account balance endpoint at the end of the session: **$3.10 actually charged**
($29.46 → $26.36) vs $3.43 in the ledger, so the ledger, and every cost figure in this report, is a conservative upper bound (~10% high).

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

1. **Integrate with ReflectorNet/Unity-MCP** (owner approval): an adapter `TypeModel` policy on ReflectorNet's
   reflection rules; rerun Tier B on a real Unity scene, comparing against the production ReflectorNet tools rather than the re-implemented legacy baseline.
2. **Unreal provider** for the C++ engine plus the sidecar plan forwarding (ADR-0003).
3. **Second model family** for external validity (needs owner approval for another provider's spend).
4. **Task catalog v3** with harder multi-step tasks; catalog v2 is now spent (§3.4) and the final configs saturate it.
5. **Destructive-method policy** (`Destructive` flag requiring a prior dry-run) for the residual
   "approximate an impossible change" risk: 1/6 runs with tool schema, 0/6 with prompt schema.
6. Investigate the bimodal JIT behaviour and the slower allocation-free recurse variant (low priority:
   all queries are < 15 ms on 145k objects).
