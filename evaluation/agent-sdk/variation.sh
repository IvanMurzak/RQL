#!/usr/bin/env bash
# One Unity session with one tool presentation, then the probe for each given model.
# usage: variation.sh <toolFormat Json|RqlText|RqlLean> <escaping Minimal|Ascii|Json> <model-spec>...
#   model-spec = label|model|baseUrl|keyFile[|toolSearch true/false]
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${RQL_LIVE_PROJECT:?path of the Unity project (Unity-MCP experiment/rql)}"
unity="${UNITY_BIN:-/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity}"
work="$HOME/Library/Caches/rql-agentsdk"
format="$1"; escaping="$2"; shift 2
python3 - "$project/UserSettings/AI-Game-Developer-Config.json" "$format" "$escaping" <<'PY'
import json, sys
p, f, e = sys.argv[1:]
d = json.load(open(p)); d["toolFormat"] = f; d["toolTextEscaping"] = e
json.dump(d, open(p, "w"), indent=2)
PY
stop="$work/stop-$format-$escaping"; rm -f "$stop"
log="$project/Logs/live-$format-$escaping.log"
UNITY_MCP_HOST=http://localhost:58090 UNITY_MCP_CONNECTION_MODE=Custom UNITY_MCP_KEEP_CONNECTED=true UNITY_MCP_START_SERVER=false \
  "$unity" -batchmode -nographics -projectPath "$project" -executeMethod RqlTest.Eval.McpLive.Run -liveStopFile "$stop" -logFile "$log" &
upid=$!
for _ in $(seq 1 300); do grep -q "\[LIVE\] connected" "$log" 2>/dev/null && break; kill -0 $upid 2>/dev/null || break; sleep 1; done
grep -h "\[LIVE\]" "$log" | head -3
if ! grep -q "\[LIVE\] connected" "$log"; then
  echo "Unity did not connect (see $log)"; grep -h "error CS" "$log" | sort -u | head -5; touch "$stop"; kill $upid 2>/dev/null; exit 1
fi
sleep 3
for spec in "$@"; do
  IFS='|' read -r label model base keyfile search <<<"$spec"
  # Local ai-game.dev proxy: app access tokens are short-lived (the app refreshes them); log in again before each run.
  if [[ "$base" == http://127.0.0.1:58000/* && -n "${RQL_LOCAL_LOGIN_PASSWORD:-}" ]]; then
    curl -s -X POST http://127.0.0.1:58000/api/auth/login -H "Content-Type: application/json" \
      --data-raw "{\"email\":\"dev@example.com\",\"password\":\"$RQL_LOCAL_LOGIN_PASSWORD\"}" \
      | python3 -c "import json,sys; print(json.load(sys.stdin)['accessToken'])" > "$keyfile"
  fi
  node "$here/run.mjs" --label "$label-$format-$escaping-ts${search:-true}" --tool-search "${search:-true}" --model "$model" --base-url "$base" --key-file "$keyfile" \
    --mcp-url http://localhost:58090/mcp --out "${RQL_LIVE_OUT:-$here/results.jsonl}" --tasks "${RQL_LIVE_TASKS:-}"
done
touch "$stop"; wait $upid; echo "unity exit=$?"
