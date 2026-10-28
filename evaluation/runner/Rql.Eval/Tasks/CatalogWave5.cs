using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rql.Sample;

namespace Rql.Eval.Tasks
{
    /// <summary>
    /// Catalog "wave5": component-wise vector arithmetic (spec §4.2: <c>set Transform.Scale = Transform.Scale * 1.5</c>) and absent
    /// map entries reading as null (spec §3: <c>Properties["requiredKey"] is null</c>). A separate split, so the dev / holdout /
    /// holdout2 / bulk numbers stay comparable with earlier runs.
    /// </summary>
    public static partial class Catalog
    {
        public static readonly int[] Wave5Seeds = { 51, 52 };

        /// <summary>A float as RQL computes with it (the double of its shortest decimal, spec §1.1).</summary>
        private static double Widened(float f) => double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        private static IEnumerable<Template> TemplatesWave5()
        {
            const string S = "wave5";
            yield return new Template
            {
                Id = "wave5-scale-up", Category = "vector", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).OfType<Enemy>().Any());
                    var ids = c.In(s).OfType<Enemy>().Select(e => e.Id).ToHashSet();
                    if (ids.Count == 0) return null;
                    return T($"Make every enemy in scene {Q(s.Name)} 1.5 times bigger: multiply all three components of its Transform.Scale by 1.5.", FmtState,
                        Expected.State(sb =>
                        {
                            foreach (var e in WorldGenerator.AllObjects(sb.World).OfType<Enemy>().Where(e => ids.Contains(e.Id)))
                            {
                                var v = e.Transform.Scale;
                                e.Transform.Scale = new Vector3((float)(Widened(v.x) * 1.5), (float)(Widened(v.y) * 1.5), (float)(Widened(v.z) * 1.5));
                            }
                        }),
                        h => Mutate(h, $"objects | where $ is Enemy and Scene.Name == {Q(s.Name)} | set Transform.Scale = Transform.Scale * 1.5", 200));
                },
            };
            yield return new Template
            {
                Id = "wave5-raise-lights", Category = "vector", Split = S, Mutation = true,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).Any(g => g.Components.OfType<LightComponent>().Any()));
                    var ids = c.In(s).Where(g => g.Components.OfType<LightComponent>().Any()).Select(g => g.Id).ToHashSet();
                    if (ids.Count == 0) return null;
                    return T($"Raise every object in scene {Q(s.Name)} that has a light component by 2.5 units: add (0, 2.5, 0) to its Transform.Position.", FmtState,
                        Expected.State(sb =>
                        {
                            foreach (var g in WorldGenerator.AllObjects(sb.World).Where(g => ids.Contains(g.Id)))
                            {
                                var p = g.Transform.Position;
                                g.Transform.Position = new Vector3((float)(Widened(p.x) + 0), (float)(Widened(p.y) + 2.5), (float)(Widened(p.z) + 0));
                            }
                        }),
                        h => Mutate(h, $"objects | where Scene.Name == {Q(s.Name)} and Components.any($ is LightComponent) | set Transform.Position = Transform.Position + [0, 2.5, 0]", 200));
                },
            };
            yield return new Template
            {
                Id = "wave5-missing-property", Category = "map", Split = S,
                Make = c =>
                {
                    int n = c.All.SelectMany(g => g.Components).OfType<ScriptComponent>()
                        .Count(sc => !sc.Properties.TryGetValue("requiredKey", out var v) || v == null);
                    return T("How many script components in the whole world have no key requirement, i.e. their \"requiredKey\" script property is "
                        + "missing or null?", FmtNumber, Expected.Num(n),
                        h => Scalar(h, "objects | get Components | where $ is ScriptComponent and Properties[\"requiredKey\"] is null | count"));
                },
            };
            yield return new Template
            {
                Id = "wave5-has-property", Category = "map", Split = S,
                Make = c =>
                {
                    var s = c.PickScene(sc => c.In(sc).Any(g => g.Components.OfType<ScriptComponent>().Any(x => x.Properties.ContainsKey("keyId"))));
                    var refs = c.In(s).Where(g => g.Components.OfType<ScriptComponent>().Any(x => x.Properties.ContainsKey("keyId"))).Select(RefOf).ToList();
                    if (refs.Count == 0) return null;
                    return T($"List the objects in scene {Q(s.Name)} with a script component that has a \"keyId\" script property at all (even a null one).",
                        FmtRefs, Expected.Refs(refs),
                        h => Col0(h, $"objects | where Scene.Name == {Q(s.Name)} and Components.any($ is ScriptComponent and has(Properties[\"keyId\"])) | select $ref"));
                },
            };
        }
    }
}
