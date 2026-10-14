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
    public class UnityEngine_Component_AdapterTests : AdapterTestBase
    {
        [Test]
        public void BackPointersAreReadable()
        {
            new GameObject("Owner", typeof(Light));
            Assert.AreEqual("Owner", Cell(Q("objects | get components | where $ is Light | select gameObject.name")));
            Assert.AreEqual("Owner", Cell(Q("objects | get components | where $ is Light | select transform.gameObject.name")));
        }

        [Test]
        public void DestroyRemovesTheComponentUndoably()
        {
            var go = new GameObject("Owner", typeof(Light));
            AssertOk(M("objects | get components | where $ is Light | call Destroy()"));
            Assert.IsNull(go.GetComponent<Light>());
            Undo.PerformUndo();
            Assert.IsNotNull(go.GetComponent<Light>());
        }

        [Test]
        public void AGameObjectStandsForItsComponentInComponentFields()
        {
            var holder = new GameObject("Holder").AddComponent<RqlTestBehaviour>();
            var lamp = new GameObject("Lamp", typeof(Light));
            var lampRef = Cell(Q("objects | where name == \"Lamp\" | select $ref"));
            AssertOk(M($"objects | get components | where $ is RqlTestBehaviour | set lamp = ref(\"{lampRef}\"), anchor = ref(\"{lampRef}\")"));
            Assert.AreSame(lamp.GetComponent<Light>(), holder.lamp);
            Assert.AreSame(lamp.transform, holder.anchor);
        }

        [Test]
        public void AGameObjectWithoutTheComponentIsRejected()
        {
            var holder = new GameObject("Holder").AddComponent<RqlTestBehaviour>();
            new GameObject("Empty");
            var emptyRef = Cell(Q("objects | where name == \"Empty\" | select $ref"));
            AssertFails(M($"objects | get components | where $ is RqlTestBehaviour | set lamp = ref(\"{emptyRef}\")"), "coercion-failed", "has no Light");
            Assert.IsNull(holder.lamp);
        }
    }
}
