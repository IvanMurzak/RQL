using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Rql.Eval.Agent;

namespace Rql.Eval
{
    public sealed class Summary
    {
        public string Name { get; set; } = "";
        public int N { get; set; }
        public int Successes { get; set; }
        public double SuccessRate { get; set; }
        public double CiLow { get; set; }
        public double CiHigh { get; set; }
        public double CostUsd { get; set; }
        public double CostPerTask { get; set; }
        public double CostPerSuccess { get; set; }
        public double MeanPromptTokens { get; set; }
        public double MeanCompletionTokens { get; set; }
        public double MeanReasoningTokens { get; set; }
        public double MeanTotalTokens { get; set; }
        public double CacheHitRatio { get; set; }
        public double MeanTurns { get; set; }
        public double MeanToolCalls { get; set; }
        public double MeanToolErrors { get; set; }
        public int ProtocolErrors { get; set; }
        public double MeanToolResultChars { get; set; }
        public double MedianWallSec { get; set; }
        public double P95WallSec { get; set; }
        public double MeanExecutorMs { get; set; }
        public double SuccessPerDollar { get; set; }
        public double SuccessPerMinute { get; set; }
        public int UnintendedChangeTasks { get; set; }
        public Dictionary<string, string> ByCategory { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> Outcomes { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> ToolErrorCodes { get; set; } = new Dictionary<string, int>();
    }

    public static class Report
    {
        private static string P(double x) => (100 * x).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        private static string N0(double x) => x.ToString("#,0", CultureInfo.InvariantCulture);
        private static string N2(double x) => x.ToString("0.00", CultureInfo.InvariantCulture);
        private static string Usd(double x) => "$" + x.ToString(x < 0.01 ? "0.00000" : "0.0000", CultureInfo.InvariantCulture);

        /// <summary>Wilson score interval (95%).</summary>
        public static (double lo, double hi) Wilson(int k, int n)
        {
            if (n == 0) return (0, 0);
            const double z = 1.96;
            double p = k / (double)n;
            double denom = 1 + z * z / n;
            double centre = (p + z * z / (2 * n)) / denom;
            double half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denom;
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }

        private static double Percentile(List<double> xs, double q)
        {
            if (xs.Count == 0) return 0;
            var s = xs.OrderBy(x => x).ToList();
            double pos = (s.Count - 1) * q;
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            return s[lo] + (s[hi] - s[lo]) * (pos - lo);
        }

        public static Summary Summarize(string name, List<TaskResult> rs)
        {
            var s = new Summary { Name = name, N = rs.Count, Successes = rs.Count(r => r.Success) };
            if (rs.Count == 0) return s;
            s.SuccessRate = s.Successes / (double)s.N;
            (s.CiLow, s.CiHigh) = Wilson(s.Successes, s.N);
            s.CostUsd = rs.Sum(r => r.Usage.CostUsd);
            s.CostPerTask = s.CostUsd / s.N;
            s.CostPerSuccess = s.Successes == 0 ? double.PositiveInfinity : s.CostUsd / s.Successes;
            s.MeanPromptTokens = rs.Average(r => (double)r.Usage.PromptTokens);
            s.MeanCompletionTokens = rs.Average(r => (double)r.Usage.CompletionTokens);
            s.MeanReasoningTokens = rs.Average(r => (double)r.Usage.ReasoningTokens);
            s.MeanTotalTokens = s.MeanPromptTokens + s.MeanCompletionTokens;
            long prompt = rs.Sum(r => r.Usage.PromptTokens);
            s.CacheHitRatio = prompt == 0 ? 0 : rs.Sum(r => r.Usage.CacheHitTokens) / (double)prompt;
            s.MeanTurns = rs.Average(r => (double)r.ModelTurns);
            s.MeanToolCalls = rs.Average(r => (double)r.ToolCalls);
            s.MeanToolErrors = rs.Average(r => (double)r.ToolErrors);
            s.ProtocolErrors = rs.Sum(r => r.ProtocolErrors);
            s.MeanToolResultChars = rs.Average(r => (double)r.ToolResultChars);
            var walls = rs.Select(r => r.WallMs / 1000).ToList();
            s.MedianWallSec = Percentile(walls, 0.5);
            s.P95WallSec = Percentile(walls, 0.95);
            s.MeanExecutorMs = rs.Average(r => r.ExecutorMs);
            s.SuccessPerDollar = s.CostUsd == 0 ? 0 : s.Successes / s.CostUsd;
            s.SuccessPerMinute = walls.Sum() == 0 ? 0 : s.Successes / (walls.Sum() / 60);
            s.UnintendedChangeTasks = rs.Count(r => r.UnintendedChanges > 0);
            foreach (var g in rs.GroupBy(r => r.Category).OrderBy(g => g.Key))
                s.ByCategory[g.Key] = $"{g.Count(r => r.Success)}/{g.Count()}";
            foreach (var g in rs.GroupBy(r => r.Outcome.StartsWith("error") ? "error" : r.Outcome)) s.Outcomes[g.Key] = g.Count();
            foreach (var g in rs.SelectMany(r => r.Steps).SelectMany(st => st.Calls).Where(c => c.Error).GroupBy(c => c.ErrorCode ?? "?"))
                s.ToolErrorCodes[g.Key] = g.Count();
            return s;
        }

        public static string Markdown(Summary s, EvalConfig cfg)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"### {s.Name}");
            sb.AppendLine();
            sb.AppendLine($"model `{cfg.Model}`, reasoning `{cfg.ReasoningEffort}`, prompt `{cfg.Prompt}`, format `{cfg.Format}`, toolset `{cfg.Toolset}`, schemaInPrompt `{cfg.SchemaInPrompt}`, maxSteps {cfg.MaxSteps}, maxRows {cfg.MaxRows}");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine($"| Tasks (n) | {s.N} |");
            sb.AppendLine($"| Exact success | {s.Successes}/{s.N} = {P(s.SuccessRate)} (95% CI {P(s.CiLow)}–{P(s.CiHigh)}) |");
            sb.AppendLine($"| Total cost (provider-reported tokens × price snapshot) | {Usd(s.CostUsd)} |");
            sb.AppendLine($"| Cost / task | {Usd(s.CostPerTask)} |");
            sb.AppendLine($"| Cost / successful task | {Usd(s.CostPerSuccess)} |");
            sb.AppendLine($"| Successful tasks per $ | {N0(s.SuccessPerDollar)} |");
            sb.AppendLine($"| Successful tasks per wall-minute (sequential-equivalent) | {N2(s.SuccessPerMinute)} |");
            sb.AppendLine($"| Mean prompt / completion tokens per task | {N0(s.MeanPromptTokens)} / {N0(s.MeanCompletionTokens)} (reasoning {N0(s.MeanReasoningTokens)}) |");
            sb.AppendLine($"| Prompt cache-hit ratio | {P(s.CacheHitRatio)} |");
            sb.AppendLine($"| Mean model turns / tool calls / tool errors | {N2(s.MeanTurns)} / {N2(s.MeanToolCalls)} / {N2(s.MeanToolErrors)} |");
            sb.AppendLine($"| Protocol errors (turns without a tool call) | {s.ProtocolErrors} |");
            sb.AppendLine($"| Mean tool-result chars per task | {N0(s.MeanToolResultChars)} |");
            sb.AppendLine($"| Wall time per task median / p95 | {N2(s.MedianWallSec)} s / {N2(s.P95WallSec)} s |");
            sb.AppendLine($"| Mean executor time per task | {N2(s.MeanExecutorMs)} ms |");
            sb.AppendLine($"| Tasks with unintended state changes | {s.UnintendedChangeTasks} |");
            sb.AppendLine($"| By category | {string.Join(", ", s.ByCategory.Select(kv => kv.Key + " " + kv.Value))} |");
            sb.AppendLine($"| Outcomes | {string.Join(", ", s.Outcomes.Select(kv => kv.Key + " " + kv.Value))} |");
            if (s.ToolErrorCodes.Count > 0) sb.AppendLine($"| Tool error codes | {string.Join(", ", s.ToolErrorCodes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value))} |");
            return sb.ToString();
        }

        public static string Compare(string nameA, List<TaskResult> a, string nameB, List<TaskResult> b)
        {
            var sa = Summarize(nameA, a);
            var sb2 = Summarize(nameB, b);
            var keyed = b.ToDictionary(r => r.TaskId + "/" + r.Repeat);
            var pairs = a.Where(r => keyed.ContainsKey(r.TaskId + "/" + r.Repeat)).Select(r => (a: r, b: keyed[r.TaskId + "/" + r.Repeat])).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"### {nameA} → {nameB} (paired n = {pairs.Count})");
            sb.AppendLine();
            sb.AppendLine($"| Measure | {nameA} | {nameB} | Delta |");
            sb.AppendLine("|---|---:|---:|---:|");
            void Row(string m, double x, double y, Func<double, string> fmt, bool pct = true)
            {
                string delta = pct && x != 0 ? ((y - x) / x * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%" : (y - x).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
                sb.AppendLine($"| {m} | {fmt(x)} | {fmt(y)} | {delta} |");
            }
            sb.AppendLine($"| Exact success | {sa.Successes}/{sa.N} ({P(sa.SuccessRate)}) | {sb2.Successes}/{sb2.N} ({P(sb2.SuccessRate)}) | {((sb2.SuccessRate - sa.SuccessRate) * 100).ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture)} pp |");
            Row("Cost / task", sa.CostPerTask, sb2.CostPerTask, Usd);
            Row("Cost / successful task", sa.CostPerSuccess, sb2.CostPerSuccess, Usd);
            Row("Successful tasks per $", sa.SuccessPerDollar, sb2.SuccessPerDollar, N0);
            Row("Mean total tokens / task", sa.MeanTotalTokens, sb2.MeanTotalTokens, N0);
            Row("Mean prompt tokens / task", sa.MeanPromptTokens, sb2.MeanPromptTokens, N0);
            Row("Mean completion tokens / task", sa.MeanCompletionTokens, sb2.MeanCompletionTokens, N0);
            Row("Mean tool calls / task", sa.MeanToolCalls, sb2.MeanToolCalls, N2);
            Row("Mean tool errors / task", sa.MeanToolErrors, sb2.MeanToolErrors, N2);
            Row("Mean tool-result chars / task", sa.MeanToolResultChars, sb2.MeanToolResultChars, N0);
            Row("Median wall s / task", sa.MedianWallSec, sb2.MedianWallSec, N2);
            Row("p95 wall s / task", sa.P95WallSec, sb2.P95WallSec, N2);
            sb.AppendLine();
            var gained = pairs.Where(p => !p.a.Success && p.b.Success).Select(p => p.a.TaskId).ToList();
            var lost = pairs.Where(p => p.a.Success && !p.b.Success).Select(p => p.a.TaskId).ToList();
            sb.AppendLine($"Paired flips: +{gained.Count} gained ({string.Join(", ", gained)}), −{lost.Count} lost ({string.Join(", ", lost)}).");
            var tokDeltas = pairs.Select(p => (double)(p.b.Usage.PromptTokens + p.b.Usage.CompletionTokens) - (p.a.Usage.PromptTokens + p.a.Usage.CompletionTokens)).ToList();
            if (tokDeltas.Count > 0)
                sb.AppendLine($"Per-task total-token delta: median {N0(Percentile(tokDeltas, 0.5))}, tasks cheaper in {nameB}: {tokDeltas.Count(d => d < 0)}/{tokDeltas.Count}.");
            return sb.ToString();
        }
    }
}
