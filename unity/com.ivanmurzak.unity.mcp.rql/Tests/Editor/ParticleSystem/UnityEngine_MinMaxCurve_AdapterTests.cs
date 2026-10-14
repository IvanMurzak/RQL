/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Particles.Tests
{
    public class UnityEngine_MinMaxCurve_AdapterTests : ParticleTestBase
    {
        [Test]
        public void ANumberIsAConstant()
        {
            Assert.IsTrue(M(P + " | set main.startLifetime = 3").Ok);
            Assert.AreEqual(ParticleSystemCurveMode.Constant, Ps.main.startLifetime.mode);
            Assert.AreEqual(3f, Ps.main.startLifetime.constant, 1e-6f);
            Assert.AreEqual("3", Cell(Q(P + " | select main.startLifetime")));
        }

        [Test]
        public void TwoNumbersAreARandomRange()
        {
            Assert.IsTrue(M(P + " | set emission.rateOverTime = [5, 25]").Ok);
            Assert.AreEqual(ParticleSystemCurveMode.TwoConstants, Ps.emission.rateOverTime.mode);
            Assert.AreEqual(5f, Ps.emission.rateOverTime.constantMin, 1e-6f);
            Assert.AreEqual(25f, Ps.emission.rateOverTime.constantMax, 1e-6f);
            StringAssert.Contains("[5,25]", Cell(Q(P + " | select emission.rateOverTime")).Replace(" ", ""));
        }

        [Test]
        public void CurvesAreReportedByMode()
        {
            var main = Ps.main;
            main.startSize = new ParticleSystem.MinMaxCurve(2f, AnimationCurve.Linear(0, 0, 1, 1));
            StringAssert.Contains("Curve", Cell(Q(P + " | select main.startSize")));
        }

        [Test]
        public void OtherValuesAreRejected()
        {
            var bad = M(P + " | set main.startLifetime = \"long\"");
            Assert.IsFalse(bad.Ok);
            StringAssert.Contains("expected a number or [min, max]", Table(bad));
            var badRange = M(P + " | set main.startLifetime = [1, \"x\"]");
            Assert.IsFalse(badRange.Ok);
            StringAssert.Contains("[1]", Table(badRange));
        }
    }
}
