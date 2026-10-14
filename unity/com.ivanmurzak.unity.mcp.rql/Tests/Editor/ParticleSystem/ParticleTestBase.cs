/*
 * RQL tools for Unity-MCP — experimental, local only.
 * Copyright (c) 2026 Ivan Murzak. Not for redistribution.
 */

#nullable enable
using NUnit.Framework;
using Rql;
using Rql.Execution;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace com.IvanMurzak.Unity.MCP.Rql.Editor.Particles.Tests
{
    /// <summary>A fresh scene with one particle system ("Sparks") per test.</summary>
    public abstract class ParticleTestBase
    {
        protected ParticleSystem Ps = null!;
        protected const string P = "objects | get components | where $ is ParticleSystem";

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RqlUnity.Reset();
            Ps = new GameObject("Sparks").AddComponent<ParticleSystem>();
        }

        protected static QueryResult Q(string rql) => RqlUnity.Engine.Execute(rql);

        protected static QueryResult M(string rql) => RqlUnity.Engine.Execute(rql, null, new QueryOptions { AllowMutation = true });

        protected static long Count(string rql)
        {
            var r = Q(rql);
            Assert.IsTrue(r.Ok, Table(r));
            return r.Value!.GetValue<long>();
        }

        protected static string Table(QueryResult r) => AgentFormat.Render(r, OutputFormat.Table);

        protected static string Cell(QueryResult r)
        {
            Assert.IsTrue(r.Ok, Table(r));
            return r.Rows![0][0]!.ToJsonString().Trim('"');
        }
    }
}
