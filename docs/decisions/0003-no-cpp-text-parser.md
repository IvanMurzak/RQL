# ADR-0003: No text parser in C++ (for now)

**Status:** accepted · 2026-10-13

**Context.** The planned Unreal integration runs a .NET MCP sidecar (MCP-Plugin-dotnet) that already
hosts the agent tools; the live objects are in the Unreal process.

**Decision.** The sidecar parses text with the .NET parser and ships the plan over IPC; the C++
executor validates and executes plans. Text-only fixtures are reported as "skipped: needs parser".

**Revisit when** a C++-only host without a sidecar needs RQL text input.
