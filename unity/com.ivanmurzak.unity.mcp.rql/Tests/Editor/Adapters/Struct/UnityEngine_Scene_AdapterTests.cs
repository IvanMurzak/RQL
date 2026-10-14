/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Tests.Adapters
{
    public class UnityEngine_Scene_AdapterTests : AdapterTestBase
    {
        [Test]
        public void ScenesExposeSafeMembersAndTheirRoots()
        {
            new GameObject("RootA");
            new GameObject("RootB");
            Assert.AreEqual("true", Cell(Q("scenes | select isLoaded")));
            Assert.AreEqual("2", Cell(Q("scenes | select rootCount")));
            Assert.AreEqual("2", Cell(Q("scenes | select roots.count()")));
            Assert.AreEqual("RootA", Cell(Q("scenes | get roots | where name == \"RootA\" | select name")));
        }

        [Test]
        public void OnlyWhitelistedMembersAreVisible()
        {
            var schema = Schema("Scene");
            StringAssert.Contains("rootCount", schema);
            StringAssert.DoesNotContain("handle", schema);
            Assert.IsTrue(Warns(Q("scenes | select handle"), "unknown-member"));
        }
    }
}
