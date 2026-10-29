using System;
using System.Collections.Generic;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Catalog "wave6b" (ADR-0011, ADR-0012): owned subobjects built from typed literals (instanced literal kind), counting by
    /// the meta member $type where a lookalike member (`type`) exists on a few items only (the sparse-key warning), arithmetic
    /// on plain number lists and vector functions. A separate split, so the earlier numbers stay comparable.
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
            yield return new Template
            {
                Id = "wave6b-move-scaled-offset", Category = "update", Split = S, Mutation = true,
                Make = c =>
                {
                    var go = c.All.Where(g => !(g is Character)).OrderBy(g => g.Id).Skip(c.Rng.Next(20)).First();
                    int dx = 1 + c.Rng.Next(4), dz = 1 + c.Rng.Next(4), k = 2 + c.Rng.Next(4);
                    return T($"Move the object with Id {go.Id} by the offset ({dx}, 0, {dz}) multiplied by {k}, i.e. add {k} times that offset to "
                        + "its Transform.Position.", FmtState,
                        Expected.State(sb =>
                        {
                            var t = ById(sb, go.Id).Transform;
                            var p = t.Position;
                            t.Position = new Vector3(p.x + dx * k, p.y, p.z + dz * k);
                        }),
                        h => Mutate(h, $"objects | where Id == {go.Id} | set Transform.Position = Transform.Position + [{dx}, 0, {dz}] * {k}"));
                },
            };
            yield return new Template
            {
                Id = "wave6b-distance", Category = "aggregate", Split = S,
                Make = c =>
                {
                    var all = c.All.OrderBy(g => g.Id).ToList();
                    var a = all[c.Rng.Next(all.Count)];
                    var b = all[c.Rng.Next(all.Count)];
                    if (a == b) return null;
                    double dx = (double)a.Transform.Position.x - b.Transform.Position.x, dy = (double)a.Transform.Position.y - b.Transform.Position.y,
                        dz = (double)a.Transform.Position.z - b.Transform.Position.z;
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    return T($"What is the straight-line distance between the positions (Transform.Position) of the objects with Id {a.Id} and Id {b.Id}? "
                        + "Round to 2 decimals.", FmtNumber, Expected.Num(d, 0.0051),
                        h => Scalar(h, $"objects | where Id == {a.Id} | sum distance(Transform.Position, "
                            + $"ref(\"{b.GetType().Name}#{b.Id}\").Transform.Position)"));
                },
            };
        }
    }
}
