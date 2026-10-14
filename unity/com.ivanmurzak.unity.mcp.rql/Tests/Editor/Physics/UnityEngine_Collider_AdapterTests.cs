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

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Physics3D.Tests
{
    public class UnityEngine_Collider_AdapterTests
    {
        private BoxCollider _box = null!;

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RqlUnity.Reset();
            _box = new GameObject("Crate").AddComponent<BoxCollider>();
        }

        private static string Table(QueryResult r) => AgentFormat.Render(r, OutputFormat.Table);

        [Test]
        public void ThePhysicsModuleIsDiscovered()
        {
            CollectionAssert.Contains(RqlUnity.Engine.Modules.Select(m => m.Module).ToList(), typeof(PhysicsModule).FullName);
            Assert.IsTrue(RqlUnity.Engine.Modules.All(m => m.Ok), string.Join("\n", RqlUnity.Engine.Modules));
        }

        [Test]
        public void ReadingCollidersNeverInstantiatesPhysicsMaterials()
        {
            var r = RqlUnity.Engine.Execute("objects | get components | where $ is BoxCollider");
            Assert.IsTrue(r.Ok, Table(r));
            Assert.IsTrue(RqlUnity.Engine.Execute("roots | recurse children | get components").Ok);
            Assert.IsNull(_box.sharedMaterial, "a physics material instance was created by a read");
            StringAssert.Contains("explicit-only", RqlUnitySchema.Render(RqlUnity.Engine, "BoxCollider"));
        }

        [Test]
        public void ColliderMembersAreWritable()
        {
            var r = RqlUnity.Engine.Execute("objects | get components | where $ is BoxCollider | set isTrigger = true, size = \"(2, 2, 2)\"",
                null, new QueryOptions { AllowMutation = true });
            Assert.IsTrue(r.Ok, Table(r));
            Assert.IsTrue(_box.isTrigger);
            Assert.AreEqual(Vector3.one * 2, _box.size);
        }
    }
}
