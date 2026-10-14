using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rql.Execution;

namespace Rql.Conformance
{
    /// <summary>
    /// Shared golden-vector suite (fixtures/). The .NET implementation is the reference: `regen` fills
    /// generated fields (plan / expect / code), `check` verifies them. TS and C++ runners consume the same files.
    /// </summary>
    public sealed class ConformanceSuite
    {
        public ConformanceSuite(string? root = null) { Root = root ?? FindRoot(); }

        public string Root { get; }
        public AccessorMode Accessors { get; set; } = AccessorMode.Compiled;
        public List<string> Failures { get; } = new List<string>();
        public int Passed { get; private set; }

        public static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "spec", "semantics.md"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("cannot locate the RQL repository root (spec/semantics.md)");
            return dir.FullName;
        }

        private string F(string rel) => Path.Combine(Root, "fixtures", rel);

        private static readonly JsonSerializerOptions Indented = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private static JsonObject Load(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        private static void Save(string path, JsonObject doc) => File.WriteAllText(path, doc.ToJsonString(Indented) + "\n");

        private void Check(bool ok, string what)
        {
            if (ok) Passed++;
            else Failures.Add(what);
        }

        public void RunAll(bool regen)
        {
            Syntax(regen);
            NegativeSyntax(regen);
            NegativePlans();
            Execution(regen);
            Adapters(regen);
        }

        // ------------------------------------------------------------ syntax / codec

        public void Syntax(bool regen)
        {
            var path = F("syntax/cases.json");
            var doc = Load(path);
            foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
            {
                var rql = c["rql"]!.GetValue<string>();
                JsonNode? produced;
                try { produced = JsonNode.Parse(PlanCodec.Serialize(RqlParser.Parse(rql))); }
                catch (RqlException ex) { Check(false, $"syntax: '{rql}' failed to parse: {ex.Message}"); continue; }
                if (regen) c["plan"] = produced;
                var stored = c["plan"];
                Check(stored != null && JsonEq(produced, stored), $"syntax: '{rql}' plan mismatch\n  got:  {produced?.ToJsonString()}\n  want: {stored?.ToJsonString()}");
                if (stored != null)
                {
                    try
                    {
                        var round = JsonNode.Parse(PlanCodec.Serialize(PlanCodec.Deserialize(stored.ToJsonString())));
                        Check(JsonEq(round, stored), $"codec: round-trip mismatch for '{rql}'");
                    }
                    catch (RqlException ex) { Check(false, $"codec: '{rql}' stored plan rejected: {ex.Message}"); }
                }
            }
            if (regen) Save(path, doc);
        }

        public void NegativeSyntax(bool regen)
        {
            var path = F("negative/syntax.json");
            var doc = Load(path);
            foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
            {
                var rql = c["rql"]!.GetValue<string>();
                string? code = null;
                try { RqlParser.Parse(rql); }
                catch (RqlException ex) { code = ex.Code; }
                if (regen && code != null) c["code"] = code;
                var want = c["code"]?.GetValue<string>();
                Check(code != null && code == want, $"negative syntax: '{rql}' → {code ?? "parsed OK"}, want {want}");
            }
            if (regen) Save(path, doc);
        }

        public void NegativePlans()
        {
            var doc = Load(F("negative/plans.json"));
            foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
            {
                var plan = c["plan"]!.ToJsonString();
                var want = c["code"]?.GetValue<string>();
                string? code = null;
                try { PlanCodec.Deserialize(plan); }
                catch (RqlException ex) { code = ex.Code; }
                Check(code == want, $"negative plan: {plan} → {code ?? "accepted"}, want {want ?? "accepted"}");
            }
        }

        // ------------------------------------------------------------ execution

        public RqlEngine NewEngine(Graph g)
        {
            var e = new RqlEngine(Accessors);
            e.AddSource("g", () => g);
            e.Mutations.Enabled = true;
            return e;
        }

        public void Execution(bool regen) => Cases(regen, F("execution/cases.json"), NewEngine);

        /// <summary>Spec §13 cases: the same graph with the fixture's test adapter set registered.</summary>
        public void Adapters(bool regen) => Cases(regen, F("adapters/cases.json"), g =>
        {
            var e = NewEngine(g);
            TestAdapters.Register(e);
            return e;
        });

        private void Cases(bool regen, string path, Func<Graph, RqlEngine> newEngine)
        {
            var doc = Load(path);
            var graphPath = F("cross-runtime/graph.json");
            foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
            {
                var name = c["name"]!.GetValue<string>();
                var engine = newEngine(GraphLoader.Load(graphPath));
                var budgets = c["budgets"] is JsonObject bj ? Budgets.FromJson(JsonDocument.Parse(bj.ToJsonString()).RootElement) : null;
                var options = new QueryOptions();
                if (c["options"] is JsonObject oj)
                {
                    options.AllowMutation = oj["allowMutation"]?.GetValue<bool>() ?? false;
                    options.DryRun = oj["dryRun"]?.GetValue<bool>() ?? false;
                }
                Step(c, regen, name, "plan", "expect", c["rql"]!.GetValue<string>(), engine, budgets, options);
                if (c["then"] != null)
                    Step(c, regen, name + " (then)", "thenPlan", "thenExpect", c["then"]!.GetValue<string>(), engine, null, new QueryOptions());
            }
            if (regen) Save(path, doc);
        }

        private void Step(JsonObject c, bool regen, string name, string planKey, string expectKey, string rql, RqlEngine engine, Budgets? budgets, QueryOptions options)
        {
            QueryResult result;
            if (regen)
            {
                if (RqlParser.TryParse(rql, out var parsed, out var err))
                {
                    c[planKey] = JsonNode.Parse(PlanCodec.Serialize(parsed!));
                    result = engine.Execute(parsed!, budgets, options);
                }
                else
                {
                    c.Remove(planKey);
                    result = QueryResult.Fail(new[] { err! });
                }
                c[expectKey] = Normalize(result);
                Passed++;
                return;
            }
            if (c[planKey] is JsonObject planJson)
            {
                QueryPlan plan;
                try { plan = PlanCodec.Deserialize(planJson.ToJsonString()); }
                catch (RqlException ex) { Check(false, $"execution {name}: stored plan rejected: {ex.Message}"); return; }
                result = engine.Execute(plan, budgets, options);
            }
            else
            {
                result = engine.Execute(rql, budgets, options);
            }
            var got = Normalize(result);
            var want = c[expectKey];
            Check(JsonEq(got, want), $"execution {name}: '{rql}'\n  got:  {got.ToJsonString()}\n  want: {want?.ToJsonString()}");
        }

        /// <summary>Conformance view of a result: no stats, diagnostics reduced to severity+code.</summary>
        public static JsonObject Normalize(QueryResult r)
        {
            var o = JsonNode.Parse(r.ToJson(includeStats: false))!.AsObject();
            o.Remove("rql");
            if (o["diagnostics"] is JsonArray diags)
            {
                var reduced = new JsonArray();
                foreach (var d in diags) reduced.Add(new JsonObject { ["severity"] = d!["severity"]!.GetValue<string>(), ["code"] = d["code"]!.GetValue<string>() });
                o["diagnostics"] = reduced;
            }
            return o;
        }

        /// <summary>Structural JSON equality; numbers compare by value; object key order ignored.</summary>
        public static bool JsonEq(JsonNode? a, JsonNode? b)
        {
            if (a == null || b == null) return a == null && b == null;
            switch (a)
            {
                case JsonObject oa:
                    if (!(b is JsonObject ob) || oa.Count != ob.Count) return false;
                    foreach (var kv in oa)
                    {
                        if (!ob.TryGetPropertyValue(kv.Key, out var bv) || !JsonEq(kv.Value, bv)) return false;
                    }
                    return true;
                case JsonArray aa:
                    if (!(b is JsonArray ab) || aa.Count != ab.Count) return false;
                    for (int i = 0; i < aa.Count; i++) if (!JsonEq(aa[i], ab[i])) return false;
                    return true;
                default:
                    var va = a.AsValue();
                    if (!(b is JsonValue vb)) return false;
                    var ka = va.GetValueKind();
                    var kb = vb.GetValueKind();
                    if (ka == JsonValueKind.Number && kb == JsonValueKind.Number) return va.GetValue<double>() == vb.GetValue<double>();
                    return ka == kb && va.ToJsonString() == vb.ToJsonString();
            }
        }
    }
}
