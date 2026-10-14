/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using System.Linq;
using NUnit.Framework;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Particles.Tests
{
    public class ParticleSystemModuleTests : ParticleTestBase
    {
        [Test]
        public void TheOptionalModuleIsDiscovered()
        {
            var modules = RqlUnity.Engine.Modules.Select(m => m.Module).ToList();
            CollectionAssert.Contains(modules, typeof(ParticleSystemModule).FullName);
            CollectionAssert.Contains(modules, typeof(Modules.UnityCoreModule).FullName);
            Assert.IsTrue(RqlUnity.Engine.Modules.All(m => m.Ok), string.Join("\n", RqlUnity.Engine.Modules));
        }

        [Test]
        public void SchemaFindsNestedParticleTypes()
        {
            // ParticleSystem.MinMaxCurve / MainModule are nested types: the type index must include them.
            var curve = RqlUnitySchema.Render(RqlUnity.Engine, "MinMaxCurve");
            StringAssert.DoesNotContain("unknown-type", curve);
            StringAssert.Contains("constantMin", curve);
            StringAssert.DoesNotContain("unknown-type", RqlUnitySchema.Render(RqlUnity.Engine, "MainModule"));
        }
    }
}
