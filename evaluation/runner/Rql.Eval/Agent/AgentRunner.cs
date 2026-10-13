using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Rql.Eval.Tasks;
using Rql.Execution;
using Rql.Sample;

namespace Rql.Eval.Agent
{
    /// <summary>One experimental condition. Everything that can influence results lives here (and is saved with the run).</summary>
    public sealed class EvalConfig
    {
        public string Name { get; set; } = "baseline";
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-flash";
        /// <summary>none | low | high | max (DeepSeek reasoning_effort). none = non-thinking mode.</summary>
        public string ReasoningEffort { get; set; } = "none";
        public double Temperature { get; set; } = 0;
        public int MaxSteps { get; set; } = 12;
        public int MaxTokensPerCall { get; set; } = 4096;
        public int TimeoutSeconds { get; set; } = 120;
        public string Prompt { get; set; } = "v1";
        public string Format { get; set; } = "table";
        public string Toolset { get; set; } = "rql";
        public bool SchemaInPrompt { get; set; }
        public int MaxRows { get; set; } = 50;
        public int Parallel { get; set; } = 6;
        public double BudgetUsd { get; set; } = 2;
        public int Repeats { get; set; } = 1;
    }

    public sealed class StepRecord
    {
        public int Step { get; set; }
        public double LatencyMs { get; set; }
        public Usage Usage { get; set; } = new Usage();
        public List<ToolCallRecord> Calls { get; set; } = new List<ToolCallRecord>();
        public string? Content { get; set; }
        public string? FinishReason { get; set; }
    }

    public sealed class ToolCallRecord
    {
        public string Tool { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Result { get; set; } = "";
        public bool Error { get; set; }
        public string? ErrorCode { get; set; }
        public double ExecMs { get; set; }
    }

    public sealed class TaskResult
    {
        public string TaskId { get; set; } = "";
        public string Template { get; set; } = "";
        public string Category { get; set; } = "";
        public string Split { get; set; } = "";
        public int Repeat { get; set; }
        public bool Success { get; set; }
        public double Precision { get; set; } = double.NaN;
        public double Recall { get; set; } = double.NaN;
        public int UnintendedChanges { get; set; }
        public string GradeDetail { get; set; } = "";
        public string? Answer { get; set; }
        public string Outcome { get; set; } = "";
        public int ModelTurns { get; set; }
        public int ToolCalls { get; set; }
        public int ToolErrors { get; set; }
        public int ProtocolErrors { get; set; }
        public long ToolResultChars { get; set; }
        public long QueryChars { get; set; }
        public double WallMs { get; set; }
        public double ExecutorMs { get; set; }
        public Usage Usage { get; set; } = new Usage();
        public List<StepRecord> Steps { get; set; } = new List<StepRecord>();
    }

    /// <summary>Thread-safe spend ledger shared by all tasks of a run (hard stop on budget).</summary>
    public sealed class Budget
    {
        private readonly object _lock = new object();
        public Budget(double limitUsd, double alreadySpent) { Limit = limitUsd; Spent = alreadySpent; }
        public double Limit { get; }
        public double Spent { get; private set; }
        public bool Exhausted { get { lock (_lock) return Spent >= Limit; } }
        public void Add(double usd) { lock (_lock) Spent += usd; }
    }

    public sealed class AgentRunner
    {
        private readonly EvalConfig _cfg;
        private readonly ChatClient? _client;
        private readonly Budget _budget;

        public AgentRunner(EvalConfig cfg, ChatClient? client, Budget budget)
        {
            _cfg = cfg; _client = client; _budget = budget;
        }

        public static string UserMessage(TaskInstance t) => "Task: " + t.Goal + "\n" + t.AnswerFormat;

        private string SystemPrompt(SampleHost host)
        {
            var p = Prompts.Get(_cfg.Prompt);
            if (_cfg.SchemaInPrompt) p += "\n\nSchema:\n" + host.Schema();
            return p;
        }

        // ------------------------------------------------------------ tool execution

        private (string text, bool error, string? code) Execute(SampleHost host, string tool, JsonObject? args, bool allowMutation)
        {
            var format = AgentFormat.Parse(_cfg.Format);
            var budgets = new Rql.Budgets { MaxRows = _cfg.MaxRows };
            switch (tool)
            {
                case "rql_query":
                {
                    var q = args?["query"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(q)) return ("error: missing 'query' argument", true, "bad-arguments");
                    var r = host.Query(q!, budgets);
                    return (AgentFormat.Render(r, format), !r.Ok, r.Ok ? null : r.Diagnostics.FirstOrDefault()?.Code);
                }
                case "rql_mutate":
                {
                    if (!allowMutation) return ("error permission-denied: this task does not allow changes", true, "permission-denied");
                    var q = args?["query"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(q)) return ("error: missing 'query' argument", true, "bad-arguments");
                    bool dry = args?["dry_run"]?.GetValueKind() == JsonValueKind.True;
                    var r = host.Mutate(q!, dry, budgets);
                    return (AgentFormat.Render(r, format), !r.Ok, r.Ok ? null : r.Diagnostics.FirstOrDefault()?.Code);
                }
                case "rql_schema":
                {
                    var type = args?["type"]?.GetValue<string>();
                    var s = host.Schema(string.IsNullOrWhiteSpace(type) ? null : type);
                    return (s, s.StartsWith("error"), s.StartsWith("error") ? "unknown-type" : null);
                }
                default:
                    return ($"error: unknown tool '{tool}'", true, "unknown-tool");
            }
        }

        // ------------------------------------------------------------ model loop

        public async Task<TaskResult> RunAsync(TaskInstance task, int repeat, CancellationToken ct)
        {
            var result = new TaskResult { TaskId = task.Id, Template = task.Template, Category = task.Category, Split = task.Split, Repeat = repeat };
            var host = new SampleHost(WorldGenerator.Create(task.Seed, task.Size), allowMutations: true);
            var sw = Stopwatch.StartNew();
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(host) },
                new JsonObject { ["role"] = "user", ["content"] = UserMessage(task) },
            };
            var tools = Prompts.Tools(task.AllowMutation, _cfg.Toolset);
            string? answer = null;
            try
            {
                for (int step = 1; step <= _cfg.MaxSteps && answer == null; step++)
                {
                    if (_budget.Exhausted) { result.Outcome = "budget-exhausted"; break; }
                    var req = new JsonObject
                    {
                        ["model"] = _cfg.Model,
                        ["messages"] = messages.DeepClone(),
                        ["tools"] = tools.DeepClone(),
                        ["max_tokens"] = _cfg.MaxTokensPerCall,
                        ["reasoning_effort"] = _cfg.ReasoningEffort,
                    };
                    if (_cfg.ReasoningEffort == "none") req["temperature"] = _cfg.Temperature;
                    var resp = await _client!.CompleteAsync(req, _cfg.Model, ct).ConfigureAwait(false);
                    _budget.Add(resp.Usage.CostUsd);
                    result.Usage.Add(resp.Usage);
                    result.ModelTurns++;
                    var rec = new StepRecord { Step = step, LatencyMs = resp.LatencyMs, Usage = resp.Usage, Content = resp.Message["content"]?.GetValue<string>(), FinishReason = resp.FinishReason };
                    result.Steps.Add(rec);

                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = resp.Message["content"]?.DeepClone() };
                    if (resp.Message["reasoning_content"] != null) assistant["reasoning_content"] = resp.Message["reasoning_content"]!.DeepClone();
                    var calls = resp.Message["tool_calls"] as JsonArray;
                    if (calls != null && calls.Count > 0) assistant["tool_calls"] = calls.DeepClone();
                    messages.Add(assistant);

                    if (calls == null || calls.Count == 0)
                    {
                        result.ProtocolErrors++;
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
                        var tc = new ToolCallRecord { Tool = name, Arguments = rawArgs };
                        string output;
                        if (name == "submit_answer")
                        {
                            answer = args?["answer"] is JsonNode an ? (an.GetValueKind() == JsonValueKind.String ? an.GetValue<string>() : an.ToJsonString()) : rawArgs;
                            output = "submitted";
                        }
                        else if (answer != null)
                        {
                            output = "ignored: answer already submitted";
                        }
                        else
                        {
                            var esw = Stopwatch.StartNew();
                            var (text, error, code) = args == null ? ("error: arguments are not valid JSON", true, "bad-arguments") : Execute(host, name, args, task.AllowMutation);
                            tc.ExecMs = esw.Elapsed.TotalMilliseconds;
                            result.ExecutorMs += tc.ExecMs;
                            output = text;
                            tc.Error = error;
                            tc.ErrorCode = code;
                            result.ToolCalls++;
                            if (error) result.ToolErrors++;
                            result.ToolResultChars += text.Length;
                            result.QueryChars += (args?["query"]?.GetValue<string>() ?? "").Length;
                        }
                        tc.Result = output;
                        rec.Calls.Add(tc);
                        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = output });
                    }
                }
                if (answer == null && result.Outcome == "") result.Outcome = "max-steps";
                else if (answer != null) result.Outcome = "answered";
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                result.Outcome = "error: " + ChatClient.Redact(ex.Message);
            }
            result.WallMs = sw.Elapsed.TotalMilliseconds;
            result.Answer = answer;
            var grade = Grader.GradeAnswer(task, answer, host);
            result.Success = grade.Success;
            result.Precision = grade.Precision;
            result.Recall = grade.Recall;
            result.UnintendedChanges = grade.UnintendedChanges;
            result.GradeDetail = grade.Detail;
            return result;
        }

        /// <summary>Scripted reference agent (no LLM): validates that every task is solvable via RQL and that grader+oracle agree.</summary>
        public static TaskResult RunReference(TaskInstance task)
        {
            var result = new TaskResult { TaskId = task.Id, Template = task.Template, Category = task.Category, Split = task.Split };
            var host = new SampleHost(WorldGenerator.Create(task.Seed, task.Size), allowMutations: true);
            var sw = Stopwatch.StartNew();
            try { result.Answer = task.Reference!(host); result.Outcome = "answered"; }
            catch (Exception ex) { result.Outcome = "error: " + ex.Message; }
            result.WallMs = sw.Elapsed.TotalMilliseconds;
            var g = Grader.GradeAnswer(task, result.Answer, host);
            result.Success = g.Success;
            result.GradeDetail = g.Detail;
            return result;
        }
    }
}
