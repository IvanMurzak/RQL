#nullable enable
using System.Diagnostics;
using com.IvanMurzak.Unity.MCP.Rql.Editor;
using Rql;
using Rql.Execution;
using UnityEditor;
using UnityEditor.SceneManagement;
using Debug = UnityEngine.Debug;

namespace RqlTest.EditorTools
{
    /// <summary>
    /// Runs representative agent queries against the test scene and logs the exact text the tools return (with timing).
    /// Menu Tools/RQL/Smoke Queries, or batch: -executeMethod RqlTest.EditorTools.RqlSmoke.Run
    /// </summary>
    public static class RqlSmoke
    {
        private static readonly string[] Queries =
        {
            "objects | count",
            "roots | select $ref name children.count()",
            "objects | where name starts-with \"Enemy\" | select name active transform.position",
            "objects | get components | where $ is Light | select gameObject.name type intensity color",
            "objects | get components | where $ is Enemy | sort damage desc | select gameObject.name kind damage target.name lootTable",
            "objects | get components | where $ is Enemy and target == null | select gameObject.name kind",
            "objects | get components | where $ is Spawner | select spawnPoints.count() missing=(spawnPoints.count($ == null))",
            "objects | get components | where $ is MeshRenderer | group-by sharedMaterial.name",
            "unity.assets(\"t:Material\") | select name shader.name properties[\"_Color\"] properties[\"_Glossiness\"]",
            "objects | where name == \"Player\" | get components | select $type",
            "objects | where name == \"Enemy_06\" | select transform.localScale transform.eulerAngles",
            "objects | get components | where $ is Health and current < max | select gameObject.name current max regenPerSecond",
            "objects | where nmae == \"Player\"",
            "objects | get components | where $ is Lightt",
        };

        private static readonly string[] Mutations =
        {
            "objects | get components | where $ is Light and intensity > 2 | set intensity = intensity / 2",
            "objects | where name == \"Player\" | set transform.position = \"(1, 1, 1)\"",
        };

        [MenuItem("Tools/RQL/Smoke Queries")]
        public static void Run()
        {
            if (EditorSceneManager.GetActiveScene().path != RqlTestSceneBuilder.ScenePath)
                EditorSceneManager.OpenScene(RqlTestSceneBuilder.ScenePath);
            RqlUnity.Reset();
            var engine = RqlUnity.Engine;
            var log = new System.Text.StringBuilder("[RQL] smoke results\n");
            foreach (var q in Queries) Append(log, q, () => engine.Execute(q));
            foreach (var q in Mutations) Append(log, "(dry-run) " + q, () => engine.Execute(q, null, new QueryOptions { AllowMutation = true, DryRun = true }));
            var schema = Stopwatch.StartNew();
            var overview = RqlUnitySchema.Render(engine, null);
            log.Append("== rql-schema () [").Append(schema.ElapsedMilliseconds).Append(" ms]\n").Append(overview).Append("\n\n");
            log.Append("== rql-schema (\"Enemy\")\n").Append(RqlUnitySchema.Render(engine, "Enemy")).Append("\n\n");
            log.Append("== rql-schema (\"Light\")\n").Append(RqlUnitySchema.Render(engine, "Light")).Append('\n');
            Debug.Log(log.ToString());
        }

        private static void Append(System.Text.StringBuilder log, string label, System.Func<QueryResult> run)
        {
            var sw = Stopwatch.StartNew();
            var r = run();
            log.Append("== ").Append(label).Append(" [").Append(sw.Elapsed.TotalMilliseconds.ToString("0.0")).Append(" ms]\n")
               .Append(AgentFormat.Render(r, OutputFormat.Table)).Append("\n\n");
        }
    }
}
