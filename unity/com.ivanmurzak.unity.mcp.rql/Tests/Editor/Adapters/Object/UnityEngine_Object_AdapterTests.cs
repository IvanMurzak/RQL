/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using com.IvanMurzak.Unity.MCP.Rql.Tests;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Tests.Adapters
{
    public class UnityEngine_Object_AdapterTests : AdapterTestBase
    {
        [Test]
        public void RefsAreTypeAndIdAndResolveBack()
        {
            var go = new GameObject("Alpha");
            var light = go.AddComponent<Light>();
            var goRef = Cell(Q("objects | where name == \"Alpha\" | select $ref"));
            StringAssert.StartsWith("GameObject#", goRef);
            StringAssert.StartsWith("Light#", Cell(Q("objects | get components | where $ is Light | select $ref")));
            Assert.AreEqual("Alpha", Cell(Q($"ref(\"{goRef}\") | select name")));
            AssertFails(Q("ref(\"GameObject#999999999999\") | select name"), "stale-ref");
            Assert.IsNotNull(light);
        }

        [Test]
        public void DestroyedObjectsReadAsNull()
        {
            var holder = new GameObject("Holder").AddComponent<RqlTestBehaviour>();
            var lamp = new GameObject("Lamp");
            holder.lamp = lamp.AddComponent<Light>();
            Assert.AreEqual("Lamp", Cell(Q("objects | get components | where $ is RqlTestBehaviour | select lamp.gameObject.name")));
            Object.DestroyImmediate(lamp);
            Assert.AreEqual("Holder", Cell(Q("objects | get components | where $ is RqlTestBehaviour and lamp == null | select gameObject.name")));
            Assert.AreEqual("null", Cell(Q("objects | get components | where $ is RqlTestBehaviour | select lamp")));
        }

        [Test]
        public void PrivateSerializedFieldsAreMembersObsoleteAndNonSerializedAreHidden()
        {
            var b = new GameObject("B").AddComponent<RqlTestBehaviour>();
            Assert.AreEqual("7", Cell(Q("objects | get components | where $ is RqlTestBehaviour | select secret")));
            AssertOk(M("objects | get components | where $ is RqlTestBehaviour | set secret = 9"));
            Assert.AreEqual(9, new SerializedObject(b).FindProperty("secret").intValue);

            var schema = Schema("RqlTestBehaviour");
            StringAssert.Contains("secret", schema);
            StringAssert.DoesNotContain("transient", schema);
            StringAssert.DoesNotContain("legacy", schema);
            StringAssert.DoesNotContain("hideFlags", schema);
            Assert.IsTrue(Warns(Q("objects | get components | where $ is RqlTestBehaviour | select legacy"), "unknown-member"));
        }

        [Test]
        public void SummariesShowTheName()
        {
            new GameObject("Shown");
            StringAssert.Contains("Shown", Table(Q("objects | where name == \"Shown\" | select $")));
        }
    }
}
