using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Rql.Eval.Agent;
using Rql.Eval.Tasks;

namespace Rql.Eval
{
    /// <summary>
    /// rql-eval — Tier B agent evaluation.
    ///   tasks [--split dev|holdout|all]                 export the public task list (no answers)
    ///   reference [--split ...]                          scripted reference agent: solvability + grader self-check (no LLM)
    ///   probe --config FILE                              one tiny authenticated request (connectivity + usage fields)
    ///   run --config FILE [--split dev] [--filter s] [--limit N] [--repeats N]
    ///   report RUN_DIR [RUN_DIR_B]                       summary or paired comparison (markdown)
    /// Secrets: RQL_EVAL_API_KEY or RQL_EVAL_API_KEY_FILE (never logged).
    /// </summary>
    public static class Program
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        private static string Root => Conformance.ConformanceSuite.FindRoot();
        private static string EvalDir => Path.Combine(Root, "evaluation");
        private static string LedgerPath => Path.Combine(EvalDir, "baselines", "spend-ledger.json");

        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("usage: rql-eval tasks|reference|probe|run|report ..."); return 2; }
            var opts = ParseOpts(args.Skip(1).ToArray(), out var positional);
            string split = opts.TryGetValue("split", out var sp) ? sp : "dev";
            switch (args[0])
            {
                case "tasks": return ExportTasks(split);
                case "reference": return Reference(split, opts);
                case "probe": return await Probe(LoadConfig(opts));
                case "run": return await Run(LoadConfig(opts), split, opts);
                case "report": return ReportCmd(positional, opts);
                case "regrade": return Regrade(positional[0]);
                default: Console.Error.WriteLine("unknown command " + args[0]); return 2;
            }
        }

        private static Dictionary<string, string> ParseOpts(string[] args, out List<string> positional)
        {
            var d = new Dictionary<string, string>();
            positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    var k = args[i].Substring(2);
                    var v = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
                    // --set may repeat; values accumulate separated by '\n'.
                    d[k] = k == "set" && d.TryGetValue(k, out var prev) ? prev + "\n" + v : v;
                }
                else positional.Add(args[i]);
            }
            return d;
        }

        private static EvalConfig LoadConfig(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("config", out var path)) throw new ArgumentException("--config FILE required");
            var cfg = JsonSerializer.Deserialize<EvalConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            foreach (var kv in opts)
            {
                // CLI overrides for quick one-factor experiments: --set Key=Value
                if (kv.Key != "set") continue;
                foreach (var assignment in kv.Value.Split('\n'))
                {
                    var eq = assignment.IndexOf('=');
                    var prop = typeof(EvalConfig).GetProperty(assignment.Substring(0, eq), System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                               ?? throw new ArgumentException("unknown config key " + assignment.Substring(0, eq));
                    prop.SetValue(cfg, Convert.ChangeType(assignment.Substring(eq + 1), prop.PropertyType, CultureInfo.InvariantCulture));
                }
            }
            var env = Environment.GetEnvironmentVariable("RQL_EVAL_BASE_URL");
            if (!string.IsNullOrWhiteSpace(env)) cfg.BaseUrl = env!;
            var model = Environment.GetEnvironmentVariable("RQL_EVAL_MODEL");
            if (!string.IsNullOrWhiteSpace(model)) cfg.Model = model!;
            return cfg;
        }

        private static PriceTable LoadPrices() =>
            JsonSerializer.Deserialize<PriceTable>(File.ReadAllText(Path.Combine(EvalDir, "configs", "pricing.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        // ------------------------------------------------------------ ledger (global hard cap)

        private sealed class Ledger
        {
            public double CapUsd { get; set; } = 25;
            public double SpentUsd { get; set; }
            public List<JsonObject> Runs { get; set; } = new List<JsonObject>();
        }

        private static Ledger LoadLedger() =>
            File.Exists(LedgerPath) ? JsonSerializer.Deserialize<Ledger>(File.ReadAllText(LedgerPath))! : new Ledger();

        private static void SaveLedger(Ledger l)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
            File.WriteAllText(LedgerPath, JsonSerializer.Serialize(l, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        // ------------------------------------------------------------ commands

        private static int ExportTasks(string split)
        {
            var tasks = Catalog.Build(split);
            var path = Path.Combine(EvalDir, "tasks", $"tasks-{Catalog.Version}-{split}.jsonl");
            using (var w = new StreamWriter(path))
            {
                foreach (var t in tasks)
                {
                    w.WriteLine(JsonSerializer.Serialize(new
                    {
                        id = t.Id, template = t.Template, category = t.Category, split = t.Split, seed = t.Seed, size = t.Size.ToString(),
                        allowMutation = t.AllowMutation, answerKind = t.Expected.Kind.ToString(), goal = t.Goal, format = t.AnswerFormat,
                    }, Json));
                }
            }
            Console.WriteLine($"{tasks.Count} tasks → {path}");
            foreach (var g in tasks.GroupBy(t => t.Category)) Console.WriteLine($"  {g.Key}: {g.Count()}");
            return 0;
        }

        private static int Reference(string split, Dictionary<string, string> opts)
        {
            var tasks = Filter(Catalog.Build(split), opts);
            int ok = 0;
            foreach (var t in tasks)
            {
                var r = AgentRunner.RunReference(t);
                if (r.Success) ok++;
                else Console.WriteLine($"FAIL {t.Id}: {r.Outcome} — {r.GradeDetail}\n     answer: {Trim(r.Answer, 300)}");
            }
            Console.WriteLine($"reference agent: {ok}/{tasks.Count} tasks solved");
            return ok == tasks.Count ? 0 : 1;
        }

        private static string Trim(string? s, int n) => s == null ? "(none)" : s.Length <= n ? s : s.Substring(0, n) + "…";

        private static List<TaskInstance> Filter(List<TaskInstance> tasks, Dictionary<string, string> opts)
        {
            if (opts.TryGetValue("filter", out var f)) tasks = tasks.Where(t => t.Id.Contains(f) || t.Category == f).ToList();
            if (opts.TryGetValue("limit", out var l)) tasks = tasks.Take(int.Parse(l, CultureInfo.InvariantCulture)).ToList();
            return tasks;
        }

        private static async Task<int> Probe(EvalConfig cfg)
        {
            var ledger = LoadLedger();
            using var client = new ChatClient(cfg.BaseUrl, ChatClient.LoadApiKey(), LoadPrices(), TimeSpan.FromSeconds(cfg.TimeoutSeconds));
            var req = new JsonObject
            {
                ["model"] = cfg.Model,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word: ok" } },
                ["max_tokens"] = 8,
                ["reasoning_effort"] = "none",
                ["temperature"] = 0,
            };
            var r = await client.CompleteAsync(req, cfg.Model, CancellationToken.None);
            ledger.SpentUsd += r.Usage.CostUsd;
            ledger.Runs.Add(new JsonObject { ["id"] = "probe-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture), ["costUsd"] = r.Usage.CostUsd });
            SaveLedger(ledger);
            Console.WriteLine($"reply: {r.Message["content"]?.GetValue<string>()} | prompt {r.Usage.PromptTokens} (hit {r.Usage.CacheHitTokens}) completion {r.Usage.CompletionTokens} | cost ${r.Usage.CostUsd:0.000000} | {r.LatencyMs:0} ms");
            return 0;
        }

        private static async Task<int> Run(EvalConfig cfg, string split, Dictionary<string, string> opts)
        {
            if (opts.TryGetValue("repeats", out var rep)) cfg.Repeats = int.Parse(rep, CultureInfo.InvariantCulture);
            var tasks = Filter(Catalog.Build(split), opts);
            var ledger = LoadLedger();
            double remainingGlobal = ledger.CapUsd - ledger.SpentUsd;
            double runCap = Math.Min(cfg.BudgetUsd, remainingGlobal);
            if (runCap <= 0) { Console.Error.WriteLine($"global spend cap reached (${ledger.SpentUsd:0.00} of ${ledger.CapUsd:0.00})"); return 3; }

            var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + cfg.Name + "-" + split;
            var outDir = opts.TryGetValue("out", out var o) ? o : Path.Combine(EvalDir, "runs", id);
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "config.json"), JsonSerializer.Serialize(new { cfg, split, catalog = Catalog.Version, tasks = tasks.Count, git = GitRev() }, new JsonSerializerOptions { WriteIndented = true }));

            var budget = new Budget(runCap, 0);
            using var client = new ChatClient(cfg.BaseUrl, ChatClient.LoadApiKey(), LoadPrices(), TimeSpan.FromSeconds(cfg.TimeoutSeconds));
            var runner = new AgentRunner(cfg, client, budget);
            var results = new List<TaskResult>();
            var work = new List<(TaskInstance task, int repeat)>();
            for (int r = 0; r < cfg.Repeats; r++) foreach (var t in tasks) work.Add((t, r));
            var gate = new SemaphoreSlim(Math.Max(1, cfg.Parallel));
            var lockObj = new object();
            using var jsonl = new StreamWriter(Path.Combine(outDir, "results.jsonl"));
            Console.WriteLine($"run {id}: {work.Count} task-runs, run cap ${runCap:0.00} (global spent ${ledger.SpentUsd:0.00}/{ledger.CapUsd:0.00})");
            var all = work.Select(async w =>
            {
                await gate.WaitAsync();
                try
                {
                    if (budget.Exhausted) return;
                    var res = await runner.RunAsync(w.task, w.repeat, CancellationToken.None);
                    lock (lockObj)
                    {
                        results.Add(res);
                        jsonl.WriteLine(JsonSerializer.Serialize(res, Json));
                        jsonl.Flush();
                        Console.WriteLine($"{(res.Success ? "PASS" : "FAIL")} {res.TaskId} r{res.Repeat} turns={res.ModelTurns} tools={res.ToolCalls} err={res.ToolErrors} tok={res.Usage.PromptTokens + res.Usage.CompletionTokens} ${res.Usage.CostUsd:0.0000} {res.WallMs / 1000:0.0}s {res.Outcome} | {Trim(res.GradeDetail, 80)}");
                    }
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(all);

            var spent = results.Sum(r => r.Usage.CostUsd);
            ledger = LoadLedger();
            ledger.SpentUsd += spent;
            ledger.Runs.Add(new JsonObject { ["id"] = id, ["config"] = cfg.Name, ["split"] = split, ["taskRuns"] = results.Count, ["costUsd"] = Math.Round(spent, 6) });
            SaveLedger(ledger);
            var summary = Report.Summarize(cfg.Name, results);
            File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            var md = Report.Markdown(summary, cfg);
            File.WriteAllText(Path.Combine(outDir, "summary.md"), md);
            Console.WriteLine();
            Console.WriteLine(md);
            Console.WriteLine($"results: {outDir}");
            if (budget.Exhausted) Console.WriteLine("WARNING: run budget exhausted; some tasks were skipped");
            return 0;
        }

        /// <summary>Re-grade answer-type tasks from stored answers with the current oracle (state tasks keep their original grade).</summary>
        private static int Regrade(string dir)
        {
            var path = Path.Combine(dir, "results.jsonl");
            var orig = Path.Combine(dir, "results.orig.jsonl");
            if (!File.Exists(orig)) File.Copy(path, orig);
            var tasks = Catalog.Build("all").ToDictionary(t => t.Id);
            var results = File.ReadAllLines(orig).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<TaskResult>(l, Json)!).ToList();
            int changed = 0;
            foreach (var r in results)
            {
                if (!tasks.TryGetValue(r.TaskId, out var t) || t.Expected.Kind == AnswerKind.State || t.Expected.Kind == AnswerKind.Impossible) continue;
                var g = Grader.GradeAnswer(t, r.Answer, new Sample.SampleHost(Sample.WorldGenerator.Create(t.Seed, t.Size)));
                if (g.Success != r.Success) { changed++; Console.WriteLine($"{r.TaskId} r{r.Repeat}: {r.Success} → {g.Success} ({g.Detail})"); }
                r.Success = g.Success; r.Precision = g.Precision; r.Recall = g.Recall; r.GradeDetail = g.Detail;
            }
            File.WriteAllLines(path, results.Select(r => JsonSerializer.Serialize(r, Json)));
            Console.WriteLine($"regraded {results.Count} results, {changed} changed (original kept in results.orig.jsonl)");
            return 0;
        }

        private static List<TaskResult> LoadResults(string dir) =>
            File.ReadAllLines(Path.Combine(dir, "results.jsonl")).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<TaskResult>(l, Json)!).ToList();

        private static EvalConfig LoadRunConfig(string dir)
        {
            var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "config.json")))!;
            return JsonSerializer.Deserialize<EvalConfig>(doc["cfg"]!.ToJsonString())!;
        }

        private static int ReportCmd(List<string> dirs, Dictionary<string, string> opts)
        {
            if (dirs.Count == 0) { Console.Error.WriteLine("report RUN_DIR [RUN_DIR_B]"); return 2; }
            string md;
            if (dirs.Count == 1)
            {
                var cfg = LoadRunConfig(dirs[0]);
                md = Report.Markdown(Report.Summarize(opts.TryGetValue("name", out var n) ? n : cfg.Name, LoadResults(dirs[0])), cfg);
            }
            else
            {
                var ca = LoadRunConfig(dirs[0]);
                var cb = LoadRunConfig(dirs[1]);
                md = Report.Compare(ca.Name, LoadResults(dirs[0]), cb.Name, LoadResults(dirs[1]));
            }
            Console.WriteLine(md);
            if (opts.TryGetValue("save", out var save)) File.WriteAllText(save, md);
            return 0;
        }

        private static string GitRev()
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, WorkingDirectory = Root });
                var s = p!.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return s;
            }
            catch { return "unknown"; }
        }
    }
}
