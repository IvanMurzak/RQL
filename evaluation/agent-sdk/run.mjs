// Short live probe: Claude Agent SDK (as the AI Game Dev app runs it) -> MCP server -> Unity-MCP, one model per run.
// Checks that the provider and harness accept the listed tool schemas and that text with special characters survives
// tool results and arguments. See README.md for the setup. Never logs keys or tokens.
//
// node run.mjs --label luna-text-minimal --model gpt-6-luna --base-url http://127.0.0.1:58000/api/ai \
//   --key-file <token file> --mcp-url http://localhost:58090/mcp --out results.jsonl
import { query } from "@anthropic-ai/claude-agent-sdk";
import { appendFileSync, existsSync, mkdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";

const arg = (name, fallback) => {
  const i = process.argv.indexOf("--" + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
};
const label = arg("label", "run");
const model = arg("model");
const baseUrl = arg("base-url");
const key = readFileSync(arg("key-file"), "utf8").trim();
const mcpUrl = arg("mcp-url", "http://localhost:58090/mcp");
const out = arg("out", "results.jsonl");
const maxTurns = Number(arg("max-turns", "12"));
// "true" as the app forces it (MCP schemas load on demand); "false" sends every tool schema with the first request.
const toolSearch = arg("tool-search", "true");
const only = (arg("tasks", "") || "").split(",").filter(Boolean);

const PROBE = 'Crate #7 <A+B> — café "x"';
const RENAMED = "Box <1+1> naïve & 'ok' — ü";

// The agent's project lives outside every repo, so no CLAUDE.md / .claude / .git is picked up (memory:
// live-test-project-outside-git-claudemd); a private config dir keeps the owner's Claude settings and login out.
const root = join(homedir(), "Library", "Caches", "rql-agentsdk");
const cwd = join(root, "work", label);
const configDir = join(root, "claude-config", label);
mkdirSync(cwd, { recursive: true });
mkdirSync(configDir, { recursive: true });
for (let d = cwd; d !== dirname(d); d = dirname(d))
  for (const f of ["CLAUDE.md", ".claude", ".git"])
    if (existsSync(join(d, f)) && !(d === homedir() && f === ".claude"))
      throw new Error(`refusing to run: ${join(d, f)} is an ancestor of the agent project`);

// --- minimal MCP client (streamable HTTP) for setup and grading, outside the agent ---
let session = null;
async function rpc(method, params, id = Math.floor(Math.random() * 1e9)) {
  const headers = { "content-type": "application/json", accept: "application/json, text/event-stream" };
  if (session) headers["mcp-session-id"] = session;
  const res = await fetch(mcpUrl, { method: "POST", headers, body: JSON.stringify({ jsonrpc: "2.0", id, method, params }) });
  session = res.headers.get("mcp-session-id") ?? session;
  const text = await res.text();
  const data = text.trimStart().startsWith("{") ? text : text.split("\n").filter(l => l.startsWith("data:")).map(l => l.slice(5)).pop();
  return data ? JSON.parse(data) : null;
}
async function mcp() {
  if (session) return;
  await rpc("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "rql-probe", version: "1" } });
  await fetch(mcpUrl, { method: "POST", headers: { "content-type": "application/json", accept: "application/json, text/event-stream", "mcp-session-id": session },
    body: JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) });
}
async function tool(name, args) {
  await mcp();
  const r = await rpc("tools/call", { name, arguments: args });
  return (r?.result?.content ?? []).map(c => c.text ?? "").join("\n");
}

const tasks = [
  {
    id: "answer-tricky-name",
    prompt: 'Exactly one GameObject in the open Unity scene has a name starting with "Crate #7". Reply with its exact full name only, character for character.',
    check: async (answer) => answer.trim().replace(/^[`'"]+|[`'"]+$/g, "") === PROBE || answer.includes(PROBE),
  },
  {
    id: "rename-tricky-name",
    prompt: `In the open Unity scene, rename the GameObject whose name starts with "Crate #7" to exactly: ${RENAMED}`,
    check: async () => (await tool("rql-query", { query: "objects | where name starts-with \"Box <1+1>\" | select name" })).includes(RENAMED),
  },
  {
    id: "light-intensity",
    prompt: 'In the open Unity scene, set the intensity of the Light component on the GameObject "Lamp_B" to 3.25.',
    check: async () => /3\.25/.test(await tool("rql-query", { query: 'objects | where name == "Lamp_B" | get components | where $ is Light | select intensity' })),
  },
  {
    // Loads a tool whose schema is ReflectorNet-heavy (SerializedMemberList $defs) and runs it.
    id: "script-execute",
    prompt: "Use the Unity tool that compiles and runs C# code to compute 6 * 7 in C# (return it from the method), and reply with the number only.",
    check: async (answer, rec) => rec.toolCalls.includes("script-execute") && /\b42\b/.test(answer),
  },
];

for (const task of tasks.filter(t => only.length === 0 || only.includes(t.id))) {
  const rec = { label, model, toolSearch, task: task.id, ok: false, apiErrors: [], toolErrors: 0, toolCalls: [], turns: 0, costUsd: null, answer: "" };
  const started = Date.now();
  try {
    for await (const m of query({
      prompt: task.prompt,
      options: {
        model, cwd, maxTurns,
        settingSources: [],
        mcpServers: { unity: { type: "http", url: mcpUrl } },
        allowedTools: ["mcp__unity__*"],
        disallowedTools: ["Bash", "Read", "Write", "Edit", "Glob", "Grep", "WebFetch", "WebSearch", "Task", "NotebookEdit", "TodoWrite"],
        permissionMode: "bypassPermissions",
        env: {
          PATH: process.env.PATH, HOME: process.env.HOME,
          CLAUDE_CONFIG_DIR: configDir,
          ANTHROPIC_BASE_URL: baseUrl, ANTHROPIC_API_KEY: key,
          ANTHROPIC_SMALL_FAST_MODEL: model, ANTHROPIC_DEFAULT_HAIKU_MODEL: model,
          ENABLE_TOOL_SEARCH: toolSearch,
          CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: "1",
        },
      },
    })) {
      if (m.type === "system" && m.subtype === "init") {
        rec.mcp = m.mcp_servers;
        rec.mcpTools = (m.tools ?? []).filter(t => t.startsWith("mcp__")).length;
      } else if (m.type === "assistant") {
        for (const c of m.message?.content ?? []) {
          if (c.type === "tool_use") { rec.toolCalls.push(c.name.replace("mcp__unity__", "")); (rec.toolInputs ??= []).push(JSON.stringify(c.input).slice(0, 800)); }
          if (c.type === "text" && /API Error|invalid_request|Invalid schema|input_schema/i.test(c.text)) rec.apiErrors.push(c.text.slice(0, 400));
        }
        if (m.error) rec.apiErrors.push(String(m.error).slice(0, 400));
      } else if (m.type === "user") {
        for (const c of m.message?.content ?? []) {
          if (c.type !== "tool_result") continue;
          if (c.is_error) rec.toolErrors++;
          const text = typeof c.content === "string" ? c.content : (c.content ?? []).map(x => x.text ?? "").join("\n");
          (rec.toolResults ??= []).push(text.slice(0, 800));
        }
      } else if (m.type === "result") {
        rec.turns = m.num_turns;
        rec.costUsd = m.total_cost_usd;
        rec.subtype = m.subtype;
        rec.answer = m.result ?? "";
        if (m.subtype !== "success") rec.apiErrors.push(m.subtype + ": " + (m.errors ?? []).join("; ").slice(0, 400));
      }
    }
    rec.ok = rec.apiErrors.length === 0 && (await task.check(rec.answer, rec));
  } catch (e) {
    rec.apiErrors.push("threw: " + String(e?.message ?? e).slice(0, 400));
  }
  rec.seconds = Math.round((Date.now() - started) / 100) / 10;
  appendFileSync(out, JSON.stringify(rec) + "\n");
  console.log(`${rec.ok ? "PASS" : "FAIL"} ${label} ${task.id} turns=${rec.turns} calls=${rec.toolCalls.join(",")} toolErr=${rec.toolErrors} apiErr=${rec.apiErrors.length} ${rec.seconds}s ${rec.apiErrors[0] ?? ""}`);
}

// Restore the scene for the next model in the same Unity session.
await tool("rql-mutate", { query: `objects | where name starts-with "Box <1+1>" | set name = ${JSON.stringify(PROBE)}` });
await tool("rql-mutate", { query: 'objects | where name == "Lamp_B" | get components | where $ is Light | set intensity = 2.5' });
