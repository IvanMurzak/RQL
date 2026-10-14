/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System.Linq;
using NUnit.Framework;
using Rql;
using Rql.Execution;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Particles.Tests
{
    public class ParticleSystemModuleTests
    {
        private ParticleSystem _ps = null!;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RqlUnity.Reset();
            _ps = new GameObject("Sparks").AddComponent<ParticleSystem>();
        }

        private static QueryResult M(string rql) =>
            RqlUnity.Engine.Execute(rql, null, new QueryOptions { AllowMutation = true });

        private static string Table(QueryResult r) => AgentFormat.Render(r, OutputFormat.Table);

        [Test]
        public void TheOptionalModuleIsDiscovered()
        {
            var modules = RqlUnity.Engine.Modules.Select(m => m.Module).ToList();
            CollectionAssert.Contains(modules, typeof(ParticleSystemModule).FullName);
            CollectionAssert.Contains(modules, typeof(Modules.UnityCoreModule).FullName);
            Assert.IsTrue(RqlUnity.Engine.Modules.All(m => m.Ok), string.Join("\n", RqlUnity.Engine.Modules));
        }

        [Test]
        public void HandleStructsWriteThroughGetOnlyModules()
        {
            var r = M("objects | get components | where $ is ParticleSystem | set main.startLifetime = 3, emission.rateOverTime = [5, 25]");
            Assert.IsTrue(r.Ok, Table(r));
            Assert.AreEqual(3f, _ps.main.startLifetime.constant, 1e-6f);
            Assert.AreEqual(ParticleSystemCurveMode.TwoConstants, _ps.emission.rateOverTime.mode);
            Assert.AreEqual(25f, _ps.emission.rateOverTime.constantMax, 1e-6f);
        }

        [Test]
        public void MinMaxGradientReusesColorAndGradientThroughTheKernel()
        {
            // A color literal in the Color adapter's own format, which the MinMaxGradient adapter never parses itself.
            var color = M("objects | get components | where $ is ParticleSystem | set main.startColor = \"#ff0000\"");
            Assert.IsTrue(color.Ok, Table(color));
            Assert.AreEqual(Color.red, _ps.main.startColor.color);

            var two = M("objects | get components | where $ is ParticleSystem | set main.startColor = [\"#ff0000\", \"(0, 0, 1, 1)\"]");
            Assert.IsTrue(two.Ok, Table(two));
            Assert.AreEqual(ParticleSystemGradientMode.TwoColors, _ps.main.startColor.mode);

            var gradient = M("objects | get components | where $ is ParticleSystem | set main.startColor = [[\"#ff0000\", 0], [\"#00ff00\", 0.5], [\"#0000ff\", 1]]");
            Assert.IsTrue(gradient.Ok, Table(gradient));
            Assert.AreEqual(ParticleSystemGradientMode.Gradient, _ps.main.startColor.mode);
            Assert.AreEqual(3, _ps.main.startColor.gradient.colorKeys.Length);

            var read = RqlUnity.Engine.Execute("objects | get components | where $ is ParticleSystem | select c=main.startColor");
            StringAssert.Contains("(0, 1, 0, 1)", Table(read)); // the gradient's keys, encoded by the Gradient and Color adapters

            var bad = M("objects | get components | where $ is ParticleSystem | set main.startColor = [[\"#ff0000\", 0], [\"nope\", 1]]");
            Assert.IsFalse(bad.Ok);
            StringAssert.Contains("[1].color", Table(bad));
        }
    }
}
