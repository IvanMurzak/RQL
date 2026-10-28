using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Catalog "wave6" (ADR-0010): calls whose arguments are bound by name from one object literal
    /// (<c>call Spawn({name: "A", layer: 2})</c>), and a dry run through overloads that must leave the world unchanged
    /// (overload resolution constructs nothing; scene objects are never built from literals). A separate split, so the
    /// earlier numbers stay comparable.
    /// </summary>
    public static partial class Catalog
    {
        public static readonly int[] Wave6Seeds = { 61, 62 };

        /// <summary>A reference dry run: must succeed, and must be reported as a dry run.</summary>
        private static string DryRunMutate(SampleHost h, string rql)
        {
            var r = h.Mutate(rql, dryRun: true);
            if (!r.Ok || !r.DryRun) throw new InvalidOperationException("reference dry run failed: " + rql + " → " + string.Join("; ", r.Diagnostics));
            return "ok";
        }

        private static IEnumerable<Template> TemplatesWave6()
        {
            const string S = "wave6";
            yield return new Template
            {
                Id = "wave6-spawn-named", Category = "create", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    int layer = 1 + c.Rng.Next(8), x = c.Rng.Next(20), z = c.Rng.Next(20);
                    string name = "Beacon_" + c.Rng.Next(100);
                    return T($"In scene {Q(s.Name)}, spawn one object named {Q(name)} on layer {layer} at position x = {x}, y = 0, z = {z} with the "
                        + "scene's Spawn method. Leave its other options (tag) at their defaults.", FmtState,
                        Expected.State(sb => sb.World.Scenes.First(sc => sc.Id == s.Id).Spawn(name, "Untagged", layer, x, 0, z)),
                        h => Mutate(h, $"world.Scenes | where Id == {s.Id} | call Spawn({{name: {Q(name)}, layer: {layer}, x: {x}, z: {z}}})"));
                },
            };
            yield return new Template
            {
                Id = "wave6-spawn-bulk-named", Category = "create", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    int n = 8 + c.Rng.Next(12);
                    return T($"In scene {Q(s.Name)}, spawn {n} objects named Crate_0 … Crate_{n - 1} with the scene's Spawn method, each with tag "
                        + "\"Crate\" and placed in a row along x: Crate_i at x = i * 2, y = 0, z = 0. Leave the layer at its default.", FmtState,
                        Expected.State(sb =>
                        {
                            var scene = sb.World.Scenes.First(sc => sc.Id == s.Id);
                            for (int i = 0; i < n; i++) scene.Spawn("Crate_" + i, "Crate", 0, i * 2, 0, 0);
                        }),
                        h => Mutate(h, $"range(0, {n}) | call ref(\"Scene#{s.Id}\").Spawn({{name: \"Crate_\" + $, tag: \"Crate\", x: $ * 2}})", 200));
                },
            };
            yield return new Template
            {
                Id = "wave6-dry-run-placement", Category = "create", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene();
                    int x = c.Rng.Next(20), y = 1 + c.Rng.Next(5);
                    return T($"Check, with a dry run only, that spawning an object named \"Probe\" in scene {Q(s.Name)} at the placement "
                        + $"Position (x = {x}, y = {y}, z = 0), tag \"Probe\" (the Spawn overload that takes a Placement) would be accepted. "
                        + "Do not change the world: nothing may be created.", FmtState,
                        Expected.State(_ => { }),
                        h => DryRunMutate(h, $"world.Scenes | where Id == {s.Id} | call Spawn(\"Probe\", {{Position: {{x: {x}, y: {y}, z: 0}}, Tag: \"Probe\"}})"));
                },
            };
        }
    }
}
