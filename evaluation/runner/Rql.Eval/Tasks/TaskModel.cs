using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    public enum AnswerKind { Refs, Names, Number, Text, Bool, Object, State, None, Impossible }

    /// <summary>Expected result, computed by the hidden oracle from the live sandbox. Never shown to the model.</summary>
    public sealed class Expected
    {
        public AnswerKind Kind { get; set; }
        public HashSet<string>? Set { get; set; }
        /// <summary>Alternative acceptable sets (ties).</summary>
        public List<HashSet<string>>? AltSets { get; set; }
        public double? Number { get; set; }
        public double Tolerance { get; set; } = 1e-6;
        public string? Text { get; set; }
        public HashSet<string>? AcceptTexts { get; set; }
        public bool? Bool { get; set; }
        public Dictionary<string, object?>? Fields { get; set; }
        /// <summary>For state tasks: applies the intended mutation to a fresh sandbox copy.</summary>
        public Action<Sandbox>? Apply { get; set; }

        public static Expected Refs(IEnumerable<string> refs) => new Expected { Kind = AnswerKind.Refs, Set = new HashSet<string>(refs, StringComparer.Ordinal) };
        public static Expected Names(IEnumerable<string> names) => new Expected { Kind = AnswerKind.Names, Set = new HashSet<string>(names, StringComparer.Ordinal) };
        public static Expected Num(double v, double tol = 1e-6) => new Expected { Kind = AnswerKind.Number, Number = v, Tolerance = tol };
        public static Expected Str(string s, params string[] alternatives) => new Expected { Kind = AnswerKind.Text, Text = s, AcceptTexts = new HashSet<string>(alternatives.Append(s), StringComparer.OrdinalIgnoreCase) };
        public static Expected Flag(bool b) => new Expected { Kind = AnswerKind.Bool, Bool = b };
        public static Expected Obj(Dictionary<string, object?> f) => new Expected { Kind = AnswerKind.Object, Fields = f };
        public static Expected State(Action<Sandbox> apply) => new Expected { Kind = AnswerKind.State, Apply = apply };
        public static Expected NoneAnswer() => new Expected { Kind = AnswerKind.None };
        public static Expected ImpossibleAnswer() => new Expected { Kind = AnswerKind.Impossible };
    }

    public sealed class TaskInstance
    {
        public string Id { get; set; } = "";
        public string Template { get; set; } = "";
        public string Category { get; set; } = "";
        public string Split { get; set; } = "";
        public int Seed { get; set; }
        public WorldSize Size { get; set; }
        public string Goal { get; set; } = "";
        public string AnswerFormat { get; set; } = "";
        public bool AllowMutation { get; set; }
        public Expected Expected { get; set; } = new Expected();
        /// <summary>Reference RQL solution (hidden; used by the scripted oracle agent to validate solvability).</summary>
        public Func<SampleHost, string>? Reference { get; set; }
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();
    }

    public sealed class Grade
    {
        public bool Success { get; set; }
        public double Precision { get; set; } = double.NaN;
        public double Recall { get; set; } = double.NaN;
        public int UnintendedChanges { get; set; }
        public int MissingChanges { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>Hidden deterministic grader: compares the submitted answer / final state with the oracle.</summary>
    public static class Grader
    {
        private static readonly Regex RefPattern = new Regex(@"[A-Za-z_][A-Za-z0-9_]*[#@][A-Za-z0-9_/\-.]+", RegexOptions.Compiled);

        public static bool IsImpossible(string? answer) => answer != null && answer.TrimStart().StartsWith("IMPOSSIBLE", StringComparison.OrdinalIgnoreCase);
        public static bool IsNone(string? answer) => answer != null && Regex.IsMatch(answer.Trim(), @"^(NONE|none|\[\]|""""|null)\b", RegexOptions.IgnoreCase);

        public static Grade GradeAnswer(TaskInstance task, string? answer, SampleHost finalHost)
        {
            var e = task.Expected;
            var g = new Grade();
            if (e.Kind == AnswerKind.State || e.Kind == AnswerKind.Impossible)
            {
                var expectedHost = new SampleHost(WorldGenerator.Create(task.Seed, task.Size), allowMutations: true);
                if (e.Kind == AnswerKind.State) e.Apply!(expectedHost.Sandbox);
                var expected = GraphSnapshot.Capture(expectedHost);
                var actual = GraphSnapshot.Capture(finalHost);
                var fresh = GraphSnapshot.Capture(new SampleHost(WorldGenerator.Create(task.Seed, task.Size)));
                var diff = GraphSnapshot.Diff(expected, actual);
                var intended = new HashSet<string>(GraphSnapshot.Diff(fresh, expected));
                g.MissingChanges = diff.Count(p => intended.Contains(p));
                g.UnintendedChanges = diff.Count - g.MissingChanges;
                bool stateOk = diff.Count == 0;
                if (e.Kind == AnswerKind.Impossible)
                {
                    g.Success = stateOk && IsImpossible(answer);
                    g.Detail = stateOk ? (IsImpossible(answer) ? "unchanged + IMPOSSIBLE" : "state unchanged but answer did not say IMPOSSIBLE") : $"{diff.Count} unintended change(s)";
                }
                else
                {
                    g.Success = stateOk;
                    g.Recall = intended.Count == 0 ? 1 : (intended.Count - g.MissingChanges) / (double)intended.Count;
                    g.Detail = stateOk ? "state matches" : $"missing {g.MissingChanges}, unintended {g.UnintendedChanges}; e.g. {string.Join(", ", diff.Take(3))}";
                }
                return g;
            }
            if (answer == null) { g.Detail = "no answer submitted"; return g; }
            switch (e.Kind)
            {
                case AnswerKind.None:
                    g.Success = IsNone(answer);
                    g.Detail = g.Success ? "NONE" : "expected NONE";
                    return g;
                case AnswerKind.Refs:
                {
                    var got = new HashSet<string>(RefPattern.Matches(answer).Select(m => m.Value.TrimEnd('.', ',')), StringComparer.Ordinal);
                    return SetGrade(g, got, e);
                }
                case AnswerKind.Names:
                {
                    // Leniency v1.1: "Asset#mat/wood" names the same thing as "mat/wood" when ids were asked for.
                    var got = new HashSet<string>(ParseList(answer).Select(x =>
                    {
                        var m = Regex.Match(x, @"^[A-Za-z_][A-Za-z0-9_]*[#@](.+)$");
                        return m.Success && !e.Set!.Contains(x) && e.Set.Contains(m.Groups[1].Value) ? m.Groups[1].Value : x;
                    }), StringComparer.Ordinal);
                    return SetGrade(g, got, e);
                }
                case AnswerKind.Number:
                {
                    var m = Regex.Match(answer.Replace(",", ""), @"-?\d+(\.\d+)?([eE][-+]?\d+)?");
                    if (!m.Success) { g.Detail = "no number in answer"; return g; }
                    var v = double.Parse(m.Value, CultureInfo.InvariantCulture);
                    g.Success = Math.Abs(v - e.Number!.Value) <= Math.Max(e.Tolerance, 1e-9 * Math.Abs(e.Number.Value));
                    g.Detail = $"got {v}, want {e.Number}";
                    return g;
                }
                case AnswerKind.Text:
                {
                    var t = Unquote(answer.Trim());
                    g.Success = e.AcceptTexts!.Contains(t);
                    g.Detail = $"got '{t}', want '{e.Text}'";
                    return g;
                }
                case AnswerKind.Bool:
                {
                    var t = answer.Trim().ToLowerInvariant();
                    bool? b = t.StartsWith("true") || t.StartsWith("yes") ? true : t.StartsWith("false") || t.StartsWith("no") ? false : (bool?)null;
                    g.Success = b == e.Bool;
                    g.Detail = $"got {b?.ToString() ?? "?"}, want {e.Bool}";
                    return g;
                }
                case AnswerKind.Object:
                    return ObjectGrade(g, answer, e);
            }
            return g;
        }

        private static string Unquote(string s) => s.Length >= 2 && (s[0] == '"' && s[s.Length - 1] == '"' || s[0] == '\'' && s[s.Length - 1] == '\'') ? s.Substring(1, s.Length - 2) : s;

        public static HashSet<string> ParseList(string answer)
        {
            var t = answer.Trim();
            if (t.StartsWith("["))
            {
                try
                {
                    using var doc = JsonDocument.Parse(t);
                    return new HashSet<string>(doc.RootElement.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()), StringComparer.Ordinal);
                }
                catch (JsonException) { }
            }
            var sep = t.Contains('\n') ? new[] { '\n' } : t.Contains(';') ? new[] { ';' } : new[] { ',' };
            return new HashSet<string>(t.Split(sep).Select(x => Unquote(x.Trim().TrimStart('-', '*', ' ').Trim())).Where(x => x.Length > 0), StringComparer.Ordinal);
        }

        private static Grade SetGrade(Grade g, HashSet<string> got, Expected e)
        {
            var candidates = new List<HashSet<string>> { e.Set! };
            if (e.AltSets != null) candidates.AddRange(e.AltSets);
            var best = candidates.OrderByDescending(c => got.Intersect(c).Count() - got.Except(c).Count()).First();
            int tp = got.Intersect(best).Count();
            g.Precision = got.Count == 0 ? (best.Count == 0 ? 1 : 0) : tp / (double)got.Count;
            g.Recall = best.Count == 0 ? 1 : tp / (double)best.Count;
            g.Success = candidates.Any(c => c.SetEquals(got));
            g.Detail = $"got {got.Count}, want {best.Count}, tp {tp}";
            return g;
        }

        private static Grade ObjectGrade(Grade g, string answer, Expected e)
        {
            var t = answer.Trim();
            int a = t.IndexOf('{'), b = t.LastIndexOf('}');
            if (a < 0 || b < a) { g.Detail = "answer is not a JSON object"; return g; }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(t.Substring(a, b - a + 1)); }
            catch (JsonException) { g.Detail = "answer is not valid JSON"; return g; }
            using (doc)
            {
                var props = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
                int ok = 0;
                var bad = new List<string>();
                foreach (var kv in e.Fields!)
                {
                    if (!props.TryGetValue(kv.Key, out var v)) { bad.Add(kv.Key + " missing"); continue; }
                    bool match = kv.Value switch
                    {
                        null => v.ValueKind == JsonValueKind.Null,
                        bool bb => (v.ValueKind == JsonValueKind.True && bb) || (v.ValueKind == JsonValueKind.False && !bb),
                        string s => v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), s, StringComparison.OrdinalIgnoreCase),
                        _ => v.ValueKind == JsonValueKind.Number && Math.Abs(v.GetDouble() - Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture)) <= 0.011,
                    };
                    if (match) ok++;
                    else bad.Add(kv.Key);
                }
                g.Success = ok == e.Fields.Count;
                g.Recall = ok / (double)e.Fields.Count;
                g.Detail = g.Success ? "all fields match" : "mismatch: " + string.Join(", ", bad);
            }
            return g;
        }
    }
}
