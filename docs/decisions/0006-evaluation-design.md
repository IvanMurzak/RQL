# ADR-0006: Evaluation through a hidden-oracle sandbox and paired one-factor experiments

**Status:** accepted · 2026-10-13

**Decision.** A deterministic generated sandbox (game world + app data) with versioned seeded task
templates; LINQ oracles and a state-snapshot grader hidden from the model; scripted reference solutions
must solve 100% (validates solvability and cross-checks the executor); dev/holdout split; one factor per
experiment; paired comparison by (task, repeat); provider-reported usage; spend ledger with a cap.

**Consequences.** Language changes are driven by observed agent failures (see `RESULTS.md`), and every
semantic change is mirrored into golden vectors so all runtimes inherit it.
