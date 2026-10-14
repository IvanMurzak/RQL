# Agent SDK probe — Unity-MCP tool presentations through a real harness

Short live runs that go the way the AI Game Dev app goes: Claude Agent SDK (spawned Claude Code, `ENABLE_TOOL_SEARCH`
as the app forces it) → MCP client → GameDev-MCP-Server → Unity-MCP (`experiment/rql`) in a batch-mode Editor. They
check that providers and the harness accept the listed tool schemas in each presentation (`toolFormat` Json / RqlText /
RqlLean × `toolTextEscaping` Minimal / Ascii / Json) and that text with `<`, `+`, `&`, `'`, `"`, `—` and non-ASCII
survives tool results and arguments.

- `run.mjs` — one model, four tasks (read the probe name back, rename it, set a light, run C# via script-execute);
  grades through its own MCP client; restores the scene afterwards. The agent project and Claude config live under
  `~/Library/Caches/rql-agentsdk/` (outside every repo: no CLAUDE.md / .claude / .git reaches the model).
- `variation.sh` — one Unity session per presentation (written into the project's
  `UserSettings/AI-Game-Developer-Config.json`, as a host app would), then `run.mjs` per model spec.
- Unity side: `unity/RqlTestProject/Assets/Editor/Eval/McpLive.cs` (copy into the project under test).

Setup used (2026-10-14): MCP server `gamedev-mcp-server port=58090 client-transport=streamableHttp auth=none` (the
binary Unity-MCP downloads into `Library/mcp-server`); Luna through a LOCAL AI-Game-Dev-Server (native uvicorn on
:58000 with its own Postgres :55432 / Redis :56379, `SEED_LOCAL_DEV`, `OPENAI_ENABLED`) — the app's proxy path,
Anthropic Messages → OpenAI; Haiku through DeepInfra's Anthropic endpoint (no Anthropic inference key; the server routes
Claude only to Anthropic). Results: `results.jsonl` (matrix), `diag.jsonl` (tool I/O of the follow-up runs).
Report: `../reports/unity-mcp-agent-sdk-and-player-runtime.md`.
