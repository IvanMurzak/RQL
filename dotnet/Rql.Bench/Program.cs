using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rql.Execution;
using Rql.Sample;

namespace Rql.Bench
{
    /// <summary>
    /// Tier A: deterministic executor benchmarks (no LLM). For each query: median wall time over N runs,
    /// bytes allocated per run, visited objects, members read, encoded output size.
    ///   rql-bench [--size large|huge] [--runs N] [--out results.json]
    /// </summary>
    public static class Program
    {
        private static readonly (string name, string rql)[] Queries =
        {
            ("count-all", "objects | count"),
            ("filter-type-scalar", "objects | where $ is Enemy and Health < 20 and Health > 0 | count"),
            ("filter-nested-any", "objects | where Components.any($ is LightComponent and Enabled == false) | count"),
            ("top-n", "objects | where $ is Enemy | sort Level desc | first 10 | select $ref Name Level"),
            ("first-lazy", "objects | where $ is Enemy and IsElite | first 1 | select $ref Name"),
            ("group-by", "objects | where $ is Enemy | group-by Faction | sort count desc"),
            ("project-50", "objects | where $ is Character | select $ref Name Health Transform.Position"),
            ("recurse-children", "world.Scenes | get Objects | recurse Children max-depth=16 | count"),
            ("recurse-generic", "world | recurse max-depth=8 | count"),
            ("path-tracking", "world.Scenes | get Objects | recurse Children max-depth=16 | where Name starts-with \"Chest\" | first 5 | select $path"),
            ("string-ops", "objects | where Name contains \"oak\" or Name matches \"^Torch_0[0-9]+$\" | count"),
            ("dict-access", "objects | get Components | where $ is ScriptComponent and ScriptName == \"Chest\" and Properties.locked == true | count"),
            ("app-join", "app.Orders | where Status == \"Delivered\" and Customer.Profile.Country == \"DE\" | sum Total"),
        };

        public static int Main(string[] args)
        {
            var size = WorldSize.Large;
            int runs = 15;
            string? outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--size") size = WorldGenerator.ParseSize(args[++i]);
                else if (args[i] == "--runs") runs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--out") outPath = args[++i];
            }
            var sb = WorldGenerator.Create(1, size);
            int objects = WorldGenerator.AllObjects(sb.World).Count();
            Console.WriteLine($"world size={size} objects={objects} runtime={Environment.Version} os={Environment.OSVersion.Platform} cpus={Environment.ProcessorCount}");
            var rows = new List<Dictionary<string, object>>();
            var hosts = new[] { new SampleHost(sb, accessors: AccessorMode.Compiled), new SampleHost(sb, accessors: AccessorMode.Reflection) };
            Console.WriteLine($"\n{"query",-20} {"mode",-10} {"median ms",10} {"p90 ms",9} {"alloc KB",10} {"visited",9} {"members",9} {"out B",8}");
            foreach (var (name, rql) in Queries)
            {
                // Long warmup so measured runs execute tier-1 (PGO-optimized) code; modes are interleaved per run
                // so JIT/GC drift affects both equally.
                for (int w = 0; w < 60; w++) foreach (var h in hosts) h.Query(rql);
                var times = new[] { new List<double>(), new List<double>() };
                var alloc = new long[2];
                var last = new Rql.QueryResult?[2];
                for (int r = 0; r < runs; r++)
                {
                    for (int m = 0; m < 2; m++)
                    {
                        long a0 = GC.GetAllocatedBytesForCurrentThread();
                        var sw = Stopwatch.StartNew();
                        last[m] = hosts[m].Query(rql);
                        times[m].Add(sw.Elapsed.TotalMilliseconds);
                        alloc[m] += GC.GetAllocatedBytesForCurrentThread() - a0;
                    }
                }
                for (int m = 0; m < 2; m++)
                {
                    var res = last[m]!;
                    if (!res.Ok) { Console.WriteLine($"  {name}: FAILED {string.Join("; ", res.Diagnostics)}"); continue; }
                    times[m].Sort();
                    var outBytes = res.ToJson(includeStats: false).Length;
                    double med = times[m][times[m].Count / 2], p90 = times[m][(int)(times[m].Count * 0.9)];
                    var mode = m == 0 ? "Compiled" : "Reflection";
                    Console.WriteLine($"{(m == 0 ? name : ""),-20} {mode,-10} {med,10:0.000} {p90,9:0.000} {alloc[m] / runs / 1024.0,10:0.0} {res.Stats.Visited,9} {res.Stats.MembersRead,9} {outBytes,8}");
                    rows.Add(new Dictionary<string, object>
                    {
                        ["mode"] = mode, ["query"] = name, ["rql"] = rql, ["medianMs"] = Math.Round(med, 4), ["p90Ms"] = Math.Round(p90, 4),
                        ["allocKB"] = Math.Round(alloc[m] / runs / 1024.0, 1), ["visited"] = res.Stats.Visited, ["membersRead"] = res.Stats.MembersRead, ["outputBytes"] = outBytes,
                    });
                }
            }

            // Contrast: what "serialize everything, filter later" costs on the same graph.
            // (IgnoreCycles is not usable: shared non-cyclic references re-serialize whole subgraphs and ran out of memory
            // on the Large world; Preserve emits $id/$ref and is the realistic "dump everything" baseline.)
            var opts = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve, IncludeFields = true, MaxDepth = 512 };
            var sw2 = Stopwatch.StartNew();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            var json = JsonSerializer.Serialize(sb.World, opts);
            double serMs = sw2.Elapsed.TotalMilliseconds;
            long serAlloc = GC.GetAllocatedBytesForCurrentThread() - a1;
            Console.WriteLine($"\nnaive full-graph JSON (System.Text.Json, ReferenceHandler.Preserve): {json.Length / 1024.0 / 1024.0:0.00} MB, {serMs:0.0} ms, {serAlloc / 1024.0 / 1024.0:0.0} MB allocated");
            if (outPath != null)
            {
                File.WriteAllText(outPath, JsonSerializer.Serialize(new
                {
                    size = size.ToString(), objects, runs, runtime = Environment.Version.ToString(), cpus = Environment.ProcessorCount,
                    naiveFullGraphJson = new { bytes = json.Length, ms = Math.Round(serMs, 1), allocBytes = serAlloc },
                    results = rows,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return 0;
        }
    }
}
