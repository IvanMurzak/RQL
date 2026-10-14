#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.Unity.MCP;
using RqlTest.EditorTools;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace RqlTest.Eval
{
    /// <summary>
    /// In-Editor agent comparison: the same DeepSeek agent solves the same Unity tasks once with Unity-MCP's ReflectorNet-based
    /// tools and once with the RQL tools. Both toolsets come from Unity-MCP's own tool list (name, description, input schema as
    /// an MCP client sees them) and run through its RunCallTool pipeline; the scene is rebuilt before every run and graded on
    /// the final Editor state plus a diff for unintended changes.
    /// Batch: Unity -batchmode -nographics -projectPath RqlTestProject -executeMethod RqlTest.Eval.UnityEval.Run
    ///        [-evalConditions reflectornet,rql] [-evalTasks id,id] [-evalRepeats 2] [-evalMaxSteps 15] [-evalBudget 4] -logFile -
    /// (without -quit; the Editor exits when the run is done). Needs RQL_EVAL_API_KEY_FILE.
    /// </summary>
    public static class UnityEval
    {
        private const string Model = "deepseek-flash";
        private const int MaxTokensPerCall = 4096;
        private const int MaxToolResultChars = 40_000;

        public static readonly Dictionary<string, string[]> Toolsets = new Dictionary<string, string[]>
        {
            ["reflectornet"] = new[]
            {
                "gameobject-find", "gameobject-create", "gameobject-modify", "gameobject-set-parent", "gameobject-destroy", "gameobject-duplicate",
                "gameobject-component-add", "gameobject-component-destroy", "gameobject-component-get", "gameobject-component-list-all",
                "gameobject-component-modify", "object-get-data", "object-modify", "assets-find", "assets-get-data", "assets-modify",
                "assets-shader-get-data", "scene-get-data", "scene-list-opened", "type-get-json-schema",
            },
            ["rql"] = new[] { "rql-query", "rql-mutate", "rql-schema" },
        };

        /// <summary>
        /// Whole-catalog conditions: every tool Unity-MCP lists (exactly what an MCP client sees), minus the excluded name
        /// prefixes. catalog-legacy = Unity-MCP without the standalone RQL package's tools.
        /// </summary>
        public static readonly Dictionary<string, string[]> Catalogs = new Dictionary<string, string[]>
        {
            ["catalog"] = Array.Empty<string>(),
            ["catalog-legacy"] = new[] { "rql-" },
        };

        // Catalog conditions take the ENABLED tools only, as an MCP client sees them (disabled tools are not exposed).
        private static string[] ToolNames(string condition, ICollection<string> listed, com.IvanMurzak.McpPlugin.IToolManager manager) =>
            Catalogs.TryGetValue(condition, out var excluded)
                ? listed.Where(n => manager.IsToolEnabled(n) && !excluded.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                        .OrderBy(n => n, StringComparer.Ordinal).ToArray()
                : Toolsets[condition];

        // The engine of whichever RQL integration is installed (standalone package or Unity-MCP's own), reset between runs.
        private static void ResetRql()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("com.IvanMurzak.Unity.MCP.Rql.Editor.RqlUnity") ?? asm.GetType("com.IvanMurzak.Unity.MCP.Editor.Rql.RqlUnity");
                t?.GetMethod("Reset", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            }
        }

        private const string SystemPrompt =
            "You are an AI assistant working inside the Unity Editor through tools. The scene that is open is the one to work on.\n" +
            "Rules: be efficient — use as few tool calls as possible. Perform exactly the change requested and nothing else (do not save, " +
            "rename or move anything that the task does not mention). If a requested change is impossible, change nothing and answer " +
            "\"IMPOSSIBLE: <reason>\". Always finish by calling submit_answer exactly once: after a change task answer \"done\"; for a " +
            "question answer exactly in the requested format.";

        private static readonly JsonObject SubmitTool = new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "submit_answer",
                ["description"] = "Submit the final answer and end the task.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "string", ["description"] = "the final answer" } },
                    ["required"] = new JsonArray("answer"),
                },
            },
        };

        private static bool _running;

        public static void Run() => _ = RunAsync();

        private static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        private static async Task RunAsync()
        {
            if (_running) return;
            _running = true;
            int exit = 0;
            try { await RunCore(); }
            catch (Exception ex)
            {
                Debug.LogError("[EVAL] failed: " + DeepSeekClient.Redact(ex.ToString()));
                exit = 1;
            }
            finally { _running = false; }
            if (UnityEngine.Application.isBatchMode) EditorApplication.Exit(exit);
        }

        /// <summary>The RQL repo's evaluation folder: the nearest ancestor of the project with evaluation/configs/pricing.json.</summary>
        private static string FindEvaluationRoot()
        {
            for (var dir = new DirectoryInfo(UnityEngine.Application.dataPath); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "evaluation");
                if (File.Exists(Path.Combine(candidate, "configs", "pricing.json"))) return candidate;
            }
            throw new InvalidOperationException("evaluation/configs/pricing.json not found above the project");
        }

        private static async Task RunCore()
        {
            var conditions = Arg("-evalConditions", "reflectornet,rql").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var taskFilter = Arg("-evalTasks", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            int repeats = int.Parse(Arg("-evalRepeats", "2"), CultureInfo.InvariantCulture);
            int maxSteps = int.Parse(Arg("-evalMaxSteps", "15"), CultureInfo.InvariantCulture);
            double budget = double.Parse(Arg("-evalBudget", "4"), CultureInfo.InvariantCulture);
            var tasks = EvalTasks.All().Where(t => taskFilter.Count == 0 || taskFilter.Contains(t.Id)).ToList();

            var evalRoot = FindEvaluationRoot();
            var prices = JsonNode.Parse(File.ReadAllText(Path.Combine(evalRoot, "configs/pricing.json")))!["models"]![Model]!.AsObject();
            var ledgerPath = Path.Combine(evalRoot, "baselines/spend-ledger.json");
            var ledger = JsonNode.Parse(File.ReadAllText(ledgerPath))!.AsObject();
            double cap = ledger["CapUsd"]!.GetValue<double>(), spentBefore = ledger["SpentUsd"]!.GetValue<double>();
            budget = Math.Min(budget, cap - spentBefore);
            if (budget <= 0) throw new InvalidOperationException($"ledger cap reached: spent {spentBefore:0.0000} of {cap}");

            var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-unity-inproc";
            var outDir = Path.Combine(evalRoot, "runs", runId);
            Directory.CreateDirectory(outDir);

            UnityMcpPluginEditor.InitSingletonIfNeeded();
            var manager = UnityMcpPluginEditor.Instance.Tools ?? throw new InvalidOperationException("Unity-MCP tool manager unavailable");
            var listed = await manager.RunListTool(new RequestListTool());
            if (listed.Status == ResponseStatus.Error || listed.Value == null) throw new InvalidOperationException("tools/list failed: " + listed.Message);
            var catalog = listed.Value.ToDictionary(t => t.Name, t => t);

            var toolDefs = new Dictionary<string, JsonArray>();
            var config = new JsonObject
            {
                ["runId"] = runId, ["model"] = Model, ["reasoningEffort"] = "none", ["temperature"] = 0, ["maxTokensPerCall"] = MaxTokensPerCall,
                ["maxSteps"] = maxSteps, ["repeats"] = repeats, ["budgetUsd"] = budget, ["maxToolResultChars"] = MaxToolResultChars,
                ["unity"] = UnityEngine.Application.unityVersion, ["unityMcp"] = UnityMcpPlugin.Version, ["systemPrompt"] = SystemPrompt,
                ["tasks"] = new JsonArray(tasks.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["category"] = t.Category, ["prompt"] = t.Prompt }).ToArray()),
            };
            var condInfo = new JsonObject();
            foreach (var c in conditions)
            {
                var defs = new JsonArray();
                var names = ToolNames(c, catalog.Keys, manager);
                foreach (var name in names)
                {
                    if (!catalog.TryGetValue(name, out var t)) throw new InvalidOperationException($"tool '{name}' is not in Unity-MCP's tool list (disabled?)");
                    var schema = t.InputSchema.ValueKind == JsonValueKind.Undefined ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                        : JsonNode.Parse(t.InputSchema.GetRawText())!;
                    defs.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description ?? "", ["parameters"] = schema },
                    });
                }
                defs.Add(SubmitTool.DeepClone());
                toolDefs[c] = defs;
                condInfo[c] = new JsonObject { ["tools"] = new JsonArray(names.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()), ["toolDefinitionChars"] = defs.ToJsonString().Length };
            }
            config["conditions"] = condInfo;
            File.WriteAllText(Path.Combine(outDir, "config.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            using var client = new DeepSeekClient("https://api.deepseek.com", DeepSeekClient.LoadApiKey(), prices, TimeSpan.FromSeconds(120));
            var results = new List<JsonObject>();
            double spent = 0;
            var ledgerRuns = ledger["Runs"]!.AsArray();
            var ledgerEntry = new JsonObject { ["id"] = runId, ["config"] = "unity-inproc", ["taskRuns"] = 0, ["costUsd"] = 0.0 };
            ledgerRuns.Add(ledgerEntry);
            Debug.Log($"[EVAL] {runId}: {tasks.Count} tasks × {conditions.Length} conditions × {repeats} repeats, budget ${budget:0.00}");

            using var jsonl = new StreamWriter(Path.Combine(outDir, "results.jsonl"), false, new UTF8Encoding(false));
            for (int r = 1; r <= repeats; r++)
            foreach (var task in tasks)
            foreach (var c in conditions)
            {
                if (spent >= budget) { Debug.LogWarning("[EVAL] budget exhausted, stopping"); goto done; }
                var res = await RunOne(client, manager, task, c, toolDefs[c], r, maxSteps, () => budget - spent);
                double cost = (double)res["usage"]!["costUsd"]!;
                spent += cost;
                results.Add(res);
                jsonl.WriteLine(res.ToJsonString());
                jsonl.Flush();
                ledgerEntry["taskRuns"] = results.Count;
                ledgerEntry["costUsd"] = Math.Round(spent, 6);
                ledger["SpentUsd"] = spentBefore + spent;
                File.WriteAllText(ledgerPath, ledger.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Debug.Log($"[EVAL] {(res["success"]!.GetValue<bool>() ? "PASS" : "FAIL")} {c,-12} {task.Id,-24} r{r} steps={res["modelTurns"]} tools={res["toolCalls"]} " +
                          $"err={res["toolErrors"]} ${cost:0.0000} {(double)res["wallMs"]! / 1000:0.0}s {res["outcome"]} | {res["gradeDetail"]}");
            }
            done:
            RqlTestSceneBuilder.Build();
            File.WriteAllText(Path.Combine(outDir, "summary.md"), Summary(runId, config, conditions, tasks, results, spent));
            Debug.Log($"[EVAL] done: {results.Count} runs, ${spent:0.0000}; {outDir}");
        }

        private static async Task<JsonObject> RunOne(DeepSeekClient client, com.IvanMurzak.McpPlugin.IToolManager manager, EvalTask task, string condition, JsonArray tools,
                                                     int repeat, int maxSteps, Func<double> budgetLeft)
        {
            RqlTestSceneBuilder.Build();
            ResetRql();
            task.Setup?.Invoke();
            Undo.ClearAll();
            var before = SceneSnapshot.Take();
            // Snapshot after setup, so whatever the setup changed is the baseline.

            var usage = new Usage();
            var steps = new JsonArray();
            int turns = 0, toolCalls = 0, toolErrors = 0, protocolErrors = 0, resultChars = 0, truncated = 0;
            double modelMs = 0, toolMs = 0;
            string? answer = null;
            string outcome = "";
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = "Task: " + task.Prompt },
            };
            var sw = Stopwatch.StartNew();
            try
            {
                for (int step = 1; step <= maxSteps && answer == null; step++)
                {
                    if (budgetLeft() - usage.CostUsd <= 0) { outcome = "budget-exhausted"; break; }
                    var req = new JsonObject
                    {
                        ["model"] = Model, ["messages"] = messages.DeepClone(), ["tools"] = tools.DeepClone(),
                        ["max_tokens"] = MaxTokensPerCall, ["reasoning_effort"] = "none", ["temperature"] = 0,
                    };
                    var (msg, finish, u, latency) = await client.CompleteAsync(req);
                    usage.Add(u);
                    modelMs += latency;
                    turns++;
                    var calls = msg["tool_calls"] as JsonArray;
                    var rec = new JsonObject { ["step"] = step, ["latencyMs"] = Math.Round(latency), ["usage"] = u.ToJson(), ["content"] = msg["content"]?.DeepClone(), ["finish"] = finish };
                    var recCalls = new JsonArray();
                    rec["calls"] = recCalls;
                    steps.Add(rec);

                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = msg["content"]?.DeepClone() };
                    if (msg["reasoning_content"] != null) assistant["reasoning_content"] = msg["reasoning_content"]!.DeepClone();
                    if (calls != null && calls.Count > 0) assistant["tool_calls"] = calls.DeepClone();
                    messages.Add(assistant);
                    if (calls == null || calls.Count == 0)
                    {
                        protocolErrors++;
                        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Use the tools. When you are done, call submit_answer with the final answer." });
                        continue;
                    }
                    foreach (var call in calls)
                    {
                        var id = call!["id"]!.GetValue<string>();
                        var name = call["function"]!["name"]!.GetValue<string>();
                        var rawArgs = call["function"]!["arguments"]?.GetValue<string>() ?? "{}";
                        JsonObject? args = null;
                        try { args = JsonNode.Parse(rawArgs) as JsonObject; } catch (JsonException) { }
                        var tc = new JsonObject { ["tool"] = name, ["arguments"] = rawArgs };
                        string output;
                        if (name == "submit_answer")
                        {
                            answer = args?["answer"] is JsonNode an ? (an.GetValueKind() == JsonValueKind.String ? an.GetValue<string>() : an.ToJsonString()) : rawArgs;
                            output = "submitted";
                        }
                        else if (answer != null) output = "ignored: answer already submitted";
                        else
                        {
                            var esw = Stopwatch.StartNew();
                            var (text, error) = args == null ? ("error: arguments are not valid JSON", true) : await CallTool(manager, name, args, tools);
                            var ms = esw.Elapsed.TotalMilliseconds;
                            toolMs += ms;
                            toolCalls++;
                            if (error) toolErrors++;
                            resultChars += text.Length;
                            if (text.Length > MaxToolResultChars)
                            {
                                truncated++;
                                text = text.Substring(0, MaxToolResultChars) + $"\n…[truncated {text.Length - MaxToolResultChars} chars]";
                            }
                            output = text;
                            tc["ms"] = Math.Round(ms, 1);
                            tc["error"] = error;
                        }
                        tc["result"] = output;
                        recCalls.Add(tc);
                        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output });
                    }
                }
                if (outcome == "") outcome = answer != null ? "answered" : "max-steps";
            }
            catch (Exception ex) { outcome = "error: " + DeepSeekClient.Redact(ex.Message); }
            var wall = sw.Elapsed.TotalMilliseconds;

            var after = SceneSnapshot.Take();
            var changed = SceneSnapshot.Diff(before, after);
            var unintended = changed.Where(k => !task.Allowed(k)).ToList();
            (bool ok, string detail) check;
            try { check = task.Check(answer); }
            catch (Exception ex) { check = (false, "check threw " + ex.GetType().Name + ": " + ex.Message); }
            bool success = check.ok && unintended.Count == 0 && outcome == "answered";
            var detail = check.detail + (unintended.Count > 0 ? $" | unintended: {string.Join(", ", unintended.Take(8))}{(unintended.Count > 8 ? " …" : "")}" : "");

            return new JsonObject
            {
                ["task"] = task.Id, ["category"] = task.Category, ["condition"] = condition, ["repeat"] = repeat,
                ["success"] = success, ["taskCheck"] = check.ok, ["outcome"] = outcome, ["answer"] = answer, ["gradeDetail"] = detail,
                ["unintendedChanges"] = unintended.Count, ["unintended"] = new JsonArray(unintended.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()),
                ["changedKeys"] = changed.Count, ["modelTurns"] = turns, ["toolCalls"] = toolCalls, ["toolErrors"] = toolErrors, ["protocolErrors"] = protocolErrors,
                ["toolResultChars"] = resultChars, ["truncatedResults"] = truncated, ["usage"] = usage.ToJson(),
                ["wallMs"] = Math.Round(wall), ["modelMs"] = Math.Round(modelMs), ["toolMs"] = Math.Round(toolMs, 1), ["steps"] = steps,
            };
        }

        private static async Task<(string text, bool error)> CallTool(com.IvanMurzak.McpPlugin.IToolManager manager, string name, JsonObject args, JsonArray tools)
        {
            if (!tools.Any(t => t!["function"]!["name"]!.GetValue<string>() == name)) return ($"error: unknown tool '{name}'", true);
            var dict = new Dictionary<string, JsonElement>();
            foreach (var kv in args)
            {
                using var doc = JsonDocument.Parse(kv.Value?.ToJsonString() ?? "null");
                dict[kv.Key] = doc.RootElement.Clone();
            }
            try
            {
                var outer = await manager.RunCallTool(new RequestCallTool(name, dict));
                if (outer.Status == ResponseStatus.Error) return (outer.Message ?? "[Error] Got an error during running tool", true);
                if (outer.Value == null) return ("[Error] Tool returned null value", true);
                var v = outer.Value;
                var sb = new StringBuilder();
                if (v.Content != null)
                    foreach (var block in v.Content)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(block.Text ?? "");
                    }
                if (sb.Length == 0 && v.StructuredContent != null) sb.Append(v.StructuredContent.ToJsonString());
                return (sb.ToString(), v.Status == ResponseStatus.Error);
            }
            catch (Exception ex) { return ($"[Error] {ex.GetType().Name}: {ex.Message}", true); }
        }

        // ------------------------------------------------------------ report

        private static string Summary(string runId, JsonObject config, string[] conditions, List<EvalTask> tasks, List<JsonObject> results, double spent)
        {
            static double D(JsonObject o, string k) => double.Parse(o[k]!.ToJsonString(), CultureInfo.InvariantCulture);
            static double U(JsonObject o, string k) => D(o["usage"]!.AsObject(), k);
            static bool Ok(JsonObject r) => r["success"]!.GetValue<bool>();
            var byCondition = conditions.ToDictionary(c => c, c => results.Where(r => r["condition"]!.GetValue<string>() == c).ToList());
            var sb = new StringBuilder();
            void Header(string leading)
            {
                sb.Append(leading);
                foreach (var c in conditions) sb.Append($" {c} |");
                sb.AppendLine().Append(string.Concat(Enumerable.Repeat("|---", leading.Count(ch => ch == '|') - 1))).Append('|');
                foreach (var _ in conditions) sb.Append("---|");
                sb.AppendLine();
            }
            void Row(string label, Func<List<JsonObject>, string> f)
            {
                sb.Append($"| {label} |");
                foreach (var c in conditions) sb.Append($" {(byCondition[c].Count == 0 ? "–" : f(byCondition[c]))} |");
                sb.AppendLine();
            }

            sb.AppendLine($"# Unity in-Editor comparison — {runId}").AppendLine();
            sb.AppendLine($"model `{Model}`, reasoning `none`, temperature 0, maxSteps {config["maxSteps"]}, repeats {config["repeats"]}, Unity {config["unity"]}, Unity-MCP {config["unityMcp"]}; total cost ${spent:0.0000}.").AppendLine();
            Header("| Metric |");
            Row("Runs", rs => rs.Count.ToString(CultureInfo.InvariantCulture));
            Row("Success", rs => $"{rs.Count(Ok)}/{rs.Count} ({100.0 * rs.Count(Ok) / rs.Count:0}%)");
            Row("Task check passed (ignoring side effects)", rs => $"{rs.Count(r => r["taskCheck"]!.GetValue<bool>())}/{rs.Count}");
            Row("Runs with unintended changes", rs => rs.Count(r => D(r, "unintendedChanges") > 0).ToString(CultureInfo.InvariantCulture));
            Row("Mean model turns", rs => rs.Average(r => D(r, "modelTurns")).ToString("0.0", CultureInfo.InvariantCulture));
            Row("Mean tool calls / errors", rs => $"{rs.Average(r => D(r, "toolCalls")):0.0} / {rs.Average(r => D(r, "toolErrors")):0.0}");
            Row("Mean prompt tokens", rs => rs.Average(r => U(r, "promptTokens")).ToString("N0", CultureInfo.InvariantCulture));
            Row("Mean completion tokens", rs => rs.Average(r => U(r, "completionTokens")).ToString("N0", CultureInfo.InvariantCulture));
            Row("Mean tool result chars", rs => rs.Average(r => D(r, "toolResultChars")).ToString("N0", CultureInfo.InvariantCulture));
            Row("Mean cost per task (USD)", rs => rs.Average(r => U(r, "costUsd")).ToString("0.00000", CultureInfo.InvariantCulture));
            Row("Mean wall time (s)", rs => (rs.Average(r => D(r, "wallMs")) / 1000).ToString("0.0", CultureInfo.InvariantCulture));
            Row("Mean model time (s)", rs => (rs.Average(r => D(r, "modelMs")) / 1000).ToString("0.0", CultureInfo.InvariantCulture));
            Row("Mean tool time (ms)", rs => rs.Average(r => D(r, "toolMs")).ToString("0.0", CultureInfo.InvariantCulture));
            Row("Tool definitions (chars)", rs => ((JsonObject)config["conditions"]!)[rs[0]["condition"]!.GetValue<string>()]!["toolDefinitionChars"]!.ToString());

            sb.AppendLine().AppendLine("## Per task (passes / runs, mean tool calls, mean cost)").AppendLine();
            Header("| Task | Category |");
            foreach (var t in tasks)
            {
                sb.Append($"| {t.Id} | {t.Category} |");
                foreach (var c in conditions)
                {
                    var rs = byCondition[c].Where(r => r["task"]!.GetValue<string>() == t.Id).ToList();
                    sb.Append(rs.Count == 0 ? " – |" : $" {rs.Count(Ok)}/{rs.Count}, {rs.Average(r => D(r, "toolCalls")):0.0} calls, ${rs.Average(r => U(r, "costUsd")):0.0000} |");
                }
                sb.AppendLine();
            }
            sb.AppendLine().AppendLine("## Failures").AppendLine();
            foreach (var r in results.Where(r => !Ok(r)))
                sb.AppendLine($"- `{r["condition"]}` {r["task"]} r{r["repeat"]}: {r["outcome"]} — {r["gradeDetail"]}");
            return sb.ToString();
        }
    }
}
