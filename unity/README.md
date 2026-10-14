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

Sources: `objects`, `roots`, `scenes`, `selection`; functions `unity.assets("t:Material")`, `unity.asset(path)`.
Unity adapters (`Editor/Scripts/UnityAdapters.cs`):
- **Ids and liveness:** `$ref` ids come from `EntityId`; destroyed objects read as null.
- **Members:** private `[SerializeField]` fields are members, `[Obsolete]` members are hidden.
- **GameObject:** `components` / `children` / `active` members.
- **Renderer:** `material(s)` and `mesh` are explicit-only; `sharedMaterials` writes are assigned back.
- **Literals:** vectors and colors are read and written as `"(x, y, z)"`, `[x, y, z]`, `{x: …}` or `"#rrggbb"`.
- **Materials:** shader properties as `properties["_Color"]`.
- **Writes:** Undo before each write, SetDirty after.

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
     -assemblyNames com.IvanMurzak.Unity.MCP.Rql.Editor.Tests -testResults results.xml -logFile -
"$U" -batchmode -nographics -quit -projectPath "$PWD/RqlTestProject" -executeMethod RqlTest.EditorTools.RqlSmoke.Run -logFile -
```

## Known limits

- **Unity versions:** compiled and tested on Unity 6000.6.4f1 (macOS) only. Code paths for Unity < 6.5 (instance ids
  instead of EntityId) are in place but not compiled here.
- **RQL runtimes:** RQL features added for this experiment (`TypeModel.TreatAsObject`, host functions as query sources)
  exist in the .NET runtime only so far.
- **Edit mode only:** the tools run in the Editor; Play Mode objects work but writes there are not undoable.
