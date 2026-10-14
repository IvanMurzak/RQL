using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rql
{
    public enum DiagnosticSeverity { Error, Warning }

    public sealed class Diagnostic
    {
        public Diagnostic(DiagnosticSeverity severity, string code, string message, TextSpan? at = null, string? hint = null)
        {
            Severity = severity; Code = code; Message = message; At = at; Hint = hint;
        }
        public DiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public TextSpan? At { get; }
        public string? Hint { get; }

        public static Diagnostic Error(string code, string message, string? hint = null) => new Diagnostic(DiagnosticSeverity.Error, code, message, null, hint);
        public static Diagnostic Warning(string code, string message, string? hint = null) => new Diagnostic(DiagnosticSeverity.Warning, code, message, null, hint);

        public override string ToString() =>
            (Severity == DiagnosticSeverity.Error ? "error" : "warning") + " " + Code + ": " + Message + (Hint != null ? " (" + Hint + ")" : "");
    }

    /// <summary>Request-level resource limits (spec/semantics.md §10). Null = runtime default.</summary>
    public sealed class Budgets
    {
        public int? MaxVisited { get; set; }
        public int? MaxRows { get; set; }
        public int? MaxListItems { get; set; }
        public int? MaxDepth { get; set; }
        public int? TimeoutMs { get; set; }
        public int? MaxOutputBytes { get; set; }
        public int? MaxMutationTargets { get; set; }

        private static bool IsIntegral(JsonElement e, out int v)
        {
            var d = e.GetDouble();
            v = (int)d;
            return Math.Floor(d) == d && d >= int.MinValue && d <= int.MaxValue;
        }

        public static Budgets FromJson(JsonElement e)
        {
            var b = new Budgets();
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number || !(p.Value.TryGetInt32(out var v) || IsIntegral(p.Value, out v)) || v < 0)
                    throw new RqlException("invalid-plan", $"budget '{p.Name}' must be a non-negative integer");
                switch (p.Name)
                {
                    case "maxVisited": b.MaxVisited = v; break;
                    case "maxRows": b.MaxRows = v; break;
                    case "maxListItems": b.MaxListItems = v; break;
                    case "maxDepth": b.MaxDepth = v; break;
                    case "timeoutMs": b.TimeoutMs = v; break;
                    case "maxOutputBytes": b.MaxOutputBytes = v; break;
                    case "maxMutationTargets": b.MaxMutationTargets = v; break;
                    default: throw new RqlException("invalid-plan", $"unknown budget '{p.Name}'");
                }
            }
            return b;
        }
    }

    public sealed class QueryOptions
    {
        public bool DryRun { get; set; }
        public bool AllowMutation { get; set; }
    }

    public enum ResultKind { Rows, Items, Scalar, Schema }

    public sealed class Stats
    {
        public long Visited { get; set; }
        public long MembersRead { get; set; }
        public double ElapsedMs { get; set; }
    }

    /// <summary>Execution result. Values are already encoded (spec/semantics.md §6).</summary>
    public sealed class QueryResult
    {
        public bool Ok { get; set; }
        public ResultKind? Kind { get; set; }
        public List<string>? Columns { get; set; }
        public List<JsonNode?[]>? Rows { get; set; }
        public List<JsonNode?>? Items { get; set; }
        public JsonNode? Value { get; set; }
        public bool HasValue { get; set; }
        public JsonArray? Types { get; set; }
        public long? Total { get; set; }
        public bool Truncated { get; set; }
        public bool DryRun { get; set; }
        public List<Diagnostic> Diagnostics { get; } = new List<Diagnostic>();
        public Stats Stats { get; } = new Stats();

        /// <summary>
        /// Messages of the first few distinct error cells (<c>{"$error": code}</c> in the JSON carries only the code). Not
        /// part of the JSON contract; the agent table format prints them so a failing function in a select is explained.
        /// </summary>
        public List<string> CellErrors { get; } = new List<string>();

        public static QueryResult Fail(IEnumerable<Diagnostic> diagnostics)
        {
            var r = new QueryResult { Ok = false };
            r.Diagnostics.AddRange(diagnostics);
            return r;
        }

        public string ToJson(bool indented = false, bool includeStats = true)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
            {
                Indented = indented,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                WriteTo(w, includeStats);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public void WriteTo(Utf8JsonWriter w, bool includeStats)
        {
            w.WriteStartObject();
            w.WriteString("rql", QueryPlan.CurrentVersion);
            w.WriteBoolean("ok", Ok);
            if (Kind.HasValue) w.WriteString("kind", KindName(Kind.Value));
            if (Columns != null)
            {
                w.WriteStartArray("columns");
                foreach (var c in Columns) w.WriteStringValue(c);
                w.WriteEndArray();
            }
            if (Rows != null)
            {
                w.WriteStartArray("rows");
                foreach (var row in Rows)
                {
                    w.WriteStartArray();
                    foreach (var cell in row) WriteNode(w, cell);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            if (Items != null)
            {
                w.WriteStartArray("items");
                foreach (var i in Items) WriteNode(w, i);
                w.WriteEndArray();
            }
            if (HasValue) { w.WritePropertyName("value"); WriteNode(w, Value); }
            if (Types != null) { w.WritePropertyName("types"); Types.WriteTo(w); }
            if (Total.HasValue) w.WriteNumber("total", Total.Value);
            if (Truncated) w.WriteBoolean("truncated", true);
            if (DryRun) w.WriteBoolean("dryRun", true);
            if (Diagnostics.Count > 0)
            {
                w.WriteStartArray("diagnostics");
                foreach (var d in Diagnostics)
                {
                    w.WriteStartObject();
                    w.WriteString("severity", d.Severity == DiagnosticSeverity.Error ? "error" : "warning");
                    w.WriteString("code", d.Code);
                    w.WriteString("message", d.Message);
                    if (d.Hint != null) w.WriteString("hint", d.Hint);
                    if (d.At.HasValue)
                    {
                        w.WriteStartObject("at");
                        w.WriteNumber("start", d.At.Value.Start);
                        w.WriteNumber("end", d.At.Value.End);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (includeStats)
            {
                w.WriteStartObject("stats");
                w.WriteNumber("visited", Stats.Visited);
                w.WriteNumber("membersRead", Stats.MembersRead);
                w.WriteNumber("elapsedMs", Math.Round(Stats.ElapsedMs, 3));
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        public static void WriteNode(Utf8JsonWriter w, JsonNode? n)
        {
            if (n == null) w.WriteNullValue();
            else n.WriteTo(w);
        }

        public static string KindName(ResultKind k) => k switch
        {
            ResultKind.Rows => "rows",
            ResultKind.Items => "items",
            ResultKind.Scalar => "scalar",
            _ => "schema",
        };
    }
}
