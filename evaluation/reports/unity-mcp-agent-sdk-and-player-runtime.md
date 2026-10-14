# Unity-MCP: Agent SDK probe of the tool presentations, RQL in built players (2026-10-14)

## 1. Tool schemas and escaping through a real harness (Claude Agent SDK)

Path: Agent SDK → MCP → GameDev-MCP-Server 9.2.11 → Unity-MCP `experiment/rql` (7 enabled tools) in Unity 6000.6.4f1.
Models: `gpt-6-luna` via a local AI-Game-Dev-Server (the app's proxy: Anthropic Messages → OpenAI) and
`anthropic/claude-haiku-4-5` via DeepInfra's Anthropic endpoint. Tool search on (as the app forces it: schemas load
on demand) and off (every schema, incl. the ReflectorNet `$defs` of script-execute / screenshot-isolated / tests-run,
goes with the first request). 4 tasks per run; setup and harness in `evaluation/agent-sdk/`.

| Presentation | Luna (search on) | Haiku (search on) | Luna (all schemas) | Haiku (all schemas) |
|---|---|---|---|---|
| Json | 4/4 | 4/4 | 4/4 | 4/4 |
| RqlText + Minimal | 4/4 | 4/4 | 4/4 | 4/4 |
| RqlText + Ascii | 4/4 | 3/4 | | |
| RqlText + Json | 4/4 | 4/4 | | |
| RqlLean + Minimal | 4/4 | 3/4 | 4/4 | 4/4 |
| RqlLean + Ascii | 4/4 | 4/4 | | |
| RqlLean + Json | 4/4 | 3/4 | | |

- **No API or harness errors in any of the 80 tasks**: no provider rejected a schema (incl. `$defs` keys such as
  `System.Collections.Generic.List(System.Collections.Generic.List(AIGD.PathPatch))`), no tool call failed for format
  reasons, the probe name `Crate #7 <A+B> — café "x"` was read back exactly and the rename to `Box <1+1> naïve & 'ok' — ü`
  arrived intact in every passing run.
- The 3 failures (Haiku, `answer-tricky-name`) were Haiku answering through `script-execute` with a `void` method that
  only `Debug.Log`s the name: the tool returns `value: Success`, the agent never sees the log and guessed. Repeated with
  tool I/O capture (`diag.jsonl`): 7/8 pass; when the C# returns the string the name arrives exactly
  (`value: "Crate #7 <A+B> — café \"x\""`). Once Haiku itself HTML-escaped its C# argument
  (`FindObjectsByType&lt;GameObject&gt;`), got a compile error and corrected it — model-side, not our output.
- Possible Unity-MCP improvement (not done): `script-execute` could return the Debug.Log lines of the run.
- Note: the escaping setting applies to the compact JSON-result text of the presentation; RQL's own table output
  (rql-query / rql-mutate) has its own quoting (AgentFormat) and is not switched by it.
- Cost (SDK-reported, list prices): ≈ $3.3 for the matrix, ≈ $0.5 for the follow-up.

## 2. RQL inside built players (no Editor)

Project `unity/RqlPlayerProject`: RQL + System.Text.Json DLLs, a smoke that builds a scene and runs 15 checks
(queries, Vector3/Color literals, set field / vector, `call` an `[RqlCallable]` method, ParseValue, syntax error,
whole-component encoding, two members reached ONLY by reflection) in both accessor modes (Reflection, Compiled).

| Player | Stripping | link.xml | Result |
|---|---|---|---|
| macOS Mono, 6000.6.4f1 | Minimal | — (first run) | all pass (pre reflection-only checks) |
| macOS Mono, 6000.6.4f1 | Minimal | CoreModule | 30/30 (after the Scene adapter fix below) |
| WebGL IL2CPP, 6000.6.4f1 (headless Chrome) | Minimal | — | 28/30: `Camera.fieldOfView` stripped |
| WebGL IL2CPP, 6000.6.4f1 | High | — | 28/30: same |
| WebGL IL2CPP, 6000.6.4f1 | High | CoreModule | 30/30 |
| Windows IL2CPP, 6000.0.34f1 | Minimal | — | 28/30: same |
| Windows IL2CPP, 6000.0.34f1 | High | CoreModule | 30/30 |
| Windows IL2CPP, 2022.3.61f1 | Minimal | — | 28/30: same |
| Windows IL2CPP, 2022.3.61f1 | High | CoreModule | 30/30 |
| Editor Play Mode, 6000.6.4f1, no RQL_RUNTIME define | — | — | 30/30 |

Findings:
- **RQL runs in players**, Mono and IL2CPP (WebGL and native Windows), Unity 2022.3 / 6000.0 / 6000.6. The Compiled
  accessor mode also works under IL2CPP (System.Linq.Expressions falls back to its interpreter); Reflection is the
  safe default there.
- **IL2CPP strips engine members that only reflection reaches**, at every stripping level (engine code is always
  stripped): `Camera.fieldOfView` disappeared; user script members were kept. Fix: `link.xml` preserving
  `UnityEngine.CoreModule` — costs ≈ 5.5 MB of WebAssembly (15.4 → 20.9 MB at High). A finer link.xml (only the engine
  types of the project's components) would cost less.
- **A reflection read can crash a player natively**: encoding whole components reached `Scene.loadableSceneId`, which
  crashed the macOS Mono player (SIGSEGV, not catchable) once CoreModule was fully preserved. Unity-MCP's Editor
  adapters already whitelist Scene members; a runtime RQL must reuse those adapters (move them to a runtime assembly)
  and keep a deny list for native getters.
- **Optional runtime is possible with `defineConstraints`**: the RQL DLLs (PluginImporter) and the runtime asmdef
  carry `UNITY_EDITOR || RQL_RUNTIME`. Without the define the macOS player contains no RQL DLL or RQL code
  (0 files, verified in `Contents/Resources/Data/Managed`) and Editor Play Mode still has everything (30/30); with
  `RQL_RUNTIME` in the player's scripting defines the player has them and passes. The same pattern applies to
  Unity-MCP's own DLLs (ReflectorNet, McpPlugin, SignalR) and its Runtime assembly: a "runtime support" toggle in the
  Unity-MCP settings would just add / remove the define.
- The Windows runs used the assets before the Scene adapter / asmdef change (IL2CPP did not hit the Scene crash).
