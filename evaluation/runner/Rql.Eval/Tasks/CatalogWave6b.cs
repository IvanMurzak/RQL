using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Catalog "wave6b" (ADR-0011): owned subobjects built from typed literals (instanced literal kind), and counting by the meta
    /// member $type where a lookalike member (`type`) exists on a few items only (the sparse-key warning). A separate split, so
    /// the earlier numbers stay comparable.
    /// </summary>
    public static partial class Catalog
    {
        public static readonly int[] Wave6bSeeds = { 63, 64 };

        private static IEnumerable<Template> TemplatesWave6b()
        {
            const string S = "wave6b";
            yield return new Template
            {
                Id = "wave6b-audio-filter", Category = "update", Split = S, Mutation = true,
                Make = c =>
                {
                    var go = c.All.Where(g => g.Components.Any(k => k is AudioSourceComponent)).OrderBy(g => g.Id).Skip(c.Rng.Next(3)).FirstOrDefault();
                    if (go == null) return null;
                    int cutoff = 400 + 100 * c.Rng.Next(10);
                    return T($"Add a low-pass filter with a cutoff of {cutoff} Hz to the audio source of the object with Id {go.Id} (keep any filters it "
                        + "already has; leave the filter's other settings at their defaults).", FmtState,
                        Expected.State(sb =>
                        {
                            var src = ById(sb, go.Id).Components.OfType<AudioSourceComponent>().First();
                            src.Filters.Add(new LowPassFilter { Cutoff = cutoff, Owner = src });
                        }),
                        h => Mutate(h, $"objects | where Id == {go.Id} | get Components | where $ is AudioSourceComponent "
                            + $"| set Filters = append(Filters, {{$type: \"LowPassFilter\", Cutoff: {cutoff}}})"));
                },
            };
            yield return new Template
            {
                Id = "wave6b-most-common-component", Category = "aggregate", Split = S,
                Make = c =>
                {
                    var s = c.PickScene();
                    var counts = c.In(s).SelectMany(g => g.Components).GroupBy(k => k.GetType().Name).Select(g => (g.Key, n: g.Count()))
                        .OrderByDescending(t => t.n).ToList();
                    if (counts.Count < 2 || counts[0].n == counts[1].n) return null;
                    return T($"Which component type (its type name, e.g. RendererComponent) is the most common among the components of the objects "
                        + $"in scene {Q(s.Name)}?", "Answer with just the type name.", Expected.Str(counts[0].Key),
                        h => Rows(h, $"objects | where Scene.Name == {Q(s.Name)} | get Components | group-by $type | sort count desc | first 1")[0][0]!);
                },
            };
        }
    }
}
