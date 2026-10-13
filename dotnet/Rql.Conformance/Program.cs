using System;

namespace Rql.Conformance
{
    /// <summary>rql-conformance check | regen — the shared golden-vector suite (fixtures/).</summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            bool regen = args.Length > 0 && args[0] == "regen";
            var suite = new ConformanceSuite();
            suite.RunAll(regen);
            foreach (var f in suite.Failures) Console.WriteLine("FAIL " + f);
            Console.WriteLine($"{(regen ? "regenerated" : "checked")}: {suite.Passed} passed, {suite.Failures.Count} failed");
            return suite.Failures.Count == 0 ? 0 : 1;
        }
    }
}
