using System;
using System.Linq;
using Rql.Execution;

namespace Rql.Sample
{
    /// <summary>
    /// rql-sample — run RQL against the deterministic sandbox.
    ///   rql-sample [--seed N] [--size small|medium|large|huge] [--format table|json] [--mutate] [--dry-run] "query"
    ///   rql-sample schema [Type]
    ///   rql-sample plan "query"          (print the canonical QueryPlan JSON)
    ///   rql-sample repl
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            int seed = 1;
            var size = WorldSize.Medium;
            var format = OutputFormat.Table;
            bool mutate = false, dryRun = false;
            var rest = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed": seed = int.Parse(args[++i]); break;
                    case "--size": size = WorldGenerator.ParseSize(args[++i]); break;
                    case "--format": format = AgentFormat.Parse(args[++i]); break;
                    case "--mutate": mutate = true; break;
                    case "--dry-run": mutate = true; dryRun = true; break;
                    default: rest.Add(args[i]); break;
                }
            }
            if (rest.Count == 0)
            {
                Console.Error.WriteLine("usage: rql-sample [--seed N] [--size small|medium|large|huge] [--format table|json] [--mutate|--dry-run] \"query\" | schema [Type] | plan \"query\" | repl");
                return 2;
            }
            var host = new SampleHost(seed, size, allowMutations: mutate);
            switch (rest[0])
            {
                case "schema":
                    Console.WriteLine(host.Schema(rest.Count > 1 ? rest[1] : null));
                    return 0;
                case "plan":
                    try
                    {
                        Console.WriteLine(PlanCodec.Serialize(RqlParser.Parse(string.Join(" ", rest.Skip(1))), indented: true));
                        return 0;
                    }
                    catch (RqlException ex)
                    {
                        Console.Error.WriteLine(ex.ToDiagnostic());
                        return 1;
                    }
                case "repl":
                    Console.Error.WriteLine($"RQL sandbox seed={seed} size={size}. Empty line exits.");
                    while (true)
                    {
                        Console.Error.Write("rql> ");
                        var line = Console.ReadLine();
                        if (string.IsNullOrWhiteSpace(line)) return 0;
                        var rr = mutate ? host.Mutate(line, dryRun) : host.Query(line);
                        Console.WriteLine(AgentFormat.Render(rr, format));
                        Console.Error.WriteLine($"  ({rr.Stats.ElapsedMs:0.###} ms, visited {rr.Stats.Visited}, members read {rr.Stats.MembersRead})");
                    }
            }
            var text = string.Join(" ", rest);
            var r = mutate ? host.Mutate(text, dryRun) : host.Query(text);
            Console.WriteLine(AgentFormat.Render(r, format));
            Console.Error.WriteLine($"({r.Stats.ElapsedMs:0.###} ms, visited {r.Stats.Visited}, members read {r.Stats.MembersRead})");
            return r.Ok ? 0 : 1;
        }
    }
}
