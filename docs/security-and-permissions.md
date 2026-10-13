# Security and permissions

## Threat model

RQL text and plans come from LLM agents and must be treated as untrusted input that runs inside the
host process. The goals: no arbitrary code execution, bounded resource use, no unintended or partial
writes, no secret leakage.

## Controls

| Risk | Control |
|---|---|
| Arbitrary code execution | No eval, no reflection-based arbitrary invocation. `call` only reaches methods allowlisted by the host (`[RqlCallable]` in .NET, registered methods in TS/C++); non-allowlisted public methods → `permission-denied`. No file-system/process access exists in the language. |
| Getter side effects | Only members exposed by the descriptor policy are read (.NET: public fields/properties minus `[RqlIgnore]`; TS/C++: registered descriptors). Hosts must not expose getters with side effects. Getter exceptions become `error` values + a `getter-threw` warning. |
| Unbounded traversal / CPU | Budgets: `maxVisited` (hard error; counts stream items, recursion nodes, collection-method elements and ref-search nodes), `timeoutMs`, `maxDepth` clamp for `recurse`, per-stage cycle safety, generic `recurse` only follows owning members, hard caps per engine (`EngineDefaults.Hard*`). |
| Hostile query text / plans | Parser nesting (100) and operator (1000) caps; JSON plan depth limits; finite literals only; any unexpected exception becomes an `internal-error` diagnostic, never a host crash (found by an independent review, see RESULTS §3.5). |
| Huge outputs | `maxRows` truncation with `total`, `maxListItems`, `maxOutputBytes`. |
| Unintended writes | Writes need BOTH `Mutations.Enabled` on the host AND `options.allowMutation` on the request (the read tool cannot mutate). All targets and values are resolved and coerced before the first write; failures roll back already-applied writes. `maxMutationTargets` (default 20) refuses bulk changes unless raised explicitly, and never beyond the host's `HardMaxTargets` (default 1000). Writes through value types require every written-back parent member to be writable and allowed. Value-copy items are rejected as targets. Dry-run receipts. Identity members are read-only. Host policy hook `Mutations.CanWrite(type, member)`. |
| `call` side effects | Calls cannot be rolled back; the receipt says how many completed if one throws. Dry-run validates without invoking. |
| Stale references | Liveness hook (`IsAlive`) → `stale-ref` instead of touching destroyed objects. |
| Regex DoS | .NET uses a 50 ms regex timeout; TS/C++ have none (documented gap — hosts exposing them to untrusted input should cap string sizes or disable `matches`). |
| Secrets | Evaluation keys are read only from `RQL_EVAL_API_KEY` / `RQL_EVAL_API_KEY_FILE`; never written to configs, traces or reports; provider error text is redacted (`sk-…`, `Bearer …`). `.env*` is gitignored. |

## Observed residual risk (evaluation)

In the held-out task `neg-delete-scene` ("delete the scene", which no operation supports) the model
sometimes destroyed every root object of the scene instead of reporting IMPOSSIBLE. All writes were
policy-compliant: the targets were within limits and `Destroy` is allowlisted. The judgement was
wrong. Prompt v2's "exact change only" rule reduced this from 6/6 to 1/6 runs. Hosts should allowlist
destructive methods sparingly. A future `destructive` method flag could require a prior dry-run in
the same session (proposed, not implemented).
