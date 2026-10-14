# RQL in Unity — experiment

Local experiment: RQL tools inside the Unity Editor, served by Unity-MCP, so the AI Game Dev App's agent can use them.
Nothing here is published; the package stays in this private repository.

| Folder | What |
|---|---|
| `com.ivanmurzak.unity.mcp.rql/` | UPM package (depends on `com.ivanmurzak.unity.mcp` 0.93.4): RQL DLLs in `Plugins/`, the tools, Unity adapters, EditMode tests |
| `RqlTestProject/` | Unity 6000.6 test project: references the package via `file:`, sample scripts (`Health`, `Enemy`, `Spawner`), scene builder, smoke queries |
| `build-rql-dlls.sh` | Rebuild the RQL DLLs (netstandard2.1) into the package after changing `dotnet/Rql.*` |

## Tools the agent gets

| Tool | Does |
|---|---|
| `rql-query(query)` | read-only RQL over the live scene and assets, compact table result |
| `rql-mutate(query, dry_run)` | `set` / `call`: all-or-nothing, undoable (Ctrl+Z), dry-run receipt |
| `rql-schema(type?)` | sources, component types present in the scene, members of one type |

Unity-MCP discovers them like any extension tool. The App lists Unity tools live (`tool-list`), so no App or Unity-MCP
change is needed: the catalog shows the one-line blurb, and the agent loads the full description (the RQL language
guide, adapted from the evaluated prompt v4) before first use.

## Structure (mirrors Unity-MCP's converters)

```
com.ivanmurzak.unity.mcp.rql/Editor/
  Scripts/RqlUnity.cs                 engine: Undo / SetDirty hooks, module discovery (assembly scan)
  Scripts/RqlUnity.Refs[.pre-Unity.6.5].cs   Type#id resolution per Unity version
  Scripts/Tool_Rql.cs, RqlGuide.cs, RqlUnitySchema.cs   the MCP tools and their texts
  Scripts/Modules/UnityCoreModule.cs  sources, functions, core adapters, type blacklist
  Scripts/Adapters/<Group>/UnityEngine_<Type>_Adapter[.part][.pre-Unity.6.5].cs   one adapter per file
  Modules/ParticleSystem/, Physics/   OPTIONAL modules: own asmdef, compile only with com.unity.modules.particlesystem / .physics
com.ivanmurzak.unity.mcp.rql/Tests/
  Editor/Adapters/<Group>/<Adapter>Tests.cs   one test file per adapter (Editor/ParticleSystem, Editor/Physics: per module)
  Runtime/RqlTestBehaviour.cs         test script with the member kinds adapters handle (runtime asmdef: components can't be editor types)
```

- **Version split:** `X.cs` targets the current Unity (`#if UNITY_6000_5_OR_NEWER`); `X.pre-Unity.6.5.cs` targets everything
  before (`#if !…`). When the next Unity changes an API, today's `X.cs` becomes `X.pre-Unity.<new>.cs` and a new `X.cs`
  targets the newest version.
- **Modules (`IRqlModule`, RQL's counterpart of Unity-MCP's `IReflectorModule`):** `Order` plus `Configure(ctx)` registering
  adapters, exclusions, sources and functions. `RqlUnity` scans the loaded assemblies, so any installed package that
  contains a module contributes it. Failures are isolated and reported in the Console.
- **Optional Unity packages:** put the module in its own asmdef with `versionDefines` on the package and that define in
  `defineConstraints`, as `Modules/ParticleSystem/` does. Disabling the package removes the assembly and with it the
  adapters. This is verified: with `com.unity.modules.particlesystem` removed the project compiles and the module is
  gone; re-added, it is back.
- **Extension packages** (like the Unity-AI-* extensions): a package with an Editor asmdef that references
  `com.IvanMurzak.Unity.MCP.Rql.Editor` and contains an `IRqlModule`.
- **Adapters reuse each other only through the kernel.** For example, `UnityEngine_MinMaxGradient_Adapter` accepts a
  color, two colors or gradient keys via `kernel.TryCoerce(value, typeof(Color) / typeof(Gradient))` and encodes via
  `context.EncodeValue`. It never references the Color or Gradient adapter. Recursion protection, the write policy and
  error paths (`[1].color: …`) are the kernel's job.

Adapters today:
- **Identity and liveness:** `$ref` ids come from `EntityId`; destroyed objects read as null.
- **Members:** private `[SerializeField]` fields are members, `[Obsolete]` members are hidden.
- **GameObject:** `components` / `children` / `active` members.
- **Renderer:** `material(s)` and `mesh` are explicit-only; `sharedMaterials` writes are assigned back.
- **Literals:** vectors and colors as `"(x, y, z)"`, `[x, y, z]`, `{x: …}` or `"#rrggbb"`; gradients as
  `[[color, time], …]`.
- **Materials:** shader properties as `properties["_Color"]`.
- **Particle systems:** module handles write through; `MinMaxCurve` takes a number or `[min, max]`, `MinMaxGradient`
  a color, two colors or a gradient.

## Run it

1. Open `RqlTestProject` in Unity 6000.6 (Hub → Add). On first open Unity-MCP's resolver downloads its NuGet DLLs
   (`Assets/Plugins/NuGet`, gitignored) and recompiles once.
2. Menu **Tools → RQL → Build Test Scene** (deterministic scene: player, 6 enemies, lamps, spawner with an empty slot,
   materials). **Tools → RQL → Smoke Queries** logs sample tool outputs to the Console.
3. Point the AI Game Dev App at this project; the agent's tool catalog then contains `rql-query`, `rql-mutate`,
   `rql-schema` next to the regular Unity-MCP tools (disable those in the Unity-MCP window for a pure comparison).

Batch mode (from this folder; `U` = the Unity 6000.6 binary):

```bash
"$U" -batchmode -nographics -projectPath "$PWD/RqlTestProject" -rqlWaitReady -logFile -   # restore NuGet + compile, exit 0 when ready
"$U" -batchmode -nographics -projectPath "$PWD/RqlTestProject" -runTests -testPlatform EditMode \
     -assemblyNames "com.IvanMurzak.Unity.MCP.Rql.Editor.Tests;com.IvanMurzak.Unity.MCP.Rql.Editor.ParticleSystem.Tests;com.IvanMurzak.Unity.MCP.Rql.Editor.Physics.Tests" \
     -testResults results.xml -logFile -
"$U" -batchmode -nographics -quit -projectPath "$PWD/RqlTestProject" -executeMethod RqlTest.EditorTools.RqlSmoke.Run -logFile -
```

## Agent comparison inside Unity (ReflectorNet tools vs RQL tools)

`RqlTestProject/Assets/Editor/Eval/` runs the same DeepSeek agent on 16 Editor tasks (material properties, lights,
creating hierarchies, adding components, moving / reparenting, bulk script-field edits, two questions), once with
Unity-MCP's ReflectorNet-based tools (`gameobject-*`, `object-*`, `assets-*`, `scene-*`, `type-get-json-schema`) and once
with the three RQL tools. Equal conditions: one system prompt, tool names / descriptions / schemas taken from Unity-MCP's
own tool list and executed through its `RunCallTool` pipeline, the scene rebuilt before every run. A run passes only if the
task check holds AND a scene / material snapshot diff shows no change the task did not ask for.

```bash
export RQL_EVAL_API_KEY_FILE=<path to the agent-testing key>
"$U" -batchmode -nographics -projectPath "$PWD/RqlTestProject" -executeMethod RqlTest.Eval.UnityEval.Run \
     -evalRepeats 3 -evalBudget 2 -logFile RqlTestProject/Logs/eval.log    # no -quit; optional -evalTasks a,b -evalConditions rql
```

Output: `../evaluation/runs/<ts>-unity-inproc/` (`config.json`, `results.jsonl` with full transcripts, `summary.md`); spend
goes into `../evaluation/baselines/spend-ledger.json`.

## Known limits

- **Unity versions:** compiled and tested on Unity 6000.6.4f1 (macOS) only. Code paths for Unity < 6.5 (instance ids
  instead of EntityId) are in place but not compiled here.
- **RQL runtimes:** RQL features added for this experiment exist in the .NET runtime only so far:
  `TypeModel.TreatAsObject`, host functions as query sources, the kernel context for adapters, and modules.
- **Edit mode only:** the tools run in the Editor; Play Mode objects work but writes there are not undoable.
