# Implementation roadmap

| Milestone | Status | Evidence |
|---|---|---|
| M0 inventory, decisions, repo | done | ADRs 0001–0007, `AGENTS.md`, this repo |
| M1 protocol in 3 languages | done | schemas in `spec/`; codecs in .NET/TS/C++; shared syntax/negative vectors pass everywhere |
| M2 .NET vertical slice | done | parser, executor over live CLR objects, sandbox, tool host |
| M3 measurement + DeepSeek evals | done | Tier A bench, Tier B runner, baseline + 9 experiments, reports, spend ledger |
| M4 safe mutations + experiments | done | `set`/`call`, dry-run, receipts, all-or-nothing, limits; mutation tasks graded by state diff |
| M5 TS/C++ parity | done for all fixture-covered features | `docs/feature-matrix.md` |
| Type adapters (spec §13) | done in .NET, TS, C++ (C++: no non-public field inclusion) | `docs/type-adapters.md`, `fixtures/adapters/cases.json` |
| M6 integration design | design only | `docs/architecture.md` §Integration path; no sibling repo modified |

## Next (prioritized)

1. **Unity adapter pack** (spec §13 adapters mirroring Unity-MCP's converters, see `docs/type-adapters.md`) and a
   Unity-MCP tool set; rerun the eval on a real Unity scene dump.
2. **Adapter-defined methods** (callable without `[RqlCallable]` on engine types, e.g. `Material.SetFloat`), and
   per-instance dynamic members.
3. **Unreal provider** for the C++ engine (`UClass`/`FProperty`, `TArray`/`TMap` adapters, `IsValid`).
4. **Static binding pass**: type-check member paths against declared types before execution (catch typos
   with zero items in the stream; today `unknown-member` needs at least one evaluated item).
5. **Destructive-method policy** (`[RqlCallable(Destructive = true)]` requiring a dry-run first).
6. **Plan cache** (parse/compile once per text) — not needed at current latencies (<15 ms on 145k objects).
7. Broaden the evaluation: second model family, larger task catalog v2 with fresh holdout templates.
