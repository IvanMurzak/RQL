/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using com.IvanMurzak.Unity.MCP.Rql.Tests;
using NUnit.Framework;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Tests.Adapters
{
    public class UnityEngine_MonoBehaviour_AdapterTests : AdapterTestBase
    {
        [Test]
        public void EditorBookkeepingMembersAreHidden()
        {
            new GameObject("B").AddComponent<RqlTestBehaviour>();
            var schema = Schema("RqlTestBehaviour");
            foreach (var hidden in new[] { "useGUILayout", "runInEditMode", "didStart", "didAwake", "destroyCancellationToken" })
                StringAssert.DoesNotContain(hidden, schema, hidden);
            StringAssert.Contains("speed", schema);
        }

        [Test]
        public void EnabledAndScriptFieldsAreWritable()
        {
            var b = new GameObject("B").AddComponent<RqlTestBehaviour>();
            AssertOk(M("objects | get components | where $ is RqlTestBehaviour | set enabled = false, speed = speed * 3"));
            Assert.IsFalse(b.enabled);
            Assert.AreEqual(3f, b.speed, 1e-6f);
        }
    }
}
