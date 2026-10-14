#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RqlTest.Eval
{
    /// <summary>
    /// One evaluation task on the RqlTest scene (rebuilt fresh before every run). The grader checks the final Editor state
    /// (or the submitted answer), and <see cref="Allowed"/> says which snapshot keys the task may change — any other change
    /// counts as an unintended change and fails the run.
    /// </summary>
    public sealed class EvalTask
    {
        public string Id = "";
        public string Category = "";
        public string Prompt = "";
        public Func<string?, (bool ok, string detail)> Check = _ => (false, "no check");
        public Func<string, bool> Allowed = _ => false;
        /// <summary>Runs after the scene is rebuilt and before the baseline snapshot (e.g. writes console messages).</summary>
        public Action? Setup;
    }

    public static class EvalTasks
    {
        // ------------------------------------------------------------ helpers

        private static GameObject? Go(string path)
        {
            var parts = path.Split('/');
            var root = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == parts[0]);
            if (root == null) return null;
            var t = root.transform;
            foreach (var p in parts.Skip(1))
            {
                t = t.Find(p);
                if (t == null) return null;
            }
            return t.gameObject;
        }

        private static Material Mat(string name) => AssetDatabase.LoadAssetAtPath<Material>($"Assets/Materials/{name}.mat");

        private static bool Near(float a, float b, float eps = 1e-3f) => Math.Abs(a - b) <= eps;
        private static bool Near(Vector3 a, Vector3 b, float eps = 1e-2f) => (a - b).magnitude <= eps;
        private static bool Near(Color a, Color b, float eps = 2e-3f) => Near(a.r, b.r, eps) && Near(a.g, b.g, eps) && Near(a.b, b.b, eps) && Near(a.a, b.a, eps);

        private static (bool, string) All(params (bool ok, string what)[] checks)
        {
            var failed = checks.Where(c => !c.ok).Select(c => c.what).ToList();
            return failed.Count == 0 ? (true, "ok") : (false, "failed: " + string.Join("; ", failed));
        }

        private static Func<string, bool> Keys(params string[] exact) => k => exact.Contains(k);
        private static Func<string, bool> Prefixes(params string[] prefixes) => k => prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal));

        private static string Norm(string? answer) => (answer ?? "").Trim().Trim('"', '\'', '.').Trim();

        private static bool NumberEquals(string? answer, double expected) =>
            double.TryParse(Norm(answer), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && Math.Abs(v - expected) < 1e-6;

        private static bool NameSetEquals(string? answer, params string[] expected)
        {
            var got = Norm(answer).Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().Trim('"', '\'')).Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return got.SetEquals(expected);
        }

        // ------------------------------------------------------------ tasks

        public static List<EvalTask> All() => new List<EvalTask>
        {
            new EvalTask
            {
                Id = "material-shader-floats", Category = "material",
                Prompt = "Set the smoothness (shader property _Glossiness) of the material asset \"Red\" to 0.8 and its metallic value (_Metallic) to 0.3.",
                Check = _ => All((Near(Mat("Red").GetFloat("_Glossiness"), 0.8f), "Red._Glossiness == 0.8"), (Near(Mat("Red").GetFloat("_Metallic"), 0.3f), "Red._Metallic == 0.3")),
                Allowed = Keys("mat:Red|_Glossiness", "mat:Red|_Metallic"),
            },
            new EvalTask
            {
                Id = "material-color-alpha", Category = "material",
                Prompt = "Make the main color (_Color) of the material asset \"Blue\" 50% transparent (alpha 0.5) without changing its red, green and blue components.",
                Check = _ => All((Near(Mat("Blue").GetColor("_Color"), new Color(0.1f, 0.3f, 0.9f, 0.5f)), "Blue._Color == (0.1, 0.3, 0.9, 0.5)")),
                Allowed = Keys("mat:Blue|_Color"),
            },
            new EvalTask
            {
                Id = "light-color-intensity", Category = "component",
                Prompt = "Set the light on the GameObject \"Lamp_B\" to orange color (1, 0.5, 0, 1) and intensity 3.",
                Check = _ =>
                {
                    var l = Go("Lamps/Lamp_B")?.GetComponent<Light>();
                    return l == null ? (false, "Lamp_B light missing") : All((Near(l.color, new Color(1, 0.5f, 0, 1)), "color"), (Near(l.intensity, 3), "intensity"));
                },
                Allowed = Keys("Lamps/Lamp_B|Light.color", "Lamps/Lamp_B|Light.intensity"),
            },
            new EvalTask
            {
                Id = "lights-bulk-halve", Category = "component",
                Prompt = "Halve the intensity of every point light in the scene whose intensity is currently above 2. Leave all other lights unchanged.",
                Check = _ => All((Near(Go("Lamps/Lamp_B")!.GetComponent<Light>().intensity, 1.25f), "Lamp_B 2.5 → 1.25"), (Near(Go("Lamps/Lamp_C")!.GetComponent<Light>().intensity, 2f), "Lamp_C 4 → 2")),
                Allowed = Keys("Lamps/Lamp_B|Light.intensity", "Lamps/Lamp_C|Light.intensity"),
            },
            new EvalTask
            {
                Id = "create-hierarchy", Category = "create",
                Prompt = "Create an empty root GameObject named \"Props\" with two empty child GameObjects: \"Crate_1\" at local position (2, 0, 3) and \"Crate_2\" at local position (4, 0, 3).",
                Check = _ =>
                {
                    var props = Go("Props");
                    var c1 = Go("Props/Crate_1");
                    var c2 = Go("Props/Crate_2");
                    return All((props != null && props.transform.parent == null, "root Props"), (c1 != null && Near(c1.transform.localPosition, new Vector3(2, 0, 3)), "Crate_1 at (2, 0, 3)"),
                               (c2 != null && Near(c2.transform.localPosition, new Vector3(4, 0, 3)), "Crate_2 at (4, 0, 3)"),
                               (props == null || props.transform.childCount == 2, "exactly two children"));
                },
                Allowed = Prefixes("Props"),
            },
            new EvalTask
            {
                Id = "add-rigidbody-by-kind", Category = "create",
                Prompt = "Add a Rigidbody component with mass 50 to every enemy whose Enemy component has kind Brute or Boss.",
                Check = _ =>
                {
                    float? Mass(string p) => Go(p)?.GetComponent<Rigidbody>()?.mass;
                    var others = new[] { "Enemies/Enemy_01", "Enemies/Enemy_02", "Enemies/Enemy_03", "Enemies/Enemy_05" };
                    return All((Mass("Enemies/Enemy_04") is float a && Near(a, 50), "Enemy_04 (Brute) mass 50"), (Mass("Enemies/Enemy_06") is float b && Near(b, 50), "Enemy_06 (Boss) mass 50"),
                               (others.All(o => Go(o)?.GetComponent<Rigidbody>() == null), "no Rigidbody on other enemies"));
                },
                Allowed = Prefixes("Enemies/Enemy_04|components", "Enemies/Enemy_04|Rigidbody.", "Enemies/Enemy_06|components", "Enemies/Enemy_06|Rigidbody."),
            },
            new EvalTask
            {
                Id = "move-and-rotate", Category = "transform",
                Prompt = "Move the GameObject \"Player\" to world position (0, 1, -5) and rotate it so its Y rotation (euler angle) is 90 degrees, with X and Z rotation 0.",
                Check = _ =>
                {
                    var p = Go("Player")!.transform;
                    return All((Near(p.position, new Vector3(0, 1, -5)), "position"), (SceneSnapshot.Euler(p.eulerAngles) == "(0, 90, 0)", "rotation (0, 90, 0)"));
                },
                Allowed = Keys("Player|pos", "Player|rot"),
            },
            new EvalTask
            {
                Id = "reparent-keep-world", Category = "transform",
                Prompt = "Make the GameObject \"Lamp_A\" a child of \"Player\" while keeping Lamp_A at its current world position.",
                Check = _ =>
                {
                    var lamp = Go("Player/Lamp_A");
                    return All((lamp != null, "Lamp_A under Player"), (lamp != null && Near(lamp.transform.position, new Vector3(-6, 3, 0)), "world position (-6, 3, 0) kept"));
                },
                Allowed = k => k.Contains("Lamp_A"),
            },
            new EvalTask
            {
                Id = "script-field-bulk", Category = "component",
                Prompt = "Increase the damage of every enemy whose kind is Archer by 5 (include inactive enemies).",
                Check = _ => All((Go("Enemies/Enemy_03")!.GetComponent<Enemy>().damage == 24, "Enemy_03 19 → 24"), (Go("Enemies/Enemy_05")!.GetComponent<Enemy>().damage == 38, "Enemy_05 33 → 38")),
                Allowed = Keys("Enemies/Enemy_03|Enemy.damage", "Enemies/Enemy_05|Enemy.damage"),
            },
            new EvalTask
            {
                Id = "deactivate-no-target", Category = "gameobject",
                Prompt = "Deactivate every enemy GameObject whose Enemy component has no target assigned.",
                Check = _ => All((!Go("Enemies/Enemy_03")!.activeSelf, "Enemy_03 inactive"), (!Go("Enemies/Enemy_06")!.activeSelf, "Enemy_06 inactive"),
                                 (Go("Enemies/Enemy_01")!.activeSelf && Go("Enemies/Enemy_02")!.activeSelf && Go("Enemies/Enemy_04")!.activeSelf, "others still active")),
                Allowed = Keys("Enemies/Enemy_03|active", "Enemies/Enemy_06|active"),
            },
            new EvalTask
            {
                Id = "rename-strongest", Category = "gameobject",
                Prompt = "Rename the enemy GameObject with the highest Enemy.damage to \"Champion\".",
                Check = _ => All((Go("Enemies/Champion")?.GetComponent<Enemy>()?.kind == EnemyKind.Boss, "Enemy_06 (damage 40) renamed to Champion")),
                Allowed = k => k.StartsWith("Enemies/Enemy_06", StringComparison.Ordinal) || k.StartsWith("Enemies/Champion", StringComparison.Ordinal),
            },
            new EvalTask
            {
                Id = "spawn-light", Category = "create",
                Prompt = "Create a new GameObject \"Lamp_D\" as a child of \"Lamps\" at local position (0, 4, 0) with a point Light component of intensity 1.5 and color blue (0, 0, 1, 1).",
                Check = _ =>
                {
                    var go = Go("Lamps/Lamp_D");
                    var l = go?.GetComponent<Light>();
                    return All((go != null, "Lamps/Lamp_D exists"), (go != null && Near(go.transform.localPosition, new Vector3(0, 4, 0)), "local position"),
                               (l != null && l.type == LightType.Point, "point light"), (l != null && Near(l.intensity, 1.5f), "intensity 1.5"), (l != null && Near(l.color, Color.blue), "color blue"));
                },
                Allowed = k => k.StartsWith("Lamps/Lamp_D", StringComparison.Ordinal),
            },
            new EvalTask
            {
                Id = "assign-material", Category = "material",
                Prompt = "Make the Player's MeshRenderer use the material asset \"Gold\" (shared material).",
                Check = _ => All((Go("Player")!.GetComponent<MeshRenderer>().sharedMaterial == Mat("Gold"), "Player uses Gold")),
                Allowed = Keys("Player|Renderer.materials"),
            },
            new EvalTask
            {
                Id = "combined-archers", Category = "component",
                Prompt = "For every enemy whose aggroRadius is above 10: set its Health.max to 150 and make its MeshRenderer use the material asset \"Blue\".",
                Check = _ =>
                {
                    bool Ok(string p) => Go(p)!.GetComponent<Health>().max == 150 && Go(p)!.GetComponent<MeshRenderer>().sharedMaterial == Mat("Blue");
                    return All((Ok("Enemies/Enemy_03"), "Enemy_03"), (Ok("Enemies/Enemy_05"), "Enemy_05"));
                },
                Allowed = Keys("Enemies/Enemy_03|Health.max", "Enemies/Enemy_03|Renderer.materials", "Enemies/Enemy_05|Health.max", "Enemies/Enemy_05|Renderer.materials"),
            },
            new EvalTask
            {
                Id = "duplicate-move", Category = "gameobject",
                Prompt = "Duplicate the GameObject \"Pillar_1\" (under \"Level\") and move the copy to world position (0, 2, -10). The original must stay where it is.",
                Check = _ =>
                {
                    var level = Go("Level")!.transform;
                    var pillars = Enumerable.Range(0, level.childCount).Select(i => level.GetChild(i)).Where(t => t.name.StartsWith("Pillar_1", StringComparison.Ordinal)).ToList();
                    var copy = pillars.FirstOrDefault(t => Near(t.position, new Vector3(0, 2, -10)));
                    var original = pillars.FirstOrDefault(t => Near(t.position, new Vector3(-10, 2, -10)));
                    return All((level.childCount == 6, "Level has one more child"), (copy != null, "a copy at (0, 2, -10)"), (original != null, "the original at (-10, 2, -10)"),
                               (copy != null && copy.GetComponent<MeshRenderer>()?.sharedMaterial == Mat("Gold"), "the copy keeps the Gold material"));
                },
                Allowed = Prefixes("Level/Pillar_1"),
            },
            new EvalTask
            {
                Id = "prefab-instantiate", Category = "prefab",
                Prompt = "Save the GameObject \"Spawner\" as a prefab asset at \"Assets/EvalOut/Spawner.prefab\" (create the folder if needed), then add a new instance of that prefab as a root GameObject at world position (5, 0, 5).",
                Check = _ =>
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/EvalOut/Spawner.prefab");
                    var instances = SceneManager.GetActiveScene().GetRootGameObjects()
                        .Where(g => prefab != null && PrefabUtility.GetCorrespondingObjectFromSource(g) == prefab).ToList();
                    return All((prefab != null, "prefab asset exists"), (prefab != null && prefab.GetComponent<Spawner>() != null, "prefab has the Spawner component"),
                               (instances.Any(g => Near(g.transform.position, new Vector3(5, 0, 5))), "an instance at (5, 0, 5)"));
                },
                Allowed = Prefixes("Spawner"),
            },
            new EvalTask
            {
                Id = "answer-console-errors", Category = "query",
                Prompt = "How many error messages (log type Error) containing the text \"EvalError\" are in the Unity console log? Submit just the number.",
                Setup = () =>
                {
                    Debug.ClearDeveloperConsole();
                    com.IvanMurzak.Unity.MCP.UnityMcpPluginEditor.Instance.LogCollector?.Clear();
                    Debug.LogError("EvalError: shader Foo failed to compile");
                    Debug.LogWarning("EvalError mentioned in a warning (not an error)");
                    Debug.LogError("EvalError: missing reference on Spawner");
                    Debug.Log("EvalError in a normal log line");
                    Debug.LogError("EvalError: NullReferenceException in Enemy.Update");
                    Debug.LogError("Another error without the marker");
                    com.IvanMurzak.Unity.MCP.UnityMcpPluginEditor.Instance.LogCollector?.Save();
                },
                Check = a => NumberEquals(a, 3) ? (true, "3") : (false, $"expected 3, got '{a}'"),
            },
            new EvalTask
            {
                Id = "select-archers", Category = "editor",
                Prompt = "In the Unity Editor, select every enemy GameObject whose Enemy component has kind Archer (inactive ones included), and nothing else.",
                Check = _ =>
                {
                    var names = Selection.objects.Select(o => o.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    return names.SequenceEqual(new[] { "Enemy_03", "Enemy_05" }) ? (true, "Enemy_03, Enemy_05") : (false, "selected: " + string.Join(", ", names));
                },
            },
            new EvalTask
            {
                Id = "answer-low-health", Category = "query",
                Prompt = "How many enemies (active or inactive) have Health.current below 60% of their Health.max? Submit just the number.",
                Check = a => NumberEquals(a, 1) ? (true, "1") : (false, $"expected 1, got '{a}'"),
            },
            new EvalTask
            {
                Id = "answer-gold-users", Category = "query",
                Prompt = "Which GameObjects render with the material \"Gold\"? Submit their names, comma-separated.",
                Check = a => NameSetEquals(a, "Enemy_06", "Pillar_1", "Pillar_2", "Pillar_3", "Pillar_4") ? (true, "5 names") : (false, $"got '{a}'"),
            },
            // Text-escaping probes: the name passes through tool results and arguments unchanged only if no layer mangles it.
            new EvalTask
            {
                Id = "answer-tricky-name", Category = "escaping",
                Prompt = "Exactly one GameObject has a name starting with \"Crate #7\". Submit its exact full name, character for character.",
                Setup = () => new GameObject(TrickyName).transform.SetParent(Go("Level")!.transform, false),
                Check = a => a?.Trim() == TrickyName ? (true, "exact") : (false, $"got '{a}'"),
            },
            new EvalTask
            {
                Id = "rename-tricky-name", Category = "escaping",
                Prompt = "Rename the GameObject whose name starts with \"Crate #7\" to exactly: " + TrickyRename,
                Setup = () => new GameObject(TrickyName).transform.SetParent(Go("Level")!.transform, false),
                Check = _ => Go("Level/" + TrickyRename) != null ? (true, "renamed") : (false, "no Level/" + TrickyRename),
                Allowed = k => k.StartsWith("Level/" + TrickyName, StringComparison.Ordinal) || k.StartsWith("Level/" + TrickyRename, StringComparison.Ordinal),
            },
        };

        // ------------------------------------------------------------ tools beyond RQL (their schemas / results differ per data layer)

        public static IEnumerable<EvalTask> BeyondRql() => new[]
        {
            new EvalTask
            {
                Id = "script-param-children", Category = "script",
                Prompt = "Write a C# method, run it with the script-execute tool and pass the GameObject \"Enemies\" to it as a method " +
                         "parameter (do not look it up by name inside the code). The method must return how many direct children that " +
                         "GameObject has. Submit just the number.",
                Check = a =>
                {
                    var n = Go("Enemies")!.transform.childCount;
                    return NumberEquals(a, n) ? (true, n.ToString(CultureInfo.InvariantCulture)) : (false, $"expected {n}, got '{a}'");
                },
            },
            new EvalTask
            {
                Id = "script-return-position", Category = "script",
                Prompt = "Using the script-execute tool, return the world position (a Vector3) of the GameObject \"Player\" from a C# " +
                         "method. Submit it as three numbers \"x, y, z\".",
                Check = a =>
                {
                    var p = Go("Player")!.transform.position;
                    var nums = System.Text.RegularExpressions.Regex.Matches(a ?? "", @"-?\d+(\.\d+)?").Cast<System.Text.RegularExpressions.Match>()
                        .Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
                    return nums.Count == 3 && Near(new Vector3(nums[0], nums[1], nums[2]), p) ? (true, "ok") : (false, $"expected {p}, got '{a}'");
                },
            },
            new EvalTask
            {
                Id = "tool-count", Category = "tools",
                Prompt = "How many MCP tools are registered in this Unity Editor in total (enabled and disabled)? Use the tool that lists " +
                         "them. Submit just the number.",
                Check = a =>
                {
                    var n = com.IvanMurzak.Unity.MCP.UnityMcpPluginEditor.Instance.Tools!.GetAllTools().Count();
                    return NumberEquals(a, n) ? (true, n.ToString(CultureInfo.InvariantCulture)) : (false, $"expected {n}, got '{a}'");
                },
            },
        };

        private const string TrickyName = "Crate #7 <A+B> — café \"x\"";
        private const string TrickyRename = "Box <1+1> naïve & 'ok' — ü";
    }
}
