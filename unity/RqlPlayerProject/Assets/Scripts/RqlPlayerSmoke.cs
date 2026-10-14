#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Rql;
using Rql.Execution;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RqlPlayer
{
    /// <summary>
    /// RQL inside a built player (no Editor): builds a small scene, runs queries and mutations in both accessor modes, logs
    /// "[RQLPLAYER] PASS/FAIL …" lines, writes a JSON report to the path after -rqlOut (desktop players) and quits with the
    /// number of failures as exit code. WebGL has no command line: the log lines are the report.
    /// </summary>
    public static class RqlPlayerSmoke
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Run()
        {
            var lines = new List<string>();
            int failures = 0;
            void Check(string mode, string name, Func<string?> body)
            {
                string? failure;
                try { failure = body(); }
                catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                if (failure != null) failures++;
                var line = $"[RQLPLAYER] {(failure == null ? "PASS" : "FAIL")} {mode} {name}{(failure == null ? "" : " — " + failure)}";
                lines.Add(line);
                Debug.Log(line);
            }

            lines.Add($"[RQLPLAYER] start {Application.platform} {Application.unityVersion} backend={Backend()}");
            Debug.Log(lines[0]);
            foreach (var mode in new[] { AccessorMode.Reflection, AccessorMode.Compiled })
            {
                BuildScene();
                RqlEngine? engine = null;
                var m = mode.ToString();
                Check(m, "engine", () => { engine = CreateEngine(mode); return null; });
                if (engine == null) continue;
                var e = engine;
                string Table(string q) => AgentFormat.Render(e.Execute(q), OutputFormat.Table);
                string Mutate(string q) => AgentFormat.Render(e.Execute(q, null, new QueryOptions { AllowMutation = true }), OutputFormat.Table);
                string? Expect(string text, params string[] parts) => parts.All(text.Contains) ? null : "got: " + text.Replace("\n", " / ");

                Check(m, "count", () => Expect(Table("objects | count"), "6"));
                Check(m, "where-select", () => Expect(Table("objects | where name starts-with \"Enemy\" | sort name | select name"), "Enemy_1", "Enemy_3"));
                Check(m, "component-fields", () => Expect(Table("objects | get components | where $ is Health | where current < 80 | select gameObject.name current Label"), "Enemy_2", "40", "unit"));
                Check(m, "vector3-read", () => Expect(Table("objects | where name == \"Enemy_1\" | select transform.position"), "(1, 0, 0)"));
                Check(m, "color-light", () => Expect(Table("objects | get components | where $ is Light | select intensity color"), "2.5", "(1, 0, 0, 1)"));
                Check(m, "children", () => Expect(Table("objects | where name == \"Group\" | get children | select name"), "Enemy_3"));
                Check(m, "set-field", () =>
                {
                    Mutate("objects | where name == \"Enemy_1\" | get components | where $ is Health | set current = 5");
                    return Find("Enemy_1").GetComponent<Health>().current == 5 ? null : "current is " + Find("Enemy_1").GetComponent<Health>().current;
                });
                Check(m, "set-vector3", () =>
                {
                    var r = Mutate("objects | where name == \"Enemy_2\" | set transform.position = [4, 5, 6]");
                    return Find("Enemy_2").transform.position == new Vector3(4, 5, 6) ? null : r;
                });
                Check(m, "call", () =>
                {
                    var r = Mutate("objects | where name == \"Enemy_3\" | get components | where $ is Health | call Damage(10)");
                    return Find("Enemy_3").GetComponent<Health>().current == 90 ? null : r;
                });
                Check(m, "parse-value", () => RqlParser.ParseValue("{a: [1, 2], b: \"x\"}") is ObjectExpr ? null : "not an object literal");
                Check(m, "syntax-error", () => Expect(Table("objects | where (name =="), "syntax-error"));
                // Members no C# code here touches: only reflection reaches them, so code stripping could remove them.
                Check(m, "reflection-only-engine-member", () => Expect(Table("objects | get components | where $ is Camera | select fieldOfView nearClipPlane"), "60", "0.3"));
                Check(m, "reflection-only-user-member", () => Expect(Table("objects | get components | where $ is Health | first 1 | select max"), "100"));
                Check(m, "all-members", () => { var t = Table("objects | get components | first 20"); return t.Length > 0 ? null : "empty"; });
            }

            Debug.Log($"[RQLPLAYER] done failures={failures}");
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-rqlOut");
            var report = string.Join("\n", lines) + $"\n[RQLPLAYER] done failures={failures}\n";
            if (i >= 0 && i + 1 < args.Length) File.WriteAllText(args[i + 1], report, Encoding.UTF8);
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                // WebGL has no files or exit code: post the report to the page's server (Tools/serve-webgl.mjs).
                UnityEngine.Networking.UnityWebRequest.Post(new Uri(new Uri(Application.absoluteURL), "report"), report, "text/plain").SendWebRequest();
            else
                Application.Quit(failures);
        }

        private static string Backend()
        {
#if ENABLE_IL2CPP
            return "il2cpp";
#else
            return "mono";
#endif
        }

        private static GameObject Find(string name) => UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None).First(g => g.name == name);

        private static void BuildScene()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
            GameObject Enemy(string name, Vector3 at, float current, Transform? parent = null)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                go.transform.position = at;
                go.AddComponent<Health>().current = current;
                return go;
            }
            Enemy("Enemy_1", new Vector3(1, 0, 0), 100);
            Enemy("Enemy_2", new Vector3(2, 0, 0), 40);
            var group = new GameObject("Group");
            Enemy("Enemy_3", new Vector3(3, 0, 0), 100, group.transform);
            var lamp = new GameObject("Lamp").AddComponent<Light>();
            lamp.intensity = 2.5f;
            lamp.color = Color.red;
            new GameObject("Camera").AddComponent<Camera>();
        }

        private static RqlEngine CreateEngine(AccessorMode mode)
        {
            var engine = new RqlEngine(mode) { RuntimeName = "unity-player" };
            engine.Mutations.Enabled = true;
            engine.AddSource("objects", () => UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID).ToList());
            TypeModel.TreatAsObject(typeof(Transform));
            engine.ExcludeTypes(t => t == typeof(IntPtr) || t == typeof(UIntPtr) || t.IsPointer || t == typeof(System.Threading.CancellationToken)
                                           || t.FullName == "UnityEngine.TransformHandle");
            engine.AddAdapter(new GameObjectAdapter());
            engine.AddAdapter(new SceneAdapter());
            engine.AddAdapter(new Vector3Adapter());
            engine.AddAdapter(new ColorAdapter());
            return engine;
        }

        private sealed class GameObjectAdapter : RqlTypeAdapter<GameObject>
        {
            protected override void Members(MemberTable<GameObject> m) => m
                .Add("components", g => g.GetComponents<Component>())
                .Add("children", g => Enumerable.Range(0, g.transform.childCount).Select(i => g.transform.GetChild(i).gameObject).ToArray())
                .Add("active", g => g.activeSelf, (g, v) => g.SetActive(v));
        }

        // As in Unity-MCP: only these Scene members. Reading e.g. Scene.loadableSceneId by reflection crashes a Mono player
        // natively (found by the all-members check once link.xml kept the whole CoreModule).
        private sealed class SceneAdapter : RqlTypeAdapter<Scene>
        {
            protected override void Members(MemberTable<Scene> m) => m.Untyped.Only("name", "path", "buildIndex", "isLoaded", "isDirty", "rootCount").WritesThrough();
        }

        private sealed class Vector3Adapter : NumericTupleAdapter<Vector3>
        {
            protected override string Expected => "(x, y, z) or [x, y, z]";
            protected override int Width => 3;
            protected override float Part(Vector3 v, int i) => i switch { 0 => v.x, 1 => v.y, _ => v.z };
            protected override Vector3? Make(float[] f) => f.Length == 3 ? new Vector3(f[0], f[1], f[2]) : (Vector3?)null;
        }

        private sealed class ColorAdapter : NumericTupleAdapter<Color>
        {
            protected override string Expected => "(r, g, b, a) or [r, g, b, a]";
            protected override int Width => 4;
            protected override float Part(Color c, int i) => i switch { 0 => c.r, 1 => c.g, 2 => c.b, _ => c.a };
            protected override Color? Make(float[] f) => f.Length == 4 ? new Color(f[0], f[1], f[2], f[3]) : f.Length == 3 ? new Color(f[0], f[1], f[2]) : (Color?)null;
        }
    }
}
