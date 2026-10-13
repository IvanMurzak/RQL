# ADR-0005: Compiled accessors by default, reflection mode for AOT

**Status:** accepted · 2026-10-13 (evidence: Tier A benchmark)

**Decision.** `AccessorMode.Compiled` (expression-tree delegates) is the default; `AccessorMode.Reflection`
is offered for AOT/IL2CPP targets.

**Evidence.** Interleaved benchmark on the Large world (14k objects, .NET 10): compiled is only 3–10% faster
than `PropertyInfo.GetValue`; allocations are identical once warmed up (an early measurement that showed 6×
more allocation for compiled was a JIT-tiering artefact of a short warmup). The AOT-safe mode costs little.
