# ADR-0001: One multi-language monorepo, zero heavy dependencies

**Status:** accepted · 2026-10-13

**Context.** RQL needs C#, TypeScript and C++ implementations that must not drift semantically.

**Decision.** One repository: `spec/` + `fixtures/` are shared; each runtime lives in its own folder with
its native build (dotnet SDK / npm workspaces / CMake). Dependencies: .NET — `System.Text.Json`
(netstandard2.1 only; built into net8+), xunit for tests; TS — `typescript` (dev only); C++ — none
(own small JSON library). Libraries target `netstandard2.1;net8.0` so Unity/ReflectorNet can consume them.

**Consequences.** One commit changes spec + all runtimes + goldens atomically; `scripts/test-all.sh`
gates everything. C++ carries ~600 lines of JSON code instead of a third-party dependency, which keeps
the future Unreal integration free of dependency conflicts.
