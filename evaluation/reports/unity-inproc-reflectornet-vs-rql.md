# Unity in-Editor comparison — 20261014-092855-unity-inproc

model `deepseek-flash`, reasoning `none`, temperature 0, maxSteps 15, repeats 3, Unity 6000.6.4f1, Unity-MCP 0.93.4; total cost $0.2045.

| Metric | reflectornet | rql |
|---|---|---|
| Runs | 48 | 48 |
| Success | 45/48 (94%) | 48/48 (100%) |
| Task check passed (ignoring side effects) | 46/48 | 48/48 |
| Runs with unintended changes | 0 | 0 |
| Mean model turns | 5.9 | 3.7 |
| Mean tool calls / errors | 14.2 / 1.8 | 3.5 / 0.0 |
| Mean prompt tokens | 118,881 | 11,073 |
| Mean completion tokens | 1,443 | 273 |
| Mean tool result chars | 11,495 | 663 |
| Mean cost per task (USD) | 0.00363 | 0.00063 |
| Mean wall time (s) | 11.3 | 4.6 |
| Mean model time (s) | 11.3 | 4.6 |
| Mean tool time (ms) | 17.1 | 20.4 |
| Tool definitions (chars) | 68865 | 9166 |

## Per task (passes / runs, mean tool calls, mean cost)

| Task | Category | reflectornet | rql |
|---|---|---|---|
| material-shader-floats | material | 2/3, 5.7 calls, $0.0023 | 3/3, 4.0 calls, $0.0008 |
| material-color-alpha | material | 3/3, 7.0 calls, $0.0024 | 3/3, 5.0 calls, $0.0009 |
| light-color-intensity | component | 3/3, 2.3 calls, $0.0012 | 3/3, 1.7 calls, $0.0004 |
| lights-bulk-halve | component | 3/3, 17.7 calls, $0.0041 | 3/3, 2.0 calls, $0.0004 |
| create-hierarchy | create | 3/3, 3.0 calls, $0.0008 | 3/3, 5.0 calls, $0.0007 |
| add-rigidbody-by-kind | create | 3/3, 28.0 calls, $0.0059 | 3/3, 4.0 calls, $0.0007 |
| move-and-rotate | transform | 2/3, 2.7 calls, $0.0012 | 3/3, 2.0 calls, $0.0005 |
| reparent-keep-world | transform | 3/3, 3.0 calls, $0.0008 | 3/3, 3.0 calls, $0.0005 |
| script-field-bulk | component | 3/3, 21.0 calls, $0.0052 | 3/3, 3.0 calls, $0.0006 |
| deactivate-no-target | gameobject | 3/3, 16.7 calls, $0.0044 | 3/3, 2.3 calls, $0.0004 |
| rename-strongest | gameobject | 3/3, 18.0 calls, $0.0039 | 3/3, 2.0 calls, $0.0005 |
| spawn-light | create | 3/3, 5.0 calls, $0.0022 | 3/3, 5.0 calls, $0.0008 |
| assign-material | material | 3/3, 5.0 calls, $0.0017 | 3/3, 3.0 calls, $0.0005 |
| combined-archers | component | 2/3, 32.7 calls, $0.0088 | 3/3, 10.3 calls, $0.0015 |
| answer-low-health | query | 3/3, 26.0 calls, $0.0053 | 3/3, 1.3 calls, $0.0004 |
| answer-gold-users | query | 3/3, 33.0 calls, $0.0081 | 3/3, 2.0 calls, $0.0004 |

## Failures

- `reflectornet` material-shader-floats r1: answered — failed: Red._Glossiness == 0.8; Red._Metallic == 0.3
- `reflectornet` combined-archers r2: max-steps — ok
- `reflectornet` move-and-rotate r3: answered — failed: rotation (0, 90, 0)

## Setup and reading

- Harness: `unity/RqlTestProject/Assets/Editor/Eval/` (see `unity/README.md`). Both conditions get the same system prompt; tool
  names, descriptions and input schemas come from Unity-MCP's own tool list, calls go through its `RunCallTool` pipeline.
  ReflectorNet condition: 20 tools (`gameobject-*`, `gameobject-component-*`, `object-*`, `assets-find/get-data/modify`,
  `assets-shader-get-data`, `scene-get-data`, `scene-list-opened`, `type-get-json-schema`); `script-execute` and
  `reflection-method-call` are excluded (code escape hatches). RQL condition: `rql-query`, `rql-mutate`, `rql-schema`.
- The scene and materials are rebuilt before every run; success = task check AND no unintended change in a scene/material
  snapshot diff.
- Failures (all genuine agent/tool behaviour, transcripts in the run's `results.jsonl`):
  - material-shader-floats r1: `assets-modify` rejected `_Glossiness` / `_Metallic` in three patch formats and the agent
    answered IMPOSSIBLE. The runs that passed found the `content.props` format after 2–3 errors.
  - move-and-rotate r3: the agent wrote Euler numbers (0, 90, 0) into `Transform.rotation` (a quaternion) and the tool
    accepted that silently.
  - combined-archers r2: ran out of 15 steps while discovering enemies one by one.
- Tool execution time is about the same for both (≈ 20 ms per task). The 2.5× wall-time gap and the 5.8× cost gap come
  from fewer turns, smaller tool results (11.5 K vs 0.7 K chars) and 7.5× smaller tool definitions (69 K vs 9 K chars,
  sent with every request).
