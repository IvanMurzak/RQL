/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Tests.Adapters
{
    public class UnityEngine_MeshFilter_AdapterTests : AdapterTestBase
    {
        [Test]
        public void ReadingMeshFiltersNeverInstantiatesMeshes()
        {
            var filter = GameObject.CreatePrimitive(PrimitiveType.Cube).GetComponent<MeshFilter>();
            var shared = filter.sharedMesh;
            AssertOk(Q("objects | get components | where $ is MeshFilter"));
            AssertOk(Q("roots | recurse children | get components"));
            Assert.AreSame(shared, filter.sharedMesh, "a mesh instance was created by a read");
            Assert.AreEqual(shared.name, Cell(Q("objects | get components | where $ is MeshFilter | select sharedMesh.name")));
            StringAssert.Contains("explicit-only", Schema("MeshFilter"));
        }
    }
}
