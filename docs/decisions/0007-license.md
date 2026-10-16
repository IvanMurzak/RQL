# ADR-0007: License — PolyForm Shield 1.0.0

**Status:** accepted (owner decision, 2026-10-14)

RQL is licensed under **PolyForm Shield 1.0.0** (`LICENSE.md`): anyone may use it, including in commercial products
such as games built with Unity-MCP, except to provide a product that competes with the licensor's products. The source
repository is public since 2026-10-15 (owner decision, so the chain train can clone it); the .NET runtime is published to NuGet as `com.IvanMurzak.Rql` (`.github/workflows/release.yml`).

Considered: Apache-2.0 / MIT (as ReflectorNet and MCP-Plugin-dotnet) — rejected, they allow competitors to ship RQL in
competing products; Elastic License 2.0 — only forbids offering it as a managed service; Business Source License 1.1 —
workable but needs a custom use grant and a change date; PolyForm Noncommercial — would block commercial studios using
Unity-MCP.

Packaging note: NuGet's toolset does not recognise the SPDX id `PolyForm-Shield-1.0.0` (NU5124), so the package carries
the license as a file (`<license type="file">LICENSE.md</license>`).

Downstream: Unity-MCP and McpPlugin (Apache-2.0) depend on the package; their users accept PolyForm Shield for RQL.
